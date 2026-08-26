import Foundation
import SwiftUI
import Combine
import GRDB
import AppKit

class OfflineCacheManager: ObservableObject {
    static let shared = OfflineCacheManager()
    
    @Published var cacheDirectory: URL?
    @Published var downloadProgress: [String: Double] = [:] // Key 改为 fileId 追踪进度
    
    // 兼容旧逻辑保留文件名集合，同时新增唯一缓存键集合
    @Published var cachedFileNames: Set<String> = []
    @Published var cachedFileKeys: Set<String> = []
    @Published var cacheStatusMessage: String? = nil
    
    // 无沙盒模式下，我们只需要存一个普通的字符串路径即可
    private let cachePathKey = "OfflineCacheDirectoryPath"
    private let minimumFreeSpaceAfterCache: Int64 = 256 * 1024 * 1024
    
    private init() {
        loadCachePath()
        checkCachedFiles()
    }
    
    // MARK: - 1. 目录权限管理 (无沙盒版)
    func selectCacheDirectory() {
        let panel = NSOpenPanel()
        panel.message = "请选择用于保存离线影视的本地文件夹"
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        
        if panel.runModal() == .OK, let url = panel.url {
            saveCachePath(for: url)
            self.cacheDirectory = url
            checkCachedFiles()
        }
    }
    
    private func saveCachePath(for url: URL) {
        // 直接存物理路径的字符串
        UserDefaults.standard.set(url.path, forKey: cachePathKey)
    }
    
    private func loadCachePath() {
        // 直接读取路径字符串并转为 URL
        if let path = UserDefaults.standard.string(forKey: cachePathKey) {
            var isDirectory: ObjCBool = false
            if FileManager.default.fileExists(atPath: path, isDirectory: &isDirectory), isDirectory.boolValue {
                self.cacheDirectory = URL(fileURLWithPath: path)
            }
        }
    }
    
    func checkCachedFiles() {
        guard let dir = cacheDirectory else { return }
        DispatchQueue.global(qos: .background).async {
            let enumerator = FileManager.default.enumerator(at: dir, includingPropertiesForKeys: [.isRegularFileKey], options: [.skipsHiddenFiles])
            var fileNames = Set<String>()
            var fileKeys = Set<String>()
            
            while let url = enumerator?.nextObject() as? URL {
                let values = try? url.resourceValues(forKeys: [.isRegularFileKey])
                guard values?.isRegularFile == true else { continue }
                fileNames.insert(url.lastPathComponent)
                
                let relativePath = url.path.replacingOccurrences(of: dir.path + "/", with: "")
                let parts = relativePath.split(separator: "/", maxSplits: 1).map(String.init)
                if parts.count == 2, Int64(parts[0]) != nil {
                    fileKeys.insert("\(parts[0]):\(parts[1])")
                }
            }
            
            DispatchQueue.main.async {
                self.cachedFileNames = fileNames
                self.cachedFileKeys = fileKeys
            }
        }
    }
    
    func getLocalPlaybackURL(for file: VideoFile) -> URL? {
        guard let dir = cacheDirectory else { return nil }
        
        let preferredURL = cachedURL(for: file, in: dir)
        if FileManager.default.fileExists(atPath: preferredURL.path) {
            return preferredURL
        }
        
        let legacyURL = dir.appendingPathComponent(file.fileName)
        if FileManager.default.fileExists(atPath: legacyURL.path) {
            return legacyURL
        }
        
        return nil
    }
    
    func isCached(_ file: VideoFile) -> Bool {
        if cachedFileKeys.contains(cacheKey(for: file)) { return true }
        if cachedFileNames.contains(file.fileName) { return true }
        return getLocalPlaybackURL(for: file) != nil
    }

    func supportsCaching(mediaSource: MediaSource?) -> Bool {
        guard let mediaSource, let kind = mediaSource.protocolKind else { return false }
        switch kind {
        case .local, .direct, .webdav:
            return true
        case .plex, .emby, .jellyfin, .omniplayDocker:
            return false
        }
    }
    
    func hasMissingSource(for file: VideoFile, mediaSource: MediaSource?) -> Bool {
        if isCached(file) { return false }
        guard let mediaSource else { return true }
        guard let kind = mediaSource.protocolKind else { return true }
        if kind == .webdav || kind == .plex || kind == .emby || kind == .jellyfin || kind == .omniplayDocker { return false }
        let sourceURL = sourceFileURL(for: file, mediaSource: mediaSource)
        return !FileManager.default.fileExists(atPath: sourceURL.path)
    }
    
    // MARK: - 2. 核心搬运引擎 (空间预检 + 带进度复制)
    func startDownloads(files: [VideoFile], groupTitle: String? = nil) {
        Task {
            await startDownloadsAfterPreflight(files: files, groupTitle: groupTitle)
        }
    }

    func startDownload(file: VideoFile) {
        startDownloads(files: [file], groupTitle: file.fileName)
    }

    @MainActor
    private func startDownloadsAfterPreflight(files: [VideoFile], groupTitle: String?) async {
        guard let cacheDir = cacheDirectory else {
            cacheStatusMessage = "请先在设置里选择离线缓存保存位置"
            showStorageAlert(title: "离线缓存", message: "请先在设置里选择离线缓存保存位置。")
            return
        }

        var seenFileIds = Set<String>()
        let uniqueFiles = files.filter { file in
            seenFileIds.insert(file.id).inserted
        }
        do {
            let plan = try await buildCachePlan(files: uniqueFiles, cacheDir: cacheDir)
            guard !plan.files.isEmpty else {
                cacheStatusMessage = plan.skippedUnsupportedCount > 0
                    ? "媒体源暂不支持离线缓存"
                    : "所选内容已在本地缓存中"
                return
            }

            let available = availableCapacity(at: cacheDir)
            if available < plan.totalBytes + minimumFreeSpaceAfterCache {
                let title = groupTitle?.trimmingCharacters(in: .whitespacesAndNewlines)
                let displayTitle = title?.isEmpty == false ? "《\(title!)》" : "所选内容"
                let message = "\(displayTitle) 需要 \(formatBytes(plan.totalBytes))，当前缓存磁盘可用 \(formatBytes(available))，空间不足，已取消离线缓存。"
                cacheStatusMessage = "硬盘存储空间不够"
                showStorageAlert(title: "硬盘存储空间不够", message: message)
                return
            }

            if plan.skippedUnsupportedCount > 0 {
                cacheStatusMessage = "部分媒体源暂不支持离线缓存，已跳过"
            }

            for filePlan in plan.files {
                startPreparedDownload(filePlan)
            }
        } catch {
            cacheStatusMessage = "离线缓存准备失败"
            showStorageAlert(title: "离线缓存", message: "离线缓存准备失败：\(error.localizedDescription)")
        }
    }

    private func startPreparedDownload(_ plan: CacheFilePlan) {
        let fileId = plan.file.id
        DispatchQueue.main.async {
            self.downloadProgress[fileId] = 0.01
        }

        DispatchQueue.global(qos: .utility).async {
            do {
                try FileManager.default.createDirectory(at: plan.destinationURL.deletingLastPathComponent(), withIntermediateDirectories: true)
                if FileManager.default.fileExists(atPath: plan.destinationURL.path) {
                    try FileManager.default.removeItem(at: plan.destinationURL)
                }

                switch plan.source {
                case .local(let sourceURL):
                    try self.copyFileWithProgress(
                        from: sourceURL,
                        to: plan.destinationURL,
                        expectedBytes: plan.fileSize,
                        fileId: fileId
                    )
                case .remote(let request):
                    try self.downloadRemoteFileWithProgress(
                        request: request,
                        to: plan.destinationURL,
                        expectedBytes: plan.fileSize,
                        fileId: fileId
                    )
                }

                DispatchQueue.main.async {
                    self.downloadProgress.removeValue(forKey: fileId)
                    self.cachedFileNames.insert(plan.file.fileName)
                    self.cachedFileKeys.insert(self.cacheKey(for: plan.file))
                    self.cacheStatusMessage = "《\(plan.file.fileName)》缓存完成"
                }
            } catch {
                print("❌ 离线缓存物理拷贝失败: \(error)")
                DispatchQueue.main.async {
                    self.downloadProgress.removeValue(forKey: fileId)
                    self.cacheStatusMessage = "《\(plan.file.fileName)》缓存失败"
                }
            }
        }
    }

    private enum CacheFileSource {
        case local(URL)
        case remote(URLRequest)
    }

    private struct CacheFilePlan {
        let file: VideoFile
        let source: CacheFileSource
        let destinationURL: URL
        let fileSize: Int64
    }

    private struct CachePlan {
        let files: [CacheFilePlan]
        let skippedUnsupportedCount: Int

        var totalBytes: Int64 {
            files.reduce(Int64(0)) { $0 + max(0, $1.fileSize) }
        }
    }

    private func buildCachePlan(files: [VideoFile], cacheDir: URL) async throws -> CachePlan {
        guard !files.isEmpty else { return CachePlan(files: [], skippedUnsupportedCount: 0) }
        let sources = try await AppDatabase.shared.dbQueue.read { db -> [Int64: MediaSource] in
            let sourceIds = Set(files.map(\.sourceId))
            var result: [Int64: MediaSource] = [:]
            for sourceId in sourceIds {
                if let source = try MediaSource.fetchOne(db, key: sourceId) {
                    result[sourceId] = source
                }
            }
            return result
        }

        var plannedFiles: [CacheFilePlan] = []
        var skippedUnsupportedCount = 0
        for file in files where !isCached(file) {
            guard let source = sources[file.sourceId], supportsCaching(mediaSource: source), let kind = source.protocolKind else {
                skippedUnsupportedCount += 1
                continue
            }
            let destinationURL = cachedURL(for: file, in: cacheDir)
            if FileManager.default.fileExists(atPath: destinationURL.path) {
                continue
            }
            switch kind {
            case .local, .direct:
                let sourceURL = sourceFileURL(for: file, mediaSource: source)
                guard FileManager.default.fileExists(atPath: sourceURL.path) else {
                    continue
                }
                let fileSize = fileSize(at: sourceURL)
                plannedFiles.append(CacheFilePlan(file: file, source: .local(sourceURL), destinationURL: destinationURL, fileSize: fileSize))
            case .webdav:
                guard let request = webDAVDownloadRequest(for: file, mediaSource: source) else {
                    continue
                }
                let fileSize = await remoteFileSize(for: request, fallback: file.fileSize)
                plannedFiles.append(CacheFilePlan(file: file, source: .remote(request), destinationURL: destinationURL, fileSize: fileSize))
            case .plex, .emby, .jellyfin, .omniplayDocker:
                skippedUnsupportedCount += 1
            }
        }

        return CachePlan(files: plannedFiles, skippedUnsupportedCount: skippedUnsupportedCount)
    }

    private func copyFileWithProgress(from sourceURL: URL, to destinationURL: URL, expectedBytes: Int64, fileId: String) throws {
        let input = try FileHandle(forReadingFrom: sourceURL)
        defer { try? input.close() }
        FileManager.default.createFile(atPath: destinationURL.path, contents: nil)
        let output = try FileHandle(forWritingTo: destinationURL)
        defer { try? output.close() }

        let bufferSize = 1024 * 1024
        var copiedBytes: Int64 = 0
        var lastReportedBytes: Int64 = 0
        while true {
            let data = try input.read(upToCount: bufferSize) ?? Data()
            if data.isEmpty { break }
            try output.write(contentsOf: data)
            copiedBytes += Int64(data.count)
            if copiedBytes - lastReportedBytes >= Int64(bufferSize) || copiedBytes == expectedBytes {
                lastReportedBytes = copiedBytes
                let progress = expectedBytes > 0 ? min(0.999, max(0.01, Double(copiedBytes) / Double(expectedBytes))) : 0.5
                DispatchQueue.main.async {
                    self.downloadProgress[fileId] = progress
                }
            }
        }
    }

    private func downloadRemoteFileWithProgress(request: URLRequest, to destinationURL: URL, expectedBytes: Int64, fileId: String) throws {
        let handler = RemoteFileDownloadHandler(
            destinationURL: destinationURL,
            expectedBytes: expectedBytes,
            fileId: fileId
        ) { progress in
            DispatchQueue.main.async {
                self.downloadProgress[fileId] = progress
            }
        }
        try handler.download(request: request, configuration: remoteSessionConfiguration())
    }

    private func remoteFileSize(for request: URLRequest, fallback: Int64) async -> Int64 {
        if fallback > 0 { return fallback }

        var headRequest = request
        headRequest.httpMethod = "HEAD"
        headRequest.httpBody = nil

        let session = URLSession(
            configuration: remoteSessionConfiguration(),
            delegate: LocalNetworkTrustSessionDelegate.shared,
            delegateQueue: nil
        )
        defer { session.invalidateAndCancel() }

        do {
            let (_, response) = try await session.data(for: headRequest)
            guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
                return 0
            }
            if let contentLength = http.value(forHTTPHeaderField: "Content-Length"),
               let size = Int64(contentLength),
               size > 0 {
                return size
            }
            if response.expectedContentLength > 0 {
                return response.expectedContentLength
            }
        } catch {}
        return 0
    }

    private func webDAVDownloadRequest(for file: VideoFile, mediaSource: MediaSource) -> URLRequest? {
        let normalizedBase = MediaSourceProtocol.webdav.normalizedBaseURL(mediaSource.baseUrl)
        guard let rawBaseURL = URL(string: normalizedBase),
              var components = URLComponents(url: rawBaseURL, resolvingAgainstBaseURL: false) else {
            return nil
        }

        let credential = webDAVCredential(authConfig: mediaSource.authConfig, fallbackURL: rawBaseURL)
        components.user = nil
        components.password = nil
        guard var remoteURL = components.url else { return nil }

        let normalizedPath = file.relativePath.isEmpty
            ? file.fileName
            : file.relativePath.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        for component in normalizedPath.split(separator: "/") {
            remoteURL.appendPathComponent(String(component))
        }

        var request = URLRequest(url: remoteURL)
        request.httpMethod = "GET"
        request.timeoutInterval = 30
        request.setValue("application/octet-stream", forHTTPHeaderField: "Accept")
        if let credential,
           !credential.username.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            let raw = "\(credential.username):\(credential.password)"
            let encoded = Data(raw.utf8).base64EncodedString()
            request.setValue("Basic \(encoded)", forHTTPHeaderField: "Authorization")
        }
        return request
    }

    private func webDAVCredential(authConfig: String?, fallbackURL: URL) -> (username: String, password: String)? {
        if let id = WebDAVCredentialStore.shared.credentialID(from: authConfig),
           let stored = WebDAVCredentialStore.shared.loadCredential(id: id) {
            return (stored.username, stored.password)
        }
        if let legacy = WebDAVCredentialStore.shared.decodeLegacyCredential(from: authConfig) {
            return (legacy.username, legacy.password)
        }
        if let user = fallbackURL.user, !user.isEmpty {
            return (user, fallbackURL.password ?? "")
        }
        return nil
    }

    private func remoteSessionConfiguration() -> URLSessionConfiguration {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 30
        configuration.timeoutIntervalForResource = 24 * 60 * 60
        configuration.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        configuration.urlCache = nil
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        configuration.connectionProxyDictionary = [:]
        if let protocolClasses = WebDAVScannerRuntimeOverrides.protocolClasses {
            configuration.protocolClasses = protocolClasses
        }
        return configuration
    }

    private func fileSize(at url: URL) -> Int64 {
        let values = try? url.resourceValues(forKeys: [.fileSizeKey, .totalFileAllocatedSizeKey])
        if let fileSize = values?.fileSize, fileSize > 0 { return Int64(fileSize) }
        if let allocated = values?.totalFileAllocatedSize, allocated > 0 { return Int64(allocated) }
        let attributes = try? FileManager.default.attributesOfItem(atPath: url.path)
        return (attributes?[.size] as? NSNumber)?.int64Value ?? 0
    }

    private func availableCapacity(at url: URL) -> Int64 {
        let values = try? url.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey, .volumeAvailableCapacityKey])
        if let important = values?.volumeAvailableCapacityForImportantUsage, important > 0 {
            return important
        }
        if let available = values?.volumeAvailableCapacity, available > 0 {
            return Int64(available)
        }
        return 0
    }

    @MainActor
    private func showStorageAlert(title: String, message: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.alertStyle = .warning
        alert.addButton(withTitle: "知道了")
        alert.runModal()
    }

    private func formatBytes(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: max(0, bytes), countStyle: .file)
    }
    
    // MARK: - 3. 删除缓存 (无沙盒版)
    func deleteCache(fileId: String, fileName: String) {
        guard let cacheDir = cacheDirectory else { return }
        let destinationURL = cacheDir.appendingPathComponent(fileName)
        let nestedCandidates = try? FileManager.default.subpathsOfDirectory(atPath: cacheDir.path)
        
        // 直接无脑删，不再需要向系统申请权限！
        try? FileManager.default.removeItem(at: destinationURL)
        if let nestedCandidates {
            for path in nestedCandidates where path.hasSuffix("/" + fileName) || path == fileName {
                try? FileManager.default.removeItem(at: cacheDir.appendingPathComponent(path))
            }
        }
        
        DispatchQueue.main.async {
            self.cachedFileNames.remove(fileName)
            self.checkCachedFiles()
            self.downloadProgress.removeValue(forKey: fileId)
        }
    }
    
    private func cacheKey(for file: VideoFile) -> String {
        let normalizedPath = file.relativePath.isEmpty ? file.fileName : file.relativePath.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        return "\(file.sourceId):\(normalizedPath)"
    }
    
    private func cachedURL(for file: VideoFile, in dir: URL) -> URL {
        let normalizedPath = file.relativePath.isEmpty ? file.fileName : file.relativePath.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        return dir.appendingPathComponent(String(file.sourceId)).appendingPathComponent(normalizedPath)
    }
    
    private func sourceFileURL(for file: VideoFile, mediaSource: MediaSource) -> URL {
        let sourceBaseUrl = URL(fileURLWithPath: mediaSource.baseUrl)
        let normalizedPath = file.relativePath.isEmpty ? file.fileName : file.relativePath.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        return sourceBaseUrl.appendingPathComponent(normalizedPath)
    }
}

private final class RemoteFileDownloadHandler: NSObject, URLSessionDownloadDelegate {
    private let destinationURL: URL
    private let expectedBytes: Int64
    private let fileId: String
    private let progressHandler: (Double) -> Void
    private let semaphore = DispatchSemaphore(value: 0)
    private let lock = NSLock()

    private var session: URLSession?
    private var result: Result<Void, Error>?
    private var hasSignaled = false

    init(destinationURL: URL, expectedBytes: Int64, fileId: String, progressHandler: @escaping (Double) -> Void) {
        self.destinationURL = destinationURL
        self.expectedBytes = expectedBytes
        self.fileId = fileId
        self.progressHandler = progressHandler
    }

    func download(request: URLRequest, configuration: URLSessionConfiguration) throws {
        let session = URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
        self.session = session
        session.downloadTask(with: request).resume()
        semaphore.wait()
        session.finishTasksAndInvalidate()
        self.session = nil

        let finalResult = lockedResult() ?? .failure(downloadError(message: "远程下载未返回结果"))
        try finalResult.get()
    }

    func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didWriteData bytesWritten: Int64,
        totalBytesWritten: Int64,
        totalBytesExpectedToWrite: Int64
    ) {
        let resolvedExpected = totalBytesExpectedToWrite > 0 ? totalBytesExpectedToWrite : expectedBytes
        let progress = resolvedExpected > 0
            ? min(0.999, max(0.01, Double(totalBytesWritten) / Double(resolvedExpected)))
            : 0.5
        progressHandler(progress)
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        if let http = downloadTask.response as? HTTPURLResponse,
           !(200...299).contains(http.statusCode) {
            storeResult(.failure(downloadError(message: "远程下载失败：HTTP \(http.statusCode)")))
            try? FileManager.default.removeItem(at: location)
            return
        }

        do {
            if FileManager.default.fileExists(atPath: destinationURL.path) {
                try FileManager.default.removeItem(at: destinationURL)
            }
            try FileManager.default.moveItem(at: location, to: destinationURL)
            progressHandler(0.999)
            storeResult(.success(()))
        } catch {
            storeResult(.failure(error))
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        if let error {
            signalIfNeeded(.failure(error))
        } else {
            signalIfNeeded(nil)
        }
    }

    private func storeResult(_ newResult: Result<Void, Error>) {
        lock.lock()
        if result == nil {
            result = newResult
        }
        lock.unlock()
    }

    private func signalIfNeeded(_ newResult: Result<Void, Error>?) {
        lock.lock()
        if let newResult, result == nil {
            result = newResult
        }
        if result == nil {
            result = .success(())
        }
        guard !hasSignaled else {
            lock.unlock()
            return
        }
        hasSignaled = true
        lock.unlock()
        semaphore.signal()
    }

    private func lockedResult() -> Result<Void, Error>? {
        lock.lock()
        defer { lock.unlock() }
        return result
    }

    private func downloadError(message: String) -> NSError {
        NSError(
            domain: "OfflineCacheManager.RemoteDownload",
            code: -1,
            userInfo: [
                NSLocalizedDescriptionKey: message,
                "fileId": fileId
            ]
        )
    }
}

struct OfflineCacheProgressBadge: View {
    let progress: Double
    let tint: Color
    var background: Color = Color.black.opacity(0.62)

    var body: some View {
        ZStack {
            Circle()
                .fill(background)
            Circle()
                .stroke(Color.white.opacity(0.28), lineWidth: 4)
            Circle()
                .trim(from: 0, to: CGFloat(min(max(progress, 0), 1)))
                .stroke(tint, style: StrokeStyle(lineWidth: 4, lineCap: .round))
                .rotationEffect(.degrees(-90))
            Text("\(Int((min(max(progress, 0), 1) * 100).rounded()))%")
                .font(.caption2.bold())
                .monospacedDigit()
                .foregroundColor(.white)
        }
        .frame(width: 52, height: 52)
    }
}
