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

        // v3: 媒体源支持启用/停用，停用后保留索引一段时间以便快速恢复
        migrator.registerMigration("v3") { db in
            try db.alter(table: "mediaSource") { t in
                t.add(column: "isEnabled", .boolean).notNull().defaults(to: true)
                t.add(column: "disabledAt", .double)
            }
        }
        
        try migrator.migrate(dbQueue)
    }
}
