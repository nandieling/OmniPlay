import Foundation

/// 容器完整性快检。
///
/// 「播得卡 + 进度乱跳」有两种完全相反的成因：播放器没被喂饱，或者这个文件本身就不齐。
/// 两者在屏幕上一模一样，修法却南辕北辙，所以加载本地 MKV/WebM 时顺手量两件事：
///   1. 顺 SeekHead 找 Matroska 的 seek 索引（尾部 Cues），确认那一块真的写进去了。
///      索引没了，mpv 每次定位都得从头扫文件，几秒才落位，进度条就跟着乱跳。
///   2. 全文件等距抽样若干个 2MB 窗口（外加文件尾巴），数有多少 64KB 整块是全零。
///      拷贝丢块的典型特征就是内容里躺着一片一片的零，exFAT 外置盘上尤其常见。
/// 只做这两件便宜事，读完几十兆就收手，不跟播放抢带宽。
enum ContainerIntegrityProbe {
    struct Report {
        let fileName: String
        let fileSize: Int64
        let cuesIndexOK: Bool
        let zeroRatio: Double
        let zeroBlocks: Int

        /// 超过这个占比就算丢块，而不是编码器留下的正常 Void 填充。
        var looksIncomplete: Bool { !cuesIndexOK || zeroRatio >= 0.02 }

        var diagnosis: String {
            switch (cuesIndexOK, zeroRatio >= 0.02) {
            case (false, true):  return "SeekHead 指的 seek 索引读不出来，内容里还有大片全零"
            case (false, false): return "SeekHead 指的 seek 索引读不出来，定位只能靠从头扫文件"
            case (true, true):   return "seek 索引还在，但内容里有大片全零"
            case (true, false):  return "索引和抽样内容都正常"
            }
        }
    }

    private static let ebmlID: UInt64 = 0x1A_45_DF_A3
    private static let segmentID: UInt64 = 0x18_53_80_67
    private static let seekHeadID: UInt64 = 0x11_4D_9B_74
    private static let cuesID: UInt64 = 0x1C_53_BB_6B
    private static let seekEntryID: UInt64 = 0x4D_BB
    private static let seekIDElementID: UInt64 = 0x53_AB
    private static let seekPosID: UInt64 = 0x53_AC
    /// 「长度未知」的哨兵值，遇到就没法再按长度往下跳。
    private static let unknownSize: UInt64 = (1 << 56) - 1

    private static let headReadLength = 8 * 1024 * 1024
    private static let windowLength = 2 * 1024 * 1024
    private static let blockSize = 64 * 1024
    private static let sampleWindows = 16

    static func probe(url: URL) -> Report? {
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: url.path),
              let fileSize = attrs[.size] as? Int64, fileSize > Int64(blockSize) else { return nil }
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }

        let head = read(handle, 0, min(headReadLength, Int(fileSize)))
        var off = 0
        guard let ebml = vint(head, off, keepMarker: true), ebml.0 == ebmlID else { return nil }
        off += ebml.1
        guard let ebmlBody = valueSize(head, &off) else { return nil }
        off += Int(ebmlBody)
        guard let segment = vint(head, off, keepMarker: true), segment.0 == segmentID else { return nil }
        off += segment.1
        guard let segmentBody = valueSize(head, &off) else { return nil }
        // SeekHead 里的 SeekPos 是相对 Segment 数据区起点算的
        let segmentDataStart = off
        _ = segmentBody

        var seekEntries: [(UInt64, UInt64)] = []
        var cuesWrittenInPlace = false
        var io = segmentDataStart
        for _ in 0..<128 {
            guard let child = vint(head, io, keepMarker: true) else { break }
            io += child.1
            guard let childSize = valueSize(head, &io) else { break }
            if child.0 == cuesID { cuesWrittenInPlace = true }
            if child.0 == seekHeadID {
                seekEntries = parseSeekHead(head, io, childSize)
                break
            }
            if childSize >= unknownSize { break }
            let next = io + Int(childSize)
            guard next > io, next <= head.count else { break }
            io = next
        }

        var cuesIndexOK = cuesWrittenInPlace
        if !cuesIndexOK, let entry = seekEntries.first(where: { $0.0 == cuesID }) {
            let at = Int64(segmentDataStart) + Int64(entry.1)
            if at >= 0, at < fileSize {
                let atCues = read(handle, UInt64(at), 16)
                cuesIndexOK = atCues.count >= 4 && vint(atCues, 0, keepMarker: true)?.0 == cuesID
            }
        }

        // 文件尾巴必读：Cues、Tags 都写在最后，丢块也最常从这里开始
        var offsets: [Int64] = [max(0, fileSize - Int64(windowLength))]
        if fileSize > Int64(windowLength) * 4 {
            let step = Double(fileSize) / Double(sampleWindows)
            for i in 0..<sampleWindows {
                let raw = Int64(Double(i) * step)
                offsets.append(raw / Int64(blockSize) * Int64(blockSize))
            }
        }

        let zeros = Data(count: blockSize)
        var zeroBlocks = 0
        var scannedBlocks = 0
        for offset in offsets {
            let window = read(handle, UInt64(max(0, offset)), windowLength)
            guard window.count >= blockSize else { continue }
            var cursor = 0
            while cursor + blockSize <= window.count {
                let blockStart = cursor
                let isZeroBlock = window.withUnsafeBytes { (raw: UnsafeRawBufferPointer) -> Bool in
                    guard let base = raw.baseAddress else { return false }
                    return zeros.withUnsafeBytes { memcmp(base + blockStart, $0.baseAddress, blockSize) == 0 }
                }
                scannedBlocks += 1
                if isZeroBlock { zeroBlocks += 1 }
                cursor += blockSize
            }
        }
        let zeroRatio = scannedBlocks > 0 ? Double(zeroBlocks) / Double(scannedBlocks) : 0
        return Report(fileName: url.lastPathComponent,
                      fileSize: fileSize,
                      cuesIndexOK: cuesIndexOK,
                      zeroRatio: zeroRatio,
                      zeroBlocks: zeroBlocks)
    }

    // MARK: - EBML 小工具

    /// 读一个 EBML 变长整数。ID 要保留前导标记位，长度则要把它去掉。
    private static func vint(_ data: Data, _ at: Int, keepMarker: Bool) -> (UInt64, Int)? {
        guard at >= 0, at < data.count else { return nil }
        let first = data[data.startIndex + at]
        var length = 0
        for n in 1...8 where length == 0 {
            if first & (0x80 >> UInt8(n - 1)) != 0 { length = n }
        }
        guard length > 0, at + length <= data.count else { return nil }
        var value: UInt64 = keepMarker ? UInt64(first) : UInt64(first & (0xFF >> UInt8(length)))
        for k in 1..<length {
            value = (value << 8) | UInt64(data[data.startIndex + at + k])
        }
        return (value, length)
    }

    /// 读元素长度并越过它，返回长度值。
    private static func valueSize(_ data: Data, _ offset: inout Int) -> UInt64? {
        guard let (value, length) = vint(data, offset, keepMarker: false) else { return nil }
        offset += length
        return value
    }

    private static func parseSeekHead(_ data: Data, _ bodyOffset: Int, _ declaredSize: UInt64) -> [(UInt64, UInt64)] {
        var entries: [(UInt64, UInt64)] = []
        let end = min(bodyOffset + Int(declaredSize), data.count)
        var i = bodyOffset
        while i < end {
            guard let entry = vint(data, i, keepMarker: true), entry.0 == seekEntryID else { break }
            i += entry.1
            guard let entrySize = valueSize(data, &i) else { break }
            let bodyStart = i
            let bodyEnd = min(bodyStart + Int(entrySize), end)
            var k = bodyStart
            var elementID: UInt64?
            var position: UInt64?
            while k < bodyEnd {
                guard let sub = vint(data, k, keepMarker: true) else { break }
                k += sub.1
                guard let subSize = valueSize(data, &k), subSize < 9 else { break }
                let valueStart = k
                let valueEnd = valueStart + Int(subSize)
                guard valueEnd <= bodyEnd else { break }
                var value: UInt64 = 0
                for x in valueStart..<valueEnd {
                    value = (value << 8) | UInt64(data[data.startIndex + x])
                }
                if sub.0 == seekIDElementID { elementID = value }
                if sub.0 == seekPosID { position = value }
                k = valueEnd
            }
            if let elementID, let position { entries.append((elementID, position)) }
            i = bodyStart + Int(entrySize)
        }
        return entries
    }

    private static func read(_ handle: FileHandle, _ offset: UInt64, _ length: Int) -> Data {
        do {
            try handle.seek(toOffset: offset)
            return (try? handle.read(upToCount: length)) ?? Data()
        } catch {
            return Data()
        }
    }
}
