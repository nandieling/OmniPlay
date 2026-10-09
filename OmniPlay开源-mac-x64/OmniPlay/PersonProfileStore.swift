import Foundation
import GRDB

// 刮削 credits 后写入人物资料（id/头像仅补空，不覆盖已有数据），预下载头像图片；
// 首次遇到某人物时立即拉取完整作品列表并缓存到本地，避免打开分类文件夹时再在线获取。
// 人名与作品名的中文译名补全见 ChineseNameStore。
enum PersonProfileStore {
    // 默认 MainActor 隔离，下面的状态读写天然串行，不需要额外加锁
    private static var filmographyFetching: Set<String> = []
    private static var backfillStarted = false

    static func upsertFromCredits(directors: [TMDBPersonInfo], casts: [TMDBPersonInfo]) {
        for (kind, list) in [("director", directors), ("cast", casts)] {
            for person in list where person.tmdbId != nil {
                upsert(kind: kind, person: person)
            }
        }
    }

    private static func upsert(kind: String, person: TMDBPersonInfo) {
        guard let tmdbId = person.tmdbId else { return }
        let id = CategoryFolder.folderID(kind: kind, name: person.name)
        if let profilePath = person.profilePath {
            PosterManager.shared.downloadPoster(posterPath: profilePath)
        }
        Task {
            let needsFilmography: Bool = (try? await AppDatabase.shared.dbQueue.write { db -> Bool in
                if var existing = try PersonProfile.fetchOne(db, key: id) {
                    var changed = false
                    if existing.tmdbPersonId == nil {
                        existing.tmdbPersonId = Int64(tmdbId)
                        changed = true
                    }
                    if (existing.photoPath ?? "").isEmpty, let profilePath = person.profilePath, !profilePath.isEmpty {
                        existing.photoPath = profilePath
                        changed = true
                    }
                    if changed {
                        existing.updatedAt = Date().timeIntervalSince1970
                        try existing.update(db)
                    }
                    return (existing.filmographyJson ?? "").isEmpty
                } else {
                    let record = PersonProfile(
                        id: id,
                        name: person.name,
                        kind: kind,
                        tmdbPersonId: Int64(tmdbId),
                        photoPath: person.profilePath,
                        filmographyJson: nil,
                        updatedAt: Date().timeIntervalSince1970
                    )
                    try record.insert(db)
                    return true
                }
            }) ?? false
            if needsFilmography {
                fillFilmography(kind: kind, personId: Int64(tmdbId), profileID: id)
            }
        }
    }

    // 启动后一次性后台补齐缺失的人物作品列表：历史刮削产生的人物档案在刮削时未预取，
    // 这里顺序拉取入库，之后打开分类文件夹即可直接展示简介，不再出现“正在获取”。
    static func backfillMissingFilmographies() {
        guard !backfillStarted else { return }
        backfillStarted = true
        Task.detached(priority: .utility) {
            try? await Task.sleep(nanoseconds: 3_000_000_000)
            // 已脱离主线程，读取 MainActor 隔离的 TMDB 凭据状态需要显式跳回去
            let hasCredential = await MainActor.run {
                TMDBService.shared.currentCredentialSource().isAvailable
            }
            guard hasCredential else { return }
            let profiles = (try? await AppDatabase.shared.dbQueue.read { db in
                try PersonProfile
                    .filter(sql: "tmdbPersonId IS NOT NULL AND (filmographyJson IS NULL OR filmographyJson = '')")
                    .fetchAll(db)
            }) ?? []
            for profile in profiles {
                guard let personId = profile.tmdbPersonId else { continue }
                if let items = await ChineseNameStore.filmography(personId: Int(personId), kind: profile.kind),
                   let data = try? JSONEncoder().encode(items),
                   let json = String(data: data, encoding: .utf8) {
                    try? await AppDatabase.shared.dbQueue.write { db in
                        if var existing = try PersonProfile.fetchOne(db, key: profile.id) {
                            existing.filmographyJson = json
                            existing.updatedAt = Date().timeIntervalSince1970
                            try existing.update(db)
                        }
                    }
                }
                // 顺序拉取并留间歇，避免触发 TMDB 限流
                try? await Task.sleep(nanoseconds: 120_000_000)
            }
        }
    }

    // 拉取人物完整作品列表写入 personProfile.filmographyJson；同一人物并发只触发一次
    private static func fillFilmography(kind: String, personId: Int64, profileID: String) {
        guard !filmographyFetching.contains(profileID) else { return }
        filmographyFetching.insert(profileID)
        Task {
            await runFilmographyFetch(kind: kind, personId: personId, profileID: profileID)
            filmographyFetching.remove(profileID)
        }
    }

    private static func runFilmographyFetch(kind: String, personId: Int64, profileID: String) async {
        guard TMDBService.shared.currentCredentialSource().isAvailable else { return }
        guard let items = await ChineseNameStore.filmography(personId: Int(personId), kind: kind),
              let data = try? JSONEncoder().encode(items),
              let json = String(data: data, encoding: .utf8) else { return }
        try? await AppDatabase.shared.dbQueue.write { db in
            if var profile = try PersonProfile.fetchOne(db, key: profileID) {
                profile.filmographyJson = json
                profile.updatedAt = Date().timeIntervalSince1970
                try profile.update(db)
            }
        }
    }
}
