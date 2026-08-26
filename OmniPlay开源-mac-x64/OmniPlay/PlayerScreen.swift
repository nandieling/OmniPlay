import SwiftUI
import GRDB
import UniformTypeIdentifiers // 🌟 引入系统类型以支持原生文件选择器
import NetFS
import Darwin
import Network

extension Notification.Name {
    static let standalonePlayerShouldClose = Notification.Name("StandalonePlayerShouldClose")
}

private enum RemoteISOPlaybackError: Error {
    case invalidContentLength
    case rangeUnsupported(Int)
    case invalidUDF
    case pathNotFound(String)
    case noPlayableStreams
    case localProxyUnavailable
}

private struct RemoteISOExtent {
    let offset: Int64
    let length: Int64
}

private struct RemoteISOFile {
    let path: String
    let fileName: String
    let size: Int64
    let extents: [RemoteISOExtent]
}

private struct RemoteISOProxyRegistration {
    let urls: [URL]
    let routeIDs: [String]
}

private extension Data {
    func uint8(at offset: Int) -> UInt8 {
        guard offset >= 0, offset < count else { return 0 }
        return self[startIndex + offset]
    }

    func uint16LE(at offset: Int) -> UInt16 {
        guard offset + 1 < count else { return 0 }
        return UInt16(uint8(at: offset)) | (UInt16(uint8(at: offset + 1)) << 8)
    }

    func uint32LE(at offset: Int) -> UInt32 {
        guard offset + 3 < count else { return 0 }
        return UInt32(uint16LE(at: offset)) | (UInt32(uint16LE(at: offset + 2)) << 16)
    }

    func uint64LE(at offset: Int) -> UInt64 {
        guard offset + 7 < count else { return 0 }
        return UInt64(uint32LE(at: offset)) | (UInt64(uint32LE(at: offset + 4)) << 32)
    }

    func uint16BE(at offset: Int) -> UInt16 {
        guard offset + 1 < count else { return 0 }
        return (UInt16(uint8(at: offset)) << 8) | UInt16(uint8(at: offset + 1))
    }

    func subdataSafe(offset: Int, length: Int) -> Data {
        guard offset >= 0, length > 0, offset < count else { return Data() }
        let end = Swift.min(count, offset + length)
        return subdata(in: offset..<end)
    }
}

private final class RemoteISOHTTPByteReader {
    private let requestURL: URL
    private let authorizationHeader: String?
    private let session: URLSession

    init(url: URL, credential: (username: String, password: String)?) {
        var resolvedURL = url
        var resolvedAuthorization: String?
        if var components = URLComponents(url: url, resolvingAgainstBaseURL: false) {
            let username = credential?.username ?? components.user
            let password = credential?.password ?? components.password ?? ""
            if let username, !username.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
               let data = "\(username):\(password)".data(using: .utf8) {
                resolvedAuthorization = "Basic \(data.base64EncodedString())"
            }
            components.user = nil
            components.password = nil
            resolvedURL = components.url ?? url
        }
        requestURL = resolvedURL
        authorizationHeader = resolvedAuthorization

        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 20
        configuration.timeoutIntervalForResource = 60
        session = URLSession(configuration: configuration, delegate: LocalNetworkTrustSessionDelegate.shared, delegateQueue: nil)
    }

    func contentLength(hint: Int64) async throws -> Int64 {
        if hint > 0 { return hint }

        var headRequest = URLRequest(url: requestURL)
        headRequest.httpMethod = "HEAD"
        applyAuthorization(to: &headRequest)
        do {
            let (_, response) = try await session.data(for: headRequest)
            if let http = response as? HTTPURLResponse,
               (200...299).contains(http.statusCode),
               let lengthText = http.value(forHTTPHeaderField: "Content-Length"),
               let length = Int64(lengthText), length > 0 {
                return length
            }
        } catch {}

        var rangeRequest = URLRequest(url: requestURL)
        rangeRequest.setValue("bytes=0-0", forHTTPHeaderField: "Range")
        applyAuthorization(to: &rangeRequest)
        let (_, response) = try await session.data(for: rangeRequest)
        guard let http = response as? HTTPURLResponse else { throw RemoteISOPlaybackError.invalidContentLength }
        if let total = Self.contentRangeTotal(http.value(forHTTPHeaderField: "Content-Range")) {
            return total
        }
        if let lengthText = http.value(forHTTPHeaderField: "Content-Length"),
           let length = Int64(lengthText), length > 1 {
            return length
        }
        throw RemoteISOPlaybackError.invalidContentLength
    }

    func read(offset: Int64, length: Int) async throws -> Data {
        guard length > 0 else { return Data() }
        let end = offset + Int64(length) - 1
        var request = URLRequest(url: requestURL)
        request.setValue("bytes=\(offset)-\(end)", forHTTPHeaderField: "Range")
        applyAuthorization(to: &request)
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw RemoteISOPlaybackError.rangeUnsupported(-1)
        }
        guard http.statusCode == 206 else {
            if http.statusCode == 200, offset == 0, data.count <= length {
                return data
            }
            throw RemoteISOPlaybackError.rangeUnsupported(http.statusCode)
        }
        return data.count > length ? data.prefix(length) : data
    }

    func read(file: RemoteISOFile, offset: Int64, length: Int) async throws -> Data {
        guard length > 0, offset < file.size else { return Data() }
        var remaining = Swift.min(Int64(length), file.size - offset)
        var skip = offset
        var output = Data()

        for extent in file.extents {
            if skip >= extent.length {
                skip -= extent.length
                continue
            }
            var extentOffset = extent.offset + skip
            var extentRemaining = extent.length - skip
            skip = 0
            while remaining > 0 && extentRemaining > 0 {
                let chunkLength = Int(Swift.min(remaining, extentRemaining, 512 * 1024))
                let data = try await read(offset: extentOffset, length: chunkLength)
                output.append(data)
                let readCount = Int64(data.count)
                guard readCount > 0 else { return output }
                extentOffset += readCount
                remaining -= readCount
                extentRemaining -= readCount
                if readCount < Int64(chunkLength) { return output }
            }
            if remaining <= 0 { break }
        }

        return output
    }

    private func applyAuthorization(to request: inout URLRequest) {
        if let authorizationHeader {
            request.setValue(authorizationHeader, forHTTPHeaderField: "Authorization")
        }
    }

    private static func contentRangeTotal(_ value: String?) -> Int64? {
        guard let value,
              let slash = value.lastIndex(of: "/") else { return nil }
        let totalText = value[value.index(after: slash)...]
        return Int64(totalText)
    }
}

private final class RemoteISOStreamProxy {
    static let shared = RemoteISOStreamProxy()

    private final class Route {
        let reader: RemoteISOHTTPByteReader
        let file: RemoteISOFile

        init(reader: RemoteISOHTTPByteReader, file: RemoteISOFile) {
            self.reader = reader
            self.file = file
        }
    }

    private let stateQueue = DispatchQueue(label: "nan.omniplay.remote-iso.proxy.state")
    private let serverQueue = DispatchQueue(label: "nan.omniplay.remote-iso.proxy.server")
    private var listener: NWListener?
    private var port: UInt16?
    private var routes: [String: Route] = [:]

    private final class StartupSignal {
        let semaphore = DispatchSemaphore(value: 0)
        private let lock = NSLock()
        private var completed = false
        private var readyValue = false

        func complete(ready: Bool) {
            lock.lock()
            guard !completed else {
                lock.unlock()
                return
            }
            completed = true
            readyValue = ready
            lock.unlock()
            semaphore.signal()
        }

        var isReady: Bool {
            lock.lock()
            let value = readyValue
            lock.unlock()
            return value
        }
    }

    func register(files: [RemoteISOFile], reader: RemoteISOHTTPByteReader) throws -> RemoteISOProxyRegistration {
        try stateQueue.sync {
            let port = try startIfNeededLocked()
            var urls: [URL] = []
            var routeIDs: [String] = []
            for file in files {
                let routeID = UUID().uuidString
                routes[routeID] = Route(reader: reader, file: file)
                routeIDs.append(routeID)

                var components = URLComponents()
                components.scheme = "http"
                components.host = "127.0.0.1"
                components.port = Int(port)
                components.path = "/remoteiso/\(routeID)/\(file.fileName)"
                if let url = components.url {
                    urls.append(url)
                }
            }
            guard urls.count == files.count else { throw RemoteISOPlaybackError.localProxyUnavailable }
            return RemoteISOProxyRegistration(urls: urls, routeIDs: routeIDs)
        }
    }

    func unregister(routeIDs: [String]) {
        stateQueue.sync {
            for routeID in routeIDs {
                routes.removeValue(forKey: routeID)
            }
        }
    }

    private func startIfNeededLocked() throws -> UInt16 {
        if let port, port > 0 { return port }
        let parameters = NWParameters.tcp
        parameters.allowLocalEndpointReuse = true
        let listener = try NWListener(using: parameters, on: .any)
        let startup = StartupSignal()
        listener.stateUpdateHandler = { state in
            switch state {
            case .ready:
                startup.complete(ready: true)
            case .failed, .cancelled:
                startup.complete(ready: false)
            default:
                break
            }
        }
        listener.newConnectionHandler = { [weak self] connection in
            self?.handle(connection: connection)
        }
        listener.start(queue: serverQueue)
        guard startup.semaphore.wait(timeout: .now() + 3) == .success,
              startup.isReady,
              let rawPort = listener.port?.rawValue,
              rawPort > 0 else {
            listener.cancel()
            throw RemoteISOPlaybackError.localProxyUnavailable
        }
        listener.stateUpdateHandler = nil
        self.listener = listener
        self.port = rawPort
        return rawPort
    }

    private func route(for routeID: String) -> Route? {
        stateQueue.sync { routes[routeID] }
    }

    private func handle(connection: NWConnection) {
        connection.start(queue: serverQueue)
        receiveHeader(connection: connection, buffer: Data())
    }

    private func receiveHeader(connection: NWConnection, buffer: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1024) { [weak self] data, _, _, error in
            guard let self else { return }
            if error != nil {
                connection.cancel()
                return
            }
            var nextBuffer = buffer
            if let data { nextBuffer.append(data) }
            if nextBuffer.range(of: Data("\r\n\r\n".utf8)) != nil {
                self.respond(to: nextBuffer, connection: connection)
            } else if nextBuffer.count < 256 * 1024 {
                self.receiveHeader(connection: connection, buffer: nextBuffer)
            } else {
                self.sendError(400, connection: connection)
            }
        }
    }

    private func respond(to requestData: Data, connection: NWConnection) {
        guard let requestText = String(data: requestData, encoding: .isoLatin1) else {
            sendError(400, connection: connection)
            return
        }
        let lines = requestText.components(separatedBy: "\r\n")
        guard let requestLine = lines.first else {
            sendError(400, connection: connection)
            return
        }
        let parts = requestLine.split(separator: " ")
        guard parts.count >= 2 else {
            sendError(400, connection: connection)
            return
        }
        let method = parts[0].uppercased()
        guard method == "GET" || method == "HEAD" else {
            sendError(405, connection: connection)
            return
        }

        let path = String(parts[1]).split(separator: "?").first.map(String.init) ?? String(parts[1])
        let pathParts = path.split(separator: "/").map(String.init)
        guard pathParts.count >= 2,
              pathParts[0] == "remoteiso",
              let route = route(for: pathParts[1]) else {
            sendError(404, connection: connection)
            return
        }

        var headers: [String: String] = [:]
        for line in lines.dropFirst() {
            guard let colon = line.firstIndex(of: ":") else { continue }
            let name = line[..<colon].trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
            let value = line[line.index(after: colon)...].trimmingCharacters(in: .whitespacesAndNewlines)
            headers[name] = value
        }

        guard let byteRange = parseByteRange(headers["range"], fileSize: route.file.size) else {
            sendError(416, connection: connection)
            return
        }
        let status = headers["range"] == nil ? 200 : 206
        stream(route: route, range: byteRange, method: method, status: status, connection: connection)
    }

    private func parseByteRange(_ header: String?, fileSize: Int64) -> (offset: Int64, length: Int64)? {
        guard fileSize > 0 else { return nil }
        guard let header, header.lowercased().hasPrefix("bytes=") else {
            return (0, fileSize)
        }
        let rangeText = String(header.dropFirst(6))
        guard let firstRange = rangeText.split(separator: ",").first else { return nil }
        let bounds = firstRange.split(separator: "-", omittingEmptySubsequences: false)
        guard bounds.count == 2 else { return nil }

        if bounds[0].isEmpty, let suffix = Int64(bounds[1]), suffix > 0 {
            let length = Swift.min(suffix, fileSize)
            return (fileSize - length, length)
        }

        guard let start = Int64(bounds[0]), start >= 0, start < fileSize else { return nil }
        let end = bounds[1].isEmpty ? (fileSize - 1) : Swift.min(Int64(bounds[1]) ?? (fileSize - 1), fileSize - 1)
        guard end >= start else { return nil }
        return (start, end - start + 1)
    }

    private func stream(route: Route, range: (offset: Int64, length: Int64), method: String, status: Int, connection: NWConnection) {
        Task.detached(priority: .userInitiated) {
            do {
                let statusLine = status == 206 ? "HTTP/1.1 206 Partial Content" : "HTTP/1.1 200 OK"
                var headers = [
                    statusLine,
                    "Content-Type: video/MP2T",
                    "Accept-Ranges: bytes",
                    "Content-Length: \(range.length)",
                    "Connection: close"
                ]
                if status == 206 {
                    let end = range.offset + range.length - 1
                    headers.append("Content-Range: bytes \(range.offset)-\(end)/\(route.file.size)")
                }
                headers.append("\r\n")
                try await self.send(Data(headers.joined(separator: "\r\n").utf8), connection: connection)

                if method != "HEAD" {
                    var sent: Int64 = 0
                    while sent < range.length {
                        let chunkLength = Int(Swift.min(range.length - sent, 512 * 1024))
                        let data = try await route.reader.read(file: route.file, offset: range.offset + sent, length: chunkLength)
                        guard !data.isEmpty else { break }
                        try await self.send(data, connection: connection)
                        sent += Int64(data.count)
                    }
                }
                connection.cancel()
            } catch {
                connection.cancel()
            }
        }
    }

    private func send(_ data: Data, connection: NWConnection) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            connection.send(content: data, completion: .contentProcessed { error in
                if let error {
                    continuation.resume(throwing: error)
                } else {
                    continuation.resume(returning: ())
                }
            })
        }
    }

    private func sendError(_ status: Int, connection: NWConnection) {
        let message: String
        switch status {
        case 400: message = "Bad Request"
        case 404: message = "Not Found"
        case 405: message = "Method Not Allowed"
        case 416: message = "Range Not Satisfiable"
        default: message = "Error"
        }
        let body = Data(message.utf8)
        let response = [
            "HTTP/1.1 \(status) \(message)",
            "Content-Length: \(body.count)",
            "Connection: close",
            "\r\n"
        ].joined(separator: "\r\n")
        connection.send(content: Data(response.utf8) + body, completion: .contentProcessed { _ in
            connection.cancel()
        })
    }
}

private final class RemoteISOImageParser {
    private struct UDFPartition {
        let number: Int
        let startBlock: Int64
        let blockCount: Int64
    }

    private struct UDFMetadataPartitionMap {
        let physicalPartitionNumber: Int
        let metadataFileLocation: Int64
        let metadataMirrorFileLocation: Int64
    }

    private struct UDFLongAD {
        let length: Int64
        let block: Int64
        let partitionRef: Int
    }

    private struct UDFFileEntry {
        let fileType: UInt8
        let informationLength: Int64
        let extents: [RemoteISOExtent]
        let inlineData: Data?
    }

    private struct UDFDirectoryEntry {
        let name: String
        let isDirectory: Bool
        let icb: UDFLongAD
    }

    private let reader: RemoteISOHTTPByteReader
    private let isoSize: Int64
    private var logicalBlockSize: Int64 = 2048
    private var partitionsByNumber: [Int: UDFPartition] = [:]
    private var partitionsByReference: [Int: UDFPartition] = [:]
    private var metadataPartitionMapsByReference: [Int: UDFMetadataPartitionMap] = [:]
    private var metadataExtentsByReference: [Int: [RemoteISOExtent]] = [:]

    init(reader: RemoteISOHTTPByteReader, isoSize: Int64) {
        self.reader = reader
        self.isoSize = isoSize
    }

    func bluRayStreamFiles() async throws -> [RemoteISOFile] {
        if let udfFiles = try? await parseUDFBluRayStreamFiles(), !udfFiles.isEmpty {
            return udfFiles
        }
        let isoFiles = try await parseISO9660BluRayStreamFiles()
        if !isoFiles.isEmpty { return isoFiles }
        throw RemoteISOPlaybackError.noPlayableStreams
    }

    private func parseUDFBluRayStreamFiles() async throws -> [RemoteISOFile] {
        partitionsByNumber.removeAll()
        partitionsByReference.removeAll()
        metadataPartitionMapsByReference.removeAll()
        metadataExtentsByReference.removeAll()

        let anchor = try await findUDFAnchor()
        let mainLength = Int64(anchor.uint32LE(at: 16))
        let mainLocation = Int64(anchor.uint32LE(at: 20))
        guard mainLength > 0, mainLocation > 0 else { throw RemoteISOPlaybackError.invalidUDF }

        var fileSetAD: UDFLongAD?
        var partitionMap: [Int: Int] = [:]
        let descriptorSectors = Swift.min((mainLength + 2047) / 2048, 4096)
        for sectorOffset in 0..<descriptorSectors {
            let descriptor = try await readSector(mainLocation + sectorOffset)
            let tag = descriptor.uint16LE(at: 0)
            if tag == 8 { break }
            switch tag {
            case 5:
                let number = Int(descriptor.uint16LE(at: 22))
                let start = Int64(descriptor.uint32LE(at: 188))
                let length = Int64(descriptor.uint32LE(at: 192))
                partitionsByNumber[number] = UDFPartition(number: number, startBlock: start, blockCount: length)
            case 6:
                let blockSize = Int64(descriptor.uint32LE(at: 212))
                if blockSize > 0 { logicalBlockSize = blockSize }
                fileSetAD = parseLongAD(descriptor, offset: 248)
                partitionMap = parsePartitionMaps(descriptor)
            default:
                continue
            }
        }

        for (reference, partitionNumber) in partitionMap {
            if let partition = partitionsByNumber[partitionNumber] {
                partitionsByReference[reference] = partition
            }
        }
        if partitionsByReference.isEmpty {
            for (index, partition) in partitionsByNumber.values.sorted(by: { $0.number < $1.number }).enumerated() {
                partitionsByReference[index] = partition
            }
        }
        try await resolveMetadataPartitions()

        guard let fileSetAD else { throw RemoteISOPlaybackError.invalidUDF }
        let fileSetDescriptor = try await readDescriptor(at: fileSetAD)
        guard fileSetDescriptor.uint16LE(at: 0) == 256 else { throw RemoteISOPlaybackError.invalidUDF }
        let rootICB = parseLongAD(fileSetDescriptor, offset: 400)
        let streamICB = try await locateUDFBluRayStreamDirectory(rootICB: rootICB)
        let entries = try await readUDFDirectory(icb: streamICB)

        var files: [RemoteISOFile] = []
        for entry in entries where !entry.isDirectory {
            let lower = entry.name.lowercased()
            guard lower.hasSuffix(".m2ts") || lower.hasSuffix(".m2t") || lower.hasSuffix(".ts") else { continue }
            let fileEntry = try await readUDFFileEntry(icb: entry.icb)
            let extents = limitedExtents(fileEntry.extents, length: fileEntry.informationLength)
            guard fileEntry.informationLength > 0, !extents.isEmpty else { continue }
            files.append(RemoteISOFile(path: "BDMV/STREAM/\(entry.name)", fileName: entry.name, size: fileEntry.informationLength, extents: extents))
        }
        return files
    }

    private func findUDFAnchor() async throws -> Data {
        let sectors = Swift.max(0, isoSize / 2048)
        let candidates = [Int64(256), sectors - 256, sectors - 1].filter { $0 >= 0 }
        for sector in candidates {
            let data = try await readSector(sector)
            if data.uint16LE(at: 0) == 2 {
                return data
            }
        }
        throw RemoteISOPlaybackError.invalidUDF
    }

    private func parsePartitionMaps(_ descriptor: Data) -> [Int: Int] {
        let mapTableLength = Int(descriptor.uint32LE(at: 264))
        let mapCount = Int(descriptor.uint32LE(at: 268))
        var result: [Int: Int] = [:]
        var offset = 440
        for reference in 0..<mapCount {
            guard offset + 2 <= descriptor.count, offset < 440 + mapTableLength else { break }
            let type = descriptor.uint8(at: offset)
            let length = Int(descriptor.uint8(at: offset + 1))
            guard length > 0, offset + length <= descriptor.count else { break }
            if type == 1, length >= 6 {
                result[reference] = Int(descriptor.uint16LE(at: offset + 4))
            } else if type == 2, length >= 59 {
                let identifier = String(data: descriptor.subdataSafe(offset: offset + 5, length: 23), encoding: .ascii) ?? ""
                if identifier.contains("UDF Metadata") {
                    metadataPartitionMapsByReference[reference] = UDFMetadataPartitionMap(
                        physicalPartitionNumber: Int(descriptor.uint16LE(at: offset + 38)),
                        metadataFileLocation: Int64(descriptor.uint32LE(at: offset + 40)),
                        metadataMirrorFileLocation: Int64(descriptor.uint32LE(at: offset + 44))
                    )
                }
            }
            offset += length
        }
        return result
    }

    private func resolveMetadataPartitions() async throws {
        for (reference, map) in metadataPartitionMapsByReference {
            let locations = [map.metadataFileLocation, map.metadataMirrorFileLocation]
            for location in locations where location > 0 {
                let metadataICB = UDFLongAD(
                    length: logicalBlockSize,
                    block: location,
                    partitionRef: map.physicalPartitionNumber
                )
                if let fileEntry = try? await readUDFFileEntry(icb: metadataICB) {
                    let extents = limitedExtents(fileEntry.extents, length: fileEntry.informationLength)
                    if !extents.isEmpty {
                        metadataExtentsByReference[reference] = extents
                        break
                    }
                }
            }
        }
    }

    private func locateUDFBluRayStreamDirectory(rootICB: UDFLongAD) async throws -> UDFLongAD {
        if let streamICB = try? await traverseUDFPath(rootICB: rootICB, components: ["BDMV", "STREAM"]) {
            return streamICB
        }
        var visited = 0
        if let streamICB = try await searchUDFBluRayStreamDirectory(in: rootICB, depth: 0, visited: &visited) {
            return streamICB
        }
        throw RemoteISOPlaybackError.pathNotFound("BDMV/STREAM")
    }

    private func searchUDFBluRayStreamDirectory(in directoryICB: UDFLongAD, depth: Int, visited: inout Int) async throws -> UDFLongAD? {
        guard depth <= 4, visited < 256 else { return nil }
        visited += 1

        let entries = try await readUDFDirectory(icb: directoryICB)
        if let bdmv = entries.first(where: { $0.isDirectory && $0.name.caseInsensitiveCompare("BDMV") == .orderedSame }) {
            let bdmvEntries = try await readUDFDirectory(icb: bdmv.icb)
            if let stream = bdmvEntries.first(where: { $0.isDirectory && $0.name.caseInsensitiveCompare("STREAM") == .orderedSame }) {
                return stream.icb
            }
        }

        for entry in entries where entry.isDirectory {
            let name = entry.name.lowercased()
            guard name != "certificate", name != "any!" else { continue }
            if let found = try? await searchUDFBluRayStreamDirectory(in: entry.icb, depth: depth + 1, visited: &visited) {
                return found
            }
        }
        return nil
    }

    private func traverseUDFPath(rootICB: UDFLongAD, components: [String]) async throws -> UDFLongAD {
        var current = rootICB
        for component in components {
            let entries = try await readUDFDirectory(icb: current)
            guard let next = entries.first(where: { $0.isDirectory && $0.name.caseInsensitiveCompare(component) == .orderedSame }) else {
                throw RemoteISOPlaybackError.pathNotFound(component)
            }
            current = next.icb
        }
        return current
    }

    private func readUDFDirectory(icb: UDFLongAD) async throws -> [UDFDirectoryEntry] {
        let fileEntry = try await readUDFFileEntry(icb: icb)
        let data: Data
        if let inlineData = fileEntry.inlineData {
            data = inlineData
        } else {
            let length = Int(Swift.min(fileEntry.informationLength, 64 * 1024 * 1024))
            data = try await readAbsoluteExtents(fileEntry.extents, maxLength: length)
        }

        var entries: [UDFDirectoryEntry] = []
        var offset = 0
        while offset + 38 <= data.count {
            let tag = data.uint16LE(at: offset)
            if tag != 257 {
                offset += 4
                continue
            }
            let characteristics = data.uint8(at: offset + 18)
            let nameLength = Int(data.uint8(at: offset + 19))
            let icb = parseLongAD(data, offset: offset + 20)
            let implementationUseLength = Int(data.uint16LE(at: offset + 36))
            let nameOffset = offset + 38 + implementationUseLength
            let entryLength = align4(38 + implementationUseLength + nameLength)
            guard entryLength > 0, offset + entryLength <= data.count else { break }
            let nameData = data.subdataSafe(offset: nameOffset, length: nameLength)
            let name = decodeOSTACompressedUnicode(nameData)
            let isParent = (characteristics & 0x08) != 0
            if !name.isEmpty, !isParent {
                entries.append(UDFDirectoryEntry(name: name, isDirectory: (characteristics & 0x02) != 0, icb: icb))
            }
            offset += entryLength
        }
        return entries
    }

    private func readUDFFileEntry(icb: UDFLongAD) async throws -> UDFFileEntry {
        let descriptor = try await readDescriptor(at: icb)
        let tag = descriptor.uint16LE(at: 0)
        guard tag == 261 || tag == 266 else { throw RemoteISOPlaybackError.invalidUDF }

        let fileType = descriptor.uint8(at: 27)
        let informationLength = Int64(bitPattern: descriptor.uint64LE(at: 56))
        let flags = descriptor.uint16LE(at: 34) & 0x0007
        let lengthEAOffset = tag == 261 ? 168 : 208
        let lengthADOffset = tag == 261 ? 172 : 212
        let allocationOffset = tag == 261 ? 176 : 216
        let lengthEA = Int(descriptor.uint32LE(at: lengthEAOffset))
        let lengthAD = Int(descriptor.uint32LE(at: lengthADOffset))
        let adOffset = allocationOffset + lengthEA
        let adData = descriptor.subdataSafe(offset: adOffset, length: lengthAD)

        if flags == 3 {
            return UDFFileEntry(fileType: fileType, informationLength: Int64(adData.count), extents: [], inlineData: adData)
        }

        var extents: [RemoteISOExtent] = []
        var offset = 0
        let descriptorLength: Int
        switch flags {
        case 0: descriptorLength = 8
        case 1: descriptorLength = 16
        case 2: descriptorLength = 20
        default: descriptorLength = 0
        }
        while descriptorLength > 0, offset + descriptorLength <= adData.count {
            let rawLength = adData.uint32LE(at: offset)
            let extentType = rawLength >> 30
            let length = Int64(rawLength & 0x3fffffff)
            if length > 0, extentType != 3 {
                let block: Int64
                let partitionRef: Int
                if flags == 0 {
                    block = Int64(adData.uint32LE(at: offset + 4))
                    partitionRef = icb.partitionRef
                } else if flags == 1 {
                    block = Int64(adData.uint32LE(at: offset + 4))
                    partitionRef = Int(adData.uint16LE(at: offset + 8))
                } else {
                    block = Int64(adData.uint32LE(at: offset + 12))
                    partitionRef = Int(adData.uint16LE(at: offset + 16))
                }
                if let absoluteOffset = absoluteOffset(block: block, partitionRef: partitionRef) {
                    extents.append(RemoteISOExtent(offset: absoluteOffset, length: length))
                }
            }
            offset += descriptorLength
        }
        return UDFFileEntry(fileType: fileType, informationLength: informationLength, extents: extents, inlineData: nil)
    }

    private func parseLongAD(_ data: Data, offset: Int) -> UDFLongAD {
        let rawLength = data.uint32LE(at: offset)
        return UDFLongAD(
            length: Int64(rawLength & 0x3fffffff),
            block: Int64(data.uint32LE(at: offset + 4)),
            partitionRef: Int(data.uint16LE(at: offset + 8))
        )
    }

    private func readDescriptor(at ad: UDFLongAD) async throws -> Data {
        guard let offset = absoluteOffset(block: ad.block, partitionRef: ad.partitionRef) else {
            throw RemoteISOPlaybackError.invalidUDF
        }
        return try await reader.read(offset: offset, length: Int(logicalBlockSize))
    }

    private func absoluteOffset(block: Int64, partitionRef: Int) -> Int64? {
        if let metadataExtents = metadataExtentsByReference[partitionRef],
           let metadataOffset = offsetInExtents(metadataExtents, fileOffset: block * logicalBlockSize) {
            return metadataOffset
        }
        let partition = partitionsByReference[partitionRef] ?? partitionsByNumber[partitionRef] ?? partitionsByReference.values.first
        guard let partition else { return nil }
        return (partition.startBlock + block) * logicalBlockSize
    }

    private func offsetInExtents(_ extents: [RemoteISOExtent], fileOffset: Int64) -> Int64? {
        var remaining = fileOffset
        for extent in extents {
            if remaining < extent.length {
                return extent.offset + remaining
            }
            remaining -= extent.length
        }
        return nil
    }

    private func readAbsoluteExtents(_ extents: [RemoteISOExtent], maxLength: Int) async throws -> Data {
        var output = Data()
        var remaining = maxLength
        for extent in extents where remaining > 0 {
            var extentOffset = extent.offset
            var extentRemaining = Int(Swift.min(Int64(remaining), extent.length))
            while extentRemaining > 0 {
                let chunkLength = Swift.min(extentRemaining, 512 * 1024)
                let data = try await reader.read(offset: extentOffset, length: chunkLength)
                guard !data.isEmpty else { return output }
                output.append(data)
                extentOffset += Int64(data.count)
                extentRemaining -= data.count
                remaining -= data.count
                if data.count < chunkLength { return output }
            }
        }
        return output
    }

    private func limitedExtents(_ extents: [RemoteISOExtent], length: Int64) -> [RemoteISOExtent] {
        var remaining = length
        var result: [RemoteISOExtent] = []
        for extent in extents where remaining > 0 {
            let resolvedLength = Swift.min(extent.length, remaining)
            result.append(RemoteISOExtent(offset: extent.offset, length: resolvedLength))
            remaining -= resolvedLength
        }
        return result
    }

    private func parseISO9660BluRayStreamFiles() async throws -> [RemoteISOFile] {
        let pvd = try await readSector(16)
        guard String(data: pvd.subdataSafe(offset: 1, length: 5), encoding: .ascii) == "CD001" else {
            return []
        }
        guard let root = parseISO9660DirectoryRecord(pvd, offset: 156) else { return [] }
        let stream = try await locateISO9660BluRayStreamDirectory(root: root)
        let entries = try await readISO9660Directory(stream)
        return entries.compactMap { entry in
            guard !entry.isDirectory else { return nil }
            let lower = entry.name.lowercased()
            guard lower.hasSuffix(".m2ts") || lower.hasSuffix(".m2t") || lower.hasSuffix(".ts") else { return nil }
            let offset = Int64(entry.extent) * 2048
            let size = Int64(entry.size)
            return RemoteISOFile(path: "BDMV/STREAM/\(entry.name)", fileName: entry.name, size: size, extents: [RemoteISOExtent(offset: offset, length: size)])
        }
    }

    private struct ISO9660Entry {
        let name: String
        let extent: UInt32
        let size: UInt32
        let isDirectory: Bool
    }

    private func locateISO9660BluRayStreamDirectory(root: ISO9660Entry) async throws -> ISO9660Entry {
        if let bdmv = try? await findISO9660Entry(in: root, named: "BDMV", directory: true),
           let stream = try? await findISO9660Entry(in: bdmv, named: "STREAM", directory: true) {
            return stream
        }
        var visited = 0
        if let stream = try await searchISO9660BluRayStreamDirectory(in: root, depth: 0, visited: &visited) {
            return stream
        }
        throw RemoteISOPlaybackError.pathNotFound("BDMV/STREAM")
    }

    private func searchISO9660BluRayStreamDirectory(in directory: ISO9660Entry, depth: Int, visited: inout Int) async throws -> ISO9660Entry? {
        guard depth <= 4, visited < 256 else { return nil }
        visited += 1

        let entries = try await readISO9660Directory(directory)
        if let bdmv = entries.first(where: { $0.isDirectory && $0.name.caseInsensitiveCompare("BDMV") == .orderedSame }) {
            let bdmvEntries = try await readISO9660Directory(bdmv)
            if let stream = bdmvEntries.first(where: { $0.isDirectory && $0.name.caseInsensitiveCompare("STREAM") == .orderedSame }) {
                return stream
            }
        }

        for entry in entries where entry.isDirectory {
            let name = entry.name.lowercased()
            guard name != "certificate", name != "any!" else { continue }
            if let found = try? await searchISO9660BluRayStreamDirectory(in: entry, depth: depth + 1, visited: &visited) {
                return found
            }
        }
        return nil
    }

    private func findISO9660Entry(in directory: ISO9660Entry, named name: String, directory isDirectory: Bool) async throws -> ISO9660Entry {
        let entries = try await readISO9660Directory(directory)
        guard let entry = entries.first(where: { $0.isDirectory == isDirectory && $0.name.caseInsensitiveCompare(name) == .orderedSame }) else {
            throw RemoteISOPlaybackError.pathNotFound(name)
        }
        return entry
    }

    private func readISO9660Directory(_ directory: ISO9660Entry) async throws -> [ISO9660Entry] {
        let offset = Int64(directory.extent) * 2048
        let length = Int(directory.size)
        let data = try await reader.read(offset: offset, length: length)
        var entries: [ISO9660Entry] = []
        var cursor = 0
        while cursor < data.count {
            let recordLength = Int(data.uint8(at: cursor))
            if recordLength == 0 {
                cursor = ((cursor / 2048) + 1) * 2048
                continue
            }
            if let entry = parseISO9660DirectoryRecord(data, offset: cursor),
               entry.name != ".", entry.name != ".." {
                entries.append(entry)
            }
            cursor += recordLength
        }
        return entries
    }

    private func parseISO9660DirectoryRecord(_ data: Data, offset: Int) -> ISO9660Entry? {
        guard offset + 34 <= data.count else { return nil }
        let recordLength = Int(data.uint8(at: offset))
        guard recordLength >= 34, offset + recordLength <= data.count else { return nil }
        let extent = data.uint32LE(at: offset + 2)
        let size = data.uint32LE(at: offset + 10)
        let flags = data.uint8(at: offset + 25)
        let nameLength = Int(data.uint8(at: offset + 32))
        let rawName = data.subdataSafe(offset: offset + 33, length: nameLength)
        let name: String
        if rawName.count == 1, rawName.uint8(at: 0) == 0 {
            name = "."
        } else if rawName.count == 1, rawName.uint8(at: 0) == 1 {
            name = ".."
        } else {
            let decoded = String(data: rawName, encoding: .ascii) ?? ""
            name = decoded
                .replacingOccurrences(of: ";1", with: "", options: [.caseInsensitive])
                .trimmingCharacters(in: CharacterSet(charactersIn: "."))
        }
        return ISO9660Entry(name: name, extent: extent, size: size, isDirectory: (flags & 0x02) != 0)
    }

    private func readSector(_ sector: Int64) async throws -> Data {
        try await reader.read(offset: sector * 2048, length: 2048)
    }

    private func align4(_ value: Int) -> Int {
        (value + 3) & ~3
    }

    private func decodeOSTACompressedUnicode(_ data: Data) -> String {
        guard !data.isEmpty else { return "" }
        let compressionID = data.uint8(at: 0)
        if compressionID == 8 {
            let payload = data.subdataSafe(offset: 1, length: data.count - 1)
            return String(data: payload, encoding: .utf8)
                ?? String(data: payload, encoding: .isoLatin1)
                ?? ""
        }
        if compressionID == 16 {
            var scalars = String.UnicodeScalarView()
            var offset = 1
            while offset + 1 < data.count {
                let value = data.uint16BE(at: offset)
                if let scalar = UnicodeScalar(Int(value)) {
                    scalars.append(scalar)
                }
                offset += 2
            }
            return String(scalars)
        }
        return String(data: data, encoding: .utf8)
            ?? String(data: data, encoding: .isoLatin1)
            ?? ""
    }
}

// ==========================================
// 🌟 长按快进/快退的手势修饰器
// ==========================================
struct ContinuousPressModifier: ViewModifier {
    let action: () -> Void
    @State private var timer: Timer?
    @State private var isPressing = false

    func body(content: Content) -> some View {
        content
            .scaleEffect(isPressing ? 0.85 : 1.0)
            .animation(.easeInOut(duration: 0.1), value: isPressing)
            .simultaneousGesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { _ in
                        if !isPressing {
                            isPressing = true
                            action()
                            timer = Timer.scheduledTimer(withTimeInterval: 0.2, repeats: true) { _ in action() }
                        }
                    }
                    .onEnded { _ in
                        isPressing = false
                        timer?.invalidate()
                        timer = nil
                    }
            )
    }
}

extension View {
    func onContinuousPress(action: @escaping () -> Void) -> some View { self.modifier(ContinuousPressModifier(action: action)) }
}

// ==========================================
// 🌟 核心播放器视图
// ==========================================
private struct PlayerTimelineSegment: Identifiable {
    let id: String
    let startRatio: Double
    let widthRatio: Double
    let isCurrent: Bool
}

struct PlayerScreen: View {
    let movie: Movie
    var initialFileId: String? = nil
    var isStandaloneWindow: Bool = false
    var initialSourceBasePath: String? = nil
    var initialSourceProtocolType: String? = nil
    var initialSourceAuthConfig: String? = nil
    var initialPlaylistFiles: [VideoFile]? = nil

    @StateObject private var playerManager = MPVPlayerManager()
    @AppStorage("seekDuration") var seekDuration: Int = 10

    @State private var videoURLs: [URL] = []
    @State private var allPlaylistFiles: [VideoFile] = []
    @State private var rootFolderURL: URL?
    @State private var errorMessage: String?

    @State private var currentVideoFileId: String?
    @State private var startPosition: Double = 0.0
    @State private var playbackSourceBaseURL: String?
    @State private var playbackSourceProtocolType: String?
    @State private var playbackSourceAuthConfig: String?

    @State private var isBluRayFolder: Bool = false
    @State private var blurayRootPath: String? = nil
    @State private var attachedDiscImageMountPaths: [String] = []
    @State private var remoteISOProxyRouteIDs: [String] = []

    @State private var showControls = true
    @State private var isCursorHidden = false
    @State private var hideUITask: Task<Void, Never>? = nil
    @State private var menuInteractionTask: Task<Void, Never>? = nil
    @State private var isMenuInteractionActive = false
    @State private var isPointerInTopControlArea = false
    @State private var hasIssuedLoad = false
    @State private var hasPersistedBeforeExit = false
    @State private var isClosingPlayback = false
    @State private var embyRemotePlaybackContext: EmbyRemotePlaybackContext?
    @State private var isSwitchingEmbyRemoteStream = false
    @Environment(\.dismiss) var dismiss

    private let topControlHoldHeight: CGFloat = 88

    private struct EmbyStreamSelection: Equatable {
        let audioStreamIndex: Int?
        let subtitleStreamIndex: Int?
        let subtitleMethod: String?
    }

    private struct EmbyMediaStreamOption: Identifiable, Equatable {
        let index: Int
        let displayName: String
        let method: String?

        var id: Int { index }
    }

    private struct EmbyStreamOptions {
        let audioTracks: [EmbyMediaStreamOption]
        let subtitleTracks: [EmbyMediaStreamOption]
        let selection: EmbyStreamSelection
    }

    private struct EmbyRemotePlaybackContext {
        let itemId: String
        let mediaSourceId: String
        let fileName: String
        let sourceBaseUrl: URL
        let token: String?
        let userId: String?
        let audioTracks: [EmbyMediaStreamOption]
        let subtitleTracks: [EmbyMediaStreamOption]
        var streamSelection: EmbyStreamSelection
    }

    private func log(_ message: String) {
        print("[PlayerScreen] \(message)")
    }

    private func traceClose(_ message: String) {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let queue = String(cString: __dispatch_queue_get_label(nil), encoding: .utf8) ?? "unknown"
        let thread = Thread.isMainThread ? "main" : "bg"
        print("[PlayerScreenClose][\(formatter.string(from: Date()))][\(thread)][q:\(queue)] \(message)")
    }

    // 智能推算当前播放集数标题
    private var currentPlaybackTitle: String {
        guard let id = currentVideoFileId, let file = allPlaylistFiles.first(where: { $0.id == id }) else { return movie.title }
        let resolvedInfo = EpisodeMetadataOverrideStore.shared.resolvedEpisodeInfo(
            fileId: file.id,
            fileName: file.fileName,
            fallbackIndex: 0
        )
        return resolvedInfo.isTVShow ? "\(movie.title) \(resolvedInfo.displayName)" : movie.title
    }

    private var playbackProgressRatio: Double {
        let dur = playerManager.duration
        guard dur > 0 else { return 0 }
        return min(max(playerManager.currentTimePos / dur, 0), 1)
    }

    private var hasNextEpisode: Bool {
        guard let id = currentVideoFileId,
              let currentIndex = allPlaylistFiles.firstIndex(where: { $0.id == id }) else { return false }
        return currentIndex < (allPlaylistFiles.count - 1)
    }

    private var isTVPlayback: Bool {
        if allPlaylistFiles.enumerated().contains(where: { idx, file in
            MediaNameParser.parseEpisodeInfo(from: file.fileName, fallbackIndex: idx).isTVShow
        }) {
            return true
        }
        return allPlaylistFiles.count > 1 && (movie.title.contains("季") || movie.title.contains("集"))
    }

    private var shouldShowNextEpisodeButton: Bool {
        isTVPlayback && showControls && !isCursorHidden && hasNextEpisode && playbackProgressRatio >= 0.95
    }

    private var isMultipartMoviePlayback: Bool {
        !isTVPlayback && allPlaylistFiles.count > 1
    }

    private var currentPlaylistFileIndex: Int? {
        if let id = currentVideoFileId,
           let index = allPlaylistFiles.firstIndex(where: { $0.id == id }) {
            return index
        }
        let index = playerManager.currentPlaylistIndex
        return allPlaylistFiles.indices.contains(index) ? index : nil
    }

    private var playerTimeline: (position: Double, duration: Double) {
        guard isMultipartMoviePlayback, let currentIndex = currentPlaylistFileIndex else {
            return (max(playerManager.currentTimePos, 0), max(playerManager.duration, 0))
        }

        var position = 0.0
        var duration = 0.0
        for index in allPlaylistFiles.indices {
            let partDuration = index == currentIndex
                ? max(allPlaylistFiles[index].duration, max(playerManager.duration, playerManager.currentTimePos))
                : max(allPlaylistFiles[index].duration, 0)
            if index < currentIndex {
                position += partDuration
            }
            duration += partDuration
        }
        position += max(playerManager.currentTimePos, 0)
        return (min(position, max(duration, position)), duration)
    }

    private var playerTimelinePosition: Float {
        let timeline = playerTimeline
        guard timeline.duration > 0 else { return 0 }
        return Float(min(max(timeline.position / timeline.duration, 0), 1))
    }

    private var playerTimelineCurrentText: String {
        formatPlaybackTime(playerTimeline.position)
    }

    private var playerTimelineRemainingText: String {
        let timeline = playerTimeline
        guard timeline.duration > 0 else { return "-00:00" }
        return "-\(formatPlaybackTime(max(0, timeline.duration - timeline.position)))"
    }

    private var playerTimelineDurationText: String {
        let timeline = playerTimeline
        guard timeline.duration > 0 else { return "--:--" }
        return formatPlaybackTime(timeline.duration)
    }

    private var playerTimelineSegments: [PlayerTimelineSegment] {
        guard isMultipartMoviePlayback,
              let currentIndex = currentPlaylistFileIndex else { return [] }

        let durations = allPlaylistFiles.indices.map { index in
            index == currentIndex
                ? max(allPlaylistFiles[index].duration, max(playerManager.duration, playerManager.currentTimePos))
                : max(allPlaylistFiles[index].duration, 0)
        }
        let totalDuration = durations.reduce(0, +)
        guard allPlaylistFiles.count > 1, totalDuration > 0 else { return [] }

        var cursor = 0.0
        return allPlaylistFiles.indices.map { index in
            let duration = durations[index]
            let segment = PlayerTimelineSegment(
                id: allPlaylistFiles[index].id,
                startRatio: cursor / totalDuration,
                widthRatio: duration / totalDuration,
                isCurrent: index == currentIndex
            )
            cursor += duration
            return segment
        }
    }

    private var playbackTimelineSlider: some View {
        ZStack {
            Slider(
                value: Binding(
                    get: { isMultipartMoviePlayback ? playerTimelinePosition : playerManager.position },
                    set: { newValue in
                        seekPlayerTimeline(to: newValue)
                        resetHideTimer()
                    }
                )
            )
            .tint(.red)

            if isMultipartMoviePlayback && playerTimelineSegments.count > 1 {
                GeometryReader { proxy in
                    ForEach(playerTimelineSegments) { segment in
                        let segmentWidth = max(1, proxy.size.width * CGFloat(segment.widthRatio))
                        let segmentCenter = proxy.size.width * CGFloat(segment.startRatio + segment.widthRatio / 2)
                        Rectangle()
                            .fill(segment.isCurrent ? Color.red.opacity(0.16) : Color.white.opacity(0.06))
                            .frame(width: segmentWidth, height: 8)
                            .position(x: segmentCenter, y: proxy.size.height / 2)

                        if segment.startRatio > 0 {
                            Rectangle()
                                .fill(Color.white.opacity(0.85))
                                .frame(width: 1, height: 10)
                                .position(x: proxy.size.width * CGFloat(segment.startRatio), y: proxy.size.height / 2)
                        }
                    }
                }
                .allowsHitTesting(false)
            }
        }
        .frame(height: 24)
    }

    private func formatPlaybackTime(_ seconds: Double) -> String {
        let safeSeconds = max(0, Int(seconds.isFinite ? seconds.rounded(.down) : 0))
        let hours = safeSeconds / 3600
        let minutes = (safeSeconds % 3600) / 60
        let remainingSeconds = safeSeconds % 60
        return hours > 0
            ? String(format: "%d:%02d:%02d", hours, minutes, remainingSeconds)
            : String(format: "%d:%02d", minutes, remainingSeconds)
    }

    private func seekPlayerTimeline(to newPosition: Float) {
        guard isMultipartMoviePlayback,
              let currentIndex = currentPlaylistFileIndex else {
            playerManager.setPosition(newPosition)
            return
        }

        let timeline = playerTimeline
        guard timeline.duration > 0 else { return }
        let targetPosition = min(max(Double(newPosition), 0), 1) * timeline.duration
        var cursor = 0.0
        for index in allPlaylistFiles.indices {
            let partDuration = index == currentIndex
                ? max(allPlaylistFiles[index].duration, max(playerManager.duration, playerManager.currentTimePos))
                : max(allPlaylistFiles[index].duration, 0)
            guard partDuration > 0 else { continue }
            if targetPosition <= cursor + partDuration || index == (allPlaylistFiles.indices.last ?? index) {
                let localPosition = min(max(targetPosition - cursor, 0), partDuration)
                if index == currentIndex {
                    playerManager.seekAbsolute(seconds: localPosition)
                } else {
                    currentVideoFileId = allPlaylistFiles[index].id
                    playerManager.playPlaylistIndex(index, startPosition: localPosition)
                }
                return
            }
            cursor += partDuration
        }
    }

    var body: some View {
        ZStack {
            Color.black.edgesIgnoringSafeArea(.all)

            if !videoURLs.isEmpty {
                MPVVideoView(playerManager: playerManager)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .clipped()
                .onTapGesture { withAnimation(.easeInOut(duration: 0.3)) { showControls.toggle() }; if showControls { resetHideTimer() } }
            } else if let error = errorMessage {
                Text(error).foregroundColor(.red).font(.title2)
            } else {
                ProgressView("准备播放环境...").foregroundColor(.white)
            }

            if showControls {
                VStack {
                    // 顶部栏
                    HStack {
                        if !isStandaloneWindow {
                            Button(action: closePlayer) {
                                Image(systemName: "chevron.left.circle.fill").font(.system(size: 32))
                            }
                            .buttonStyle(.plain)
                            .onHover { if $0 { NSCursor.pointingHand.push() } else { NSCursor.pop() } }
                        }
                        Text(currentPlaybackTitle).font(.title3).fontWeight(.bold).padding(.leading, 10)
                        Spacer()
                    }
                    .foregroundColor(.white)
                    .padding(20)
                    .background(LinearGradient(gradient: Gradient(colors: [.black.opacity(0.8), .clear]), startPoint: .top, endPoint: .bottom))
                    .contentShape(Rectangle())
                    .onHover { hovering in
                        isPointerInTopControlArea = hovering
                        if hovering {
                            hideUITask?.cancel()
                            setCursorHidden(false)
                        } else {
                            resetHideTimer()
                        }
                    }

                    Spacer()

                    // 底部进度条与菜单
                    VStack(spacing: 15) {
                        if shouldShowNextEpisodeButton {
                            HStack {
                                Spacer()
                                Button(action: playNextEpisodeAndMarkCurrentWatched) {
                                    Text("播放下一集")
                                        .font(.system(size: 14, weight: .semibold))
                                        .foregroundColor(.white)
                                        .padding(.horizontal, 14)
                                        .padding(.vertical, 8)
                                        .background(Color.black.opacity(0.72))
                                        .overlay(
                                            RoundedRectangle(cornerRadius: 10)
                                                .stroke(Color.white.opacity(0.25), lineWidth: 1)
                                        )
                                        .clipShape(RoundedRectangle(cornerRadius: 10))
                                }
                                .buttonStyle(.plain)
                                .help("播放下一集")
                            }
                        }

                        // 底部播放控制区（常见播放器布局）
                        HStack(spacing: 40) {
                            Image(systemName: "backward.end.fill")
                                .font(.system(size: 34))
                                .onContinuousPress {
                                    playerManager.seekRelative(seconds: Double(-seekDuration))
                                    resetHideTimer()
                                }
                            Button(action: {
                                playerManager.playOrPause()
                                resetHideTimer()
                            }) {
                                Image(systemName: playerManager.isPlaying ? "pause.circle.fill" : "play.circle.fill")
                                    .font(.system(size: 60))
                                    .shadow(radius: 10)
                            }
                            .buttonStyle(.plain)
                            Image(systemName: "forward.end.fill")
                                .font(.system(size: 34))
                                .onContinuousPress {
                                    playerManager.seekRelative(seconds: Double(seekDuration))
                                    resetHideTimer()
                                }
                        }
                        .foregroundColor(.white)
                        .onHover { if $0 { NSCursor.pointingHand.push() } else { NSCursor.pop() } }

                        HStack {
                            Text(isMultipartMoviePlayback ? playerTimelineCurrentText : playerManager.currentTime).font(.caption.monospacedDigit())
                            playbackTimelineSlider
                            Text(isMultipartMoviePlayback ? playerTimelineDurationText : playerManager.remainingTime).font(.caption.monospacedDigit())
                        }

                        HStack(spacing: 16) {
                            Spacer()

                            // 🌟 音轨菜单
                            Menu {
                                if let embyRemotePlaybackContext {
                                    if embyRemotePlaybackContext.audioTracks.isEmpty {
                                        Text("默认音轨")
                                    } else {
                                        ForEach(embyRemotePlaybackContext.audioTracks) { track in
                                            Button(action: {
                                                switchEmbyRemoteAudioTrack(track)
                                                completeMenuInteraction()
                                            }) {
                                                let isActive = embyRemotePlaybackContext.streamSelection.audioStreamIndex == track.index
                                                Text((isActive ? "✓ " : "") + track.displayName)
                                            }
                                        }
                                    }
                                } else {
                                    ForEach(0..<playerManager.audioTrackNames.count, id: \.self) { i in
                                        Button(action: {
                                            playerManager.setAudioTrack(at: i)
                                            completeMenuInteraction()
                                        }) {
                                            let isActive = i < playerManager.audioTrackIds.count && playerManager.activeAudioId == playerManager.audioTrackIds[i]
                                            Text((isActive ? "✓ " : "") + playerManager.audioTrackNames[i])
                                        }
                                    }
                                }
                            } label: { Image(systemName: "waveform").font(.system(size: 16, weight: .semibold)).frame(width: 44, height: 38).background(Color.black.opacity(0.75)).foregroundColor(.white).cornerRadius(8) }.help("音轨").menuStyle(.borderlessButton).fixedSize().colorScheme(.dark).simultaneousGesture(TapGesture().onEnded { beginMenuInteraction() })

                            // 🌟 字幕菜单 (含本地外挂及时间轴控制)
                            Menu {
                                if let embyRemotePlaybackContext {
                                    Button(action: {
                                        switchEmbyRemoteSubtitleTrack(nil)
                                        completeMenuInteraction()
                                    }) {
                                        let isActive = embyRemotePlaybackContext.streamSelection.subtitleStreamIndex == nil
                                        Text((isActive ? "✓ " : "") + "关闭字幕")
                                    }
                                    ForEach(embyRemotePlaybackContext.subtitleTracks) { track in
                                        Button(action: {
                                            switchEmbyRemoteSubtitleTrack(track)
                                            completeMenuInteraction()
                                        }) {
                                            let isActive = embyRemotePlaybackContext.streamSelection.subtitleStreamIndex == track.index
                                            Text((isActive ? "✓ " : "") + track.displayName)
                                        }
                                    }
                                } else {
                                    ForEach(0..<playerManager.subtitleNames.count, id: \.self) { i in
                                        Button(action: {
                                            playerManager.setSubtitleTrack(at: i)
                                            completeMenuInteraction()
                                        }) {
                                            let isActive = i < playerManager.subtitleIds.count && playerManager.activeSubtitleId == playerManager.subtitleIds[i]
                                            Text((isActive ? "✓ " : "") + playerManager.subtitleNames[i])
                                        }
                                    }
                                }

                                Divider() // 分割线

                                // 🌟 1. 加载本地外挂字幕
                                Button(action: {
                                    loadExternalSubtitle()
                                    completeMenuInteraction()
                                }) {
                                    HStack {
                                        Image(systemName: "doc.badge.plus")
                                        Text("加载外置字幕...")
                                    }
                                }

                                Divider() // 分割线

                                Menu("字幕大小") {
                                    Button(action: {
                                        setSubtitleSize(12)
                                        completeMenuInteraction()
                                    }) {
                                        Text((playerManager.currentSubtitleSize == 12 ? "✓ " : "") + "小号字体 (12)")
                                    }
                                    Button(action: {
                                        setSubtitleSize(16)
                                        completeMenuInteraction()
                                    }) {
                                        Text((playerManager.currentSubtitleSize == 16 ? "✓ " : "") + "标准字体 (16)")
                                    }
                                    Button(action: {
                                        setSubtitleSize(20)
                                        completeMenuInteraction()
                                    }) {
                                        Text((playerManager.currentSubtitleSize == 20 ? "✓ " : "") + "大号字体 (20)")
                                    }
                                    Button(action: {
                                        setSubtitleSize(24)
                                        completeMenuInteraction()
                                    }) {
                                        Text((playerManager.currentSubtitleSize == 24 ? "✓ " : "") + "特大字体 (24)")
                                    }
                                }

                                Divider() // 分割线

                                // 🌟 2. 时间轴同步手动控制 (G/H 国际标快捷键)
                                VStack(alignment: .leading, spacing: 6) {
                                    Text("字幕同步 (G 早 / H 晚)").font(.caption2).foregroundColor(.gray)
                                    HStack(spacing: 8) {
                                        Button(action: {
                                            playerManager.adjustSubtitleDelay(by: -0.5)
                                            completeMenuInteraction()
                                        }) {
                                            Image(systemName: "minus.circle")
                                        }.help("字幕调早 0.5 秒")

                                        Text(String(format: "同步 %.1fs", playerManager.subtitleDelay))
                                            .font(.system(size: 13, weight: .bold))
                                            .foregroundColor(.white)
                                            .frame(width: 55, alignment: .center)

                                        Button(action: {
                                            playerManager.adjustSubtitleDelay(by: 0.5)
                                            completeMenuInteraction()
                                        }) {
                                            Image(systemName: "plus.circle")
                                        }.help("字幕调晚 0.5 秒")
                                    }
                                    .padding(.horizontal, 10).padding(.vertical, 6)
                                    .background(Color.white.opacity(0.1)).cornerRadius(6)
                                }.padding(.horizontal, 10)

                            } label: { Image(systemName: "captions.bubble").font(.system(size: 16, weight: .semibold)).frame(width: 44, height: 38).background(Color.black.opacity(0.75)).foregroundColor(.white).cornerRadius(8) }.help("字幕").menuStyle(.borderlessButton).fixedSize().colorScheme(.dark).simultaneousGesture(TapGesture().onEnded { beginMenuInteraction() })

                        }
                    }.foregroundColor(.white).padding(.horizontal, 30).padding(.bottom, 30).padding(.top, 40).background(LinearGradient(gradient: Gradient(colors: [.clear, .black.opacity(0.9)]), startPoint: .top, endPoint: .bottom))
                }.transition(.opacity)
            }

            // 快捷键映射
            Button("") { playerManager.playOrPause(); resetHideTimer() }.keyboardShortcut(.space, modifiers: []).opacity(0)
            Button("") { playerManager.seekRelative(seconds: Double(-seekDuration)); resetHideTimer() }.keyboardShortcut(.leftArrow, modifiers: []).opacity(0)
            Button("") { playerManager.seekRelative(seconds: Double(seekDuration)); resetHideTimer() }.keyboardShortcut(.rightArrow, modifiers: []).opacity(0)
            Button("") { playerManager.adjustSubtitleDelay(by: -0.5); resetHideTimer() }.keyboardShortcut("g", modifiers: []).opacity(0)
            Button("") { playerManager.adjustSubtitleDelay(by: 0.5); resetHideTimer() }.keyboardShortcut("h", modifiers: []).opacity(0)

        }
        .toolbar(isStandaloneWindow ? .visible : .hidden, for: .windowToolbar)
        .onContinuousHover(coordinateSpace: .local) { phase in
            switch phase {
            case .active(let location):
                if !showControls {
                    withAnimation(.easeInOut(duration: 0.3)) { showControls = true }
                }
                if location.y <= topControlHoldHeight {
                    isPointerInTopControlArea = true
                    hideUITask?.cancel()
                    setCursorHidden(false)
                } else {
                    if isPointerInTopControlArea {
                        isPointerInTopControlArea = false
                    }
                    resetHideTimer()
                }
            case .ended:
                isPointerInTopControlArea = false
                resetHideTimer()
            }
        }
        .onAppear {
            log("onAppear movie=\(movie.title) initialFileId=\(initialFileId ?? "nil") seedFiles=\(initialPlaylistFiles?.count ?? 0) seedSource=\(initialSourceBasePath ?? "nil") seedProtocol=\(initialSourceProtocolType ?? "nil")")
            ensureComfortableWindowSize()
            prepareVideoWithPermission()
            resetHideTimer()
        }
        .task(id: videoURLs.count) {
            guard !videoURLs.isEmpty else { return }
            // Let the initial MPV load/seek settle before publishing progress;
            // otherwise a remote client could briefly overwrite a saved resume
            // position with the initial 0-second state.
            try? await Task.sleep(nanoseconds: 8_000_000_000)
            while !Task.isCancelled {
                await syncCurrentPlaybackProgressToDocker()
                try? await Task.sleep(nanoseconds: 10_000_000_000)
            }
        }
        .onDisappear {
            traceClose("onDisappear triggered")
            hideUITask?.cancel()
            menuInteractionTask?.cancel()
            setCursorHidden(false)
            persistAndStopPlaybackIfNeeded(reason: "onDisappear")
        }
        .onReceive(NotificationCenter.default.publisher(for: .standalonePlayerShouldClose)) { _ in
            guard isStandaloneWindow else { return }
            traceClose("received standalonePlayerShouldClose notification")
            persistAndStopPlaybackIfNeeded(reason: "notification")
        }
        .onChange(of: videoURLs.count) { _, newCount in
            log("videoURLs updated count=\(newCount)")
            if newCount > 0 {
                hasIssuedLoad = false
                tryStartPlaybackIfReady()
            }
        }
        .onChange(of: playerManager.drawableBindVersion) { _, _ in
            tryStartPlaybackIfReady()
        }
        .onChange(of: errorMessage) { _, newValue in
            if let newValue {
                log("errorMessage=\(newValue)")
            }
        }
        .onChange(of: showControls) { _, isVisible in setCursorHidden(!isVisible) }
        .onChange(of: playerManager.currentPlaylistIndex) { _, newIndex in
            guard allPlaylistFiles.indices.contains(newIndex) else { return }
            guard let oldId = currentVideoFileId else {
                currentVideoFileId = allPlaylistFiles[newIndex].id
                return
            }
            let newFile = allPlaylistFiles[newIndex]
            guard oldId != newFile.id else { return }
            persistFinishedPlaylistFiles(finishedPlaylistFileIds(before: newIndex, previousFileId: oldId))
            currentVideoFileId = newFile.id
        }
    }

    // ==========================================
    // 🌟 原生加载本地外挂字幕逻辑
    // ==========================================
    private func loadExternalSubtitle() {
        let wasPlaying = playerManager.isPlaying
        if wasPlaying { playerManager.playOrPause() } // 选文件前自动暂停

        let panel = NSOpenPanel()
        panel.message = "选择要挂载的字幕文件"
        // 仅允许常见的字幕格式
        panel.allowedContentTypes = [UTType(filenameExtension: "srt"), UTType(filenameExtension: "ass"), UTType(filenameExtension: "ssa"), UTType(filenameExtension: "vtt")].compactMap { $0 }
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false

        if panel.runModal() == .OK, let url = panel.url {
            let subName = url.lastPathComponent
            // 发送底层挂载命令，并强制选中这根新轨道
            playerManager.addExternalSubtitle(url: url, title: subName)
        }

        if wasPlaying { playerManager.playOrPause() } // 选完自动恢复播放
    }

    @MainActor private func switchEmbyRemoteAudioTrack(_ track: EmbyMediaStreamOption) {
        guard var context = embyRemotePlaybackContext else { return }
        let selection = EmbyStreamSelection(
            audioStreamIndex: track.index,
            subtitleStreamIndex: context.streamSelection.subtitleStreamIndex,
            subtitleMethod: context.streamSelection.subtitleMethod
        )
        guard selection != context.streamSelection else { return }
        context.streamSelection = selection
        reloadEmbyRemotePlayback(context: context)
    }

    @MainActor private func switchEmbyRemoteSubtitleTrack(_ track: EmbyMediaStreamOption?) {
        guard var context = embyRemotePlaybackContext else { return }
        let selection = EmbyStreamSelection(
            audioStreamIndex: context.streamSelection.audioStreamIndex,
            subtitleStreamIndex: track?.index,
            subtitleMethod: track?.method
        )
        guard selection != context.streamSelection else { return }
        context.streamSelection = selection
        reloadEmbyRemotePlayback(context: context)
    }

    @MainActor private func reloadEmbyRemotePlayback(context: EmbyRemotePlaybackContext) {
        guard !isSwitchingEmbyRemoteStream else { return }
        isSwitchingEmbyRemoteStream = true
        let resumePosition = max(playerManager.currentTimePos, 0)
        log("switch Jellyfin/Emby stream audio=\(context.streamSelection.audioStreamIndex.map(String.init) ?? "nil") subtitle=\(context.streamSelection.subtitleStreamIndex.map(String.init) ?? "nil") method=\(context.streamSelection.subtitleMethod ?? "nil") resume=\(resumePosition)")

        Task {
            let resolvedURL = await resolveEmbyCompatiblePlaybackURL(
                itemId: context.itemId,
                mediaSourceId: context.mediaSourceId,
                fileName: context.fileName,
                sourceBaseUrl: context.sourceBaseUrl,
                token: context.token,
                userId: context.userId,
                streamSelection: context.streamSelection
            )

            await MainActor.run {
                self.isSwitchingEmbyRemoteStream = false
                guard let resolvedURL else {
                    self.log("switch Jellyfin/Emby stream failed: no playable URL")
                    return
                }
                self.embyRemotePlaybackContext = context
                self.startPosition = resumePosition
                self.videoURLs = [resolvedURL]
                self.playerManager.loadFiles(urls: [resolvedURL], startPosition: resumePosition, isBluRay: false, blurayRootPath: nil, trackPreferenceKey: self.currentVideoFileId)
                self.resetHideTimer()
            }
        }
    }

    private func resetHideTimer() {
        hideUITask?.cancel()
        guard !isMenuInteractionActive else { return }
        guard !isPointerInTopControlArea else { return }
        hideUITask = Task {
            try? await Task.sleep(nanoseconds: 3_000_000_000)
            guard !Task.isCancelled else { return }
            await MainActor.run {
                guard !isMenuInteractionActive else { return }
                guard !isPointerInTopControlArea else { return }
                withAnimation(.easeInOut(duration: 0.5)) { showControls = false }
            }
        }
    }

    private func beginMenuInteraction() {
        hideUITask?.cancel()
        menuInteractionTask?.cancel()
        isMenuInteractionActive = true
        if !showControls {
            withAnimation(.easeInOut(duration: 0.2)) { showControls = true }
        }
        setCursorHidden(false)
        menuInteractionTask = Task {
            try? await Task.sleep(nanoseconds: 10_000_000_000)
            guard !Task.isCancelled else { return }
            await MainActor.run {
                isMenuInteractionActive = false
                resetHideTimer()
            }
        }
    }

    private func completeMenuInteraction() {
        menuInteractionTask?.cancel()
        isMenuInteractionActive = false
        resetHideTimer()
    }

    private func setCursorHidden(_ hidden: Bool) {
        if isClosingPlayback && hidden {
            return
        }
        let shouldHide = hidden && isPlayerWindowFullScreen()
        guard shouldHide != isCursorHidden else { return }
        if shouldHide { NSCursor.hide() } else { NSCursor.unhide() }
        isCursorHidden = shouldHide
    }

    private func forceCursorVisible() {
        // Defensively unwind possible nested hide calls across fullscreen transitions.
        NSCursor.setHiddenUntilMouseMoves(false)
        for _ in 0..<10 { NSCursor.unhide() }
        NSCursor.arrow.set()
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.08) {
            NSCursor.setHiddenUntilMouseMoves(false)
            for _ in 0..<10 { NSCursor.unhide() }
            NSCursor.arrow.set()
        }
        isCursorHidden = false
    }

    private func prepareVideoWithPermission() {
        let cacheDirectory = OfflineCacheManager.shared.cacheDirectory
        log("prepare start cacheDir=\(cacheDirectory?.path ?? "nil")")
        if let seedSourceBasePath = initialSourceBasePath,
           let seedFiles = initialPlaylistFiles,
           !seedFiles.isEmpty {
            let orderedSeedFiles = seedFiles.enumerated().sorted {
                MediaNameParser.playbackSortPrecedes(
                    lhsRelativePath: $0.element.relativePath,
                    lhsFileName: $0.element.fileName,
                    lhsFallbackIndex: $0.offset,
                    rhsRelativePath: $1.element.relativePath,
                    rhsFileName: $1.element.fileName,
                    rhsFallbackIndex: $1.offset
                )
            }.map(\.element)
            let startIndex = initialFileId.flatMap { targetId in
                orderedSeedFiles.firstIndex(where: { $0.id == targetId })
            } ?? 0
            let seedProtocolType = initialSourceProtocolType ?? MediaSourceProtocol.local.rawValue
            log("prepare try seed data files=\(orderedSeedFiles.count) startIndex=\(startIndex) source=\(seedSourceBasePath) protocol=\(seedProtocolType)")
            Task { @MainActor in
                if await applyPreparedPlayback(
                    files: orderedSeedFiles,
                    sourceBasePath: seedSourceBasePath,
                    sourceProtocolType: seedProtocolType,
                    sourceAuthConfig: initialSourceAuthConfig,
                    startIndex: startIndex,
                    cacheDirectory: cacheDirectory
                ) {
                    log("prepare seed data applied")
                    return
                }
                log("prepare seed data not applied, fallback DB")
                prepareVideoFromDatabase(cacheDirectory: cacheDirectory)
            }
            return
        }

        prepareVideoFromDatabase(cacheDirectory: cacheDirectory)
    }

    private func prepareVideoFromDatabase(cacheDirectory: URL?) {
        let movieValue = movie
        let initialFileIdValue = initialFileId
        guard let dbQueue = AppDatabase.shared.dbQueue else {
            self.errorMessage = "数据库尚未初始化"
            return
        }

        Task.detached(priority: .userInitiated) {
            do {
                print("[PlayerScreen] prepare DB snapshot start")
                let snapshot: (files: [VideoFile], sourceBasePath: String, sourceProtocolType: String, sourceAuthConfig: String?, startIndex: Int)? = try await dbQueue.read { db in
                    let fetchedFiles = try VideoFile.fetchVisibleFiles(movieId: movieValue.id, in: db)
                    let files = fetchedFiles.enumerated().sorted {
                        MediaNameParser.playbackSortPrecedes(
                            lhsRelativePath: $0.element.relativePath,
                            lhsFileName: $0.element.fileName,
                            lhsFallbackIndex: $0.offset,
                            rhsRelativePath: $1.element.relativePath,
                            rhsFileName: $1.element.fileName,
                            rhsFallbackIndex: $1.offset
                        )
                    }.map(\.element)
                    guard !files.isEmpty,
                          let source = try files[0].request(for: VideoFile.mediaSource).fetchOne(db) else { return nil }

                    var startIndex = 0
                    if let targetId = initialFileIdValue,
                       let idx = files.firstIndex(where: { $0.id == targetId }) {
                        startIndex = idx
                    }
                    return (
                        files: files,
                        sourceBasePath: source.baseUrl,
                        sourceProtocolType: source.protocolType,
                        sourceAuthConfig: source.authConfig,
                        startIndex: startIndex
                    )
                }
                print("[PlayerScreen] prepare DB snapshot done files=\(snapshot?.files.count ?? 0)")

                guard let snapshot else {
                    await MainActor.run { self.errorMessage = "找不到该视频的文件记录" }
                    return
                }

                let applied = await applyPreparedPlayback(
                    files: snapshot.files,
                    sourceBasePath: snapshot.sourceBasePath,
                    sourceProtocolType: snapshot.sourceProtocolType,
                    sourceAuthConfig: snapshot.sourceAuthConfig,
                    startIndex: snapshot.startIndex,
                    cacheDirectory: cacheDirectory
                )
                if !applied {
                    await MainActor.run {
                        if self.errorMessage == nil {
                            self.errorMessage = "文件不存在。请重新连接外置硬盘/NAS，或先缓存到本地后再播放。"
                        }
                    }
                } else {
                    print("[PlayerScreen] prepare DB snapshot applied")
                }
            } catch {
                await MainActor.run {
                    self.errorMessage = "数据库读取失败：\(error.localizedDescription)"
                }
            }
        }
    }

    private func closePlayer() {
        persistAndStopPlaybackIfNeeded(reason: "closePlayerButton")
        dismiss()
    }

    private func detachAttachedDiscImages() {
        let mountPaths = attachedDiscImageMountPaths
        attachedDiscImageMountPaths.removeAll()
        guard !mountPaths.isEmpty else { return }
        Task.detached(priority: .utility) {
            for mountPath in mountPaths {
                let process = Process()
                process.executableURL = URL(fileURLWithPath: "/usr/bin/hdiutil")
                process.arguments = ["detach", mountPath, "-force", "-quiet"]
                do {
                    try process.run()
                    process.waitUntilExit()
                    print("[PlayerScreen] ISO detach mount=\(mountPath) status=\(process.terminationStatus)")
                } catch {
                    print("[PlayerScreen] ISO detach failed mount=\(mountPath) error=\(error.localizedDescription)")
                }
            }
        }
    }

    private func stopRemoteISOProxyRoutes() {
        let routeIDs = remoteISOProxyRouteIDs
        remoteISOProxyRouteIDs.removeAll()
        guard !routeIDs.isEmpty else { return }
        RemoteISOStreamProxy.shared.unregister(routeIDs: routeIDs)
    }

    nonisolated private static func applyFinishedPlaybackState(
        to file: inout VideoFile,
        observedDuration: Double = 0,
        observedProgress: Double = 0
    ) {
        let resolvedDuration = max(file.duration, max(observedDuration, observedProgress))
        file.duration = resolvedDuration
        file.playProgress = resolvedDuration
        file.lastPlayedAt = nil
    }

    nonisolated private static func playbackResumeStartPosition(for file: VideoFile) -> Double {
        guard file.playProgress > 5.0 else { return 0.0 }
        guard file.duration > 0 else { return file.playProgress }
        if file.playProgress / file.duration >= 0.95 || file.duration - file.playProgress <= 5.0 {
            return 0.0
        }
        return min(file.playProgress, max(file.duration - 5.0, 0.0))
    }

    nonisolated private static func omniPlayDockerRemoteFileId(from file: VideoFile) -> String? {
        let idParts = file.id.split(separator: ":", maxSplits: 2, omittingEmptySubsequences: false).map(String.init)
        if idParts.count == 3, idParts[0] == "omniplay-docker", !idParts[2].isEmpty {
            return idParts[2].removingPercentEncoding ?? idParts[2]
        }

        let trimmed = file.relativePath.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        let parts = trimmed.split(separator: "/").map(String.init)
        guard parts.count >= 5,
              parts[0] == "api",
              parts[1] == "playback",
              parts[2] == "files",
              parts[4] == "stream" else {
            return nil
        }
        return parts[3].removingPercentEncoding ?? parts[3]
    }

    private static func syncOmniPlayDockerProgressIfNeeded(file: VideoFile, source: MediaSource?) async {
        guard source?.protocolKind == .omniplayDocker,
              let source,
              let remoteFileId = omniPlayDockerRemoteFileId(from: file) else {
            return
        }
        let config = OmniPlayDockerAuthConfig.decode(source.authConfig)
        do {
            let client = try OmniPlayDockerClient(baseURLString: source.baseUrl, sessionCookie: config?.sessionCookie)
            try await client.updateProgress(
                videoFileId: remoteFileId,
                positionSeconds: file.playProgress,
                durationSeconds: file.duration
            )
        } catch {
            print("Docker 进度同步失败：\(error.localizedDescription)")
        }
    }

    private func syncCurrentPlaybackProgressToDocker() async {
        guard playbackSourceProtocolType == MediaSourceProtocol.omniplayDocker.rawValue,
              let baseURL = playbackSourceBaseURL,
              !isClosingPlayback else {
            return
        }

        let snapshot = playerManager.playbackEngineSnapshot
        guard snapshot.timePos.isFinite, snapshot.timePos > 0.5 else { return }
        guard let fileIndex = resolvedPlaybackFileIndex(from: snapshot),
              allPlaylistFiles.indices.contains(fileIndex) else {
            return
        }

        let file = allPlaylistFiles[fileIndex]
        guard let remoteFileId = Self.omniPlayDockerRemoteFileId(from: file) else { return }
        let duration = max(
            snapshot.duration.isFinite ? snapshot.duration : 0,
            file.duration,
            playerManager.duration
        )
        guard duration > 0 else { return }

        let config = OmniPlayDockerAuthConfig.decode(playbackSourceAuthConfig)
        do {
            let client = try OmniPlayDockerClient(
                baseURLString: baseURL,
                sessionCookie: config?.sessionCookie
            )
            try await client.updateProgress(
                videoFileId: remoteFileId,
                positionSeconds: snapshot.timePos,
                durationSeconds: duration
            )
            if allPlaylistFiles.indices.contains(fileIndex) {
                allPlaylistFiles[fileIndex].playProgress = max(allPlaylistFiles[fileIndex].playProgress, snapshot.timePos)
                allPlaylistFiles[fileIndex].duration = max(allPlaylistFiles[fileIndex].duration, duration)
            }
        } catch {
            print("Docker 播放中进度同步失败：\(error.localizedDescription)")
        }
    }

    private func normalizedPlaybackFilename(_ value: String) -> String {
        let decoded = value.removingPercentEncoding ?? value
        return (decoded as NSString).lastPathComponent.lowercased()
    }

    private func playbackFilename(_ playbackFilename: String, matches file: VideoFile) -> Bool {
        let normalized = normalizedPlaybackFilename(playbackFilename)
        guard !normalized.isEmpty else { return false }

        let candidates = [
            file.fileName,
            (file.relativePath as NSString).lastPathComponent
        ]
        return candidates.contains { candidate in
            !candidate.isEmpty && normalizedPlaybackFilename(candidate) == normalized
        }
    }

    private func resolvedPlaybackFileIndex(from snapshot: MPVPlayerManager.PlaybackEngineSnapshot) -> Int? {
        if let filename = snapshot.filename,
           let filenameIndex = allPlaylistFiles.firstIndex(where: { playbackFilename(filename, matches: $0) }) {
            return filenameIndex
        }
        if allPlaylistFiles.indices.contains(snapshot.playlistIndex) {
            return snapshot.playlistIndex
        }
        if let currentVideoFileId {
            return allPlaylistFiles.firstIndex(where: { $0.id == currentVideoFileId })
        }
        return nil
    }

    private func finishedPlaylistFileIds(before currentIndex: Int, previousFileId: String?) -> [String] {
        guard allPlaylistFiles.indices.contains(currentIndex),
              let previousFileId,
              let previousIndex = allPlaylistFiles.firstIndex(where: { $0.id == previousFileId }),
              previousIndex < currentIndex else {
            return []
        }
        return Array(allPlaylistFiles[previousIndex..<currentIndex].map(\.id))
    }

    private func persistFinishedPlaylistFiles(_ fileIds: [String]) {
        var seenFileIds = Set<String>()
        let uniqueFileIds = fileIds.filter { seenFileIds.insert($0).inserted }
        guard !uniqueFileIds.isEmpty else { return }

        Task.detached {
            do {
                try await AppDatabase.shared.dbQueue.write { db in
                    for fileId in uniqueFileIds {
                        if var file = try VideoFile.fetchOne(db, key: fileId) {
                            Self.applyFinishedPlaybackState(to: &file)
                            try file.update(db)
                        }
                    }
                }
                await MainActor.run { NotificationCenter.default.post(name: .libraryUpdated, object: nil) }
            } catch {}
        }
    }

    private func persistAndStopPlaybackIfNeeded(reason: String) {
        traceClose("persistAndStop begin reason=\(reason) hasPersisted=\(hasPersistedBeforeExit)")
        guard !hasPersistedBeforeExit else { return }
        hasPersistedBeforeExit = true
        isClosingPlayback = true
        hideUITask?.cancel()
        hideUITask = nil
        showControls = true

        let playbackSnapshot = playerManager.playbackEngineSnapshot
        let resolvedPlaylistIndex = resolvedPlaybackFileIndex(from: playbackSnapshot)
        let fileIdToSave = resolvedPlaylistIndex.flatMap { allPlaylistFiles.indices.contains($0) ? allPlaylistFiles[$0].id : nil } ?? currentVideoFileId
        let completedFileIds = resolvedPlaylistIndex.map {
            finishedPlaylistFileIds(before: $0, previousFileId: currentVideoFileId)
        } ?? []
        let progressToSave = playbackSnapshot.timePos.isFinite ? playbackSnapshot.timePos : playerManager.currentTimePos
        let snapshotDuration = playbackSnapshot.duration.isFinite ? playbackSnapshot.duration : 0.0
        let durationToSave = snapshotDuration > 0 ? snapshotDuration : playerManager.duration
        let isTVPlaybackAtClose = isTVPlayback
        traceClose("snapshot fileId=\(fileIdToSave ?? "nil") playlistIndex=\(playbackSnapshot.playlistIndex) filename=\(playbackSnapshot.filename ?? "nil") progress=\(progressToSave) duration=\(durationToSave) isTV=\(isTVPlaybackAtClose)")
        forceCursorVisible()
        traceClose("calling playerManager.stop()")
        playerManager.stop()
        stopRemoteISOProxyRoutes()
        detachAttachedDiscImages()

        if let fileId = fileIdToSave {
            Task.detached {
                let formatter = ISO8601DateFormatter()
                formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
                let queue = String(cString: __dispatch_queue_get_label(nil), encoding: .utf8) ?? "unknown"
                let thread = "bg"
                print("[PlayerScreenClose][\(formatter.string(from: Date()))][\(thread)][q:\(queue)] progress save task begin fileId=\(fileId)")
                do {
                    let syncTarget = try await AppDatabase.shared.dbQueue.write { db -> (VideoFile, MediaSource?)? in
                        var fileForRemoteSync: VideoFile?
                        var sourceForRemoteSync: MediaSource?
                        for completedId in completedFileIds where completedId != fileId {
                            if var completedFile = try VideoFile.fetchOne(db, key: completedId) {
                                Self.applyFinishedPlaybackState(to: &completedFile)
                                try completedFile.update(db)
                            }
                        }
                        if var file = try VideoFile.fetchOne(db, key: fileId) {
                            let resolvedDuration = max(durationToSave, file.duration)
                            let finished = resolvedDuration > 0 && (progressToSave / resolvedDuration) >= 0.95
                            let thresholded = progressToSave > 5.0 ? progressToSave : 0.0
                            if finished {
                                Self.applyFinishedPlaybackState(
                                    to: &file,
                                    observedDuration: durationToSave,
                                    observedProgress: progressToSave
                                )
                            } else {
                                file.playProgress = thresholded
                                file.duration = resolvedDuration
                                file.lastPlayedAt = thresholded > 0.0 ? Date().timeIntervalSince1970 : nil
                            }
                            try file.update(db)
                            fileForRemoteSync = file
                            sourceForRemoteSync = try file.request(for: VideoFile.mediaSource).fetchOne(db)
                        }
                        guard let fileForRemoteSync else { return nil }
                        return (fileForRemoteSync, sourceForRemoteSync)
                    }
                    if let syncTarget {
                        await Self.syncOmniPlayDockerProgressIfNeeded(file: syncTarget.0, source: syncTarget.1)
                    }
                    await MainActor.run { NotificationCenter.default.post(name: .libraryUpdated, object: nil) }
                    let doneFormatter = ISO8601DateFormatter()
                    doneFormatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
                    let doneQueue = String(cString: __dispatch_queue_get_label(nil), encoding: .utf8) ?? "unknown"
                    let doneThread = "bg"
                    print("[PlayerScreenClose][\(doneFormatter.string(from: Date()))][\(doneThread)][q:\(doneQueue)] progress save task done fileId=\(fileId)")
                } catch {}
            }
        }
    }

    private func toggleFullScreen() {
        guard let keyWindow = NSApplication.shared.windows.first(where: { $0.isKeyWindow }) else { return }
        let targetWindow = keyWindow.sheetParent ?? keyWindow
        targetWindow.toggleFullScreen(nil)
    }
    private func setSubtitleSize(_ size: Int) { playerManager.setSubtitleSize(size); resetHideTimer() }
    private func subtitleSizeLabel(_ size: Int) -> String { switch size { case 12: return "小"; case 16: return "标准"; case 20: return "大"; case 24: return "特大"; default: return "标准" } }
    private func ensureComfortableWindowSize() {
        guard let window = NSApplication.shared.windows.first(where: { $0.isKeyWindow }) else { return }
        guard !window.styleMask.contains(.fullScreen) else { return }
        let minSize = NSSize(width: 1100, height: 680)
        window.minSize = minSize
        let current = window.frame.size
        if current.width < minSize.width || current.height < minSize.height {
            let newRect = NSRect(
                x: window.frame.origin.x,
                y: window.frame.origin.y,
                width: max(current.width, minSize.width),
                height: max(current.height, minSize.height)
            )
            // Avoid NSWindowTransformAnimation teardown crashes on macOS 26.x.
            window.setFrame(newRect, display: true, animate: false)
        }
    }
    private func isPlayerWindowFullScreen() -> Bool {
        guard let keyWindow = NSApplication.shared.windows.first(where: { $0.isKeyWindow }) else { return false }
        let targetWindow = keyWindow.sheetParent ?? keyWindow
        return targetWindow.styleMask.contains(.fullScreen)
    }

    @MainActor private func applyPreparedPlayback(
        files: [VideoFile],
        sourceBasePath: String,
        sourceProtocolType: String,
        sourceAuthConfig: String?,
        startIndex: Int,
        cacheDirectory: URL?
    ) async -> Bool {
        log("applyPreparedPlayback files=\(files.count) startIndex=\(startIndex) source=\(sourceBasePath) protocol=\(sourceProtocolType)")
        guard !files.isEmpty else { return false }
        guard startIndex >= 0 && startIndex < files.count else { return false }

        let sourceKind = MediaSourceProtocol(rawValue: sourceProtocolType) ?? .local
        let normalizedBase = sourceKind.normalizedBaseURL(sourceBasePath)
        let sourceBaseUrl: URL
        let webDAVCredential = sourceKind == .webdav
            ? resolveWebDAVCredential(authConfig: sourceAuthConfig, baseURLString: normalizedBase)
            : nil
        let mediaServerCredential = (sourceKind == .plex || sourceKind == .emby || sourceKind == .jellyfin)
            ? MediaServerAuthConfig.decode(sourceAuthConfig)
            : nil
        let omniPlayDockerAuth = sourceKind == .omniplayDocker
            ? OmniPlayDockerAuthConfig.decode(sourceAuthConfig)
            : nil
        switch sourceKind {
        case .webdav, .plex, .emby, .jellyfin, .omniplayDocker:
            guard let remoteBase = URL(string: normalizedBase) else { return false }
            sourceBaseUrl = remoteBase
        case .local, .direct:
            sourceBaseUrl = URL(fileURLWithPath: normalizedBase)
        }

        var remotePlaybackContexts: [String: EmbyRemotePlaybackContext] = [:]

        func sanitizedWebDAVMountURL(_ url: URL) -> URL {
            guard var components = URLComponents(url: url, resolvingAgainstBaseURL: false) else { return url }
            components.user = nil
            components.password = nil
            return components.url ?? url
        }

        func stableMountKey(for url: URL) -> String {
            let raw = url.absoluteString.precomposedStringWithCanonicalMapping.lowercased()
            var hash: UInt64 = 1469598103934665603
            for byte in raw.utf8 {
                hash ^= UInt64(byte)
                hash = hash &* 1099511628211
            }
            return String(format: "%016llx", hash)
        }

        func mountPathComponents(from path: String) -> [String] {
            path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
                .split(separator: "/")
                .map(String.init)
        }

        func mountedLocalURL(pathComponents: [String], mountRoot: URL) -> URL {
            var localURL = mountRoot
            for component in pathComponents {
                localURL.appendPathComponent(component)
            }
            return localURL
        }

        func webDAVOriginURL(from url: URL) -> URL? {
            guard var components = URLComponents(url: url, resolvingAgainstBaseURL: false),
                  components.scheme != nil,
                  components.host != nil else {
                return nil
            }
            components.user = nil
            components.password = nil
            components.path = ""
            components.query = nil
            components.fragment = nil
            return components.url
        }

        func webDAVParentURL(from url: URL) -> URL? {
            guard var components = URLComponents(url: url, resolvingAgainstBaseURL: false) else {
                return nil
            }
            components.user = nil
            components.password = nil
            components.query = nil
            components.fragment = nil
            let parentPath = (components.path as NSString).deletingLastPathComponent
            components.path = parentPath.isEmpty ? "/" : parentPath
            return components.url
        }

        func webDAVISOMountCandidates(for file: VideoFile, remoteURL: URL) -> [(label: String, mountURL: URL, relativeComponents: [String])] {
            let fileRelativePath = file.relativePath.isEmpty ? file.fileName : file.relativePath
            let fileComponents = mountPathComponents(from: fileRelativePath)
            let sanitizedBaseURL = sanitizedWebDAVMountURL(sourceBaseUrl)
            var candidates: [(label: String, mountURL: URL, relativeComponents: [String])] = [
                ("library", sanitizedBaseURL, fileComponents)
            ]

            if let parentURL = webDAVParentURL(from: remoteURL) {
                let fileName = remoteURL.lastPathComponent.removingPercentEncoding ?? remoteURL.lastPathComponent
                candidates.append(("parent", parentURL, [fileName]))
            }

            if let originURL = webDAVOriginURL(from: sourceBaseUrl) {
                let sourcePathComponents = mountPathComponents(from: sourceBaseUrl.path.removingPercentEncoding ?? sourceBaseUrl.path)
                candidates.append(("origin", originURL, sourcePathComponents + fileComponents))
            }

            var seen = Set<String>()
            return candidates.filter { candidate in
                let key = candidate.mountURL.absoluteString + "|" + candidate.relativeComponents.joined(separator: "/")
                guard !seen.contains(key) else { return false }
                seen.insert(key)
                return !candidate.relativeComponents.isEmpty
            }
        }

        func webDAVMountedISOPlaybackURL(for file: VideoFile, remoteURL: URL) async -> URL? {
            guard sourceKind == .webdav,
                  !remoteURL.isFileURL,
                  isISOFile(file) else {
                return nil
            }

            let credential = webDAVCredential
            for candidate in webDAVISOMountCandidates(for: file, remoteURL: remoteURL) {
                let mountRoot = URL(fileURLWithPath: NSHomeDirectory())
                    .appendingPathComponent("Library/Caches/OmniPlay/WebDAVMounts")
                    .appendingPathComponent(stableMountKey(for: candidate.mountURL), isDirectory: true)
                let localURL = mountedLocalURL(pathComponents: candidate.relativeComponents, mountRoot: mountRoot)
                if FileManager.default.fileExists(atPath: localURL.path) {
                    log("WebDAV ISO using existing mounted path=\(localURL.path) label=\(candidate.label)")
                    return localURL
                }

                log("WebDAV ISO mount start label=\(candidate.label) base=\(candidate.mountURL.absoluteString) mount=\(mountRoot.path)")
                let result = await Task.detached(priority: .userInitiated) { () -> (status: Int, mountPoints: [String], error: String?) in
                    do {
                        try FileManager.default.createDirectory(at: mountRoot, withIntermediateDirectories: true)

                        let openOptions = NSMutableDictionary()
                        openOptions[kNAUIOptionKey] = kNAUIOptionNoUI
                        if credential != nil {
                            openOptions[kNetFSUseAuthenticationInfoKey] = true
                        }

                        let mountOptions = NSMutableDictionary()
                        mountOptions[kNetFSMountAtMountDirKey] = true
                        mountOptions[kNetFSMountFlagsKey] = NSNumber(value: MNT_RDONLY)
                        mountOptions[kNetFSSoftMountKey] = true

                        var mountPoints: Unmanaged<CFArray>?
                        let status = NetFSMountURLSync(
                            candidate.mountURL as CFURL,
                            mountRoot as CFURL,
                            credential?.username as CFString?,
                            credential?.password as CFString?,
                            openOptions,
                            mountOptions,
                            &mountPoints
                        )
                        let points = (mountPoints?.takeRetainedValue() as? [String]) ?? []
                        return (Int(status), points, nil)
                    } catch {
                        return (Int.max, [], error.localizedDescription)
                    }
                }.value

                if FileManager.default.fileExists(atPath: localURL.path) {
                    log("WebDAV ISO mount ready label=\(candidate.label) status=\(result.status) path=\(localURL.path)")
                    return localURL
                }

                log("WebDAV ISO mount failed label=\(candidate.label) status=\(result.status) points=\(result.mountPoints.joined(separator: ",")) error=\(result.error ?? "nil")")
            }

            return nil
        }

        func attachISOImageIfNeeded(_ isoURL: URL) async -> String? {
            guard isoURL.isFileURL,
                  isoURL.pathExtension.lowercased() == "iso",
                  FileManager.default.fileExists(atPath: isoURL.path) else {
                return nil
            }

            log("ISO attach start path=\(isoURL.path)")
            let result = await Task.detached(priority: .userInitiated) { () -> (mountPoint: String?, error: String?) in
                let process = Process()
                process.executableURL = URL(fileURLWithPath: "/usr/bin/hdiutil")
                process.arguments = [
                    "attach",
                    "-readonly",
                    "-nobrowse",
                    "-noverify",
                    "-noautoopen",
                    "-plist",
                    isoURL.path
                ]

                let output = Pipe()
                let errorOutput = Pipe()
                process.standardOutput = output
                process.standardError = errorOutput

                do {
                    try process.run()
                    process.waitUntilExit()
                    let data = output.fileHandleForReading.readDataToEndOfFile()
                    let errorData = errorOutput.fileHandleForReading.readDataToEndOfFile()
                    guard process.terminationStatus == 0 else {
                        let message = String(data: errorData, encoding: .utf8) ?? "hdiutil attach failed"
                        return (nil, message.trimmingCharacters(in: .whitespacesAndNewlines))
                    }

                    guard let plist = try PropertyListSerialization.propertyList(from: data, options: [], format: nil) as? [String: Any],
                          let entities = plist["system-entities"] as? [[String: Any]] else {
                        return (nil, "hdiutil attach output parse failed")
                    }

                    let mountPoints = entities.compactMap { $0["mount-point"] as? String }
                    if let bluRayMount = mountPoints.first(where: {
                        FileManager.default.fileExists(atPath: URL(fileURLWithPath: $0).appendingPathComponent("BDMV").path)
                    }) {
                        return (bluRayMount, nil)
                    }
                    return (mountPoints.first, nil)
                } catch {
                    return (nil, error.localizedDescription)
                }
            }.value

            if let mountPoint = result.mountPoint {
                if !attachedDiscImageMountPaths.contains(mountPoint) {
                    attachedDiscImageMountPaths.append(mountPoint)
                }
                log("ISO attach ready mount=\(mountPoint)")
                return mountPoint
            }

            log("ISO attach failed path=\(isoURL.path) error=\(result.error ?? "nil")")
            return nil
        }

        func remoteISOProxyPlaybackRegistration(for file: VideoFile, remoteURL: URL) async -> RemoteISOProxyRegistration? {
            guard sourceKind == .webdav || sourceKind == .omniplayDocker,
                  !remoteURL.isFileURL,
                  isISOFile(file) else {
                return nil
            }

            let readerCredential = sourceKind == .webdav ? webDAVCredential : nil
            let reader = RemoteISOHTTPByteReader(url: remoteURL, credential: readerCredential)
            do {
                let isoSize = try await reader.contentLength(hint: file.fileSize)
                let streamFiles = try await RemoteISOImageParser(reader: reader, isoSize: isoSize).bluRayStreamFiles()
                let includeExtras = UserDefaults.standard.bool(forKey: "playBluRayExtras")
                let candidates = streamFiles.map {
                    MediaNameParser.BluRayStreamCandidate(
                        fileName: $0.fileName,
                        fileSize: $0.size,
                        duration: 0
                    )
                }
                let selectedIndexes = MediaNameParser.selectedBluRayStreamIndices(
                    from: candidates,
                    includeExtras: includeExtras
                )
                let selectedFiles = selectedIndexes.map { streamFiles[$0] }.sorted { lhs, rhs in
                    let lhsKey = MediaNameParser.bluRayStreamPlaybackSortKey(for: lhs.fileName)
                    let rhsKey = MediaNameParser.bluRayStreamPlaybackSortKey(for: rhs.fileName)
                    if lhsKey.number != rhsKey.number { return lhsKey.number < rhsKey.number }
                    return lhsKey.name < rhsKey.name
                }
                guard !selectedFiles.isEmpty else { throw RemoteISOPlaybackError.noPlayableStreams }

                let registration = try RemoteISOStreamProxy.shared.register(files: selectedFiles, reader: reader)
                remoteISOProxyRouteIDs.append(contentsOf: registration.routeIDs)
                let names = selectedFiles.map { "\($0.fileName):\($0.size)" }.joined(separator: ",")
                log("Remote ISO proxy ready source=\(sourceKind.rawValue) isoSize=\(isoSize) streams=\(streamFiles.count) selected=\(selectedFiles.count) includeExtras=\(includeExtras) files=\(names)")
                return registration
            } catch {
                log("Remote ISO proxy failed source=\(sourceKind.rawValue) error=\(error)")
                return nil
            }
        }

        func bdmvRootKey(for file: VideoFile) -> String? {
            let relativePath = file.relativePath.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
            let components = relativePath.split(separator: "/").map(String.init)
            guard let bdmvIndex = components.firstIndex(where: { $0.caseInsensitiveCompare("BDMV") == .orderedSame }),
                  bdmvIndex > 0 else {
                return nil
            }
            return components[..<bdmvIndex]
                .joined(separator: "/")
                .precomposedStringWithCanonicalMapping
                .lowercased()
        }

        func isBDMVStreamFile(_ file: VideoFile) -> Bool {
            let relativePath = file.relativePath.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
            let components = relativePath.split(separator: "/").map(String.init)
            guard let bdmvIndex = components.firstIndex(where: { $0.caseInsensitiveCompare("BDMV") == .orderedSame }),
                  components.indices.contains(bdmvIndex + 1),
                  components[bdmvIndex + 1].caseInsensitiveCompare("STREAM") == .orderedSame else {
                return false
            }
            let fileName = file.fileName.isEmpty ? (file.relativePath as NSString).lastPathComponent : file.fileName
            let ext = (fileName as NSString).pathExtension.lowercased()
            return ext == "m2ts" || ext == "m2t" || ext == "ts"
        }

        func normalizedBDMVPlaybackFiles(from sourceFiles: [VideoFile], requestedIndex: Int) -> (files: [VideoFile], startIndex: Int)? {
            guard sourceFiles.indices.contains(requestedIndex),
                  let rootKey = bdmvRootKey(for: sourceFiles[requestedIndex]) else {
                return nil
            }
            let includeExtras = UserDefaults.standard.bool(forKey: "playBluRayExtras")
            let groupFiles = sourceFiles.filter { bdmvRootKey(for: $0) == rootKey }
            let streamFiles = groupFiles.filter(isBDMVStreamFile)
            guard !streamFiles.isEmpty else { return nil }

            let candidates = streamFiles.map {
                MediaNameParser.BluRayStreamCandidate(
                    fileName: $0.fileName,
                    fileSize: $0.fileSize,
                    duration: $0.duration
                )
            }
            let selectedIndexes = MediaNameParser.selectedBluRayStreamIndices(
                from: candidates,
                includeExtras: includeExtras
            )
            let selectedFiles = selectedIndexes.map { streamFiles[$0] }

            let orderedFiles = selectedFiles.sorted { lhs, rhs in
                let lhsKey = MediaNameParser.bluRayStreamPlaybackSortKey(for: lhs.fileName)
                let rhsKey = MediaNameParser.bluRayStreamPlaybackSortKey(for: rhs.fileName)
                if lhsKey.number != rhsKey.number { return lhsKey.number < rhsKey.number }
                return lhsKey.name < rhsKey.name
            }
            guard !orderedFiles.isEmpty else { return nil }

            let requestedId = sourceFiles[requestedIndex].id
            let resolvedStartIndex: Int
            if includeExtras, let requestedStreamIndex = orderedFiles.firstIndex(where: { $0.id == requestedId }) {
                resolvedStartIndex = requestedStreamIndex
            } else {
                resolvedStartIndex = 0
            }

            log("BDMV playback normalized root=\(rootKey) input=\(sourceFiles.count) streams=\(streamFiles.count) selected=\(orderedFiles.count) startIndex=\(resolvedStartIndex) includeExtras=\(includeExtras)")
            return (orderedFiles, resolvedStartIndex)
        }

        let playbackSelection = normalizedBDMVPlaybackFiles(from: files, requestedIndex: startIndex)
        let playbackFiles = playbackSelection?.files ?? files
        let playbackStartIndex = playbackSelection?.startIndex ?? startIndex
        let targetFile = playbackFiles[playbackStartIndex]
        let isDirectOpenFile = targetFile.mediaType == "direct"
        let remainingFiles = Array(playbackFiles[playbackStartIndex...])

        func sourcePlaybackURL(for file: VideoFile) async -> URL? {
            switch sourceKind {
            case .webdav:
                let relativePath = file.relativePath.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
                var composed = sourceBaseUrl
                if !relativePath.isEmpty {
                    for component in relativePath.split(separator: "/") {
                        composed.appendPathComponent(String(component))
                    }
                }
                if let credential = webDAVCredential,
                   var components = URLComponents(url: composed, resolvingAgainstBaseURL: false) {
                    components.user = credential.username
                    components.password = credential.password
                    return components.url
                }
                return composed
            case .plex, .emby, .jellyfin:
                let discIdentity = embyCompatibleDiscIdentity(for: file, sourceKind: sourceKind)
                let streamOptions: EmbyStreamOptions?
                if let discIdentity {
                    streamOptions = await resolveEmbyCompatibleStreamOptions(
                        itemId: discIdentity.itemId,
                        mediaSourceId: discIdentity.mediaSourceId,
                        sourceBaseUrl: sourceBaseUrl,
                        token: mediaServerCredential?.token,
                        userId: mediaServerCredential?.userId
                    )
                } else {
                    streamOptions = nil
                }
                let streamSelection = streamOptions?.selection
                if let discIdentity, let streamOptions {
                    remotePlaybackContexts[file.id] = EmbyRemotePlaybackContext(
                        itemId: discIdentity.itemId,
                        mediaSourceId: discIdentity.mediaSourceId,
                        fileName: file.fileName,
                        sourceBaseUrl: sourceBaseUrl,
                        token: mediaServerCredential?.token,
                        userId: mediaServerCredential?.userId,
                        audioTracks: streamOptions.audioTracks,
                        subtitleTracks: streamOptions.subtitleTracks,
                        streamSelection: streamOptions.selection
                    )
                }
                if let discIdentity,
                   let resolved = await resolveEmbyCompatiblePlaybackURL(
                    itemId: discIdentity.itemId,
                    mediaSourceId: discIdentity.mediaSourceId,
                    fileName: file.fileName,
                    sourceBaseUrl: sourceBaseUrl,
                    token: mediaServerCredential?.token,
                    userId: mediaServerCredential?.userId,
                    streamSelection: streamSelection
                   ) {
                    return resolved
                }
                guard var components = URLComponents(url: sourceBaseUrl, resolvingAgainstBaseURL: false) else { return nil }
                let basePath = components.path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
                let relative = mediaServerPlaybackRelativePath(for: file, sourceKind: sourceKind, streamSelection: streamSelection)
                let split = splitRelativePathAndQuery(relative)
                let joinedPath = [basePath, split.path]
                    .filter { !$0.isEmpty }
                    .joined(separator: "/")
                components.path = "/" + joinedPath
                var items = (components.queryItems ?? []) + split.queryItems
                if sourceKind == .plex,
                   !items.contains(where: { $0.name.caseInsensitiveCompare("download") == .orderedSame }) {
                    items.append(URLQueryItem(name: "download", value: "1"))
                }
                if let token = mediaServerCredential?.token.trimmingCharacters(in: .whitespacesAndNewlines), !token.isEmpty {
                    items.append(URLQueryItem(name: sourceKind == .plex ? "X-Plex-Token" : "api_key", value: token))
                }
                components.queryItems = items.isEmpty ? nil : items
                return components.url
            case .omniplayDocker:
                guard let remoteId = Self.omniPlayDockerRemoteFileId(from: file),
                      let client = try? OmniPlayDockerClient(
                        baseURLString: normalizedBase,
                        sessionCookie: omniPlayDockerAuth?.sessionCookie
                      ) else {
                    return nil
                }
                return try? await client.playbackURL(videoFileId: remoteId)
            case .local, .direct:
                let relativePath = file.relativePath.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
                return sourceBaseUrl.appendingPathComponent(relativePath)
            }
        }

        func embyCompatibleDiscIdentity(for file: VideoFile, sourceKind: MediaSourceProtocol) -> (itemId: String, mediaSourceId: String)? {
            guard sourceKind == .emby || sourceKind == .jellyfin else { return nil }
            guard isEmbyCompatibleDiscImageOrFolder(fileName: file.fileName) else { return nil }
            let split = splitRelativePathAndQuery(file.relativePath.trimmingCharacters(in: CharacterSet(charactersIn: "/")))
            let parts = split.path.split(separator: "/").map(String.init)
            guard parts.count >= 2,
                  parts[0].caseInsensitiveCompare("Items") == .orderedSame ||
                  parts[0].caseInsensitiveCompare("Videos") == .orderedSame else {
                return nil
            }
            let itemId = parts[1]
            let mediaSourceId = split.queryItems.first(where: { $0.name.caseInsensitiveCompare("MediaSourceId") == .orderedSame })?.value
                ?? split.queryItems.first(where: { $0.name.caseInsensitiveCompare("mediaSourceId") == .orderedSame })?.value
                ?? itemId
            return (itemId, mediaSourceId)
        }

        func mediaServerPlaybackRelativePath(for file: VideoFile, sourceKind: MediaSourceProtocol, streamSelection: EmbyStreamSelection?) -> String {
            let relativePath = file.relativePath.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
            guard (sourceKind == .emby || sourceKind == .jellyfin),
                  isEmbyCompatibleDiscImageOrFolder(fileName: file.fileName) else {
                return relativePath
            }
            let split = splitRelativePathAndQuery(relativePath)
            let pathOnly = split.path
            let parts = pathOnly.split(separator: "/").map(String.init)
            guard parts.count >= 3,
                  (parts[0].caseInsensitiveCompare("Items") == .orderedSame
                   || parts[0].caseInsensitiveCompare("Videos") == .orderedSame) else {
                return relativePath
            }
            let itemId = parts[1]
            guard (parts[0].caseInsensitiveCompare("Items") == .orderedSame && parts[2].caseInsensitiveCompare("Download") == .orderedSame)
                    || (parts[0].caseInsensitiveCompare("Videos") == .orderedSame && parts[2].caseInsensitiveCompare("master.m3u8") == .orderedSame) else {
                return relativePath
            }
            let mediaSourceId = split.queryItems.first(where: { $0.name.caseInsensitiveCompare("mediaSourceId") == .orderedSame })?.value ?? itemId
            let playSessionId = "omniplay\(itemId.filter { $0.isLetter || $0.isNumber })"
            return "Videos/\(itemId)/master.m3u8?\(embyCompatibleHlsQuery(mediaSourceId: mediaSourceId, playSessionId: playSessionId, deviceId: "omniplay-mac", fileName: file.fileName, streamSelection: streamSelection))"
        }

        func embyCompatibleHlsQuery(mediaSourceId: String, playSessionId: String, deviceId: String, fileName: String, streamSelection: EmbyStreamSelection?) -> String {
            let quality = embyCompatibleHlsQuality(for: fileName)
            var items = [
                "MediaSourceId=\(mediaSourceId)",
                "PlaySessionId=\(playSessionId)",
                "DeviceId=\(deviceId)",
                "EnableAutoStreamCopy=true",
                "AllowVideoStreamCopy=true",
                "AllowAudioStreamCopy=true",
                "EnableAdaptiveBitrateStreaming=false",
                "VideoCodec=h264,hevc",
                "AudioCodec=aac,ac3,eac3,dts,flac,truehd,mp3,opus,vorbis",
                "SegmentContainer=ts",
                "SegmentLength=6",
                "MinSegments=1",
                "VideoBitRate=\(quality.videoBitRate)",
                "MaxStreamingBitrate=\(quality.maxStreamingBitrate)",
                "AudioBitRate=640000",
                "MaxWidth=\(quality.maxWidth)",
                "MaxHeight=\(quality.maxHeight)",
                "Profile=high",
                "Level=51",
                "RequireAvc=false",
                "TranscodingMaxAudioChannels=6",
                "BreakOnNonKeyFrames=false",
                "CopyTimestamps=true",
                "Context=Streaming"
            ]
            if let audioStreamIndex = streamSelection?.audioStreamIndex {
                items.append("AudioStreamIndex=\(audioStreamIndex)")
            }
            if let subtitleStreamIndex = streamSelection?.subtitleStreamIndex {
                items.append("SubtitleStreamIndex=\(subtitleStreamIndex)")
                if let subtitleMethod = streamSelection?.subtitleMethod {
                    items.append("SubtitleMethod=\(subtitleMethod)")
                    if subtitleMethod == "Encode" {
                        items.append("AlwaysBurnInSubtitleWhenTranscoding=true")
                    }
                }
            } else {
                items.append("SubtitleStreamIndex=-1")
            }
            return items.joined(separator: "&")
        }

        func embyCompatibleHlsQuality(for fileName: String) -> (videoBitRate: Int, maxStreamingBitrate: Int, maxWidth: Int, maxHeight: Int) {
            let lower = fileName.lowercased()
            if lower.contains("2160p") || lower.contains("uhd") || lower.contains("4k") {
                return (60_000_000, 70_000_000, 3840, 2160)
            }
            return (35_000_000, 45_000_000, 1920, 1080)
        }

        func isEmbyCompatibleDiscImageOrFolder(fileName: String) -> Bool {
            let lower = fileName.lowercased()
            let fileExtension = (fileName as NSString).pathExtension.lowercased()
            if fileExtension == "iso" { return true }
            let mediaFileExtensions: Set<String> = ["mp4", "mkv", "mov", "avi", "rmvb", "flv", "webm", "m2ts", "m2t", "ts", "m4v", "wmv"]
            if mediaFileExtensions.contains(fileExtension) { return false }
            return lower.contains("bdmv")
                || lower.contains("blu-ray")
                || lower.contains("bluray")
                || lower.contains("uhd")
        }

        func isISOFile(_ file: VideoFile) -> Bool {
            [file.fileName, file.relativePath].contains { value in
                let path = value.split(separator: "?", maxSplits: 1, omittingEmptySubsequences: false).first.map(String.init) ?? value
                return path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
                    .lowercased()
                    .hasSuffix(".iso")
            }
        }

        func splitRelativePathAndQuery(_ value: String) -> (path: String, queryItems: [URLQueryItem]) {
            guard let separator = value.firstIndex(of: "?") else {
                return (value.trimmingCharacters(in: CharacterSet(charactersIn: "/")), [])
            }
            let path = String(value[..<separator]).trimmingCharacters(in: CharacterSet(charactersIn: "/"))
            let query = String(value[value.index(after: separator)...])
            let items = URLComponents(string: "http://omniplay.local?\(query)")?.queryItems ?? []
            return (path, items)
        }

        func localPlaybackURL(for file: VideoFile) -> URL? {
            guard let cacheDirectory else { return nil }
            if (sourceKind == .emby || sourceKind == .jellyfin),
               (isEmbyCompatibleDiscImageOrFolder(fileName: file.fileName)
                || file.relativePath.lowercased().contains("master.m3u8")) {
                return nil
            }
            let normalizedPath = file.relativePath.isEmpty
                ? file.fileName
                : file.relativePath.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
            let preferredURL = cacheDirectory
                .appendingPathComponent(String(file.sourceId))
                .appendingPathComponent(normalizedPath)
            if FileManager.default.fileExists(atPath: preferredURL.path) {
                return preferredURL
            }
            let legacyURL = cacheDirectory.appendingPathComponent(file.fileName)
            if FileManager.default.fileExists(atPath: legacyURL.path) {
                return legacyURL
            }
            if let mirroredISOURL = mirroredISOPlaybackURL(for: file, under: cacheDirectory, sourceKind: sourceKind) {
                return mirroredISOURL
            }
            return nil
        }

        func mirroredISOPlaybackURL(for file: VideoFile, under rootURL: URL, sourceKind: MediaSourceProtocol) -> URL? {
            guard sourceKind == .plex else { return nil }
            guard file.fileName.lowercased().hasSuffix(".iso") else { return nil }
            guard FileManager.default.fileExists(atPath: rootURL.path) else { return nil }

            let targetName = normalizedFileNameKey(file.fileName)
            guard let enumerator = FileManager.default.enumerator(
                at: rootURL,
                includingPropertiesForKeys: [.isRegularFileKey],
                options: [.skipsHiddenFiles, .skipsPackageDescendants]
            ) else {
                return nil
            }

            for case let candidateURL as URL in enumerator {
                guard candidateURL.pathExtension.lowercased() == "iso" else { continue }
                if normalizedFileNameKey(candidateURL.lastPathComponent) == targetName {
                    return candidateURL
                }
            }
            return nil
        }

        func normalizedFileNameKey(_ value: String) -> String {
            value.precomposedStringWithCanonicalMapping.lowercased()
        }

        guard let originalTargetSourceURL = await sourcePlaybackURL(for: targetFile) else { return false }
        let targetLocalURL = localPlaybackURL(for: targetFile)
        let requiresRemoteISO = targetLocalURL == nil
            && (sourceKind == .webdav || sourceKind == .omniplayDocker)
            && !originalTargetSourceURL.isFileURL
            && isISOFile(targetFile)
        let remoteISOProxyRegistration = requiresRemoteISO
            ? await remoteISOProxyPlaybackRegistration(for: targetFile, remoteURL: originalTargetSourceURL)
            : nil
        let mountedISOPlaybackURL = sourceKind == .webdav
            && requiresRemoteISO
            && remoteISOProxyRegistration == nil
            ? await webDAVMountedISOPlaybackURL(for: targetFile, remoteURL: originalTargetSourceURL)
            : nil
        if requiresRemoteISO && remoteISOProxyRegistration == nil && mountedISOPlaybackURL == nil {
            if sourceKind == .omniplayDocker {
                errorMessage = "无法播放 OmniPlay Docker ISO：客户端 Range 代理失败，请确认服务端支持 Range 请求。"
            } else {
                errorMessage = "无法播放 WebDAV ISO：远程 Range 解析失败，系统挂载也失败。请确认服务器支持 Range 请求，或使用 SMB/NFS 源播放 ISO。"
            }
            return false
        }
        let targetSourceURL = remoteISOProxyRegistration?.urls.first ?? mountedISOPlaybackURL ?? originalTargetSourceURL
        let targetExists: Bool = {
            if isDirectOpenFile { return true }
            if targetLocalURL != nil { return true }
            if remoteISOProxyRegistration != nil { return true }
            if let mountedISOPlaybackURL {
                return FileManager.default.fileExists(atPath: mountedISOPlaybackURL.path)
            }
            switch sourceKind {
            case .local:
                return FileManager.default.fileExists(atPath: targetSourceURL.path)
            case .webdav, .direct, .plex, .emby, .jellyfin, .omniplayDocker:
                return true
            }
        }()
        log("applyPreparedPlayback targetFile=\(targetFile.fileName) direct=\(isDirectOpenFile) targetLocal=\(targetLocalURL != nil) targetURL=\(targetSourceURL.path)")
        guard targetExists else { return false }

        var urls: [URL] = []
        var localCacheAccessCount = 0
        if let proxyURLs = remoteISOProxyRegistration?.urls {
            urls = proxyURLs
        } else {
            for file in remainingFiles {
                if let localPlaybackURL = localPlaybackURL(for: file) {
                    urls.append(localPlaybackURL)
                    localCacheAccessCount += 1
                } else if file.id == targetFile.id {
                    urls.append(targetSourceURL)
                } else {
                    if let nasURL = await sourcePlaybackURL(for: file),
                       (isDirectOpenFile || !nasURL.absoluteString.isEmpty) {
                        urls.append(nasURL)
                    }
                }
            }
        }
        guard !urls.isEmpty else { return false }
        log("applyPreparedPlayback urlsPrepared=\(urls.count) localCacheCount=\(localCacheAccessCount)")
        let firstPlaybackURL = urls.first ?? targetSourceURL
        let attachedISOBlurayRoot = firstPlaybackURL.isFileURL && firstPlaybackURL.pathExtension.lowercased() == "iso"
            ? await attachISOImageIfNeeded(firstPlaybackURL)
            : nil

        var isBDMV = false
        var bdRoot: String? = nil
        if localCacheAccessCount > 0 && targetLocalURL != nil {
            let firstURL = firstPlaybackURL
            let isISOFile = firstURL.pathExtension.lowercased() == "iso"
            isBDMV = isISOFile
            bdRoot = isISOFile ? (attachedISOBlurayRoot ?? firstURL.path) : nil
        } else if sourceBaseUrl.isFileURL, targetFile.relativePath.contains("BDMV/STREAM") {
            isBDMV = true
            let pathComps = targetFile.relativePath.components(separatedBy: "/")
            if let bdmvIdx = pathComps.firstIndex(of: "BDMV") {
                let parentComps = pathComps[0..<bdmvIdx]
                var root = sourceBaseUrl
                for comp in parentComps { root = root.appendingPathComponent(comp) }
                bdRoot = root.path
            }
        } else if targetSourceURL.isFileURL, targetSourceURL.pathExtension.lowercased() == "iso" {
            isBDMV = true
            bdRoot = attachedISOBlurayRoot ?? targetSourceURL.path
        }

        let resolvedURLs = urls
        DispatchQueue.main.async {
            self.isBluRayFolder = isBDMV
            self.blurayRootPath = bdRoot
            self.allPlaylistFiles = remainingFiles
            self.currentVideoFileId = targetFile.id
            self.startPosition = Self.playbackResumeStartPosition(for: targetFile)
            self.playbackSourceBaseURL = sourceBasePath
            self.playbackSourceProtocolType = sourceProtocolType
            self.playbackSourceAuthConfig = sourceAuthConfig
            self.rootFolderURL = sourceBaseUrl
            self.videoURLs = resolvedURLs
            self.embyRemotePlaybackContext = remotePlaybackContexts[targetFile.id]
            self.log("applyPreparedPlayback committed videoURLs=\(resolvedURLs.count) isBDMV=\(isBDMV) bdRoot=\(bdRoot ?? "nil")")
        }
        return true
    }

    private func resolveEmbyCompatibleStreamOptions(
        itemId: String,
        mediaSourceId: String,
        sourceBaseUrl: URL,
        token: String?,
        userId: String?
    ) async -> EmbyStreamOptions? {
        guard var components = URLComponents(url: sourceBaseUrl, resolvingAgainstBaseURL: false) else { return nil }
        let basePath = components.path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        let trimmedUserId = userId?.trimmingCharacters(in: .whitespacesAndNewlines)
        let itemPath: String
        if let trimmedUserId, !trimmedUserId.isEmpty {
            itemPath = "Users/\(trimmedUserId)/Items/\(itemId)"
        } else {
            itemPath = "Items/\(itemId)"
        }
        components.path = "/" + [basePath, itemPath].filter { !$0.isEmpty }.joined(separator: "/")
        var queryItems = [
            URLQueryItem(name: "Fields", value: "MediaSources,MediaStreams")
        ]
        let queryToken = token?.trimmingCharacters(in: .whitespacesAndNewlines)
        if let queryToken, !queryToken.isEmpty {
            queryItems.append(URLQueryItem(name: "api_key", value: queryToken))
        }
        components.queryItems = queryItems
        guard let url = components.url else { return nil }

        var request = URLRequest(url: url)
        request.timeoutInterval = 10
        request.setValue("OmniPlay", forHTTPHeaderField: "X-Emby-Client")
        request.setValue("omniplay-mac", forHTTPHeaderField: "X-Emby-Device-Id")
        request.setValue("OmniPlay", forHTTPHeaderField: "X-Emby-Device-Name")
        request.setValue("1.0", forHTTPHeaderField: "X-Emby-Client-Version")
        if let queryToken, !queryToken.isEmpty {
            request.setValue(queryToken, forHTTPHeaderField: "X-Emby-Token")
            request.setValue(queryToken, forHTTPHeaderField: "X-MediaBrowser-Token")
        }

        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 10
        configuration.timeoutIntervalForResource = 15
        let session = URLSession(configuration: configuration, delegate: LocalNetworkTrustSessionDelegate.shared, delegateQueue: nil)

        do {
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
                log("PlaybackInfo stream selection request failed status=\((response as? HTTPURLResponse)?.statusCode ?? -1)")
                return nil
            }
            guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                log("PlaybackInfo stream selection response invalid")
                return nil
            }
            let mediaSources = object["MediaSources"] as? [[String: Any]] ?? []
            let selectedSource = mediaSources.first { source in
                (embyMediaStreamString(source, keys: ["Id"]).caseInsensitiveCompare(mediaSourceId) == .orderedSame)
            } ?? mediaSources.first
            let streams = (selectedSource?["MediaStreams"] as? [[String: Any]])
                ?? (object["MediaStreams"] as? [[String: Any]])
                ?? []
            guard !streams.isEmpty else {
                log("PlaybackInfo stream selection missing MediaStreams")
                return nil
            }

            let audioIndex = selectedEmbyAudioStreamIndex(from: streams)
            let subtitle = selectedEmbySubtitleStream(from: streams)
            log("PlaybackInfo stream selection audio=\(audioIndex.map(String.init) ?? "nil") subtitle=\(subtitle.map { String($0.index) } ?? "nil") method=\(subtitle?.method ?? "nil")")
            return EmbyStreamOptions(
                audioTracks: embyAudioStreamOptions(from: streams),
                subtitleTracks: embySubtitleStreamOptions(from: streams),
                selection: EmbyStreamSelection(
                    audioStreamIndex: audioIndex,
                    subtitleStreamIndex: subtitle?.index,
                    subtitleMethod: subtitle?.method
                )
            )
        } catch {
            log("PlaybackInfo stream selection error=\(error.localizedDescription)")
            return nil
        }
    }

    private func selectedEmbyAudioStreamIndex(from streams: [[String: Any]]) -> Int? {
        let preference = (UserDefaults.standard.string(forKey: "defaultAudio") ?? "auto")
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()
        var best: (index: Int, score: Int, order: Int)?

        for (order, stream) in streams.enumerated() where embyMediaStreamString(stream, keys: ["Type"]).caseInsensitiveCompare("Audio") == .orderedSame {
            guard let streamIndex = embyMediaStreamInt(stream, keys: ["Index"]) else { continue }
            let language = embyMediaStreamCombinedString(stream, keys: ["Language", "LanguageCode", "DisplayLanguage"])
            let title = embyMediaStreamCombinedString(stream, keys: ["Title", "DisplayTitle", "LocalizedDefault"])
            let isDefault = embyMediaStreamBool(stream, keys: ["IsDefault", "Default"]) ?? false
            let score: Int
            if preference == "auto" {
                score = (isDefault ? 0 : 100) + order
            } else if let languageScore = embyLanguagePreferenceScore(preference: preference, language: language, title: title) {
                score = languageScore + (isDefault ? 0 : 10) + order
            } else {
                score = (isDefault ? 200 : 300) + order
            }

            if best == nil || score < best!.score || (score == best!.score && order < best!.order) {
                best = (streamIndex, score, order)
            }
        }
        return best?.index
    }

    private func embyAudioStreamOptions(from streams: [[String: Any]]) -> [EmbyMediaStreamOption] {
        streams.enumerated().compactMap { order, stream in
            guard embyMediaStreamString(stream, keys: ["Type"]).caseInsensitiveCompare("Audio") == .orderedSame,
                  let index = embyMediaStreamInt(stream, keys: ["Index"]) else {
                return nil
            }
            return EmbyMediaStreamOption(
                index: index,
                displayName: embyMediaStreamDisplayName(stream, fallback: "音轨 \(order + 1)", subtitleMethod: nil),
                method: nil
            )
        }
    }

    private func embySubtitleStreamOptions(from streams: [[String: Any]]) -> [EmbyMediaStreamOption] {
        streams.enumerated().compactMap { order, stream in
            guard embyMediaStreamString(stream, keys: ["Type"]).caseInsensitiveCompare("Subtitle") == .orderedSame,
                  let index = embyMediaStreamInt(stream, keys: ["Index"]) else {
                return nil
            }
            let method = embySubtitleDeliveryMethod(for: stream)
            return EmbyMediaStreamOption(
                index: index,
                displayName: embyMediaStreamDisplayName(stream, fallback: "字幕 \(order + 1)", subtitleMethod: method),
                method: method
            )
        }
    }

    private func embyMediaStreamDisplayName(_ stream: [String: Any], fallback: String, subtitleMethod: String?) -> String {
        let language = embyMediaStreamCombinedString(stream, keys: ["Language", "LanguageCode", "DisplayLanguage", "LanguageTag", "LanguageName"])
        let title = embyMediaStreamCombinedString(stream, keys: ["Title", "DisplayTitle", "LocalizedDefault", "Name"])
        let languageContext = embyMediaStreamCombinedString(stream, keys: ["Title", "DisplayTitle", "LocalizedDefault", "Name", "Path", "DeliveryUrl"])
        let codec = embyFormattedCodec(embyMediaStreamCombinedString(stream, keys: ["Codec", "CodecTag"]))
        let channels = embyMediaStreamInt(stream, keys: ["Channels", "AudioChannels"])
        let channelLayout = embyMediaStreamString(stream, keys: ["ChannelLayout"])

        var parts: [String] = []
        let languageLabel = embyMediaLanguageLabel(language, title: languageContext)
        if !languageLabel.isEmpty {
            parts.append(languageLabel)
        } else if subtitleMethod != nil {
            parts.append("未知语言")
        }
        if !title.isEmpty,
           title.caseInsensitiveCompare(language) != .orderedSame,
           !embyMediaStreamTitleIsRedundant(title, languageLabel: languageLabel, codec: codec) {
            parts.append(title)
        }

        var details: [String] = []
        if !codec.isEmpty {
            details.append(codec)
        }
        if let channels, channels > 0 {
            details.append(channels == 2 ? "2.0" : "\(channels)ch")
        } else if !channelLayout.isEmpty {
            details.append(channelLayout)
        }
        if subtitleMethod?.caseInsensitiveCompare("Encode") == .orderedSame {
            details.append("烧录")
        }

        var name = parts.isEmpty ? fallback : parts.joined(separator: " - ")
        if !details.isEmpty {
            name += " (\(details.joined(separator: " / ")))"
        }
        return name
    }

    private func embyFormattedCodec(_ codec: String) -> String {
        let normalized = codec.lowercased()
        if normalized.contains("truehd") { return "TrueHD" }
        if normalized.contains("dts-hd") || normalized.contains("dtshd") { return "DTS-HD MA" }
        if normalized.contains("dts") { return "DTS" }
        if normalized.contains("eac3") { return "E-AC3" }
        if normalized.contains("ac3") { return "AC3" }
        if normalized.contains("aac") { return "AAC" }
        if normalized.contains("flac") { return "FLAC" }
        if normalized.contains("pgs") || normalized.contains("hdmv") { return "PGS" }
        if normalized.contains("subrip") || normalized.contains("srt") { return "SRT" }
        if normalized.contains("ass") { return "ASS" }
        if normalized.contains("ssa") { return "SSA" }
        return codec.uppercased()
    }

    private func embyMediaStreamTitleIsRedundant(_ title: String, languageLabel: String, codec: String) -> Bool {
        let normalized = title.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !normalized.isEmpty else { return true }
        if !languageLabel.isEmpty && normalized == languageLabel.lowercased() { return true }
        if !codec.isEmpty && normalized == codec.lowercased() { return true }
        let compact = normalized.replacingOccurrences(of: " ", with: "")
        return ["pgs", "pgssub", "hdmvpgssub", "srt", "subrip", "ass", "ssa"].contains(compact)
    }

    private func embyMediaLanguageLabel(_ value: String, title: String = "") -> String {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        let context = "\(trimmed) \(title)"
        let primaryCode = normalizedMediaLanguagePrimaryCode(trimmed)
        let lowerContext = context.lowercased()

        if primaryCode == "und"
            || primaryCode == "undefined"
            || primaryCode == "unknown"
            || primaryCode == "mis"
            || primaryCode == "mul" {
            if isChineseMediaLanguage(language: "", title: title) {
                return chineseMediaLanguageScore(language: "", title: title) == 2 ? "繁体中文" : "简体中文"
            }
            if isEnglishMediaLanguage(language: "", title: title) { return "英语" }
            if isJapaneseMediaLanguage(language: "", title: title) { return "日语" }
            return ""
        }

        if primaryCode == "yue" || lowerContext.contains("cantonese") || context.contains("粤") || context.contains("粵") {
            return "粤语"
        }
        if isChineseMediaLanguage(language: trimmed, title: title) {
            let score = chineseMediaLanguageScore(language: trimmed, title: title)
            if score == 0 { return "简体中文" }
            if score == 2 { return "繁体中文" }
            return "中文"
        }
        if isEnglishMediaLanguage(language: trimmed, title: title) { return "英语" }
        if isJapaneseMediaLanguage(language: trimmed, title: title) { return "日语" }
        switch normalizedMediaLanguagePrimaryCode(value) {
        case "kor", "ko": return "韩语"
        case "fre", "fra", "fr": return "法语"
        case "spa", "es": return "西语"
        case "ger", "deu", "de": return "德语"
        case "ita", "it": return "意语"
        case "rus", "ru": return "俄语"
        case "por", "pt": return "葡语"
        case "tha", "th": return "泰语"
        case "vie", "vi": return "越南语"
        default: return trimmed
        }
    }

    private func selectedEmbySubtitleStream(from streams: [[String: Any]]) -> (index: Int, method: String)? {
        let preference = (UserDefaults.standard.string(forKey: "defaultSub") ?? "chi")
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()
        guard preference != "no" else { return nil }

        var best: (index: Int, method: String, score: Int, order: Int)?
        for (order, stream) in streams.enumerated() where embyMediaStreamString(stream, keys: ["Type"]).caseInsensitiveCompare("Subtitle") == .orderedSame {
            guard let streamIndex = embyMediaStreamInt(stream, keys: ["Index"]) else { continue }
            let language = embyMediaStreamCombinedString(stream, keys: ["Language", "LanguageCode", "DisplayLanguage"])
            let title = embyMediaStreamCombinedString(stream, keys: ["Title", "DisplayTitle", "LocalizedDefault"])
            guard let preferenceScore = embySubtitlePreferenceScore(preference: preference, language: language, title: title) else { continue }
            let method = embySubtitleDeliveryMethod(for: stream)
            let isForced = embyMediaStreamBool(stream, keys: ["IsForced", "Forced"]) ?? false
            let forcedPenalty = isForced ? 50 : 0
            let methodPenalty = method.caseInsensitiveCompare("Encode") == .orderedSame ? 10 : 0
            let score = preferenceScore * 100 + methodPenalty + forcedPenalty + order
            if best == nil || score < best!.score || (score == best!.score && order < best!.order) {
                best = (streamIndex, method, score, order)
            }
        }
        if best == nil {
            log("PlaybackInfo subtitle selection no preferred match defaultSub=\(preference) candidates=\(embySubtitleCandidateSummary(from: streams))")
        }
        return best.map { ($0.index, $0.method) }
    }

    private func embySubtitleDeliveryMethod(for stream: [String: Any]) -> String {
        let codec = embyMediaStreamString(stream, keys: ["Codec", "CodecTag", "DisplayTitle"]).lowercased()
        if codec.contains("pgs")
            || codec.contains("hdmv")
            || codec.contains("dvb")
            || codec.contains("dvdsub")
            || codec.contains("vobsub") {
            return "Encode"
        }

        let rawMethod = embyMediaStreamString(stream, keys: ["DeliveryMethod", "SubtitleDeliveryMethod"])
        if let canonical = canonicalEmbySubtitleMethod(rawMethod) {
            return canonical
        }

        let isExternal = embyMediaStreamBool(stream, keys: ["IsExternal", "External"]) ?? false
        let isText = codec.contains("srt")
            || codec.contains("subrip")
            || codec.contains("ass")
            || codec.contains("ssa")
            || codec.contains("vtt")
            || codec.contains("webvtt")
        return isExternal && isText ? "External" : "Encode"
    }

    private func canonicalEmbySubtitleMethod(_ value: String) -> String? {
        switch value.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() {
        case "external": return "External"
        case "hls": return "Hls"
        case "embed", "embedded": return "Embed"
        case "encode", "burn", "burnin": return "Encode"
        default: return nil
        }
    }

    private func embyLanguagePreferenceScore(preference: String, language: String, title: String) -> Int? {
        switch preference {
        case "chi":
            return isChineseMediaLanguage(language: language, title: title) ? chineseMediaLanguageScore(language: language, title: title) : nil
        case "eng":
            return isEnglishMediaLanguage(language: language, title: title) ? 0 : nil
        case "jpn":
            return isJapaneseMediaLanguage(language: language, title: title) ? 0 : nil
        default:
            return nil
        }
    }

    private func embySubtitlePreferenceScore(preference: String, language: String, title: String) -> Int? {
        switch preference {
        case "chi":
            if isChineseMediaLanguage(language: language, title: title) {
                return chineseMediaLanguageScore(language: language, title: title)
            }
            return isEnglishMediaLanguage(language: language, title: title) ? 100 : nil
        case "eng":
            if isEnglishMediaLanguage(language: language, title: title) {
                return 0
            }
            return isChineseMediaLanguage(language: language, title: title)
                ? 100 + chineseMediaLanguageScore(language: language, title: title)
                : nil
        default:
            return nil
        }
    }

    private func isChineseMediaLanguage(language: String, title: String) -> Bool {
        let primaryCode = normalizedMediaLanguagePrimaryCode(language)
        if ["chi", "zho", "zh", "cmn", "yue"].contains(primaryCode) {
            return true
        }
        let combined = "\(language) \(title)"
        let lowerCombined = combined.lowercased()
        if combined.contains("中文")
            || combined.contains("简体")
            || combined.contains("簡體")
            || combined.contains("繁體")
            || combined.contains("繁体")
            || combined.contains("中字")
            || combined.contains("简中")
            || combined.contains("簡中")
            || combined.contains("繁中")
            || combined.contains("国语")
            || combined.contains("國語")
            || combined.contains("普通话")
            || combined.contains("普通話") {
            return true
        }
        if lowerCombined.contains("chinese")
            || lowerCombined.contains("mandarin")
            || lowerCombined.contains("cantonese")
            || lowerCombined.contains("simplified")
            || lowerCombined.contains("traditional") {
            return true
        }
        let tokens = normalizedMediaLanguageTokens(from: combined)
        return !tokens.isDisjoint(with: ["chs", "cht", "zho", "chi", "cmn", "zh", "hans", "hant", "zhongwen", "mandarin"])
    }

    private func isEnglishMediaLanguage(language: String, title: String) -> Bool {
        let primaryCode = normalizedMediaLanguagePrimaryCode(language)
        if ["eng", "en"].contains(primaryCode) {
            return true
        }
        let combined = "\(language) \(title)"
        if combined.contains("英语") || combined.contains("英文") {
            return true
        }
        let tokens = normalizedMediaLanguageTokens(from: combined)
        return !tokens.isDisjoint(with: ["eng", "english"])
    }

    private func isJapaneseMediaLanguage(language: String, title: String) -> Bool {
        let primaryCode = normalizedMediaLanguagePrimaryCode(language)
        if ["jpn", "ja", "jp"].contains(primaryCode) {
            return true
        }
        let combined = "\(language) \(title)"
        if combined.contains("日语") || combined.contains("日文") || combined.contains("日本語") {
            return true
        }
        let tokens = normalizedMediaLanguageTokens(from: combined)
        return !tokens.isDisjoint(with: ["jpn", "japanese", "ja"])
    }

    private func chineseMediaLanguageScore(language: String, title: String) -> Int {
        let normalizedLanguage = language.lowercased().replacingOccurrences(of: "_", with: "-")
        let combined = "\(language) \(title)"
        let lowerCombined = combined.lowercased()
        let tokens = normalizedMediaLanguageTokens(from: combined)

        if normalizedLanguage.contains("hans")
            || normalizedLanguage.contains("zh-cn")
            || normalizedLanguage.contains("zh-sg")
            || combined.contains("简")
            || combined.contains("簡")
            || lowerCombined.contains("simplified")
            || tokens.contains("chs")
            || tokens.contains("gb") {
            return 0
        }

        if normalizedLanguage.contains("hant")
            || normalizedLanguage.contains("zh-tw")
            || normalizedLanguage.contains("zh-hk")
            || normalizedLanguage.contains("zh-mo")
            || combined.contains("繁")
            || lowerCombined.contains("traditional")
            || tokens.contains("cht")
            || tokens.contains("big5") {
            return 2
        }

        return 1
    }

    private func normalizedMediaLanguagePrimaryCode(_ value: String) -> String {
        let normalized = value
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()
            .replacingOccurrences(of: "_", with: "-")
        return normalized.split(separator: "-").first.map(String.init) ?? normalized
    }

    private func normalizedMediaLanguageTokens(from value: String) -> Set<String> {
        let lower = value.lowercased().replacingOccurrences(of: "_", with: "-")
        return Set(lower.components(separatedBy: CharacterSet.alphanumerics.inverted).filter { !$0.isEmpty })
    }

    private func embyMediaStreamString(_ stream: [String: Any], keys: [String]) -> String {
        for key in keys {
            if let value = stream[key] as? String {
                let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
                if !trimmed.isEmpty {
                    return trimmed
                }
            }
            if let value = stream[key] as? NSNumber {
                return value.stringValue
            }
        }
        return ""
    }

    private func embyMediaStreamCombinedString(_ stream: [String: Any], keys: [String]) -> String {
        var values: [String] = []
        for key in keys {
            let value = embyMediaStreamString(stream, keys: [key])
            if !value.isEmpty && !values.contains(where: { $0.caseInsensitiveCompare(value) == .orderedSame }) {
                values.append(value)
            }
        }
        return values.joined(separator: " ")
    }

    private func embySubtitleCandidateSummary(from streams: [[String: Any]]) -> String {
        streams
            .filter { embyMediaStreamString($0, keys: ["Type"]).caseInsensitiveCompare("Subtitle") == .orderedSame }
            .prefix(12)
            .map { stream in
                let index = embyMediaStreamInt(stream, keys: ["Index"]).map(String.init) ?? "?"
                let language = embyMediaStreamCombinedString(stream, keys: ["Language", "LanguageCode", "DisplayLanguage"])
                let title = embyMediaStreamCombinedString(stream, keys: ["Title", "DisplayTitle", "LocalizedDefault"])
                let codec = embyMediaStreamString(stream, keys: ["Codec", "CodecTag"])
                return "#\(index){lang=\(language.isEmpty ? "nil" : language),title=\(title.isEmpty ? "nil" : title),codec=\(codec.isEmpty ? "nil" : codec)}"
            }
            .joined(separator: ";")
    }

    private func embyMediaStreamInt(_ stream: [String: Any], keys: [String]) -> Int? {
        for key in keys {
            if let value = stream[key] as? Int {
                return value
            }
            if let value = stream[key] as? NSNumber {
                return value.intValue
            }
            if let value = stream[key] as? String, let intValue = Int(value) {
                return intValue
            }
        }
        return nil
    }

    private func embyMediaStreamBool(_ stream: [String: Any], keys: [String]) -> Bool? {
        for key in keys {
            if let value = stream[key] as? Bool {
                return value
            }
            if let value = stream[key] as? NSNumber {
                return value.boolValue
            }
            if let value = stream[key] as? String {
                switch value.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() {
                case "true", "1", "yes": return true
                case "false", "0", "no": return false
                default: break
                }
            }
        }
        return nil
    }

    private func resolveEmbyCompatiblePlaybackURL(
        itemId: String,
        mediaSourceId: String,
        fileName: String,
        sourceBaseUrl: URL,
        token: String?,
        userId: String?,
        streamSelection: EmbyStreamSelection?
    ) async -> URL? {
        if let playbackInfoURL = await resolveEmbyCompatiblePlaybackInfoURL(
            itemId: itemId,
            mediaSourceId: mediaSourceId,
            fileName: fileName,
            sourceBaseUrl: sourceBaseUrl,
            token: token,
            userId: userId,
            streamSelection: streamSelection
        ) {
            log("PlaybackInfo resolved URL path=\(playbackInfoURL.path)")
            return playbackInfoURL
        }

        return fallbackEmbyCompatibleHlsURL(
            itemId: itemId,
            mediaSourceId: mediaSourceId,
            fileName: fileName,
            sourceBaseUrl: sourceBaseUrl,
            token: token,
            streamSelection: streamSelection
        )
    }

    private func fallbackEmbyCompatibleHlsURL(
        itemId: String,
        mediaSourceId: String,
        fileName: String,
        sourceBaseUrl: URL,
        token: String?,
        streamSelection: EmbyStreamSelection?
    ) -> URL? {
        guard var components = URLComponents(url: sourceBaseUrl, resolvingAgainstBaseURL: false) else { return nil }
        let basePath = components.path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        components.path = "/" + [basePath, "Videos/\(itemId)/master.m3u8"]
            .filter { !$0.isEmpty }
            .joined(separator: "/")
        var queryItems = embyCompatibleHlsQueryItems(
            mediaSourceId: mediaSourceId,
            playSessionId: "omniplay\(itemId.filter { $0.isLetter || $0.isNumber })",
            deviceId: "omniplay-mac",
            fileName: fileName,
            streamSelection: streamSelection
        )
        if let token = token?.trimmingCharacters(in: .whitespacesAndNewlines), !token.isEmpty {
            queryItems.append(URLQueryItem(name: "api_key", value: token))
        }
        components.queryItems = queryItems
        log("PlaybackInfo fallback HLS URL path=\(components.path)")
        return components.url
    }

    private func embyCompatibleHlsQueryItems(
        mediaSourceId: String,
        playSessionId: String,
        deviceId: String,
        fileName: String,
        streamSelection: EmbyStreamSelection?
    ) -> [URLQueryItem] {
        let quality = embyCompatiblePlaybackInfoQuality(for: fileName)
        var items = [
            URLQueryItem(name: "MediaSourceId", value: mediaSourceId),
            URLQueryItem(name: "PlaySessionId", value: playSessionId),
            URLQueryItem(name: "DeviceId", value: deviceId),
            URLQueryItem(name: "EnableAutoStreamCopy", value: "true"),
            URLQueryItem(name: "AllowVideoStreamCopy", value: "true"),
            URLQueryItem(name: "AllowAudioStreamCopy", value: "true"),
            URLQueryItem(name: "EnableAdaptiveBitrateStreaming", value: "false"),
            URLQueryItem(name: "VideoCodec", value: "h264,hevc"),
            URLQueryItem(name: "AudioCodec", value: "aac,ac3,eac3,dts,flac,truehd,mp3,opus,vorbis"),
            URLQueryItem(name: "SegmentContainer", value: "ts"),
            URLQueryItem(name: "SegmentLength", value: "6"),
            URLQueryItem(name: "MinSegments", value: "1"),
            URLQueryItem(name: "VideoBitRate", value: "\(quality.videoBitRate)"),
            URLQueryItem(name: "MaxStreamingBitrate", value: "\(quality.maxStreamingBitrate)"),
            URLQueryItem(name: "AudioBitRate", value: "640000"),
            URLQueryItem(name: "MaxWidth", value: "\(quality.maxWidth)"),
            URLQueryItem(name: "MaxHeight", value: "\(quality.maxHeight)"),
            URLQueryItem(name: "Profile", value: "high"),
            URLQueryItem(name: "Level", value: "51"),
            URLQueryItem(name: "RequireAvc", value: "false"),
            URLQueryItem(name: "TranscodingMaxAudioChannels", value: "6"),
            URLQueryItem(name: "BreakOnNonKeyFrames", value: "false"),
            URLQueryItem(name: "CopyTimestamps", value: "true"),
            URLQueryItem(name: "Context", value: "Streaming")
        ]
        if let audioStreamIndex = streamSelection?.audioStreamIndex {
            items.append(URLQueryItem(name: "AudioStreamIndex", value: "\(audioStreamIndex)"))
        }
        if let subtitleStreamIndex = streamSelection?.subtitleStreamIndex {
            items.append(URLQueryItem(name: "SubtitleStreamIndex", value: "\(subtitleStreamIndex)"))
            if let subtitleMethod = streamSelection?.subtitleMethod {
                items.append(URLQueryItem(name: "SubtitleMethod", value: subtitleMethod))
                if subtitleMethod.caseInsensitiveCompare("Encode") == .orderedSame {
                    items.append(URLQueryItem(name: "AlwaysBurnInSubtitleWhenTranscoding", value: "true"))
                }
            }
        } else {
            items.append(URLQueryItem(name: "SubtitleStreamIndex", value: "-1"))
        }
        return items
    }

    private func resolveEmbyCompatiblePlaybackInfoURL(
        itemId: String,
        mediaSourceId: String,
        fileName: String,
        sourceBaseUrl: URL,
        token: String?,
        userId: String?,
        streamSelection: EmbyStreamSelection?
    ) async -> URL? {
        guard var components = URLComponents(url: sourceBaseUrl, resolvingAgainstBaseURL: false) else { return nil }
        let basePath = components.path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        let itemPath = "Items/\(itemId)/PlaybackInfo"
        components.path = "/" + [basePath, itemPath].filter { !$0.isEmpty }.joined(separator: "/")
        let queryUserId = userId?.trimmingCharacters(in: .whitespacesAndNewlines)
        let queryToken = token?.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let url = components.url else { return nil }

        let attempts: [(label: String, allowVideoCopy: Bool, allowAudioCopy: Bool, videoCodecs: String, audioCodecs: String)] = [
            ("copy", true, true, "h264,hevc", "aac,ac3,eac3,dts,flac,truehd,mp3,opus,vorbis"),
            ("compatible", true, false, "h264,hevc", "aac")
        ]

        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 20
        configuration.timeoutIntervalForResource = 30
        let session = URLSession(configuration: configuration, delegate: LocalNetworkTrustSessionDelegate.shared, delegateQueue: nil)
        for attempt in attempts {
            do {
                guard var attemptComponents = URLComponents(url: url, resolvingAgainstBaseURL: false) else { continue }
                var queryItems: [URLQueryItem] = [
                    URLQueryItem(name: "UserId", value: queryUserId),
                    URLQueryItem(name: "MediaSourceId", value: mediaSourceId),
                    URLQueryItem(name: "StartTimeTicks", value: "0"),
                    URLQueryItem(name: "AudioStreamIndex", value: streamSelection?.audioStreamIndex.map(String.init)),
                    URLQueryItem(name: "SubtitleStreamIndex", value: streamSelection?.subtitleStreamIndex.map(String.init) ?? "-1"),
                    URLQueryItem(name: "SubtitleMethod", value: streamSelection?.subtitleMethod),
                    URLQueryItem(name: "MaxAudioChannels", value: "6"),
                    URLQueryItem(name: "MaxStreamingBitrate", value: "\(embyCompatiblePlaybackInfoQuality(for: fileName).maxStreamingBitrate)"),
                    URLQueryItem(name: "EnableDirectPlay", value: attempt.label == "copy" ? "true" : "false"),
                    URLQueryItem(name: "EnableDirectStream", value: "true"),
                    URLQueryItem(name: "EnableTranscoding", value: "true"),
                    URLQueryItem(name: "AllowVideoStreamCopy", value: attempt.allowVideoCopy ? "true" : "false"),
                    URLQueryItem(name: "AllowAudioStreamCopy", value: attempt.allowAudioCopy ? "true" : "false"),
                    URLQueryItem(name: "AutoOpenLiveStream", value: "true")
                ]
                if let queryToken, !queryToken.isEmpty {
                    queryItems.append(URLQueryItem(name: "api_key", value: queryToken))
                }
                attemptComponents.queryItems = queryItems.filter { $0.value != nil }
                guard let attemptURL = attemptComponents.url else { continue }

                var request = URLRequest(url: attemptURL)
                request.httpMethod = "POST"
                request.timeoutInterval = 20
                request.setValue("application/json", forHTTPHeaderField: "Content-Type")
                request.setValue("OmniPlay", forHTTPHeaderField: "X-Emby-Client")
                request.setValue("omniplay-mac", forHTTPHeaderField: "X-Emby-Device-Id")
                request.setValue("OmniPlay", forHTTPHeaderField: "X-Emby-Device-Name")
                request.setValue("1.0", forHTTPHeaderField: "X-Emby-Client-Version")
                if let queryToken, !queryToken.isEmpty {
                    request.setValue(queryToken, forHTTPHeaderField: "X-Emby-Token")
                    request.setValue(queryToken, forHTTPHeaderField: "X-MediaBrowser-Token")
                }
                request.httpBody = try? JSONSerialization.data(withJSONObject: embyCompatiblePlaybackInfoBody(
                    fileName: fileName,
                    userId: queryUserId,
                    mediaSourceId: mediaSourceId,
                    allowVideoStreamCopy: attempt.allowVideoCopy,
                    allowAudioStreamCopy: attempt.allowAudioCopy,
                    enableDirectPlay: attempt.label == "copy",
                    videoCodecs: attempt.videoCodecs,
                    audioCodecs: attempt.audioCodecs,
                    audioStreamIndex: streamSelection?.audioStreamIndex,
                    subtitleStreamIndex: streamSelection?.subtitleStreamIndex,
                    subtitleMethod: streamSelection?.subtitleMethod
                ))

                let (data, response) = try await session.data(for: request)
                guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
                    log("PlaybackInfo \(attempt.label) request failed status=\((response as? HTTPURLResponse)?.statusCode ?? -1)")
                    continue
                }
                guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                      let mediaSources = object["MediaSources"] as? [[String: Any]],
                      !mediaSources.isEmpty else {
                    log("PlaybackInfo \(attempt.label) response missing MediaSources")
                    continue
                }
                let selected = mediaSources.first { source in
                    (source["Id"] as? String)?.caseInsensitiveCompare(mediaSourceId) == .orderedSame
                } ?? mediaSources[0]
                let playSessionId = object["PlaySessionId"] as? String
                for key in ["TranscodingUrl", "DirectStreamUrl"] {
                    guard let raw = selected[key] as? String, !raw.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { continue }
                    if let resolved = resolvedMediaServerPlaybackURL(raw, relativeTo: sourceBaseUrl, token: token, playSessionId: playSessionId) {
                        log("PlaybackInfo \(attempt.label) selected \(key)")
                        return resolved
                    }
                }
                let errorCode = object["ErrorCode"] as? String ?? "nil"
                let supportsTranscoding = selected["SupportsTranscoding"] as? Bool
                let supportsDirectStream = selected["SupportsDirectStream"] as? Bool
                let supportsDirectPlay = selected["SupportsDirectPlay"] as? Bool
                let transcodingContainer = selected["TranscodingContainer"] as? String ?? "nil"
                let transcodingSubProtocol = selected["TranscodingSubProtocol"] as? String ?? "nil"
                log("PlaybackInfo \(attempt.label) no URL error=\(errorCode) directPlay=\(supportsDirectPlay.map(String.init) ?? "nil") directStream=\(supportsDirectStream.map(String.init) ?? "nil") transcoding=\(supportsTranscoding.map(String.init) ?? "nil") container=\(transcodingContainer) subProtocol=\(transcodingSubProtocol)")
            } catch {
                log("PlaybackInfo \(attempt.label) request error=\(error.localizedDescription)")
                continue
            }
        }
        return nil
    }

    private func resolvedMediaServerPlaybackURL(_ value: String, relativeTo baseURL: URL, token: String?, playSessionId: String?) -> URL? {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        let rawURL = URL(string: trimmed, relativeTo: baseURL)?.absoluteURL
        guard var components = rawURL.flatMap({ URLComponents(url: $0, resolvingAgainstBaseURL: false) }) else { return nil }
        var items = components.queryItems ?? []
        if let token = token?.trimmingCharacters(in: .whitespacesAndNewlines),
           !token.isEmpty,
           !items.contains(where: { $0.name.caseInsensitiveCompare("api_key") == .orderedSame }) {
            items.append(URLQueryItem(name: "api_key", value: token))
        }
        if let playSessionId = playSessionId?.trimmingCharacters(in: .whitespacesAndNewlines),
           !playSessionId.isEmpty,
           !items.contains(where: { $0.name.caseInsensitiveCompare("PlaySessionId") == .orderedSame }) {
            items.append(URLQueryItem(name: "PlaySessionId", value: playSessionId))
        }
        components.queryItems = items.isEmpty ? nil : items
        return components.url
    }

    private func embyCompatiblePlaybackInfoBody(
        fileName: String,
        userId: String?,
        mediaSourceId: String,
        allowVideoStreamCopy: Bool,
        allowAudioStreamCopy: Bool,
        enableDirectPlay: Bool,
        videoCodecs: String,
        audioCodecs: String,
        audioStreamIndex: Int?,
        subtitleStreamIndex: Int?,
        subtitleMethod: String?
    ) -> [String: Any] {
        let quality = embyCompatiblePlaybackInfoQuality(for: fileName)
        var body: [String: Any] = [
            "MediaSourceId": mediaSourceId,
            "StartTimeTicks": 0,
            "MaxAudioChannels": 6,
            "MaxStreamingBitrate": quality.maxStreamingBitrate,
            "EnableDirectPlay": enableDirectPlay,
            "EnableDirectStream": true,
            "EnableTranscoding": true,
            "AllowVideoStreamCopy": allowVideoStreamCopy,
            "AllowAudioStreamCopy": allowAudioStreamCopy,
            "AutoOpenLiveStream": true,
            "DeviceProfile": [
                "Name": "OmniPlay mpv",
                "MaxStreamingBitrate": quality.maxStreamingBitrate,
                "MaxStaticBitrate": quality.maxStreamingBitrate,
                "MusicStreamingTranscodingBitrate": 1_920_000,
                "DirectPlayProfiles": [
                    [
                        "Container": "mkv,mp4,mov,m4v,ts,m2ts,avi,webm",
                        "Type": "Video",
                        "VideoCodec": videoCodecs,
                        "AudioCodec": audioCodecs
                    ]
                ],
                "TranscodingProfiles": [
                    [
                        "Container": "ts",
                        "Type": "Video",
                        "Protocol": "hls",
                        "Context": "Streaming",
                        "VideoCodec": videoCodecs,
                        "AudioCodec": audioCodecs,
                        "MaxAudioChannels": "6",
                        "MinSegments": "1",
                        "BreakOnNonKeyFrames": false,
                        "CopyTimestamps": true
                    ]
                ],
                "ContainerProfiles": [],
                "CodecProfiles": [],
                "ResponseProfiles": [],
                "SubtitleProfiles": [
                    ["Format": "srt", "Method": "External"],
                    ["Format": "ass", "Method": "External"],
                    ["Format": "ssa", "Method": "External"],
                    ["Format": "vtt", "Method": "External"],
                    ["Format": "pgs", "Method": "Encode"],
                    ["Format": "dvdsub", "Method": "Encode"],
                    ["Format": "sub", "Method": "Encode"]
                ]
            ]
        ]
        if let userId, !userId.isEmpty {
            body["UserId"] = userId
        }
        if let audioStreamIndex {
            body["AudioStreamIndex"] = audioStreamIndex
        }
        body["SubtitleStreamIndex"] = subtitleStreamIndex ?? -1
        if let subtitleMethod, !subtitleMethod.isEmpty {
            body["SubtitleMethod"] = subtitleMethod
            if subtitleMethod.caseInsensitiveCompare("Encode") == .orderedSame {
                body["AlwaysBurnInSubtitleWhenTranscoding"] = true
            }
        }
        return body
    }

    private func embyCompatiblePlaybackInfoQuality(for fileName: String) -> (videoBitRate: Int, maxStreamingBitrate: Int, maxWidth: Int, maxHeight: Int) {
        let lower = fileName.lowercased()
        if lower.contains("2160p") || lower.contains("uhd") || lower.contains("4k") {
            return (60_000_000, 70_000_000, 3840, 2160)
        }
        return (35_000_000, 45_000_000, 1920, 1080)
    }

    private func resolveWebDAVCredential(authConfig: String?, baseURLString: String) -> (username: String, password: String)? {
        if let id = WebDAVCredentialStore.shared.credentialID(from: authConfig),
           let stored = WebDAVCredentialStore.shared.loadCredential(id: id) {
            return (stored.username, stored.password)
        }
        if let legacy = WebDAVCredentialStore.shared.decodeLegacyCredential(from: authConfig) {
            return (legacy.username, legacy.password)
        }
        if let url = URL(string: baseURLString), let user = url.user, !user.isEmpty {
            return (user, url.password ?? "")
        }
        return nil
    }

    private func playNextEpisodeAndMarkCurrentWatched() {
        guard let currentId = currentVideoFileId else { return }
        guard let currentIndex = allPlaylistFiles.firstIndex(where: { $0.id == currentId }),
              allPlaylistFiles.indices.contains(currentIndex + 1) else { return }
        let nextFileId = allPlaylistFiles[currentIndex + 1].id
        let snapshot = playerManager.playbackEngineSnapshot
        let duration = max(playerManager.duration, snapshot.duration)
        let currentTime = max(playerManager.currentTimePos, snapshot.timePos)

        Task.detached {
            do {
                try await AppDatabase.shared.dbQueue.write { db in
                    if var file = try VideoFile.fetchOne(db, key: currentId) {
                        Self.applyFinishedPlaybackState(
                            to: &file,
                            observedDuration: duration,
                            observedProgress: currentTime
                        )
                        try file.update(db)
                    }
                }
                await MainActor.run {
                    NotificationCenter.default.post(
                        name: .libraryUpdated,
                        object: nil,
                        userInfo: [LibraryUpdateUserInfoKey.preferredVideoFileId: nextFileId]
                    )
                    currentVideoFileId = nextFileId
                    playerManager.executeMpvCommand(["playlist-next", "force"])
                    resetHideTimer()
                }
            } catch {
                await MainActor.run {
                    currentVideoFileId = nextFileId
                    playerManager.executeMpvCommand(["playlist-next", "force"])
                    resetHideTimer()
                }
            }
        }
    }

    private func tryStartPlaybackIfReady() {
        guard !hasIssuedLoad else { return }
        guard !videoURLs.isEmpty else { return }
        guard playerManager.drawableBindVersion > 0 else { return }
        hasIssuedLoad = true
        log("start playback after drawable ready version=\(playerManager.drawableBindVersion)")
        playerManager.loadFiles(urls: videoURLs, startPosition: startPosition, isBluRay: isBluRayFolder, blurayRootPath: blurayRootPath, trackPreferenceKey: currentVideoFileId)
    }
}
