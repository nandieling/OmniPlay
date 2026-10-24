import Foundation

// NeoDB 同步的数据模型与纯映射逻辑。
//
// 这里刻意不放任何网络与数据库代码：接口字段、评分换算、站外链解析这些容易写错
// 又容易改坏的东西集中在这一个文件里，单元测试直接对着它跑，不用起数据库也不发出请求。

// MARK: - 错误

nonisolated enum NeodbError: LocalizedError {
    case notConfigured
    case invalidInstance(String)
    case passwordLoginFailed(reason: String)
    case unauthorized
    case forbidden
    case httpStatus(Int)
    case itemNotFound(url: String)
    case itemStillFetching(url: String)
    case wrongItem(localTitle: String)
    case invalidResponse
    case network(String)

    var errorDescription: String? {
        switch self {
        case .notConfigured:
            return "尚未登录 NeoDB，请在设置里先用账户密码登录或粘贴访问令牌。"
        case .invalidInstance(let value):
            return "NeoDB 站内地址不可用：\(value)。"
        case .passwordLoginFailed(let reason):
            return "账户密码登录失败：\(reason)"
        case .unauthorized:
            return "NeoDB 拒绝了当前登录（令牌无效、已被撤销或不属于这个实例），请重新登录。"
        case .forbidden:
            return "NeoDB 拒绝写入：当前令牌可能没有 write 权限。"
        case .httpStatus(let status):
            return "NeoDB 返回 HTTP \(status)。"
        case .itemNotFound(let url):
            return "NeoDB 目录里没有这个条目（\(url)），可以先在详情页绑定豆瓣条目后重试。"
        case .wrongItem(let localTitle):
            return "按 TMDB 编号没能对上 NeoDB 条目（《\(localTitle)》），请先在详情页绑定豆瓣条目再同步。"
        case .itemStillFetching(let url):
            return "NeoDB 正在抓取这个条目（\(url)），稍后再同步一次即可。"
        case .invalidResponse:
            return "NeoDB 返回的内容无法解析。"
        case .network(let reason):
            return "连不上 NeoDB：\(reason)"
        }
    }
}

// MARK: - 本地媒体类型

nonisolated enum MediaMarkKind: String, Codable, CaseIterable {
    case movie
    case tv

    var displayName: String { self == .movie ? "电影" : "剧集" }

    /// TMDB 站外链用来看 NeoDB 抓哪一页；剧集是 /tv/，电影是 /movie/。
    var tmdbPathSegment: String { self == .movie ? "movie" : "tv" }
}

// MARK: - 书架标记

/// 对应 NeoDB 的 `shelf_type`；`.none` 用空串入库，表示本地没有标记（同步时删除远端标记）。
nonisolated enum NeodbShelfType: String, Codable, CaseIterable, Identifiable {
    case none = ""
    case wishlist
    case progress
    case complete
    case dropped

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .none: return "未标记"
        case .wishlist: return "想看"
        case .progress: return "在看"
        case .complete: return "已看"
        case .dropped: return "搁置"
        }
    }

    var apiValue: String? { rawValue.isEmpty ? nil : rawValue }

    init(apiValue: String?) {
        switch apiValue {
        case "wishlist": self = .wishlist
        case "progress": self = .progress
        case "complete": self = .complete
        case "dropped": self = .dropped
        default: self = .none
        }
    }
}

// MARK: - 标记可见性

nonisolated enum NeodbVisibility: Int, CaseIterable, Identifiable {
    case everyOne = 0
    case followers = 1
    case onlyMe = 2

    var id: Int { rawValue }

    var displayName: String {
        switch self {
        case .everyOne: return "公开"
        case .followers: return "关注可见"
        case .onlyMe: return "仅自己"
        }
    }
}

// MARK: - 评分

/// 界面按五星（半格步进）输入，NeoDB 存 1…10 的 `rating_grade`。
nonisolated enum NeodbRating {
    static func grade(fromStars stars: Double) -> Int {
        let clamped = min(max(stars, 0), 5)
        let grade = Int((clamped * 2).rounded())
        return max(0, min(10, grade))
    }

    static func stars(fromGrade grade: Int?) -> Double {
        guard let grade, grade > 0 else { return 0 }
        return Double(min(10, grade)) / 2.0
    }

    static func displayGrade(_ grade: Int?) -> String? {
        guard let grade, grade > 0 else { return nil }
        return "\(grade)/10"
    }
}

// MARK: - 本地标记记录

nonisolated struct MediaMark: Codable, Equatable {
    /// `movie:{tmdbId}` / `tv:{tmdbId}`；本地影片与剧集的 id 存的就是 TMDB id。
    let key: String
    var kind: MediaMarkKind
    var localId: Int64
    var shelfType: NeodbShelfType = .none
    var ratingGrade: Int = 0
    var comment: String = ""
    var progressPercent: Int? = nil
    /// 标记是否由用户在详情页手动设定：手动设定的记录不会被播放历史回填覆盖。
    var isManual: Bool = false
    var updatedAt: Double = 0
    var syncedAt: Double? = nil
    var lastError: String? = nil
    /// 这条记录对应的 NeoDB 条目网页，同步/导入解析出条目后记下，供「打开 NeoDB 页面」用
    var remoteUrl: String? = nil

    /// 电影和剧集在库里都是 `movie` 表的一行，主键只认本地 id；类型另存一列，只影响发哪条站外链。
    static func key(localId: Int64) -> String { "item:\(localId)" }

    static func key(kind: MediaMarkKind, localId: Int64) -> String { key(localId: localId) }

    init(kind: MediaMarkKind, localId: Int64) {
        self.key = Self.key(kind: kind, localId: localId)
        self.kind = kind
        self.localId = localId
    }

    init(key: String, kind: MediaMarkKind, localId: Int64, shelfType: NeodbShelfType,
         ratingGrade: Int, comment: String, progressPercent: Int?, isManual: Bool = false,
         updatedAt: Double, syncedAt: Double?, lastError: String?, remoteUrl: String? = nil) {
        self.key = key
        self.kind = kind
        self.localId = localId
        self.shelfType = shelfType
        self.ratingGrade = ratingGrade
        self.comment = comment
        self.progressPercent = progressPercent
        self.isManual = isManual
        self.updatedAt = updatedAt
        self.syncedAt = syncedAt
        self.lastError = lastError
        self.remoteUrl = remoteUrl
    }

    /// 占位条目（未刮削成功，id 为负）没有 TMDB id，同步不了。
    var isSyncable: Bool { localId > 0 }

    var isDirty: Bool { syncedAt == nil || (syncedAt ?? 0) < updatedAt }

    /// 界面上的「有改动还没同步」：只看真存过的行。播放进度推导出来的标记还没落库时，
    /// 没什么改动可言，别拿这句话骗人。
    var hasPendingLocalChange: Bool { updatedAt > 0 && isDirty }

    var displayTitleHint: String { kind.displayName }

    var commentTrimmed: String { comment.trimmingCharacters(in: .whitespacesAndNewlines) }
}

// MARK: - 站外条目定位

/// 一部影视在站外的身份。豆瓣优先（NeoDB 的豆瓣条目最全），其次 TMDB。
nonisolated struct NeodbExternalRef: Equatable {
    var doubanSubjectID: String? = nil
    var tmdbID: Int? = nil
    var kind: MediaMarkKind = .movie

    var doubanURL: String? {
        guard let doubanSubjectID else { return nil }
        return "https://movie.douban.com/subject/\(doubanSubjectID)/"
    }

    var tmdbURL: String? {
        guard let tmdbID, tmdbID > 0 else { return nil }
        return "https://www.themoviedb.org/\(kind.tmdbPathSegment)/\(tmdbID)"
    }

    /// 送去 `/api/catalog/fetch` 的地址：豆瓣在前，再试本类型推出来的 TMDB 链接，
    /// 最后试另一种类型——TMDB 的 movie id 和 tv id 是两套计数器，同一个数字可能指向
    /// 完全不同的作品，所以调用方必须拿标题校验一遍。
    var lookupURLs: [String] {
        var urls: [String] = []
        if let doubanURL { urls.append(doubanURL) }
        for variant in [kind, kind == .movie ? MediaMarkKind.tv : MediaMarkKind.movie] {
            guard let tmdbID, tmdbID > 0 else { continue }
            let url = "https://www.themoviedb.org/\(variant.tmdbPathSegment)/\(tmdbID)"
            if !urls.contains(url) { urls.append(url) }
        }
        return urls
    }

    var isResolvable: Bool { !lookupURLs.isEmpty }

    /// 从 NeoDB 条目的 `external_resources[].url` 里取出豆瓣 / TMDB id。
    static func parse(externalURLs: [String]) -> NeodbExternalRef {
        var ref = NeodbExternalRef()
        for raw in externalURLs {
            guard let url = URL(string: raw) else { continue }
            let host = url.host?.lowercased() ?? ""
            let path = url.path
            let parts = path.split(separator: "/").map(String.init)
            if host.hasSuffix("douban.com"),
               let index = parts.firstIndex(of: "subject"),
               parts.indices.contains(index + 1),
               parts[index + 1].allSatisfy(\.isNumber),
               !parts[index + 1].isEmpty {
                ref.doubanSubjectID = parts[index + 1]
                continue
            }
            if host.hasSuffix("themoviedb.org"), parts.count >= 2 {
                if parts[0] == "movie", let id = Int(parts[1]) {
                    ref.tmdbID = id
                    ref.kind = .movie
                } else if parts[0] == "tv", let id = Int(parts[1]) {
                    ref.tmdbID = id
                    ref.kind = .tv
                }
            }
        }
        return ref
    }

    /// 只要有一侧的 id 对得上就算同一部片子。
    func matches(_ other: NeodbExternalRef) -> Bool {
        if let doubanSubjectID, let otherDouban = other.doubanSubjectID,
           doubanSubjectID == otherDouban {
            return true
        }
        if let tmdbID, let otherTMDB = other.tmdbID,
           tmdbID == otherTMDB, kind == other.kind {
            return true
        }
        return false
    }
}

/// 只靠站外链定位条目时，用标题确认没走错条目。判不准就放行，别把用户挡在门外。
nonisolated enum NeodbTitleMatch {
    /// 标题对不上时返回 false。任何一侧缺信息都放行，别把用户挡在门外。
    static func likelyMatch(local: String?, remote: String?) -> Bool {
        guard let local, let remote else { return true }
        let left = compact(local), right = compact(remote)
        if left.isEmpty || right.isEmpty { return true }
        if left == right || left.contains(right) || right.contains(left) { return true }
        let leftWords = words(local), rightWords = words(remote)
        guard !leftWords.isEmpty, !rightWords.isEmpty else { return false }
        return !leftWords.isDisjoint(with: rightWords)
    }

    // 去掉标点和空格后的紧凑形式，中文标题主要靠它比对。
    private static func compact(_ value: String) -> String {
        let kept = value.lowercased().unicodeScalars.filter { CharacterSet.alphanumerics.contains($0) }
        return String(String.UnicodeScalarView(kept))
    }

    // 拉丁字母标题按词取交集，译名顺序不同也能认出来。
    private static func words(_ value: String) -> Set<String> {
        Set(value.lowercased().components(separatedBy: CharacterSet.alphanumerics.inverted).filter { $0.count >= 3 })
    }
}

// MARK: - 写标记请求体

/// 对应 NeoDB 的 `MarkInSchema`。POST 时缺省的字段会被服务端清空，所以永远全量带上。
nonisolated struct NeodbMarkPayload: Encodable {
    var shelfType: String
    var visibility: Int
    var commentText: String
    var ratingGrade: Int
    var tags: [String]
    var createdTime: String
    var postToFediverse: Bool

    enum CodingKeys: String, CodingKey {
        case shelfType = "shelf_type"
        case visibility
        case commentText = "comment_text"
        case ratingGrade = "rating_grade"
        case tags
        case createdTime = "created_time"
        case postToFediverse = "post_to_fediverse"
    }

    static func timestamp(_ date: Date) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.string(from: date)
    }

    static func date(fromISO8601 value: String) -> Date? {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: value)
    }

    init(mark: MediaMark, visibility: NeodbVisibility, postToFediverse: Bool, tags: [String] = []) {
        self.shelfType = mark.shelfType.apiValue ?? NeodbShelfType.complete.rawValue
        self.visibility = visibility.rawValue
        self.commentText = mark.commentTrimmed
        self.ratingGrade = max(0, min(10, mark.ratingGrade))
        self.tags = tags
        self.createdTime = Self.timestamp(Date(timeIntervalSince1970: mark.updatedAt > 0 ? mark.updatedAt : Date().timeIntervalSince1970))
        self.postToFediverse = postToFediverse
    }

    init(shelfType: String, visibility: Int, commentText: String, ratingGrade: Int,
         tags: [String], createdTime: String, postToFediverse: Bool) {
        self.shelfType = shelfType
        self.visibility = visibility
        self.commentText = commentText
        self.ratingGrade = ratingGrade
        self.tags = tags
        self.createdTime = createdTime
        self.postToFediverse = postToFediverse
    }

    func encodedBody() throws -> Data {
        try JSONEncoder().encode(self)
    }
}

/// 进度：NeoDB 用 `{type, value}`，影视按百分比。
nonisolated struct NeodbProgressPayload: Encodable {
    var type: String
    var value: String

    enum CodingKeys: String, CodingKey {
        case type
        case value
    }

    init(percent: Int) {
        self.type = "percentage"
        self.value = String(max(0, min(100, percent)))
    }
}

// MARK: - 响应

nonisolated struct NeodbAccount: Decodable {
    var username: String?
    var displayName: String?
    var url: String?

    enum CodingKeys: String, CodingKey {
        case username
        case displayName = "display_name"
        case url
    }

    var label: String {
        if let username, !username.isEmpty {
            guard let displayName, !displayName.isEmpty else { return username }
            return "\(displayName)（@\(username)）"
        }
        return displayName ?? "未知账户"
    }
}

/// 目录条目：只取同步要用的字段。
nonisolated struct NeodbCatalogItem: Decodable {
    var uuid: String
    var category: String?
    var title: String?
    var externalResourceURLs: [String]

    enum CodingKeys: String, CodingKey {
        case uuid
        case category
        case title
        case externalResources = "external_resources"
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        uuid = try container.decode(String.self, forKey: .uuid)
        category = try container.decodeIfPresent(String.self, forKey: .category)
        title = try container.decodeIfPresent(String.self, forKey: .title)
        let resources = try container.decodeIfPresent([ExternalResource].self, forKey: .externalResources) ?? []
        externalResourceURLs = resources.compactMap { $0.url }
    }

    private struct ExternalResource: Decodable {
        var url: String?
    }

    var externalRef: NeodbExternalRef { NeodbExternalRef.parse(externalURLs: externalResourceURLs) }
}

/// 远端标记（MarkSchema 的子集）。
nonisolated struct NeodbRemoteMark: Decodable {
    var shelfType: String?
    var ratingGrade: Int?
    var commentText: String?
    var createdTime: String?
    var itemUUID: String?
    var itemCategory: String?
    var itemTitle: String?
    /// 季 / 集条目的父条目 uuid；非空说明这条标记打在某一季或某一集上。
    var itemParentUUID: String? = nil
    var externalResourceURLs: [String]

    enum CodingKeys: String, CodingKey {
        case shelfType = "shelf_type"
        case ratingGrade = "rating_grade"
        case commentText = "comment_text"
        case createdTime = "created_time"
        case item
    }

    private struct ItemPayload: Decodable {
        var uuid: String?
        var category: String?
        var displayTitle: String?
        var title: String?
        var parentUUID: String?
        var externalResources: [ExternalResource]?

        enum CodingKeys: String, CodingKey {
            case uuid
            case category
            case displayTitle = "display_title"
            case title
            case parentUUID = "parent_uuid"
            case externalResources = "external_resources"
        }

        var resolvedTitle: String? { displayTitle ?? title }

        struct ExternalResource: Decodable {
            var url: String?
        }
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        shelfType = try container.decodeIfPresent(String.self, forKey: .shelfType)
        ratingGrade = try container.decodeIfPresent(Int.self, forKey: .ratingGrade)
        commentText = try container.decodeIfPresent(String.self, forKey: .commentText)
        createdTime = try container.decodeIfPresent(String.self, forKey: .createdTime)
        if let item = try container.decodeIfPresent(ItemPayload.self, forKey: .item) {
            itemUUID = item.uuid
            itemCategory = item.category
            itemTitle = item.resolvedTitle
            itemParentUUID = item.parentUUID
            externalResourceURLs = (item.externalResources ?? []).compactMap { $0.url }
        } else {
            itemUUID = nil
            itemCategory = nil
            itemTitle = nil
            itemParentUUID = nil
            externalResourceURLs = []
        }
    }

    var shelf: NeodbShelfType { NeodbShelfType(apiValue: shelfType) }
    var externalRef: NeodbExternalRef { NeodbExternalRef.parse(externalURLs: externalResourceURLs) }

    /// `created_time` 是 ISO8601；解析不出来时给 0，让调用方当成「没有时间戳」。
    var createdAt: Double {
        guard let createdTime else { return 0 }
        if let date = NeodbMarkPayload.date(fromISO8601: createdTime) {
            return date.timeIntervalSince1970
        }
        // Django 也发得出带偏移量的形式，逐个常见格式试一次。
        let formats = ["yyyy-MM-dd'T'HH:mm:ssXXXXX", "yyyy-MM-dd'T'HH:mm:ssZZZZZ", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"]
        for format in formats {
            let formatter = DateFormatter()
            formatter.locale = Locale(identifier: "en_US_POSIX")
            formatter.dateFormat = format
            if let date = formatter.date(from: createdTime) {
                return date.timeIntervalSince1970
            }
        }
        return 0
    }

    var commentTrimmed: String { (commentText ?? "").trimmingCharacters(in: .whitespacesAndNewlines) }
}

/// 列表接口可能被分页包成 `{"data": [...]}`，也可能直接是数组，两种都吃。
nonisolated enum NeodbShelfResponse {
    static func marks(from data: Data) throws -> [NeodbRemoteMark] {
        guard let object = try? JSONSerialization.jsonObject(with: data) else {
            throw NeodbError.invalidResponse
        }
        let arrays: [[Any]]
        if let array = object as? [[String: Any]] {
            arrays = [array]
        } else if let dict = object as? [String: Any], let array = dict["data"] as? [[String: Any]] {
            arrays = [array]
        } else {
            throw NeodbError.invalidResponse
        }
        return arrays.flatMap { items in
            items.compactMap { item in
                guard let data = try? JSONSerialization.data(withJSONObject: item) else { return nil }
                return try? JSONDecoder().decode(NeodbRemoteMark.self, from: data)
            }
        }
    }

    static func catalogItem(from data: Data) throws -> NeodbCatalogItem {
        try JSONDecoder().decode(NeodbCatalogItem.self, from: data)
    }

    static func account(from data: Data) throws -> NeodbAccount {
        try JSONDecoder().decode(NeodbAccount.self, from: data)
    }
}
