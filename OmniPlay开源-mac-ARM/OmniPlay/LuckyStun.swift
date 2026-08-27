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

    private let session: URLSession

    private init() {
        session = Self.makeSession()
    }

    init(session: URLSession) {
        self.session = session
    }

    func fetch(
        managementURL rawManagementURL: String,
        username: String,
        password: String,
        selectedRuleID: String,
        selectedRuleName: String
    ) async throws -> [LuckyStunRule] {
        let managementURL = try normalizeManagementURL(rawManagementURL)
        let account = username.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !account.isEmpty, !password.isEmpty else {
            throw clientError(code: 4, message: "请填写 Lucky 管理账号和密码。")
        }

        let token = try await login(baseURL: managementURL, account: account, password: password)
        let rulesURL = requestURL("api/stunrulelist", under: managementURL)
        let response = try await request(
            rulesURL,
            method: "GET",
            body: nil,
            token: token,
            baseURL: managementURL
        )
        guard (200..<300).contains(response.statusCode) else {
            throw clientError(
                code: response.statusCode,
                message: "无法读取 Lucky STUN 规则：\(rulesURL.path) 返回 HTTP \(response.statusCode)。"
            )
        }
        guard let object = try? JSONSerialization.jsonObject(with: response.data) as? [String: Any],
              let result = integer(object["ret"]) else {
            throw clientError(code: 2, message: "Lucky STUN 返回了无法解析的规则数据。")
        }
        guard result == 0 else {
            throw clientError(code: result, message: "无法读取 Lucky STUN 规则：\(message(from: object))")
        }
        return parseRules(response.data)
    }

    func selectedRule(
        from rules: [LuckyStunRule],
        id: String,
        name: String
    ) -> LuckyStunRule? {
        if !id.isEmpty, let rule = rules.first(where: { $0.id.caseInsensitiveCompare(id) == .orderedSame }) {
            return rule
        }
        if !name.isEmpty, let rule = rules.first(where: { $0.name.caseInsensitiveCompare(name) == .orderedSame }) {
            return rule
        }
        return rules.first
    }

    static func normalizeAddress(_ value: String) -> String? {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        let candidate = trimmed.hasPrefix("http://") || trimmed.hasPrefix("https://")
            ? trimmed
            : "http://\(trimmed)"
        guard let url = URL(string: candidate), let host = url.host, !host.isEmpty else { return nil }
        var result = candidate
        while result.hasSuffix("/") { result.removeLast() }
        return result
    }

    private func login(
        baseURL: URL,
        account: String,
        password: String
    ) async throws -> String {
        let loginURL = requestURL("api/login", under: baseURL)
        let body = try JSONSerialization.data(withJSONObject: [
            "Account": account,
            "Password": password,
            "TwoFA": ""
        ])
        let response = try await request(
            loginURL,
            method: "POST",
            body: body,
            token: nil,
            baseURL: baseURL
        )
        guard (200..<300).contains(response.statusCode) else {
            throw clientError(
                code: response.statusCode,
                message: "Lucky STUN 登录失败：\(loginURL.path) 返回 HTTP \(response.statusCode)。"
            )
        }
        guard let object = try? JSONSerialization.jsonObject(with: response.data) as? [String: Any],
              let result = integer(object["ret"]) else {
            throw clientError(code: 2, message: "Lucky STUN 返回了无法解析的登录数据。")
        }
        guard result == 0 else {
            throw clientError(code: result, message: "Lucky STUN 登录失败：\(message(from: object))")
        }
        guard let token = findValue(object, keys: ["token"]), !token.isEmpty else {
            throw clientError(code: 2, message: "Lucky STUN 登录成功，但响应中没有管理令牌。")
        }
        return token
    }

    private func request(
        _ url: URL,
        method: String,
        body: Data?,
        token: String?,
        baseURL: URL
    ) async throws -> (data: Data, statusCode: Int) {
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.timeoutInterval = 12
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("Mozilla/5.0 (Macintosh; Intel Mac OS X) AppleWebKit/605.1.15", forHTTPHeaderField: "User-Agent")
        request.setValue("zh-CN,zh;q=0.9,en;q=0.8", forHTTPHeaderField: "Accept-Language")
        if let token, !token.isEmpty { request.setValue(token, forHTTPHeaderField: "Lucky-Admin-Token") }
        setBrowserOriginHeaders(on: &request, baseURL: baseURL)
        if let body {
            request.httpBody = body
            request.setValue("application/json; charset=utf-8", forHTTPHeaderField: "Content-Type")
        }
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw clientError(code: 2, message: "Lucky STUN 返回了无效响应。")
        }
        return (data, http.statusCode)
    }

    private func parseRules(_ data: Data) -> [LuckyStunRule] {
        guard let object = try? JSONSerialization.jsonObject(with: data) else { return [] }
        var rules: [LuckyStunRule] = []
        collectRules(object, fallbackName: "Lucky STUN 规则", into: &rules)
        var seen = Set<String>()
        return rules.filter { seen.insert("\($0.id)|\($0.name)|\($0.address)".lowercased()).inserted }
    }

    private func collectRules(_ value: Any, fallbackName: String, into rules: inout [LuckyStunRule]) {
        if let array = value as? [Any] {
            array.forEach { collectRules($0, fallbackName: fallbackName, into: &rules) }
            return
        }
        guard let object = value as? [String: Any] else { return }
        let id = findValue(object, keys: ["id", "ruleId", "uuid", "key", "index"]) ?? ""
        let name = findValue(object, keys: ["name", "title", "ruleName", "remark", "description"]) ?? fallbackName
        let address = findAddress(object)
        if let address, let normalized = Self.normalizeAddress(address), address.contains(".") || address.contains(":") {
            let resolvedID = id.isEmpty ? "\(name):\(normalized)" : id
            rules.append(LuckyStunRule(id: resolvedID, name: name, address: normalized))
        }
        object.values.forEach { child in
            if child is [String: Any] || child is [Any] { collectRules(child, fallbackName: name, into: &rules) }
        }
    }

    private func findAddress(_ object: [String: Any]) -> String? {
        let keys = ["address", "url", "remoteAddress", "publicAddress", "publicAddr", "externalAddress", "penetrationAddress", "forwardAddress", "stunAddress", "remoteUrl", "domain", "host"]
        if let value = findValue(object, keys: keys), !value.lowercased().contains("/api/") { return value }
        let host = findValue(object, keys: ["remoteHost", "publicHost", "externalHost"])
        let port = findValue(object, keys: ["remotePort", "publicPort", "externalPort", "port"])
        if let host, let port { return "\(host):\(port)" }
        return nil
    }

    private func findValue(_ object: [String: Any], keys: [String]) -> String? {
        guard let pair = object.first(where: { pair in keys.contains { $0.caseInsensitiveCompare(pair.key) == .orderedSame } }) else { return nil }
        if let value = pair.value as? String { return value.trimmingCharacters(in: .whitespacesAndNewlines) }
        if let value = pair.value as? NSNumber { return value.stringValue }
        return nil
    }

    private func normalizeManagementURL(_ value: String) throws -> URL {
        var candidate = value.trimmingCharacters(in: .whitespacesAndNewlines)
        if !candidate.contains("://") { candidate = "http://\(candidate)" }
        guard var components = URLComponents(string: candidate),
              let scheme = components.scheme?.lowercased(), ["http", "https"].contains(scheme),
              components.host?.isEmpty == false else {
            throw clientError(code: 3, message: "Lucky STUN 管理地址无效。")
        }
        components.query = nil
        components.fragment = nil
        var pathComponents = components.path.split(separator: "/").map(String.init)
        if pathComponents.last?.lowercased() == "api" { pathComponents.removeLast() }
        components.path = pathComponents.isEmpty ? "/" : "/\(pathComponents.joined(separator: "/"))/"
        guard let url = components.url else {
            throw clientError(code: 3, message: "Lucky STUN 管理地址无效。")
        }
        return url
    }

    private func requestURL(_ path: String, under baseURL: URL) -> URL {
        let endpoint = path.split(separator: "/").reduce(baseURL) { url, component in
            url.appendingPathComponent(String(component))
        }
        guard var components = URLComponents(url: endpoint, resolvingAgainstBaseURL: false) else { return endpoint }
        components.queryItems = [URLQueryItem(name: "_", value: requestNonce())]
        return components.url ?? endpoint
    }

    private func requestNonce(now: Date = Date()) -> String {
        let milliseconds = String(Int64(now.timeIntervalSince1970 * 1_000))
        guard milliseconds.count > 1 else { return milliseconds }
        let prefix = milliseconds.dropLast()
        let checksum = prefix.compactMap(\.wholeNumberValue).reduce(0, +) % 8
        return prefix + String(checksum)
    }

    private func setBrowserOriginHeaders(on request: inout URLRequest, baseURL: URL) {
        guard let components = URLComponents(url: baseURL, resolvingAgainstBaseURL: false),
              let scheme = components.scheme,
              let host = components.host else { return }
        let renderedHost = host.contains(":") ? "[\(host)]" : host
        let port = components.port.map { ":\($0)" } ?? ""
        request.setValue("\(scheme)://\(renderedHost)\(port)", forHTTPHeaderField: "Origin")
        request.setValue(baseURL.absoluteString, forHTTPHeaderField: "Referer")
    }

    private func integer(_ value: Any?) -> Int? {
        if let value = value as? Int { return value }
        if let value = value as? NSNumber { return value.intValue }
        if let value = value as? String { return Int(value) }
        return nil
    }

    private func message(from object: [String: Any]) -> String {
        let rawMessage = (object["msg"] as? String).flatMap { $0.isEmpty ? nil : $0 }
            ?? (object["message"] as? String).flatMap { $0.isEmpty ? nil : $0 }
            ?? "未知错误"
        switch rawMessage {
        case "IncorrectAccountOrPassword":
            return "账号或密码错误"
        case "LoginFailed":
            return "登录失败"
        case "NoPermission", "PermissionDenied":
            return "当前账号没有访问权限"
        case "TokenExpired", "InvalidToken":
            return "登录凭证已失效"
        default:
            return rawMessage
        }
    }

    private func clientError(code: Int, message: String) -> NSError {
        NSError(domain: "OmniPlayLuckyStun", code: code, userInfo: [NSLocalizedDescriptionKey: message])
    }

    private static func makeSession() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpCookieAcceptPolicy = .always
        configuration.httpShouldSetCookies = true
        configuration.timeoutIntervalForRequest = 15
        configuration.timeoutIntervalForResource = 30
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        return URLSession(configuration: configuration)
    }
}

@MainActor
final class LuckyStunCoordinator {
    static let shared = LuckyStunCoordinator()
    static let sourceUpdatedNotification = Notification.Name("OmniPlayLuckyStunSourceUpdated")
    static let rulesUpdatedNotification = Notification.Name("OmniPlayLuckyStunRulesUpdated")

    private var automaticTask: Task<Void, Never>?
    private var refreshingSourceIDs = Set<Int64>()
    private(set) var cachedRules: [LuckyStunRule] = []

    private init() {}

    func refresh(sourceID: Int64) async -> LuckyStunRefreshResult {
        guard !refreshingSourceIDs.contains(sourceID) else {
            return LuckyStunRefreshResult(rules: [], selectedRule: nil, updatedSourceCount: 0, message: "Lucky STUN 更新正在进行。")
        }
        guard let dbQueue = AppDatabase.shared.dbQueue,
              let source = try? await dbQueue.read({ db in try MediaSource.fetchOne(db, key: sourceID) }),
              let configuration = source.addressConfiguration().luckyStun else {
            return LuckyStunRefreshResult(rules: [], selectedRule: nil, updatedSourceCount: 0, message: "未找到 Lucky STUN 媒体源配置。")
        }
        refreshingSourceIDs.insert(sourceID)
        defer { refreshingSourceIDs.remove(sourceID) }
        do {
            let rules = try await LuckyStunClient.shared.fetch(
                managementURL: configuration.managementURL,
                username: configuration.username,
                password: configuration.password,
                selectedRuleID: configuration.ruleID,
                selectedRuleName: configuration.ruleName
            )
            cachedRules = rules
            NotificationCenter.default.post(name: Self.rulesUpdatedNotification, object: rules)
            let selected = LuckyStunClient.shared.selectedRule(from: rules, id: configuration.ruleID, name: configuration.ruleName)
            guard let selected, let normalizedAddress = LuckyStunClient.normalizeAddress(selected.address) else {
                return LuckyStunRefreshResult(rules: rules, selectedRule: selected, updatedSourceCount: 0, message: "已读取规则，但没有有效的穿透地址。")
            }
            let updatedAddress = Self.appendingPathSuffix(configuration.pathSuffix, to: normalizedAddress)
            let updatedCount = updateSource(
                sourceID: sourceID,
                selectedRule: selected,
                selectedAddress: updatedAddress
            )
            if updatedCount > 0 { NotificationCenter.default.post(name: Self.sourceUpdatedNotification, object: nil) }
            let message = updatedCount > 0
                ? "已更新规则“\(selected.name)”：\(normalizedAddress)"
                : "规则“\(selected.name)”检测成功，地址未变化：\(normalizedAddress)"
            return LuckyStunRefreshResult(rules: rules, selectedRule: selected, updatedSourceCount: updatedCount, message: message)
        } catch {
            return LuckyStunRefreshResult(rules: [], selectedRule: nil, updatedSourceCount: 0, message: "Lucky STUN 更新失败：\(error.localizedDescription)")
        }
    }

    func startAutomaticUpdates() {
        automaticTask?.cancel()
        automaticTask = Task { @MainActor [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                await self.refreshDueSources()
                try? await Task.sleep(nanoseconds: 5 * 60 * 1_000_000_000)
            }
        }
    }

    private func refreshDueSources() async {
        guard let dbQueue = AppDatabase.shared.dbQueue,
              let sources = try? await dbQueue.read({ db in try MediaSource.fetchAll(db, sql: "SELECT * FROM mediaSource WHERE isEnabled = 1") }) else { return }
        let now = Date().timeIntervalSince1970
        for source in sources {
            guard let sourceID = source.id,
                  let configuration = source.addressConfiguration().luckyStun,
                  configuration.autoUpdate else { continue }
            let interval = max(5, configuration.updateIntervalMinutes)
            if configuration.lastUpdatedAt == 0 || now - configuration.lastUpdatedAt >= Double(interval * 60) {
                _ = await refresh(sourceID: sourceID)
            }
        }
    }

    private func updateSource(sourceID: Int64, selectedRule: LuckyStunRule, selectedAddress: String) -> Int {
        guard let dbQueue = AppDatabase.shared.dbQueue else { return 0 }
        do {
            return try dbQueue.write { db in
                guard var source = try MediaSource.fetchOne(db, key: sourceID) else { return 0 }
                var addressConfiguration = source.addressConfiguration()
                guard var luckyConfiguration = addressConfiguration.luckyStun else { return 0 }
                let addressChanged = source.baseUrl.caseInsensitiveCompare(selectedAddress) != .orderedSame
                luckyConfiguration.ruleID = selectedRule.id
                luckyConfiguration.ruleName = selectedRule.name
                luckyConfiguration.lastUpdatedAt = Date().timeIntervalSince1970
                addressConfiguration.luckyStun = luckyConfiguration
                addressConfiguration.localAddress = selectedAddress
                addressConfiguration.localLabel = ""
                addressConfiguration.activeAddress = selectedAddress
                addressConfiguration.activeLabel = ""
                source.baseUrl = selectedAddress
                source.setAddressConfiguration(addressConfiguration)
                try source.update(db)
                return addressChanged ? 1 : 0
            }
        } catch { return 0 }
    }

    private static func appendingPathSuffix(_ suffix: String, to address: String) -> String {
        let trimmedSuffix = suffix.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        guard !trimmedSuffix.isEmpty, var components = URLComponents(string: address) else { return address }
        let basePath = components.path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        components.path = "/" + [basePath, trimmedSuffix].filter { !$0.isEmpty }.joined(separator: "/")
        var result = components.url?.absoluteString ?? address
        while result.hasSuffix("/") { result.removeLast() }
        return result
    }
}

private extension String {
    var trimmedTrailingSlash: String {
        var value = self
        while value.hasSuffix("/") { value.removeLast() }
        return value
    }
}
