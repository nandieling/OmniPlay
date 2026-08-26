import Foundation
import GRDB

class AppDatabase {
    static let shared = AppDatabase()
    var dbQueue: DatabaseQueue!
    
    private init() {}
    
    func setup(databaseURL: URL) throws {
        dbQueue = try DatabaseQueue(path: databaseURL.path)
        
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

        migrator.registerMigration("v3") { db in
            try db.alter(table: "videoFile") { t in
                t.add(column: "customSubtitle", .text)
            }
        }

        migrator.registerMigration("v4") { db in
            try db.alter(table: "mediaSource") { t in
                t.add(column: "isEnabled", .boolean).notNull().defaults(to: true)
                t.add(column: "disabledAt", .double)
            }
        }

        // v5: 记录最近一次退出时仍未播完的文件，用于详情页续播默认选集。
        migrator.registerMigration("v5") { db in
            try db.alter(table: "videoFile") { t in
                t.add(column: "lastPlayedAt", .double)
            }
        }

        // v6: 记录文件大小，用于蓝光 BDMV 主片识别和缓存空间预估。
        migrator.registerMigration("v6") { db in
            try db.alter(table: "videoFile") { t in
                t.add(column: "fileSize", .integer).notNull().defaults(to: 0)
            }
        }

        // v7: 豆瓣只作为手动绑定的辅助元数据源，独立缓存，避免影响 TMDB 主刮削。
        migrator.registerMigration("v7") { db in
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

        // v8: 一个媒体源支持局域网入口、多个外网入口和当前入口标签。
        migrator.registerMigration("v8") { db in
            try db.alter(table: "mediaSource") { t in
                t.add(column: "addressConfig", .text)
            }
        }
        
        try migrator.migrate(dbQueue)
    }
}
