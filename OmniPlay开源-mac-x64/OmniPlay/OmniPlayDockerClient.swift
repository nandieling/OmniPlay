import Foundation

struct OmniPlayDockerAuthConfig: Codable {
    var username: String?
    var sessionCookie: String?

    static func encode(username: String?, sessionCookie: String?) -> String? {
        let config = OmniPlayDockerAuthConfig(
            username: username?.trimmingCharacters(in: .whitespacesAndNewlines),
            sessionCookie: sessionCookie?.trimmingCharacters(in: .whitespacesAndNewlines)
        )
        guard config.username?.isEmpty == false || config.sessionCookie?.isEmpty == false else { return nil }
        guard let data = try? JSONEncoder().encode(config) else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func decode(_ value: String?) -> OmniPlayDockerAuthConfig? {
        guard let value,
              let data = value.data(using: .utf8) else {
            return nil
        }
        return try? JSONDecoder().decode(OmniPlayDockerAuthConfig.self, from: data)
    }
}

struct OmniPlayDockerLibraryItem: Decodable {
    let id: String
    let itemKind: String
    let title: String
    let releaseDate: String?
    let overview: String?
    let posterAssetId: String?
    let voteAverage: Double?
    let doubanRating: Double?
    let maxProgressSeconds: Double
    let maxDurationSeconds: Double
    let updatedAt: String
}

struct OmniPlayDockerLibraryDetail: Decodable {
    let id: String
    let itemKind: String
    let title: String
    let releaseDate: String?
    let overview: String?
    let posterAssetId: String?
    let voteAverage: Double?
    let doubanRating: Double?
    let douban: OmniPlayDockerDoubanMetadata?
    let videoFiles: [OmniPlayDockerVideoFile]
    let seasons: [OmniPlayDockerSeason]
}

struct OmniPlayDockerDoubanMetadata: Codable {
    let subjectId: String
    let subjectUrl: String
    let title: String
    let originalTitle: String?
    let year: String?
    let rating: Double?
    let ratingCount: Int?
    let summary: String?
    let genres: String?
    let countries: String?
    let posterUrl: String?
    let fetchedAt: String
}

struct OmniPlayDockerDoubanMetadataImportRequest: Encodable {
    let subjectId: String
    let subjectUrl: String
    let title: String
    let originalTitle: String?
    let year: String?
    let rating: Double?
    let ratingCount: Int?
    let summary: String?
    let genres: String?
    let countries: String?
    let posterUrl: String?
    let fetchedAt: String
}

struct OmniPlayDockerSeason: Decodable {
    let episodes: [OmniPlayDockerEpisode]
}

struct OmniPlayDockerEpisode: Decodable {
    let stillAssetId: String?
    let videoFile: OmniPlayDockerVideoFile?
    let videoFiles: [OmniPlayDockerVideoFile]

    private enum CodingKeys: String, CodingKey {
        case stillAssetId
        case videoFile
        case videoFiles
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        stillAssetId = try container.decodeIfPresent(String.self, forKey: .stillAssetId)
        videoFile = try container.decodeIfPresent(OmniPlayDockerVideoFile.self, forKey: .videoFile)
        let parsed = try container.decodeIfPresent([OmniPlayDockerVideoFile].self, forKey: .videoFiles) ?? []
        videoFiles = parsed.isEmpty ? (videoFile.map { [$0] } ?? []) : parsed
    }
}

struct OmniPlayDockerVideoFile: Decodable {
    let id: String
    let relativePath: String
    let fileName: String
    let mediaKind: String
    let fileSizeBytes: Int64?
    let durationSeconds: Double
    let positionSeconds: Double
    let isWatched: Bool
}

struct OmniPlayDockerPlaybackTicket: Decodable {
    let streamUrl: String
    let positionSeconds: Double?
    let durationSeconds: Double?
    let isWatched: Bool?
}

struct OmniPlayDockerPlaybackProgress: Decodable {
    let positionSeconds: Double
    let durationSeconds: Double
    let isWatched: Bool
}

enum OmniPlayDockerClientError: LocalizedError {
    case invalidBaseURL
    case invalidResponse
    case authenticationRequired
    case network(String)
    case requestFailed(Int, String?)

    var errorDescription: String? {
        switch self {
        case .invalidBaseURL:
            return "Docker 服务地址无效。"
        case .invalidResponse:
            return "Docker 服务响应无效。"
        case .authenticationRequired:
            return "Docker 服务需要登录，请填写用户名和密码。"
        case .network(let message):
            return "Docker 服务连接失败：\(message)"
        case .requestFailed(let status, let message):
            if let message, !message.isEmpty {
                return "Docker 服务请求失败：HTTP \(status)，\(message)"
            }
            return "Docker 服务请求失败：HTTP \(status)。"
        }
    }
}

final class OmniPlayDockerClient {
    private let baseURL: URL
    private let session: URLSession
    private(set) var sessionCookie: String?

    init(baseURLString: String, sessionCookie: String? = nil) throws {
        let normalized = MediaSourceProtocol.omniplayDocker.normalizedBaseURL(baseURLString)
        guard let url = URL(string: normalized) else { throw OmniPlayDockerClientError.invalidBaseURL }
        self.baseURL = url
        self.sessionCookie = sessionCookie?.trimmingCharacters(in: .whitespacesAndNewlines)

        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 20
        configuration.timeoutIntervalForResource = 60
        configuration.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        configuration.urlCache = nil
        // Docker 服务通常在局域网内，禁用系统代理/PAC，避免局域网 HTTP 请求被代理接管后报离线。
        configuration.connectionProxyDictionary = [:]
        self.session = URLSession(configuration: configuration, delegate: LocalNetworkTrustSessionDelegate.shared, delegateQueue: nil)
    }

    func login(username: String, password: String) async throws {
        var request = try makeRequest(path: "/api/auth/login", method: "POST")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: [
            "username": username,
            "password": password
        ])

        let (data, response) = try await send(request)
        let http = try validate(response: response, data: data, allowUnauthorized: false)
        sessionCookie = Self.sessionCookie(from: http) ?? sessionCookie
        guard let sessionCookie, !sessionCookie.isEmpty else {
            throw OmniPlayDockerClientError.invalidResponse
        }
    }

    func libraryItems() async throws -> [OmniPlayDockerLibraryItem] {
        try await get(path: "/api/library/items")
    }

    func libraryDetail(id: String) async throws -> OmniPlayDockerLibraryDetail {
        try await get(path: "/api/library/items/\(Self.escapePath(id))")
    }

    func playbackTicket(videoFileId: String) async throws -> OmniPlayDockerPlaybackTicket {
        try await post(
            path: "/api/playback/files/\(Self.escapePath(videoFileId))/ticket",
            json: nil
        )
    }

    func playbackURL(videoFileId: String) async throws -> URL {
        let ticket = try await playbackTicket(videoFileId: videoFileId)
        guard let url = absoluteURL(ticket.streamUrl) else {
            throw OmniPlayDockerClientError.invalidResponse
        }
        return url
    }

    func playbackProgress(videoFileId: String) async throws -> OmniPlayDockerPlaybackProgress {
        try await get(path: "/api/playback/files/\(Self.escapePath(videoFileId))/progress")
    }

    func updateProgress(videoFileId: String, positionSeconds: Double, durationSeconds: Double) async throws {
        try await postEmpty(path: "/api/playback/progress", json: [
            "videoFileId": videoFileId,
            "positionSeconds": max(0, positionSeconds),
            "durationSeconds": max(0, durationSeconds),
            "userId": "local"
        ])
    }

    @discardableResult
    func importDoubanMetadata(libraryItemId: String, metadata: DoubanMetadata) async throws -> OmniPlayDockerLibraryDetail {
        let request = OmniPlayDockerDoubanMetadataImportRequest(
            subjectId: metadata.subjectId,
            subjectUrl: metadata.subjectURL,
            title: metadata.title,
            originalTitle: metadata.originalTitle,
            year: metadata.year,
            rating: metadata.rating,
            ratingCount: metadata.ratingCount,
            summary: metadata.summary,
            genres: metadata.genres,
            countries: metadata.countries,
            posterUrl: metadata.posterURL,
            fetchedAt: ISO8601DateFormatter().string(from: Date(timeIntervalSince1970: metadata.fetchedAt))
        )
        return try await postEncoded(path: "/api/library/items/\(Self.escapePath(libraryItemId))/douban/import", body: request)
    }

    nonisolated func posterURL(assetId: String) -> String {
        absoluteString(path: "/api/assets/posters/\(Self.escapePath(assetId))")
    }

    func posterData(assetId: String) async throws -> Data {
        try await assetData(path: "/api/assets/posters/\(Self.escapePath(assetId))")
    }

    func thumbnailData(assetId: String) async throws -> Data {
        try await assetData(path: "/api/assets/thumbnails/\(Self.escapePath(assetId))")
    }

    private func assetData(path: String) async throws -> Data {
        for attempt in 0..<2 {
            do {
                let request = try makeRequest(path: path, method: "GET")
                let (data, response) = try await send(request)
                _ = try validate(response: response, data: data)
                guard !data.isEmpty else { throw OmniPlayDockerClientError.invalidResponse }
                return data
            } catch {
                guard attempt == 0 else { throw error }
                try await Task.sleep(nanoseconds: 250_000_000)
            }
        }
        throw OmniPlayDockerClientError.invalidResponse
    }

    private func get<T: Decodable>(path: String) async throws -> T {
        let request = try makeRequest(path: path, method: "GET")
        let (data, response) = try await send(request)
        _ = try validate(response: response, data: data)
        return try JSONDecoder().decode(T.self, from: data)
    }

    private func post<T: Decodable>(path: String, json: [String: Any]?) async throws -> T {
        var request = try makeRequest(path: path, method: "POST")
        if let json {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try JSONSerialization.data(withJSONObject: json)
        }
        let (data, response) = try await send(request)
        _ = try validate(response: response, data: data)
        return try JSONDecoder().decode(T.self, from: data)
    }

    private func postEmpty(path: String, json: [String: Any]?) async throws {
        var request = try makeRequest(path: path, method: "POST")
        if let json {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try JSONSerialization.data(withJSONObject: json)
        }
        let (data, response) = try await send(request)
        _ = try validate(response: response, data: data)
    }

    private func postEncodedEmpty<T: Encodable>(path: String, body: T) async throws {
        var request = try makeRequest(path: path, method: "POST")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(body)
        let (data, response) = try await send(request)
        _ = try validate(response: response, data: data)
    }

    private func postEncoded<Response: Decodable, Body: Encodable>(path: String, body: Body) async throws -> Response {
        var request = try makeRequest(path: path, method: "POST")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(body)
        let (data, response) = try await send(request)
        _ = try validate(response: response, data: data)
        return try JSONDecoder().decode(Response.self, from: data)
    }

    private func makeRequest(path: String, method: String) throws -> URLRequest {
        guard let url = absoluteURL(path) else { throw OmniPlayDockerClientError.invalidBaseURL }
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if let sessionCookie, !sessionCookie.isEmpty {
            request.setValue("omniplay_session=\(sessionCookie)", forHTTPHeaderField: "Cookie")
        }
        return request
    }

    private func send(_ request: URLRequest) async throws -> (Data, URLResponse) {
        do {
            return try await session.data(for: request)
        } catch let error as URLError {
            let endpoint = request.url?.absoluteString ?? "未知地址"
            throw OmniPlayDockerClientError.network(
                "\(endpoint)（\(error.code.rawValue)：\(error.localizedDescription)）。请确认 NAS 与 Mac 在同一局域网、端口 45722 已放行，并检查 Docker 服务是否正在运行。"
            )
        }
    }

    @discardableResult
    private func validate(response: URLResponse, data: Data, allowUnauthorized: Bool = false) throws -> HTTPURLResponse {
        guard let http = response as? HTTPURLResponse else {
            throw OmniPlayDockerClientError.invalidResponse
        }
        if (200...299).contains(http.statusCode) {
            return http
        }
        if http.statusCode == 401 && !allowUnauthorized {
            throw OmniPlayDockerClientError.authenticationRequired
        }
        throw OmniPlayDockerClientError.requestFailed(http.statusCode, Self.errorMessage(from: data))
    }

    nonisolated private func absoluteURL(_ value: String) -> URL? {
        if let url = URL(string: value), url.scheme != nil {
            return url
        }
        return absoluteURL(path: value)
    }

    nonisolated private func absoluteURL(path: String) -> URL? {
        guard var components = URLComponents(url: baseURL, resolvingAgainstBaseURL: false) else { return nil }
        let split = path.split(separator: "?", maxSplits: 1, omittingEmptySubsequences: false)
        let pathPart = split.first.map(String.init) ?? path
        let queryPart = split.count > 1 ? String(split[1]) : nil
        let basePath = components.path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        let relative = pathPart.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        components.path = "/" + [basePath, relative].filter { !$0.isEmpty }.joined(separator: "/")
        components.percentEncodedQuery = queryPart
        return components.url
    }

    nonisolated private func absoluteString(path: String) -> String {
        absoluteURL(path: path)?.absoluteString ?? path
    }

    private static func sessionCookie(from response: HTTPURLResponse) -> String? {
        let values = response.allHeaderFields.compactMap { key, value -> String? in
            guard String(describing: key).caseInsensitiveCompare("Set-Cookie") == .orderedSame else { return nil }
            if let value = value as? String { return value }
            if let values = value as? [String] { return values.joined(separator: ";") }
            return String(describing: value)
        }
        for value in values {
            for segment in value.components(separatedBy: ";") {
            let trimmed = segment.trimmingCharacters(in: .whitespacesAndNewlines)
            guard trimmed.hasPrefix("omniplay_session=") else { continue }
            let token = String(trimmed.dropFirst("omniplay_session=".count))
            return token.isEmpty ? nil : token
            }
        }
        return nil
    }

    nonisolated private static func escapePath(_ value: String) -> String {
        value.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? value
    }

    private static func errorMessage(from data: Data) -> String? {
        guard !data.isEmpty,
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return nil
        }
        return object["error"] as? String ?? object["message"] as? String
    }
}
