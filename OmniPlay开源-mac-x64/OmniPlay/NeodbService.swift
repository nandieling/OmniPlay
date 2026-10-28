import Foundation
import Security

// NeoDB 的 HTTP 客户端。
//
// 只认实例根地址（例如 https://neodb.social），路径全部按 NeoDB 主干的 `/api/...` 走；
// 账户密码登录是个例外：那是 `/v0/password_login`，官方实例挂在 https://api.neodb.ai，
// 自建实例没这个接口时就退回去试官方地址，两处都没有再把原因告诉用户。

// MARK: - 实例地址

nonisolated struct NeodbInstance: Equatable {
    static let defaultRaw = "https://neodb.social"
    static let legacyLoginRaw = "https://api.neodb.ai"

    let baseURL: URL

    init?(raw: String) {
        var value = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !value.isEmpty else { return nil }
        if !value.contains("://") { value = "https://" + value }
        guard var components = URLComponents(string: value),
              let scheme = components.scheme?.lowercased(),
              scheme == "http" || scheme == "https",
              let host = components.host, !host.isEmpty else {
            return nil
        }
        components.query = nil
        components.fragment = nil
        components.user = nil
        components.password = nil
        var path = components.path
        while path.hasSuffix("/") { path = String(path.dropLast()) }
        if path == "/api" { path = "" }
        components.path = path
        guard let url = components.url else { return nil }
        baseURL = url
    }

    var host: String { baseURL.host ?? "" }

    /// NeoDB 条目的网页地址：<实例>/<分类>/<uuid>。分类拿不到时退回本地推导的 movie / tv。
    func itemPageURL(category: String?, fallbackKind: MediaMarkKind, uuid: String) -> URL? {
        let trimmed = uuid.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        let segment = (category ?? "").trimmingCharacters(in: CharacterSet(charactersIn: "/ ")).lowercased()
        let path = segment.isEmpty ? fallbackKind.tmdbPathSegment : segment
        guard !path.isEmpty else { return nil }
        return baseURL.appendingPathComponent(path).appendingPathComponent(trimmed)
    }

    var display: String { baseURL.absoluteString }

    /// 站内 API 的根：`https://实例/api`。
    var apiURL: URL { URL(string: display + "/api") ?? baseURL }

    // appendingPathComponent 会把子路径里的 "/" 转义成 %2F，所以这里手拼后交给 URL 校验。
    func api(_ path: String) -> URL {
        URL(string: apiURL.absoluteString + "/" + path) ?? baseURL
    }

    func root(_ path: String) -> URL {
        URL(string: display + "/" + path) ?? baseURL
    }
}

// MARK: - 令牌存储

/// 令牌进钥匙串，按实例主机分键，切换实例不会互相顶掉。
nonisolated enum NeodbTokenStore {
    private static let service = "nan.omniplay.neodb.token"

    static func save(token: String, for instance: NeodbInstance) throws {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: instance.host
        ]
        let data = Data(token.utf8)
        if SecItemCopyMatching(query as CFDictionary, nil) == errSecSuccess {
            let status = SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
            guard status == errSecSuccess else { throw neodbKeychainError(status) }
        } else {
            var add = query
            add[kSecValueData as String] = data
            add[kSecAttrDescription as String] = "觅影 OmniPlay 的 NeoDB 访问令牌"
            let status = SecItemAdd(add as CFDictionary, nil)
            guard status == errSecSuccess else { throw neodbKeychainError(status) }
        }
    }

    static func token(for instance: NeodbInstance) -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: instance.host,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data else {
            return nil
        }
        let token = String(data: data, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines)
        return (token?.isEmpty == false) ? token : nil
    }

    static func remove(for instance: NeodbInstance) {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: instance.host
        ]
        _ = SecItemDelete(query as CFDictionary)
    }

    private static func neodbKeychainError(_ status: OSStatus) -> NeodbError {
        .passwordLoginFailed(reason: "钥匙串写入失败（状态码 \(status)）")
    }
}

// MARK: - OAuth

nonisolated struct NeodbOAuthApp: Codable {
    var clientID: String
    var clientSecret: String

    enum CodingKeys: String, CodingKey {
        case clientID = "client_id"
        case clientSecret = "client_secret"
    }
}

// MARK: - 客户端

nonisolated final class NeodbService: @unchecked Sendable {
    static let shared = NeodbService()

    static let oauthRedirect = "urn:ietf:wg:oauth:2.0:oob"
    static let oauthScopes = "read write"

    private let session: URLSession
    private let userAgent = "OmniPlay/1.10 (+https://github.com/nandieling/OmniPlay)"

    private init() {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 20
        config.timeoutIntervalForResource = 30
        config.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        config.urlCache = nil
        config.httpCookieStorage = nil
        config.httpShouldSetCookies = false
        session = URLSession(configuration: config)
    }

    // MARK: 登录

    /// 账户密码登录。先试用户填的实例，再试官方 https://api.neodb.ai。
    func loginWithPassword(instance: NeodbInstance, identifier: String, password: String,
                           attemptLegacyHost: Bool = true) async throws -> String {
        let trimmed = identifier.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, !password.isEmpty else {
            throw NeodbError.passwordLoginFailed(reason: "账户名和密码不能为空")
        }
        var lastReason = "站点 \(instance.display) 没有提供密码登录接口"
        do {
            return try await passwordLogin(at: instance.root("v0/password_login"), identifier: trimmed, password: password)
        } catch let error as NeodbError {
            switch error {
            case .httpStatus(404), .httpStatus(405), .invalidResponse, .network:
                lastReason = error.localizedDescription
            default:
                throw error
            }
        }
        guard attemptLegacyHost, instance.host != "api.neodb.ai" else {
            throw NeodbError.passwordLoginFailed(reason: lastReason)
        }
        guard let legacy = NeodbInstance(raw: NeodbInstance.legacyLoginRaw) else {
            throw NeodbError.passwordLoginFailed(reason: lastReason)
        }
        do {
            return try await passwordLogin(at: legacy.root("v0/password_login"), identifier: trimmed, password: password)
        } catch let error as NeodbError {
            if case .unauthorized = error {
                throw NeodbError.passwordLoginFailed(reason: "账户或密码不正确")
            }
            throw NeodbError.passwordLoginFailed(reason: "\(lastReason)；官方登录网关也不可用（\(error.localizedDescription)）")
        }
    }

    private func passwordLogin(at url: URL, identifier: String, password: String) async throws -> String {
        let payload: [String: String] = ["identifier": identifier, "password": password]
        let body = try JSONSerialization.data(withJSONObject: payload)
        let response = try await send("POST", url, token: nil, body: body, contentType: "application/json")
        guard response.status == 200 else {
            if response.status == 401 || response.status == 400 {
                throw NeodbError.passwordLoginFailed(reason: serverMessage(response.data) ?? "HTTP \(response.status)")
            }
            throw NeodbError.httpStatus(response.status)
        }
        guard let object = (try? JSONSerialization.jsonObject(with: response.data)) as? [String: Any],
              let token = (object["token"] as? String) ?? (object["access_token"] as? String),
              !token.isEmpty else {
            throw NeodbError.invalidResponse
        }
        return token
    }

    func fetchAccount(instance: NeodbInstance, token: String) async throws -> NeodbAccount {
        let response = try await send("GET", instance.api("me"), token: token)
        if response.status == 404 {
            // 站点没有站内 API（或者不是 NeoDB），给出实际请求的地址便于排查。
            throw NeodbError.invalidInstance(instance.display + "/api/me")
        }
        guard response.status == 200 else { throw mapStatus(response.status) }
        return try NeodbShelfResponse.account(from: response.data)
    }

    // MARK: OAuth 授权码（OOB）

    func registerApp(instance: NeodbInstance, clientName: String) async throws -> NeodbOAuthApp {
        let form = "client_name=\(escape(clientName))&redirect_uris=\(escape(NeodbService.oauthRedirect))&scopes=\(escape(NeodbService.oauthScopes))"
        let response = try await send("POST", instance.root("api/v1/apps"), body: Data(form.utf8),
                                      contentType: "application/x-www-form-urlencoded")
        guard response.status == 200 || response.status == 201 else { throw mapStatus(response.status) }
        guard let app = try? JSONDecoder().decode(NeodbOAuthApp.self, from: response.data) else {
            throw NeodbError.invalidResponse
        }
        return app
    }

    func authorizeURL(instance: NeodbInstance, app: NeodbOAuthApp) -> URL? {
        var components = URLComponents(url: instance.root("oauth/authorize"), resolvingAgainstBaseURL: false)
        components?.queryItems = [
            URLQueryItem(name: "response_type", value: "code"),
            URLQueryItem(name: "client_id", value: app.clientID),
            URLQueryItem(name: "redirect_uri", value: NeodbService.oauthRedirect),
            URLQueryItem(name: "scope", value: NeodbService.oauthScopes)
        ]
        return components?.url
    }

    func exchangeCode(instance: NeodbInstance, app: NeodbOAuthApp, code: String) async throws -> String {
        let form = [
            "client_id=" + escape(app.clientID),
            "client_secret=" + escape(app.clientSecret),
            "code=" + escape(code.trimmingCharacters(in: .whitespacesAndNewlines)),
            "redirect_uri=" + escape(NeodbService.oauthRedirect),
            "grant_type=authorization_code"
        ].joined(separator: "&")
        let response = try await send("POST", instance.root("oauth/token"), body: Data(form.utf8),
                                      contentType: "application/x-www-form-urlencoded")
        guard response.status == 200 else {
            if let message = serverMessage(response.data) {
                throw NeodbError.passwordLoginFailed(reason: message)
            }
            throw mapStatus(response.status)
        }
        guard let object = (try? JSONSerialization.jsonObject(with: response.data)) as? [String: Any],
              let token = object["access_token"] as? String,
              !token.isEmpty else {
            throw NeodbError.invalidResponse
        }
        return token
    }

    // MARK: 目录

    /// 用站外链定位 NeoDB 条目。202 表示服务端还在抓取，等一会儿再问，最多三次。
    func catalogItem(instance: NeodbInstance, token: String?, url: String, retries: Int = 3,
                     retryDelay: TimeInterval = 2.5) async throws -> NeodbCatalogItem {
        guard var components = URLComponents(url: instance.api("catalog/fetch"), resolvingAgainstBaseURL: false) else {
            throw NeodbError.invalidResponse
        }
        components.queryItems = [URLQueryItem(name: "url", value: url)]
        guard let requestURL = components.url else { throw NeodbError.invalidResponse }

        var attempt = 0
        while true {
            let response = try await send("GET", requestURL, token: token)
            if response.status == 202, attempt < retries {
                attempt += 1
                try? await Task.sleep(nanoseconds: UInt64(retryDelay * Double(attempt) * 1_000_000_000))
                continue
            }
            if response.status == 404 { throw NeodbError.itemNotFound(url: url) }
            guard response.status == 200 else { throw mapStatus(response.status) }
            return try NeodbShelfResponse.catalogItem(from: response.data)
        }
    }

    // MARK: 标记

    /// 读当前标记；没标记（404）返回 nil。
    func remoteMark(instance: NeodbInstance, token: String, itemUUID: String) async throws -> NeodbRemoteMark? {
        let response = try await send("GET", instance.api("me/shelf/item/\(itemUUID)"), token: token)
        if response.status == 404 { return nil }
        guard response.status == 200 else { throw mapStatus(response.status) }
        return try JSONDecoder().decode(NeodbRemoteMark.self, from: response.data)
    }

    func setMark(instance: NeodbInstance, token: String, itemUUID: String, payload: NeodbMarkPayload) async throws {
        let body = try payload.encodedBody()
        let response = try await send("POST", instance.api("me/shelf/item/\(itemUUID)"), token: token,
                                      body: body, contentType: "application/json")
        guard (200..<300).contains(response.status) else { throw mapStatus(response.status) }
    }

    func deleteMark(instance: NeodbInstance, token: String, itemUUID: String) async throws {
        let response = try await send("DELETE", instance.api("me/shelf/item/\(itemUUID)"), token: token)
        // 远端本来就没标记：删除返回 404，对同步来说算成功。
        guard response.status == 200 || response.status == 204 || response.status == 404 else {
            throw mapStatus(response.status)
        }
    }

    func setProgress(instance: NeodbInstance, token: String, itemUUID: String, percent: Int) async throws {
        guard percent > 0 else { return }
        let body = try JSONEncoder().encode(NeodbProgressPayload(percent: percent))
        let response = try await send("POST", instance.api("me/shelf/item/\(itemUUID)/progress"), token: token,
                                      body: body, contentType: "application/json")
        guard (200..<300).contains(response.status) else { throw mapStatus(response.status) }
    }

    /// 拉某个书架的一页内容。
    func shelfMarks(instance: NeodbInstance, token: String, shelf: NeodbShelfType,
                    category: MediaMarkKind, page: Int = 1) async throws -> [NeodbRemoteMark] {
        guard let shelfValue = shelf.apiValue else { return [] }
        guard var components = URLComponents(url: instance.api("me/shelf/\(shelfValue)"), resolvingAgainstBaseURL: false) else {
            throw NeodbError.invalidResponse
        }
        components.queryItems = [
            URLQueryItem(name: "category", value: category.rawValue),
            URLQueryItem(name: "page", value: String(max(1, page)))
        ]
        guard let url = components.url else { throw NeodbError.invalidResponse }
        let response = try await send("GET", url, token: token)
        guard response.status == 200 else { throw mapStatus(response.status) }
        return try NeodbShelfResponse.marks(from: response.data)
    }

    // MARK: 内部

    private struct Response {
        let status: Int
        let data: Data
    }

    private func send(_ method: String, _ url: URL, token: String? = nil, body: Data? = nil,
                      contentType: String? = nil) async throws -> Response {
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue(userAgent, forHTTPHeaderField: "User-Agent")
        if let contentType { request.setValue(contentType, forHTTPHeaderField: "Content-Type") }
        if let token, !token.isEmpty {
            request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
            // 老的 v0 接口认这个头，顺手带上，用户不必区分令牌来自哪套接口。
            request.setValue(token, forHTTPHeaderField: "X-User-Token")
        }
        request.httpBody = body

        do {
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else { throw NeodbError.invalidResponse }
            return Response(status: http.statusCode, data: data)
        } catch let error as NeodbError {
            throw error
        } catch {
            throw NeodbError.network(error.localizedDescription)
        }
    }

    private func mapStatus(_ status: Int) -> NeodbError {
        switch status {
        case 401: return .unauthorized
        case 403: return .forbidden
        default: return .httpStatus(status)
        }
    }

    private func serverMessage(_ data: Data) -> String? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        for key in ["message", "error_description", "error", "detail"] {
            if let value = object[key] as? String, !value.isEmpty { return value }
        }
        return nil
    }

    private func escape(_ value: String) -> String {
        value.addingPercentEncoding(withAllowedCharacters: NeodbService.formAllowed) ?? value
    }

    private static let formAllowed: CharacterSet = {
        var set = CharacterSet.alphanumerics
        set.insert(charactersIn: "-._~")
        return set
    }()
}
