import Foundation
import GRDB
import LocalAuthentication
import Security

enum MediaSourceProtocol: String, Codable, CaseIterable {
    case local
    case webdav
    case direct

    nonisolated func normalizedBaseURL(_ value: String) -> String {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return trimmed }

        switch self {
        case .local:
            if trimmed == "/" { return trimmed }
            var value = trimmed
            while value.count > 1 && value.hasSuffix("/") {
                value.removeLast()
            }
            return value
        case .webdav:
            guard let url = URL(string: trimmed) else { return trimmed }
            var normalized = url.absoluteString
            if normalized.hasSuffix("/") {
                normalized.removeLast()
            }
            return normalized
        case .direct:
            return "/"
        }
    }

    nonisolated func isValidBaseURL(_ value: String) -> Bool {
        let normalized = normalizedBaseURL(value)
        switch self {
        case .local:
            return !normalized.isEmpty
        case .webdav:
            guard let url = URL(string: normalized), let scheme = url.scheme?.lowercased() else { return false }
            return (scheme == "http" || scheme == "https") && (url.host?.isEmpty == false)
        case .direct:
            return normalized == "/"
        }
    }

    nonisolated func webDAVPathValidationError(_ value: String) -> String? {
        guard self == .webdav else { return nil }
        let normalized = normalizedBaseURL(value)
        guard let url = URL(string: normalized), let host = url.host, !host.isEmpty else {
            return "WebDAV 地址无效，请输入 http(s):// 开头且包含主机名的地址。"
        }
        let segments = url.path.split(separator: "/").map(String.init)
        if segments.isEmpty {
            return "请填写 NAS 里的具体媒体文件夹路径，例如 /电影 或 /dav/Movies。"
        }
        if segments.count == 1 {
            let root = segments[0].lowercased()
            if root == "dav" || root == "webdav" {
                return "当前地址看起来是 WebDAV 服务根目录，请继续指定媒体文件夹，例如 /dav/Movies。"
            }
        }
        return nil
    }
}

// 1. 媒体源
struct MediaSource: Codable, FetchableRecord, PersistableRecord {
    var id: Int64?
    var name: String
    var protocolType: String
    var baseUrl: String
    var authConfig: String?
    var isEnabled: Bool = true
    var disabledAt: Double?

    nonisolated var protocolKind: MediaSourceProtocol? {
        MediaSourceProtocol(rawValue: protocolType)
    }

    nonisolated func normalizedBaseURL() -> String {
        guard let kind = protocolKind else { return baseUrl }
        return kind.normalizedBaseURL(baseUrl)
    }

    nonisolated func isValidConfiguration() -> Bool {
        guard let kind = protocolKind else { return false }
        return kind.isValidBaseURL(baseUrl)
    }

    nonisolated func displayBaseURL() -> String {
        guard protocolKind == .webdav else { return baseUrl }
        let normalized = normalizedBaseURL()
        guard var components = URLComponents(string: normalized) else {
            return normalized.removingPercentEncoding ?? normalized
        }

        // UI 展示层不显示凭据，避免泄露。
        components.user = nil
        components.password = nil

        let decodedPath = components.percentEncodedPath.removingPercentEncoding ?? components.percentEncodedPath
        components.percentEncodedPath = decodedPath.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? components.percentEncodedPath

        guard var display = components.string else {
            return normalized.removingPercentEncoding ?? normalized
        }
        if let range = display.range(of: components.percentEncodedPath) {
            display.replaceSubrange(range, with: decodedPath)
        }
        return display
    }

    nonisolated var disabledDate: Date? {
        disabledAt.map { Date(timeIntervalSince1970: $0) }
    }
}

final class WebDAVCredentialStore {
    static let shared = WebDAVCredentialStore()

    struct Credential {
        let username: String
        let password: String
    }

    private let service = "nan.omniplay.webdav.credential"
    private let prefix = "keychain:webdav:"
    private let accessPrompt = "\"觅影\" 需要读取保存的 WebDAV 登录信息以自动填充并连接 WebDAV。"

    private init() {}

    private func makeAuthenticationContext() -> LAContext {
        let context = LAContext()
        context.localizedReason = accessPrompt
        return context
    }

    func authReference(for credentialID: String) -> String {
        "\(prefix)\(credentialID)"
    }

    func credentialID(from authConfig: String?) -> String? {
        guard let authConfig else { return nil }
        let trimmed = authConfig.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.hasPrefix(prefix) else { return nil }
        let id = String(trimmed.dropFirst(prefix.count))
        return id.isEmpty ? nil : id
    }

    func decodeLegacyCredential(from authConfig: String?) -> Credential? {
        guard let authConfig else { return nil }
        let trimmed = authConfig.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }

        if let data = trimmed.data(using: .utf8),
           let object = try? JSONSerialization.jsonObject(with: data),
           let dict = object as? [String: Any] {
            let username = (dict["username"] as? String) ?? (dict["user"] as? String) ?? (dict["account"] as? String)
            let password = (dict["password"] as? String) ?? (dict["pass"] as? String) ?? (dict["pwd"] as? String) ?? ""
            if let username, !username.isEmpty {
                return Credential(username: username, password: password)
            }
        }

        if let colon = trimmed.firstIndex(of: ":") {
            let user = String(trimmed[..<colon]).trimmingCharacters(in: .whitespacesAndNewlines)
            let pass = String(trimmed[trimmed.index(after: colon)...]).trimmingCharacters(in: .whitespacesAndNewlines)
            if !user.isEmpty {
                return Credential(username: user, password: pass)
            }
        }
        return nil
    }

    func saveCredential(username: String, password: String, credentialID: String = UUID().uuidString) throws -> String {
        let credential = Credential(username: username, password: password)
        let data = try serialize(credential: credential)

        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: credentialID
        ]

        let attributes: [String: Any] = [
            kSecValueData as String: data
        ]

        let status: OSStatus
        if existsCredential(id: credentialID) {
            status = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        } else {
            var addQuery = query
            addQuery[kSecValueData as String] = data
            status = SecItemAdd(addQuery as CFDictionary, nil)
        }

        guard status == errSecSuccess else {
            throw NSError(
                domain: "WebDAVCredentialStore",
                code: Int(status),
                userInfo: [NSLocalizedDescriptionKey: "Keychain 保存失败，状态码：\(status)"]
            )
        }
        return credentialID
    }

    func loadCredential(id: String) -> Credential? {
        let authContext = makeAuthenticationContext()
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: id,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
            kSecUseAuthenticationContext as String: authContext
        ]

        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        guard status == errSecSuccess,
              let data = result as? Data,
              let object = try? JSONSerialization.jsonObject(with: data),
              let dict = object as? [String: String],
              let username = dict["username"] else {
            return nil
        }
        let password = dict["password"] ?? ""
        return Credential(username: username, password: password)
    }

    func removeCredential(id: String) {
        let authContext = makeAuthenticationContext()
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: id,
            kSecUseAuthenticationContext as String: authContext
        ]
        _ = SecItemDelete(query as CFDictionary)
    }

    private func serialize(credential: Credential) throws -> Data {
        let payload: [String: String] = [
            "username": credential.username,
            "password": credential.password
        ]
        return try JSONSerialization.data(withJSONObject: payload, options: [])
    }

    private func existsCredential(id: String) -> Bool {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: id,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]
        let status = SecItemCopyMatching(query as CFDictionary, nil)
        return status == errSecSuccess
    }
}

// 2. 电影元数据 (TMDB)
struct Movie: Codable, FetchableRecord, PersistableRecord {
    var id: Int64?
    var title: String
    var releaseDate: String?
    var overview: String?
    var posterPath: String?
    var voteAverage: Double?  // 🌟 新增：TMDB 官方评分
    var isLocked: Bool = false
    
    static let videoFiles = hasMany(VideoFile.self)
}

// 3. 剧集元数据
struct TVShow: Codable, FetchableRecord, PersistableRecord {
    var id: Int64
    var title: String
    var posterPath: String?
    var voteAverage: Double?  // 🌟 新增：TMDB 官方评分
    var isLocked: Bool = false
}

// 4. 视频文件
struct VideoFile: Codable, FetchableRecord, PersistableRecord {
    var id: String
    var sourceId: Int64
    var relativePath: String
    var fileName: String
    var mediaType: String
    var movieId: Int64?
    var episodeId: Int64?
    
    // 🌟 记录播放进度和总时长
    var playProgress: Double
    var duration: Double = 0.0
    
    static let mediaSource = belongsTo(MediaSource.self)
}

nonisolated private func trimmingLeadingPathSeparators(_ value: String) -> String {
    var result = value.trimmingCharacters(in: .whitespacesAndNewlines)
    while result.hasPrefix("/") {
        result.removeFirst()
    }
    return result
}

nonisolated private func trimmingTrailingPathSeparators(_ value: String) -> String {
    var result = value.trimmingCharacters(in: .whitespacesAndNewlines)
    while result.count > 1 && result.hasSuffix("/") {
        result.removeLast()
    }
    return result
}

nonisolated private func joinDisplayPath(base: String, relative: String) -> String {
    let cleanRelative = trimmingLeadingPathSeparators(relative)
    guard !cleanRelative.isEmpty else { return base }

    let cleanBase = trimmingTrailingPathSeparators(base)
    if cleanBase == "/" {
        return "/" + cleanRelative
    }
    if cleanBase.isEmpty {
        return cleanRelative
    }
    return cleanBase + "/" + cleanRelative
}

private enum LibraryVisibilitySQL {
    static let enabledSourcePredicate = "COALESCE(mediaSource.isEnabled, 1) = 1"
}

extension MediaSource {
    static func fetchManageableSources(in db: Database) throws -> [MediaSource] {
        try MediaSource.fetchAll(
            db,
            sql: """
            SELECT *
            FROM mediaSource
            WHERE protocolType IN (?, ?)
            ORDER BY id DESC
            """,
            arguments: [MediaSourceProtocol.local.rawValue, MediaSourceProtocol.webdav.rawValue]
        )
    }

    static func fetchEnabledScannableSources(in db: Database) throws -> [MediaSource] {
        try MediaSource.fetchAll(
            db,
            sql: """
            SELECT *
            FROM mediaSource
            WHERE protocolType IN (?, ?)
              AND COALESCE(isEnabled, 1) = 1
            ORDER BY id ASC
            """,
            arguments: [MediaSourceProtocol.local.rawValue, MediaSourceProtocol.webdav.rawValue]
        )
    }

    static func fetchEnabledScannableSource(id: Int64, in db: Database) throws -> MediaSource? {
        try MediaSource.fetchOne(
            db,
            sql: """
            SELECT *
            FROM mediaSource
            WHERE id = ?
              AND protocolType IN (?, ?)
              AND COALESCE(isEnabled, 1) = 1
            LIMIT 1
            """,
            arguments: [id, MediaSourceProtocol.local.rawValue, MediaSourceProtocol.webdav.rawValue]
        )
    }
}

extension Movie {
    static func fetchVisibleLibrary(in db: Database) throws -> [Movie] {
        try Movie.fetchAll(
            db,
            sql: """
            SELECT DISTINCT movie.*
            FROM movie
            JOIN videoFile ON videoFile.movieId = movie.id
            JOIN mediaSource ON mediaSource.id = videoFile.sourceId
            WHERE videoFile.mediaType != 'direct'
              AND \(LibraryVisibilitySQL.enabledSourcePredicate)
            """
        )
    }

    static func fetchVisibleContinueWatching(in db: Database) throws -> [Movie] {
        try Movie.fetchAll(
            db,
            sql: """
            SELECT DISTINCT movie.*
            FROM movie
            JOIN videoFile ON videoFile.movieId = movie.id
            JOIN mediaSource ON mediaSource.id = videoFile.sourceId
            WHERE \(LibraryVisibilitySQL.enabledSourcePredicate)
              AND videoFile.playProgress > 5
              AND (videoFile.duration = 0 OR (videoFile.playProgress / videoFile.duration) < 0.95)
            """
        )
    }
}

extension VideoFile {
    nonisolated func displayPath(mediaSource source: MediaSource?) -> String {
        let relative = relativePath.trimmingCharacters(in: .whitespacesAndNewlines)
        let fallback = relative.isEmpty ? fileName : relative
        guard let source, source.protocolKind != .direct else {
            return fallback
        }
        return joinDisplayPath(base: source.displayBaseURL(), relative: fallback)
    }

    nonisolated func displayDirectoryPath(mediaSource source: MediaSource?) -> String {
        let relative = relativePath.trimmingCharacters(in: .whitespacesAndNewlines)
        let fallback = relative.isEmpty ? fileName : relative
        let relativeDirectory = (fallback as NSString).deletingLastPathComponent

        guard let source, source.protocolKind != .direct else {
            return relativeDirectory.isEmpty ? "未知目录" : relativeDirectory
        }
        if relativeDirectory.isEmpty {
            return source.displayBaseURL()
        }
        return joinDisplayPath(base: source.displayBaseURL(), relative: relativeDirectory)
    }

    static func fetchVisibleFiles(movieId: Int64?, in db: Database) throws -> [VideoFile] {
        guard let movieId else { return [] }
        return try VideoFile.fetchAll(
            db,
            sql: """
            SELECT videoFile.*
            FROM videoFile
            JOIN mediaSource ON mediaSource.id = videoFile.sourceId
            WHERE videoFile.movieId = ?
              AND \(LibraryVisibilitySQL.enabledSourcePredicate)
            """,
            arguments: [movieId]
        )
    }

    static func fetchVisibleFirstFile(movieId: Int64?, in db: Database) throws -> VideoFile? {
        guard let movieId else { return nil }
        return try VideoFile.fetchOne(
            db,
            sql: """
            SELECT videoFile.*
            FROM videoFile
            JOIN mediaSource ON mediaSource.id = videoFile.sourceId
            WHERE videoFile.movieId = ?
              AND \(LibraryVisibilitySQL.enabledSourcePredicate)
            ORDER BY videoFile.id ASC
            LIMIT 1
            """,
            arguments: [movieId]
        )
    }

    static func fetchVisibleSourcePairs(movieId: Int64?, in db: Database) throws -> [(VideoFile, MediaSource?)] {
        let files = try fetchVisibleFiles(movieId: movieId, in: db)
        return try files.map { file in
            (file, try file.request(for: VideoFile.mediaSource).fetchOne(db))
        }
    }

    static func fetchAllVisible(in db: Database) throws -> [VideoFile] {
        try VideoFile.fetchAll(
            db,
            sql: """
            SELECT videoFile.*
            FROM videoFile
            JOIN mediaSource ON mediaSource.id = videoFile.sourceId
            WHERE \(LibraryVisibilitySQL.enabledSourcePredicate)
            """
        )
    }

    static func fetchVisibleUnmatched(in db: Database) throws -> [VideoFile] {
        try VideoFile.fetchAll(
            db,
            sql: """
            SELECT videoFile.*
            FROM videoFile
            JOIN mediaSource ON mediaSource.id = videoFile.sourceId
            WHERE videoFile.mediaType = 'unmatched'
              AND \(LibraryVisibilitySQL.enabledSourcePredicate)
            """
        )
    }
}
