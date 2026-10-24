//
//  NeodbSyncTests.swift
//  OmniPlayTests
//
//  NeoDB 同步的纯逻辑与本地存储测试：不联网，只用临时库。
//

import Foundation
import GRDB
import XCTest
import Testing
@testable import OmniPlay

@Suite("NeoDB sync logic", .serialized)
struct NeodbSyncLogicTests {

    @Test("评分在五星与 1-10 之间互转")
    func ratingConversion() {
        #expect(NeodbRating.grade(fromStars: 4.5) == 9)
        #expect(NeodbRating.grade(fromStars: 3) == 6)
        #expect(NeodbRating.grade(fromStars: 0) == 0)
        #expect(NeodbRating.grade(fromStars: 99) == 10)
        #expect(NeodbRating.stars(fromGrade: 9) == 4.5)
        #expect(NeodbRating.stars(fromGrade: 0) == 0)
        #expect(NeodbRating.stars(fromGrade: nil) == 0)
    }

    @Test("播放进度推导书架标记")
    func derivedShelfFromProgress() {
        #expect(MediaMarkStore.deriveShelf(fileCount: 1, watched: 1, progressSum: 1).shelfType == .complete)
        #expect(MediaMarkStore.deriveShelf(fileCount: 2, watched: 1, progressSum: 1.4).shelfType == .progress)
        #expect(MediaMarkStore.deriveShelf(fileCount: 2, watched: 0, progressSum: 0).shelfType == .none)
        #expect(MediaMarkStore.deriveShelf(fileCount: 0, watched: 0, progressSum: 0).shelfType == .none)
        // 全部播完时进度至少算 100%
        #expect(MediaMarkStore.deriveShelf(fileCount: 1, watched: 1, progressSum: 1).progressPercent == 100)
    }

    @Test("从 NeoDB 条目的外链里解析豆瓣与 TMDB 编号")
    func externalRefParsing() {
        let ref = NeodbExternalRef.parse(externalURLs: [
            "https://movie.douban.com/subject/1292063/",
            "https://www.themoviedb.org/tv/1396/"
        ])
        #expect(ref.doubanSubjectID == "1292063")
        #expect(ref.tmdbID == 1396)
        #expect(ref.kind == .tv)

        let empty = NeodbExternalRef.parse(externalURLs: ["https://www.imdb.com/title/tt0068646/"])
        #expect(empty.doubanSubjectID == nil)
        #expect(empty.tmdbID == nil)

        let movie = NeodbExternalRef(doubanSubjectID: "25", tmdbID: 155, kind: .movie)
        // 豆瓣在前，再试本类型，最后试另一种类型
        #expect(movie.lookupURLs == [
            "https://movie.douban.com/subject/25/",
            "https://www.themoviedb.org/movie/155",
            "https://www.themoviedb.org/tv/155"
        ])
        #expect(movie.matches(NeodbExternalRef(doubanSubjectID: nil, tmdbID: 155, kind: .movie)))
        #expect(!movie.matches(NeodbExternalRef(doubanSubjectID: nil, tmdbID: 156, kind: .movie)))
    }

    @Test("标题校验：译名不同也能认出来")
    func titleMatch() {
        #expect(NeodbTitleMatch.likelyMatch(local: "肖申克的救赎", remote: "肖申克的救赎 1994"))
        // 中西文标题之间没有任何共同词时给不出证据，算对不上；同步层会退回首选那条链接。
        #expect(!NeodbTitleMatch.likelyMatch(local: "Inception (2010)", remote: "盗梦空间"))
        #expect(NeodbTitleMatch.likelyMatch(local: "The Matrix", remote: "The Matrix"))
        #expect(!NeodbTitleMatch.likelyMatch(local: "Forrest Gump", remote: "The Matrix"))
        // 缺信息时放行，别把用户挡在门外
        #expect(NeodbTitleMatch.likelyMatch(local: nil, remote: "Anything"))
        #expect(NeodbTitleMatch.likelyMatch(local: "Anything", remote: ""))
    }

    @Test("写标记请求体的字段名与 NeoDB 一致")
    func markPayloadEncoding() throws {
        var mark = MediaMark(kind: .movie, localId: 129)
        mark.shelfType = .complete
        mark.ratingGrade = 9
        mark.comment = "  很好看  "
        mark.updatedAt = 1_700_000_000

        let data = try NeodbMarkPayload(mark: mark, visibility: .onlyMe, postToFediverse: true).encodedBody()
        let json = try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
        #expect(json["shelf_type"] as? String == "complete")
        #expect(json["visibility"] as? Int == 2)
        #expect(json["rating_grade"] as? Int == 9)
        #expect(json["comment_text"] as? String == "很好看")
        #expect(json["post_to_fediverse"] as? Bool == true)
        #expect(json["tags"] as? [String] == [])
        #expect(json["created_time"] as? String != nil)
    }

    @Test("进度请求体按百分比写")
    func progressPayloadEncoding() throws {
        let data = try JSONEncoder().encode(NeodbProgressPayload(percent: 250))
        let json = try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
        #expect(json["type"] as? String == "percentage")
        #expect(json["value"] as? String == "100")
    }

    @Test("书架列表：数组与 data 包装两种响应都能解析")
    func shelfResponseParsing() throws {
        let item = """
        "item":{"uuid":"4upSY7ttUqa5kjvcnXflWt","category":"movie","display_title":"阿甘正传",\
        "external_resources":[{"url":"https://movie.douban.com/subject/1292052/"},{"url":"https://www.themoviedb.org/movie/13"}]}
        """
        let mark = """
        {"shelf_type":"complete","rating_grade":8,"comment_text":"经典","created_time":"2026-10-01T10:00:00Z",\(item)}
        """
        let bare = try NeodbShelfResponse.marks(from: Data("[\(mark)]".utf8))
        let wrapped = try NeodbShelfResponse.marks(from: Data("{\"data\":[\(mark)]}".utf8))

        for marks in [bare, wrapped] {
            let first = try #require(marks.first)
            #expect(marks.count == 1)
            #expect(first.shelf == .complete)
            #expect(first.ratingGrade == 8)
            #expect(first.commentTrimmed == "经典")
            #expect(first.itemUUID == "4upSY7ttUqa5kjvcnXflWt")
            #expect(first.itemParentUUID == nil)
            #expect(first.externalRef.doubanSubjectID == "1292052")
            #expect(first.createdAt > 0)
        }
    }

    @Test("季集条目的标记带 parent_uuid")
    func shelfResponseParentUUID() throws {
        let json = """
        [{"shelf_type":"progress","item":{"uuid":"ep","category":"tv","parent_uuid":"season1"}}]
        """
        let mark = try #require(try NeodbShelfResponse.marks(from: Data(json.utf8)).first)
        #expect(mark.itemParentUUID == "season1")
        #expect(mark.shelf == .progress)
    }

    @Test("目录条目解析出站外编号")
    func catalogItemParsing() throws {
        let json = """
        {"uuid":"abc","category":"movie","title":"霸王别姬","external_resources":[\
        {"url":"https://movie.douban.com/subject/1291546/"},{"url":"https://www.themoviedb.org/movie/6446"}]}
        """
        let item = try NeodbShelfResponse.catalogItem(from: Data(json.utf8))
        #expect(item.uuid == "abc")
        #expect(item.title == "霸王别姬")
        #expect(item.externalRef.doubanSubjectID == "1291546")
        #expect(item.externalRef.tmdbID == 6446)
    }

    @Test("实例地址归一化")
    func instanceNormalization() {
        #expect(NeodbInstance(raw: "neodb.social")?.display == "https://neodb.social")
        #expect(NeodbInstance(raw: "https://neodb.social/")?.display == "https://neodb.social")
        #expect(NeodbInstance(raw: "https://db.example.com/api")?.display == "https://db.example.com")
        #expect(NeodbInstance(raw: "  https://db.example.com/reviews/  ")?.display == "https://db.example.com/reviews")
        #expect(NeodbInstance(raw: "ftp://db.example.com") == nil)
        #expect(NeodbInstance(raw: "") == nil)

        let instance = NeodbInstance(raw: "https://neodb.social")
        #expect(instance?.api("me/shelf/complete").absoluteString == "https://neodb.social/api/me/shelf/complete")
        #expect(instance?.root("v0/password_login").absoluteString == "https://neodb.social/v0/password_login")
    }

    @Test("账户标签")
    func accountLabel() throws {
        let json = """
        {"username":"alice","display_name":"爱丽丝","url":"/users/alice/"}
        """
        let account = try NeodbShelfResponse.account(from: Data(json.utf8))
        #expect(account.label.contains("alice"))
        #expect(account.label.contains("爱丽丝"))
    }
}

@MainActor
@Suite("NeoDB mark store", .serialized)
struct MediaMarkStoreTests {

    /// 临时库里只建同步逻辑真正碰到的几张表，且不碰 `AppDatabase.shared`：
    /// WebDAV 那套测试也会重指向那个单例，并行跑的时候会互相踩。
    private func makeDatabase() throws -> DatabaseQueue {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("OmniPlayNeodbTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let dbQueue = try DatabaseQueue(path: folder.appendingPathComponent("marks.sqlite").path)
        try dbQueue.write { db in
            try db.execute(sql: """
                CREATE TABLE mediaSource(id INTEGER PRIMARY KEY, name TEXT NOT NULL,
                                         protocolType TEXT NOT NULL, baseUrl TEXT NOT NULL)
                """)
            try db.execute(sql: "CREATE TABLE movie(id INTEGER PRIMARY KEY, title TEXT NOT NULL, isLocked BOOLEAN NOT NULL DEFAULT 0)")
            try db.execute(sql: """
                CREATE TABLE videoFile(id TEXT PRIMARY KEY, sourceId INTEGER NOT NULL REFERENCES mediaSource(id),
                                       relativePath TEXT NOT NULL, fileName TEXT NOT NULL, mediaType TEXT NOT NULL,
                                       movieId INTEGER REFERENCES movie(id), playProgress DOUBLE NOT NULL DEFAULT 0,
                                       duration DOUBLE NOT NULL DEFAULT 0, fileSize INTEGER NOT NULL DEFAULT 0)
                """)
            try db.execute(sql: """
                CREATE TABLE doubanMetadata(movieId INTEGER PRIMARY KEY, subjectId TEXT NOT NULL)
                """)
            try MediaMarkStore.createTable(db)
        }
        return dbQueue
    }

    /// 一部影片 + 若干播放记录。
    private func seed(dbQueue: DatabaseQueue, movieId: Int64 = 1399, title: String = "测试影片",
                      files: [(fileName: String, relativePath: String, progress: Double, duration: Double)]) throws {
        try dbQueue.write { db in
            try db.execute(sql: "INSERT INTO mediaSource(id, name, protocolType, baseUrl) VALUES(1, '本地', 'local', '/Media')")
            try db.execute(sql: "INSERT INTO movie(id, title, isLocked) VALUES(?, ?, 0)", arguments: [movieId, title])
            for (index, file) in files.enumerated() {
                try db.execute(
                    sql: """
                        INSERT INTO videoFile(id, sourceId, relativePath, fileName, mediaType, movieId,
                                              playProgress, duration, fileSize)
                        VALUES(?, 1, ?, ?, 'movie', ?, ?, ?, 1000)
                        """,
                    arguments: ["file\(index)", file.relativePath, file.fileName, movieId, file.progress, file.duration])
            }
        }
    }

    @Test("播完的影片被收成待同步记录，同步后不再算脏")
    func harvestWatchHistory() throws {
        let dbQueue = try makeDatabase()
        try seed(dbQueue: dbQueue, files: [
            (fileName: "Fury.2014.1080p.mkv", relativePath: "Fury/Fury.2014.1080p.mkv", progress: 5400, duration: 5400)
        ])
        try dbQueue.write { db in
            let written = try MediaMarkStore.harvestWatchHistory(db: db, now: 1_700_000_000)
            #expect(written == 1)
            let pending = try MediaMarkStore.pendingMarks(db: db)
            #expect(pending.count == 1)
            #expect(pending.first?.shelfType == .complete)
            #expect(pending.first?.key == MediaMark.key(localId: 1399))
            #expect(pending.first?.isManual == false)

            try MediaMarkStore.didSync(db: db, key: MediaMark.key(localId: 1399), at: 1_700_000_100)
            let pendingAfterSync = try MediaMarkStore.pendingCount(db: db)
            #expect(pendingAfterSync == 0)
            // 再收一次：已经同步过且没变化，不该重复写
            let again = try MediaMarkStore.harvestWatchHistory(db: db, now: 1_700_000_200)
            #expect(again == 0)
        }
    }

    @Test("手工标记不会被播放历史覆盖")
    func manualMarkSurvivesHarvest() throws {
        let dbQueue = try makeDatabase()
        try seed(dbQueue: dbQueue, files: [(fileName: "a.mkv", relativePath: "a.mkv", progress: 5400, duration: 5400)])
        try dbQueue.write { db in
            try MediaMarkStore.save(db: db, localId: 1399, shelfType: .dropped, ratingGrade: 3,
                                    comment: "看不下去", at: 1_700_000_000)
            let reharvest = try MediaMarkStore.harvestWatchHistory(db: db, now: 1_700_000_300)
            #expect(reharvest == 0)
            let record = try #require(try MediaMarkStore.mark(db: db, localId: 1399))
            #expect(record.shelfType == .dropped)
            #expect(record.ratingGrade == 3)
            #expect(record.comment == "看不下去")
            #expect(record.isDirty)
        }
    }

    @Test("未看完的剧集按在看入库，电影和剧集都在 movie 表里")
    func inProgressMark() throws {
        let dbQueue = try makeDatabase()
        try seed(dbQueue: dbQueue, files: [
            (fileName: "S01E01.mkv", relativePath: "Show/Season 1/S01E01.mkv", progress: 1200, duration: 1200),
            (fileName: "S01E02.mkv", relativePath: "Show/Season 1/S01E02.mkv", progress: 300, duration: 1200)
        ])
        try dbQueue.write { db in
            let derived = MediaMarkStore.derivedState(db: db, localId: 1399)
            #expect(derived.shelfType == .progress)
            #expect(MediaMarkStore.derivedKind(db: db, localId: 1399) == .tv)
            let written = try MediaMarkStore.harvestWatchHistory(db: db)
            #expect(written == 1)
        }
    }

    @Test("没存过的条目不算「有改动」，脏标记与同步状态一致")
    func pendingChangeOnlyForStoredRows() throws {
        let dbQueue = try makeDatabase()
        try seed(dbQueue: dbQueue, files: [(fileName: "a.mkv", relativePath: "a.mkv", progress: 5400, duration: 5400)])
        try dbQueue.write { db in
            // 从没落过库：弹窗退回播放进度推导的标记，此时不能提示「有改动还没同步」
            let fresh = try MediaMarkStore.editorState(db: db, localId: 1399)
            #expect(fresh.shelfType == .complete)
            #expect(fresh.isDirty)
            #expect(!fresh.hasPendingLocalChange)

            // 手工存一次：改动落库，此时才算真没同步
            try MediaMarkStore.save(db: db, localId: 1399, shelfType: .complete, ratingGrade: 6,
                                    comment: "", at: 1_700_000_000)
            let edited = try #require(try MediaMarkStore.mark(db: db, localId: 1399))
            #expect(edited.hasPendingLocalChange)
            #expect(try MediaMarkStore.pendingCount(db: db) == 1)

            // 推成功后脏标记要消失，界面上才会显示上次同步时间
            try MediaMarkStore.didSync(db: db, key: MediaMark.key(localId: 1399), at: 1_700_000_002)
            let synced = try MediaMarkStore.editorState(db: db, localId: 1399)
            #expect(!synced.isDirty)
            #expect(!synced.hasPendingLocalChange)
            #expect(try MediaMarkStore.pendingCount(db: db) == 0)
        }
    }

    @Test("NeoDB 条目网址落库后不被后续编辑清掉")
    func remoteURLSurvivesEdit() throws {
        let dbQueue = try makeDatabase()
        try seed(dbQueue: dbQueue, files: [(fileName: "a.mkv", relativePath: "a.mkv", progress: 5400, duration: 5400)])
        try dbQueue.write { db in
            try MediaMarkStore.save(db: db, localId: 1399, shelfType: .complete, ratingGrade: 6,
                                    comment: "", at: 1_700_000_000)
            try MediaMarkStore.setRemoteURL(db: db, key: MediaMark.key(localId: 1399),
                                            url: "https://neodb.social/movie/6b0c")
            // 再改一次评分：URL 要留着，不然「打开 NeoDB 页面」会时灵时不灵
            try MediaMarkStore.save(db: db, localId: 1399, shelfType: .complete, ratingGrade: 8,
                                    comment: "改一下", at: 1_700_000_100)
            let record = try #require(try MediaMarkStore.mark(db: db, localId: 1399))
            #expect(record.remoteUrl == "https://neodb.social/movie/6b0c")
            #expect(record.ratingGrade == 8)

            // 导入路径同样带上 URL
            let json = """
            {"shelf_type":"watching","rating_grade":0,"comment_text":"",\
            "created_time":"2026-10-01T10:00:00Z",\
            "item":{"uuid":"tv77","category":"tv","external_resources":[]}}
            """
            let remote = try JSONDecoder().decode(NeodbRemoteMark.self, from: Data(json.utf8))
            try MediaMarkStore.applyRemote(db: db, kind: .tv, localId: 1399, remote: remote,
                                          at: 1_700_000_200, markLocalWatched: false)
            #expect(try MediaMarkStore.mark(db: db, localId: 1399)?.remoteUrl == "https://neodb.social/movie/6b0c")
        }
    }

    @Test("拼得出 NeoDB 条目网页地址")
    func neodbItemPageURL() throws {
        let instance = try #require(NeodbInstance(raw: NeodbInstance.defaultRaw))
        #expect(instance.itemPageURL(category: "movie", fallbackKind: .movie, uuid: "6b0c")?
            .absoluteString == "https://neodb.social/movie/6b0c")
        #expect(instance.itemPageURL(category: "tv", fallbackKind: .movie, uuid: "tv77")?
            .absoluteString == "https://neodb.social/tv/tv77")
        // 站外链解析不出类别时退回本地推断的类型
        #expect(instance.itemPageURL(category: nil, fallbackKind: .tv, uuid: "tv77")?
            .absoluteString == "https://neodb.social/tv/tv77")
        #expect(instance.itemPageURL(category: "movie", fallbackKind: .movie, uuid: "  ") == nil)
    }

    @Test("导入远端标记后不再算脏，并把本地进度顶到片尾")
    func applyRemote() throws {
        let dbQueue = try makeDatabase()
        try seed(dbQueue: dbQueue, files: [(fileName: "a.mkv", relativePath: "a.mkv", progress: 0, duration: 5400)])
        let json = """
        {"shelf_type":"complete","rating_grade":9,"comment_text":"神作",\
        "created_time":"2026-10-01T10:00:00Z",\
        "item":{"uuid":"abc","category":"movie","external_resources":[{"url":"https://www.themoviedb.org/movie/1399"}]}}
        """
        let remote = try JSONDecoder().decode(NeodbRemoteMark.self, from: Data(json.utf8))
        try dbQueue.write { db in
            try MediaMarkStore.applyRemote(db: db, kind: .movie, localId: 1399, remote: remote,
                                          at: 1_700_000_000, markLocalWatched: true)
            let record = try #require(try MediaMarkStore.mark(db: db, localId: 1399))
            #expect(record.shelfType == .complete)
            #expect(record.ratingGrade == 9)
            #expect(record.comment == "神作")
            #expect(!record.isDirty)
            let pending = try MediaMarkStore.pendingCount(db: db)
            #expect(pending == 0)
            let progress = try #require(try Double.fetchOne(db, sql: "SELECT playProgress FROM videoFile WHERE id = 'file0'"))
            #expect(progress == 5400)
        }
    }

    @Test("占位条目不进待同步队列")
    func placeholderItemsSkipped() throws {
        let dbQueue = try makeDatabase()
        try dbQueue.write { db in
            try db.execute(sql: "INSERT INTO mediaSource(id, name, protocolType, baseUrl) VALUES(1, '本地', 'local', '/Media')")
            try db.execute(sql: "INSERT INTO movie(id, title, isLocked) VALUES(-42, '未识别', 1)")
            try db.execute(
                sql: """
                    INSERT INTO videoFile(id, sourceId, relativePath, fileName, mediaType, movieId,
                                          playProgress, duration, fileSize)
                    VALUES('p1', 1, 'x.mkv', 'x.mkv', 'unmatched', -42, 500, 1000, 10)
                    """)
            let written = try MediaMarkStore.harvestWatchHistory(db: db)
            #expect(written == 0)
            try MediaMarkStore.save(db: db, localId: -42, shelfType: .complete, ratingGrade: 8, comment: "")
            let pending = try MediaMarkStore.pendingCount(db: db)
            #expect(pending == 0)
        }
    }

        @Test("读得出豆瓣绑定编号与本地片名")
    func boundDoubanSubjectAndTitle() throws {
        let dbQueue = try makeDatabase()
        try seed(dbQueue: dbQueue, files: [(fileName: "a.mkv", relativePath: "a.mkv", progress: 0, duration: 100)])
        try dbQueue.write { db in
            let before = MediaMarkStore.boundDoubanSubjectID(db: db, movieId: 1399)
            #expect(before == nil)
            try db.execute(sql: "INSERT INTO doubanMetadata(movieId, subjectId) VALUES(1399, '1292063')")
            let bound = MediaMarkStore.boundDoubanSubjectID(db: db, movieId: 1399)
            #expect(bound == "1292063")
            #expect(MediaMarkStore.title(db: db, localId: 1399) == "测试影片")
        }
    }

@Test("详情页状态：没有手工记录时退回播放进度")
    func editorStateFallsBackToProgress() throws {
        let dbQueue = try makeDatabase()
        try seed(dbQueue: dbQueue, files: [(fileName: "a.mkv", relativePath: "a.mkv", progress: 5400, duration: 5400)])
        try dbQueue.write { db in
            let state = try MediaMarkStore.editorState(db: db, localId: 1399)
            #expect(state.shelfType == .complete)
            #expect(state.ratingGrade == 0)
            #expect(state.comment.isEmpty)
        }
    }
}
