import Foundation
import GRDB

/// 数据库自身出问题（而不是扫描或网络出问题）时给用户的可读错误。
enum AppDatabaseError: LocalizedError {
    case schemaIncomplete([String])

    var errorDescription: String? {
        switch self {
        case .schemaIncomplete(let missing):
            return "媒体库结构不完整，缺少：" + missing.joined(separator: "、")
                + "。请完全退出 OmniPlay 后重新打开，让程序补一次数据库结构。"
        }
    }
}

class AppDatabase {
    static let shared = AppDatabase()
    var dbQueue: (any DatabaseWriter)!
    /// 实际打开的数据库文件（本地镜像）
    var databaseURL: URL?
    /// 当前数据库所在的 iCloud 容器目录（使用本地路径时为 nil）
    var icloudContainerURL: URL?
    /// 启动时发现的数据库问题（nil 表示结构完整）。界面靠它把「数据库没升级完」讲清楚，
    /// 以前这里只在控制台打一行日志，用户看到的只是海报墙莫名其妙空着。
    private(set) var setupError: String?
    
    private init() {}
    
    func setup(databaseURL: URL, icloudContainerURL: URL? = nil) throws {
        dbQueue = try Self.open(path: databaseURL.path)
        self.databaseURL = databaseURL
        self.icloudContainerURL = icloudContainerURL
        
        var migrator = DatabaseMigrator()
        
        // v1: 原始表结构
        migrator.registerMigration("v1") { db in
            try db.create(table: "mediaSource") { t in
                t.autoIncrementedPrimaryKey("id")
                t.column("name", .text).notNull()
                t.column("protocolType", .text).notNull()
                t.column("baseUrl", .text).notNull()
                t.column("authConfig", .text)
            }
            try db.create(table: "movie") { t in
                t.primaryKey("id", .integer)
                t.column("title", .text).notNull()
                t.column("releaseDate", .text)
                t.column("overview", .text)
                t.column("posterPath", .text)
                t.column("voteAverage", .double)
                t.column("isLocked", .boolean).notNull().defaults(to: false)
            }
            try db.create(table: "tvShow") { t in
                t.primaryKey("id", .integer)
                t.column("title", .text).notNull()
                t.column("posterPath", .text)
                t.column("voteAverage", .double)
                t.column("isLocked", .boolean).notNull().defaults(to: false)
            }
            try db.create(table: "videoFile") { t in
                t.primaryKey("id", .text)
                t.column("sourceId", .integer).notNull().references("mediaSource", onDelete: .cascade)
                t.column("relativePath", .text).notNull()
                t.column("fileName", .text).notNull()
                t.column("mediaType", .text).notNull()
                t.column("movieId", .integer).references("movie", onDelete: .setNull)
                t.column("episodeId", .integer).references("tvShow", onDelete: .setNull)
                t.column("playProgress", .double).notNull().defaults(to: 0.0)
            }
        }
        
        // 🌟 v2: 无损平滑升级！自动向表中追加 duration 字段，绝不丢失数据！
        migrator.registerMigration("v2") { db in
            try db.alter(table: "videoFile") { t in
                t.add(column: "duration", .double).notNull().defaults(to: 0.0)
            }
        }

        // v3: 媒体源支持启用/停用，停用后保留索引一段时间以便快速恢复
        migrator.registerMigration("v3") { db in
            try db.alter(table: "mediaSource") { t in
                t.add(column: "isEnabled", .boolean).notNull().defaults(to: true)
                t.add(column: "disabledAt", .double)
            }
        }

        // v4: 记录最近一次退出时仍未播完的文件，用于详情页续播默认选集。
        migrator.registerMigration("v4") { db in
            try db.alter(table: "videoFile") { t in
                t.add(column: "lastPlayedAt", .double)
            }
        }

        // v5: 记录文件大小，用于蓝光 BDMV 主片识别和缓存空间预估。
        migrator.registerMigration("v5") { db in
            try db.alter(table: "videoFile") { t in
                t.add(column: "fileSize", .integer).notNull().defaults(to: 0)
            }
        }

        // v6: 豆瓣只作为手动绑定的辅助元数据源，独立缓存，避免影响 TMDB 主刮削。
        migrator.registerMigration("v6") { db in
            try db.create(table: "doubanMetadata") { t in
                t.primaryKey("movieId", .integer).references("movie", onDelete: .cascade)
                t.column("subjectId", .text).notNull()
                t.column("subjectURL", .text).notNull()
                t.column("title", .text).notNull()
                t.column("originalTitle", .text)
                t.column("year", .text)
                t.column("rating", .double)
                t.column("ratingCount", .integer)
                t.column("summary", .text)
                t.column("genres", .text)
                t.column("countries", .text)
                t.column("directors", .text)
                t.column("casts", .text)
                t.column("posterURL", .text)
                t.column("fetchedAt", .double).notNull()
                t.column("nextRefreshAt", .double).notNull()
                t.column("lastError", .text)
            }
        }

        // v7: 一个媒体源支持局域网入口、多个外网入口和当前入口标签。
        migrator.registerMigration("v7") { db in
            try db.alter(table: "mediaSource") { t in
                t.add(column: "addressConfig", .text)
            }
        }

        // v8: 影片元数据引入导演/主演名单，并支持首页分类文件夹（海报可手动指定或自定义）。
        migrator.registerMigration("v8") { db in
            try db.alter(table: "movie") { t in
                t.add(column: "directors", .text)
                t.add(column: "casts", .text)
            }
            try db.create(table: "categoryFolder") { t in
                t.primaryKey("id", .text)
                t.column("kind", .text).notNull()
                t.column("name", .text).notNull()
                t.column("posterPath", .text)
                t.column("updatedAt", .double)
            }
        }

        // v9: 人物资料（导演/主演头像 + 完整作品列表缓存），支撑分类文件夹人物照片与作品简介。
        migrator.registerMigration("v9") { db in
            try db.create(table: "personProfile") { t in
                t.primaryKey("id", .text)
                t.column("name", .text).notNull()
                t.column("kind", .text).notNull()
                t.column("tmdbPersonId", .integer)
                t.column("photoPath", .text)
                t.column("filmographyJson", .text)
                t.column("updatedAt", .double)
            }
        }
        
        // v10: 中文译名缓存（外文导演/主演名与外文片名的中文译名，含"确认无中文译名"的负缓存）。
        migrator.registerMigration("v10") { db in
            try db.create(table: "chineseNameCache") { t in
                t.primaryKey("key", .text)
                t.column("value", .text)
                t.column("updatedAt", .double).notNull()
            }
        }

        // v11: 不看迁移编号，只按实际结构补齐缺口（原因见 healSchemaGaps 上方的说明）。
        migrator.registerMigration("v11") { db in
            _ = try AppDatabase.healSchemaGaps(db)
        }

        // v12: NeoDB 同步用的本地标记表（已看 / 评分 / 评论），见 MediaMarkStore。
        migrator.registerMigration("v12") { db in
            try MediaMarkStore.createTable(db)
        }

        // v13: 标记表记下条目在 NeoDB 上的网页地址，弹窗里的跳转不用再查一次。
        migrator.registerMigration("v13") { db in
            try MediaMarkStore.createTable(db)
            _ = try AppDatabase.healSchemaGaps(db)
        }

        // v14: 短剧入库。videoFile 记下条目形态（电影 / 剧集 / 短剧），
        // 短剧专属字段走旁表，形态与 doubanMetadata 一致。
        migrator.registerMigration("v14") { db in
            // v13 的自愈可能已经补过这一列（老库先 heal 再走阶梯），这里只在缺的时候加，
            // 否则 duplicate column 会把阶梯卡在 v14，整个库报「升级未完成」。
            let existingColumns = try db.columns(in: "videoFile").map { $0.name.lowercased() }
            if !existingColumns.contains("mediakind") {
                try db.alter(table: "videoFile") { t in
                    t.add(column: "mediaKind", .text)
                }
            }
            try ShortDramaMetadata.createTable(db)
            try db.execute(sql: #"CREATE INDEX IF NOT EXISTS "idx_videoFile_mediaKind" ON videoFile("mediaKind")"#)
        }

        var migrationError: Error?
        do {
            try migrator.migrate(dbQueue)
        } catch {
            migrationError = error
            print("❌ 媒体库迁移失败：\(error)")
        }

        // 迁移半途失败也照样补一次：结构缺口正是扫描入库那句
        // "FOREIGN KEY constraint failed" 的来源，别让它躲在控制台里。
        _ = try? dbQueue.write { try AppDatabase.healSchemaGaps($0) }

        let missing = (try? dbQueue.read { try AppDatabase.missingSchemaObjects($0) }) ?? []
        var problems: [String] = []
        if !missing.isEmpty {
            problems.append("媒体库缺少：" + missing.joined(separator: "、"))
        }
        if let migrationError {
            problems.append("媒体库升级未完成：\(migrationError.localizedDescription)")
        }
        setupError = problems.isEmpty ? nil : problems.joined(separator: "；")

        // 结构不全就别装成正常启动：让调用方拿到一条读得懂的错误，
        // 而不是等扫描结束时才吐一句看不懂的外键失败。
        if !missing.isEmpty {
            throw AppDatabaseError.schemaIncomplete(missing)
        }
    }

    /// 扫描会连续写库。单连接的 DatabaseQueue 把界面线程的读操作排在写事务后面，
    /// 表现就是扫描一开始卡几秒；DatabasePool 用 WAL 开多个读连接，读写不再互相等。
    /// 库文件落在不支持 WAL 的位置（网络盘之类）时退回单连接队列，功能一样。
    private static func open(path: String) throws -> any DatabaseWriter {
        do {
            return try DatabasePool(path: path)
        } catch {
            print("⚠️ 媒体库无法以 WAL 模式打开（\(error.localizedDescription)），退回单连接模式")
            return try DatabaseQueue(path: path)
        }
    }

    /// 全量拷贝库文件前把 WAL 落盘：不然推上云端的副本会缺最近一段事务
    func checkpointForCopy() {
        guard let dbQueue else { return }
        try? dbQueue.write { db in
            try db.execute(sql: "PRAGMA wal_checkpoint(TRUNCATE)")
        }
    }

    /// 库文件被整体替换（比如从 iCloud 拉取覆盖）后，残留的 WAL 会被 SQLite 当成
    /// 未提交事务回放、盖掉新库，所以换文件时必须一起清掉
    static func removeWALFiles(beside databaseURL: URL) {
        for suffix in ["-wal", "-shm"] {
            try? FileManager.default.removeItem(atPath: databaseURL.path + suffix)
        }
    }
}

// MARK: - 结构自愈

extension AppDatabase {
    private struct ExpectedColumn: Sendable {
        let table: String
        let column: String
        let ddl: String
    }

    private struct ExpectedTable: Sendable {
        let name: String
        let ddl: String
    }

    /// 迁移编号在不同版本之间被重排过：x64 版 1.7 及更早的阶梯是 v3=customSubtitle、
    /// v4=isEnabled/disabledAt、v5=lastPlayedAt、v6=fileSize、v7=doubanMetadata、
    /// v8=addressConfig；1.8 把同一批改动挪到了 v3…v7，并把 v8 换成了
    /// movie.directors/casts + categoryFolder。GRDB 只认「这个编号是否已记录」，
    /// 于是老 x64 库升级时新 v8 被当成已应用直接跳过：movie 少了 directors/casts、
    /// categoryFolder 表不存在。扫描时占位影片写不进去，紧随其后的 videoFile 插入只会报
    /// "SQLite error 19: FOREIGN KEY constraint failed"，整批入库回滚，海报墙一片空白。
    /// 下面这份清单按代码真正依赖的结构列出的缺口，缺什么补什么，与编号无关。
    nonisolated private static let expectedTables: [ExpectedTable] = [
        ExpectedTable(name: "doubanMetadata", ddl: """
            CREATE TABLE IF NOT EXISTS "doubanMetadata"("movieId" INTEGER PRIMARY KEY REFERENCES "movie"("id") ON DELETE CASCADE, \
            "subjectId" TEXT NOT NULL, "subjectURL" TEXT NOT NULL, "title" TEXT NOT NULL, "originalTitle" TEXT, "year" TEXT, \
            "rating" DOUBLE, "ratingCount" INTEGER, "summary" TEXT, "genres" TEXT, "countries" TEXT, "directors" TEXT, \
            "casts" TEXT, "posterURL" TEXT, "fetchedAt" DOUBLE NOT NULL, "nextRefreshAt" DOUBLE NOT NULL, "lastError" TEXT)
            """),
        ExpectedTable(name: "categoryFolder", ddl: """
            CREATE TABLE IF NOT EXISTS "categoryFolder"("id" TEXT PRIMARY KEY, "kind" TEXT NOT NULL, "name" TEXT NOT NULL, \
            "posterPath" TEXT, "updatedAt" DOUBLE)
            """),
        ExpectedTable(name: "personProfile", ddl: """
            CREATE TABLE IF NOT EXISTS "personProfile"("id" TEXT PRIMARY KEY, "name" TEXT NOT NULL, "kind" TEXT NOT NULL, \
            "tmdbPersonId" INTEGER, "photoPath" TEXT, "filmographyJson" TEXT, "updatedAt" DOUBLE)
            """),
        ExpectedTable(name: "chineseNameCache", ddl: """
            CREATE TABLE IF NOT EXISTS "chineseNameCache"("key" TEXT PRIMARY KEY, "value" TEXT, "updatedAt" DOUBLE NOT NULL)
            """),
        ExpectedTable(name: "mediaMark", ddl: MediaMarkStore.createTableSQL),
        ExpectedTable(name: "shortDramaMetadata", ddl: ShortDramaMetadata.createTableSQL)
    ]

    nonisolated private static let expectedColumns: [ExpectedColumn] = [
        ExpectedColumn(table: "videoFile", column: "duration", ddl: "DOUBLE NOT NULL DEFAULT 0.0"),
        ExpectedColumn(table: "videoFile", column: "lastPlayedAt", ddl: "DOUBLE"),
        ExpectedColumn(table: "videoFile", column: "fileSize", ddl: "INTEGER NOT NULL DEFAULT 0"),
        ExpectedColumn(table: "mediaSource", column: "isEnabled", ddl: "BOOLEAN NOT NULL DEFAULT 1"),
        ExpectedColumn(table: "mediaSource", column: "disabledAt", ddl: "DOUBLE"),
        ExpectedColumn(table: "mediaSource", column: "addressConfig", ddl: "TEXT"),
        ExpectedColumn(table: "movie", column: "directors", ddl: "TEXT"),
        ExpectedColumn(table: "movie", column: "casts", ddl: "TEXT"),
        ExpectedColumn(table: "mediaMark", column: "remoteUrl", ddl: "TEXT"),
        ExpectedColumn(table: "videoFile", column: "mediaKind", ddl: "TEXT")
    ]

    nonisolated private static let coreTables = ["mediaSource", "movie", "tvShow", "videoFile"]

    /// 缺表建表、缺列加列，返回补齐了哪些对象；空数组表示这个库本来就是完整的。
    @discardableResult
    nonisolated static func healSchemaGaps(_ db: Database) throws -> [String] {
        var healed: [String] = []
        for table in expectedTables {
            if try db.tableExists(table.name) { continue }
            try db.execute(sql: table.ddl)
            healed.append("表 " + table.name)
        }
        for column in expectedColumns {
            guard try db.tableExists(column.table) else { continue }
            let existing = try db.columns(in: column.table).map { $0.name.lowercased() }
            if existing.contains(column.column.lowercased()) { continue }
            try db.execute(
                sql: "ALTER TABLE \"\(column.table)\" ADD COLUMN \"\(column.column)\" \(column.ddl)")
            healed.append("列 \(column.table).\(column.column)")
        }
        if !healed.isEmpty {
            print("🧬 媒体库结构已自动补齐：\(healed.joined(separator: "、"))")
        }
        return healed
    }

    /// 自愈之后仍然缺的东西——这种库没法正常使用，必须明确告诉用户。
    nonisolated static func missingSchemaObjects(_ db: Database) throws -> [String] {
        var missing: [String] = []
        for name in coreTables {
            if try !db.tableExists(name) {
                missing.append(name)
            }
        }
        for table in expectedTables {
            if try !db.tableExists(table.name) {
                missing.append(table.name)
            }
        }
        for column in expectedColumns {
            guard try db.tableExists(column.table) else { continue }
            let existing = try db.columns(in: column.table).map { $0.name.lowercased() }
            if !existing.contains(column.column.lowercased()) {
                missing.append("\(column.table).\(column.column)")
            }
        }
        return missing
    }
}
