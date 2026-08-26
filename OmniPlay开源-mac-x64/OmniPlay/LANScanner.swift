import Foundation
import Network
import SwiftUI
import Combine
import Darwin

final class LocalNetworkTrustSessionDelegate: NSObject, URLSessionDelegate {
    static let shared = LocalNetworkTrustSessionDelegate()

    func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge, completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              let serverTrust = challenge.protectionSpace.serverTrust,
              Self.isLocalOrPrivateHost(challenge.protectionSpace.host) else {
            completionHandler(.performDefaultHandling, nil)
            return
        }

        completionHandler(.useCredential, URLCredential(trust: serverTrust))
    }

    static func isLocalOrPrivateHost(_ host: String) -> Bool {
        let normalized = host
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .trimmingCharacters(in: CharacterSet(charactersIn: "[]"))
            .lowercased()

        if normalized == "localhost" || normalized == "::1" || normalized.hasSuffix(".local") {
            return true
        }
        if normalized.hasPrefix("fe80:") || normalized.hasPrefix("fc") || normalized.hasPrefix("fd") {
            return true
        }

        let octets = normalized.split(separator: ".").compactMap { Int($0) }
        guard octets.count == 4 else { return false }
        if octets[0] == 10 || octets[0] == 127 { return true }
        if octets[0] == 169 && octets[1] == 254 { return true }
        if octets[0] == 192 && octets[1] == 168 { return true }
        if octets[0] == 172 && (16...31).contains(octets[1]) { return true }
        return false
    }
}

// 🌟 发现的设备数据模型
struct DiscoveredDevice: Identifiable, Hashable {
    let id = UUID()
    let name: String
    let ipAddress: String
    let port: Int
    let type: DeviceType
    
    enum DeviceType: String {
        case smb = "SMB"
        case webdavHTTP = "WebDAV (HTTP)"
        case webdavHTTPS = "WebDAV (HTTPS)"
        case plex = "Plex"
        case emby = "Emby"
        case jellyfin = "Jellyfin"
        case omniplayDocker = "OmniPlay Docker"

        var isWebDAV: Bool {
            self == .webdavHTTP || self == .webdavHTTPS
        }

        var isMediaServer: Bool {
            self == .plex || self == .emby || self == .jellyfin || self == .omniplayDocker
        }

        var scheme: String {
            self == .webdavHTTPS ? "https" : "http"
        }
    }
}

// 🌟 核心雷达扫描器
class LANScanner: NSObject, ObservableObject, NetServiceBrowserDelegate, NetServiceDelegate {
    @Published var discoveredDevices: [DiscoveredDevice] = []
    @Published var isScanning = false
    
    private var browsers: [NetServiceBrowser] = []
    private var resolvingServices: [NetService] = []
    private var scanTimer: Timer?
    private var probeTasks: [Task<Void, Never>] = []
    private lazy var probeSession: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 2.0
        configuration.timeoutIntervalForResource = 3.0
        configuration.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        configuration.urlCache = nil
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        configuration.connectionProxyDictionary = [:]
        return URLSession(configuration: configuration, delegate: LocalNetworkTrustSessionDelegate.shared, delegateQueue: nil)
    }()
    
    func startScanning() {
        stopScanning()
        isScanning = true
        discoveredDevices = []
        
        let serviceTypes = [
            "_smb._tcp.",
            "_webdav._tcp.",
            "_webdavs._tcp.",
            "_plexmediasvr._tcp.",
            "_jellyfin._tcp.",
            "_emby._tcp.",
            "_http._tcp.",
            "_https._tcp."
        ]
        
        for type in serviceTypes {
            let browser = NetServiceBrowser()
            browser.delegate = self
            browser.searchForServices(ofType: type, inDomain: "local.")
            browsers.append(browser)
        }

        probeLocalMediaServerPorts()
        probeLANOmniPlayDockerPorts()
        probeLANMediaServerPorts()
        probeEmbyUdpDiscovery()
        
        // A NAS can take a few seconds to answer while waking disks or
        // starting the container.  Do not cancel the subnet probes too early.
        scanTimer = Timer.scheduledTimer(withTimeInterval: 20.0, repeats: false) { [weak self] _ in
            self?.stopScanning()
        }
    }
    
    func stopScanning() {
        scanTimer?.invalidate()
        scanTimer = nil
        for browser in browsers { browser.stop() }
        browsers.removeAll()
        resolvingServices.removeAll()
        probeTasks.forEach { $0.cancel() }
        probeTasks.removeAll()
        isScanning = false
    }
    
    func netServiceBrowser(_ browser: NetServiceBrowser, didFind service: NetService, moreComing: Bool) {
        resolvingServices.append(service)
        service.delegate = self
        service.resolve(withTimeout: 5.0)
    }
    
    func netServiceDidResolveAddress(_ sender: NetService) {
        guard let addresses = sender.addresses,
              let ipString = addresses
                .map({ getIPAddress(from: $0) })
                .first(where: { !$0.isEmpty && !$0.contains(":") }) else { return }
        
        let serviceType = sender.type.lowercased()
        if serviceType.contains("smb") {
            addDevice(DiscoveredDevice(name: sender.name, ipAddress: ipString, port: sender.port, type: .smb))
            return
        }

        let task = Task { [weak self] in
            guard let self else { return }
            guard let type = await self.detectHTTPServiceType(serviceName: sender.name, serviceType: serviceType, ipAddress: ipString, port: sender.port) else { return }
            guard !Task.isCancelled else { return }
            self.addDevice(DiscoveredDevice(name: sender.name, ipAddress: ipString, port: sender.port, type: type))
        }
        probeTasks.append(task)
    }
    
    private func getIPAddress(from addressData: Data) -> String {
        var hostname = [CChar](repeating: 0, count: Int(NI_MAXHOST))
        addressData.withUnsafeBytes { pointer in
            guard let sockaddrPtr = pointer.bindMemory(to: sockaddr.self).baseAddress else { return }
            getnameinfo(sockaddrPtr, socklen_t(addressData.count), &hostname, socklen_t(hostname.count), nil, 0, NI_NUMERICHOST)
        }
        return String(cString: hostname)
    }

    private func addDevice(_ device: DiscoveredDevice) {
        DispatchQueue.main.async {
            guard self.isScanning else { return }
            guard device.name != Host.current().localizedName else { return }
            if self.discoveredDevices.contains(where: { $0.ipAddress == device.ipAddress && $0.port == device.port }) {
                return
            }
            if device.type == .plex,
               self.isLoopbackHost(device.ipAddress),
               let existingIndex = self.discoveredDevices.firstIndex(where: { $0.type == device.type && $0.port == device.port && !self.isLoopbackHost($0.ipAddress) }) {
                self.discoveredDevices[existingIndex] = device
                return
            }
            if device.type == .plex,
               !self.isLoopbackHost(device.ipAddress),
               self.discoveredDevices.contains(where: { $0.type == device.type && $0.port == device.port && self.isLoopbackHost($0.ipAddress) }) {
                return
            }
            self.discoveredDevices.append(device)
        }
    }

    private func probeLocalMediaServerPorts() {
        struct Endpoint {
            let url: URL
            let host: String
            let port: Int
        }

        let rawCandidates = [
            ("http://127.0.0.1:32400", "127.0.0.1", 32400),
            ("http://localhost:32400", "localhost", 32400),
            ("http://[::1]:32400", "localhost", 32400),
            ("http://127.0.0.1:8096", "127.0.0.1", 8096),
            ("http://localhost:8096", "localhost", 8096),
            ("http://[::1]:8096", "localhost", 8096),
            ("http://127.0.0.1:8097", "127.0.0.1", 8097),
            ("http://localhost:8097", "localhost", 8097),
            ("http://[::1]:8097", "localhost", 8097),
            ("http://127.0.0.1:8098", "127.0.0.1", 8098),
            ("http://localhost:8098", "localhost", 8098),
            ("http://[::1]:8098", "localhost", 8098),
            ("http://127.0.0.1:45722", "127.0.0.1", 45722),
            ("http://localhost:45722", "localhost", 45722),
            ("http://[::1]:45722", "localhost", 45722),
            ("http://127.0.0.1:45721", "127.0.0.1", 45721),
            ("http://localhost:45721", "localhost", 45721),
            ("http://[::1]:45721", "localhost", 45721),
            ("https://127.0.0.1:8920", "127.0.0.1", 8920),
            ("https://localhost:8920", "localhost", 8920),
            ("https://[::1]:8920", "localhost", 8920)
        ]

        let candidates = rawCandidates.compactMap { raw, host, port -> Endpoint? in
            guard let url = URL(string: raw) else { return nil }
            return Endpoint(url: url, host: host, port: port)
        }

        for candidate in candidates {
            let task = Task { [weak self] in
                guard let self else { return }
                guard let type = await self.probeMediaServer(baseURL: candidate.url, serviceName: "本机媒体服务器") else { return }
                guard !Task.isCancelled else { return }
                let displayHost = self.isLoopbackHost(candidate.host) ? "本机" : candidate.host
                self.addDevice(DiscoveredDevice(name: "\(type.rawValue) · \(displayHost)", ipAddress: candidate.host, port: candidate.port, type: type))
            }
            probeTasks.append(task)
        }
    }

    private func isLoopbackHost(_ host: String) -> Bool {
        let normalized = host.trimmingCharacters(in: CharacterSet(charactersIn: "[]")).lowercased()
        return normalized == "localhost" || normalized == "127.0.0.1" || normalized == "::1"
    }

    private func probeLANMediaServerPorts() {
        let hosts = privateIPv4SubnetHosts()
        guard !hosts.isEmpty else { return }

        struct Endpoint {
            let url: URL
            let host: String
            let port: Int
        }

        let ports = [32400, 8096, 8097, 8098, 8920, 5006]
        let endpoints: [Endpoint] = hosts.flatMap { host in
            ports.compactMap { port in
                let scheme = (port == 8920 || port == 5006) ? "https" : "http"
                guard let url = URL(string: "\(scheme)://\(host):\(port)") else { return nil }
                return Endpoint(url: url, host: host, port: port)
            }
        }

        let task = Task { [weak self] in
            guard let self else { return }
            var iterator = endpoints.makeIterator()
            let maxConcurrent = 256

            await withTaskGroup(of: Void.self) { group in
                func addNext() {
                    guard let endpoint = iterator.next() else { return }
                    group.addTask { [weak self] in
                        guard let self else { return }
                        guard let type = await self.probeMediaServer(baseURL: endpoint.url, serviceName: "局域网媒体服务器") else { return }
                        guard !Task.isCancelled else { return }
                        await MainActor.run {
                            self.addDevice(DiscoveredDevice(name: "\(type.rawValue) · \(endpoint.host)", ipAddress: endpoint.host, port: endpoint.port, type: type))
                        }
                    }
                }

                for _ in 0..<min(maxConcurrent, endpoints.count) {
                    addNext()
                }

                while await group.next() != nil {
                    if Task.isCancelled { break }
                    addNext()
                }
            }
        }
        probeTasks.append(task)
    }

    private func probeLANOmniPlayDockerPorts() {
        let hosts = privateIPv4SubnetHosts()
        guard !hosts.isEmpty else { return }

        struct Endpoint {
            let url: URL
            let host: String
            let port: Int
        }

        let endpoints = hosts.flatMap { host in
            [45722, 45721].compactMap { port -> Endpoint? in
                guard let url = URL(string: "http://\(host):\(port)") else { return nil }
                return Endpoint(url: url, host: host, port: port)
            }
        }

        let task = Task { [weak self] in
            guard let self else { return }
            var iterator = endpoints.makeIterator()
            let maxConcurrent = 256

            await withTaskGroup(of: Void.self) { group in
                func addNext() {
                    guard let endpoint = iterator.next() else { return }
                    group.addTask { [weak self] in
                        guard let self else { return }
                        guard await self.probeOmniPlayDocker(baseURL: endpoint.url) else { return }
                        guard !Task.isCancelled else { return }
                        await MainActor.run {
                            self.addDevice(DiscoveredDevice(name: "OmniPlay Docker · \(endpoint.host)", ipAddress: endpoint.host, port: endpoint.port, type: .omniplayDocker))
                        }
                    }
                }

                for _ in 0..<min(maxConcurrent, endpoints.count) {
                    addNext()
                }

                while await group.next() != nil {
                    if Task.isCancelled { break }
                    addNext()
                }
            }
        }
        probeTasks.append(task)
    }

    private func probeEmbyUdpDiscovery() {
        let task = Task { [weak self] in
            guard let self else { return }
            let devices = Self.discoverEmbyServersViaUDP()
            guard !Task.isCancelled else { return }
            for device in devices {
                guard !Task.isCancelled else { return }
                self.addDevice(device)
            }
        }
        probeTasks.append(task)
    }

    private static func discoverEmbyServersViaUDP() -> [DiscoveredDevice] {
        let socketDescriptor = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard socketDescriptor >= 0 else { return [] }
        defer { close(socketDescriptor) }

        var enabled: Int32 = 1
        setsockopt(socketDescriptor, SOL_SOCKET, SO_BROADCAST, &enabled, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(socketDescriptor, SOL_SOCKET, SO_REUSEADDR, &enabled, socklen_t(MemoryLayout<Int32>.size))

        var receiveTimeout = timeval(tv_sec: 0, tv_usec: 250_000)
        setsockopt(socketDescriptor, SOL_SOCKET, SO_RCVTIMEO, &receiveTimeout, socklen_t(MemoryLayout<timeval>.size))

        let payload = Array("who is EmbyServer?".utf8)
        for address in embyUdpBroadcastAddresses() {
            sendEmbyUdpDiscoveryPayload(socket: socketDescriptor, address: address, payload: payload)
        }

        var devices: [String: DiscoveredDevice] = [:]
        let deadline = Date().addingTimeInterval(2.2)
        while Date() < deadline {
            var storage = sockaddr_storage()
            var addressLength = socklen_t(MemoryLayout<sockaddr_storage>.size)
            var buffer = [UInt8](repeating: 0, count: 4096)
            let byteCount = buffer.withUnsafeMutableBytes { bufferPointer -> ssize_t in
                guard let baseAddress = bufferPointer.baseAddress else { return -1 }
                return withUnsafeMutablePointer(to: &storage) { storagePointer in
                    storagePointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockaddrPointer in
                        recvfrom(socketDescriptor, baseAddress, bufferPointer.count, 0, sockaddrPointer, &addressLength)
                    }
                }
            }

            guard byteCount > 0 else {
                if errno == EWOULDBLOCK || errno == EAGAIN || errno == EINTR {
                    continue
                }
                break
            }

            let data = Data(buffer.prefix(Int(byteCount)))
            let remoteHost = numericHost(from: storage, length: addressLength)
            guard let device = embyDevice(fromUDPResponse: data, remoteHost: remoteHost) else { continue }
            devices["\(device.ipAddress):\(device.port)"] = device
        }

        return Array(devices.values)
    }

    private static func sendEmbyUdpDiscoveryPayload(socket socketDescriptor: Int32, address: String, payload: [UInt8]) {
        var destination = sockaddr_in()
        destination.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        destination.sin_family = sa_family_t(AF_INET)
        destination.sin_port = in_port_t(7359).bigEndian
        guard address.withCString({ inet_pton(AF_INET, $0, &destination.sin_addr) }) == 1 else { return }

        payload.withUnsafeBytes { payloadPointer in
            guard let baseAddress = payloadPointer.baseAddress else { return }
            withUnsafePointer(to: &destination) { destinationPointer in
                destinationPointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockaddrPointer in
                    _ = sendto(socketDescriptor, baseAddress, payloadPointer.count, 0, sockaddrPointer, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
        }
    }

    private static func embyUdpBroadcastAddresses() -> [String] {
        var addresses = Set(["255.255.255.255"])
        var interfaces: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&interfaces) == 0, let first = interfaces else {
            return addresses.sorted()
        }
        defer { freeifaddrs(interfaces) }

        var pointer: UnsafeMutablePointer<ifaddrs>? = first
        while let current = pointer {
            defer { pointer = current.pointee.ifa_next }
            let flags = Int32(current.pointee.ifa_flags)
            guard flags & IFF_UP != 0, flags & IFF_LOOPBACK == 0 else { continue }
            guard let address = current.pointee.ifa_addr,
                  let netmask = current.pointee.ifa_netmask,
                  address.pointee.sa_family == UInt8(AF_INET) else { continue }

            let ip = address.withMemoryRebound(to: sockaddr_in.self, capacity: 1) {
                UInt32(bigEndian: $0.pointee.sin_addr.s_addr)
            }
            let mask = netmask.withMemoryRebound(to: sockaddr_in.self, capacity: 1) {
                UInt32(bigEndian: $0.pointee.sin_addr.s_addr)
            }
            guard mask != 0 else { continue }

            let broadcast = (ip & mask) | ~mask
            var broadcastAddress = in_addr(s_addr: broadcast.bigEndian)
            var buffer = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
            let converted = withUnsafePointer(to: &broadcastAddress) { addressPointer in
                buffer.withUnsafeMutableBufferPointer { bufferPointer in
                    inet_ntop(AF_INET, addressPointer, bufferPointer.baseAddress, socklen_t(bufferPointer.count))
                }
            }
            if converted != nil {
                addresses.insert(String(cString: buffer))
            }
        }

        return addresses.sorted()
    }

    private static func numericHost(from storage: sockaddr_storage, length: socklen_t) -> String {
        var storage = storage
        var hostname = [CChar](repeating: 0, count: Int(NI_MAXHOST))
        let result = withUnsafePointer(to: &storage) { storagePointer in
            storagePointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockaddrPointer in
                getnameinfo(sockaddrPointer, length, &hostname, socklen_t(hostname.count), nil, 0, NI_NUMERICHOST)
            }
        }
        guard result == 0 else { return "" }
        return String(cString: hostname)
    }

    private static func embyDevice(fromUDPResponse data: Data, remoteHost: String) -> DiscoveredDevice? {
        guard let object = try? JSONSerialization.jsonObject(with: data),
              let dictionary = object as? [String: Any] else { return nil }

        let serverName = stringValue(in: dictionary, for: "Name")?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""

        if let address = stringValue(in: dictionary, for: "Address")?
            .trimmingCharacters(in: .whitespacesAndNewlines),
           let url = URL(string: address),
           let scheme = url.scheme?.lowercased(),
           (scheme == "http" || scheme == "https"),
           let host = url.host?.trimmingCharacters(in: .whitespacesAndNewlines),
           !host.isEmpty,
           !host.contains(":") {
            let port = url.port ?? (scheme == "https" ? 8920 : 8096)
            let name = serverName.isEmpty ? "Emby · \(host)" : serverName
            return DiscoveredDevice(name: name, ipAddress: host, port: port, type: .emby)
        }

        let fallbackHost = remoteHost.trimmingCharacters(in: .whitespacesAndNewlines)
        let hasEmbyIdentity = stringValue(in: dictionary, for: "Id") != nil || !serverName.isEmpty
        guard hasEmbyIdentity, !fallbackHost.isEmpty, !fallbackHost.contains(":") else { return nil }
        let name = serverName.isEmpty ? "Emby · \(fallbackHost)" : serverName
        return DiscoveredDevice(name: name, ipAddress: fallbackHost, port: 8096, type: .emby)
    }

    private static func stringValue(in dictionary: [String: Any], for key: String) -> String? {
        for (candidate, value) in dictionary where candidate.caseInsensitiveCompare(key) == .orderedSame {
            return value as? String
        }
        return nil
    }

    private func privateIPv4SubnetHosts() -> [String] {
        var hosts = Set<String>()
        var interfaces: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&interfaces) == 0, let first = interfaces else { return [] }
        defer { freeifaddrs(interfaces) }

        var pointer: UnsafeMutablePointer<ifaddrs>? = first
        while let current = pointer {
            defer { pointer = current.pointee.ifa_next }
            let flags = Int32(current.pointee.ifa_flags)
            guard flags & IFF_UP != 0, flags & IFF_LOOPBACK == 0 else { continue }
            guard let address = current.pointee.ifa_addr,
                  let netmask = current.pointee.ifa_netmask,
                  address.pointee.sa_family == UInt8(AF_INET),
                  netmask.pointee.sa_family == UInt8(AF_INET) else { continue }

            let ipValue = address.withMemoryRebound(to: sockaddr_in.self, capacity: 1) {
                UInt32(bigEndian: $0.pointee.sin_addr.s_addr)
            }
            let maskValue = netmask.withMemoryRebound(to: sockaddr_in.self, capacity: 1) {
                UInt32(bigEndian: $0.pointee.sin_addr.s_addr)
            }
            let ip = ipv4String(ipValue)
            guard isPrivateIPv4(ip), maskValue != 0 else { continue }

            let network = ipValue & maskValue
            let broadcast = network | ~maskValue
            guard broadcast > network + 1 else { continue }
            let hostCount = broadcast - network - 1
            if hostCount <= 1022 {
                for value in (network + 1)..<broadcast {
                    hosts.insert(ipv4String(value))
                }
            } else {
                let local24 = ipValue & 0xFFFFFF00
                for suffix in 1...254 {
                    hosts.insert(ipv4String(local24 | UInt32(suffix)))
                }
            }
        }

        return hosts.sorted()
    }

    private func isPrivateIPv4(_ ip: String) -> Bool {
        let parts = ip.split(separator: ".").compactMap { Int($0) }
        guard parts.count == 4 else { return false }
        if parts[0] == 10 { return true }
        if parts[0] == 100 && (64...127).contains(parts[1]) { return true }
        if parts[0] == 169 && parts[1] == 254 { return true }
        if parts[0] == 192 && parts[1] == 168 { return true }
        if parts[0] == 172 && (16...31).contains(parts[1]) { return true }
        return false
    }

    private func ipv4String(_ value: UInt32) -> String {
        var address = in_addr(s_addr: value.bigEndian)
        var buffer = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
        let converted = withUnsafePointer(to: &address) { addressPointer in
            buffer.withUnsafeMutableBufferPointer { bufferPointer in
                inet_ntop(AF_INET, addressPointer, bufferPointer.baseAddress, socklen_t(bufferPointer.count))
            }
        }
        return converted == nil ? "" : String(cString: buffer)
    }

    private func detectHTTPServiceType(serviceName: String, serviceType: String, ipAddress: String, port: Int) async -> DiscoveredDevice.DeviceType? {
        if serviceType.contains("plexmediasvr") {
            return .plex
        }
        if serviceType.contains("jellyfin") {
            return .jellyfin
        }
        if serviceType.contains("emby") {
            return .emby
        }

        let isHTTPS = serviceType.contains("https") || serviceType.contains("webdavs") || port == 443 || port == 5006 || port == 8920
        let scheme = isHTTPS ? "https" : "http"
        guard let baseURL = URL(string: "\(scheme)://\(ipAddress):\(port)") else { return nil }

        if let mediaServerType = await probeMediaServer(baseURL: baseURL, serviceName: serviceName) {
            return mediaServerType
        }

        if serviceType.contains("webdav") || serviceType.contains("webdavs") {
            return isHTTPS ? .webdavHTTPS : .webdavHTTP
        }

        if await probeWebDAV(baseURL: baseURL) {
            return isHTTPS ? .webdavHTTPS : .webdavHTTP
        }

        return nil
    }

    private func probeMediaServer(baseURL: URL, serviceName: String) async -> DiscoveredDevice.DeviceType? {
        if await probeOmniPlayDocker(baseURL: baseURL) {
            return .omniplayDocker
        }

        if baseURL.port == 32400 {
            return await probePlex(baseURL: baseURL) ? .plex : nil
        }

        if let port = baseURL.port, [8096, 8097, 8098, 45722, 45721, 8920, 5006].contains(port) {
            if let embyCompatibleType = await probeEmbyCompatible(baseURL: baseURL, serviceName: serviceName) {
                return embyCompatibleType
            }

            return await probePlex(baseURL: baseURL) ? .plex : nil
        }

        if await probePlex(baseURL: baseURL) {
            return .plex
        }

        if let embyCompatibleType = await probeEmbyCompatible(baseURL: baseURL, serviceName: serviceName) {
            return embyCompatibleType
        }

        return nil
    }

    private func probeOmniPlayDocker(baseURL: URL) async -> Bool {
        for path in ["api/health", "api/auth/status"] {
            if await probeOmniPlayDockerEndpoint(baseURL: baseURL, path: path) {
                return true
            }
        }
        return false
    }

    private func probeOmniPlayDockerEndpoint(baseURL: URL, path: String) async -> Bool {
        guard var components = URLComponents(url: baseURL, resolvingAgainstBaseURL: false) else { return false }
        let basePath = components.path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        components.path = "/" + [basePath, path].filter { !$0.isEmpty }.joined(separator: "/")
        guard let url = components.url else { return false }
        var request = URLRequest(url: url)
        request.timeoutInterval = 2.5
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        do {
            let (data, response) = try await probeSession.data(for: request)
            guard let http = response as? HTTPURLResponse else { return false }
            let body = String(data: data, encoding: .utf8)?.lowercased() ?? ""
            guard (200...299).contains(http.statusCode) || [401, 403].contains(http.statusCode) else { return false }
            if path == "api/health",
               let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
               let service = object["service"] as? String,
               service.lowercased().contains("omniplay") {
                return true
            }
            if path == "api/auth/status",
               (body.contains("\"issetuprequired\"") || body.contains("\"isauthenticated\"")) {
                return true
            }
            return body.contains("omniplay.server")
                || body.contains("\"omniplay\"")
                || (body.contains("\"service\"") && body.contains("omniplay"))
                || (body.contains("\"servicename\"") && body.contains("omniplay"))
                || http.value(forHTTPHeaderField: "Server")?.lowercased().contains("kestrel") == true && body.contains("database")
        } catch {
            return false
        }
    }

    private func probePlex(baseURL: URL) async -> Bool {
        guard let url = URL(string: "identity", relativeTo: baseURL)?.absoluteURL else { return false }
        var request = URLRequest(url: url)
        request.timeoutInterval = 1.2
        request.setValue("application/xml", forHTTPHeaderField: "Accept")
        applyPlexHeaders(to: &request, token: nil)

        do {
            let (data, response) = try await probeSession.data(for: request)
            guard let http = response as? HTTPURLResponse else { return false }
            let hasPlexHeader = http.value(forHTTPHeaderField: "X-Plex-Protocol") != nil ||
                http.value(forHTTPHeaderField: "X-Plex-Content-Original-Length") != nil
            let body = String(data: data, encoding: .utf8)?.lowercased() ?? ""
            let hasPlexIdentity = body.contains("mediacontainer") && body.contains("machineidentifier")
            return (200...299).contains(http.statusCode) && (hasPlexHeader || hasPlexIdentity)
        } catch {
            return false
        }
    }

    private func probeEmbyCompatible(baseURL: URL, serviceName: String) async -> DiscoveredDevice.DeviceType? {
        guard let url = URL(string: "System/Info/Public", relativeTo: baseURL)?.absoluteURL else { return nil }
        var request = URLRequest(url: url)
        request.timeoutInterval = 1.2
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        do {
            let (data, response) = try await probeSession.data(for: request)
            guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else { return nil }
            let body = String(data: data, encoding: .utf8) ?? ""
            let lower = "\(serviceName) \(body)".lowercased()
            if lower.contains("jellyfin") {
                return .jellyfin
            }
            if lower.contains("emby") {
                return .emby
            }
            return nil
        } catch {
            return nil
        }
    }

    private func probeWebDAV(baseURL: URL) async -> Bool {
        var request = URLRequest(url: baseURL)
        request.httpMethod = "PROPFIND"
        request.setValue("0", forHTTPHeaderField: "Depth")
        request.setValue("application/xml", forHTTPHeaderField: "Content-Type")
        request.httpBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <d:propfind xmlns:d="DAV:">
          <d:prop>
            <d:resourcetype />
          </d:prop>
        </d:propfind>
        """.data(using: .utf8)

        do {
            let (data, response) = try await probeSession.data(for: request)
            guard let http = response as? HTTPURLResponse else { return false }
            if http.statusCode == 207 { return true }
            if http.value(forHTTPHeaderField: "DAV") != nil { return true }
            let body = String(data: data, encoding: .utf8)?.lowercased() ?? ""
            return body.contains("multistatus") && body.contains("dav:")
        } catch {
            return false
        }
    }

    private func applyPlexHeaders(to request: inout URLRequest, token: String?) {
        request.setValue("OmniPlay", forHTTPHeaderField: "X-Plex-Product")
        request.setValue("1.0", forHTTPHeaderField: "X-Plex-Version")
        request.setValue("omniplay-mac", forHTTPHeaderField: "X-Plex-Client-Identifier")
        request.setValue("OmniPlay", forHTTPHeaderField: "X-Plex-Device-Name")
        if let token, !token.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            request.setValue(token.trimmingCharacters(in: .whitespacesAndNewlines), forHTTPHeaderField: "X-Plex-Token")
        }
    }
}
