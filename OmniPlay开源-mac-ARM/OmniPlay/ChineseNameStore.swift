import Foundation
import GRDB

// 中文译名查询结果：区分"TMDB 确实没有中文条目"和"本次请求失败"，
// 前者才写负缓存，后者下一轮重试。
enum ChineseNameResult {
    case found(String)
    case notTranslated
    case requestFailed
}

// TMDB 中文译名缓存（key = person#<人物id> / work#<movie|tv>#<作品id>）
// value 为 NULL 表示"已确认 TMDB 暂无中文译名"，负缓存过期后允许重试。
struct ChineseNameRecord: Codable, FetchableRecord, PersistableRecord {
    static let databaseTableName = "chineseNameCache"

    var key: String
    var value: String?
    var updatedAt: Double
}

// 外文导演/主演名、外文片名的中文译名补全。
//
// TMDB 在缺少中文条目时会直接回传原文名称，刮削结果里就会出现 "Christopher Nolan / Inception"
// 这类外文。这里把"取中文译名 + 落地"收敛到一处：
// 1. 刮削拿到演职员名单后，把外文人名换成 TMDB 中文名，并同步重命名分类文件夹与人物档案；
// 2. 分类文件夹详情页的作品名，用 TMDB 官方别名（alternative_titles）补中文；
// 3. 启动后在后台补齐历史数据，查询结果（含"确实没有中文名"）持久化缓存，避免重复请求。
enum ChineseNameStore {
    // 补齐任务是否已在跑（只在 MainActor 上读写）
    private static var backfillRunning = false

    // 单个人物档案一次最多补全的中文片名数量，避免打开详情页时产生过多额外请求
    private static let maxTitleLookupsPerProfile = 20
    private static let negativeCacheTTL: TimeInterval = 30 * 24 * 60 * 60
    private static let lookupConcurrency = 3

    private static func personKey(_ tmdbId: Int) -> String { "person#\(tmdbId)" }

    private static func workKey(mediaType: String?, id: Int) -> String {
        "work#\(mediaType ?? "unknown")#\(id)"
    }

    // 用户在分类文件夹里手动指定的人物名，key 用刮削到的名字，避免依赖人物 id
    private static func aliasKey(kind: String, name: String) -> String {
        "alias#\(kind)#\(name)"
    }

    // 中文界面 + TMDB 凭据可用才有补全的意义（英文界面应保留原文名）
    private static var isEnabled: Bool {
        let appLang = UserDefaults.standard.string(forKey: "appLanguage") ?? "zh-Hans"
        return appLang != "en" && TMDBService.shared.currentCredentialSource().isAvailable
    }

    // MARK: - 刮削路径

    // 刮削到演职员名单后调用：外文人名换成用户手动名或 TMDB 中文名，顺序与入参保持一致。
    static func localizeCredits(
        directors: [TMDBPersonInfo],
        casts: [TMDBPersonInfo],
        dbQueue: DatabaseQueue? = nil
    ) async -> (directors: [TMDBPersonInfo], casts: [TMDBPersonInfo]) {
        let localizedDirectors = await localizePeople(directors, kind: "director", dbQueue: dbQueue)
        let localizedCasts = await localizePeople(casts, kind: "cast", dbQueue: dbQueue)
        return (localizedDirectors, localizedCasts)
    }

    private static func localizePeople(
        _ people: [TMDBPersonInfo],
        kind: String,
        dbQueue: DatabaseQueue? = nil
    ) async -> [TMDBPersonInfo] {
        guard !people.isEmpty else { return people }
        // 1. 用户在分类文件夹里手动改过的名字优先，不需要网络
        var names = people.map { $0.name }
        let manual = await manualAliasNames(kind: kind, names: people.map(\.name), dbQueue: dbQueue)
        for (index, person) in people.enumerated() {
            guard let manualName = manual[person.name], manualName != person.name else { continue }
            names[index] = manualName
            await applyPersonRename(kind: kind, oldName: person.name, newName: manualName, dbQueue: dbQueue)
        }
        guard isEnabled else { return rebuild(people, names: names) }

        // 2. 仍是外文名的，按 TMDB 人物 id 查中文译名
        let pending = people.indices.filter { index in
            people[index].tmdbId != nil && !TMDBService.containsChinese(names[index])
        }
        guard !pending.isEmpty else { return rebuild(people, names: names) }

        let keys = Set(pending.compactMap { index -> String? in
            guard let id = people[index].tmdbId else { return nil }
            return personKey(id)
        })
        let lookup = await resolveChineseNames(keys: keys) { key in
            guard let id = Int(key.dropFirst("person#".count)) else { return .notTranslated }
            return await TMDBService.shared.fetchChinesePersonName(personId: id)
        }
        for index in pending {
            guard let id = people[index].tmdbId,
                  let chineseName = lookup.names[personKey(id)],
                  chineseName != names[index] else { continue }
            // 只在本次真正解析出中文名时迁移：库里可能已有以外文名建好的档案/文件夹
            if lookup.freshKeys.contains(personKey(id)) {
                await applyPersonRename(kind: kind, oldName: people[index].name, newName: chineseName, dbQueue: dbQueue)
            }
            names[index] = chineseName
        }
        return rebuild(people, names: names)
    }

    private static func rebuild(_ people: [TMDBPersonInfo], names: [String]) -> [TMDBPersonInfo] {
        people.indices.map { index in
            names[index] == people[index].name
                ? people[index]
                : TMDBPersonInfo(name: names[index], tmdbId: people[index].tmdbId, profilePath: people[index].profilePath)
        }
    }

    // 用户手动指定过的人物名（刮削到的名字 → 手动名）
    private static func manualAliasNames(
        kind: String,
        names: [String],
        dbQueue: DatabaseQueue? = nil
    ) async -> [String: String] {
        guard let dbQueue = dbQueue ?? AppDatabase.shared.dbQueue, !names.isEmpty else { return [:] }
        let keys = Set(names.map { aliasKey(kind: kind, name: $0) })
        let snapshot = (try? await dbQueue.read { db -> [ChineseNameRecord] in
            try ChineseNameRecord.fetchAll(db, keys: Array(keys))
        }) ?? []
        var result: [String: String] = [:]
        for record in snapshot {
            guard let value = record.value else { continue }
            let original = record.key.replacingOccurrences(of: "alias#\(kind)#", with: "")
            guard names.contains(original) else { continue }
            result[original] = value
        }
        return result
    }

    // MARK: - 用户手动改名

    /// 分类文件夹里手动修改导演/主演名：迁移库内数据，并记住这个名字，
    /// 下次刮削到同一人物时继续沿用，不再退回外文名。
    @discardableResult
    static func setUserPersonName(
        kind: String,
        currentName: String,
        newName: String,
        dbQueue: DatabaseQueue? = nil
    ) async -> Bool {
        guard let dbQueue = dbQueue ?? AppDatabase.shared.dbQueue else { return false }
        let target = newName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !target.isEmpty, target != currentName else { return false }

        let personID: Int64? = (try? await dbQueue.read { db -> Int64? in
            try PersonProfile.fetchOne(db, key: CategoryFolder.folderID(kind: kind, name: currentName))?.tmdbPersonId
        }) ?? nil

        // 手动名写进译名缓存（正缓存，永不过期）：人物 id 与刮削名各记一条
        let now = Date().timeIntervalSince1970
        var records: [ChineseNameRecord] = [
            ChineseNameRecord(key: aliasKey(kind: kind, name: currentName), value: target, updatedAt: now)
        ]
        if let personID {
            records.append(ChineseNameRecord(key: personKey(Int(personID)), value: target, updatedAt: now))
        }
        let persisted = records
        try? await dbQueue.write { db in
            for record in persisted {
                try record.save(db)
            }
        }
        await applyPersonRename(kind: kind, oldName: currentName, newName: target, dbQueue: dbQueue)
        return true
    }

    // 人物完整作品列表：拉取后补全中文片名，调用方可直接写入 personProfile.filmographyJson。
    static func filmography(personId: Int, kind: String) async -> [TMDBFilmographyItem]? {
        guard let items = await TMDBService.shared.fetchPersonFilmography(personId: personId, kind: kind) else {
            return nil
        }
        return await localizeFilmographyTitles(items)
    }

    // 作品名补全：只处理前若干条缺中文的作品，命中缓存时不产生请求。
    static func localizeFilmographyTitles(_ items: [TMDBFilmographyItem]) async -> [TMDBFilmographyItem] {
        guard isEnabled else { return items }
        var targets: [(index: Int, key: String)] = []
        for (index, item) in items.enumerated() {
            guard let id = item.tmdbId, !TMDBService.containsChinese(item.title) else { continue }
            targets.append((index, workKey(mediaType: item.mediaType?.lowercased(), id: id)))
            if targets.count >= maxTitleLookupsPerProfile { break }
        }
        guard !targets.isEmpty else { return items }

        let lookup = await resolveChineseNames(keys: Set(targets.map(\.key))) { key in
            // work#<mediaType>#<id>；mediaType 未知（老缓存）时由服务层自行探测
            let parts = key.split(separator: "#")
            guard parts.count == 3, let id = Int(parts[2]) else { return .notTranslated }
            let mediaType = parts[1] == "unknown" ? nil : String(parts[1])
            return await TMDBService.shared.fetchChineseTitle(mediaType: mediaType, id: id)
        }
        guard !lookup.names.isEmpty else { return items }

        var result = items
        for target in targets {
            guard let chineseTitle = lookup.names[target.key] else { continue }
            var item = result[target.index]
            item.title = chineseTitle
            result[target.index] = item
        }
        return result
    }

    // MARK: - 历史数据补齐

    // 启动后一次性后台补齐：历史库里已有的外文人名与外文片名。
    static func backfillExistingChineseNames() {
        guard beginBackfill() else { return }
        Task.detached(priority: .utility) {
            // 与开机扫描/刮削错峰，避免抢占 TMDB 配额
            try? await Task.sleep(nanoseconds: 5_000_000_000)
            let renamed = await backfillPersonNames()
            let localizedTitles = await backfillFilmographyTitles()
            await MainActor.run {
                backfillRunning = false
                if renamed || localizedTitles {
                    NotificationCenter.default.post(name: .libraryUpdated, object: nil)
                }
            }
        }
    }

    // 同一时间只允许一轮补齐
    private static func beginBackfill() -> Bool {
        guard isEnabled, !backfillRunning else { return false }
        backfillRunning = true
        return true
    }

    private static func backfillPersonNames() async -> Bool {
        let profiles = (try? await AppDatabase.shared.dbQueue.read { db in
            try PersonProfile
                .filter(sql: "tmdbPersonId IS NOT NULL")
                .fetchAll(db)
        }) ?? []

        var changed = false
        for profile in profiles {
            guard !TMDBService.containsChinese(profile.name), let personId = profile.tmdbPersonId else { continue }
            let lookup = await resolveChineseNames(keys: [personKey(Int(personId))]) { key in
                guard let id = Int(key.dropFirst("person#".count)) else { return .notTranslated }
                return await TMDBService.shared.fetchChinesePersonName(personId: id)
            }
            if let chineseName = lookup.names[personKey(Int(personId))],
               await applyPersonRename(kind: profile.kind, oldName: profile.name, newName: chineseName) {
                changed = true
            }
            // 只在真的打了请求时留间歇，缓存命中时不拖慢补齐
            if !lookup.freshKeys.isEmpty {
                try? await Task.sleep(nanoseconds: 120_000_000)
            }
        }
        return changed
    }

    private static func backfillFilmographyTitles() async -> Bool {
        let profiles = (try? await AppDatabase.shared.dbQueue.read { db in
            try PersonProfile.fetchAll(db)
        }) ?? []

        var changed = false
        for profile in profiles {
            let cached = profile.filmography
            guard cached.contains(where: { !TMDBService.containsChinese($0.title) }) else { continue }
            let localized = await localizeFilmographyTitles(cached)
            guard localized != cached,
                  let data = try? JSONEncoder().encode(localized),
                  let json = String(data: data, encoding: .utf8) else { continue }
            try? await AppDatabase.shared.dbQueue.write { db in
                if var existing = try PersonProfile.fetchOne(db, key: profile.id) {
                    existing.filmographyJson = json
                    existing.updatedAt = Date().timeIntervalSince1970
                    try existing.update(db)
                }
            }
            changed = true
        }
        return changed
    }

    // MARK: - 改名迁移

    // 外文名 → 中文名后迁移以名称为主键的数据：影片名单、分类文件夹封面、人物档案（含作品列表缓存）。
    @discardableResult
    static func applyPersonRename(
        kind: String,
        oldName: String,
        newName: String,
        dbQueue: DatabaseQueue? = nil
    ) async -> Bool {
        guard oldName != newName else { return false }
        guard let dbQueue = dbQueue ?? AppDatabase.shared.dbQueue else { return false }
        let oldID = CategoryFolder.folderID(kind: kind, name: oldName)
        let newID = CategoryFolder.folderID(kind: kind, name: newName)
        let pattern = "%\(oldName)%"

        return (try? await dbQueue.write { db -> Bool in
            let movies = try Movie.filter(
                sql: "directors LIKE ? OR casts LIKE ?",
                arguments: [pattern, pattern]
            ).fetchAll(db)
            let oldFolder = try CategoryFolder.fetchOne(db, key: oldID)
            let oldProfile = try PersonProfile.fetchOne(db, key: oldID)
            guard oldFolder != nil || oldProfile != nil || !movies.isEmpty else { return false }

            for var movie in movies {
                var changed = false
                if !movie.directorList.isEmpty {
                    let renamed = movie.directorList.map { $0 == oldName ? newName : $0 }
                    let encoded = Movie.encodePersonList(renamed)
                    if encoded != movie.directors {
                        movie.directors = encoded
                        changed = true
                    }
                }
                if !movie.castList.isEmpty {
                    let renamed = movie.castList.map { $0 == oldName ? newName : $0 }
                    let encoded = Movie.encodePersonList(renamed)
                    if encoded != movie.casts {
                        movie.casts = encoded
                        changed = true
                    }
                }
                if changed { try movie.update(db) }
            }

            if let folder = oldFolder {
                if var existing = try CategoryFolder.fetchOne(db, key: newID) {
                    if (existing.posterPath ?? "").isEmpty, !(folder.posterPath ?? "").isEmpty {
                        existing.posterPath = folder.posterPath
                        existing.updatedAt = Date().timeIntervalSince1970
                        try existing.update(db)
                    }
                } else {
                    let record = CategoryFolder(
                        id: newID,
                        kind: kind,
                        name: newName,
                        posterPath: folder.posterPath,
                        updatedAt: folder.updatedAt
                    )
                    try record.insert(db)
                }
                try folder.delete(db)
            }

            if let profile = oldProfile {
                if var existing = try PersonProfile.fetchOne(db, key: newID) {
                    var changed = false
                    if existing.tmdbPersonId == nil {
                        existing.tmdbPersonId = profile.tmdbPersonId
                        changed = true
                    }
                    if (existing.photoPath ?? "").isEmpty, !(profile.photoPath ?? "").isEmpty {
                        existing.photoPath = profile.photoPath
                        changed = true
                    }
                    if (existing.filmographyJson ?? "").isEmpty, !(profile.filmographyJson ?? "").isEmpty {
                        existing.filmographyJson = profile.filmographyJson
                        changed = true
                    }
                    if changed {
                        existing.updatedAt = Date().timeIntervalSince1970
                        try existing.update(db)
                    }
                } else {
                    let record = PersonProfile(
                        id: newID,
                        name: newName,
                        kind: profile.kind,
                        tmdbPersonId: profile.tmdbPersonId,
                        photoPath: profile.photoPath,
                        filmographyJson: profile.filmographyJson,
                        updatedAt: profile.updatedAt
                    )
                    try record.insert(db)
                }
                try profile.delete(db)
            }
            return true
        }) ?? false
    }

    // MARK: - 缓存

    // 查询结果：names 为 key → 中文译名（含命中的缓存），freshKeys 是本次真正请求过 TMDB 的 key。
    private struct LookupResult {
        var names: [String: String] = [:]
        var freshKeys: Set<String> = []
    }

    // 先查持久化缓存，未命中的按 key 并发取 TMDB 中文译名；"确实没有中文译名"同样落库，
    // 但请求失败不写缓存，下一轮扫描或打开详情页时会重试。
    private static func resolveChineseNames(
        keys: Set<String>,
        fetch: @escaping @Sendable (String) async -> ChineseNameResult
    ) async -> LookupResult {
        var lookup = LookupResult()
        guard isEnabled, !keys.isEmpty else { return lookup }
        let uniqueKeys = Array(keys)
        let now = Date().timeIntervalSince1970

        let snapshot = (try? await AppDatabase.shared.dbQueue.read { db -> [String: ChineseNameRecord] in
            let records = try ChineseNameRecord.fetchAll(db, keys: uniqueKeys)
            return Dictionary(uniqueKeysWithValues: records.map { ($0.key, $0) })
        }) ?? [:]

        var missing: [String] = []
        for key in uniqueKeys {
            if let record = snapshot[key] {
                if let value = record.value {
                    lookup.names[key] = value
                    continue
                }
                if now - record.updatedAt < negativeCacheTTL { continue }
            }
            missing.append(key)
        }
        guard !missing.isEmpty else { return lookup }

        // fetched / confirmedMissing 只在下面单线程消费循环里写入
        var fetched: [String: String] = [:]
        var confirmedMissing: [String] = []
        await withTaskGroup(of: (String, ChineseNameResult).self) { group in
            var nextIndex = 0
            func enqueueNext() {
                guard nextIndex < missing.count else { return }
                let key = missing[nextIndex]
                nextIndex += 1
                group.addTask { () -> (String, ChineseNameResult) in
                    (key, await fetch(key))
                }
            }
            while nextIndex < min(lookupConcurrency, missing.count) { enqueueNext() }
            for await (key, result) in group {
                lookup.freshKeys.insert(key)
                switch result {
                case .found(let value):
                    fetched[key] = value
                case .notTranslated:
                    confirmedMissing.append(key)
                case .requestFailed:
                    break
                }
                enqueueNext()
            }
        }

        fetched.forEach { lookup.names[$0.key] = $0.value }
        let persisted = fetched.map { ChineseNameRecord(key: $0.key, value: $0.value, updatedAt: now) }
            + confirmedMissing.map { ChineseNameRecord(key: $0, value: nil, updatedAt: now) }
        try? await AppDatabase.shared.dbQueue.write { db in
            for record in persisted {
                try record.save(db)
            }
        }
        return lookup
    }
}
