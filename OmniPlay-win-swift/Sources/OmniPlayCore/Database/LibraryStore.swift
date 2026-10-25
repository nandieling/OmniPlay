import Foundation

/// 本地片库。表名、列名跟 mac 版对齐（movie / videoFile / mediaSource / mediaMark），
/// 以后两边互导库不用做字段翻译。这里只管存取和排序输入，界面长什么样归宿主。
public final class LibraryStore {
    public struct Movie: Sendable {
        public let id: Int64
        public let title: String
        public let year: Int?
        public let posterPath: String?
        public let genres: [String]
        public let directors: [String]
        public let casts: [String]
    }

    public struct Entry: Sendable {
        public let videoFileID: Int64
        public let movie: Movie
        public let path: String
        public let duration: Double
        public let position: Double
        public let lastPlayedAt: Date?
        public let watched: Bool

        public var progressPercent: Int {
            guard duration > 0 else { return watched ? 100 : 0 }
            return min(100, max(0, Int((position / duration * 100).rounded())))
        }

        /// 海报接进来之前的占位色相。有真实海报后这个字段被忽略也无所谓。
        public var tintHue: Double {
            var hash = 5381
            for byte in movie.title.utf8 { hash = ((hash << 5) &+ hash) &+ Int(byte) }
            return Double(abs(hash) % 360) / 360
        }
    }

    public struct Section: Sendable {
        public let id: String
        public let title: String
        public let entries: [Entry]
    }

    public struct Stats: Sendable {
        public let movies: Int
        public let files: Int
        public let watched: Int
        public let resumable: Int
    }

    public struct NewMovie {
        public var title: String
        public var originalTitle: String?
        public var year: Int?
        public var posterPath: String?
        public var genres: [String]
        public var directors: [String]
        public var casts: [String]

        public init(title: String, originalTitle: String? = nil, year: Int? = nil, posterPath: String? = nil,
                    genres: [String] = [], directors: [String] = [], casts: [String] = []) {
            self.title = title
            self.originalTitle = originalTitle
            self.year = year
            self.posterPath = posterPath
            self.genres = genres
            self.directors = directors
            self.casts = casts
        }
    }

    public static let sectionTitles: [String: String] = [
        "continue": "继续播放", "all": "所有影视", "watched": "已看", "unwatched": "未看",
    ]

    private let db: SQLiteDatabase

    public init(database: SQLiteDatabase) throws {
        self.db = database
        try Self.createSchema(in: database)
    }

    public var fileURL: URL { db.fileURL }

    // MARK: - 结构

    static func createSchema(in db: SQLiteDatabase) throws {
        let statements = [
            ##"""
            CREATE TABLE IF NOT EXISTS "mediaSource"(
                "id" INTEGER PRIMARY KEY AUTOINCREMENT,
                "kind" TEXT NOT NULL,
                "name" TEXT NOT NULL,
                "endpoint" TEXT NOT NULL,
                "isEnabled" INTEGER NOT NULL DEFAULT 1,
                "addedAt" REAL NOT NULL DEFAULT 0
            )
            """##,
            ##"""
            CREATE TABLE IF NOT EXISTS "movie"(
                "id" INTEGER PRIMARY KEY AUTOINCREMENT,
                "title" TEXT NOT NULL,
                "originalTitle" TEXT,
                "year" INTEGER,
                "posterPath" TEXT,
                "genres" TEXT,
                "directors" TEXT,
                "casts" TEXT,
                "addedAt" REAL NOT NULL DEFAULT 0,
                "updatedAt" REAL NOT NULL DEFAULT 0
            )
            """##,
            ##"""
            CREATE TABLE IF NOT EXISTS "videoFile"(
                "id" INTEGER PRIMARY KEY AUTOINCREMENT,
                "movieId" INTEGER NOT NULL REFERENCES "movie"("id") ON DELETE CASCADE,
                "sourceId" INTEGER REFERENCES "mediaSource"("id") ON DELETE SET NULL,
                "path" TEXT NOT NULL,
                "duration" REAL NOT NULL DEFAULT 0,
                "lastPosition" REAL NOT NULL DEFAULT 0,
                "lastPlayedAt" REAL,
                "watched" INTEGER NOT NULL DEFAULT 0
            )
            """##,
            // 与 mac 版 MediaMarkStore 同一份结构，NeoDB 同步要用的字段一个不少
            ##"""
            CREATE TABLE IF NOT EXISTS "mediaMark"(
                "key" TEXT PRIMARY KEY,
                "kind" TEXT NOT NULL,
                "localId" INTEGER NOT NULL,
                "shelfType" TEXT NOT NULL DEFAULT '',
                "ratingGrade" INTEGER NOT NULL DEFAULT 0,
                "comment" TEXT,
                "progressPercent" INTEGER,
                "isManual" BOOLEAN NOT NULL DEFAULT 0,
                "updatedAt" DOUBLE NOT NULL DEFAULT 0,
                "syncedAt" DOUBLE,
                "lastError" TEXT,
                "remoteUrl" TEXT
            )
            """##,
            ##"""
            CREATE INDEX IF NOT EXISTS "videoFile_lastPlayed" ON "videoFile"("lastPlayedAt" DESC)
            """##,
        ]
        for statement in statements { try db.exec(statement) }
    }

    // MARK: - 读

    /// 首页的四个分组。排序走 HomeSort，和 mac 那侧逐字一致。
    public func sections() throws -> [Section] {
        [
            Section(id: "continue", title: Self.sectionTitles["continue"]!,
                    entries: try entries(where: ##""watched" = 0 AND "lastPosition" > 0"##,
                                         order: ##""videoFile"."lastPlayedAt" DESC"##, limit: 20)),
            Section(id: "all", title: Self.sectionTitles["all"]!,
                    entries: sortedByName(try entries(where: "1 = 1"))),
            Section(id: "watched", title: Self.sectionTitles["watched"]!,
                    entries: sortedByName(try entries(where: ##""watched" = 1"##))),
            Section(id: "unwatched", title: Self.sectionTitles["unwatched"]!,
                    entries: sortedByName(try entries(where: ##""watched" = 0"##))),
        ]
    }

    public func section(id: String) throws -> Section? {
        try sections().first { $0.id == id }
    }

    public func stats() throws -> Stats {
        let movies = try db.first(##"SELECT COUNT(*) AS "n" FROM "movie""##)?.int64("n") ?? 0
        let files = try db.first(##"SELECT COUNT(*) AS "n" FROM "videoFile""##)?.int64("n") ?? 0
        let watched = try db.first(##"SELECT COUNT(*) AS "n" FROM "videoFile" WHERE "watched" = 1"##)?.int64("n") ?? 0
        let resumable = try db.first(##"SELECT COUNT(*) AS "n" FROM "videoFile" WHERE "watched" = 0 AND "lastPosition" > 0"##)?.int64("n") ?? 0
        return Stats(movies: Int(movies), files: Int(files), watched: Int(watched), resumable: Int(resumable))
    }

    /// 分类页要的影视条目（导演 / 主演分桶由 AppService 拿同一套排序去建）
    public func movies() throws -> [Movie] {
        let rows = try db.query(##"SELECT "id" AS "movieId", "title", "year", "posterPath", "genres", "directors", "casts" FROM "movie" ORDER BY "id""##)
        return rows.map(Self.movie(from:))
    }

    // MARK: - 写

    public func setWatched(videoFileID: Int64, _ watched: Bool, now: Date = Date()) throws {
        _ = try db.run(##"UPDATE "videoFile" SET "watched" = ? WHERE "id" = ?"##,
                       [.int(watched ? 1 : 0), .int(videoFileID)])
        try upsertMark(key: videoFileID, shelfType: watched ? "markWatched" : "markWatchedUndo",
                       percent: nil, now: now)
    }

    public func recordProgress(videoFileID: Int64, position: Double, duration: Double, now: Date = Date()) throws {
        _ = try db.run(##"UPDATE "videoFile" SET "lastPosition" = ?, "duration" = ?, "lastPlayedAt" = ? WHERE "id" = ?"##,
                       [.real(position), .real(duration), .real(now.timeIntervalSince1970), .int(videoFileID)])
        let percent = duration > 0 ? min(100, max(0, Int((position / duration * 100).rounded()))) : 0
        try upsertMark(key: videoFileID, shelfType: nil, percent: percent, now: now)
    }

    @discardableResult
    public func addMovie(_ movie: NewMovie, now: Date = Date()) throws -> Int64 {
        let stamp = now.timeIntervalSince1970
        let written = try db.run(##"INSERT INTO "movie"("title", "originalTitle", "year", "posterPath", "genres", "directors", "casts", "addedAt", "updatedAt") VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)"##,
            [.text(movie.title), movie.originalTitle.map { SQLiteValue.text($0) } ?? .null,
             movie.year.map { SQLiteValue.int(Int64($0)) } ?? .null,
             movie.posterPath.map { SQLiteValue.text($0) } ?? .null,
             .text(Self.encode(movie.genres)), .text(Self.encode(movie.directors)), .text(Self.encode(movie.casts)),
             .real(stamp), .real(stamp)])
        return written.lastRowID
    }

    @discardableResult
    public func addVideoFile(movieID: Int64, sourceID: Int64?, path: String, duration: Double,
                             position: Double = 0, lastPlayedAt: Date? = nil, watched: Bool = false) throws -> Int64 {
        let written = try db.run(##"INSERT INTO "videoFile"("movieId", "sourceId", "path", "duration", "lastPosition", "lastPlayedAt", "watched") VALUES (?, ?, ?, ?, ?, ?, ?)"##,
            [.int(movieID), sourceID.map { SQLiteValue.int($0) } ?? .null, .text(path), .real(duration),
             .real(position), lastPlayedAt.map { SQLiteValue.real($0.timeIntervalSince1970) } ?? .null,
             .int(watched ? 1 : 0)])
        return written.lastRowID
    }

    @discardableResult
    public func addSource(kind: String, name: String, endpoint: String, now: Date = Date()) throws -> Int64 {
        try db.run(##"INSERT INTO "mediaSource"("kind", "name", "endpoint", "addedAt") VALUES (?, ?, ?, ?)"##,
                   [.text(kind), .text(name), .text(endpoint), .real(now.timeIntervalSince1970)]).lastRowID
    }

    /// 账本表 upsert。传 nil 的那一栏保持原样：光记进度不该把用户标好的架子冲掉，
    /// 反过来标记也不该把已经存下的观看百分比清空。
    private func upsertMark(key: Int64, shelfType: String?, percent: Int?, now: Date) throws {
        let sql = ##"""
        INSERT INTO "mediaMark"("key", "kind", "localId", "shelfType", "progressPercent", "updatedAt")
        VALUES (?, 'movie', ?, ?, ?, ?)
        ON CONFLICT("key") DO UPDATE SET
            "shelfType" = COALESCE(NULLIF(excluded."shelfType", ''), "mediaMark"."shelfType"),
            "progressPercent" = COALESCE(excluded."progressPercent", "mediaMark"."progressPercent"),
            "updatedAt" = excluded."updatedAt"
        """##
        _ = try db.run(sql, [.text("movie:\(key)"), .int(key), .text(shelfType ?? ""),
                             percent.map { SQLiteValue.int(Int64($0)) } ?? .null,
                             .real(now.timeIntervalSince1970)])
    }

    // MARK: - 内部

    /// 两张表都有 id，列名一律先起好别名再取，省得靠 SQLite 猜要给哪个
    private static let selectEntries = ##"""
        SELECT "videoFile"."id" AS "fileId", "videoFile"."path" AS "path",
               "videoFile"."duration" AS "duration", "videoFile"."lastPosition" AS "position",
               "videoFile"."lastPlayedAt" AS "lastPlayedAt", "videoFile"."watched" AS "watched",
               "movie"."id" AS "movieId", "movie"."title" AS "title", "movie"."year" AS "year",
               "movie"."posterPath" AS "posterPath", "movie"."genres" AS "genres",
               "movie"."directors" AS "directors", "movie"."casts" AS "casts"
        FROM "videoFile" JOIN "movie" ON "movie"."id" = "videoFile"."movieId"
        WHERE 
        """##
    private func entries(where clause: String, order: String = libraryDefaultOrder,
                         limit: Int? = nil) throws -> [Entry] {
        var sql = Self.selectEntries + clause + ##" ORDER BY "## + order
        if let limit { sql += " LIMIT \(limit)" }
        return try db.query(sql).map(Self.entry(from:))
    }

    private static func entry(from row: SQLiteDatabase.Row) -> Entry {
        Entry(videoFileID: row.int64("fileId"),
              movie: movie(from: row),
              path: row.text("path") ?? "",
              duration: row.double("duration"),
              position: row.double("position"),
              lastPlayedAt: row.hasValue("lastPlayedAt") ? Date(timeIntervalSince1970: row.double("lastPlayedAt")) : nil,
              watched: row.flag("watched"))
    }

    /// 名称排序交给公用那套：中文按拼音，外文排后面，两个平台一个结果
    private func sortedByName(_ entries: [Entry]) -> [Entry] {
        let proxies = entries.map {
            LibraryMovie(id: $0.videoFileID, title: $0.movie.title,
                         releaseDate: $0.movie.year.map { String(format: "%04d-01-01", $0) },
                         voteAverage: nil, directors: $0.movie.directors, casts: $0.movie.casts)
        }
        let order = sortHomeItems(proxies, key: .name, ascending: true)
        let byID = Dictionary(uniqueKeysWithValues: entries.map { ($0.videoFileID, $0) })
        return order.compactMap { byID[$0.id] }
    }

    private static func movie(from row: SQLiteDatabase.Row) -> Movie {
        Movie(id: row.int64("movieId"),
              title: row.text("title") ?? "",
              year: row.hasValue("year") ? Int(row.int64("year")) : nil,
              posterPath: row.text("posterPath"),
              genres: decode(row.text("genres")),
              directors: decode(row.text("directors")),
              casts: decode(row.text("casts")))
    }

    /// 演职员列表存成 JSON 文本。够用、可读、不需要再引一层序列化框架。
    static func encode(_ list: [String]) -> String {
        "[" + list.map { "\"" + $0.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"") + "\"" }
            .joined(separator: ",") + "]"
    }

    static func decode(_ text: String?) -> [String] {
        guard let text, let data = text.data(using: .utf8),
              case .array(let items)? = JSONValue.parse(data) else { return [] }
        return items.compactMap { $0.stringValue }
    }
}

/// 没指定排序时的顺序；写在文件级是因为默认参数表达式里不能写 Self
private let libraryDefaultOrder = ##""videoFile"."id""##

private typealias SQLiteValue = SQLiteDatabase.Value
