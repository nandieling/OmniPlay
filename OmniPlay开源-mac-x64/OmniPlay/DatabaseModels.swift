import Foundation
import GRDB
import LocalAuthentication
import Security

enum MediaSourceProtocol: String, Codable, CaseIterable {
    case local
    case webdav
    case direct
    case plex
    case emby
    case jellyfin
    case omniplayDocker

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
        case .webdav, .plex, .emby, .jellyfin, .omniplayDocker:
            // Accept the forms commonly copied from a NAS UI, including a
            // bare host/port and an API URL, while retaining a reverse-proxy
            // prefix such as `/omniplay`.
            var candidate = trimmed
            if !candidate.contains("://") {
                candidate = "http://\(candidate)"
            }
            guard let url = URL(string: candidate),
                  var components = URLComponents(url: url, resolvingAgainstBaseURL: false),
                  let scheme = components.scheme?.lowercased(),
                  scheme == "http" || scheme == "https",
                  components.host?.isEmpty == false else {
                return trimmed
            }

            let path = components.path
            if let apiRange = path.range(of: "/api", options: .backwards),
               apiRange.upperBound == path.endIndex || path[apiRange.upperBound] == "/" {
                components.path = String(path[..<apiRange.lowerBound])
            }
            components.query = nil
            components.fragment = nil
            if components.host?.caseInsensitiveCompare("localhost") == .orderedSame {
                components.host = "127.0.0.1"
            }
            if scheme == "http", components.port == nil {
                switch self {
                case .plex:
                    components.port = 32400
                case .emby, .jellyfin:
                    components.port = 8096
                case .omniplayDocker:
                    components.port = 45722
                default:
                    break
                }
            }
            var normalized = components.url?.absoluteString ?? url.absoluteString
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
        case .webdav, .plex, .emby, .jellyfin, .omniplayDocker:
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

nonisolated struct MediaServerAuthConfig: Codable {
    var token: String
    var userId: String?
    var libraryId: String?
    var libraryName: String?
    var libraryType: String?

    nonisolated static func encode(
        token: String,
        userId: String?,
        libraryId: String? = nil,
        libraryName: String? = nil,
        libraryType: String? = nil
    ) -> String? {
        let normalized = MediaServerAuthConfig(
            token: token.trimmingCharacters(in: .whitespacesAndNewlines),
            userId: userId?.trimmingCharacters(in: .whitespacesAndNewlines),
            libraryId: libraryId?.trimmingCharacters(in: .whitespacesAndNewlines),
            libraryName: libraryName?.trimmingCharacters(in: .whitespacesAndNewlines),
            libraryType: libraryType?.trimmingCharacters(in: .whitespacesAndNewlines)
        )
        guard !normalized.token.isEmpty || normalized.userId?.isEmpty == false || normalized.libraryId?.isEmpty == false else { return nil }
        guard let data = try? JSONEncoder().encode(normalized) else { return nil }
        return String(data: data, encoding: .utf8)
    }

    nonisolated static func decode(_ value: String?) -> MediaServerAuthConfig? {
        guard let value, let data = value.data(using: .utf8) else { return nil }
        return try? JSONDecoder().decode(MediaServerAuthConfig.self, from: data)
    }
}

nonisolated struct MediaSourceExternalAddress: Identifiable, Codable, Hashable {
    var id: UUID = UUID()
    var address: String
    var label: String = ""
    var useLuckyStun: Bool = false
    var luckyRuleId: String = ""
    var luckyRuleName: String = ""

    var displayLabel: String {
        let trimmed = label.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? "外网" : trimmed
    }

    private enum CodingKeys: String, CodingKey {
        case address, label, useLuckyStun, luckyRuleId, luckyRuleName
    }

    init(address: String = "", label: String = "", useLuckyStun: Bool = false, luckyRuleId: String = "", luckyRuleName: String = "") {
        self.address = address
        self.label = label
        self.useLuckyStun = useLuckyStun
        self.luckyRuleId = luckyRuleId
        self.luckyRuleName = luckyRuleName
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.address = try container.decodeIfPresent(String.self, forKey: .address) ?? ""
        self.label = try container.decodeIfPresent(String.self, forKey: .label) ?? ""
        self.useLuckyStun = try container.decodeIfPresent(Bool.self, forKey: .useLuckyStun) ?? false
        self.luckyRuleId = try container.decodeIfPresent(String.self, forKey: .luckyRuleId) ?? ""
        self.luckyRuleName = try container.decodeIfPresent(String.self, forKey: .luckyRuleName) ?? ""
    }
}

nonisolated struct MediaSourceAddressConfiguration: Codable, Hashable {
    var localAddress: String
    var localLabel: String
    var externalAddresses: [MediaSourceExternalAddress]
    var activeAddress: String
    var activeLabel: String

    private enum CodingKeys: String, CodingKey {
        case localAddress, localLabel, externalAddresses, activeAddress, activeLabel
    }

    init(localAddress: String = "", localLabel: String = "局域网", externalAddresses: [MediaSourceExternalAddress] = [], activeAddress: String = "", activeLabel: String = "") {
        self.localAddress = localAddress
        self.localLabel = localLabel
        self.externalAddresses = externalAddresses
        self.activeAddress = activeAddress
        self.activeLabel = activeLabel
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.localAddress = try container.decodeIfPresent(String.self, forKey: .localAddress) ?? ""
        self.localLabel = try container.decodeIfPresent(String.self, forKey: .localLabel) ?? "局域网"
        self.externalAddresses = try container.decodeIfPresent([MediaSourceExternalAddress].self, forKey: .externalAddresses) ?? []
        self.activeAddress = try container.decodeIfPresent(String.self, forKey: .activeAddress) ?? ""
        self.activeLabel = try container.decodeIfPresent(String.self, forKey: .activeLabel) ?? ""
    }
}

// 1. 媒体源
nonisolated struct MediaSource: Codable, FetchableRecord, PersistableRecord {
    var id: Int64?
    var name: String
    var protocolType: String
    var baseUrl: String
    var authConfig: String?
    var addressConfig: String? = nil
    var isEnabled: Bool = true
    var disabledAt: Double?

    nonisolated var protocolKind: MediaSourceProtocol? {
        MediaSourceProtocol(rawValue: protocolType)
    }

    nonisolated func normalizedBaseURL() -> String {
        guard let kind = protocolKind else { return baseUrl }
        return kind.normalizedBaseURL(baseUrl)
    }

    nonisolated func addressConfiguration() -> MediaSourceAddressConfiguration {
        var configuration = addressConfig.flatMap { value -> MediaSourceAddressConfiguration? in
            guard let data = value.data(using: .utf8) else { return nil }
            return try? JSONDecoder().decode(MediaSourceAddressConfiguration.self, from: data)
        } ?? MediaSourceAddressConfiguration()
        if configuration.localAddress.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            configuration.localAddress = baseUrl
        }
        if configuration.activeAddress.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            configuration.activeAddress = baseUrl
        }
        if configuration.activeLabel.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            configuration.activeLabel = configuration.activeAddress.caseInsensitiveCompare(configuration.localAddress) == .orderedSame
                ? configuration.localLabel
                : ""
        }
        return configuration
    }

    nonisolated mutating func setAddressConfiguration(_ configuration: MediaSourceAddressConfiguration) {
        guard let data = try? JSONEncoder().encode(configuration) else { return }
        addressConfig = String(data: data, encoding: .utf8)
    }

    nonisolated var displayNameWithActiveLabel: String {
        let label = addressConfiguration().activeLabel.trimmingCharacters(in: .whitespacesAndNewlines)
        return label.isEmpty ? name : "\(name) · \(label)"
    }

    nonisolated var activeAddressLabel: String {
        addressConfiguration().activeLabel
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
    var fileSize: Int64 = 0
    var lastPlayedAt: Double?
    
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

nonisolated private func isMediaServerPlaybackEndpointPath(_ value: String) -> Bool {
    let normalized = value
        .trimmingCharacters(in: .whitespacesAndNewlines)
        .trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        .lowercased()
    let pathOnly = normalized.split(separator: "?", maxSplits: 1).first.map(String.init) ?? normalized
    let parts = pathOnly.split(separator: "/").map(String.init)
    if parts.count >= 3,
       parts[0] == "items",
       parts[2] == "download" {
        return true
    }
    if parts.count >= 3,
       parts[0] == "videos",
       parts[2] == "master.m3u8" {
        return true
    }
    if parts.count >= 3,
       parts[0] == "videos",
       parts[2].hasPrefix("stream.") {
        return true
    }
    if parts.count >= 4,
       parts[0] == "library",
       parts[1] == "parts" {
        return true
    }
    if parts.count >= 2,
       parts[0] == "library",
       parts[1] == "metadata" {
        return true
    }
    return false
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
            WHERE protocolType IN (?, ?, ?, ?, ?, ?)
            ORDER BY id DESC
            """,
            arguments: [
                MediaSourceProtocol.local.rawValue,
                MediaSourceProtocol.webdav.rawValue,
                MediaSourceProtocol.plex.rawValue,
                MediaSourceProtocol.emby.rawValue,
                MediaSourceProtocol.jellyfin.rawValue,
                MediaSourceProtocol.omniplayDocker.rawValue
            ]
        )
    }

    static func fetchEnabledScannableSources(in db: Database) throws -> [MediaSource] {
        try MediaSource.fetchAll(
            db,
            sql: """
            SELECT *
            FROM mediaSource
            WHERE protocolType IN (?, ?, ?, ?, ?, ?)
              AND COALESCE(isEnabled, 1) = 1
            ORDER BY id ASC
            """,
            arguments: [
                MediaSourceProtocol.local.rawValue,
                MediaSourceProtocol.webdav.rawValue,
                MediaSourceProtocol.plex.rawValue,
                MediaSourceProtocol.emby.rawValue,
                MediaSourceProtocol.jellyfin.rawValue,
                MediaSourceProtocol.omniplayDocker.rawValue
            ]
        )
    }

    static func fetchEnabledScannableSource(id: Int64, in db: Database) throws -> MediaSource? {
        try MediaSource.fetchOne(
            db,
            sql: """
            SELECT *
            FROM mediaSource
            WHERE id = ?
              AND protocolType IN (?, ?, ?, ?, ?, ?)
              AND COALESCE(isEnabled, 1) = 1
            LIMIT 1
            """,
            arguments: [
                id,
                MediaSourceProtocol.local.rawValue,
                MediaSourceProtocol.webdav.rawValue,
                MediaSourceProtocol.plex.rawValue,
                MediaSourceProtocol.emby.rawValue,
                MediaSourceProtocol.jellyfin.rawValue,
                MediaSourceProtocol.omniplayDocker.rawValue
            ]
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
        if source.protocolKind == .omniplayDocker {
            let displayName = fileName.trimmingCharacters(in: .whitespacesAndNewlines)
            return displayName.isEmpty ? fallback : displayName
        }
        if (source.protocolKind == .plex || source.protocolKind == .emby || source.protocolKind == .jellyfin),
           isMediaServerPlaybackEndpointPath(relative),
           !fileName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return fileName
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
