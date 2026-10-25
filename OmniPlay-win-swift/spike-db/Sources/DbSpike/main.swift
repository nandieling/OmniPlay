import Foundation
import GRDB

// GRDB on Windows 的定点探针：建库、迁移、读写、WAL、中文路径，一次跑完。
// 退出码非 0 就是 spike 不过，CI 里这一步单独标红。

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data("db-spike: FAIL \(message)\n".utf8))
    exit(1)
}

let root = FileManager.default.temporaryDirectory
    .appendingPathComponent("omniplay-db-spike-\(UUID().uuidString)", isDirectory: true)
    .appendingPathComponent("觅影数据库", isDirectory: true)   // 中文目录名一起验
try? FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
let dbURL = root.appendingPathComponent("omniplay.sqlite")

var config = Configuration()
config.publicStatementArguments = true
let pool: DatabasePool
do {
    pool = try DatabasePool(path: dbURL.path, configuration: config)
} catch {
    fail("DatabasePool 打不开：\(error)")
}
var migrator = DatabaseMigrator()
migrator.registerMigration("v1") { db in
    try db.create(table: "spike_item") { t in
        t.column("id", .integer).primaryKey()
        t.column("title", .text).notNull()
        t.column("score", .double)
    }
}
migrator.registerMigration("v2") { db in
    // GRDB 默认按属性名当列名，这里就直写 watchedAt
        try db.alter(table: "spike_item") { t in t.add(column: "watchedAt", .text) }
}
do {
    try migrator.migrate(pool)
} catch {
    fail("迁移失败：\(error)")
}

struct SpikeItem: Codable, FetchableRecord, PersistableRecord {
    static let databaseTableName = "spike_item"
    var id: Int64
    var title: String
    var score: Double?
    var watchedAt: String?
}

do {
    try pool.write { db in
        try SpikeItem(id: 1, title: "卧虎藏龙", score: 7.6).insert(db)
        try SpikeItem(id: 2, title: "Inception", score: 8.4).insert(db)
        try SpikeItem(id: 3, title: "第10季", score: nil).insert(db)
    }
} catch {
    fail("写入失败：\(error)")
}

let fetched: [SpikeItem]
do {
    fetched = try pool.read { db in
        try SpikeItem.order(sql: "title COLLATE NOCASE ASC").fetchAll(db)
    }
} catch {
    fail("读取失败：\(error)")
}
guard fetched.count == 3 else { fail("读回 \(fetched.count) 行，预期 3 行") }

do {
    let journal = try pool.read { db in
        try String.fetchOne(db, sql: "PRAGMA journal_mode") ?? ""
    }
    guard journal.lowercased() == "wal" else { fail("journal_mode=\(journal)，预期 wal") }
} catch {
    fail("PRAGMA 读取失败：\(error)")
}

do {
    // 首页搜索用的是 FTS5，Windows 上的 SQLite 没编这个模块的话，搜索功能要另想办法
    try pool.write { db in
        try db.execute(sql: "CREATE VIRTUAL TABLE spike_search USING fts5(title)")
        try db.execute(sql: "INSERT INTO spike_search VALUES ('卧虎藏龙')")
    }
    let hit = try pool.read { db in
        try Int.fetchOne(db, sql: "SELECT count(*) FROM spike_search WHERE spike_search MATCH '卧虎藏龙'") ?? 0
    }
    guard hit == 1 else { fail("FTS5 检索命中 \(hit) 行，预期 1 行") }
} catch {
    fail("FTS5 不可用：\(error)")
}

do {
    // 后台刮削写库时界面还在读：验一验 Windows 上的读写并发
    let semaphore = DispatchSemaphore(value: 0)
    DispatchQueue.global().async {
        try? pool.write { db in
            try SpikeItem(id: 4, title: "并发写入", score: nil).insert(db)
        }
        semaphore.signal()
    }
    _ = try pool.read { db in try SpikeItem.fetchCount(db) }
    semaphore.wait()
} catch {
    fail("并发读写失败：\(error)")
}

let sqliteVersion: String
do {
    sqliteVersion = try pool.read { db in
        try String.fetchOne(db, sql: "SELECT sqlite_version()") ?? "unknown"
    }
} catch {
    fail("版本查询失败：\(error)")
}

try? pool.close()
try? FileManager.default.removeItem(at: root.deletingLastPathComponent())

let platform: String
#if os(Windows)
platform = "windows"
#elseif os(macOS)
platform = "macos"
#else
platform = "other"
#endif
print("db-spike: OK platform=\(platform) sqlite=\(sqliteVersion) pool=wal fts5=ok rows=\(fetched.count)")
