import Foundation
import GRDB

struct LuckyStunRule: Identifiable, Hashable {
    let id: String
    let name: String
    let address: String
}

struct LuckyStunRefreshResult {
    let rules: [LuckyStunRule]
    let selectedRule: LuckyStunRule?
    let updatedSourceCount: Int
    let message: String
}

@MainActor
final class LuckyStunClient {
    static let shared = LuckyStunClient()

    private let loginPaths = ["/api/login", "/api/auth/login", "/api/user/login", "/api/auth/signin", "/login"]
    private let rulePaths = [
        "/api/stun", "/api/stun/rules", "/api/stun/list", "/api/penetration",
        "/api/penetration/rules", "/api/nat/stun", "/api/config/stun", "/api/rules", "/api/getstunlist"
    ]

    private init() {}

    func fetch(
        managementURL rawManagementURL: String,
        username: String,
        password: String,
        selectedRuleID: String,
        selectedRuleName: String
    ) async throws -> [LuckyStunRule] {
        let managementURL = try normalizeManagementURL(rawManagementURL)
        var cookies: [String] = []
        var token: String?

        if !username.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || !password.isEmpty {
            (token, cookies) = try await login(baseURL: managementURL, username: username.trimmingCharacters(in: .whitespacesAndNewlines), password: password, cookies: cookies)
        }

        var lastError = "没有识别到规则列表。"
        for path in rulePaths {
            do {
                let response = try await request(URL(string: managementURL + path)!, method: "GET", body: nil, token: token, cookies: cookies)
                guard (200..<300).contains(response.statusCode) else { lastError = "HTTP \(response.statusCode)"; continue }
                let rules = parseRules(response.data)
                if !rules.isEmpty { return rules }
                lastError = "响应中没有识别到穿透规则。"
            } catch { lastError = error.localizedDescription }
        }
        throw NSError(domain: "OmniPlayLuckyStun", code: 1, userInfo: [NSLocalizedDescriptionKey: "无法读取 Lucky STUN 规则：\(lastError)"])
    }

    func selectedRule(from rules: [LuckyStunRule], id: String, name: String) -> LuckyStunRule? {
        if !id.isEmpty, let rule = rules.first(where: { $0.id.caseInsensitiveCompare(id) == .orderedSame }) { return rule }
        if !name.isEmpty, let rule = rules.first(where: { $0.name.caseInsensitiveCompare(name) == .orderedSame }) { return rule }
        return rules.first
    }

    static func normalizeAddress(_ value: String) -> String? {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        let candidate = trimmed.hasPrefix("http://") || trimmed.hasPrefix("https://") ? trimmed : "http://\(trimmed)"
        guard let url = URL(string: candidate), let host = url.host, !host.isEmpty else { return nil }
        return candidate.trimmedTrailingSlash
    }

    private func login(baseURL: String, username: String, password: String, cookies: [String]) async throws -> (String?, [String]) {
        let body = try JSONSerialization.data(withJSONObject: ["username": username, "password": password])
        var currentCookies = cookies
        var lastStatus = 400
        for path in loginPaths {
            do {
                let response = try await request(URL(string: baseURL + path)!, method: "POST", body: body, token: nil, cookies: currentCookies)
                currentCookies = mergeCookies(currentCookies, response.cookies)
                lastStatus = response.statusCode
                guard (200..<300).contains(response.statusCode) else { continue }
                var token = findString(in: response.data, keys: ["token", "accessToken", "access_token", "jwt", "authorization"])
                if let value = token, !value.lowercased().hasPrefix("bearer ") { token = "Bearer \(value)" }
                return (token, currentCookies)
            } catch { continue }
        }
        if lastStatus == 401 || lastStatus == 403 { throw NSError(domain: "OmniPlayLuckyStun", code: 401, userInfo: [NSLocalizedDescriptionKey: "Lucky STUN 登录失败，请检查账号和密码。"]) }
        return (nil, currentCookies)
    }

    private func request(_ url: URL, method: String, body: Data?, token: String?, cookies: [String]) async throws -> (data: Data, statusCode: Int, cookies: [String]) {
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.timeoutInterval = 12
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("OmniPlay-LuckySTUN", forHTTPHeaderField: "User-Agent")
        if let token, !token.isEmpty { request.setValue(token, forHTTPHeaderField: "Authorization") }
        if !cookies.isEmpty { request.setValue(cookies.joined(separator: "; "), forHTTPHeaderField: "Cookie") }
        if let body { request.httpBody = body; request.setValue("application/json; charset=utf-8", forHTTPHeaderField: "Content-Type") }
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw NSError(domain: "OmniPlayLuckyStun", code: 2, userInfo: [NSLocalizedDescriptionKey: "Lucky STUN 返回了无效响应。"]) }
        let setCookies = (http.allHeaderFields["Set-Cookie"] as? String).map { [$0] } ?? http.allHeaderFields.reduce(into: [String]()) { result, item in if String(describing: item.key).caseInsensitiveCompare("Set-Cookie") == .orderedSame, let value = item.value as? String { result.append(value) } }
        return (data, http.statusCode, setCookies.map { String($0.split(separator: ";", maxSplits: 1).first ?? Substring($0)) })
    }

    private func parseRules(_ data: Data) -> [LuckyStunRule] {
        guard let object = try? JSONSerialization.jsonObject(with: data) else { return [] }
        var rules: [LuckyStunRule] = []
        collectRules(object, fallbackName: "Lucky STUN 规则", into: &rules)
        var seen = Set<String>()
        return rules.filter { seen.insert("\($0.id)|\($0.name)|\($0.address)".lowercased()).inserted }
    }

    private func collectRules(_ value: Any, fallbackName: String, into rules: inout [LuckyStunRule]) {
        if let array = value as? [Any] { array.forEach { collectRules($0, fallbackName: fallbackName, into: &rules) }; return }
        guard let object = value as? [String: Any] else { return }
        let id = findValue(object, keys: ["id", "ruleId", "uuid", "key", "index"]) ?? ""
        let name = findValue(object, keys: ["name", "title", "ruleName", "remark", "description"]) ?? fallbackName
        if let address = findAddress(object), let normalized = Self.normalizeAddress(address), address.contains(".") || address.contains(":") {
            rules.append(LuckyStunRule(id: id.isEmpty ? "\(name):\(normalized)" : id, name: name, address: normalized))
        }
        object.values.forEach { child in if child is [String: Any] || child is [Any] { collectRules(child, fallbackName: name, into: &rules) } }
    }

    private func findAddress(_ object: [String: Any]) -> String? {
        let keys = ["address", "url", "remoteAddress", "publicAddress", "externalAddress", "penetrationAddress", "forwardAddress", "stunAddress", "remoteUrl", "domain", "host"]
        if let value = findValue(object, keys: keys), !value.lowercased().contains("/api/") { return value }
        if let host = findValue(object, keys: ["remoteHost", "publicHost", "externalHost"]), let port = findValue(object, keys: ["remotePort", "publicPort", "externalPort", "port"]) { return "\(host):\(port)" }
        return nil
    }

    private func findValue(_ object: [String: Any], keys: [String]) -> String? {
        guard let pair = object.first(where: { pair in keys.contains { $0.caseInsensitiveCompare(pair.key) == .orderedSame } }) else { return nil }
        if let value = pair.value as? String { return value.trimmingCharacters(in: .whitespacesAndNewlines) }
        if let value = pair.value as? NSNumber { return value.stringValue }
        return nil
    }

    private func findString(in data: Data, keys: [String]) -> String? { guard let object = try? JSONSerialization.jsonObject(with: data) else { return nil }; return findString(in: object, keys: keys) }

    private func findString(in value: Any, keys: [String]) -> String? {
        if let object = value as? [String: Any] { if let direct = findValue(object, keys: keys) { return direct }; for child in object.values { if let nested = findString(in: child, keys: keys) { return nested } } }
        else if let array = value as? [Any] { for child in array { if let nested = findString(in: child, keys: keys) { return nested } } }
        return nil
    }

    private func mergeCookies(_ old: [String], _ new: [String]) -> [String] { var result = old; for cookie in new { let key = cookie.split(separator: "=", maxSplits: 1).first.map(String.init) ?? cookie; result.removeAll { $0.hasPrefix("\(key)=") }; result.append(cookie) }; return result }

    private func normalizeManagementURL(_ value: String) throws -> String {
        var candidate = value.trimmingCharacters(in: .whitespacesAndNewlines)
        if !candidate.contains("://") { candidate = "http://\(candidate)" }
        guard var components = URLComponents(string: candidate), let scheme = components.scheme?.lowercased(), ["http", "https"].contains(scheme), components.host?.isEmpty == false else { throw NSError(domain: "OmniPlayLuckyStun", code: 3, userInfo: [NSLocalizedDescriptionKey: "Lucky STUN 管理地址无效。"]) }
        if components.path.lowercased().hasSuffix("/api") { components.path = String(components.path.dropLast(4)).trimmingCharacters(in: CharacterSet(charactersIn: "/")) }
        components.query = nil; components.fragment = nil
        return (components.url?.absoluteString ?? candidate).trimmedTrailingSlash
    }
}

@MainActor
final class LuckyStunCoordinator {
    static let shared = LuckyStunCoordinator()
    static let sourceUpdatedNotification = Notification.Name("OmniPlayLuckyStunSourceUpdated")
    static let rulesUpdatedNotification = Notification.Name("OmniPlayLuckyStunRulesUpdated")
    private var automaticTask: Task<Void, Never>?
    private var isRefreshing = false
    private(set) var cachedRules: [LuckyStunRule] = []
    private init() {}

    func refresh(force: Bool = true) async -> LuckyStunRefreshResult {
        let defaults = UserDefaults.standard
        let managementURL = defaults.string(forKey: "luckyStunManagementURL") ?? ""
        let username = defaults.string(forKey: "luckyStunUsername") ?? ""
        let password = defaults.string(forKey: "luckyStunPassword") ?? ""
        let selectedID = defaults.string(forKey: "luckyStunRuleID") ?? ""
        let selectedName = defaults.string(forKey: "luckyStunRuleName") ?? ""
        guard !managementURL.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return LuckyStunRefreshResult(rules: [], selectedRule: nil, updatedSourceCount: 0, message: "请填写 Lucky STUN 管理地址。") }
        guard !isRefreshing else { return LuckyStunRefreshResult(rules: [], selectedRule: nil, updatedSourceCount: 0, message: "Lucky STUN 更新正在进行。") }
        isRefreshing = true; defer { isRefreshing = false }
        do {
            let rules = try await LuckyStunClient.shared.fetch(managementURL: managementURL, username: username, password: password, selectedRuleID: selectedID, selectedRuleName: selectedName)
            cachedRules = rules
            NotificationCenter.default.post(name: Self.rulesUpdatedNotification, object: rules)
            let selected = LuckyStunClient.shared.selectedRule(from: rules, id: selectedID, name: selectedName)
            guard let selected, let normalizedAddress = LuckyStunClient.normalizeAddress(selected.address) else { return LuckyStunRefreshResult(rules: rules, selectedRule: selected, updatedSourceCount: 0, message: "已读取规则，但没有有效的穿透地址。") }
            defaults.set(selected.id, forKey: "luckyStunRuleID"); defaults.set(selected.name, forKey: "luckyStunRuleName")
            let updatedCount = updateConfiguredSources(rules: rules, selectedRule: selected, selectedAddress: normalizedAddress)
            defaults.set(Date().timeIntervalSince1970, forKey: "luckyStunLastUpdatedAt")
            if updatedCount > 0 { NotificationCenter.default.post(name: Self.sourceUpdatedNotification, object: nil) }
            return LuckyStunRefreshResult(rules: rules, selectedRule: selected, updatedSourceCount: updatedCount, message: updatedCount > 0 ? "已更新规则“\(selected.name)”并同步 \(updatedCount) 个媒体源：\(normalizedAddress)" : "规则“\(selected.name)”检测成功，地址未变化：\(normalizedAddress)")
        } catch { return LuckyStunRefreshResult(rules: [], selectedRule: nil, updatedSourceCount: 0, message: "Lucky STUN 更新失败：\(error.localizedDescription)") }
    }

    func startAutomaticUpdates() {
        automaticTask?.cancel(); automaticTask = nil
        guard UserDefaults.standard.bool(forKey: "luckyStunAutoUpdate") else { return }
        automaticTask = Task { @MainActor [weak self] in
            while !Task.isCancelled { guard let self else { return }; let interval = max(5, UserDefaults.standard.integer(forKey: "luckyStunUpdateIntervalMinutes")); let last = UserDefaults.standard.double(forKey: "luckyStunLastUpdatedAt"); if last == 0 || Date().timeIntervalSince1970 - last >= Double(interval * 60) { _ = await self.refresh(force: false) }; try? await Task.sleep(nanoseconds: UInt64(interval) * 60 * 1_000_000_000) }
        }
    }

    private func updateConfiguredSources(rules: [LuckyStunRule], selectedRule: LuckyStunRule, selectedAddress: String) -> Int {
        guard let dbQueue = AppDatabase.shared.dbQueue else { return 0 }
        do {
            return try dbQueue.write { db in
                let current = try MediaSource.fetchAll(db, sql: "SELECT * FROM mediaSource")
                var count = 0
                for var source in current {
                    guard let protocolKind = source.protocolKind, protocolKind != .local, protocolKind != .direct else { continue }
                    var configuration = source.addressConfiguration()
                    var changed = false
                    let hasExplicitLuckyBinding = configuration.externalAddresses.contains { $0.useLuckyStun && (!$0.luckyRuleId.isEmpty || !$0.luckyRuleName.isEmpty) }
                    for index in configuration.externalAddresses.indices {
                        guard configuration.externalAddresses[index].useLuckyStun else { continue }
                        let configured = configuration.externalAddresses[index]
                        let matchedRule = rules.first { rule in
                            (!configured.luckyRuleId.isEmpty && rule.id.caseInsensitiveCompare(configured.luckyRuleId) == .orderedSame)
                                || (!configured.luckyRuleName.isEmpty && rule.name.caseInsensitiveCompare(configured.luckyRuleName) == .orderedSame)
                        }
                        guard let matchedRule, let updatedAddress = LuckyStunClient.normalizeAddress(matchedRule.address) else { continue }
                        let previousAddress = configured.address
                        if configuration.externalAddresses[index].address.caseInsensitiveCompare(updatedAddress) != .orderedSame {
                            configuration.externalAddresses[index].address = updatedAddress
                            configuration.externalAddresses[index].luckyRuleId = matchedRule.id
                            configuration.externalAddresses[index].luckyRuleName = matchedRule.name
                            changed = true
                        }
                        if source.baseUrl.caseInsensitiveCompare(previousAddress) == .orderedSame || configuration.activeAddress.caseInsensitiveCompare(previousAddress) == .orderedSame {
                            source.baseUrl = updatedAddress
                            configuration.activeAddress = updatedAddress
                            configuration.activeLabel = configuration.externalAddresses[index].displayLabel
                            changed = true
                        }
                    }
                    if protocolKind == .omniplayDocker && !hasExplicitLuckyBinding && source.baseUrl.caseInsensitiveCompare(selectedAddress) != .orderedSame {
                        let previousAddress = source.baseUrl
                        if configuration.localAddress.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { configuration.localAddress = previousAddress }
                        if !configuration.externalAddresses.contains(where: { $0.address.caseInsensitiveCompare(selectedAddress) == .orderedSame }) {
                            configuration.externalAddresses.append(MediaSourceExternalAddress(address: selectedAddress, label: selectedRule.name, useLuckyStun: true, luckyRuleId: selectedRule.id, luckyRuleName: selectedRule.name))
                        }
                        source.baseUrl = selectedAddress
                        configuration.activeAddress = selectedAddress
                        configuration.activeLabel = selectedRule.name.isEmpty ? "外网" : selectedRule.name
                        changed = true
                    }
                    if changed { source.setAddressConfiguration(configuration); try source.update(db); count += 1 }
                }
                return count
            }
        } catch { return 0 }
    }
}

private extension String {
    var trimmedTrailingSlash: String { var value = self; while value.hasSuffix("/") { value.removeLast() }; return value }
}
