import Foundation

// MARK: - 豆瓣关键词搜索
//
// 豆瓣搜索页把结果整包塞在 `window.__DATA__` 的一段 JSON 里，一条搜索请求就能拿到
// 候选条目、评分、竖版海报、别名和单集时长，不需要再进详情页。短剧受豆瓣风控限制
// （见 DoubanRequestGate 的 12 秒闸门），这一条省掉的分时请求正好够把吞吐翻一倍。

struct DoubanSearchHit: Codable, Equatable, Identifiable {
    /// 豆瓣 subject id
    let id: Int
    var title: String
    var year: Int?
    var labels: [String]
    /// abstract 拆出来的类型段，例如「中国大陆 / 爱情 / 短片 / 古装」
    var genres: [String]
    /// abstract 里的别名段，短剧改名极多，别名必须参与匹配
    var aliases: [String]
    /// abstract 末尾的「N分钟」，即单集时长
    var episodeMinutes: Int?
    var rating: Double?
    var coverURL: String?
    var subjectURL: String

    var isTVSubject: Bool { labels.contains("剧集") }
    /// 豆瓣把短剧标成「短片」类型并给出分钟数，这是最省事的短剧判据。
    var looksLikeShortDrama: Bool {
        if genres.contains("短片") { return true }
        guard let minutes = episodeMinutes else { return false }
        return minutes <= 25 && isTVSubject
    }

    /// 与检索词的可信度：完全同名最高，别名同名次之，
    /// 「标题是检索词的子串」再次之（目录名常带 S07、SKIT、制作方这类尾巴，
    /// 豆瓣的干净标题会被包在里面），反过来的包含关系最低。
    nonisolated func matchScore(query: String) -> Int {
        baseMatchScore(query: query)
    }

    /// 季号参与比对。豆瓣的国产短剧也按季拆条目（「持械入宋：第一季」），
    /// 目录写着 S07 却绑到第一季，比认不出来更糟，所以季号对不上要重罚。
    nonisolated func matchScore(query: String, season wantedSeason: Int?) -> Int {
        ShortDramaTitleMatch.seasonAdjusted(
            base: baseMatchScore(query: query),
            candidateSeason: ShortDramaTitleMatch.splitSeason(from: title).season,
            wantedSeason: wantedSeason
        )
    }

    private nonisolated func baseMatchScore(query: String) -> Int {
        let target = DoubanSearchHit.normalize(query)
        guard !target.isEmpty else { return 0 }
        let normalizedTitle = DoubanSearchHit.normalize(title)
        let normalizedAliases = aliases.map(DoubanSearchHit.normalize)
        if normalizedTitle == target { return 100 }
        if normalizedAliases.contains(target) { return 80 }
        // 短到撑不起一个片名的片段（「第二部」「上」）当子串不算证据
        let substantial = { (candidate: String) in candidate.count >= 4 && target.contains(candidate) }
        if substantial(normalizedTitle) { return 85 }
        if normalizedAliases.contains(where: substantial) { return 70 }
        if normalizedTitle.contains(target) { return 55 }
        if normalizedAliases.contains(where: { $0.contains(target) }) { return 40 }
        return 0
    }

    nonisolated static func normalize(_ value: String) -> String {
        value
            .lowercased()
            .replacingOccurrences(of: #"[【】（）《》「」『』\[\]()·\.\-_\s:：,，、]"#, with: "", options: .regularExpression)
    }
}

enum DoubanSearchParser {
    /// 从搜索页 HTML 里取 `window.__DATA__ = {...}` 并解析成候选列表。
    /// 这里用 JSONSerialization 而不是 Codable：`__DATA__` 是豆瓣页面里内嵌的一段字面量，
    /// 字段说加就加，只取需要的键比维护一个全量模型稳当。
    nonisolated static func parse(html: String) -> [DoubanSearchHit] {
        guard let payload = extractPayload(from: html),
              let object = try? JSONSerialization.jsonObject(with: payload) as? [String: Any],
              let items = object["items"] as? [[String: Any]] else {
            return []
        }
        return items.compactMap { item -> DoubanSearchHit? in
            guard let id = intValue(item["id"]), id > 0 else { return nil }
            let rawTitle = cleanTitle(stringValue(item["title"]) ?? stringValue(item["name"]))
            guard !rawTitle.isEmpty else { return nil }
            let abstract = stringValue(item["abstract"])
            let breakdown = splitAbstract(abstract)
            var year: Int?
            if let match = rawTitle.firstMatch(pattern: #"\((\d{4})\)"#), match.count > 1 {
                year = Int(match[1])
            }
            // 年份单独存字段，标题里就不留「(2024)」了
            let title = rawTitle
                .replacingOccurrences(of: #"\s*\(\d{4}\)"#, with: "", options: .regularExpression)
                .trimmingCharacters(in: .whitespacesAndNewlines)
            guard !title.isEmpty else { return nil }
            var minutes: Int?
            if let match = (abstract ?? "").firstMatch(pattern: #"(\d{1,4})\s*分钟"#), match.count > 1 {
                minutes = Int(match[1])
            }
            let labels = (item["labels"] as? [[String: Any]] ?? [])
                .compactMap { stringValue($0["text"]) }
                .filter { !$0.isEmpty }
            let rating = (item["rating"] as? [String: Any]).flatMap { doubleValue($0["value"]) }
            return DoubanSearchHit(
                id: id,
                title: title,
                year: year,
                labels: labels,
                genres: breakdown.genres,
                aliases: breakdown.aliases,
                episodeMinutes: minutes,
                rating: rating,
                coverURL: stringValue(item["cover_url"]) ?? stringValue(item["coverUrl"]),
                subjectURL: stringValue(item["url"]) ?? "https://movie.douban.com/subject/\(id)/"
            )
        }
    }

/// `window.__DATA__ = {...};`，用括号配对截取，避免整页 HTML 进 JSONDecoder。
    nonisolated static func extractPayload(from html: String) -> Data? {
        guard let marker = html.range(of: "__DATA__") else { return nil }
        guard let brace = html[marker.upperBound...].firstIndex(of: "{") else { return nil }
        var depth = 0
        var index = brace
        var inString = false
        var escaped = false
        while index < html.endIndex {
            let character = html[index]
            if inString {
                if escaped {
                    escaped = false
                } else if character == "\\" {
                    escaped = true
                } else if character == "\"" {
                    inString = false
                }
            } else if character == "\"" {
                inString = true
            } else if character == "{" {
                depth += 1
            } else if character == "}" {
                depth -= 1
                if depth == 0 {
                    let slice = html[brace...index]
                    return Data(slice.utf8)
                }
            }
            index = html.index(after: index)
        }
        return nil
    }

    private nonisolated static func stringValue(_ value: Any?) -> String? {
        guard let text = value as? String else { return nil }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }

    private nonisolated static func intValue(_ value: Any?) -> Int? {
        if let number = value as? NSNumber { return number.intValue }
        return (value as? String).flatMap(Int.init)
    }

    private nonisolated static func doubleValue(_ value: Any?) -> Double? {
        if let number = value as? NSNumber { return number.doubleValue }
        return (value as? String).flatMap(Double.init)
    }

    /// abstract 形如「中国大陆 / 爱情 / 短片 / 别名 / 2分钟」：
    /// 国家与类型留在 genres，带年份嫌疑或含空格的短语算别名，分钟数单独提走。
    nonisolated static func splitAbstract(_ abstract: String?) -> (genres: [String], aliases: [String]) {
        guard let abstract else { return ([], []) }
        var genres: [String] = []
        var aliases: [String] = []
        for raw in abstract.split(separator: "/") {
            let piece = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !piece.isEmpty else { continue }
            if piece.containsPattern(#"^\d{1,4}\s*分钟$"#) { continue }
            if isGenreLike(piece) {
                genres.append(piece)
            } else {
                aliases.append(piece)
            }
        }
        return (genres, aliases)
    }

    private nonisolated static func isGenreLike(_ value: String) -> Bool {
        if value.count > 8 { return false }
        if value.containsPattern(#"^\d{4}$"#) { return false }
        if value.contains(" ") { return false }
        return true
    }

    private nonisolated static func cleanTitle(_ raw: String?) -> String {
        (raw ?? "")
            .replacingOccurrences(of: "\u{200e}", with: "")   // 豆瓣标题里夹带的 LTR/RTL 标记
            .replacingOccurrences(of: "\u{200f}", with: "")
            .replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

}

private extension String {
    nonisolated func firstMatch(pattern: String, options: NSRegularExpression.Options = []) -> [String]? {
        matches(pattern: pattern, options: options).first
    }

    nonisolated func containsPattern(_ pattern: String, options: NSRegularExpression.Options = []) -> Bool {
        firstMatch(pattern: pattern, options: options) != nil
    }

    nonisolated func matches(pattern: String, options: NSRegularExpression.Options = []) -> [[String]] {
        guard let regex = try? NSRegularExpression(pattern: pattern, options: options) else { return [] }
        let nsRange = NSRange(startIndex..<endIndex, in: self)
        return regex.matches(in: self, options: [], range: nsRange).map { result in
            (0..<result.numberOfRanges).map { index in
                guard let range = Range(result.range(at: index), in: self) else { return "" }
                return String(self[range])
            }
        }
    }
}

extension DoubanService {
    /// 短剧候选排序：同名 > 同别名 > 包含，再按评分；零分（对不上片名）直接丢掉。
    nonisolated static func rankedShortDramaHits(
        _ hits: [DoubanSearchHit],
        query: String
    ) -> [DoubanSearchHit] {
        hits
            .map { ($0, $0.matchScore(query: query)) }
            .filter { $0.1 > 0 }
            .sorted {
                if $0.1 != $1.1 { return $0.1 > $1.1 }
                if ($0.0.rating ?? 0) != ($1.0.rating ?? 0) { return ($0.0.rating ?? 0) > ($1.0.rating ?? 0) }
                return ($0.0.year ?? 0) > ($1.0.year ?? 0)
            }
            .map(\.0)
    }
}

