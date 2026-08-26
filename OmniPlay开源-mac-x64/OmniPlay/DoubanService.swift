import Foundation
import AppKit

enum DoubanServiceError: LocalizedError {
    case invalidSubject
    case coolingDown(Date)
    case blocked(String)
    case httpStatus(Int)
    case invalidResponse

    var errorDescription: String? {
        switch self {
        case .invalidSubject:
            return "请输入豆瓣电影条目链接或 subject ID。"
        case .coolingDown(let date):
            return "豆瓣请求已暂停到 \(Self.format(date))，请稍后再试。"
        case .blocked(let reason):
            return reason
        case .httpStatus(let status):
            return "豆瓣返回 HTTP \(status)。"
        case .invalidResponse:
            return "未能从豆瓣页面解析到有效影视信息。"
        }
    }

    private static func format(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.dateStyle = .short
        formatter.timeStyle = .short
        return formatter.string(from: date)
    }
}

actor DoubanRequestGate {
    private var nextAllowedDate = Date.distantPast
    private let minimumInterval: TimeInterval = 12
    private let cooldownDefaultsKey = "doubanRequestCooldownUntil"

    func acquire() async throws {
        let now = Date()
        let persistedCooldown = Date(timeIntervalSince1970: UserDefaults.standard.double(forKey: cooldownDefaultsKey))
        let cooldownUntil = max(persistedCooldown, Date.distantPast)
        if now < cooldownUntil {
            throw DoubanServiceError.coolingDown(cooldownUntil)
        }
        if now < nextAllowedDate {
            let delay = nextAllowedDate.timeIntervalSince(now)
            try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
        }
        let jitter = Double.random(in: 3...8)
        nextAllowedDate = Date().addingTimeInterval(minimumInterval + jitter)
    }

    func pause(hours: Double) {
        let cooldownUntil = Date().addingTimeInterval(hours * 3600)
        UserDefaults.standard.set(cooldownUntil.timeIntervalSince1970, forKey: cooldownDefaultsKey)
    }
}

final class DoubanService {
    static let shared = DoubanService()

    private let subjectURLPattern = #"https?://(?:movie\.douban\.com/subject|m\.douban\.com/movie/subject)/(\d+)/?"#
    private let gate = DoubanRequestGate()
    private let session: URLSession

    private init() {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 16
        config.timeoutIntervalForResource = 24
        config.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        config.urlCache = nil
        config.httpCookieStorage = nil
        config.httpShouldSetCookies = false
        session = URLSession(configuration: config)
    }

    func normalizedSubject(input: String) -> (id: String, url: URL)? {
        let trimmed = input.trimmingCharacters(in: .whitespacesAndNewlines)
        if let match = trimmed.firstMatch(pattern: subjectURLPattern),
           match.count > 1,
           let url = URL(string: "https://movie.douban.com/subject/\(match[1])/") {
            return (match[1], url)
        }
        let digits = trimmed.filter(\.isNumber)
        guard digits.count >= 5, digits.count == trimmed.count,
              let url = URL(string: "https://movie.douban.com/subject/\(digits)/") else {
            return nil
        }
        return (digits, url)
    }

    func fetchSubject(input: String) async throws -> DoubanSubjectPayload {
        guard let subject = normalizedSubject(input: input) else {
            throw DoubanServiceError.invalidSubject
        }
        try await gate.acquire()

        guard let fetchURL = URL(string: "https://m.douban.com/movie/subject/\(subject.id)/") else {
            throw DoubanServiceError.invalidSubject
        }
        var request = URLRequest(url: fetchURL)
        request.httpMethod = "GET"
        request.setValue("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", forHTTPHeaderField: "Accept")
        request.setValue("zh-CN,zh-Hans;q=0.9,en;q=0.8", forHTTPHeaderField: "Accept-Language")
        request.setValue("https://m.douban.com/movie/", forHTTPHeaderField: "Referer")
        request.setValue("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1", forHTTPHeaderField: "User-Agent")

        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
        } catch {
            throw error
        }
        guard let http = response as? HTTPURLResponse else {
            throw DoubanServiceError.invalidResponse
        }
        if http.url?.host?.contains("sec.douban.com") == true {
            await gate.pause(hours: 12)
            throw DoubanServiceError.blocked("豆瓣返回了验证页面，已暂停豆瓣请求 12 小时。")
        }
        if http.statusCode == 403 || http.statusCode == 429 {
            await gate.pause(hours: 12)
            throw DoubanServiceError.blocked("豆瓣返回 \(http.statusCode)，已暂停豆瓣请求 12 小时以避免继续触发风控。")
        }
        guard http.statusCode == 200 else {
            throw DoubanServiceError.httpStatus(http.statusCode)
        }
        guard let html = String(data: data, encoding: .utf8) ?? String(data: data, encoding: .doubanGB18030) else {
            throw DoubanServiceError.invalidResponse
        }
        if html.contains("检测到有异常请求") || html.contains("sec.douban.com") || html.contains("captcha") {
            await gate.pause(hours: 12)
            throw DoubanServiceError.blocked("豆瓣返回了验证或异常请求页面，已暂停豆瓣请求 12 小时。")
        }
        return try parseSubject(html: html, subjectId: subject.id, subjectURL: subject.url.absoluteString)
    }

    private func parseSubject(html: String, subjectId: String, subjectURL: String) throws -> DoubanSubjectPayload {
        let jsonLDObjects = html
            .matches(pattern: #"<script[^>]+type=["']application/ld\+json["'][^>]*>(.*?)</script>"#, options: [.caseInsensitive, .dotMatchesLineSeparators])
            .compactMap { $0.count > 1 ? $0[1].htmlDecoded.trimmingCharacters(in: .whitespacesAndNewlines) : nil }

        var jsonLD: [String: Any] = [:]
        for raw in jsonLDObjects {
            guard let data = raw.data(using: .utf8),
                  let object = try? JSONSerialization.jsonObject(with: data),
                  let dict = object as? [String: Any],
                  (dict["@type"] as? String)?.lowercased().contains("movie") == true || dict["name"] != nil else {
                continue
            }
            jsonLD = dict
            break
        }

        let title = firstNonEmpty(
            jsonLD["name"] as? String,
            html.firstMatch(pattern: #"<meta[^>]+property=["']og:title["'][^>]+content=["']([^"']+)["']"#, options: [.caseInsensitive, .dotMatchesLineSeparators])?.safeAt(1)?.strippedHTML.htmlDecoded,
            html.firstMatch(pattern: #"<meta[^>]+content=["']([^"']+)["'][^>]+property=["']og:title["']"#, options: [.caseInsensitive, .dotMatchesLineSeparators])?.safeAt(1)?.strippedHTML.htmlDecoded,
            html.firstMatch(pattern: #"<h1[^>]*>(.*?)</h1>"#, options: [.caseInsensitive, .dotMatchesLineSeparators])?.safeAt(1)?.strippedHTML.htmlDecoded,
            html.firstMatch(pattern: #"<span[^>]+property=["']v:itemreviewed["'][^>]*>(.*?)</span>"#, options: [.caseInsensitive, .dotMatchesLineSeparators])?.safeAt(1)?.strippedHTML.htmlDecoded,
            html.firstMatch(pattern: #"<title>(.*?)</title>"#, options: [.caseInsensitive, .dotMatchesLineSeparators])?.safeAt(1)?.replacingOccurrences(of: "(豆瓣)", with: "").strippedHTML.htmlDecoded
        )
        guard let title else {
            throw DoubanServiceError.invalidResponse
        }

        let aggregateRating = jsonLD["aggregateRating"] as? [String: Any]
        let embeddedRating = html.firstMatch(pattern: ##""rating"\s*:\s*\{([^{}]+)\}"##, options: [.caseInsensitive, .dotMatchesLineSeparators])?.safeAt(1)
        let rating = parseRating(html: html, aggregateRating: aggregateRating, embeddedRating: embeddedRating)
        let ratingCount = parseRatingCount(html: html, aggregateRating: aggregateRating, embeddedRating: embeddedRating)

        let normalizedTitle = title.normalizedWhitespace
        if rating == nil && ["豆瓣", "豆瓣电影"].contains(normalizedTitle) {
            throw DoubanServiceError.invalidResponse
        }

        let summary = firstNonEmpty(
            jsonLD["description"] as? String,
            html.firstMatch(pattern: #"<section[^>]+class=["'][^"']*subject-intro[^"']*["'][^>]*>.*?<p[^>]*>(.*?)</p>"#, options: [.caseInsensitive, .dotMatchesLineSeparators])?.safeAt(1)?.strippedHTML.htmlDecoded,
            html.firstMatch(pattern: #"<span[^>]+property=["']v:summary["'][^>]*>(.*?)</span>"#, options: [.caseInsensitive, .dotMatchesLineSeparators])?.safeAt(1)?.strippedHTML.htmlDecoded,
            html.firstMatch(pattern: #"<meta[^>]+name=["']description["'][^>]+content=["']([^"']+)["']"#, options: [.caseInsensitive, .dotMatchesLineSeparators])?.safeAt(1)?.strippedHTML.htmlDecoded,
            html.firstMatch(pattern: #"<meta[^>]+itemprop=["']description["'][^>]+content=["']([^"']+)["']"#, options: [.caseInsensitive, .dotMatchesLineSeparators])?.safeAt(1)?.strippedHTML.htmlDecoded,
            html.firstMatch(pattern: #"<meta[^>]+property=["']og:description["'][^>]+content=["']([^"']+)["']"#, options: [.caseInsensitive, .dotMatchesLineSeparators])?.safeAt(1)?.strippedHTML.htmlDecoded
        )?.normalizedWhitespace

        let genres = extractJSONList(jsonLD["genre"]) +
            html.matches(pattern: #"<span[^>]+property=["']v:genre["'][^>]*>(.*?)</span>"#, options: [.caseInsensitive, .dotMatchesLineSeparators])
                .compactMap { $0.safeAt(1)?.strippedHTML.htmlDecoded.normalizedWhitespace }

        let directors = extractNameList(jsonLD["director"])
        let casts = extractNameList(jsonLD["actor"])
        let posterURL = bestPosterURL(
            candidates: [
                html.firstMatch(pattern: #"<a[^>]+class=["']sub-cover["'][^>]*>.*?<img[^>]+src=["']([^"']+)["']"#, options: [.caseInsensitive, .dotMatchesLineSeparators])?.safeAt(1)?.htmlDecoded,
                html.firstMatch(pattern: #"<img[^>]+rel=["']v:image["'][^>]+src=["']([^"']+)["']"#, options: [.caseInsensitive, .dotMatchesLineSeparators])?.safeAt(1)?.htmlDecoded,
                jsonLD["image"] as? String,
                html.firstMatch(pattern: #"<meta[^>]+itemprop=["']image["'][^>]+content=["']([^"']+)["']"#, options: [.caseInsensitive, .dotMatchesLineSeparators])?.safeAt(1)?.htmlDecoded,
                html.firstMatch(pattern: #"<meta[^>]+property=["']og:image["'][^>]+content=["']([^"']+)["']"#, options: [.caseInsensitive, .dotMatchesLineSeparators])?.safeAt(1)?.htmlDecoded
            ]
        )

        let year = firstNonEmpty(
            (jsonLD["datePublished"] as? String)?.prefixYear,
            html.firstMatch(pattern: #"<div[^>]+class=["']sub-original-title["'][^>]*>.*?\((\d{4})\).*?</div>"#, options: [.caseInsensitive, .dotMatchesLineSeparators])?.safeAt(1),
            html.firstMatch(pattern: #"(\d{4})-[0-9]{2}-[0-9]{2}[^<]*上映"#, options: [.caseInsensitive])?.safeAt(1),
            html.firstMatch(pattern: #"<span[^>]+class=["']year["'][^>]*>\((\d{4})\)</span>"#, options: [.caseInsensitive])?.safeAt(1)
        )
        let originalTitle = firstNonEmpty(
            html.firstMatch(pattern: #"<div[^>]+class=["']sub-original-title["'][^>]*>(.*?)</div>"#, options: [.caseInsensitive, .dotMatchesLineSeparators])?.safeAt(1)?.strippedHTML.htmlDecoded,
            extractInfoValue(html: html, label: "又名")?.components(separatedBy: "/").first?.normalizedWhitespace
        )
        let mobileMetaParts = html.firstMatch(pattern: #"<div[^>]+class=["']sub-meta["'][^>]*>(.*?)</div>"#, options: [.caseInsensitive, .dotMatchesLineSeparators])?.safeAt(1)?
            .strippedHTML.htmlDecoded
            .components(separatedBy: "/")
            .map(\.normalizedWhitespace)
            .filter { !$0.isEmpty } ?? []
        let mobileCountries = mobileMetaParts.filter { isLikelyCountry($0) }
        let mobileGenres = mobileMetaParts.filter { isLikelyGenre($0) }
        let countries = (extractInfoValue(html: html, label: "制片国家/地区")?
            .components(separatedBy: "/")
            .map(\.normalizedWhitespace)
            .filter { !$0.isEmpty } ?? []) + mobileCountries

        return DoubanSubjectPayload(
            subjectId: subjectId,
            subjectURL: subjectURL,
            title: normalizedTitle,
            originalTitle: originalTitle,
            year: year,
            rating: rating,
            ratingCount: ratingCount,
            summary: summary,
            genres: Array(Set((genres + mobileGenres).map(\.normalizedWhitespace).filter { !$0.isEmpty })).sorted(),
            countries: Array(Set(countries)).sorted(),
            directors: directors,
            casts: casts,
            posterURL: posterURL
        )
    }

    private func extractJSONList(_ value: Any?) -> [String] {
        if let value = value as? String { return [value] }
        if let values = value as? [String] { return values }
        return []
    }

    private func extractNameList(_ value: Any?) -> [String] {
        if let dict = value as? [String: Any], let name = dict["name"] as? String { return [name] }
        if let values = value as? [[String: Any]] {
            return values.compactMap { $0["name"] as? String }
        }
        return []
    }

    private func bestPosterURL(candidates: [String?]) -> String? {
        candidates
            .compactMap { $0?.trimmingCharacters(in: .whitespacesAndNewlines) }
            .compactMap(normalizedPosterURL)
            .first { DoubanMetadata.isUsablePosterURL($0) }
    }

    private func normalizedPosterURL(_ value: String) -> String? {
        guard !value.isEmpty else { return nil }
        var result = value
        if result.hasPrefix("//") { result = "https:\(result)" }

        if result.contains("/view/photo/large/public/") || result.contains("/view/photo/photo/public/") {
            result = result.replacingOccurrences(of: "/view/photo/large/public/", with: "/view/photo/s_ratio_poster/public/")
            result = result.replacingOccurrences(of: "/view/photo/photo/public/", with: "/view/photo/s_ratio_poster/public/")
        }

        if let questionRange = result.range(of: "?") {
            result = String(result[..<questionRange.lowerBound])
        }
        return result
    }

    private func numericValue(_ value: Any?) -> Double? {
        if let number = value as? NSNumber { return number.doubleValue }
        if let string = value as? String {
            return Double(string.trimmingCharacters(in: .whitespacesAndNewlines))
        }
        return nil
    }

    private func integerValue(_ value: Any?) -> Int? {
        if let number = value as? NSNumber { return number.intValue }
        if let string = value as? String {
            return Int(string.replacingOccurrences(of: ",", with: "").trimmingCharacters(in: .whitespacesAndNewlines))
        }
        return nil
    }

    private func parseRating(html: String, aggregateRating: [String: Any]?, embeddedRating: String?) -> Double? {
        if let value = numericValue(aggregateRating?["ratingValue"]) { return value }
        if let value = numericValue(aggregateRating?["rating"]) { return value }

        let patterns = [
            #"<strong[^>]+property=["']v:average["'][^>]*>(.*?)</strong>"#,
            #"<strong[^>]+class=["'][^"']*rating_num[^"']*["'][^>]*>(.*?)</strong>"#,
            #"<span[^>]+class=["'][^"']*rating_num[^"']*["'][^>]*>(.*?)</span>"#,
            #"<meta[^>]+itemprop=["']ratingValue["'][^>]+content=["']([0-9]+(?:\.[0-9]+)?)["']"#,
            #"<meta[^>]+content=["']([0-9]+(?:\.[0-9]+)?)["'][^>]+itemprop=["']ratingValue["']"#,
            #""ratingValue"\s*:\s*"?([0-9]+(?:\.[0-9]+)?)"#,
            #""rating_num"\s*:\s*"?([0-9]+(?:\.[0-9]+)?)"#,
            #""value"\s*:\s*"?([0-9]+(?:\.[0-9]+)?)"?\s*,\s*"max""#,
            #""average"\s*:\s*"?([0-9]+(?:\.[0-9]+)?)"#,
            #"<span[^>]+class=["'][^"']*rating[^"']*["'][^>]*>\s*([0-9]+(?:\.[0-9]+)?)\s*</span>"#
        ]
        for pattern in patterns {
            if let value = htmlNumber(pattern: pattern, html: html) { return value }
        }
        if let embeddedRating {
            return htmlNumber(pattern: ##""value"\s*:\s*"?([0-9]+(?:\.[0-9]+)?)"?"##, html: embeddedRating)
        }
        return nil
    }

    private func parseRatingCount(html: String, aggregateRating: [String: Any]?, embeddedRating: String?) -> Int? {
        if let value = integerValue(aggregateRating?["ratingCount"]) { return value }
        if let value = integerValue(aggregateRating?["reviewCount"]) { return value }

        let patterns = [
            #"<span[^>]+property=["']v:votes["'][^>]*>(.*?)</span>"#,
            #"<meta[^>]+itemprop=["']reviewCount["'][^>]+content=["']([0-9,]+)["']"#,
            #"<meta[^>]+content=["']([0-9,]+)["'][^>]+itemprop=["']reviewCount["']"#,
            #"([0-9,]+)\s*人评价"#,
            #"([0-9,]+)\s*人评分"#,
            ##""ratingCount"\s*:\s*"?([0-9,]+)"?"##,
            ##""count"\s*:\s*"?([0-9,]+)"?"##
        ]
        for pattern in patterns {
            if let value = htmlInteger(pattern: pattern, html: html) { return value }
        }
        if let embeddedRating {
            return htmlInteger(pattern: ##""count"\s*:\s*"?([0-9,]+)"?"##, html: embeddedRating)
        }
        return nil
    }

    private func isLikelyCountry(_ value: String) -> Bool {
        let known = ["中国大陆", "中国香港", "中国台湾", "美国", "英国", "法国", "德国", "意大利", "日本", "韩国", "印度", "加拿大", "澳大利亚", "西班牙", "俄罗斯", "泰国"]
        return known.contains(value)
    }

    private func isLikelyGenre(_ value: String) -> Bool {
        let known = ["剧情", "喜剧", "动作", "爱情", "科幻", "动画", "悬疑", "惊悚", "恐怖", "犯罪", "同性", "音乐", "歌舞", "传记", "历史", "战争", "西部", "奇幻", "冒险", "灾难", "武侠", "古装", "运动", "家庭", "纪录片", "短片"]
        return known.contains(value)
    }

    private func htmlNumber(pattern: String, html: String) -> Double? {
        guard let raw = html.firstMatch(pattern: pattern, options: [.caseInsensitive, .dotMatchesLineSeparators])?.safeAt(1) else {
            return nil
        }
        return Double(raw.strippedHTML.htmlDecoded.normalizedWhitespace)
    }

    private func htmlInteger(pattern: String, html: String) -> Int? {
        guard let raw = html.firstMatch(pattern: pattern, options: [.caseInsensitive, .dotMatchesLineSeparators])?.safeAt(1) else {
            return nil
        }
        return Int(raw.strippedHTML.htmlDecoded.normalizedWhitespace.replacingOccurrences(of: ",", with: ""))
    }

    private func extractInfoValue(html: String, label: String) -> String? {
        guard let range = html.range(of: "<span class=\"pl\">\(label):</span>") else { return nil }
        let tail = html[range.upperBound...]
        guard let end = tail.range(of: "<br") ?? tail.range(of: "</div>") else { return nil }
        return String(tail[..<end.lowerBound]).strippedHTML.htmlDecoded.normalizedWhitespace
    }

    private func firstNonEmpty(_ values: String?...) -> String? {
        values
            .compactMap { $0?.trimmingCharacters(in: .whitespacesAndNewlines) }
            .first { !$0.isEmpty }
    }
}

private extension String {
    func firstMatch(pattern: String, options: NSRegularExpression.Options = []) -> [String]? {
        matches(pattern: pattern, options: options).first
    }

    func matches(pattern: String, options: NSRegularExpression.Options = []) -> [[String]] {
        guard let regex = try? NSRegularExpression(pattern: pattern, options: options) else { return [] }
        let nsRange = NSRange(startIndex..<endIndex, in: self)
        return regex.matches(in: self, options: [], range: nsRange).map { result in
            (0..<result.numberOfRanges).map { index in
                guard let range = Range(result.range(at: index), in: self) else { return "" }
                return String(self[range])
            }
        }
    }

    var strippedHTML: String {
        replacingOccurrences(of: #"<[^>]+>"#, with: " ", options: .regularExpression)
    }

    var htmlDecoded: String {
        guard let data = data(using: .utf8),
              let attributed = try? NSAttributedString(
                data: data,
                options: [
                    .documentType: NSAttributedString.DocumentType.html,
                    .characterEncoding: String.Encoding.utf8.rawValue
                ],
                documentAttributes: nil
              ) else {
            return self
                .replacingOccurrences(of: "&nbsp;", with: " ")
                .replacingOccurrences(of: "&amp;", with: "&")
                .replacingOccurrences(of: "&lt;", with: "<")
                .replacingOccurrences(of: "&gt;", with: ">")
                .replacingOccurrences(of: "&quot;", with: "\"")
        }
        return attributed.string
    }

    var normalizedWhitespace: String {
        replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    var prefixYear: String? {
        firstMatch(pattern: #"^(19|20)\d{2}"#)?.first
    }
}

private extension Array where Element == String {
    func safeAt(_ index: Int) -> String? {
        indices.contains(index) ? self[index] : nil
    }
}

private extension String.Encoding {
    static let doubanGB18030 = String.Encoding(
        rawValue: CFStringConvertEncodingToNSStringEncoding(CFStringEncoding(CFStringEncodings.GB_18030_2000.rawValue))
    )
}
