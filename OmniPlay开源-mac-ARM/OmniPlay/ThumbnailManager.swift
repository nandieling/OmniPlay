import Foundation
import SwiftUI
import Combine
import GRDB

class ThumbnailManager: ObservableObject {
    static let shared = ThumbnailManager()
    
    @Published var progressMessage: String = ""
    
    // 本地图片存储目录
    let thumbDirectory: URL
    
    private struct EpisodeThumbnailTask {
        let fileId: String
        let title: String
        let tmdbTVId: Int64?
        let season: Int
        let episode: Int
    }

    private var webFetchQueue: [EpisodeThumbnailTask] = []
    private var isFetching = false
    private var failedTMDBFileIDs: Set<String>
    private let failedTMDBStoreKey = "ThumbnailTMDBFailedFileIDs"
    
    private init() {
        let cachePaths = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)
        thumbDirectory = cachePaths[0].appendingPathComponent("OmniPlayThumbnails")
        failedTMDBFileIDs = Set(UserDefaults.standard.stringArray(forKey: failedTMDBStoreKey) ?? [])
        
        if !FileManager.default.fileExists(atPath: thumbDirectory.path) {
            try? FileManager.default.createDirectory(at: thumbDirectory, withIntermediateDirectories: true)
        }
    }
    
    func startBatchWebFetch(tasks: [(String, String, Int64?, Int, Int)], forceRetry: Bool = false) {
        let typedTasks = tasks.map {
            EpisodeThumbnailTask(fileId: $0.0, title: $0.1, tmdbTVId: $0.2, season: $0.3, episode: $0.4)
        }
        startBatchWebFetch(tasks: typedTasks, forceRetry: forceRetry)
    }

    private func startBatchWebFetch(tasks: [EpisodeThumbnailTask], forceRetry: Bool = false) {
        DispatchQueue.global(qos: .background).async {
            for task in tasks {
                // 核心防重复拦截：本地已存在，直接跳过
                let localFileURL = self.thumbDirectory.appendingPathComponent("\(task.fileId).jpg")
                if FileManager.default.fileExists(atPath: localFileURL.path) { continue }
                if self.failedTMDBFileIDs.contains(task.fileId) {
                    if forceRetry {
                        self.failedTMDBFileIDs.remove(task.fileId)
                        UserDefaults.standard.set(Array(self.failedTMDBFileIDs), forKey: self.failedTMDBStoreKey)
                    } else {
                        continue
                    }
                }
                
                if !self.webFetchQueue.contains(where: { $0.fileId == task.fileId }) {
                    self.webFetchQueue.append(task)
                }
            }
            if !self.isFetching { self.processNextWebFetch() }
        }
    }
    
    private func processNextWebFetch() {
        guard !webFetchQueue.isEmpty else {
            DispatchQueue.main.async {
                self.progressMessage = ""
                self.isFetching = false
            }
            return
        }
        
        isFetching = true
        let task = webFetchQueue.removeFirst()
        let fileId = task.fileId
        let title = task.title
        let season = task.season
        let episode = task.episode
        
        DispatchQueue.main.async {
            self.progressMessage = "获取剧照: \(title) S\(String(format: "%02d", season))E\(String(format: "%02d", episode))"
        }
        
        Task {
            // 1. 先尝试向 TMDB 请求官方剧照
            let tmdbSuccess = await fetchFromTMDB(
                fileId: fileId,
                title: title,
                tmdbTVId: task.tmdbTVId,
                season: season,
                episode: episode
            )
            if tmdbSuccess {
                clearTMDBFailure(for: fileId)
            } else {
                markTMDBFailure(for: fileId)
            }
            
            try? await Task.sleep(nanoseconds: 1_000_000_000) // 延迟防封IP
            self.processNextWebFetch()
        }
    }
    
    // ==========================================
    // 🎬 引擎 1：TMDB 剧照本地化下载
    // ==========================================
    private func fetchFromTMDB(fileId: String, title: String, tmdbTVId: Int64?, season: Int, episode: Int) async -> Bool {
        if let tmdbTVId, tmdbTVId > 0 {
            return await fetchEpisodeStill(fileId: fileId, tvId: Int(tmdbTVId), season: season, episode: episode)
        }

        let cleanTitle = title.replacingOccurrences(of: #"\(\d{4}\)"#, with: "", options: .regularExpression).trimmingCharacters(in: .whitespacesAndNewlines)
        guard !cleanTitle.isEmpty else { return false }

        do {
            guard let candidate = try await TMDBService.shared.multiSearch(
                query: cleanTitle,
                preferredMediaType: "tv",
                preferredSeason: season
            ) else {
                return false
            }
            let mediaType = candidate.mediaType?.lowercased()
            guard mediaType == "tv" || (mediaType == nil && candidate.firstAirDate?.isEmpty == false) else {
                return false
            }
            return await fetchEpisodeStill(fileId: fileId, tvId: candidate.id, season: season, episode: episode)
        } catch {
            return false
        }
    }

    private func fetchEpisodeStill(fileId: String, tvId: Int, season: Int, episode: Int) async -> Bool {
        let appLang = UserDefaults.standard.string(forKey: "appLanguage") ?? "zh-Hans"
        let tmdbLang = appLang == "en" ? "en-US" : "zh-CN"

        do {
            let episodeURL = "https://api.themoviedb.org/3/tv/\(tvId)/season/\(season)/episode/\(episode)?language=\(tmdbLang)"
            guard let (data2, response2) = try await TMDBService.shared.requestTMDB(urlString: episodeURL),
                  response2.statusCode == 200 else {
                return false
            }
            guard let json2 = try JSONSerialization.jsonObject(with: data2) as? [String: Any],
                  let stillPath = json2["still_path"] as? String else { return false }
            
            let imageURL = URL(string: "https://image.tmdb.org/t/p/w500\(stillPath)")!
            let (imageData, _) = try await URLSession.shared.data(from: imageURL)
            
            let localFileURL = thumbDirectory.appendingPathComponent("\(fileId).jpg")
            try imageData.write(to: localFileURL, options: .atomic)
            
            await MainActor.run {
                NotificationCenter.default.post(name: NSNotification.Name("ThumbnailGenerated_\(fileId)"), object: nil)
            }
            return true
        } catch {
            return false
        }
    }

    private func markTMDBFailure(for fileId: String) {
        guard !failedTMDBFileIDs.contains(fileId) else { return }
        failedTMDBFileIDs.insert(fileId)
        UserDefaults.standard.set(Array(failedTMDBFileIDs), forKey: failedTMDBStoreKey)
    }

    private func clearTMDBFailure(for fileId: String) {
        guard failedTMDBFileIDs.contains(fileId) else { return }
        failedTMDBFileIDs.remove(fileId)
        UserDefaults.standard.set(Array(failedTMDBFileIDs), forKey: failedTMDBStoreKey)
    }

    func thumbnailURL(for fileId: String) -> URL {
        thumbDirectory.appendingPathComponent("\(fileId).jpg")
    }

    func replaceThumbnail(fileId: String, with sourceURL: URL) throws {
        let destinationURL = thumbnailURL(for: fileId)
        if FileManager.default.fileExists(atPath: destinationURL.path) {
            try FileManager.default.removeItem(at: destinationURL)
        }
        try FileManager.default.copyItem(at: sourceURL, to: destinationURL)
        clearTMDBFailure(for: fileId)

        DispatchQueue.main.async {
            NotificationCenter.default.post(name: NSNotification.Name("ThumbnailGenerated_\(fileId)"), object: nil)
        }
    }

    func removeAssets(for fileIDs: [String]) {
        guard !fileIDs.isEmpty else { return }
        for fileId in fileIDs {
            let localFileURL = thumbDirectory.appendingPathComponent("\(fileId).jpg")
            try? FileManager.default.removeItem(at: localFileURL)
            failedTMDBFileIDs.remove(fileId)
        }
        UserDefaults.standard.set(Array(failedTMDBFileIDs), forKey: failedTMDBStoreKey)
    }
    
    func enqueueEpisodeThumbnails(for movieId: Int64) {
        DispatchQueue.global(qos: .background).async {
            guard let queue = AppDatabase.shared.dbQueue else { return }
            do {
                let tasks = try queue.read { db -> [EpisodeThumbnailTask] in
                    try self.buildEpisodeTasks(for: movieId, in: db, missingOnly: false)
                }
                if !tasks.isEmpty {
                    self.startBatchWebFetch(tasks: tasks)
                }
            } catch {}
        }
    }
    
    func enqueueMissingEpisodeThumbnailsAcrossLibrary() {
        DispatchQueue.global(qos: .background).async {
            guard let queue = AppDatabase.shared.dbQueue else { return }
            do {
                let tasks = try queue.read { db -> [EpisodeThumbnailTask] in
                    let movies = try Movie.fetchAll(db)
                    var aggregated: [EpisodeThumbnailTask] = []
                    for movie in movies {
                        if let mid = movie.id {
                            aggregated.append(contentsOf: try self.buildEpisodeTasks(for: mid, in: db, missingOnly: true))
                        }
                    }
                    return aggregated
                }
                if !tasks.isEmpty {
                    self.startBatchWebFetch(tasks: tasks, forceRetry: true)
                }
            } catch {}
        }
    }

    private func buildEpisodeTasks(for movieId: Int64, in db: Database, missingOnly: Bool) throws -> [EpisodeThumbnailTask] {
        guard let movie = try Movie.fetchOne(db, key: movieId) else { return [] }
        let files = try VideoFile.fetchVisibleFiles(movieId: movieId, in: db).filter { $0.mediaType != "direct" }
        guard !files.isEmpty else { return [] }
        
        let isTVShow = movie.title.contains("季")
            || movie.title.contains("集")
            || files.contains { file in
                let name = file.fileName
                return name.range(of: #"[sS]\d{1,2}[eE]\d{1,2}"#, options: .regularExpression) != nil
                    || name.range(of: #"[eE][pP]?\d{1,3}"#, options: .regularExpression) != nil
                    || name.range(of: #"第\d{1,3}[集话]"#, options: .regularExpression) != nil
            }
        guard isTVShow else { return [] }
        
        let sortedFiles = files.enumerated().sorted {
            MediaNameParser.episodeSortKey(for: $0.element.fileName, fallbackIndex: $0.offset) <
            MediaNameParser.episodeSortKey(for: $1.element.fileName, fallbackIndex: $1.offset)
        }.map(\.element)
        
        var tasks: [EpisodeThumbnailTask] = []
        for (index, file) in sortedFiles.enumerated() {
            let localPath = thumbDirectory.appendingPathComponent("\(file.id).jpg").path
            if missingOnly && FileManager.default.fileExists(atPath: localPath) { continue }
            let resolvedInfo = EpisodeMetadataOverrideStore.shared.resolvedEpisodeInfo(
                fileId: file.id,
                fileName: file.fileName,
                fallbackIndex: index
            )
            tasks.append(
                EpisodeThumbnailTask(
                    fileId: file.id,
                    title: movie.title,
                    tmdbTVId: movie.id ?? movieId,
                    season: resolvedInfo.season,
                    episode: resolvedInfo.episode
                )
            )
        }
        return tasks
    }
}
