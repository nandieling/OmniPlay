import Foundation
import CryptoKit

/// 蓝光导出的 `.sup`（PGS 位图字幕）在本机 mpv 上挂不上：MPVKit 自带的 ffmpeg 是白名单编译的
/// （`--disable-demuxers --enable-demuxer=...`），里面没有 `sup` 解封装器，`sub-add x.sup` 静默失败；
/// 但 `pgssub` 解码器和 `mpegts` 解封装器都在名单内，所以这里把 `.sup` 里的 PGS 段重新装进一个只含
/// 字幕流的 MPEG-TS，交给 mpv 当外挂字幕读。产物按「路径+大小+修改时间」缓存，换文件才会重生成。
///
/// 四处对着 FFmpeg 源码核过的约定：
/// - `.sup` 的段头是 `PG(2) + PTS(4) + DTS(4) + 段类型(1) + 段数据长度(2)`（见
///   libavformat/supdec.c），整段是 13 + 段数据长度 字节。
/// - `pgssub` 解码器（libavcodec/pgssubdec.c 的 `decode()`）吃的是一串
///   `段类型(1) + 段数据长度(2) + 段数据`，也就是每段前面那 10 字节（"PG" + PTS + DTS）要丢掉。
/// - 一次显示 = `PCS(0x14)` → `PDS(0x15)`/`WDS(0x16)`/`ODS(0x17)`… → `DISPLAY(0x80)`，
///   整组打成一个 PES，解码器在 DISPLAY 段出图，出图时刻就是 PES 的时间戳。
/// - PMT 必须带 program 级 `registration_descriptor`（tag 是 0x05，不是 0x09）+ `HDMV`，
///   否则 ffmpeg 不会把 stream_type 0x90 认成 hdmv_pgs_subtitle；PSI 的 CRC 用 CRC-32/MPEG-2，
///   误用 zlib 那种反射 CRC 会让整张 PAT/PMT 被丢掉。
///
/// 时间轴上别想当然：抓包样本（90 MB / 6928 个显示组）里 `.sup` 不但把两条字幕轨交错着写在同一个文件里，
/// 还有 76 个组干脆没写时间戳、39 个清屏组写的是 0。按文件顺序照抄再「保证不回退」，会把 5389 个组
/// 全钉在最大的那个时间戳上，播放到正常时段就一个字都不出。所以这里先给每组定一个时间戳（组内第一个非零
/// PTS，整组都是 0 就沿用上一组），再整体按时间戳排序写出；排序前后逐组比对过解码器的调色板/位图缓存，
/// 可渲染的显示组一个都没丢（4916 个）。
enum PGSSubtitleWrapper {
    private static let patPID = 0x0000
    private static let pmtPID = 0x1000
    private static let esPID = 0x1100
    private static let pgsStreamType: UInt8 = 0x90
    private static let displaySegmentType: UInt8 = 0x80
    private static let maxSourceByteCount = 400 * 1024 * 1024
    private static let chunkByteCount = 4 * 1024 * 1024
    private static let cacheBudgetByteCount: Int64 = 1_500 * 1024 * 1024
    private static let cacheLifetime: TimeInterval = 14 * 24 * 3600

    /// 包好的、可直接 `sub-add` 的 `.ts`。源文件太大、读不动或者结构不对时返回 nil。
    /// 打包要读几十到几百 MB，调用方自己在后台队列上叫，别压在主线程或 mpv 控制队列上。
    static func wrappedURL(for source: URL, fileManager: FileManager = .default) -> URL? {
        guard source.isFileURL else { return nil }
        guard let attributes = try? fileManager.attributesOfItem(atPath: source.path) else { return nil }
        let fileSize = (attributes[.size] as? NSNumber)?.int64Value ?? 0
        let modifiedAt = (attributes[.modificationDate] as? Date)?.timeIntervalSince1970 ?? 0
        guard fileSize > 13, fileSize <= Int64(maxSourceByteCount) else { return nil }

        let directory = cacheDirectory(fileManager: fileManager)
        let digest = SHA256.hash(data: Data("\(source.path)|\(fileSize)|\(Int(modifiedAt * 1000))".utf8))
        let key = digest.map { String(format: "%02x", $0) }.prefix(24).joined()
        let target = directory.appendingPathComponent("\(source.deletingPathExtension().lastPathComponent)-\(key).ts")
        if fileManager.fileExists(atPath: target.path) { return target }

        let staging = directory.appendingPathComponent("\(key)-\(UUID().uuidString).tmp")
        do {
            try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
            try Data().write(to: staging, options: .atomic)
            defer { try? fileManager.removeItem(at: staging) }
            guard try writeTS(from: source, to: staging) else { return nil }
            try? fileManager.removeItem(at: target)
            try fileManager.moveItem(at: staging, to: target)
            pruneCache(in: directory, keeping: target, fileManager: fileManager)
            return target
        } catch {
            return nil
        }
    }

    /// `.sup` 走打包，其余扩展名原样交给 mpv。
    static func playableURL(for candidate: URL) -> URL {
        guard candidate.pathExtension.lowercased() == "sup" else { return candidate }
        return wrappedURL(for: candidate) ?? candidate
    }

    // MARK: - 打包

    private struct DisplayGroup {
        var offset: Int        // 在 .sup 里的起始字节
        var byteCount: Int     // 整组（含各段段头）的字节数
        var timestamp: UInt64  // 出图时刻，90 kHz
    }

    private static func writeTS(from source: URL, to target: URL) throws -> Bool {
        var groups = try scanDisplayGroups(from: source)
        guard groups.isEmpty == false else { return false }
        groups.sort { ($0.timestamp, $0.offset) < ($1.timestamp, $1.offset) }

        let input = try FileHandle(forReadingFrom: source)
        defer { try? input.close() }
        let output = try FileHandle(forWritingTo: target)
        defer { try? output.close() }
        try output.seek(toOffset: 0)

        var writer = TSPacketWriter()
        var wroteAny = false
        output.write(Data(pat(&writer)))
        output.write(Data(pmt(&writer)))
        for (index, group) in groups.enumerated() {
            guard let payload = displayPayload(input: input, group: group) else { continue }
            output.write(Data(pesPackets(payload: payload, pts: group.timestamp, writer: &writer)))
            wroteAny = true
            if (index + 1) % 40 == 0 {
                output.write(Data(pat(&writer)))
                output.write(Data(pmt(&writer)))
            }
        }
        return wroteAny
    }

    /// 顺序扫一遍 `.sup`，把「上一个 DISPLAY 之后到本 DISPLAY 为止」的段并成一个显示组，
    /// 只记下偏移、长度和时间戳，负载留到写的时候再按偏移读，几百 MB 的文件也不用整份进内存。
    private static func scanDisplayGroups(from source: URL) throws -> [DisplayGroup] {
        let input = try FileHandle(forReadingFrom: source)
        defer { try? input.close() }
        var groups: [DisplayGroup] = []
        var buffer: [UInt8] = []
        var bufferOffset = 0      // buffer[0] 对应的文件偏移
        var position = 0
        var reachedEnd = false
        var groupOffset = 0
        var groupPTS: UInt64?
        var carriedPTS: UInt64 = 0

        func fill() -> Bool {
            guard !reachedEnd else { return false }
            guard let chunk = try? input.read(upToCount: chunkByteCount), chunk.isEmpty == false else {
                reachedEnd = true
                return false
            }
            buffer.append(contentsOf: chunk)
            return true
        }

        while true {
            while !reachedEnd && buffer.count - position < 13 { if !fill() { break } }
            if buffer.count - position < 13 { break }
            guard buffer[position] == 0x50, buffer[position + 1] == 0x47 else { break }   // "PG"
            let segmentPTS = UInt64(be32(buffer, position + 2))
            let segmentType = buffer[position + 10]
            let segmentSize = Int(be16(buffer, position + 11))
            while !reachedEnd && buffer.count - position < 13 + segmentSize { if !fill() { break } }
            if buffer.count - position < 13 + segmentSize { break }   // 结尾只剩半段，丢掉

            if groupPTS == nil, segmentPTS != 0 { groupPTS = segmentPTS }
            position += 13 + segmentSize
            if segmentType == displaySegmentType {
                let timestamp = groupPTS ?? carriedPTS
                carriedPTS = timestamp
                groups.append(DisplayGroup(offset: groupOffset,
                                           byteCount: bufferOffset + position - groupOffset,
                                           timestamp: timestamp))
                groupOffset = bufferOffset + position
                groupPTS = nil
            }
            if position >= chunkByteCount {
                buffer.removeFirst(position)
                bufferOffset += position
                position = 0
            }
        }
        return groups
    }

    /// 取出显示组里各段的数据：丢掉每段开头的 10 字节，留下解码器要的
    /// `段类型(1) + 段数据长度(2) + 段数据`。
    private static func displayPayload(input: FileHandle, group: DisplayGroup) -> [UInt8]? {
        guard group.byteCount >= 13,
              (try? input.seek(toOffset: UInt64(group.offset))) != nil,
              let slice = try? input.read(upToCount: group.byteCount),
              slice.count == group.byteCount else { return nil }
        let bytes = [UInt8](slice)
        var payload: [UInt8] = []
        payload.reserveCapacity(bytes.count)
        var position = 0
        while position + 13 <= bytes.count {
            guard bytes[position] == 0x50, bytes[position + 1] == 0x47 else { break }
            let segmentSize = Int(be16(bytes, position + 11))
            guard position + 13 + segmentSize <= bytes.count else { break }
            payload.append(contentsOf: bytes[(position + 10)..<(position + 13 + segmentSize)])
            position += 13 + segmentSize
        }
        return payload.isEmpty ? nil : payload
    }

    private static func pesPackets(payload: [UInt8], pts: UInt64, writer: inout TSPacketWriter) -> [UInt8] {
        // PES_packet_length = 该字段之后的字节数 = 8（标志位 + header_data_length + 5 字节 PTS）+ 负载
        let declaredLength = payload.count + 8
        // PES 头：PTS_DTS_flags 必须和实际写出去的时间戳一致。之前写的是 0xC0（PTS+DTS 都有），
        // 却只补了 5 字节的 PTS，解码器会把负载的头 5 字节当成 DTS 吞掉，整条字幕轨就再也画不出来了。
        // 0x84 是 data_alignment_indicator，0x80 表示只带 PTS，配 5 字节 header_data_length。
        var data: [UInt8] = [0x00, 0x00, 0x01, 0xBD, 0x00, 0x00, 0x84, 0x80, 0x05]
        if declaredLength <= 0xFFFF {
            data[4] = byte(declaredLength >> 8)
            data[5] = byte(declaredLength)
        }
        data.append(contentsOf: encodedPTS(pts, prefix: 2))
        data.append(contentsOf: payload)

        var out: [UInt8] = []
        out.reserveCapacity(data.count + (data.count / 176 + 2) * 12)
        var index = 0
        var isFirst = true
        while index < data.count {
            let adaptation: [UInt8]? = isFirst ? ([0x10] + encodedPCR(pts)) : nil
            let room = 188 - (adaptation.map { 4 + 1 + $0.count } ?? 4)
            let end = min(index + room, data.count)
            out.append(contentsOf: writer.packet(pid: esPID,
                                                 payload: Array(data[index..<end]),
                                                 pusi: isFirst,
                                                 adaptation: adaptation))
            index = end
            isFirst = false
        }
        return out
    }

    private static func pat(_ writer: inout TSPacketWriter) -> [UInt8] {
        var section: [UInt8] = [0x00, 0xB0, 13,
                                0x00, 0x01,             // transport_stream_id
                                0xC1, 0x00, 0x00,       // version / current_next / last_section
                                0x00, 0x01,             // program_number
                                0xE0 | byte(pmtPID >> 8), byte(pmtPID)]
        section.append(contentsOf: bigEndian(crc32MPEG2(section)))
        return writer.packet(pid: patPID, payload: [0x00] + section, pusi: true, adaptation: nil)
    }

    private static func pmt(_ writer: inout TSPacketWriter) -> [UInt8] {
        let registration: [UInt8] = [0x05, 0x04, 0x48, 0x44, 0x4D, 0x56]   // registration_descriptor "HDMV"
        var section: [UInt8] = [0x02, 0xB0, 0x00,
                                0x00, 0x01,               // program_number
                                0xC1, 0x00, 0x00,         // version / current_next / last_section
                                0xE0 | byte(esPID >> 8), byte(esPID),   // PCR_PID
                                0xF0, byte(registration.count)]         // program_info_length
        section.append(contentsOf: registration)
        section.append(contentsOf: [pgsStreamType,
                                    0xE0 | byte(esPID >> 8), byte(esPID),
                                    0xF0, byte(registration.count)])    // ES_info_length
        section.append(contentsOf: registration)
        let sectionLength = section.count + 4 - 3
        section[1] |= byte(sectionLength >> 8)
        section[2] = byte(sectionLength)
        section.append(contentsOf: bigEndian(crc32MPEG2(section)))
        return writer.packet(pid: pmtPID, payload: [0x00] + section, pusi: true, adaptation: nil)
    }

    // MARK: - TS / PES 小组件

    private struct TSPacketWriter {
        private var continuity: [Int: UInt8] = [:]

        mutating func packet(pid: Int, payload: [UInt8], pusi: Bool, adaptation: [UInt8]?) -> [UInt8] {
            var adaptationBody = adaptation
            let used = 4 + payload.count + (adaptationBody.map { 1 + $0.count } ?? 0)
            let padding = 188 - used
            if padding > 0 {
                // 补位塞进 adaptation field：长度字节本身占 1 字节，其余全填 0xFF
                if adaptationBody == nil {
                    adaptationBody = [UInt8](repeating: 0xFF, count: padding - 1)
                } else {
                    adaptationBody?.append(contentsOf: [UInt8](repeating: 0xFF, count: padding))
                }
            }
            let afc = (adaptationBody != nil ? 2 : 0) | (payload.isEmpty ? 0 : 1)
            let counter = continuity[pid, default: 0]
            continuity[pid] = counter &+ 1
            var packet: [UInt8] = [0x47,
                                   (pusi ? 0x40 : 0x00) | byte(pid >> 8),
                                   byte(pid),
                                   byte((afc << 4) | Int(counter & 0x0F))]
            if let body = adaptationBody {
                packet.append(byte(body.count))
                packet.append(contentsOf: body)
            }
            packet.append(contentsOf: payload)
            return packet
        }
    }

    private static func encodedPTS(_ pts: UInt64, prefix: UInt8) -> [UInt8] {
        [byte((UInt64(prefix) << 4) | (((pts >> 30) & 0x07) << 1) | 1),
         byte(pts >> 22),
         byte((((pts >> 15) & 0x7F) << 1) | 1),
         byte(pts >> 7),
         byte(((pts & 0x7F) << 1) | 1)]
    }

    private static func encodedPCR(_ pcr: UInt64) -> [UInt8] {
        let base = pcr / 300
        let ext = pcr % 300
        return [byte(base >> 25), byte(base >> 17), byte(base >> 9), byte(base >> 1),
                byte(((base & 1) << 7) | 0x7E | ((ext >> 8) & 1)), byte(ext)]
    }

    private static func crc32MPEG2(_ bytes: [UInt8]) -> UInt32 {
        var crc: UInt32 = 0xFFFF_FFFF
        for value in bytes {
            crc ^= UInt32(value) << 24
            for _ in 0..<8 {
                crc = (crc & 0x8000_0000) != 0 ? (crc << 1) ^ 0x04C1_1DB7 : crc << 1
            }
        }
        return crc
    }

    private static func be16(_ bytes: [UInt8], _ at: Int) -> UInt16 {
        (UInt16(bytes[at]) << 8) | UInt16(bytes[at + 1])
    }

    private static func be32(_ bytes: [UInt8], _ at: Int) -> UInt32 {
        (UInt32(bytes[at]) << 24) | (UInt32(bytes[at + 1]) << 16) |
        (UInt32(bytes[at + 2]) << 8) | UInt32(bytes[at + 3])
    }

    private static func bigEndian(_ value: UInt32) -> [UInt8] {
        [byte(value >> 24), byte(value >> 16), byte(value >> 8), byte(value)]
    }

    private static func byte(_ value: UInt64) -> UInt8 { UInt8(value & 0xFF) }
    private static func byte(_ value: Int) -> UInt8 { UInt8(value & 0xFF) }
    private static func byte(_ value: UInt32) -> UInt8 { UInt8(value & 0xFF) }

    // MARK: - 缓存

    private static func cacheDirectory(fileManager: FileManager) -> URL {
        let base = fileManager.urls(for: .cachesDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSTemporaryDirectory())
        return base.appendingPathComponent("OmniPlay", isDirectory: true)
            .appendingPathComponent("pgs", isDirectory: true)
    }

    /// 包出来的 `.ts` 和 `.sup` 差不多大，攒多了占地方：过期的先删，还超预算就从最老的开始删。
    private static func pruneCache(in directory: URL, keeping keep: URL, fileManager: FileManager) {
        guard let entries = try? fileManager.contentsOfDirectory(
            at: directory,
            includingPropertiesForKeys: [.fileSizeKey, .contentModificationDateKey],
            options: [.skipsHiddenFiles]) else { return }
        let now = Date()
        let keepPath = keep.standardizedFileURL.path
        var keepers: [(url: URL, size: Int64, date: Date)] = []
        var total: Int64 = 0
        for url in entries {
            if url.standardizedFileURL.path == keepPath { continue }
            let values = try? url.resourceValues(forKeys: [.fileSizeKey, .contentModificationDateKey])
            let size = Int64(values?.fileSize ?? 0)
            let date = values?.contentModificationDate ?? .distantPast
            if now.timeIntervalSince(date) > cacheLifetime {
                try? fileManager.removeItem(at: url)
            } else {
                keepers.append((url, size, date))
                total += size
            }
        }
        keepers.sort { $0.date < $1.date }
        var index = 0
        while total > cacheBudgetByteCount, index < keepers.count {
            try? fileManager.removeItem(at: keepers[index].url)
            total -= keepers[index].size
            index += 1
        }
    }
}
