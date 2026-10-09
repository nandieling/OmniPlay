import Foundation
import GRDB

class AppDatabase {
    static let shared = AppDatabase()
    var dbQueue: DatabaseQueue!
    /// 实际打开的数据库文件（本地镜像）
    var databaseURL: URL?
    /// 当前数据库所在的 iCloud 容器目录（使用本地路径时为 nil）
    var icloudContainerURL: URL?
    
    private init() {}
    
    func setup(databaseURL: URL, icloudContainerURL: URL? = nil) throws {
        dbQueue = try DatabaseQueue(path: databaseURL.path)
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

        try migrator.migrate(dbQueue)
    }
}
