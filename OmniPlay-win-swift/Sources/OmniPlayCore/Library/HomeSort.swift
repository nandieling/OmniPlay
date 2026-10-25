import Foundation

/// 首页海报墙的一条记录。核心与界面之间只走这个 DTO（不共享 GRDB 记录），
/// 界面不需要知道数据库长什么样。
public struct LibraryMovie: Equatable {
    public let id: Int64
    public let title: String
    public let releaseDate: String?      // "YYYY-MM-DD"，可能缺
    public let voteAverage: Double?
    public let directors: [String]
    public let casts: [String]

    public init(id: Int64, title: String, releaseDate: String? = nil, voteAverage: Double? = nil,
                directors: [String] = [], casts: [String] = []) {
        self.id = id; self.title = title; self.releaseDate = releaseDate
        self.voteAverage = voteAverage; self.directors = directors; self.casts = casts
    }
}

public enum HomeSortKey: String {
    case name
    case year
    case rating

    public init?(rawOrDefault: String) { self.init(rawValue: rawOrDefault) }
}

public enum CategoryMode: String {
    case none
    case director
    case cast
}

public struct CategoryGroup: Equatable {
    public let id: String
    public let kind: String
    public let name: String
    public let itemIDs: [Int64]

    public init(id: String, kind: String, name: String, itemIDs: [Int64]) {
        self.id = id; self.kind = kind; self.name = name; self.itemIDs = itemIDs
    }
}

/// 分类文件夹 id：与 mac 的 CategoryFolder.folderID 一字不差。
public func categoryFolderID(kind: String, name: String) -> String { "\(kind)#\(name)" }

public func containsChinese(_ value: String) -> Bool {
    value.range(of: #"\p{Han}"#, options: .regularExpression) != nil
}

/// 本地化自然排序（数字按数值比、拼音按 CLDR）。mac 走 localizedStandardCompare，
/// Windows 的 Foundation 同样落到 ICU 的 CLDR 排序；平台不支持时退化成带数字感知的
/// 普通比较，测试用例会在 CI 上把「本机排序不可信」这件事喊出来。
public func localizedNaturalCompare(_ lhs: String, _ rhs: String) -> ComparisonResult {
    #if canImport(Darwin)
    // 与 mac 版海报墙同一把尺子
    return lhs.localizedStandardCompare(rhs)
    #else
    // corelibs-foundation 没有 localizedStandardCompare，走 ICU collator；
    // 显式给 zh_CN，避免 Windows 界面语言把中文排成拼音之外的顺序。
    return lhs.compare(rhs,
                       options: [.caseInsensitive, .numeric, .diacriticInsensitive, .widthInsensitive],
                       locale: Locale(identifier: "zh_CN"))
    #endif
}

/// 首页排序规则，与 mac 的 displayedMovies 一致：名称走本地化自然序，年份与评分缺值当最小，
/// 降序直接翻转比较结果。
public func sortHomeItems(_ items: [LibraryMovie], key: HomeSortKey, ascending: Bool) -> [LibraryMovie] {
    func less(_ a: LibraryMovie, _ b: LibraryMovie) -> Bool {
        switch key {
        case .name:
            return localizedNaturalCompare(a.title, b.title) == .orderedAscending
        case .year:
            return (a.releaseDate ?? "") < (b.releaseDate ?? "")
        case .rating:
            return (a.voteAverage ?? 0) < (b.voteAverage ?? 0)
        }
    }
    return items.sorted { ascending ? less($0, $1) : less($1, $0) }
}

/// 首页分类文件夹分组。规则照抄 mac：一个人可以出现在多个组里，组按首次出现顺序建桶；
/// 没有中文译名的外文分类永远排在中文名后面；中文名之间按本地化序，升降序由界面箭头切换；
/// 同名组比作品数；没有任何人物信息的片子收进末尾的「未标注」。
public func buildCategoryGroups(items: [LibraryMovie], mode: CategoryMode, ascending: Bool) -> [CategoryGroup] {
    guard mode != .none, let kind = mode.rawValue as String? else { return [] }
    var buckets: [String: [LibraryMovie]] = [:]
    var order: [String] = []
    var uncategorized: [LibraryMovie] = []
    for item in items {
        let names = mode == .director ? item.directors : item.casts
        if names.isEmpty {
            uncategorized.append(item)
            continue
        }
        for name in names {
            let id = categoryFolderID(kind: kind, name: name)
            if buckets[id] == nil {
                buckets[id] = []
                order.append(id)
            }
            buckets[id]?.append(item)
        }
    }
    var groups = order.compactMap { id -> CategoryGroup? in
        guard let members = buckets[id], !members.isEmpty else { return nil }
        let name = String(id.dropFirst(kind.count + 1))
        guard !name.isEmpty else { return nil }
        return CategoryGroup(id: id, kind: kind, name: name, itemIDs: members.map(\.id))
    }
    groups.sort { lhs, rhs in
        let lhsForeign = !containsChinese(lhs.name)
        let rhsForeign = !containsChinese(rhs.name)
        if lhsForeign != rhsForeign { return !lhsForeign }
        let cmp = localizedNaturalCompare(lhs.name, rhs.name)
        if cmp != .orderedSame {
            return ascending ? cmp == .orderedAscending : cmp == .orderedDescending
        }
        return lhs.itemIDs.count > rhs.itemIDs.count
    }
    if !uncategorized.isEmpty {
        groups.append(CategoryGroup(
            id: categoryFolderID(kind: kind, name: "未标注"),
            kind: kind, name: "未标注", itemIDs: uncategorized.map(\.id)))
    }
    return groups
}
