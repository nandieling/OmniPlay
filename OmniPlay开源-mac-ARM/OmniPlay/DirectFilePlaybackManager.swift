import Foundation
import AppKit
import GRDB
import UniformTypeIdentifiers
import Combine

@MainActor
final class DirectFilePlaybackManager: ObservableObject {
    static let shared = DirectFilePlaybackManager()

    struct PlaybackRequest: Identifiable, Hashable {
        let id = UUID()
        let movie: Movie
        let fileId: String
        let initialSourceBasePath: String?
        let initialSourceProtocolType: String?
        let initialSourceAuthConfig: String?
        let initialPlaylistFiles: [VideoFile]?

        init(
            movie: Movie,
            fileId: String,
            initialSourceBasePath: String? = nil,
            initialSourceProtocolType: String? = nil,
            initialSourceAuthConfig: String? = nil,
            initialPlaylistFiles: [VideoFile]? = nil
        ) {
            self.movie = movie
            self.fileId = fileId
            self.initialSourceBasePath = initialSourceBasePath
            self.initialSourceProtocolType = initialSourceProtocolType
            self.initialSourceAuthConfig = initialSourceAuthConfig
            self.initialPlaylistFiles = initialPlaylistFiles
        }

        static func == (lhs: PlaybackRequest, rhs: PlaybackRequest) -> Bool {
            lhs.fileId == rhs.fileId && lhs.movie.id == rhs.movie.id
        }

        func hash(into hasher: inout Hasher) {
            hasher.combine(fileId)
            hasher.combine(movie.id)
        }
    }

    struct OpenPlaybackError: Identifiable, Hashable {
        let id = UUID()
        let message: String
    }

    @Published private(set) var pendingRequest: PlaybackRequest?
    @Published private(set) var lastOpenError: OpenPlaybackError?
    private var securityScopedURLs: Set<URL> = []

    private let supportedExtensions: Set<String> = [
        "mp4", "mkv", "mov", "avi", "rmvb", "flv", "webm", "m2ts", "ts", "iso", "m4v", "wmv"
    ]

    private init() {}

    func handleOpen(urls: [URL]) {
        Task { @MainActor in
            lastOpenError = nil
            guard !urls.isEmpty else { return }

            var firstFailureReason: String?
            var didCreateRequest = false
            for rawURL in urls {
                guard let resolvedURL = resolveFileURL(rawURL) else {
                    if firstFailureReason == nil {
                        firstFailureReason = "路径解析失败：\(rawURL.path)"
                    }
                    continue
                }

                guard isPlayableVideoFile(resolvedURL) else {
                    if firstFailureReason == nil {
                        firstFailureReason = "文件格式不支持：\(resolvedURL.lastPathComponent)"
                    }
                    continue
                }

                guard beginSecurityScopedAccessIfNeeded(for: resolvedURL) else {
                    if firstFailureReason == nil {
                        firstFailureReason = "无权限读取文件：\(resolvedURL.path)"
                    }
                    continue
                }
                do {
                    let request = try await createPlaybackRequest(for: resolvedURL)
                    pendingRequest = request
                    lastOpenError = nil
                    didCreateRequest = true
                    break
                } catch {
                    let reason = "入库或播放准备失败：\(error.localizedDescription)"
                    print("❌ 直开失败：\(reason)")
                    if firstFailureReason == nil { firstFailureReason = reason }
                }
            }

            if !didCreateRequest {
                setOpenError(firstFailureReason ?? "未找到可播放文件。")
            }
        }
    }

    func consumePendingRequest() -> PlaybackRequest? {
        defer { pendingRequest = nil }
        return pendingRequest
    }

    private func resolveFileURL(_ url: URL) -> URL? {
        guard url.isFileURL else { return nil }

        let candidates: [URL] = {
            var result = [url]
            if let aliasResolved = try? URL(resolvingAliasFileAt: url) {
                result.append(aliasResolved)
            }
            return result
        }()

        for candidate in candidates {
            let normalized = candidate.resolvingSymlinksInPath().standardizedFileURL
            if FileManager.default.fileExists(atPath: normalized.path) {
                return normalized
            }
        }
        return url.resolvingSymlinksInPath().standardizedFileURL
    }

    private func isPlayableVideoFile(_ url: URL) -> Bool {
        guard url.isFileURL else { return false }

        if let contentType = try? url.resourceValues(forKeys: [.contentTypeKey]).contentType,
            contentType.conforms(to: .movie) || contentType.conforms(to: .video) {
            return true
        }

        let ext = url.pathExtension.lowercased()
        return supportedExtensions.contains(ext)
    }

    private func beginSecurityScopedAccessIfNeeded(for url: URL) -> Bool {
        guard !securityScopedURLs.contains(url) else { return true }
        guard url.startAccessingSecurityScopedResource() else { return false }
        securityScopedURLs.insert(url)
        return true
    }

    private func setOpenError(_ message: String) {
        lastOpenError = OpenPlaybackError(message: message)
    }

    private func createPlaybackRequest(for fileURL: URL) async throws -> PlaybackRequest {
        let (movie, fileId) = try await AppDatabase.shared.dbQueue.write { db -> (Movie, String) in
            let sourceId: Int64

            if let existingSourceId = try Int64.fetchOne(
                db,
                sql: "SELECT id FROM mediaSource WHERE protocolType = ? LIMIT 1",
                arguments: [MediaSourceProtocol.direct.rawValue]
            ) {
                sourceId = existingSourceId
            } else {
                let source = MediaSource(
                    id: nil,
                    name: "访达直开",
                    protocolType: MediaSourceProtocol.direct.rawValue,
                    baseUrl: MediaSourceProtocol.direct.normalizedBaseURL("/"),
                    authConfig: nil
                )
                try source.insert(db)
                sourceId = db.lastInsertedRowID
            }

            let relativePath = fileURL.path
            let fileName = fileURL.lastPathComponent

            if var existingFile = try VideoFile
                .filter(Column("sourceId") == sourceId && Column("relativePath") == relativePath)
                .fetchOne(db) {
                if existingFile.duration > 0 && (existingFile.playProgress / existingFile.duration) >= 0.95 {
                    existingFile.playProgress = 0
                }
                let movie = try Self.ensureMovie(for: &existingFile, fileURL: fileURL, db: db)
                try existingFile.update(db)
                return (movie, existingFile.id)
            }

            let movieId = Self.stableNegativeMovieId(for: fileURL.path)
            let movie = try Self.upsertDirectMovie(id: movieId, title: fileURL.deletingPathExtension().lastPathComponent, db: db)

            let newFileId = UUID().uuidString
            let newFile = VideoFile(
                id: newFileId,
                sourceId: sourceId,
                relativePath: relativePath,
                fileName: fileName,
                mediaType: "direct",
                movieId: movie.id,
                episodeId: nil,
                playProgress: 0.0,
                duration: 0.0
            )
            try newFile.insert(db)
            return (movie, newFileId)
        }

        await MainActor.run { NotificationCenter.default.post(name: .libraryUpdated, object: nil) }
        return PlaybackRequest(movie: movie, fileId: fileId)
    }

    nonisolated private static func ensureMovie(for file: inout VideoFile, fileURL: URL, db: Database) throws -> Movie {
        if let movieId = file.movieId, let movie = try Movie.fetchOne(db, key: movieId) {
            return movie
        }

        let movieId = stableNegativeMovieId(for: fileURL.path)
        let movie = try upsertDirectMovie(id: movieId, title: fileURL.deletingPathExtension().lastPathComponent, db: db)
        file.movieId = movie.id
        file.mediaType = file.mediaType == "unmatched" ? "direct" : file.mediaType
        return movie
    }

    nonisolated private static func upsertDirectMovie(id: Int64, title: String, db: Database) throws -> Movie {
        if let existing = try Movie.fetchOne(db, key: id) {
            return existing
        }

        let movie = Movie(
            id: id,
            title: title,
            releaseDate: nil,
            overview: "来自访达直接播放",
            posterPath: nil,
            voteAverage: nil,
            isLocked: false
        )
        try movie.insert(db)
        return movie
    }

    nonisolated private static func stableNegativeMovieId(for text: String) -> Int64 {
        var hash: UInt64 = 1469598103934665603
        let prime: UInt64 = 1099511628211
        for byte in text.utf8 {
            hash ^= UInt64(byte)
            hash = hash &* prime
        }

        let positive = Int64(hash & 0x7FFF_FFFF_FFFF_FFFF)
        if positive == 0 { return -1 }
        return -positive
    }
}

final class OmniPlayAppDelegate: NSObject, NSApplicationDelegate {
    func application(_ application: NSApplication, open urls: [URL]) {
        NSApp.activate(ignoringOtherApps: true)
        Task { @MainActor in
            DirectFilePlaybackManager.shared.handleOpen(urls: urls)
        }
    }

    func application(_ sender: NSApplication, openFile filename: String) -> Bool {
        NSApp.activate(ignoringOtherApps: true)
        let url = URL(fileURLWithPath: filename)
        Task { @MainActor in
            DirectFilePlaybackManager.shared.handleOpen(urls: [url])
        }
        return true
    }

    func application(_ sender: NSApplication, openFiles filenames: [String]) {
        NSApp.activate(ignoringOtherApps: true)
        let urls = filenames.map { URL(fileURLWithPath: $0) }
        Task { @MainActor in
            DirectFilePlaybackManager.shared.handleOpen(urls: urls)
        }
        sender.reply(toOpenOrPrint: .success)
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if DirectPlaybackWindowManager.shared.focusPlaybackWindowIfVisible() {
            return false
        }
        if !flag, DirectPlaybackWindowManager.shared.restoreWindowForReopen() {
            return false
        }
        return true
    }

    func applicationDidBecomeActive(_ notification: Notification) {
        if DirectPlaybackWindowManager.shared.focusPlaybackWindowIfVisible() {
            return
        }
        if !NSApp.windows.contains(where: { $0.isVisible }) {
            _ = DirectPlaybackWindowManager.shared.restoreWindowForReopen()
        }
    }
}

extension DirectFilePlaybackManager {
    func setAsDefaultVideoPlayer() -> Bool {
        guard let bundleID = Bundle.main.bundleIdentifier else { return false }

        let extensions = ["mp4", "mkv", "mov", "avi", "rmvb", "flv", "webm", "m2ts", "ts", "iso", "m4v", "wmv"]
        let types = extensions.compactMap { UTType(filenameExtension: $0) }

        var success = false
        for type in types {
            let result = LSSetDefaultRoleHandlerForContentType(
                type.identifier as CFString,
                .all,
                bundleID as CFString
            )
            if result == noErr {
                success = true
            }
        }

        return success
    }
}
