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

    /// 最近一次成功入库并拉起播放的直开请求。
    @Published private(set) var lastRequest: PlaybackRequest?
    @Published private(set) var lastOpenError: OpenPlaybackError?
    private var securityScopedURLs: Set<URL> = []

    /// 可以直开的容器。ISO 也算在内：蓝光原盘就是直接放镜像文件。
    static let supportedVideoExtensions: [String] = [
        "mp4", "mkv", "mov", "avi", "rmvb", "flv", "webm", "m2ts", "ts", "iso", "m4v", "wmv"
    ]

    static let supportedExtensionSet = Set(supportedVideoExtensions)

    /// 上面这些扩展名在系统里对应的 UTI，打开面板和「设为默认」都用它。
    static var supportedContentTypes: [UTType] {
        supportedVideoExtensions.compactMap { UTType(filenameExtension: $0) }
    }

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
                    open(request)
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

    /// 入库完成后立刻拉起独立播放窗口，直开不该让用户再点一次。
    private func open(_ request: PlaybackRequest) {
        lastRequest = request
        DirectPlaybackWindowManager.shared.open(request)
    }

    func clearOpenError() {
        lastOpenError = nil
    }

    /// 拖进来的东西里没有文件（例如拖了个书签），给个提示而不是静默失败。
    func reportDroppedNothing() {
        setOpenError("拖进来的不是可播放的文件。")
    }

    /// 「打开视频文件…」面板：选完走的是和访达直开完全相同的一条路。
    func presentOpenPanel() {
        lastOpenError = nil
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = false
        panel.canChooseFiles = true
        panel.message = "选择要直接播放的视频文件"
        panel.prompt = "播放"
        var types: [UTType] = [.movie, .audiovisualContent]
        types.append(contentsOf: Self.supportedContentTypes)
        panel.allowedContentTypes = types
        panel.begin { response in
            guard response == .OK else { return }
            let urls = panel.urls
            Task { @MainActor in
                DirectFilePlaybackManager.shared.handleOpen(urls: urls)
            }
        }
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
        return Self.supportedExtensionSet.contains(ext)
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
                    existingFile.lastPlayedAt = nil
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

        // 直开条目不进海报墙（mediaType = direct 会被过滤），这里广播一次
        // libraryUpdated 只会让海报墙在 mpv 启动的同时重排刷新，白白抢资源。
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

/// 播放进度的「立刻落盘」登记处。
///
/// ⌘Q 这条退出路径上没有下一轮 RunLoop：SwiftUI 的 onDisappear 不一定跑得到，
/// 就算跑到了，把写库交给协程也来不及被调度，进度就丢了。播放界面出现时把同步落盘
/// 的动作登记进来，AppDelegate 在 applicationWillTerminate 里当场调一次。
@MainActor
enum PlaybackProgressFlushCenter {
    private static var handlers: [UUID: () -> Void] = [:]

    static func register(_ token: UUID, handler: @escaping () -> Void) {
        handlers[token] = handler
    }

    static func unregister(_ token: UUID) {
        handlers[token] = nil
    }

    static func flushAll() {
        // 先取走再执行：handler 自己也可能注销登记
        let pending = Array(handlers.values)
        handlers.removeAll()
        for handler in pending { handler() }
    }
}

final class OmniPlayAppDelegate: NSObject, NSApplicationDelegate {
    func applicationWillTerminate(_ notification: Notification) {
        // 在这里同步写库；写完才轮到 iCloud 那份 willTerminate 通知推最新媒体库
        PlaybackProgressFlushCenter.flushAll()
    }

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

// MARK: - 默认打开方式

extension DirectFilePlaybackManager {
    struct DefaultHandlerReport {
        let succeeded: [String]
        let failed: [String]
    }

    /// 把本应用注册成这些视频类型的默认打开应用。逐个类型上报结果，部分失败也能看清是哪个。
    @discardableResult
    func setAsDefaultVideoPlayer() async -> DefaultHandlerReport {
        let appURL = Bundle.main.bundleURL
        var succeeded: [String] = []
        var failed: [String] = []
        for ext in Self.supportedVideoExtensions {
            guard let type = UTType(filenameExtension: ext) else {
                failed.append(ext)
                continue
            }
            let ok = await withCheckedContinuation { (continuation: CheckedContinuation<Bool, Never>) in
                NSWorkspace.shared.setDefaultApplication(at: appURL, toOpen: type) { error in
                    continuation.resume(returning: error == nil)
                }
            }
            if ok { succeeded.append(ext) } else { failed.append(ext) }
        }
        return DefaultHandlerReport(succeeded: succeeded, failed: failed)
    }

    /// 还没由本应用接管的扩展名；返回空数组就说明已经是默认打开方式了。
    func extensionsNotOwnedYet() -> [String] {
        let mine = Bundle.main.bundleURL.standardizedFileURL
        return Self.supportedVideoExtensions.filter { ext in
            guard let type = UTType(filenameExtension: ext),
                  let handler = NSWorkspace.shared.urlForApplication(toOpen: type) else {
                return true
            }
            return handler.standardizedFileURL != mine
        }
    }
}
