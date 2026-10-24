import Foundation
import GRDB

/// 本地「标记 / 评分 / 评论」存储，对应 `mediaMark` 表。
///
/// 全部接口都收 `Database`，不碰 `AppDatabase.shared`：详情页要在自己的写事务里用，
/// 单元测试也要能在一个内存库上跑。
///
/// 海报墙上电影和剧集都是 `movie` 表的一行（id 存的是 TMDB 的 movie id 或 tv id），
/// 所以记录主键只用本地 id，不带类型；类型（`kind`）只影响往 NeoDB 发哪条站外链，
/// 由文件名和目录结构推导，和刮削时判断剧集用的是同一套规则。
nonisolated enum MediaMarkStore {
    static let tableName = "mediaMark"

    static let createTableSQL = """
        CREATE TABLE IF NOT EXISTS "mediaMark"(\
        "key" TEXT PRIMARY KEY, \
        "kind" TEXT NOT NULL, \
        "localId" INTEGER NOT NULL, \
        "shelfType" TEXT NOT NULL DEFAULT '', \
        "ratingGrade" INTEGER NOT NULL DEFAULT 0, \
        "comment" TEXT, \
        "progressPercent" INTEGER, \
        "isManual" BOOLEAN NOT NULL DEFAULT 0, \
        "updatedAt" DOUBLE NOT NULL DEFAULT 0, \
        "syncedAt" DOUBLE, \
        "lastError" TEXT, \
        "remoteUrl" TEXT)
        """

    static func createTable(_ db: Database) throws {
        try db.execute(sql: createTableSQL)
    }

    static func tableExists(_ db: Database) -> Bool {
        (try? db.tableExists(tableName)) ?? false
    }

    // MARK: - 读

    static func mark(db: Database, localId: Int64) throws -> MediaMark? {
        try mark(db: db, key: MediaMark.key(localId: localId))
    }

    static func mark(db: Database, key: String) throws -> MediaMark? {
        try MarkRow.fetchOne(db, sql: "SELECT * FROM mediaMark WHERE key = ?", arguments: [key]).flatMap(toMark)
    }

    /// 详情页展示用的记录：没有手工标记时退回播放进度推导出来的标记。
    static func editorState(db: Database, localId: Int64) throws -> MediaMark {
        let derived = derivedState(db: db, localId: localId)
        guard var record = try mark(db: db, localId: localId) else {
            var fresh = MediaMark(kind: derivedKind(db: db, localId: localId), localId: localId)
            fresh.shelfType = derived.shelfType
            fresh.progressPercent = derived.progressPercent
            return fresh
        }
        if record.shelfType == .none {
            record.shelfType = derived.shelfType
            record.progressPercent = derived.progressPercent
        }
        return record
    }

    // MARK: - 写

    /// 用户编辑：标记、评分、评论落库，并把记录变脏（等下一轮同步）。
    static func save(db: Database, localId: Int64, shelfType: NeodbShelfType, ratingGrade: Int,
                     comment: String, progressPercent: Int? = nil, isManual: Bool = true,
                     at time: Double = Date().timeIntervalSince1970) throws {
        var record = try mark(db: db, localId: localId) ?? MediaMark(kind: derivedKind(db: db, localId: localId), localId: localId)
        record.kind = derivedKind(db: db, localId: localId)
        record.shelfType = shelfType
        record.ratingGrade = max(0, min(10, ratingGrade))
        record.comment = comment
        if let progressPercent { record.progressPercent = progressPercent }
        record.isManual = isManual
        record.updatedAt = time
        try upsert(db: db, record)
    }

    /// 从 NeoDB 导入：写完之后视为已同步，否则下一轮推送会把远端刚读来的数据再推回去。
    static func applyRemote(db: Database, kind: MediaMarkKind, localId: Int64, remote: NeodbRemoteMark,
                            at time: Double = Date().timeIntervalSince1970,
                            markLocalWatched: Bool) throws {
        var record = try mark(db: db, localId: localId) ?? MediaMark(kind: kind, localId: localId)
        record.kind = kind
        record.shelfType = remote.shelf
        record.ratingGrade = max(0, min(10, remote.ratingGrade ?? 0))
        record.comment = remote.commentTrimmed
        record.updatedAt = time
        record.syncedAt = time
        record.lastError = nil
        try upsert(db: db, record)

        if markLocalWatched, remote.shelf == .complete {
            try markFilesWatched(db: db, localId: localId)
        }
    }

    static func didSync(db: Database, key: String, at time: Double = Date().timeIntervalSince1970) throws {
        try db.execute(
            sql: "UPDATE mediaMark SET syncedAt = ?, lastError = NULL WHERE key = ?",
            arguments: [time, key])
    }

    /// 记下这条记录对应的 NeoDB 条目网页：推送或导入解析出条目后写一次，
    /// 之后弹窗里的「打开 NeoDB 页面」不用再联网查。
    static func setRemoteURL(db: Database, key: String, url: String) throws {
        try db.execute(
            sql: "UPDATE mediaMark SET remoteUrl = ? WHERE key = ? AND (remoteUrl IS NULL OR remoteUrl != ?)",
            arguments: [url, key, url])
    }

    static func didFail(db: Database, key: String, error: String, at time: Double = Date().timeIntervalSince1970) throws {
        try db.execute(
            sql: "UPDATE mediaMark SET lastError = ?, syncedAt = COALESCE(syncedAt, 0) WHERE key = ?",
            arguments: [error, key])
    }

    static func delete(db: Database, key: String) throws {
        try db.execute(sql: "DELETE FROM mediaMark WHERE key = ?", arguments: [key])
    }

    // MARK: - 待同步队列

    /// 需要推给 NeoDB 的记录：改过、且本地 id 是个真的 TMDB id（负数是未刮削的占位条目）。
    static func pendingMarks(db: Database, limit: Int = 200) throws -> [MediaMark] {
        let rows = try MarkRow.fetchAll(
            db,
            sql: """
                SELECT * FROM mediaMark
                WHERE localId > 0 AND (syncedAt IS NULL OR syncedAt < updatedAt)
                ORDER BY updatedAt ASC LIMIT ?
                """,
            arguments: [limit])
        return rows.compactMap(toMark)
    }

    static func pendingCount(db: Database) throws -> Int {
        try Int.fetchOne(
            db,
            sql: "SELECT COUNT(*) FROM mediaMark WHERE localId > 0 AND (syncedAt IS NULL OR syncedAt < updatedAt)") ?? 0
    }

    /// 一行里既没标记也没评分评论：没什么可推的，直接当已同步。
    static func isEmpty(_ record: MediaMark) -> Bool {
        record.shelfType == .none && record.ratingGrade == 0 && record.commentTrimmed.isEmpty
    }

    /// 该影片手动绑定的豆瓣条目号，用来拼 NeoDB 更好认的豆瓣链接。
    static func boundDoubanSubjectID(db: Database, movieId: Int64) -> String? {
        guard (try? db.tableExists("doubanMetadata")) ?? false else { return nil }
        let value = try? String.fetchOne(
            db,
            sql: "SELECT subjectId FROM doubanMetadata WHERE movieId = ? AND COALESCE(subjectId, '') <> ''",
            arguments: [movieId])
        return (value?.isEmpty == false) ? value : nil
    }

    static func title(db: Database, localId: Int64) -> String? {
        try? String.fetchOne(db, sql: "SELECT title FROM movie WHERE id = ?", arguments: [localId])
    }

    // MARK: - 播放进度推导

    /// 由 `videoFile` 的播放进度推导标记：全部播完 = 已看，看过一点 = 在看。
    static func derivedState(db: Database, localId: Int64) -> (shelfType: NeodbShelfType, progressPercent: Int?) {
        let rows = try? FileProgressRow.fetchAll(
            db,
            sql: "SELECT playProgress, duration FROM videoFile WHERE movieId = ?",
            arguments: [localId])
        guard let rows, !rows.isEmpty else { return (.none, nil) }
        var watched = 0
        var progressSum = 0.0
        for row in rows {
            let ratio = row.watchedRatio
            if ratio >= 0.95 { watched += 1 }
            progressSum += ratio
        }
        return deriveShelf(fileCount: rows.count, watched: watched, progressSum: progressSum)
    }

    static func deriveShelf(fileCount: Int, watched: Int, progressSum: Double) -> (shelfType: NeodbShelfType, progressPercent: Int?) {
        guard fileCount > 0 else { return (.none, nil) }
        let percent = Int(((progressSum / Double(fileCount)) * 100).rounded())
        if watched >= fileCount { return (.complete, max(percent, 100)) }
        if watched > 0 || progressSum > 0.01 { return (.progress, min(max(percent, 1), 99)) }
        return (.none, nil)
    }

    /// 电影还是剧集：沿用刮削时那套判断——文件名能解析出季集号，或目录像剧集目录。
    static func derivedKind(db: Database, localId: Int64) -> MediaMarkKind {
        let rows = try? FileNameRow.fetchAll(
            db,
            sql: "SELECT fileName, relativePath FROM videoFile WHERE movieId = ?",
            arguments: [localId])
        guard let rows else { return .movie }
        for row in rows {
            if MediaNameParser.parseEpisodeInfo(from: row.fileName, fallbackIndex: 0).isTVShow { return .tv }
            if MediaNameParser.isLikelyTVEpisodePath(row.relativePath) { return .tv }
        }
        return .movie
    }

    /// 库里有播放记录的条目 id。
    static func watchedItemIDs(db: Database) throws -> [Int64] {
        try Int64.fetchAll(
            db,
            sql: """
                SELECT movieId FROM videoFile
                WHERE movieId IS NOT NULL AND playProgress > 0
                GROUP BY movieId
                """)
    }

    /// 把播放历史收成待同步记录：本地已经播完但还没有任何标记的条目补一行自动记录。
    /// 手工设过标记的条目不动，避免把用户的选择冲掉。返回新写入的行数。
    @discardableResult
    static func harvestWatchHistory(db: Database, now: Double = Date().timeIntervalSince1970) throws -> Int {
        var written = 0
        for localId in try watchedItemIDs(db: db) {
            guard localId > 0 else { continue }
            let existing = try mark(db: db, localId: localId)
            if let existing, existing.isManual { continue }
            let derived = derivedState(db: db, localId: localId)
            guard derived.shelfType != .none else { continue }
            if let existing, existing.shelfType == derived.shelfType,
               let syncedAt = existing.syncedAt, syncedAt >= existing.updatedAt {
                continue
            }
            var record = existing ?? MediaMark(kind: derivedKind(db: db, localId: localId), localId: localId)
            record.kind = derivedKind(db: db, localId: localId)
            record.shelfType = derived.shelfType
            record.progressPercent = derived.progressPercent
            record.isManual = false
            record.updatedAt = now
            try upsert(db: db, record)
            written += 1
        }
        return written
    }

    /// 导入时发现远端标了已看、本地还没播完：把进度顶到片尾，海报墙的三态图标才会跟上。
    static func markFilesWatched(db: Database, localId: Int64) throws {
        try db.execute(
            sql: """
                UPDATE videoFile SET playProgress = CASE WHEN duration > 0 THEN duration ELSE 100 END,
                                     duration = CASE WHEN duration > 0 THEN duration ELSE 100 END
                WHERE movieId = ?
                """,
            arguments: [localId])
    }

    /// 播完那一刻的自动标记：看完即按播放进度补一条「已看」记录（保留已有评分与评论），
    /// 手动设过标记的条目不动，避免把用户的选择冲掉。返回是否真的写了新记录。
    @discardableResult
    static func markFinishedByPlayback(db: Database, localId: Int64,
                                       at time: Double = Date().timeIntervalSince1970) throws -> Bool {
        guard localId > 0 else { return false }
        let existing = try mark(db: db, localId: localId)
        if let existing, existing.isManual { return false }
        let derived = derivedState(db: db, localId: localId)
        guard derived.shelfType != .none else { return false }
        if let existing, existing.shelfType == derived.shelfType,
           existing.progressPercent == derived.progressPercent,
           let syncedAt = existing.syncedAt, syncedAt >= existing.updatedAt {
            return false
        }
        var record = existing ?? MediaMark(kind: derivedKind(db: db, localId: localId), localId: localId)
        record.kind = derivedKind(db: db, localId: localId)
        record.shelfType = derived.shelfType
        record.progressPercent = derived.progressPercent
        record.isManual = false
        record.updatedAt = time
        try upsert(db: db, record)
        return true
    }

    // MARK: - 内部

    private static func upsert(db: Database, _ record: MediaMark) throws {
        try db.execute(
            sql: """
                INSERT INTO mediaMark(key, kind, localId, shelfType, ratingGrade, comment,
                                      progressPercent, isManual, updatedAt, syncedAt, lastError, remoteUrl)
                VALUES(?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(key) DO UPDATE SET
                    kind = excluded.kind,
                    localId = excluded.localId,
                    shelfType = excluded.shelfType,
                    ratingGrade = excluded.ratingGrade,
                    comment = excluded.comment,
                    progressPercent = excluded.progressPercent,
                    isManual = excluded.isManual,
                    updatedAt = excluded.updatedAt,
                    syncedAt = excluded.syncedAt,
                    lastError = excluded.lastError,
                    remoteUrl = COALESCE(excluded.remoteUrl, remoteUrl)
                """,
            arguments: [
                record.key,
                record.kind.rawValue,
                record.localId,
                record.shelfType.rawValue,
                record.ratingGrade,
                record.comment,
                record.progressPercent,
                record.isManual,
                record.updatedAt,
                record.syncedAt,
                record.lastError,
                record.remoteUrl
            ])
    }

    private static func toMark(_ row: MarkRow) -> MediaMark? {
        MediaMark(
            key: row.key,
            kind: MediaMarkKind(rawValue: row.kind) ?? .movie,
            localId: row.localId,
            shelfType: NeodbShelfType(apiValue: row.shelfType),
            ratingGrade: row.ratingGrade ?? 0,
            comment: row.comment ?? "",
            progressPercent: row.progressPercent,
            isManual: row.isManual ?? false,
            updatedAt: row.updatedAt ?? 0,
            syncedAt: row.syncedAt,
            lastError: row.lastError,
            remoteUrl: row.remoteUrl)
    }
}

/// `mediaMark` 的读取行。
private nonisolated struct MarkRow: Codable, FetchableRecord {
    var key: String
    var kind: String
    var localId: Int64
    var shelfType: String?
    var ratingGrade: Int?
    var comment: String?
    var progressPercent: Int?
    var isManual: Bool?
    var updatedAt: Double?
    var syncedAt: Double?
    var lastError: String?
    var remoteUrl: String?
}

/// 播放进度行：`duration` 为 0 时按「有进度就算播完」处理，和详情页保持一致。
private nonisolated struct FileProgressRow: Codable, FetchableRecord {
    var playProgress: Double
    var duration: Double

    var watchedRatio: Double {
        guard duration > 0 else { return playProgress > 0 ? 1 : 0 }
        return min(max(playProgress / duration, 0), 1)
    }
}

private nonisolated struct FileNameRow: Codable, FetchableRecord {
    var fileName: String
    var relativePath: String
}
