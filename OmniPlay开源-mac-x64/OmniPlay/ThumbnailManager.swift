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

    private var webFetchQueue: [EpisodeThumbnailTask] = []
    private var isFetching = false
    private var currentFetchTask: Task<Void, Never>? = nil
    private var currentFetchSourceID: Int64? = nil
    private var currentFetchID: UUID? = nil
    private var failedTMDBFileIDs: Set<String>
    private let failedTMDBStoreKey = "ThumbnailTMDBFailedFileIDs"
    private let failedTMDBMappingVersionKey = "ThumbnailTMDBMappingVersion"
    private let currentTMDBMappingVersion = 2
    private var tvSeasonSummaryCache: [Int: [TMDBSeasonSummary]] = [:]
    private let tvSeasonSummaryCacheQueue = DispatchQueue(label: "nan.omniplay.thumbnail.tmdb-season-summary")

    private init() {
        let cachePaths = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)
        thumbDirectory = cachePaths[0].appendingPathComponent("OmniPlayThumbnails")
        failedTMDBFileIDs = Set(UserDefaults.standard.stringArray(forKey: failedTMDBStoreKey) ?? [])
        if UserDefaults.standard.integer(forKey: failedTMDBMappingVersionKey) < currentTMDBMappingVersion {
            failedTMDBFileIDs.removeAll()
            UserDefaults.standard.set([], forKey: failedTMDBStoreKey)
            UserDefaults.standard.set(currentTMDBMappingVersion, forKey: failedTMDBMappingVersionKey)
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
        let fileId = task.fileId
        let title = task.title
        let season = task.season
        let episode = task.episode

        DispatchQueue.main.async {
            self.progressMessage = "获取剧照: \(title) S\(String(format: "%02d", season))E\(String(format: "%02d", episode))"
        }

        let fetchTask = Task {
            guard !Task.isCancelled, await self.sourceExists(task.sourceId) else {
                self.finishCurrentFetch(fetchID: fetchID)
                return
            }
            // 1. 先尝试向 TMDB 请求官方剧照
            let tmdbSuccess = await fetchFromTMDB(
                fileId: fileId,
                title: title,
                tmdbTVId: task.tmdbTVId,
                season: season,
                episode: episode
            )
            if !Task.isCancelled, await self.sourceExists(task.sourceId) {
                if tmdbSuccess {
                    clearTMDBFailure(for: fileId)
                } else {
                    markTMDBFailure(for: fileId)
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
            let stillPath = await fetchMappedEpisodeStillPath(tvId: tvId, season: season, episode: episode, language: tmdbLang)
            guard let stillPath else { return false }

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
            return true
        } catch {
            return false
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

    private func fetchMappedEpisodeStillPath(tvId: Int, season: Int, episode: Int, language: String) async -> String? {
        let candidates = await episodeStillCoordinateCandidates(tvId: tvId, requestedSeason: season, requestedEpisode: episode)
        for candidate in candidates {
            if let stillPath = await fetchEpisodeStillPath(tvId: tvId, season: candidate.season, episode: candidate.episode, language: language) {
                return stillPath
            }
        }
        return nil
    }

    private func fetchEpisodeStillPath(tvId: Int, season: Int, episode: Int, language: String) async -> String? {
        let episodeURL = "https://api.themoviedb.org/3/tv/\(tvId)/season/\(season)/episode/\(episode)?language=\(language)"
        do {
            guard let (data, response) = try await TMDBService.shared.requestTMDB(urlString: episodeURL),
                  response.statusCode == 200,
                  let json = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                return nil
            }
            if let stillPath = json["still_path"] as? String {
                return stillPath
            }
            return await fetchEpisodeStillImagePath(tvId: tvId, season: season, episode: episode, language: language)
        } catch {
            return nil
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

    private func fetchEpisodeStillImagePath(tvId: Int, season: Int, episode: Int, language: String) async -> String? {
        let imageLanguage = language == "en-US" ? "en,null" : "zh,null,en"
        let imagesURL = "https://api.themoviedb.org/3/tv/\(tvId)/season/\(season)/episode/\(episode)/images?include_image_language=\(imageLanguage)"
        do {
            guard let (data, response) = try await TMDBService.shared.requestTMDB(urlString: imagesURL),
                  response.statusCode == 200,
                  let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let stills = json["stills"] as? [[String: Any]],
                  !stills.isEmpty else {
                return nil
            }
            return stills
                .sorted { lhs, rhs in
                    let lhsScore = (lhs["vote_average"] as? Double ?? 0) + Double(lhs["vote_count"] as? Int ?? 0) * 0.1
                    let rhsScore = (rhs["vote_average"] as? Double ?? 0) + Double(rhs["vote_count"] as? Int ?? 0) * 0.1
                    return lhsScore > rhsScore
                }
                .compactMap { $0["file_path"] as? String }
                .first
        } catch {
            return nil
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
                    episode: resolvedInfo.episode,
                    sourceId: file.sourceId
                )
            )
        }
        return tasks
    }
}
