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
        let sourceId: Int64
    }

    private struct TMDBSeasonSummary {
        let seasonNumber: Int
        let episodeCount: Int
    }

    // 🌟 失败文件黑名单：区分“TMDB 确认没有剧照”（长期跳过）与“抓取失败”（下次扫描重试）。
    // 旧版本把两类原因混在同一个数组里，网络抖动后剧照就永远不重试了。
    private var confirmedNoStillFileIDs: Set<String>
    private let confirmedNoStillStoreKey = "ThumbnailTMDBConfirmedNoStillFileIDs"
    private var failedFetchFileIDs: Set<String>
    private var failedFetchReasons: [String: String]
    private let failedFetchStoreKey = "ThumbnailTMDBFailedFetchFileIDs"
    private let failedFetchReasonsStoreKey = "ThumbnailTMDBFailedFetchReasons"
    private let legacyFailedStoreKey = "ThumbnailTMDBFailedFileIDs"
    // 旧版本把“TMDB 没有剧照”和“下载失败”都写进失败数组；升级后旧记录视为确认无剧照，
    // 避免 893 个“真实无剧照”的文件每轮扫描都重新打 API。
    private let failedTMDBMappingVersionKey = "ThumbnailTMDBMappingVersion"
    private let currentTMDBMappingVersion = 3
    private var tvSeasonSummaryCache: [Int: [TMDBSeasonSummary]] = [:]
    private let tvSeasonSummaryCacheQueue = DispatchQueue(label: "nan.omniplay.thumbnail.tmdb-season-summary")
    private var webFetchQueue: [EpisodeThumbnailTask] = []
    private var inFlightFileIDs: Set<String> = []
    private var inFlightBySource: [Int64: Set<String>] = [:]
    private var inFlightTasks: [String: Task<Void, Never>] = [:]
    private let fetchQueueLock = NSLock()
    private let maxConcurrentFetches = 3
    
    private init() {
        let cachePaths = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)
        thumbDirectory = cachePaths[0].appendingPathComponent("OmniPlayThumbnails")
        let legacyFailed = Set(UserDefaults.standard.stringArray(forKey: legacyFailedStoreKey) ?? [])
        confirmedNoStillFileIDs = Set(UserDefaults.standard.stringArray(forKey: confirmedNoStillStoreKey) ?? [])
        failedFetchFileIDs = Set(UserDefaults.standard.stringArray(forKey: failedFetchStoreKey) ?? [])
        failedFetchReasons = (UserDefaults.standard.dictionary(forKey: failedFetchReasonsStoreKey) as? [String: String]) ?? [:]
        if UserDefaults.standard.integer(forKey: failedTMDBMappingVersionKey) < currentTMDBMappingVersion {
            if UserDefaults.standard.object(forKey: confirmedNoStillStoreKey) == nil {
                confirmedNoStillFileIDs = legacyFailed
            }
            failedFetchFileIDs = legacyFailed
            failedFetchReasons = [:]
            UserDefaults.standard.set([], forKey: legacyFailedStoreKey)
            UserDefaults.standard.set(currentTMDBMappingVersion, forKey: failedTMDBMappingVersionKey)
            persistNoStillSet()
            persistFailedFetchSet()
        }
        
        if !FileManager.default.fileExists(atPath: thumbDirectory.path) {
            try? FileManager.default.createDirectory(at: thumbDirectory, withIntermediateDirectories: true)
        }
    }
    
    func startBatchWebFetch(tasks: [(String, String, Int64?, Int, Int)], forceRetry: Bool = false) {
        DispatchQueue.global(qos: .background).async {
            guard let queue = AppDatabase.shared.dbQueue else { return }
            let typedTasks: [EpisodeThumbnailTask]
            do {
                typedTasks = try queue.read { db in
                    try tasks.compactMap { task in
                        guard let sourceId = try Int64.fetchOne(
                            db,
                            sql: "SELECT sourceId FROM videoFile WHERE id = ?",
                            arguments: [task.0]
                        ) else {
                            return nil
                        }
                        return EpisodeThumbnailTask(
                            fileId: task.0,
                            title: task.1,
                            tmdbTVId: task.2,
                            season: task.3,
                            episode: task.4,
                            sourceId: sourceId
                        )
                    }
                }
            } catch {
                return
            }
            self.startBatchWebFetch(tasks: typedTasks, forceRetry: forceRetry)
        }
    }

    private func startBatchWebFetch(tasks: [EpisodeThumbnailTask], forceRetry: Bool = false) {
        DispatchQueue.global(qos: .background).async {
            var didAppend = false
            for task in tasks {
                // 核心防重复拦截：本地已存在，直接跳过
                let localFileURL = self.thumbDirectory.appendingPathComponent("\(task.fileId).jpg")
                if FileManager.default.fileExists(atPath: localFileURL.path) { continue }
                // TMDB 确认没有剧照的分集，不重试，避免每轮扫描重复打 API。
                if self.confirmedNoStillFileIDs.contains(task.fileId) {
                    if forceRetry {
                        self.confirmedNoStillFileIDs.remove(task.fileId)
                        self.persistNoStillSet()
                    } else {
                        continue
                    }
                }
                // 历史上的抓取失败（网络、5xx、超时）在新一轮扫描时自动重试。
                if forceRetry, self.failedFetchFileIDs.contains(task.fileId) {
                    self.failedFetchFileIDs.remove(task.fileId)
                    self.failedFetchReasons.removeValue(forKey: task.fileId)
                    self.persistFailedFetchSet()
                }
                
                self.fetchQueueLock.lock()
                let alreadyTracked = self.inFlightFileIDs.contains(task.fileId)
                    || self.webFetchQueue.contains(where: { $0.fileId == task.fileId })
                if !alreadyTracked {
                    self.inFlightFileIDs.insert(task.fileId)
                    self.webFetchQueue.append(task)
                }
                self.fetchQueueLock.unlock()
                if !alreadyTracked { didAppend = true }
            }
            if didAppend { self.processNextWebFetch() }
        }
    }

    func cancelTasks(forSourceID sourceID: Int64) {
        DispatchQueue.global(qos: .background).async {
            self.fetchQueueLock.lock()
            self.webFetchQueue.removeAll { $0.sourceId == sourceID }
            let cancelledFileIDs = Array(self.inFlightBySource[sourceID] ?? [])
            self.fetchQueueLock.unlock()
            for fileId in cancelledFileIDs {
                self.fetchQueueLock.lock()
                let worker = self.inFlightTasks[fileId]
                self.fetchQueueLock.unlock()
                worker?.cancel()
            }
            if self.isQueueIdle() {
                DispatchQueue.main.async { self.progressMessage = "" }
            }
            if !self.isQueueEmptyExcludingInFlight() {
                self.processNextWebFetch()
            }
        }
    }
    
    private func isQueueIdle() -> Bool {
        fetchQueueLock.lock()
        defer { fetchQueueLock.unlock() }
        return webFetchQueue.isEmpty && inFlightFileIDs.isEmpty
    }

    private func isQueueEmptyExcludingInFlight() -> Bool {
        fetchQueueLock.lock()
        defer { fetchQueueLock.unlock() }
        return webFetchQueue.isEmpty
    }

    private func processNextWebFetch() {
        fetchQueueLock.lock()
        guard !webFetchQueue.isEmpty else {
            fetchQueueLock.unlock()
            return
        }
        let pending = Array(webFetchQueue.prefix(maxConcurrentFetches))
        webFetchQueue.removeFirst(pending.count)
        fetchQueueLock.unlock()
        for task in pending {
            startFetchWorker(task)
        }
    }

    private func startFetchWorker(_ task: EpisodeThumbnailTask) {
        fetchQueueLock.lock()
        inFlightBySource[task.sourceId, default: []].insert(task.fileId)
        fetchQueueLock.unlock()
        updateProgressMessage(for: task)

        let workerTask = Task {
            defer {
                // 锁清理收敛到同步方法，避免在 async 上下文里直接 NSLock lock/unlock。
                self.finishFetchWorker(task)
            }
            guard !Task.isCancelled, await self.sourceExists(task.sourceId) else { return }
            // 1. 先尝试向 TMDB 请求官方剧照
            let outcome = await self.fetchFromTMDB(
                fileId: task.fileId,
                title: task.title,
                tmdbTVId: task.tmdbTVId,
                season: task.season,
                episode: task.episode
            )
            guard !Task.isCancelled, await self.sourceExists(task.sourceId) else { return }
            self.applyFetchOutcome(outcome, fileId: task.fileId)
        }
        fetchQueueLock.lock()
        inFlightTasks[task.fileId] = workerTask
        fetchQueueLock.unlock()
    }

    /// 抓取任务结束后同步清理在途状态（lock/unlock 必须发生在同步上下文中）。
    private func finishFetchWorker(_ task: EpisodeThumbnailTask) {
        fetchQueueLock.lock()
        inFlightFileIDs.remove(task.fileId)
        inFlightTasks.removeValue(forKey: task.fileId)
        inFlightBySource[task.sourceId, default: []].remove(task.fileId)
        if inFlightBySource[task.sourceId, default: []].isEmpty {
            inFlightBySource[task.sourceId] = nil
        }
        let hasMore = !webFetchQueue.isEmpty
        fetchQueueLock.unlock()
        if hasMore {
            processNextWebFetch()
        } else if isQueueIdle() {
            DispatchQueue.main.async { self.progressMessage = "" }
        }
    }

    private func updateProgressMessage(for task: EpisodeThumbnailTask) {
        fetchQueueLock.lock()
        let remaining = webFetchQueue.count + inFlightFileIDs.count
        fetchQueueLock.unlock()
        DispatchQueue.main.async {
            self.progressMessage = "获取剧照: \(task.title) S\(String(format: "%02d", task.season))E\(String(format: "%02d", task.episode)) · 剩 \(remaining) 个"
        }
    }

    private func applyFetchOutcome(_ outcome: EpisodeStillFetchOutcome, fileId: String) {
        switch outcome {
        case .success:
            if failedFetchFileIDs.remove(fileId) != nil || failedFetchReasons.removeValue(forKey: fileId) != nil {
                persistFailedFetchSet()
            }
        case .confirmedNoStill:
            if confirmedNoStillFileIDs.insert(fileId).inserted {
                persistNoStillSet()
            }
            if failedFetchFileIDs.remove(fileId) != nil || failedFetchReasons.removeValue(forKey: fileId) != nil {
                persistFailedFetchSet()
            }
        case .fetchFailed(let reason):
            failedFetchFileIDs.insert(fileId)
            failedFetchReasons[fileId] = reason
            persistFailedFetchSet()
        }
    }

    private func persistNoStillSet() {
        UserDefaults.standard.set(Array(confirmedNoStillFileIDs), forKey: confirmedNoStillStoreKey)
    }

    private func persistFailedFetchSet() {
        UserDefaults.standard.set(Array(failedFetchFileIDs), forKey: failedFetchStoreKey)
        UserDefaults.standard.set(failedFetchReasons, forKey: failedFetchReasonsStoreKey)
    }

    // 剧照抓取结果：区分“TMDB 确认无剧照”与“抓取失败”，后者下次扫描自动重试。
    private enum EpisodeStillFetchOutcome {
        case success
        case confirmedNoStill
        case fetchFailed(reason: String)
    }

    private func fetchFromTMDB(fileId: String, title: String, tmdbTVId: Int64?, season: Int, episode: Int) async -> EpisodeStillFetchOutcome {
        if let tmdbTVId, tmdbTVId > 0 {
            return await fetchEpisodeStill(fileId: fileId, tvId: Int(tmdbTVId), season: season, episode: episode)
        }

        let cleanTitle = title.replacingOccurrences(of: #"\(\d{4})"#, with: "", options: .regularExpression).trimmingCharacters(in: .whitespacesAndNewlines)
        guard !cleanTitle.isEmpty else {
            return .fetchFailed(reason: "title is empty")
        }

        do {
            guard let candidate = try await TMDBService.shared.multiSearch(
                query: cleanTitle,
                preferredMediaType: "tv",
                preferredSeason: season
            ) else {
                return .fetchFailed(reason: "TMDB search hit no result")
            }
            let mediaType = candidate.mediaType?.lowercased()
            guard mediaType == "tv" || (mediaType == nil && candidate.firstAirDate?.isEmpty == false) else {
                return .fetchFailed(reason: "TMDB search result is not a TV series")
            }
            return await fetchEpisodeStill(fileId: fileId, tvId: candidate.id, season: season, episode: episode)
        } catch {
            return .fetchFailed(reason: error.localizedDescription)
        }
    }

    private func fetchEpisodeStill(fileId: String, tvId: Int, season: Int, episode: Int) async -> EpisodeStillFetchOutcome {
        let appLang = UserDefaults.standard.string(forKey: "appLanguage") ?? "zh-Hans"
        let tmdbLang = appLang == "en" ? "en-US" : "zh-CN"

        do {
            let lookup = await fetchMappedEpisodeStillPath(tvId: tvId, season: season, episode: episode, language: tmdbLang)
            guard let stillPath = lookup.stillPath else {
                return lookup.sawTMDBResponse ? .confirmedNoStill : .fetchFailed(reason: "TMDB still request failed")
            }

            let imageURL = URL(string: "https://image.tmdb.org/t/p/w500\(stillPath)")!
            let (imageData, _) = try await URLSession.shared.data(from: imageURL)
            
            let localFileURL = thumbDirectory.appendingPathComponent("\(fileId).jpg")
            try imageData.write(to: localFileURL, options: .atomic)

            if UserDefaults.standard.bool(forKey: "enableLocalMetadataExport") {
                await exportLocalSidecarThumbnail(fileId: fileId, thumbnailURL: localFileURL)
            }
            
            await MainActor.run {
                NotificationCenter.default.post(name: NSNotification.Name("ThumbnailGenerated_\(fileId)"), object: nil)
            }
            return .success
        } catch {
            return .fetchFailed(reason: error.localizedDescription)
        }
    }

    private func exportLocalSidecarThumbnail(fileId: String, thumbnailURL: URL) async {
        guard let queue = AppDatabase.shared.dbQueue else { return }
        let snapshot = try? await queue.read { db -> (VideoFile, MediaSource)? in
            guard let file = try VideoFile.fetchOne(db, key: fileId),
                  let source = try MediaSource.fetchOne(db, key: file.sourceId) else {
                return nil
            }
            return (file, source)
        }
        guard let (file, source) = snapshot, source.protocolKind == .local else { return }
        let videoURL = URL(fileURLWithPath: MediaSourceProtocol.local.normalizedBaseURL(source.baseUrl))
            .appendingPathComponent(file.relativePath)
        LocalMetadataSidecarStore.shared.exportEpisodeThumbnail(sourceURL: thumbnailURL, videoURL: videoURL)
    }

    private struct EpisodeStillLookup {
        let stillPath: String?
        let sawTMDBResponse: Bool
    }

    private func fetchMappedEpisodeStillPath(tvId: Int, season: Int, episode: Int, language: String) async -> EpisodeStillLookup {
        let candidates = await episodeStillCoordinateCandidates(tvId: tvId, requestedSeason: season, requestedEpisode: episode)
        var sawResponse = false
        var hasTransientFailure = false
        for candidate in candidates {
            let lookup = await fetchEpisodeStillPath(tvId: tvId, season: candidate.season, episode: candidate.episode, language: language)
            if lookup.stillPath != nil {
                return lookup
            }
            if lookup.sawTMDBResponse {
                sawResponse = true
            } else {
                // 任一候选季请求是网络/5xx 类临时失败，整体按“抓取失败”处理，下次扫描重试。
                hasTransientFailure = true
            }
        }
        return EpisodeStillLookup(stillPath: nil, sawTMDBResponse: sawResponse && !hasTransientFailure)
    }

    private func fetchEpisodeStillPath(tvId: Int, season: Int, episode: Int, language: String) async -> EpisodeStillLookup {
        let episodeURL = "https://api.themoviedb.org/3/tv/\(tvId)/season/\(season)/episode/\(episode)?language=\(language)"
        do {
            guard let (data, response) = try await TMDBService.shared.requestTMDB(urlString: episodeURL) else {
                return EpisodeStillLookup(stillPath: nil, sawTMDBResponse: false)
            }
            // 404 说明 TMDB 里不存在这个季/集，重试也没有意义，按“确认无剧照”处理。
            guard response.statusCode == 200 else {
                return EpisodeStillLookup(stillPath: nil, sawTMDBResponse: response.statusCode == 404)
            }
            guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                return EpisodeStillLookup(stillPath: nil, sawTMDBResponse: true)
            }
            if let stillPath = json["still_path"] as? String {
                return EpisodeStillLookup(stillPath: stillPath, sawTMDBResponse: true)
            }
            let imageLookup = await fetchEpisodeStillImagePath(tvId: tvId, season: season, episode: episode, language: language)
            if let stillPath = imageLookup.stillPath {
                return EpisodeStillLookup(stillPath: stillPath, sawTMDBResponse: true)
            }
            return EpisodeStillLookup(stillPath: nil, sawTMDBResponse: true)
        } catch {
            return EpisodeStillLookup(stillPath: nil, sawTMDBResponse: false)
        }
    }

    private func episodeStillCoordinateCandidates(tvId: Int, requestedSeason: Int, requestedEpisode: Int) async -> [(season: Int, episode: Int)] {
        guard requestedEpisode > 0 else { return [(requestedSeason, requestedEpisode)] }
        var candidates: [(season: Int, episode: Int)] = [(requestedSeason, requestedEpisode)]

        let seasons = await fetchTVSeasonSummaries(tvId: tvId)
        let regularSeasons = seasons
            .filter { $0.seasonNumber > 0 && $0.episodeCount > 0 }
            .sorted { lhs, rhs in
                if lhs.seasonNumber != rhs.seasonNumber { return lhs.seasonNumber < rhs.seasonNumber }
                return lhs.episodeCount > rhs.episodeCount
            }

        // 有些动画在 TMDB 只维护一个长 Season 1，本地资源则按 S02/S03 发布；
        // 不改变本地显示季集，只在剧照请求层映射到可容纳该集数的 TMDB 季。
        if regularSeasons.count == 1,
           let onlySeason = regularSeasons.first,
           onlySeason.seasonNumber != requestedSeason,
           onlySeason.episodeCount >= requestedEpisode {
            appendUniqueCandidate((onlySeason.seasonNumber, requestedEpisode), to: &candidates)
        }

        if let seasonOne = regularSeasons.first(where: { $0.seasonNumber == 1 && $0.episodeCount >= requestedEpisode }) {
            appendUniqueCandidate((seasonOne.seasonNumber, requestedEpisode), to: &candidates)
        }

        for summary in regularSeasons.sorted(by: { lhs, rhs in
            if lhs.episodeCount != rhs.episodeCount { return lhs.episodeCount > rhs.episodeCount }
            return lhs.seasonNumber < rhs.seasonNumber
        }) where summary.episodeCount >= requestedEpisode {
            appendUniqueCandidate((summary.seasonNumber, requestedEpisode), to: &candidates)
        }

        return candidates
    }

    private func appendUniqueCandidate(_ candidate: (season: Int, episode: Int), to candidates: inout [(season: Int, episode: Int)]) {
        guard !candidates.contains(where: { $0.season == candidate.season && $0.episode == candidate.episode }) else { return }
        candidates.append(candidate)
    }

    private func fetchTVSeasonSummaries(tvId: Int) async -> [TMDBSeasonSummary] {
        if let cached = tvSeasonSummaryCacheQueue.sync(execute: { tvSeasonSummaryCache[tvId] }) {
            return cached
        }

        let url = "https://api.themoviedb.org/3/tv/\(tvId)?language=en-US"
        do {
            guard let (data, response) = try await TMDBService.shared.requestTMDB(urlString: url),
                  response.statusCode == 200,
                  let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let seasons = json["seasons"] as? [[String: Any]] else {
                return []
            }
            let summaries = seasons.compactMap { item -> TMDBSeasonSummary? in
                guard let seasonNumber = item["season_number"] as? Int,
                      let episodeCount = item["episode_count"] as? Int,
                      episodeCount > 0 else { return nil }
                return TMDBSeasonSummary(seasonNumber: seasonNumber, episodeCount: episodeCount)
            }
            tvSeasonSummaryCacheQueue.sync { tvSeasonSummaryCache[tvId] = summaries }
            return summaries
        } catch {
            return []
        }
    }

    private func fetchEpisodeStillImagePath(tvId: Int, season: Int, episode: Int, language: String) async -> EpisodeStillLookup {
        let imageLanguage = language == "en-US" ? "en,null" : "zh,null,en"
        let imagesURL = "https://api.themoviedb.org/3/tv/\(tvId)/season/\(season)/episode/\(episode)/images?include_image_language=\(imageLanguage)"
        do {
            guard let (data, response) = try await TMDBService.shared.requestTMDB(urlString: imagesURL) else {
                return EpisodeStillLookup(stillPath: nil, sawTMDBResponse: false)
            }
            guard response.statusCode == 200 else {
                return EpisodeStillLookup(stillPath: nil, sawTMDBResponse: response.statusCode == 404)
            }
            guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let stills = json["stills"] as? [[String: Any]] else {
                return EpisodeStillLookup(stillPath: nil, sawTMDBResponse: true)
            }
            guard !stills.isEmpty else {
                return EpisodeStillLookup(stillPath: nil, sawTMDBResponse: true)
            }
            let best = stills
                .sorted { lhs, rhs in
                    let lhsScore = (lhs["vote_average"] as? Double ?? 0) + Double(lhs["vote_count"] as? Int ?? 0) * 0.1
                    let rhsScore = (rhs["vote_average"] as? Double ?? 0) + Double(rhs["vote_count"] as? Int ?? 0) * 0.1
                    return lhsScore > rhsScore
                }
                .compactMap { $0["file_path"] as? String }
                .first
            return EpisodeStillLookup(stillPath: best, sawTMDBResponse: true)
        } catch {
            return EpisodeStillLookup(stillPath: nil, sawTMDBResponse: false)
        }
    }

    private func resetNoStillMarks(for fileId: String) {
        var changed = false
        if confirmedNoStillFileIDs.remove(fileId) != nil { changed = true }
        if failedFetchFileIDs.remove(fileId) != nil { changed = true }
        if failedFetchReasons.removeValue(forKey: fileId) != nil { changed = true }
        if changed {
            persistNoStillSet()
            persistFailedFetchSet()
        }
    }

    func thumbnailURL(for fileId: String) -> URL {
        thumbDirectory.appendingPathComponent("\(fileId).jpg")
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

    func replaceThumbnail(fileId: String, with sourceURL: URL) throws {
        let destinationURL = thumbnailURL(for: fileId)
        if FileManager.default.fileExists(atPath: destinationURL.path) {
            try FileManager.default.removeItem(at: destinationURL)
        }
        try FileManager.default.copyItem(at: sourceURL, to: destinationURL)
        resetNoStillMarks(for: fileId)

        DispatchQueue.main.async {
            NotificationCenter.default.post(name: NSNotification.Name("ThumbnailGenerated_\(fileId)"), object: nil)
        }
    }

    func removeAssets(for fileIDs: [String]) {
        guard !fileIDs.isEmpty else { return }
        var changed = false
        for fileId in fileIDs {
            let localFileURL = thumbDirectory.appendingPathComponent("\(fileId).jpg")
            try? FileManager.default.removeItem(at: localFileURL)
            if confirmedNoStillFileIDs.remove(fileId) != nil { changed = true }
            if failedFetchFileIDs.remove(fileId) != nil { changed = true }
            if failedFetchReasons.removeValue(forKey: fileId) != nil { changed = true }
        }
        if changed {
            persistNoStillSet()
            persistFailedFetchSet()
        }
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
    
    func enqueueMissingEpisodeThumbnailsAcrossLibrary(orderedMovieIDs: [Int64] = []) {
        DispatchQueue.global(qos: .background).async {
            guard let queue = AppDatabase.shared.dbQueue else { return }
            do {
                let tasks = try queue.read { db -> [EpisodeThumbnailTask] in
                    var order: [Int64: Int] = [:]
                    for (index, movieID) in orderedMovieIDs.enumerated() where order[movieID] == nil {
                        order[movieID] = index
                    }
                    let movies: [Movie]
                    if !orderedMovieIDs.isEmpty {
                        var seenMovieIDs = Set<Int64>()
                        var orderedMovies: [Movie] = []
                        for movieID in orderedMovieIDs where seenMovieIDs.insert(movieID).inserted {
                            if let movie = try Movie.fetchOne(db, key: movieID) {
                                orderedMovies.append(movie)
                            }
                        }
                        movies = orderedMovies
                    } else {
                        movies = try Movie.fetchAll(db).sorted { lhs, rhs in
                            let lhsRank = lhs.id.flatMap { order[$0] } ?? Int.max
                            let rhsRank = rhs.id.flatMap { order[$0] } ?? Int.max
                            if lhsRank != rhsRank { return lhsRank < rhsRank }
                            return lhs.title.localizedStandardCompare(rhs.title) == .orderedAscending
                        }
                    }
                    var aggregated: [EpisodeThumbnailTask] = []
                    for movie in movies {
                        if let mid = movie.id {
                            aggregated.append(contentsOf: try self.buildEpisodeTasks(for: mid, in: db, missingOnly: true))
                        }
                    }
                    return aggregated
                }
                let skippedNoStill = self.confirmedNoStillFileIDs.count

                let pendingRetry = self.failedFetchFileIDs.count

                print("[OmniPlay][剧照] 补抓任务：\(tasks.count) 个缺失分集剧照待处理，已确认无剧照 \(skippedNoStill) 个，待重试 \(pendingRetry) 个。")

                if !tasks.isEmpty {
                    self.startBatchWebFetch(tasks: tasks, forceRetry: true)
                }
            } catch {

                print("[OmniPlay][剧照] 补抓任务读取数据库失败：\(error.localizedDescription)")
            }
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
                    episode: resolvedInfo.episode,
                    sourceId: file.sourceId
                )
            )
        }
        return tasks
    }
}
