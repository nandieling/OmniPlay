import Foundation
import SwiftUI
import Combine
import GRDB

class ThumbnailManager: ObservableObject {
    static let shared = ThumbnailManager()
    
    @Published var progressMessage: String = ""
    
    // 本地图片存储目录
    let thumbDirectory: URL
    
    private struct WebFetchTask: Equatable {
        let fileId: String
        let title: String
        let season: Int
        let episode: Int
        let sourceId: Int64
    }

    private var webFetchQueue: [WebFetchTask] = []
    private var isFetching = false
    private var currentFetchTask: Task<Void, Never>? = nil
    private var currentFetchSourceID: Int64? = nil
    private var currentFetchID: UUID? = nil
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
    
    func startBatchWebFetch(tasks: [(String, String, Int, Int)], retryFailed: Bool = false) {
        DispatchQueue.global(qos: .background).async {
            guard let queue = AppDatabase.shared.dbQueue else { return }
            let convertedTasks: [WebFetchTask]
            do {
                convertedTasks = try queue.read { db in
                    try tasks.compactMap { task in
                        guard let sourceId = try Int64.fetchOne(
                            db,
                            sql: "SELECT sourceId FROM videoFile WHERE id = ?",
                            arguments: [task.0]
                        ) else {
                            return nil
                        }
                        return WebFetchTask(fileId: task.0, title: task.1, season: task.2, episode: task.3, sourceId: sourceId)
                    }
                }
            } catch {
                return
            }
            self.startBatchWebFetch(tasks: convertedTasks, retryFailed: retryFailed)
        }
    }

    private func startBatchWebFetch(tasks: [WebFetchTask], retryFailed: Bool = false) {
        DispatchQueue.global(qos: .background).async {
            for task in tasks {
                // 核心防重复拦截：本地已存在，直接跳过
                let localFileURL = self.thumbDirectory.appendingPathComponent("\(task.fileId).jpg")
                if FileManager.default.fileExists(atPath: localFileURL.path) { continue }
                if !retryFailed && self.failedTMDBFileIDs.contains(task.fileId) { continue }
                
                if !self.webFetchQueue.contains(where: { $0.fileId == task.fileId }) {
                    self.webFetchQueue.append(task)
                }
            }
            if !self.isFetching { self.processNextWebFetch() }
        }
    }

    func cancelTasks(forSourceID sourceID: Int64) {
        DispatchQueue.global(qos: .background).async {
            self.webFetchQueue.removeAll { $0.sourceId == sourceID }
            guard self.currentFetchSourceID == sourceID else { return }
            self.currentFetchTask?.cancel()
            self.currentFetchTask = nil
            self.currentFetchSourceID = nil
            self.currentFetchID = nil
            self.isFetching = false
            DispatchQueue.main.async {
                self.progressMessage = ""
            }
            if !self.webFetchQueue.isEmpty {
                self.processNextWebFetch()
            }
        }
    }
    
    private func processNextWebFetch() {
        guard !webFetchQueue.isEmpty else {
            isFetching = false
            currentFetchTask = nil
            currentFetchSourceID = nil
            currentFetchID = nil
            DispatchQueue.main.async {
                self.progressMessage = ""
            }
            return
        }
        
        isFetching = true
        let task = webFetchQueue.removeFirst()
        let fetchID = UUID()
        currentFetchID = fetchID
        currentFetchSourceID = task.sourceId
        
        DispatchQueue.main.async {
            self.progressMessage = "获取剧照: \(task.title) S\(String(format: "%02d", task.season))E\(String(format: "%02d", task.episode))"
        }
        
        let fetchTask = Task {
            guard !Task.isCancelled, await self.sourceExists(task.sourceId) else {
                self.finishCurrentFetch(fetchID: fetchID)
                return
            }
            // 1. 先尝试向 TMDB 请求官方剧照
            let tmdbSuccess = await fetchFromTMDB(task: task)
            if !Task.isCancelled, await self.sourceExists(task.sourceId) {
                if tmdbSuccess {
                    clearTMDBFailure(for: task.fileId)
                } else {
                    markTMDBFailure(for: task.fileId)
                }
            }
            
            try? await Task.sleep(nanoseconds: 1_000_000_000) // 延迟防封IP
            self.finishCurrentFetch(fetchID: fetchID)
        }
        currentFetchTask = fetchTask
    }

    private func finishCurrentFetch(fetchID: UUID) {
        DispatchQueue.global(qos: .background).async {
            guard self.currentFetchID == fetchID else { return }
            self.currentFetchTask = nil
            self.currentFetchSourceID = nil
            self.currentFetchID = nil
            self.processNextWebFetch()
        }
    }
    
    // ==========================================
    // 🎬 引擎 1：TMDB 剧照本地化下载
    // ==========================================
    private func fetchFromTMDB(task: WebFetchTask) async -> Bool {
        let apiKey = TMDBAPIConfig.resolvedApiKey
        guard !apiKey.isEmpty else { return false }
        guard !Task.isCancelled, await sourceExists(task.sourceId) else { return false }
        
        let cleanTitle = task.title.replacingOccurrences(of: #"\(\d{4}\)"#, with: "", options: .regularExpression).trimmingCharacters(in: .whitespacesAndNewlines)
        guard let encodedTitle = cleanTitle.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) else { return false }
        
        do {
            let searchURL = URL(string: "https://api.themoviedb.org/3/search/tv?query=\(encodedTitle)&language=zh-CN")!
            var req1 = URLRequest(url: searchURL)
            if apiKey.count >= 50 {
                req1.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
            } else {
                req1.url = URL(string: "https://api.themoviedb.org/3/search/tv?query=\(encodedTitle)&language=zh-CN&api_key=\(apiKey)")
            }
            
            let (data1, _) = try await URLSession.shared.data(for: req1)
            guard !Task.isCancelled, await sourceExists(task.sourceId) else { return false }
            guard let json1 = try JSONSerialization.jsonObject(with: data1) as? [String: Any],
                  let results = json1["results"] as? [[String: Any]],
                  let firstResult = results.first,
                  let tvId = firstResult["id"] as? Int else { return false }
            
            let episodeURL = URL(string: "https://api.themoviedb.org/3/tv/\(tvId)/season/\(task.season)/episode/\(task.episode)?language=zh-CN")!
            var req2 = URLRequest(url: episodeURL)
            if apiKey.count >= 50 {
                req2.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
            } else {
                req2.url = URL(string: "https://api.themoviedb.org/3/tv/\(tvId)/season/\(task.season)/episode/\(task.episode)?language=zh-CN&api_key=\(apiKey)")
            }
            
            let (data2, _) = try await URLSession.shared.data(for: req2)
            guard !Task.isCancelled, await sourceExists(task.sourceId) else { return false }
            guard let json2 = try JSONSerialization.jsonObject(with: data2) as? [String: Any],
                  let stillPath = json2["still_path"] as? String else { return false }
            
            let imageURL = URL(string: "https://image.tmdb.org/t/p/w500\(stillPath)")!
            let (imageData, _) = try await URLSession.shared.data(from: imageURL)
            guard !Task.isCancelled, await sourceExists(task.sourceId) else { return false }
            
            let localFileURL = thumbDirectory.appendingPathComponent("\(task.fileId).jpg")
            try imageData.write(to: localFileURL, options: .atomic)
            
            await MainActor.run { NotificationCenter.default.post(name: .libraryUpdated, object: nil) }
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

    private func sourceExists(_ sourceID: Int64) async -> Bool {
        guard let queue = AppDatabase.shared.dbQueue else { return false }
        do {
            return try await queue.read { db in
                let count = try Int.fetchOne(
                    db,
                    sql: "SELECT COUNT(*) FROM mediaSource WHERE id = ?",
                    arguments: [sourceID]
                ) ?? 0
                return count > 0
            }
        } catch {
            return false
        }
    }
    
    func enqueueEpisodeThumbnails(for movieId: Int64) {
        DispatchQueue.global(qos: .background).async {
            guard let queue = AppDatabase.shared.dbQueue else { return }
            do {
                let tasks = try queue.read { db -> [WebFetchTask] in
                    guard let movie = try Movie.fetchOne(db, key: movieId) else { return [] }
                    let fetchedFiles = try VideoFile.fetchAll(
                        db,
                        sql: """
                        SELECT videoFile.*
                        FROM videoFile
                        JOIN mediaSource ON mediaSource.id = videoFile.sourceId
                        WHERE videoFile.movieId = ?
                        """,
                        arguments: [movieId]
                    )
                    let files = fetchedFiles.filter { $0.mediaType != "direct" }
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
                    
                    var tasks: [WebFetchTask] = []
                    
                    for (index, file) in sortedFiles.enumerated() {
                        let parsed = MediaNameParser.parseEpisodeInfo(from: file.fileName, fallbackIndex: index)
                        tasks.append(WebFetchTask(fileId: file.id, title: movie.title, season: parsed.season, episode: parsed.episode, sourceId: file.sourceId))
                    }
                    return tasks
                }
                
                if !tasks.isEmpty {
                    self.startBatchWebFetch(tasks: tasks)
                }
            } catch {}
        }
    }

    func enqueueMissingEpisodeThumbnailsForLibrary(retryFailed: Bool = false) {
        DispatchQueue.global(qos: .background).async {
            guard let queue = AppDatabase.shared.dbQueue else { return }
            do {
                let tasks = try queue.read { db -> [WebFetchTask] in
                    let allMovies = try Movie.fetchAll(
                        db,
                        sql: """
                        SELECT DISTINCT movie.*
                        FROM movie
                        JOIN videoFile ON videoFile.movieId = movie.id
                        JOIN mediaSource ON mediaSource.id = videoFile.sourceId
                        """
                    )
                    var collected: [WebFetchTask] = []
                    
                    for movie in allMovies {
                        guard let movieID = movie.id else { continue }
                        let fetchedFiles = try VideoFile.fetchAll(
                            db,
                            sql: """
                            SELECT videoFile.*
                            FROM videoFile
                            JOIN mediaSource ON mediaSource.id = videoFile.sourceId
                            WHERE videoFile.movieId = ?
                            """,
                            arguments: [movieID]
                        )
                        let files = fetchedFiles.filter { $0.mediaType != "direct" }
                        guard !files.isEmpty else { continue }
                        
                        let isTVShow = movie.title.contains("季")
                            || movie.title.contains("集")
                            || files.contains { file in
                                let name = file.fileName
                                return name.range(of: #"[sS]\d{1,2}[eE]\d{1,2}"#, options: .regularExpression) != nil
                                    || name.range(of: #"[eE][pP]?\d{1,3}"#, options: .regularExpression) != nil
                                    || name.range(of: #"第\d{1,3}[集话]"#, options: .regularExpression) != nil
                            }
                        guard isTVShow else { continue }
                        
                        let sortedFiles = files.enumerated().sorted {
                            MediaNameParser.episodeSortKey(for: $0.element.fileName, fallbackIndex: $0.offset) <
                            MediaNameParser.episodeSortKey(for: $1.element.fileName, fallbackIndex: $1.offset)
                        }.map(\.element)
                        
                        for (index, file) in sortedFiles.enumerated() {
                            let localFileURL = self.thumbDirectory.appendingPathComponent("\(file.id).jpg")
                            if FileManager.default.fileExists(atPath: localFileURL.path) { continue }
                            
                            let parsed = MediaNameParser.parseEpisodeInfo(from: file.fileName, fallbackIndex: index)
                            collected.append(WebFetchTask(fileId: file.id, title: movie.title, season: parsed.season, episode: parsed.episode, sourceId: file.sourceId))
                        }
                    }
                    return collected
                }
                
                if !tasks.isEmpty {
                    self.startBatchWebFetch(tasks: tasks, retryFailed: retryFailed)
                }
            } catch {}
        }
    }
}
