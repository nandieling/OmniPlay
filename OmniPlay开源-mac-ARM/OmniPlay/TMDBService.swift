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

struct TMDBTranslationsResponse: Codable {
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

// TMDB 作品别名（/movie/{id}/alternative_titles 用 titles，/tv/{id}/alternative_titles 用 results）
struct TMDBAlternativeTitlesResponse: Codable {
    struct Title: Codable {
        let title: String?
        let type: String?
        let iso3166_1: String?
        enum CodingKeys: String, CodingKey {
            case title, type
            case iso3166_1 = "iso_3166_1"
        }
    }
    let titles: [Title]?
    let results: [Title]?
}

// TMDB 人物详情（append_to_response=translations）：本地化名称 + 别名 + 各语言译名
struct TMDBPersonDetailResponse: Codable {
    struct PersonTranslations: Codable {
        let translations: [TMDBTranslationsResponse.TranslationItem]?
    }
    let name: String?
    let alsoKnownAs: [String]?
    let translations: PersonTranslations?
    enum CodingKeys: String, CodingKey {
        case name, translations
        case alsoKnownAs = "also_known_as"
    }
}

// TMDB 演职员接口（/movie/{id}/credits、/tv/{id}/credits）
struct TMDBCredits: Codable {
    struct Person: Codable {
        let name: String?
        let character: String?
        let id: Int?
        let profilePath: String?
        enum CodingKeys: String, CodingKey {
            case name, character, id
            case profilePath = "profile_path"
        }
    }
    struct Crew: Codable {
        let name: String?
        let job: String?
        let id: Int?
        let profilePath: String?
        enum CodingKeys: String, CodingKey {
            case name, job, id
            case profilePath = "profile_path"
        }
    }
    let cast: [Person]
    let crew: [Crew]
}

// TMDB 人物信息（名称 + 人物 id + 头像），用于首页分类文件夹的人物照片
struct TMDBPersonInfo: Codable, Equatable {
    let name: String
    let tmdbId: Int?
    let profilePath: String?
}

// TMDB 人物作品条目（标题 + 评分 + 年份/日期 + TMDB id），用于文件夹详情页简介
struct TMDBFilmographyItem: Codable, Equatable {
    var title: String
    var rating: Double?
    var year: Int?
    var date: String?
    var tmdbId: Int?
    // 作品类型（movie/tv）；老缓存里没有该字段，缺中文片名时会按两种类型探测
    var mediaType: String?
}

// /person/{id}/combined_credits 的通用条目（cast/crew 公共字段子集）
private struct TMDBCombinedCredits: Codable {
    struct Item: Codable {
        let id: Int?
        let title: String?
        let name: String?
        let mediaType: String?
        let voteAverage: Double?
        let job: String?
        let releaseDate: String?
        let firstAirDate: String?
        enum CodingKeys: String, CodingKey {
            case id, title, name, job
            case mediaType = "media_type"
            case voteAverage = "vote_average"
            case releaseDate = "release_date"
            case firstAirDate = "first_air_date"
        }
    }
    let cast: [Item]
    let crew: [Item]
}

enum TMDBCredentialSource {
    case customAPIKey(String)
    case customBearerToken(String)
    case none

    nonisolated var isAvailable: Bool {
        switch self {
        case .customAPIKey(let key): return !key.isEmpty
        case .customBearerToken(let token): return !token.isEmpty
        case .none: return false
        }
    }
}

struct TMDBConnectionCheckResult {
    let isConnected: Bool
    let message: String
}

class TMDBService {
    static let shared = TMDBService()

    nonisolated static let officialAPIBaseURL = URL(string: "https://api.themoviedb.org/3")!
    nonisolated static let proxyURLDefaultsKey = "tmdbProxyURL"

    private var baseURL: String { Self.officialAPIBaseURL.absoluteString }
    private var tvSeasonCountCache: [Int: Int] = [:]
    private var tvSeasonAirYearCache: [String: Int] = [:]
    private let seasonCacheQueue = DispatchQueue(label: "nan.omniplay.tmdb.season-cache")
    private var localizedResultCache: [String: TMDBResult] = [:]
    private let localizedCacheQueue = DispatchQueue(label: "nan.omniplay.tmdb.localized-cache")
    private var creditsCache: [Int: (directors: [TMDBPersonInfo], casts: [TMDBPersonInfo])] = [:]
    private let creditsCacheQueue = DispatchQueue(label: "nan.omniplay.tmdb.credits-cache")
    private var filmographyCache: [String: [TMDBFilmographyItem]] = [:]
    private let filmographyCacheQueue = DispatchQueue(label: "nan.omniplay.tmdb.filmography-cache")
    private let customRateLimiter = TMDBRateLimiter(requestsPerSecond: 3.2)
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

    nonisolated static func credentialSource(customAPIInput: String? = nil, defaults: UserDefaults = .standard) -> TMDBCredentialSource {
        let explicitInput = customAPIInput?.trimmingCharacters(in: .whitespacesAndNewlines)
        let userKey = (explicitInput?.isEmpty == false ? explicitInput! : (defaults.string(forKey: "tmdbApiKey") ?? ""))
            .trimmingCharacters(in: .whitespacesAndNewlines)
        if !userKey.isEmpty {
            return userKey.count > 50 ? .customBearerToken(userKey) : .customAPIKey(userKey)
        }
        return .none
    }

    func currentCredentialSource() -> TMDBCredentialSource {
        Self.credentialSource()
    }

    nonisolated static func proxyAPIBaseURL(
        customProxyURL: String? = nil,
        defaults: UserDefaults = .standard
    ) -> URL? {
        let input = (customProxyURL ?? defaults.string(forKey: proxyURLDefaultsKey) ?? "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !input.isEmpty else { return officialAPIBaseURL }

        let valueWithScheme = input.contains("://") ? input : "https://\(input)"
        guard var components = URLComponents(string: valueWithScheme),
              let scheme = components.scheme?.lowercased(),
              scheme == "https" || scheme == "http",
              components.host?.isEmpty == false,
              components.user == nil,
              components.password == nil,
              components.query == nil,
              components.fragment == nil else {
            return nil
        }

        components.scheme = scheme
        var path = components.percentEncodedPath
        while path.count > 1 && path.hasSuffix("/") { path.removeLast() }
        if path.isEmpty { path = "/3" }
        else if path != "/3" && !path.hasSuffix("/3") { path += "/3" }
        components.percentEncodedPath = path
        return components.url
    }

    nonisolated static func resolvedAPIURL(
        for officialURLString: String,
        customProxyURL: String? = nil,
        defaults: UserDefaults = .standard
    ) -> URL? {
        guard let sourceURL = URL(string: officialURLString),
              let source = URLComponents(url: sourceURL, resolvingAgainstBaseURL: false),
              let proxyBaseURL = proxyAPIBaseURL(customProxyURL: customProxyURL, defaults: defaults),
              var destination = URLComponents(url: proxyBaseURL, resolvingAgainstBaseURL: false) else {
            return nil
        }

        let sourcePath = source.percentEncodedPath
        let endpointPath = sourcePath == "/3" ? "" : (sourcePath.hasPrefix("/3/") ? String(sourcePath.dropFirst(2)) : sourcePath)
        let basePath = destination.percentEncodedPath.hasSuffix("/")
            ? String(destination.percentEncodedPath.dropLast())
            : destination.percentEncodedPath
        destination.percentEncodedPath = basePath + endpointPath
        destination.percentEncodedQuery = source.percentEncodedQuery
        return destination.url
    }

    /// 连通性探测：URLSession、URL 拼装与超时控制都在全局执行器上跑，
    /// 刮削前的检测不再占用主线程（否则检测期间指针与滚动会掉帧）。
    nonisolated static func probeConnection(
        credential: TMDBCredentialSource,
        customProxyURL: String? = nil
    ) async -> TMDBConnectionCheckResult {
        guard credential.isAvailable else {
            return TMDBConnectionCheckResult(isConnected: false, message: "当前未配置可用的 TMDB API。")
        }
        guard proxyAPIBaseURL(customProxyURL: customProxyURL) != nil else {
            return TMDBConnectionCheckResult(isConnected: false, message: "TMDB 代理网关地址无效。")
        }
        let urlString = "\(officialAPIBaseURL.absoluteString)/configuration"
        guard let request = makeRequest(urlString: urlString, credential: credential, customProxyURL: customProxyURL) else {
            return TMDBConnectionCheckResult(isConnected: false, message: "TMDB 请求地址无效。")
        }

        let session = URLSession(configuration: probeSessionConfiguration())
        defer { session.invalidateAndCancel() }
        do {
            let (_, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                return TMDBConnectionCheckResult(isConnected: false, message: "TMDB 未返回有效响应。")
            }
            if http.statusCode == 200 {
                return TMDBConnectionCheckResult(isConnected: true, message: "TMDB 连接正常。")
            }
            return TMDBConnectionCheckResult(isConnected: false, message: "TMDB 返回 HTTP \(http.statusCode)。")
        } catch {
            return TMDBConnectionCheckResult(isConnected: false, message: error.localizedDescription)
        }
    }

    // 探测用独立配置：超时更短，网络异常时尽快返回，不拖长刮削前的等待窗口
    nonisolated private static func probeSessionConfiguration() -> URLSessionConfiguration {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 8
        config.timeoutIntervalForResource = 10
        config.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        config.urlCache = nil
        config.httpCookieStorage = nil
        config.httpShouldSetCookies = false
        config.connectionProxyDictionary = [:]
        return config
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
    
    /// 拉取导演与主演名单（缓存），用于影片元数据与首页分类。
    /// 剧集取 Creator/导演（TMDB 剧集 credits 仅含第一季主创），电影取 Director。
    func fetchCredits(id: Int, mediaType: String?) async -> (directors: [TMDBPersonInfo], casts: [TMDBPersonInfo]) {
        if let cached = creditsCacheQueue.sync(execute: { creditsCache[id] }) {
            return cached
        }
        let appLang = UserDefaults.standard.string(forKey: "appLanguage") ?? "zh-Hans"
        let tmdbLang = appLang == "en" ? "en-US" : "zh-CN"
        let kind = (mediaType?.lowercased() == "tv") ? "tv" : "movie"
        let urlString = "\(baseURL)/\(kind)/\(id)/credits?language=\(tmdbLang)"
        guard let (data, response) = try? await requestTMDB(urlString: urlString),
              response.statusCode == 200,
              let credits = try? JSONDecoder().decode(TMDBCredits.self, from: data) else {
            return ([], [])
        }
        let directorJobs: Set<String> = (kind == "tv") ? ["creator", "director"] : ["director"]
        var directors: [TMDBPersonInfo] = []
        var seenDirectors = Set<String>()
        for member in credits.crew {
            guard let job = member.job?.lowercased(),
                  directorJobs.contains(job),
                  let rawName = member.name else { continue }
            let name = simplifyChineseIfNeeded(rawName)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            guard !name.isEmpty else { continue }
            let key = name.lowercased()
            guard !seenDirectors.contains(key) else { continue }
            seenDirectors.insert(key)
            directors.append(TMDBPersonInfo(name: name, tmdbId: member.id, profilePath: member.profilePath))
        }
        directors = Array(directors.prefix(5))

        var casts: [TMDBPersonInfo] = []
        var seenCasts = Set<String>()
        for member in credits.cast {
            guard let rawName = member.name else { continue }
            let name = simplifyChineseIfNeeded(rawName)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            guard !name.isEmpty else { continue }
            let key = name.lowercased()
            guard !seenCasts.contains(key) else { continue }
            seenCasts.insert(key)
            casts.append(TMDBPersonInfo(name: name, tmdbId: member.id, profilePath: member.profilePath))
        }
        casts = Array(casts.prefix(8))

        creditsCacheQueue.sync { creditsCache[id] = (directors, casts) }
        return (directors, casts)
    }

    // 人物完整作品列表（/person/{id}/combined_credits）：导演取 crew 中 director/creator，主演取 cast 排行
    func fetchPersonFilmography(personId: Int, kind: String) async -> [TMDBFilmographyItem]? {
        let cacheKey = "\(kind)-\(personId)"
        if let cached = filmographyCacheQueue.sync(execute: { filmographyCache[cacheKey] }) {
            return cached
        }
        let appLang = UserDefaults.standard.string(forKey: "appLanguage") ?? "zh-Hans"
        let tmdbLang = appLang == "en" ? "en-US" : "zh-CN"
        let urlString = "\(baseURL)/person/\(personId)/combined_credits?language=\(tmdbLang)"
        guard let (data, response) = try? await requestTMDB(urlString: urlString),
              response.statusCode == 200,
              let credits = try? JSONDecoder().decode(TMDBCombinedCredits.self, from: data) else {
            return nil
        }
        let source: [TMDBCombinedCredits.Item]
        if kind == "director" {
            source = credits.crew.filter { member in
                let job = (member.job ?? "").lowercased()
                return job == "director" || job == "creator"
            }
        } else {
            source = credits.cast
        }
        var byKey: [String: (title: String, rating: Double?, tmdbId: Int?, year: Int?, date: String?, mediaType: String?)] = [:]
        var order: [String] = []
        for member in source {
            let rawTitle = (member.mediaType?.lowercased() == "tv" ? member.name : member.title) ?? ""
            let title = simplifyChineseIfNeeded(rawTitle)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            guard !title.isEmpty else { continue }
            let key = "\(member.id ?? 0)-\(member.mediaType ?? "")"
            let rawDate = member.releaseDate ?? member.firstAirDate
            let year = Int((rawDate ?? "").prefix(4))
            if byKey[key] == nil {
                order.append(key)
                byKey[key] = (title, member.voteAverage, member.id, year, rawDate, member.mediaType?.lowercased())
            } else if let r = member.voteAverage, (byKey[key]?.rating ?? -1) < r {
                byKey[key]?.rating = r
            }
        }
        var items: [TMDBFilmographyItem] = []
        for key in order {
            guard let entry = byKey[key] else { continue }
            items.append(TMDBFilmographyItem(
                title: entry.title,
                rating: entry.rating,
                year: entry.year,
                date: entry.date,
                tmdbId: entry.tmdbId,
                mediaType: entry.mediaType
            ))
        }
        let sorted = items.sorted { lhs, rhs in (lhs.rating ?? -1) > (rhs.rating ?? -1) }
        let result = Array(sorted.prefix(60))
        filmographyCacheQueue.sync { filmographyCache[cacheKey] = result }
        return result
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

    func requestTMDB(urlString: String) async throws -> (Data, HTTPURLResponse)? {
        let credential = currentCredentialSource()
        guard credential.isAvailable else { return nil }
        let session = tmdbSession
        let limiter = customRateLimiter
        // 网络请求与限流等待放到全局执行器，主线程只负责发起与拿结果
        return try await Task.detached(priority: .utility) {
            try await Self.performRequest(
                session: session,
                urlString: urlString,
                credential: credential,
                limiter: limiter
            )
        }.value
    }

    nonisolated private static func performRequest(
        session: URLSession,
        urlString: String,
        credential: TMDBCredentialSource,
        limiter: TMDBRateLimiter
    ) async throws -> (Data, HTTPURLResponse)? {
        let maxAttempts = 2
        for attempt in 0..<maxAttempts {
            await limiter.acquire()
            guard let request = makeRequest(urlString: urlString, credential: credential) else { return nil }
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else { return nil }
            if http.statusCode != 429 || attempt == maxAttempts - 1 {
                return (data, http)
            }
            try? await Task.sleep(nanoseconds: retryDelayNanoseconds(for: http, attempt: attempt))
        }
        return nil
    }

    nonisolated private static func makeRequest(
        urlString: String,
        credential: TMDBCredentialSource,
        customProxyURL: String? = nil
    ) -> URLRequest? {
        guard let resolvedURL = resolvedAPIURL(for: urlString, customProxyURL: customProxyURL),
              let url = authenticatedURL(for: resolvedURL, credential: credential) else {
            return nil
        }
        var request = URLRequest(url: url)
        request.timeoutInterval = 10
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if case .customBearerToken(let token) = credential {
            request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        }
        return request
    }

    nonisolated private static func authenticatedURL(for url: URL, credential: TMDBCredentialSource) -> URL? {
        switch credential {
        case .customAPIKey(let key):
            guard var components = URLComponents(url: url, resolvingAgainstBaseURL: false) else { return nil }
            var queryItems = components.queryItems ?? []
            queryItems.removeAll { $0.name == "api_key" }
            queryItems.append(URLQueryItem(name: "api_key", value: key))
            components.queryItems = queryItems
            return components.url
        case .customBearerToken, .none:
            return url
        }
    }

    nonisolated private static func retryDelayNanoseconds(for response: HTTPURLResponse, attempt: Int) -> UInt64 {
        if let retryAfter = response.value(forHTTPHeaderField: "Retry-After")?
            .trimmingCharacters(in: .whitespacesAndNewlines),
           let seconds = Double(retryAfter),
           seconds > 0 {
            return UInt64(seconds * 1_000_000_000)
        }
        let baseDelaySeconds = 1.5
        return UInt64(baseDelaySeconds * Double(attempt + 1) * 1_000_000_000)
    }

    private func localizeCandidatesForCurrentLanguage(_ items: [TMDBResult]) async throws -> [TMDBResult] {
        let appLang = UserDefaults.standard.string(forKey: "appLanguage") ?? "zh-Hans"
        guard appLang != "en" else { return items }
        let targetLanguage = "zh-CN"
        let detailLocalizationLimit = 12

        var localized: [TMDBResult] = []
        localized.reserveCapacity(items.count)
        for (idx, item) in items.enumerated() {
            // 控制额外请求数量，只对前几条且无中文标题的候选做补全。
            if idx < detailLocalizationLimit, item.displayTitle.range(of: #"\p{Han}"#, options: .regularExpression) == nil {
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
        return Self.chineseName(from: payload.translations, requireChinese: false)
    }

    /// 人物中文姓名：先取 zh-CN 本地化名称与中文译名，再退到人物别名中的中文名。
    /// 区分"TMDB 确实没有中文条目"和"本次请求失败"，避免网络抖动被长期当成前者。
    func fetchChinesePersonName(personId: Int) async -> ChineseNameResult {
        let appLang = UserDefaults.standard.string(forKey: "appLanguage") ?? "zh-Hans"
        let tmdbLang = appLang == "en" ? "en-US" : "zh-CN"
        let urlString = "\(baseURL)/person/\(personId)?language=\(tmdbLang)&append_to_response=translations"
        guard let (data, response) = try? await requestTMDB(urlString: urlString) else { return .requestFailed }
        guard response.statusCode == 200,
              let detail = try? JSONDecoder().decode(TMDBPersonDetailResponse.self, from: data) else {
            return response.statusCode == 404 ? .notTranslated : .requestFailed
        }
        if let name = Self.chinesePersonName(from: detail) { return .found(name) }
        return .notTranslated
    }

    /// 作品中文片名：TMDB 在缺少 zh-CN 标题时会直接回传原文，此时 alternative_titles
    /// 里往往仍带有中文片名（别名与译名是两张表），这里只查别名。
    /// - Parameter mediaType: "movie" / "tv"；nil 时先按电影再按剧集探测。
    func fetchChineseTitle(mediaType: String?, id: Int) async -> ChineseNameResult {
        let candidates = mediaType == nil ? ["movie", "tv"] : [(mediaType ?? "").lowercased() == "tv" ? "tv" : "movie"]
        var sawRequestFailure = false
        for kind in candidates {
            let urlString = "\(baseURL)/\(kind)/\(id)/alternative_titles"
            guard let (data, response) = try? await requestTMDB(urlString: urlString) else {
                sawRequestFailure = true
                continue
            }
            guard response.statusCode == 200,
                  let payload = try? JSONDecoder().decode(TMDBAlternativeTitlesResponse.self, from: data) else {
                if response.statusCode != 404 { sawRequestFailure = true }
                continue
            }
            if let title = Self.chineseAlternativeTitle(from: payload) { return .found(title) }
        }
        return sawRequestFailure ? .requestFailed : .notTranslated
    }

    // MARK: - 中文名称挑选（纯函数，便于单测）

    // 人物详情 → 中文名：本地化名称 → 简体中文译名 → 中文名别名
    static func chinesePersonName(from detail: TMDBPersonDetailResponse) -> String? {
        if let name = trimmed(detail.name), containsChinese(name) {
            return simplifiedChinese(name) ?? name
        }
        if let translated = chineseName(
            from: detail.translations?.translations ?? [],
            requireChinese: true
        ) {
            return translated
        }
        if let alias = detail.alsoKnownAs?.first(where: { containsChinese($0) }), !alias.isEmpty {
            return simplifiedChinese(alias) ?? alias
        }
        return nil
    }

    // 作品别名 → 中文片名：电影在 titles、剧集在 results，优先 CN/SG/TW/HK 地区
    static func chineseAlternativeTitle(from payload: TMDBAlternativeTitlesResponse) -> String? {
        let titles = (payload.titles ?? []) + (payload.results ?? [])
        func chinese(_ entries: [TMDBAlternativeTitlesResponse.Title]) -> String? {
            for entry in entries {
                guard let value = trimmed(entry.title), containsChinese(value) else { continue }
                return simplifiedChinese(value) ?? value
            }
            return nil
        }
        let regional = ["CN", "SG", "TW", "HK"].compactMap { region in
            titles.first { ($0.iso3166_1 ?? "").uppercased() == region }
        }
        return chinese(regional) ?? chinese(titles)
    }

    // 译名列表 → 简体中文名称：优先 CN/SG/TW/HK 地区，其次任意中文条目
    static func chineseName(
        from translations: [TMDBTranslationsResponse.TranslationItem],
        requireChinese: Bool
    ) -> String? {
        let zhTranslations = translations.filter { ($0.iso639_1 ?? "").lowercased() == "zh" }
        let regional = ["CN", "SG", "TW", "HK"].compactMap { region in
            zhTranslations.first { ($0.iso3166_1 ?? "").uppercased() == region }
        }
        for item in regional + zhTranslations {
            for raw in [item.data?.title, item.data?.name] {
                guard let value = trimmed(raw), !value.isEmpty else { continue }
                if requireChinese, !containsChinese(value) { continue }
                return simplifiedChinese(value) ?? value
            }
        }
        return nil
    }

    nonisolated static func containsChinese(_ value: String) -> Bool {
        value.range(of: #"\p{Han}"#, options: .regularExpression) != nil
    }

    private static func trimmed(_ value: String?) -> String? {
        guard let value = value?.trimmingCharacters(in: .whitespacesAndNewlines), !value.isEmpty else {
            return nil
        }
        return value
    }

    func simplifyChineseIfNeeded(_ input: String?) -> String? {
        Self.simplifiedChinese(input)
    }

    static func simplifiedChinese(_ input: String?) -> String? {
        guard let input else { return nil }
        guard containsChinese(input) else { return input }
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
