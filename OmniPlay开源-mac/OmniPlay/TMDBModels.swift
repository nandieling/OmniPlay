import Foundation

// TMDB 搜索接口返回的最外层结构
struct TMDBResponse: Codable {
    let results: [TMDBResult]
}

// 每一部电影/剧集的具体信息
struct TMDBResult: Codable {
    let id: Int
    let title: String?       // 电影通常叫 title
    let name: String?        // 剧集通常叫 name
    let originalTitle: String? // 电影原始标题
    let originalName: String?  // 剧集原始标题
    let overview: String?    // 剧情简介
    let posterPath: String?  // 海报图片的后缀路径
    let releaseDate: String? // 电影上映日期
    let firstAirDate: String?// 剧集首播日期
    let voteAverage: Double? // 接收 TMDB 返回的评分数据
    
    // 🌟 匹配引擎升级：新增流行度和媒体类型
    let popularity: Double?  // 全网流行度热度值
    let mediaType: String?   // 媒体类型 (movie, tv, person)
    
    // 映射 TMDB 的下划线命名法到 Swift 的驼峰命名法
    enum CodingKeys: String, CodingKey {
        case id, title, name, overview
        case originalTitle = "original_title"
        case originalName = "original_name"
        case posterPath = "poster_path"
        case releaseDate = "release_date"
        case firstAirDate = "first_air_date"
        case voteAverage = "vote_average"
        
        // 🌟 映射新增字段
        case popularity
        case mediaType = "media_type"
    }
    
    // 一个便利属性：因为 TMDB 只返回后缀，我们需要自己拼出完整的图片网址
    var posterURL: URL? {
        guard let path = posterPath else { return nil }
        if path.hasPrefix("http://") || path.hasPrefix("https://") {
            return URL(string: path)
        }
        return URL(string: "https://image.tmdb.org/t/p/w500\(path)")
    }
    
    // 一个便利属性：统一获取名称（无论是电影还是剧集）
    var displayTitle: String {
        return title ?? name ?? "未知名称"
    }

    var hasChineseDisplayTitle: Bool {
        displayTitle.range(of: #"\p{Han}"#, options: .regularExpression) != nil
    }

    func preferredTitle(chineseFallback: String?) -> String {
        let appLang = UserDefaults.standard.string(forKey: "appLanguage") ?? "zh-Hans"
        guard appLang != "en" else { return displayTitle }
        if hasChineseDisplayTitle {
            return displayTitle.convertTraditionalToSimplifiedChineseIfNeeded()
        }
        guard
            let fallback = chineseFallback?.trimmingCharacters(in: .whitespacesAndNewlines),
            !fallback.isEmpty,
            fallback.range(of: #"\p{Han}"#, options: .regularExpression) != nil
        else { return displayTitle.convertTraditionalToSimplifiedChineseIfNeeded() }
        return fallback.convertTraditionalToSimplifiedChineseIfNeeded()
    }
}

private extension String {
    func convertTraditionalToSimplifiedChineseIfNeeded() -> String {
        guard range(of: #"\p{Han}"#, options: .regularExpression) != nil else { return self }
        let mutable = NSMutableString(string: self)
        let transformed = CFStringTransform(
            mutable as CFMutableString,
            nil,
            "Hant-Hans" as CFString,
            false
        )
        if transformed {
            return mutable as String
        }
        if let converted = applyingTransform(StringTransform("Hant-Hans"), reverse: false) {
            return converted
        }
        return self
    }
}
