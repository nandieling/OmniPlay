import Foundation
import SwiftUI
import Combine
import GRDB

class OfflineCacheManager: ObservableObject {
    static let shared = OfflineCacheManager()
    
    @Published var cacheDirectory: URL?
    @Published var downloadProgress: [String: Double] = [:] // Key 改为 fileId 追踪进度
    
    // 兼容旧逻辑保留文件名集合，同时新增唯一缓存键集合
    @Published var cachedFileNames: Set<String> = []
    @Published var cachedFileKeys: Set<String> = []
    @Published var cacheStatusMessage: String? = nil
    
    // 全局离线缓存模式开关
    @Published var isCacheModeActive: Bool = false
    
    // 无沙盒模式下，我们只需要存一个普通的字符串路径即可
    private let cachePathKey = "OfflineCacheDirectoryPath"
    
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
        case .local, .direct:
            return true
        case .webdav:
            return false
        }
    }
    
    func hasMissingSource(for file: VideoFile, mediaSource: MediaSource?) -> Bool {
        if isCached(file) { return false }
        guard let mediaSource else { return true }
        guard let kind = mediaSource.protocolKind else { return true }
        if kind == .webdav { return false }
        let sourceURL = sourceFileURL(for: file, mediaSource: mediaSource)
        return !FileManager.default.fileExists(atPath: sourceURL.path)
    }
    
    // MARK: - 2. 核心搬运引擎 (无沙盒暴力直拷版)
    func startDownload(file: VideoFile) {
        guard let cacheDir = cacheDirectory else { return }
        let fileId = file.id
        let fileName = file.fileName
        let destinationURL = cachedURL(for: file, in: cacheDir)
        
        if FileManager.default.fileExists(atPath: destinationURL.path) {
            DispatchQueue.main.async {
                self.cachedFileNames.insert(fileName)
                self.cachedFileKeys.insert(self.cacheKey(for: file))
                self.cacheStatusMessage = "《\(file.fileName)》已在本地缓存中"
            }
            return
        }
        
        DispatchQueue.main.async { self.downloadProgress[fileId] = 0.01 }
        
        Task {
            do {
                let source = try await AppDatabase.shared.dbQueue.read { db in try MediaSource.fetchOne(db, key: file.sourceId) }
                guard let mediaSource = source else { throw NSError(domain: "", code: 0, userInfo: [NSLocalizedDescriptionKey: "找不到源"]) }

                guard supportsCaching(mediaSource: mediaSource) else {
                    DispatchQueue.main.async {
                        self.downloadProgress.removeValue(forKey: fileId)
                        self.cacheStatusMessage = "远程源暂不支持离线缓存"
                    }
                    return
                }

                let sourceBaseUrl = URL(fileURLWithPath: mediaSource.baseUrl)
                var subPath = file.relativePath.isEmpty ? file.fileName : file.relativePath
                if subPath.hasPrefix("/") { subPath = String(subPath.dropFirst()) }
                
                let sourceFileURL = sourceBaseUrl.appendingPathComponent(subPath)
                
                // 彻底干掉各种 startAccessingSecurityScopedResource()，直接抛给后台线程复制！
                DispatchQueue.global(qos: .utility).async {
                    do {
                        try FileManager.default.createDirectory(at: destinationURL.deletingLastPathComponent(), withIntermediateDirectories: true)
                        try FileManager.default.copyItem(at: sourceFileURL, to: destinationURL)
                        
                        DispatchQueue.main.async {
                            self.downloadProgress.removeValue(forKey: fileId)
                            self.cachedFileNames.insert(fileName)
                            self.cachedFileKeys.insert(self.cacheKey(for: file))
                            self.cacheStatusMessage = "《\(file.fileName)》缓存完成"
                        }
                    } catch {
                        print("❌ 离线缓存物理拷贝失败: \(error)")
                        DispatchQueue.main.async {
                            self.downloadProgress.removeValue(forKey: fileId)
                            self.cacheStatusMessage = "《\(file.fileName)》缓存失败"
                        }
                    }
                }
            } catch {
                print("❌ 准备下载失败: \(error.localizedDescription)")
                DispatchQueue.main.async {
                    self.downloadProgress.removeValue(forKey: fileId)
                    self.cacheStatusMessage = "《\(file.fileName)》缓存失败"
                }
            }
        }
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
