import Foundation
import Network
import SwiftUI
import Combine

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
    }
}

// 🌟 核心雷达扫描器
class LANScanner: NSObject, ObservableObject, NetServiceBrowserDelegate, NetServiceDelegate {
    @Published var discoveredDevices: [DiscoveredDevice] = []
    @Published var isScanning = false
    
    private var browsers: [NetServiceBrowser] = []
    private var resolvingServices: [NetService] = []
    private var scanTimer: Timer?
    
    func startScanning() {
        stopScanning()
        isScanning = true
        discoveredDevices = []
        
        let serviceTypes = ["_smb._tcp.", "_webdav._tcp.", "_webdavs._tcp.", "_http._tcp.", "_https._tcp."]
        
        for type in serviceTypes {
            let browser = NetServiceBrowser()
            browser.delegate = self
            browser.searchForServices(ofType: type, inDomain: "local.")
            browsers.append(browser)
        }
        
        scanTimer = Timer.scheduledTimer(withTimeInterval: 8.0, repeats: false) { [weak self] _ in
            self?.stopScanning()
        }
    }
    
    func stopScanning() {
        scanTimer?.invalidate()
        scanTimer = nil
        for browser in browsers { browser.stop() }
        browsers.removeAll()
        resolvingServices.removeAll()
        isScanning = false
    }
    
    func netServiceBrowser(_ browser: NetServiceBrowser, didFind service: NetService, moreComing: Bool) {
        resolvingServices.append(service)
        service.delegate = self
        service.resolve(withTimeout: 5.0)
    }
    
    func netServiceDidResolveAddress(_ sender: NetService) {
        guard let addresses = sender.addresses, let firstAddress = addresses.first else { return }
        let ipString = getIPAddress(from: firstAddress)
        guard !ipString.isEmpty else { return }
        
        // 🌟 核心修复 1：IPv6 杀手！只要带冒号，统统拦截，只留纯正的 IPv4 (如 192.168.0.100)
        guard !ipString.contains(":") else { return }
        
        let type: DiscoveredDevice.DeviceType
        if sender.type.contains("smb") {
            type = .smb
        } else if sender.type.contains("https") || sender.type.contains("webdavs") || sender.port == 5006 {
            type = .webdavHTTPS
        } else {
            type = .webdavHTTP
        }
        
        let device = DiscoveredDevice(name: sender.name, ipAddress: ipString, port: sender.port, type: type)
        
        DispatchQueue.main.async {
            if !self.discoveredDevices.contains(where: { $0.ipAddress == device.ipAddress && $0.port == device.port }) {
                if device.name != Host.current().localizedName {
                    self.discoveredDevices.append(device)
                }
            }
        }
    }
    
    private func getIPAddress(from addressData: Data) -> String {
        var hostname = [CChar](repeating: 0, count: Int(NI_MAXHOST))
        addressData.withUnsafeBytes { pointer in
            guard let sockaddrPtr = pointer.bindMemory(to: sockaddr.self).baseAddress else { return }
            getnameinfo(sockaddrPtr, socklen_t(addressData.count), &hostname, socklen_t(hostname.count), nil, 0, NI_NUMERICHOST)
        }
        return String(cString: hostname)
    }
}
