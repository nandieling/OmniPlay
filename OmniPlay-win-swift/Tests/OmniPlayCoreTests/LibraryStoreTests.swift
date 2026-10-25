//
//  LibraryStoreTests.swift
//  片库这一层的规矩：筛得对不对、排得对不对、写下去的东西读不读得回来。
//  这里既直接测 LibraryStore，也过一遍桥——界面只会走桥，光测类不等于能用。
//

import XCTest
@testable import OmniPlayCore

final class LibraryStoreTests: XCTestCase {
    private let directory = FileManager.default.temporaryDirectory
        .appendingPathComponent("omniplay-library-\(UUID().uuidString)", isDirectory: true)
    private var service: OmniPlayService!

    override func setUpWithError() throws {
        service = OmniPlayService(hooks: PlatformHooks(dataDirectory: { [directory] in directory }))
    }

    override func tearDownWithError() throws {
        service = nil
        try? FileManager.default.removeItem(at: directory)
    }

    // MARK: - 助手

    private func store() throws -> LibraryStore {
        try LibraryStore(database: SQLiteDatabase(fileURL: directory.appendingPathComponent("library.db")))
    }

    private func call(_ method: String, _ params: JSONValue = .object([:])) throws -> JSONValue {
        let request = JSONValue.object(["method": .string(method), "params": params])
        let envelope = try XCTUnwrap(JSONValue.parse(service.handle(requestJSON: request.serialized())))
        XCTAssertEqual(envelope["ok"]?.boolValue, true,
                       "\(method) 失败：\(envelope["error"]?.stringValue ?? "?")")
        return envelope["result"] ?? .null
    }

    private func errorMessage(_ method: String, _ params: JSONValue = .object([:])) throws -> String {
        let request = JSONValue.object(["method": .string(method), "params": params])
        let envelope = try XCTUnwrap(JSONValue.parse(service.handle(requestJSON: request.serialized())))
        XCTAssertEqual(envelope["ok"]?.boolValue, false, "\(method) 本该报错")
        return envelope["error"]?.stringValue ?? ""
    }

    private func titles(_ section: LibraryStore.Section) -> [String] {
        section.entries.map(\.movie.title)
    }

    // MARK: - 结构

    func testOpenCreatesEveryTable() throws {
        let db = try SQLiteDatabase(fileURL: directory.appendingPathComponent("library.db"))
        try LibraryStore(database: db)
        let names = try db.query(##"SELECT "name" FROM "sqlite_master" WHERE "type" IN ('table', 'index')"##)
            .compactMap { $0.text("name") }
        for table in ["mediaSource", "movie", "videoFile", "mediaMark", "videoFile_lastPlayed"] {
            XCTAssertTrue(names.contains(table), "缺 \(table)")
        }
    }

    // MARK: - 分组

    func testContinueUsesLastPlayedOrder() throws {
        let store = try store()
        let source = try store.addSource(kind: "local", name: "本地", endpoint: "/mnt/media")
        func add(_ title: String, position: Double, playedAgo: TimeInterval, watched: Bool = false) throws {
            let movie = try store.addMovie(LibraryStore.NewMovie(title: title, year: 2020))
            try store.addVideoFile(movieID: movie, sourceID: source, path: "demo://\(title)",
                                   duration: 1000, position: position,
                                   lastPlayedAt: Date(timeIntervalSince1970: 1_700_000_000 - playedAgo),
                                   watched: watched)
        }
        try add("先看这部", position: 100, playedAgo: 60)
        try add("再看这部", position: 200, playedAgo: 10)
        try add("没看过的", position: 0, playedAgo: 5)
        try add("看完了", position: 999, playedAgo: 1, watched: true)

        let sections = try store.sections()
        let cont = try XCTUnwrap(sections.first { $0.id == "continue" })
        XCTAssertEqual(titles(cont), ["再看这部", "先看这部"], "继续播放按最近观看排，且不含零进度与已看")

        let all = try XCTUnwrap(sections.first { $0.id == "all" })
        XCTAssertEqual(all.entries.count, 4)
        XCTAssertEqual(try XCTUnwrap(all.entries.first { $0.movie.title == "看完了" }).progressPercent, 100)
    }

    func testSectionsUsePinyinOrderWithForeignNamesLast() throws {
        let store = try store()
        let source = try store.addSource(kind: "local", name: "本地", endpoint: "/mnt/media")
        for title in ["阿飞正传", "Zodiac", "霸王别姬", "In the Mood for Love"] {
            let movie = try store.addMovie(LibraryStore.NewMovie(title: title, year: 2000))
            try store.addVideoFile(movieID: movie, sourceID: source, path: "demo://\(title)", duration: 60)
        }
        let all = try XCTUnwrap(try store.sections().first { $0.id == "all" })
        XCTAssertEqual(titles(all), ["阿飞正传", "霸王别姬", "In the Mood for Love", "Zodiac"],
                       "中文按拼音在前，外文创作者按字母排后面")
    }

    func testWatchedAndUnwatchedSplit() throws {
        let store = try store()
        let source = try store.addSource(kind: "local", name: "本地", endpoint: "/mnt/media")
        let movie = try store.addMovie(LibraryStore.NewMovie(title: "活着", year: 1994))
        let file = try store.addVideoFile(movieID: movie, sourceID: source, path: "demo://one", duration: 60)

        XCTAssertEqual(titles(try XCTUnwrap(try store.section(id: "watched"))), [])
        XCTAssertEqual(titles(try XCTUnwrap(try store.section(id: "unwatched"))), ["活着"])

        try store.setWatched(videoFileID: file, true)
        XCTAssertEqual(titles(try XCTUnwrap(try store.section(id: "watched"))), ["活着"])
        XCTAssertEqual(titles(try XCTUnwrap(try store.section(id: "unwatched"))), [])

        let marks = try store.sections()  // 顺带确认没写坏
        XCTAssertFalse(marks.isEmpty)
    }

    func testStatsCountsEveryShelf() throws {
        let store = try store()
        let source = try store.addSource(kind: "local", name: "本地", endpoint: "/mnt/media")
        let one = try store.addMovie(LibraryStore.NewMovie(title: "甲"))
        let two = try store.addMovie(LibraryStore.NewMovie(title: "乙"))
        try store.addVideoFile(movieID: one, sourceID: source, path: "a", duration: 100, position: 30)
        try store.addVideoFile(movieID: two, sourceID: source, path: "b", duration: 100, watched: true)

        let stats = try store.stats()
        XCTAssertEqual(stats.movies, 2)
        XCTAssertEqual(stats.files, 2)
        XCTAssertEqual(stats.watched, 1)
        XCTAssertEqual(stats.resumable, 1)
    }

    func testProgressWritesMarkPercent() throws {
        let db = try SQLiteDatabase(fileURL: directory.appendingPathComponent("library.db"))
        let store = try LibraryStore(database: db)
        let source = try store.addSource(kind: "local", name: "本地", endpoint: "/mnt/media")
        let movie = try store.addMovie(LibraryStore.NewMovie(title: "花样年华"))
        let file = try store.addVideoFile(movieID: movie, sourceID: source, path: "demo", duration: 100)

        try store.recordProgress(videoFileID: file, position: 25, duration: 100)
        let percent = try XCTUnwrap(db.first(##"SELECT "progressPercent" AS "p" FROM "mediaMark" WHERE "localId" = ?"##,
                                            [.int(file)])).int64("p")
        XCTAssertEqual(percent, 25, "观看进度要同步进 NeoDB 账本")
    }

    // MARK: - 桥

    func testBridgeSeedsEmptyLibraryOnFirstRead() throws {
        let result = try call("library.sections")
        let sections = try XCTUnwrap(result["sections"]?.arrayValue)
        XCTAssertEqual(sections.count, 4)
        let stats = try XCTUnwrap(result["stats"])
        XCTAssertEqual(stats["movies"]?.intValue, DemoSeed.items.count)
        XCTAssertFalse(try XCTUnwrap(sections.first { $0["id"]?.stringValue == "continue" }?["entries"]?.arrayValue).isEmpty)
    }

    func testBridgeMarkRoundTrip() throws {
        let sections = try XCTUnwrap(call("library.sections")["sections"]?.arrayValue)
        let all = try XCTUnwrap(sections.first { $0["id"]?.stringValue == "all" }?["entries"]?.arrayValue)
        guard let first = all.first, let fileID = first["fileId"]?.int64Value else { return XCTFail("演示数据没给出条目") }

        _ = try call("library.mark", .object(["fileId": .int64(fileID), "watched": .bool(true)]))
        let watched = try XCTUnwrap(call("library.section", .object(["id": .string("watched")]))["section"]?["entries"]?.arrayValue)
        XCTAssertTrue(watched.contains { $0["fileId"]?.int64Value == fileID }, "标记已看之后没进已看分组")
    }

    func testBridgeReportsReadableErrors() throws {
        XCTAssertEqual(try errorMessage("library.section", .object(["id": .string("nope")])), "没有这个分组：nope")
        XCTAssertEqual(try errorMessage("library.mark", .object([:])), "缺少影视文件 id")
    }

    func testLibraryFileLandsInDataDirectory() throws {
        _ = try call("library.stats")
        XCTAssertTrue(FileManager.default.fileExists(atPath: service.libraryFileURL.path))
        XCTAssertEqual(service.libraryFileURL.lastPathComponent, "library.db")
    }

    // MARK: - 编码

    func testCastListsSurviveQuotes() throws {
        let tricky = ["昆汀·塔伦蒂诺", "他说\"嗨\"", "反斜杠 \\ 号"]
        XCTAssertEqual(LibraryStore.decode(LibraryStore.encode(tricky)), tricky)
    }
}
