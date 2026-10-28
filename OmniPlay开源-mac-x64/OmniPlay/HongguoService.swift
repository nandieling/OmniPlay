import Foundation
import GRDB

// MARK: - 红果短剧搜索
//
// 红果是短剧自己的库：一条搜索就给齐权威集数、简介、竖版封面和分类标签，
// 这些正是 TMDB 与豆瓣对国产短剧最缺的部分。GET 接口不签名，但它是单一平台的内容，
// 认不上是常态，所以只当补充源：条目身份仍然由豆瓣绑定或 TMDB 刮削决定。

enum HongguoServiceError: LocalizedError {
    case httpStatus(Int)
    case blocked(String)
    case invalidResponse

    var errorDescription: String? {
        switch self {
        case .httpStatus(let status): return "红果返回 HTTP \(status)。"
        case .blocked(let reason): return reason
        case .invalidResponse: return "未能从红果解析到搜索结果。"
        }
    }
}

/// 红果的礼貌闸门。它没有豆瓣那种 12 秒风控，但一轮扫描刮几十部剧，
/// 隔一下再走更稳妥；被拒时按小时退避。
actor HongguoRequestGate {
    private var nextAllowedDate = Date.distantPast
    private let minimumInterval: TimeInterval = 2
    private let cooldownDefaultsKey = "hongguoRequestCooldownUntil"

    func acquire() async throws {
        let cooldownUntil = Date(timeIntervalSince1970: UserDefaults.standard.double(forKey: cooldownDefaultsKey))
        if Date() < cooldownUntil {
            throw HongguoServiceError.blocked("红果请求已暂停到 \(Self.format(cooldownUntil))，请稍后再试。")
        }
        let now = Date()
        if now < nextAllowedDate {
            let delay = nextAllowedDate.timeIntervalSince(now)
            try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
        }
        nextAllowedDate = Date().addingTimeInterval(minimumInterval + Double.random(in: 0.5...1.5))
    }

    func pause(hours: Double) {
        let until = Date().addingTimeInterval(hours * 3600)
        UserDefaults.standard.set(until.timeIntervalSince1970, forKey: cooldownDefaultsKey)
    }

    private static func format(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.dateStyle = .short
        formatter.timeStyle = .short
        return formatter.string(from: date)
    }
}

struct HongguoHit: Equatable, Identifiable {
    /// 红果 series_id，19 位数字，只当外部编号用，不进 movie 主键
    let seriesID: String
    var title: String
    /// series_intro，短剧里少见的完整简介
    var intro: String?
    var coverURL: String?
    /// 权威集数，「全100集」那类文案也在这里
    var episodeCount: Int?
    var year: Int?
    var categories: [String]
    var heatText: String?

    var id: String { seriesID }

    /// 剥掉季号后的核心片名，比对时用它，避免「第一季」对上「第七季」。
    nonisolated var coreTitle: String { ShortDramaTitleMatch.splitSeason(from: title).core }
    nonisolated var season: Int? { ShortDramaTitleMatch.splitSeason(from: title).season }

    /// 与检索词的可信度，季号参与校正：目录是 S07 却绑到第一季，比认不出来更糟。
    nonisolated func matchScore(query: String, season wantedSeason: Int? = nil) -> Int {
        let target = ShortDramaTitleMatch.normalize(query)
        guard !target.isEmpty else { return 0 }
        let core = ShortDramaTitleMatch.normalize(coreTitle)
        guard core.count >= 2 else { return 0 }
        let base: Int
        if core == target {
            base = 100
        } else if core.count >= 4, target.contains(core) {
            base = 85
        } else if target.count >= 4, core.contains(target) {
            base = 55
        } else {
            base = 0
        }
        return ShortDramaTitleMatch.seasonAdjusted(base: base, candidateSeason: season, wantedSeason: wantedSeason)
    }
}

enum HongguoSearchParser {
    /// 用 JSONSerialization 而不是 Codable：接口字段是平台内部格式，说加就加，
    /// 只取需要的键比维护一份全量模型稳当（豆瓣那边同理）。
    nonisolated static func parse(json: Data) -> [HongguoHit] {
        guard let object = try? JSONSerialization.jsonObject(with: json) as? [String: Any],
              let items = object["searchList"] as? [[String: Any]] else {
            return []
        }
        return items.compactMap { item -> HongguoHit? in
            let detail = item["video_data"] as? [String: Any] ?? [:]
            let seriesID = stringValue(detail["series_id"]) ?? stringValue(item["keyword"])
            let title = stringValue(detail["series_title"]) ?? stringValue(item["name"])
            guard let seriesID, let title, !title.isEmpty else { return nil }
            var year: Int?
            if let seconds = Int(stringValue(detail["create_time"]) ?? ""), seconds > 0 {
                year = Calendar.current.component(.year, from: Date(timeIntervalSince1970: TimeInterval(seconds)))
            }
            let categories = (detail["category_list"] as? [[String: Any]] ?? [])
                .compactMap { stringValue($0["name"]) }
                .filter { !$0.isEmpty }
            var episodeCount = intValue(detail["episode_cnt"])
            if episodeCount == nil, let match = (stringValue(detail["episode_right_text"]) ?? "").firstMatch(pattern: #"(\d{1,4})\s*集"#),
               match.count > 1 {
                episodeCount = Int(match[1])
            }
            return HongguoHit(
                seriesID: seriesID,
                title: title,
                intro: stringValue(detail["series_intro"]),
                coverURL: stringValue(detail["series_cover"]),
                episodeCount: episodeCount,
                year: year,
                categories: categories,
                heatText: (detail["hot_score_data"] as? [String: Any]).flatMap { stringValue($0["text"]) }
            )
        }
    }

    private nonisolated static func stringValue(_ value: Any?) -> String? {
        if let text = value as? String {
            let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
            return trimmed.isEmpty ? nil : trimmed
        }
        if let number = value as? NSNumber { return number.stringValue }
        return nil
    }

    private nonisolated static func intValue(_ value: Any?) -> Int? {
        if let number = value as? NSNumber { return number.intValue }
        return (value as? String).flatMap(Int.init)
    }
}

extension String {
    fileprivate nonisolated func firstMatch(pattern: String, options: NSRegularExpression.Options = []) -> [String]? {
        guard let regex = try? NSRegularExpression(pattern: pattern, options: options) else { return nil }
        let nsRange = NSRange(startIndex..<endIndex, in: self)
        guard let result = regex.firstMatch(in: self, options: [], range: nsRange) else { return nil }
        return (0..<result.numberOfRanges).map { index in
            guard let range = Range(result.range(at: index), in: self) else { return "" }
            return String(self[range])
        }
    }
}

final class HongguoService {
    static let shared = HongguoService()

    private let gate = HongguoRequestGate()
    private let session: URLSession

    private init() {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 12
        config.timeoutIntervalForResource = 20
        config.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        config.urlCache = nil
        config.httpCookieStorage = nil
        config.httpShouldSetCookies = false
        session = URLSession(configuration: config)
    }

    /// 关键词搜索，一页 10 条。短剧按整部刮，一次搜索足够挑出同名条目。
    func search(query: String) async throws -> [HongguoHit] {
        let keyword = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !keyword.isEmpty else { return [] }
        try await gate.acquire()

        guard let encoded = keyword.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed),
              let url = URL(string: "https://hongguoduanju.com/api/search/result/?query=\(encoded)&page_index=1&sort_type=0") else {
            return []
        }
        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        request.setValue("application/json, text/plain, */*", forHTTPHeaderField: "Accept")
        request.setValue("zh-CN,zh-Hans;q=0.9,en;q=0.8", forHTTPHeaderField: "Accept-Language")
        request.setValue("https://hongguoduanju.com/", forHTTPHeaderField: "Referer")
        request.setValue("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36", forHTTPHeaderField: "User-Agent")

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw HongguoServiceError.invalidResponse
        }
        if http.statusCode == 403 || http.statusCode == 429 {
            await gate.pause(hours: 6)
            throw HongguoServiceError.blocked("红果返回 \(http.statusCode)，已暂停红果请求 6 小时。")
        }
        guard http.statusCode == 200 else {
            throw HongguoServiceError.httpStatus(http.statusCode)
        }
        return HongguoSearchParser.parse(json: data)
    }

    /// 排序：同名在前，季号对得上的加分，季号对不上的直接掉到后面。零分丢弃。
    nonisolated static func rankedHits(
        _ hits: [HongguoHit],
        query: String,
        season wantedSeason: Int? = nil
    ) -> [HongguoHit] {
        hits
            .map { ($0, $0.matchScore(query: query, season: wantedSeason)) }
            .filter { $0.1 > 0 }
            .sorted { lhs, rhs in
                if lhs.1 != rhs.1 { return lhs.1 > rhs.1 }
                if lhs.0.episodeCount ?? 0 != rhs.0.episodeCount ?? 0 {
                    return (lhs.0.episodeCount ?? 0) > (rhs.0.episodeCount ?? 0)
                }
                return (lhs.0.year ?? 0) > (rhs.0.year ?? 0)
            }
            .map(\.0)
    }
}

// MARK: - 手动绑定入库

enum HongguoBinding {
    /// 手动把某条红果条目绑到现有条目上。条目身份沿用现有主键（红果那 19 位 series_id
    /// 不进 movie 主键命名空间），红果只负责把标题、年份、简介、竖版封面和分类写全，
    /// 出处记进 shortDramaMetadata。
    ///
    /// 「锁定」挡的是自动刮削覆盖手填内容，手动点绑定本身就是用户的决定，
    /// 所以这里照样写入（否则点了按钮什么都不会变）；只有年份不降级——
    /// 红果只给到年份，已有更精确的日期保留。
    static func bind(_ hit: HongguoHit, movieId: Int64) async throws {
        if let cover = hit.coverURL, !cover.isEmpty {
            // 手动绑定是用户的决定，绕过自动刮削失败账本
            PosterManager.shared.downloadPoster(posterPath: cover, force: true)
        }
        try await AppDatabase.shared.dbQueue.write { db in
            guard var movie = try Movie.fetchOne(db, key: movieId) else {
                throw HongguoServiceError.invalidResponse
            }
            movie.title = hit.title
            if let year = hit.year, movie.releaseDate?.isEmpty != false {
                movie.releaseDate = String(year)
            }
            if let intro = hit.intro, !intro.isEmpty {
                movie.overview = intro + (hit.episodeCount.map { " · 共 \($0) 集" } ?? "")
            }
            if let cover = hit.coverURL, !cover.isEmpty { movie.posterPath = cover }
            try movie.update(db)
            for var file in try VideoFile.filter(Column("movieId") == movieId).fetchAll(db) {
                // 认过源就别再算未匹配，否则每次扫描都要重刮一遍
                file.mediaType = "movie"
                file.mediaKind = ShortDrama.kindValue
                try file.update(db)
            }
            try ShortDramaMetadata.save(
                db: db,
                movieId: movieId,
                source: "hongguo",
                externalId: hit.seriesID,
                platform: "红果",
                episodeCount: hit.episodeCount,
                tags: hit.categories,
                posterOrientation: "portrait"
            )
        }
    }
}
