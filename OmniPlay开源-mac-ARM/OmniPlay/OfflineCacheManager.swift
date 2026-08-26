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
    @Published private(set) var queuedFileIDs: Set<String> = []
    @Published private(set) var activeCacheTitle: String? = nil
    @Published private(set) var currentCacheFileName: String? = nil
    @Published private(set) var completedDownloadCount = 0
    @Published private(set) var totalDownloadCount = 0
    
    // 无沙盒模式下，我们只需要存一个普通的字符串路径即可
    private let cachePathKey = "OfflineCacheDirectoryPath"
    private let minimumFreeSpaceAfterCache: Int64 = 1 * 1024 * 1024 * 1024
    private let downloadQueue = DispatchQueue(label: "com.omniplay.offline-cache.serial", qos: .utility)
    private let cancellationLock = NSLock()
    private var cancelledFileIDs: Set<String> = []
    private var activeRemoteHandlers: [String: RemoteFileDownloadHandler] = [:]

    var isCaching: Bool { totalDownloadCount > 0 }

    var overallDownloadProgress: Double {
        guard totalDownloadCount > 0 else { return 0 }
        let currentProgress = downloadProgress.values.first ?? 0
        return min(1, (Double(completedDownloadCount) + currentProgress) / Double(totalDownloadCount))
    }

    func progress(for file: VideoFile) -> Double? {
        if let progress = downloadProgress[file.id] { return progress }
        return queuedFileIDs.contains(file.id) ? 0 : nil
    }
    
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
                guard !url.lastPathComponent.hasSuffix(".part") else { continue }
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
        
        let normalizedPath = file.relativePath.isEmpty
            ? file.fileName
            : file.relativePath.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        let legacyURL = dir.appendingPathComponent(String(file.sourceId)).appendingPathComponent(normalizedPath)
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
    func cancelDownloads(files: [VideoFile]) {
        let requestedIDs = Set(files.map(\.id)).filter { fileID in
            queuedFileIDs.contains(fileID) || downloadProgress[fileID] != nil
        }
        guard !requestedIDs.isEmpty else { return }

        cancellationLock.lock()
        cancelledFileIDs.formUnion(requestedIDs)
        let handlers = requestedIDs.compactMap { activeRemoteHandlers[$0] }
        cancellationLock.unlock()

        handlers.forEach { $0.cancel() }
        cacheStatusMessage = "正在取消离线缓存…"
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

            let trimmedTitle = groupTitle?.trimmingCharacters(in: .whitespacesAndNewlines)
            if totalDownloadCount == 0 {
                activeCacheTitle = trimmedTitle?.isEmpty == false ? trimmedTitle : "离线缓存"
                completedDownloadCount = 0
            } else if let trimmedTitle, !trimmedTitle.isEmpty, activeCacheTitle != trimmedTitle {
                activeCacheTitle = "多个缓存任务"
            }
            totalDownloadCount += plan.files.count
            queuedFileIDs.formUnion(plan.files.map(\.file.id))
            cacheStatusMessage = nil
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
        downloadQueue.async {
            let partialURL = plan.destinationURL.appendingPathExtension("part")
            do {
                try self.throwIfCancellationRequested(fileId)
                DispatchQueue.main.sync {
                    self.queuedFileIDs.remove(fileId)
                    self.currentCacheFileName = plan.file.fileName
                    self.downloadProgress[fileId] = 0.01
                }
                try FileManager.default.createDirectory(at: plan.destinationURL.deletingLastPathComponent(), withIntermediateDirectories: true)
                if FileManager.default.fileExists(atPath: partialURL.path) {
                    try FileManager.default.removeItem(at: partialURL)
                }

                switch plan.source {
                case .local(let sourceURL):
                    try self.copyFileWithProgress(
                        from: sourceURL,
                        to: partialURL,
                        expectedBytes: plan.fileSize,
                        fileId: fileId
                    )
                case .remote(let request):
                    try self.downloadRemoteFileWithProgress(
                        request: request,
                        to: partialURL,
                        expectedBytes: plan.fileSize,
                        fileId: fileId
                    )
                }
                try self.throwIfCancellationRequested(fileId)
                try FileManager.default.moveItem(at: partialURL, to: plan.destinationURL)
                self.clearCancellationRequest(fileId)

                DispatchQueue.main.async {
                    self.downloadProgress.removeValue(forKey: fileId)
                    self.cachedFileNames.insert(plan.file.fileName)
                    self.cachedFileKeys.insert(self.cacheKey(for: plan.file))
                    self.finishPreparedDownload()
                    self.cacheStatusMessage = "《\(plan.file.fileName)》缓存完成"
                }
            } catch {
                try? FileManager.default.removeItem(at: partialURL)
                let wasCancelled = self.isCancellationRequested(fileId) || error is CacheTransferError
                self.clearCancellationRequest(fileId)
                if !wasCancelled {
                    print("❌ 离线缓存物理拷贝失败: \(error)")
                }
                DispatchQueue.main.async {
                    self.downloadProgress.removeValue(forKey: fileId)
                    self.queuedFileIDs.remove(fileId)
                    self.finishPreparedDownload()
                    self.cacheStatusMessage = wasCancelled
                        ? "《\(plan.file.fileName)》已取消缓存"
                        : "《\(plan.file.fileName)》缓存失败"
                }
            }
        }
    }

    private func finishPreparedDownload() {
        completedDownloadCount += 1
        currentCacheFileName = nil
        if completedDownloadCount >= totalDownloadCount {
            completedDownloadCount = 0
            totalDownloadCount = 0
            activeCacheTitle = nil
            queuedFileIDs.removeAll()
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

    private enum CacheTransferError: Error {
        case cancelled
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
                guard fileSize > 0 else {
                    throw cachePreparationError("无法读取《\(file.fileName)》的文件大小，未开始缓存。")
                }
                plannedFiles.append(CacheFilePlan(file: file, source: .local(sourceURL), destinationURL: destinationURL, fileSize: fileSize))
            case .webdav:
                guard let request = webDAVDownloadRequest(for: file, mediaSource: source) else {
                    continue
                }
                let fileSize = await remoteFileSize(for: request, fallback: file.fileSize)
                guard fileSize > 0 else {
                    throw cachePreparationError("服务器未提供《\(file.fileName)》的文件大小，无法完成存储空间预检。")
                }
                plannedFiles.append(CacheFilePlan(file: file, source: .remote(request), destinationURL: destinationURL, fileSize: fileSize))
            case .plex, .emby, .jellyfin, .omniplayDocker:
                skippedUnsupportedCount += 1
            }
        }

        return CachePlan(files: plannedFiles, skippedUnsupportedCount: skippedUnsupportedCount)
    }

    private func cachePreparationError(_ message: String) -> NSError {
        NSError(
            domain: "OfflineCacheManager.Preflight",
            code: 1,
            userInfo: [NSLocalizedDescriptionKey: message]
        )
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
            try throwIfCancellationRequested(fileId)
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
        cancellationLock.lock()
        activeRemoteHandlers[fileId] = handler
        let shouldCancel = cancelledFileIDs.contains(fileId)
        cancellationLock.unlock()
        if shouldCancel { handler.cancel() }
        defer {
            cancellationLock.lock()
            activeRemoteHandlers.removeValue(forKey: fileId)
            cancellationLock.unlock()
        }
        try handler.download(request: request, configuration: remoteSessionConfiguration())
    }

    private func throwIfCancellationRequested(_ fileId: String) throws {
        if isCancellationRequested(fileId) {
            throw CacheTransferError.cancelled
        }
    }

    private func isCancellationRequested(_ fileId: String) -> Bool {
        cancellationLock.lock()
        defer { cancellationLock.unlock() }
        return cancelledFileIDs.contains(fileId)
    }

    private func clearCancellationRequest(_ fileId: String) {
        cancellationLock.lock()
        cancelledFileIDs.remove(fileId)
        activeRemoteHandlers.removeValue(forKey: fileId)
        cancellationLock.unlock()
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
        dir.appendingPathComponent(file.fileName)
    }
    
    private func sourceFileURL(for file: VideoFile, mediaSource: MediaSource) -> URL {
        let sourceBaseUrl = URL(fileURLWithPath: mediaSource.baseUrl)
        let normalizedPath = file.relativePath.isEmpty ? file.fileName : file.relativePath.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        return sourceBaseUrl.appendingPathComponent(normalizedPath)
    }
}

private final class RemoteFileDownloadHandler: NSObject, URLSessionDataDelegate {
    private let destinationURL: URL
    private let expectedBytes: Int64
    private let fileId: String
    private let progressHandler: (Double) -> Void
    private let semaphore = DispatchSemaphore(value: 0)
    private let lock = NSLock()

    private var session: URLSession?
    private var dataTask: URLSessionDataTask?
    private var result: Result<Void, Error>?
    private var hasSignaled = false
    private var isCancelled = false
    private var outputHandle: FileHandle?
    private var receivedBytes: Int64 = 0
    private var responseExpectedBytes: Int64 = 0

    init(destinationURL: URL, expectedBytes: Int64, fileId: String, progressHandler: @escaping (Double) -> Void) {
        self.destinationURL = destinationURL
        self.expectedBytes = expectedBytes
        self.fileId = fileId
        self.progressHandler = progressHandler
    }

    func download(request: URLRequest, configuration: URLSessionConfiguration) throws {
        FileManager.default.createFile(atPath: destinationURL.path, contents: nil)
        outputHandle = try FileHandle(forWritingTo: destinationURL)
        let session = URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
        self.session = session
        let task = session.dataTask(with: request)
        lock.lock()
        dataTask = task
        let shouldCancel = isCancelled
        lock.unlock()
        if shouldCancel {
            task.cancel()
            signalIfNeeded(.failure(URLError(.cancelled)))
        } else {
            task.resume()
        }
        semaphore.wait()
        session.finishTasksAndInvalidate()
        self.session = nil
        self.dataTask = nil
        try? outputHandle?.close()
        outputHandle = nil

        let finalResult = lockedResult() ?? .failure(downloadError(message: "远程下载未返回结果"))
        do {
            try finalResult.get()
        } catch {
            try? FileManager.default.removeItem(at: destinationURL)
            throw error
        }
    }

    func cancel() {
        lock.lock()
        isCancelled = true
        let task = dataTask
        lock.unlock()
        task?.cancel()
    }

    func urlSession(
        _ session: URLSession,
        dataTask: URLSessionDataTask,
        didReceive response: URLResponse,
        completionHandler: @escaping (URLSession.ResponseDisposition) -> Void
    ) {
        if let http = response as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
            storeResult(.failure(downloadError(message: "远程下载失败：HTTP \(http.statusCode)")))
            completionHandler(.cancel)
            return
        }
        responseExpectedBytes = response.expectedContentLength > 0 ? response.expectedContentLength : expectedBytes
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        do {
            try outputHandle?.write(contentsOf: data)
            receivedBytes += Int64(data.count)
            let resolvedExpected = responseExpectedBytes > 0 ? responseExpectedBytes : expectedBytes
            let progress = resolvedExpected > 0
                ? min(0.999, max(0.01, Double(receivedBytes) / Double(resolvedExpected)))
                : 0.5
            progressHandler(progress)
        } catch {
            storeResult(.failure(error))
            dataTask.cancel()
        }
    }

    func urlSession(
        _ session: URLSession,
        didReceive challenge: URLAuthenticationChallenge,
        completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void
    ) {
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              let serverTrust = challenge.protectionSpace.serverTrust,
              LocalNetworkTrustSessionDelegate.isLocalOrPrivateHost(challenge.protectionSpace.host) else {
            completionHandler(.performDefaultHandling, nil)
            return
        }
        completionHandler(.useCredential, URLCredential(trust: serverTrust))
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        if let error {
            signalIfNeeded(.failure(error))
        } else {
            progressHandler(0.999)
            signalIfNeeded(.success(()))
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
