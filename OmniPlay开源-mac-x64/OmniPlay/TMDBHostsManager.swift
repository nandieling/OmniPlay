import Foundation
import SwiftUI
import Combine
import Darwin

struct TMDBHostCandidate: Identifiable, Hashable {
    let id = UUID()
    let ip: String
    let httpCode: Int
    let connectTime: Double
    
    var isAvailable: Bool { httpCode == 200 }
    var displayText: String {
        if isAvailable {
            return "\(ip)  (\(String(format: "%.2f", connectTime))s)"
        }
        return "\(ip)  (HTTP \(httpCode))"
    }
}

@MainActor
final class TMDBHostsManager: ObservableObject {
    static let shared = TMDBHostsManager()
    
    @Published var isProbing = false
    @Published var isApplying = false
    @Published var candidates: [TMDBHostCandidate] = []
    @Published var message = ""
    @Published var messageColor: Color = .secondary
    @Published var currentManagedIP: String? = nil
    
    private let domain = "api.themoviedb.org"
    private let hostsPath = "/etc/hosts"
    private let backupPath = "/etc/hosts.omniplay.bak"
    private let managedBegin = "# OmniPlay TMDB Hosts BEGIN"
    private let managedEnd = "# OmniPlay TMDB Hosts END"
    private var fallbackApiKey: String { TMDBAPIConfig.publicApiKey }
    
    private init() {
        refreshCurrentManagedIP()
    }
    
    func detectAvailableIPs(apiKey: String) async {
        isProbing = true
        message = "正在检测可用 IP..."
        messageColor = .secondary
        defer { isProbing = false }
        
        let resolved = resolveIPv4Addresses(host: domain)
        let persisted = UserDefaults.standard.stringArray(forKey: "tmdbKnownIPs") ?? []
        let seed = Array(Set(resolved + persisted)).sorted()
        
        guard !seed.isEmpty else {
            candidates = []
            message = "未解析到可检测 IP。建议直接使用代理。"
            messageColor = .red
            return
        }
        
        let key = apiKey.isEmpty ? fallbackApiKey : apiKey
        var tmp: [TMDBHostCandidate] = []
        for ip in seed {
            if Task.isCancelled { return }
            if let tested = await probe(ip: ip, apiKey: key) {
                tmp.append(tested)
            }
        }
        
        let sorted = tmp.sorted {
            if $0.isAvailable != $1.isAvailable { return $0.isAvailable && !$1.isAvailable }
            return $0.connectTime < $1.connectTime
        }
        candidates = sorted
        
        let available = sorted.filter(\.isAvailable)
        if let best = available.first {
            message = "检测完成：找到 \(available.count) 个可用 IP，推荐 \(best.ip)。"
            messageColor = .green
            UserDefaults.standard.set(Array(Set(persisted + available.map(\.ip))).sorted(), forKey: "tmdbKnownIPs")
        } else {
            message = "检测完成：未找到可用 IP。更建议配置代理。"
            messageColor = .red
        }
    }
    
    func applyHosts(ip: String) async {
        isApplying = true
        message = "正在写入 Hosts（将请求管理员权限）..."
        messageColor = .secondary
        defer { isApplying = false }
        
        let shell = [
            "set -e",
            "if [ ! -f '\(backupPath)' ]; then cp '\(hostsPath)' '\(backupPath)'; fi",
            "sed '/\(managedBegin)/,/\(managedEnd)/d' '\(hostsPath)' > /tmp/omniplay_hosts_tmp",
            "printf '\\n\(managedBegin)\\n\(ip) \(domain)\\n\(managedEnd)\\n' >> /tmp/omniplay_hosts_tmp",
            "cp /tmp/omniplay_hosts_tmp '\(hostsPath)'",
            "rm -f /tmp/omniplay_hosts_tmp",
            "dscacheutil -flushcache || true",
            "killall -HUP mDNSResponder || true"
        ].joined(separator: "; ")
        
        let ok = await runPrivilegedShell(shell)
        if ok {
            refreshCurrentManagedIP()
            message = "Hosts 已更新为 \(ip)。若仍不稳定，建议改用代理。"
            messageColor = .green
        } else {
            message = "Hosts 写入失败或被取消。"
            messageColor = .red
        }
    }
    
    func rollbackHosts() async {
        isApplying = true
        message = "正在回滚 Hosts（将请求管理员权限）..."
        messageColor = .secondary
        defer { isApplying = false }
        
        let shell = [
            "set -e",
            "if [ -f '\(backupPath)' ]; then cp '\(backupPath)' '\(hostsPath)'; else sed '/\(managedBegin)/,/\(managedEnd)/d' '\(hostsPath)' > /tmp/omniplay_hosts_tmp && cp /tmp/omniplay_hosts_tmp '\(hostsPath)' && rm -f /tmp/omniplay_hosts_tmp; fi",
            "dscacheutil -flushcache || true",
            "killall -HUP mDNSResponder || true"
        ].joined(separator: "; ")
        
        let ok = await runPrivilegedShell(shell)
        if ok {
            refreshCurrentManagedIP()
            message = "Hosts 已回滚。若网络受限，建议优先使用代理。"
            messageColor = .green
        } else {
            message = "Hosts 回滚失败或被取消。"
            messageColor = .red
        }
    }
    
    func testCurrentConnectivity(apiKey: String) async {
        message = "正在测试 TMDB 连通性..."
        messageColor = .secondary
        let key = apiKey.isEmpty ? fallbackApiKey : apiKey
        let url = "https://\(domain)/3/configuration?api_key=\(key)"
        let result = await runProcess(launchPath: "/usr/bin/curl", arguments: ["-m", "8", "-sS", "-o", "/dev/null", "-w", "%{http_code}", url])
        if result.exitCode == 0, let code = Int(result.stdout.trimmingCharacters(in: .whitespacesAndNewlines)), code == 200 {
            message = "TMDB 连通性正常（HTTP 200）。"
            messageColor = .green
        } else {
            message = "TMDB 连通性失败（\(result.stdout.isEmpty ? result.stderr : result.stdout)）。建议配置代理。"
            messageColor = .red
        }
    }
    
    func refreshCurrentManagedIP() {
        guard let text = try? String(contentsOfFile: hostsPath, encoding: .utf8) else {
            currentManagedIP = nil
            return
        }
        guard
            let beginRange = text.range(of: managedBegin),
            let endRange = text.range(of: managedEnd),
            beginRange.lowerBound < endRange.lowerBound
        else {
            currentManagedIP = nil
            return
        }
        let block = String(text[beginRange.upperBound..<endRange.lowerBound])
        let line = block
            .split(separator: "\n")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .first { !$0.isEmpty && !$0.hasPrefix("#") && $0.contains(domain) }
        if let line {
            currentManagedIP = line.components(separatedBy: .whitespaces).first
        } else {
            currentManagedIP = nil
        }
    }
    
    private func resolveIPv4Addresses(host: String) -> [String] {
        var hints = addrinfo(
            ai_flags: AI_ADDRCONFIG,
            ai_family: AF_INET,
            ai_socktype: SOCK_STREAM,
            ai_protocol: IPPROTO_TCP,
            ai_addrlen: 0,
            ai_canonname: nil,
            ai_addr: nil,
            ai_next: nil
        )
        var result: UnsafeMutablePointer<addrinfo>?
        let status = getaddrinfo(host, nil, &hints, &result)
        guard status == 0, let first = result else { return [] }
        defer { freeaddrinfo(first) }
        
        var ips: [String] = []
        var ptr: UnsafeMutablePointer<addrinfo>? = first
        while let node = ptr {
            if node.pointee.ai_family == AF_INET,
               let addr = node.pointee.ai_addr?.withMemoryRebound(to: sockaddr_in.self, capacity: 1, { $0 }) {
                var sin = addr.pointee.sin_addr
                var buffer = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
                if inet_ntop(AF_INET, &sin, &buffer, socklen_t(INET_ADDRSTRLEN)) != nil {
                    let ip = String(cString: buffer)
                    if !ip.isEmpty { ips.append(ip) }
                }
            }
            ptr = node.pointee.ai_next
        }
        return Array(Set(ips)).sorted()
    }
    
    private func probe(ip: String, apiKey: String) async -> TMDBHostCandidate? {
        let target = "https://\(domain)/3/configuration?api_key=\(apiKey)"
        let args = ["-m", "6", "-sS", "-o", "/dev/null", "-w", "%{http_code} %{time_connect}", "--resolve", "\(domain):443:\(ip)", target]
        let result = await runProcess(launchPath: "/usr/bin/curl", arguments: args)
        guard result.exitCode == 0 else {
            return TMDBHostCandidate(ip: ip, httpCode: 0, connectTime: 99)
        }
        let parts = result.stdout.trimmingCharacters(in: .whitespacesAndNewlines).split(separator: " ")
        let code = Int(parts.first ?? "0") ?? 0
        let time = Double(parts.dropFirst().first ?? "99") ?? 99
        return TMDBHostCandidate(ip: ip, httpCode: code, connectTime: time)
    }
    
    private func runPrivilegedShell(_ command: String) async -> Bool {
        let escaped = command
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
        let script = "do shell script \"\(escaped)\" with administrator privileges"
        let result = await runProcess(launchPath: "/usr/bin/osascript", arguments: ["-e", script])
        return result.exitCode == 0
    }
    
    private func runProcess(launchPath: String, arguments: [String]) async -> (exitCode: Int32, stdout: String, stderr: String) {
        await withCheckedContinuation { continuation in
            let process = Process()
            process.executableURL = URL(fileURLWithPath: launchPath)
            process.arguments = arguments
            
            let out = Pipe()
            let err = Pipe()
            process.standardOutput = out
            process.standardError = err
            
            process.terminationHandler = { p in
                let outData = out.fileHandleForReading.readDataToEndOfFile()
                let errData = err.fileHandleForReading.readDataToEndOfFile()
                let outText = String(data: outData, encoding: .utf8) ?? ""
                let errText = String(data: errData, encoding: .utf8) ?? ""
                continuation.resume(returning: (p.terminationStatus, outText, errText))
            }
            
            do {
                try process.run()
            } catch {
                continuation.resume(returning: (1, "", error.localizedDescription))
            }
        }
    }
}
