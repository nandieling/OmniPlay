import Foundation

private actor TMDBRateLimiter {
    private var nextAllowedUptimeNs: UInt64 = 0
    private let minIntervalNs: UInt64

    init(requestsPerSecond: Double) {
        let safeRPS = max(0.5, requestsPerSecond)
        minIntervalNs = UInt64(1_000_000_000.0 / safeRPS)
    }

    func acquire() async {
        let now = DispatchTime.now().uptimeNanoseconds
        if now < nextAllowedUptimeNs {
            let waitNs = nextAllowedUptimeNs - now
            try? await Task.sleep(nanoseconds: waitNs)
        }
        let current = DispatchTime.now().uptimeNanoseconds
        nextAllowedUptimeNs = current + minIntervalNs
    }
}

private struct TMDBTranslationsResponse: Codable {
    struct TranslationItem: Codable {
        struct TranslationData: Codable {
            let title: String?
            let name: String?
        }
        let iso639_1: String?
        let iso3166_1: String?
        let data: TranslationData?

        enum CodingKeys: String, CodingKey {
            case iso639_1 = "iso_639_1"
            case iso3166_1 = "iso_3166_1"
            case data
        }
    }
    let translations: [TranslationItem]
}

enum TMDBAPIConfig {
    static let publicApiKey = ""

    static var isPublicAPIEnabled: Bool {
        if UserDefaults.standard.object(forKey: "usePublicTMDBApi") == nil { return true }
        return UserDefaults.standard.bool(forKey: "usePublicTMDBApi")
    }

    static var resolvedApiKey: String {
        let userKey = (UserDefaults.standard.string(forKey: "tmdbApiKey") ?? "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        if !userKey.isEmpty { return userKey }
        return isPublicAPIEnabled ? publicApiKey : ""
    }
}

class TMDBService {
    static let shared = TMDBService()
    
    private var apiKey: String {
        TMDBAPIConfig.resolvedApiKey
    }
    
    private let baseURL = "https://api.themoviedb.org/3"
    private var tvSeasonCountCache: [Int: Int] = [:]
    private var tvSeasonAirYearCache: [String: Int] = [:]
    private let seasonCacheQueue = DispatchQueue(label: "nan.omniplay.tmdb.season-cache")
    private var localizedResultCache: [String: TMDBResult] = [:]
    private let localizedCacheQueue = DispatchQueue(label: "nan.omniplay.tmdb.localized-cache")
    private let rateLimiter = TMDBRateLimiter(requestsPerSecond: 3.2)
    private let tmdbSession: URLSession
    
    private init() {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 12
        config.timeoutIntervalForResource = 20
        config.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        config.urlCache = nil
        config.httpCookieStorage = nil
        config.httpShouldSetCookies = false
        config.connectionProxyDictionary = [:]
        tmdbSession = URLSession(configuration: config)
    }
    
    // 🌟 自动刮削调用入口（带年份参数）
    func multiSearch(
        query: String,
        year: String? = nil,
        preferredMediaType: String? = nil,
        preferredSeason: Int? = nil,
        secondaryQuery: String? = nil
    ) async throws -> TMDBResult? {
        let candidates = try await searchCandidates(
            query: query,
            year: year,
            preferredMediaType: preferredMediaType,
            preferredSeason: preferredSeason,
            secondaryQuery: secondaryQuery
        )
        return candidates.first
    }
    
    // 🌟 手动搜索弹窗调用入口（带年份加权排序）
    func searchCandidates(
        query: String,
        year: String? = nil,
        preferredMediaType: String? = nil,
        preferredSeason: Int? = nil,
        secondaryQuery: String? = nil
    ) async throws -> [TMDBResult] {
        guard let encodedQuery = query.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) else { return [] }
        
        do {
            return try await searchFromTMDB(
                query: query,
                encodedQuery: encodedQuery,
                year: year,
                preferredMediaType: preferredMediaType,
                preferredSeason: preferredSeason,
                secondaryQuery: secondaryQuery
            )
        } catch {
            if Task.isCancelled { throw CancellationError() }
            return []
        }
    }

    private func searchFromTMDB(
        query: String,
        encodedQuery: String,
        year: String? = nil,
        preferredMediaType: String? = nil,
        preferredSeason: Int? = nil,
        secondaryQuery: String? = nil
    ) async throws -> [TMDBResult] {
        let appLang = UserDefaults.standard.string(forKey: "appLanguage") ?? "zh-Hans"
        let tmdbLang = appLang == "en" ? "en-US" : "zh-CN"

        var aggregatedWithLang: [(TMDBResult, String)] = []
        let languageOrder: [String] = (tmdbLang == "en-US") ? ["en-US", "zh-CN"] : [tmdbLang, "en-US"]
        for lang in languageOrder {
            // 拉取前两页，减少正确候选不在第一页的情况。
            for page in [1, 2] {
                let fetched = try await requestSearchPage(encodedQuery: encodedQuery, language: lang, page: page)
                aggregatedWithLang.append(contentsOf: fetched.map { ($0, lang) })
            }
        }

        // 同 ID 结果做语言优先合并：优先保留主语言条目的 title/name，避免中文界面被英文覆盖。
        var mergedByKey: [String: (item: TMDBResult, langRank: Int)] = [:]
        for (item, lang) in aggregatedWithLang {
            guard item.mediaType != "person", item.displayTitle != "未知名称" else { continue }
            let key = "\(item.mediaType ?? "unknown")#\(item.id)"
            let rank = languageOrder.firstIndex(of: lang) ?? Int.max
            if let existing = mergedByKey[key] {
                if rank < existing.langRank {
                    mergedByKey[key] = (mergeLocalized(preferred: item, fallback: existing.item), rank)
                } else {
                    mergedByKey[key] = (mergeLocalized(preferred: existing.item, fallback: item), existing.langRank)
                }
            } else {
                mergedByKey[key] = (item, rank)
            }
        }
        var validMedia = mergedByKey.values.map(\.item)

        var seasonCountByTVId: [Int: Int] = [:]
        var seasonAirYearByTVId: [Int: Int] = [:]
        if (preferredMediaType?.lowercased() == "tv"), let season = preferredSeason, season > 0 {
            let tvIDs = validMedia
                .filter { ($0.mediaType ?? "").lowercased() == "tv" }
                .sorted { ($0.popularity ?? 0) > ($1.popularity ?? 0) }
                .prefix(12)
                .map(\.id)
            for tvId in tvIDs {
                if let count = try await fetchTVSeasonCount(tvId: tvId) {
                    seasonCountByTVId[tvId] = count
                    let mappedSeason = min(max(1, season), max(1, count))
                    if let airYear = try await fetchTVSeasonAirYear(tvId: tvId, season: mappedSeason) {
                        seasonAirYearByTVId[tvId] = airYear
                    }
                }
            }
        }

        // 🌟 本地排序：标题匹配优先 + 年份提权 + 热度兜底
        validMedia.sort { item1, item2 in
            let score1 = calculateScore(
                item: item1,
                query: query,
                targetYear: year,
                preferredMediaType: preferredMediaType,
                preferredSeason: preferredSeason,
                seasonCountByTVId: seasonCountByTVId,
                seasonAirYearByTVId: seasonAirYearByTVId,
                secondaryQuery: secondaryQuery
            )
            let score2 = calculateScore(
                item: item2,
                query: query,
                targetYear: year,
                preferredMediaType: preferredMediaType,
                preferredSeason: preferredSeason,
                seasonCountByTVId: seasonCountByTVId,
                seasonAirYearByTVId: seasonAirYearByTVId,
                secondaryQuery: secondaryQuery
            )

            // 如果分数不一样，谁分高谁排前面；如果分数一样，比官方热度
            if score1 != score2 { return score1 > score2 }
            return (item1.popularity ?? 0) > (item2.popularity ?? 0)
        }
        let localized = try await localizeCandidatesForCurrentLanguage(validMedia)
        return localized
    }
    
    // 🌟 核心计分逻辑：标题语义优先，年份提权，热度兜底
    private func calculateScore(
        item: TMDBResult,
        query: String,
        targetYear: String?,
        preferredMediaType: String?,
        preferredSeason: Int?,
        seasonCountByTVId: [Int: Int],
        seasonAirYearByTVId: [Int: Int],
        secondaryQuery: String?
    ) -> Double {
        var baseScore: Double = item.popularity ?? 0.0
        let queryNorm = normalizeTitle(query)
        let candidateTitles = matchableTitles(for: item)
        let normalizedCandidates = candidateTitles.map { normalizeTitle($0) }.filter { !$0.isEmpty }
        let mediaType = (item.mediaType ?? "").lowercased()
        
        // 标题精确/包含关系优先，避免热门但不精确结果抢位。
        if !queryNorm.isEmpty {
            let bestTitleBonus = normalizedCandidates.map { candidate -> Double in
                if candidate == queryNorm { return 12000.0 }
                if candidate.contains(queryNorm) { return 4000.0 }
                if queryNorm.contains(candidate) { return 2000.0 }
                return 0.0
            }.max() ?? 0.0
            baseScore += bestTitleBonus
        }

        // 中文名不完全匹配时，使用英文/原名提示词进行二次加权，减少“同中文关键词不同作品”误配。
        if let secondary = secondaryQuery?.trimmingCharacters(in: .whitespacesAndNewlines), !secondary.isEmpty {
            let secondaryNorm = normalizeTitle(secondary)
            if !secondaryNorm.isEmpty && secondaryNorm != queryNorm {
                let bestSecondaryBonus = normalizedCandidates.map { candidate -> Double in
                    if candidate == secondaryNorm { return 8000.0 }
                    if candidate.contains(secondaryNorm) { return 2600.0 }
                    if secondaryNorm.contains(candidate) { return 1200.0 }
                    return 0.0
                }.max() ?? 0.0
                baseScore += bestSecondaryBonus
            }
        }
        
        // 查询不带“续集语义”时，压低续集/第二季等候选，减少“两杆大烟枪 -> 两杆大烟枪续集”误配。
        let queryHasSequelSemantics = containsSequelSemantics(query) || containsSequelSemantics(queryNorm)
        let titleHasSequelSemantics = candidateTitles.contains { containsSequelSemantics($0) } ||
            normalizedCandidates.contains { containsSequelSemantics($0) }
        if !queryHasSequelSemantics && titleHasSequelSemantics {
            baseScore -= 6000.0
        }
        
        // 集数命名应优先匹配 TV，避免被同名电影抢到。
        if let preferred = preferredMediaType?.lowercased(), !preferred.isEmpty {
            if mediaType == preferred {
                baseScore += 9000.0
            } else if !mediaType.isEmpty {
                baseScore -= 3500.0
            }
        }
        
        if let season = preferredSeason, season > 0, mediaType == "tv" {
            if let count = seasonCountByTVId[item.id] {
                if count >= season {
                    baseScore += 4500.0
                } else {
                    // 各站点/资源组的季编号常与 TMDB 不一致（如 S11 对应 TMDB 第 8 季），仅做轻微降权。
                    baseScore -= 800.0
                }
            }
        }

        // 查询词很短时，惩罚“标题过长扩展词”候选，避免 Roast 命中 Dean Martin Celebrity Roasts。
        let queryTokenCount = tokenCount(query)
        if queryTokenCount <= 2 {
            let longestCandidateTokenCount = candidateTitles.map(tokenCount).max() ?? 0
            let hasExactTitle = normalizedCandidates.contains(queryNorm)
            if !hasExactTitle && longestCandidateTokenCount >= 4 {
                baseScore -= 2200.0
            }
        }
        
        guard let targetStr = targetYear, let tYear = Int(targetStr) else {
            return baseScore // 没传年份，直接返回原本的热度分
        }
        
        var scoredYear: Int?
        if mediaType == "tv", let season = preferredSeason, season > 0 {
            scoredYear = seasonAirYearByTVId[item.id]
        }
        if scoredYear == nil {
            let itemYearStr = String((item.releaseDate ?? item.firstAirDate ?? "").prefix(4))
            scoredYear = Int(itemYearStr)
        }
        if let iYear = scoredYear {
            let diff = abs(iYear - tYear)
            if diff == 0 {
                baseScore += 10000.0 // 完美匹配
            } else if diff == 1 {
                baseScore += 5000.0  // 容错匹配 (解决罗小黑 2024/2025 问题)
            } else if diff >= 10 {
                baseScore -= 20000.0
            } else if diff >= 3 {
                baseScore -= 12000.0
                if mediaType == "tv", preferredSeason != nil {
                    baseScore -= 2500.0
                }
            }
        }
        return baseScore
    }

    private func mergeLocalized(preferred: TMDBResult, fallback: TMDBResult) -> TMDBResult {
        TMDBResult(
            id: preferred.id,
            title: preferred.title ?? fallback.title,
            name: preferred.name ?? fallback.name,
            originalTitle: preferred.originalTitle ?? fallback.originalTitle,
            originalName: preferred.originalName ?? fallback.originalName,
            overview: preferred.overview ?? fallback.overview,
            posterPath: preferred.posterPath ?? fallback.posterPath,
            releaseDate: preferred.releaseDate ?? fallback.releaseDate,
            firstAirDate: preferred.firstAirDate ?? fallback.firstAirDate,
            voteAverage: preferred.voteAverage ?? fallback.voteAverage,
            popularity: preferred.popularity ?? fallback.popularity,
            mediaType: preferred.mediaType ?? fallback.mediaType
        )
    }

    private func requestSearchPage(encodedQuery: String, language: String, page: Int) async throws -> [TMDBResult] {
        let urlString = "\(baseURL)/search/multi?query=\(encodedQuery)&language=\(language)&page=\(page)"
        guard let (data, response) = try await requestTMDB(urlString: urlString) else { return [] }
        guard response.statusCode == 200 else { return [] }
        let tmdbResponse = try JSONDecoder().decode(TMDBResponse.self, from: data)
        return tmdbResponse.results
    }

    private func fetchTVSeasonCount(tvId: Int) async throws -> Int? {
        if let cached = seasonCacheQueue.sync(execute: { tvSeasonCountCache[tvId] }) {
            return cached
        }

        let urlString = "\(baseURL)/tv/\(tvId)?language=en-US"
        guard let (data, response) = try await requestTMDB(urlString: urlString) else { return nil }
        guard response.statusCode == 200 else { return nil }
        let json = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        let count = json?["number_of_seasons"] as? Int
        if let count {
            seasonCacheQueue.sync { tvSeasonCountCache[tvId] = count }
        }
        return count
    }

    private func fetchTVSeasonAirYear(tvId: Int, season: Int) async throws -> Int? {
        let cacheKey = "\(tvId)#\(season)"
        if let cached = seasonCacheQueue.sync(execute: { tvSeasonAirYearCache[cacheKey] }) {
            return cached
        }
        let urlString = "\(baseURL)/tv/\(tvId)/season/\(season)?language=en-US"
        guard let (data, response) = try await requestTMDB(urlString: urlString), response.statusCode == 200 else {
            return nil
        }
        let json = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        let airDate = json?["air_date"] as? String
        let airYear = Int(String((airDate ?? "").prefix(4)))
        if let airYear {
            seasonCacheQueue.sync { tvSeasonAirYearCache[cacheKey] = airYear }
        }
        return airYear
    }
    
    private func matchableTitles(for item: TMDBResult) -> [String] {
        var list: [String] = []
        list.append(item.displayTitle)
        if let t = item.title { list.append(t) }
        if let n = item.name { list.append(n) }
        if let ot = item.originalTitle { list.append(ot) }
        if let on = item.originalName { list.append(on) }
        var seen = Set<String>()
        return list.filter { title in
            let key = title.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
            guard !key.isEmpty, !seen.contains(key) else { return false }
            seen.insert(key)
            return true
        }
    }
    
    private func normalizeTitle(_ input: String) -> String {
        input
            .folding(options: [.diacriticInsensitive, .caseInsensitive], locale: .current)
            .replacingOccurrences(of: #"[^\p{L}\p{N}]"#, with: "", options: .regularExpression)
            .lowercased()
    }

    private func tokenCount(_ input: String) -> Int {
        input
            .replacingOccurrences(of: #"[._\-]+"#, with: " ", options: .regularExpression)
            .split(separator: " ")
            .count
    }
    
    private func containsSequelSemantics(_ text: String) -> Bool {
        let lower = text.lowercased()
        let patterns = [
            "续集", "第二部", "第2部", "第二季", "第2季",
            "part2", "partii", "season2", "s2", "ii"
        ]
        if patterns.contains(where: { lower.contains($0) }) { return true }
        if lower.range(of: #"(?i)\bseason\s*\d+\b"#, options: .regularExpression) != nil { return true }
        if lower.range(of: #"第\s*[一二三四五六七八九十零〇两\d]+\s*季"#, options: .regularExpression) != nil { return true }
        if lower.range(of: #"\bs\d{1,2}\b"#, options: .regularExpression) != nil { return true }
        return false
    }

    private func requestTMDB(urlString: String) async throws -> (Data, HTTPURLResponse)? {
        await rateLimiter.acquire()
        let apiKey = self.apiKey
        guard !apiKey.isEmpty else { return nil }
        var full = urlString
        if apiKey.count < 50 {
            full += full.contains("?") ? "&api_key=\(apiKey)" : "?api_key=\(apiKey)"
        }
        guard let url = URL(string: full) else { return nil }
        var request = URLRequest(url: url)
        request.timeoutInterval = 10
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if apiKey.count >= 50 {
            request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        }
        let (data, response) = try await tmdbSession.data(for: request)
        guard let http = response as? HTTPURLResponse else { return nil }
        return (data, http)
    }

    private func localizeCandidatesForCurrentLanguage(_ items: [TMDBResult]) async throws -> [TMDBResult] {
        let appLang = UserDefaults.standard.string(forKey: "appLanguage") ?? "zh-Hans"
        guard appLang != "en" else { return items }
        let targetLanguage = "zh-CN"

        var localized: [TMDBResult] = []
        localized.reserveCapacity(items.count)
        for (idx, item) in items.enumerated() {
            // 控制额外请求数量，只对前 12 条且无中文标题的候选做补全
            if idx < 12, item.displayTitle.range(of: #"\p{Han}"#, options: .regularExpression) == nil {
                do {
                    if let refined = try await fetchLocalizedDetailIfNeeded(item: item, language: targetLanguage) {
                        localized.append(refined)
                        continue
                    }
                } catch {
                    // 本地化附加请求失败时保留原候选，避免整次搜索失败。
                }
            }
            localized.append(item)
        }
        return localized
    }

    private func fetchLocalizedDetailIfNeeded(item: TMDBResult, language: String) async throws -> TMDBResult? {
        let mediaType = (item.mediaType ?? "").lowercased()
        guard mediaType == "movie" || mediaType == "tv" else { return nil }
        let cacheKey = "\(mediaType)#\(item.id)#\(language)"
        if let cached = localizedCacheQueue.sync(execute: { localizedResultCache[cacheKey] }) {
            return cached
        }

        let urlString = "\(baseURL)/\(mediaType)/\(item.id)?language=\(language)"
        guard let (data, response) = try await requestTMDB(urlString: urlString), response.statusCode == 200 else {
            return nil
        }
        let detail = try JSONDecoder().decode(TMDBResult.self, from: data)
        let simplifiedDetail = TMDBResult(
            id: detail.id,
            title: simplifyChineseIfNeeded(detail.title),
            name: simplifyChineseIfNeeded(detail.name),
            originalTitle: detail.originalTitle,
            originalName: detail.originalName,
            overview: detail.overview,
            posterPath: detail.posterPath,
            releaseDate: detail.releaseDate,
            firstAirDate: detail.firstAirDate,
            voteAverage: detail.voteAverage,
            popularity: detail.popularity,
            mediaType: detail.mediaType
        )
        var merged = mergeLocalized(preferred: simplifiedDetail, fallback: item)

        // 某些条目 language=zh-CN 仍返回英文标题，回退到 translations 接口拿中文官方译名。
        if !merged.hasChineseDisplayTitle,
           let translatedChineseTitle = try await fetchChineseTranslatedTitle(mediaType: mediaType, id: item.id),
           translatedChineseTitle.range(of: #"\p{Han}"#, options: .regularExpression) != nil {
            if mediaType == "movie" {
                merged = TMDBResult(
                    id: merged.id,
                    title: translatedChineseTitle,
                    name: merged.name,
                    originalTitle: merged.originalTitle,
                    originalName: merged.originalName,
                    overview: merged.overview,
                    posterPath: merged.posterPath,
                    releaseDate: merged.releaseDate,
                    firstAirDate: merged.firstAirDate,
                    voteAverage: merged.voteAverage,
                    popularity: merged.popularity,
                    mediaType: merged.mediaType
                )
            } else {
                merged = TMDBResult(
                    id: merged.id,
                    title: merged.title,
                    name: translatedChineseTitle,
                    originalTitle: merged.originalTitle,
                    originalName: merged.originalName,
                    overview: merged.overview,
                    posterPath: merged.posterPath,
                    releaseDate: merged.releaseDate,
                    firstAirDate: merged.firstAirDate,
                    voteAverage: merged.voteAverage,
                    popularity: merged.popularity,
                    mediaType: merged.mediaType
                )
            }
        }
        localizedCacheQueue.sync { localizedResultCache[cacheKey] = merged }
        return merged
    }

    private func fetchChineseTranslatedTitle(mediaType: String, id: Int) async throws -> String? {
        let urlString = "\(baseURL)/\(mediaType)/\(id)/translations"
        guard let (data, response) = try await requestTMDB(urlString: urlString), response.statusCode == 200 else {
            return nil
        }
        let payload = try JSONDecoder().decode(TMDBTranslationsResponse.self, from: data)
        let preferredRegionOrder = ["CN", "SG", "TW", "HK"]

        let zhTranslations = payload.translations.filter { item in
            (item.iso639_1 ?? "").lowercased() == "zh"
        }
        for region in preferredRegionOrder {
            if let hit = zhTranslations.first(where: { ($0.iso3166_1 ?? "").uppercased() == region }) {
                if let title = hit.data?.title?.trimmingCharacters(in: .whitespacesAndNewlines), !title.isEmpty {
                    return simplifyChineseIfNeeded(title)
                }
                if let name = hit.data?.name?.trimmingCharacters(in: .whitespacesAndNewlines), !name.isEmpty {
                    return simplifyChineseIfNeeded(name)
                }
            }
        }

        for hit in zhTranslations {
            if let title = hit.data?.title?.trimmingCharacters(in: .whitespacesAndNewlines), !title.isEmpty {
                return simplifyChineseIfNeeded(title)
            }
            if let name = hit.data?.name?.trimmingCharacters(in: .whitespacesAndNewlines), !name.isEmpty {
                return simplifyChineseIfNeeded(name)
            }
        }
        return nil
    }

    private func simplifyChineseIfNeeded(_ input: String?) -> String? {
        guard let input else { return nil }
        guard input.range(of: #"\p{Han}"#, options: .regularExpression) != nil else { return input }
        let mutable = NSMutableString(string: input)
        let transformed = CFStringTransform(
            mutable as CFMutableString,
            nil,
            "Hant-Hans" as CFString,
            false
        )
        if transformed {
            return mutable as String
        }
        if let converted = input.applyingTransform(StringTransform("Hant-Hans"), reverse: false) {
            return converted
        }
        return input
    }
}
