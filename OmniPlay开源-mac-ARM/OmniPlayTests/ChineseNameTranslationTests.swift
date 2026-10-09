//
//  ChineseNameTranslationTests.swift
//  OmniPlayTests
//
//  外文导演/主演名换成中文译名后的数据迁移测试（不依赖网络）。
//

import Foundation
import GRDB
import Testing
@testable import OmniPlay

@MainActor
@Suite("Chinese Name Translation", .serialized)
struct ChineseNameTranslationTests {

    @Test("Person rename to Chinese name migrates movies, folders and profiles")
    func personRenameMigratesLibraryRows() async throws {
        let dbQueue = try makeTestDatabase()

        let filmographyJSON = """
        [{"title":"Inception","rating":8.4,"year":2010,"date":"2010-07-16","tmdbId":27205,"mediaType":"movie"}]
        """
        try await dbQueue.write { db in
            let Nolan = CategoryFolder.folderID(kind: "director", name: "Christopher Nolan")
            try Movie(
                id: 10,
                title: "Inception",
                releaseDate: "2010-07-16",
                overview: nil,
                posterPath: nil,
                voteAverage: 8.4,
                isLocked: false,
                directors: Movie.encodePersonList(["Christopher Nolan"]),
                casts: Movie.encodePersonList(["Leonardo DiCaprio"])
            ).insert(db)
            try Movie(
                id: 11,
                title: "Interstellar",
                releaseDate: "2014-11-07",
                overview: nil,
                posterPath: nil,
                voteAverage: 8.7,
                isLocked: false,
                directors: Movie.encodePersonList(["Christopher Nolan"]),
                casts: nil
            ).insert(db)
            try CategoryFolder(
                id: Nolan,
                kind: "director",
                name: "Christopher Nolan",
                posterPath: "/custom-poster.jpg",
                updatedAt: 1
            ).insert(db)
            try PersonProfile(
                id: Nolan,
                name: "Christopher Nolan",
                kind: "director",
                tmdbPersonId: 61,
                photoPath: "/profile.jpg",
                filmographyJson: filmographyJSON,
                updatedAt: 1
            ).insert(db)
        }

        let renamed = await ChineseNameStore.applyPersonRename(
            kind: "director",
            oldName: "Christopher Nolan",
            newName: "克里斯托弗·诺兰",
            dbQueue: dbQueue
        )
        #expect(renamed)

        try await dbQueue.read { db in
            let inception = try #require(try Movie.fetchOne(db, key: 10))
            #expect(inception.directorList == ["克里斯托弗·诺兰"])
            #expect(inception.castList == ["Leonardo DiCaprio"])

            let interstellar = try #require(try Movie.fetchOne(db, key: 11))
            #expect(interstellar.directorList == ["克里斯托弗·诺兰"])

            let chineseID = CategoryFolder.folderID(kind: "director", name: "克里斯托弗·诺兰")
            let legacyID = CategoryFolder.folderID(kind: "director", name: "Christopher Nolan")
            let folder = try #require(try CategoryFolder.fetchOne(db, key: chineseID))
            #expect(folder.name == "克里斯托弗·诺兰")
            #expect(folder.posterPath == "/custom-poster.jpg")
            #expect(try CategoryFolder.fetchOne(db, key: legacyID) == nil)

            let profile = try #require(try PersonProfile.fetchOne(db, key: chineseID))
            #expect(profile.tmdbPersonId == 61)
            #expect(profile.photoPath == "/profile.jpg")
            #expect(profile.filmography.first?.tmdbId == 27205)
            #expect(try PersonProfile.fetchOne(db, key: legacyID) == nil)

            // 中文译名缓存表可用（迁移前不应有任何写入）
            #expect(try ChineseNameRecord.fetchCount(db) == 0)
        }

        // 库里已没有外文名引用时不再产生写入
        let again = await ChineseNameStore.applyPersonRename(
            kind: "director",
            oldName: "Christopher Nolan",
            newName: "克里斯托弗·诺兰",
            dbQueue: dbQueue
        )
        #expect(!again)
    }

    @Test("Manual folder name migrates data and survives the next scrape")
    func manualPersonNameIsRemembered() async throws {
        let dbQueue = try makeTestDatabase()
        try await dbQueue.write { db in
            try Movie(
                id: 20,
                title: "Oppenheimer",
                releaseDate: "2023-07-21",
                overview: nil,
                posterPath: nil,
                voteAverage: 8.1,
                isLocked: false,
                directors: Movie.encodePersonList(["Christopher Nolan"]),
                casts: nil
            ).insert(db)
            try PersonProfile(
                id: CategoryFolder.folderID(kind: "director", name: "Christopher Nolan"),
                name: "Christopher Nolan",
                kind: "director",
                tmdbPersonId: 61,
                photoPath: nil,
                filmographyJson: nil,
                updatedAt: 1
            ).insert(db)
        }

        let saved = await ChineseNameStore.setUserPersonName(
            kind: "director",
            currentName: "Christopher Nolan",
            newName: "诺兰",
            dbQueue: dbQueue
        )
        #expect(saved)

        try await dbQueue.read { db in
            let movie = try #require(try Movie.fetchOne(db, key: 20))
            #expect(movie.directorList == ["诺兰"])
            let profile = try #require(try PersonProfile.fetchOne(
                db,
                key: CategoryFolder.folderID(kind: "director", name: "诺兰")
            ))
            #expect(profile.tmdbPersonId == 61)
            // 手动名同时按人物 id 和刮削到的外文名记住
            let byPersonID = try #require(try ChineseNameRecord.fetchOne(db, key: "person#61"))
            #expect(byPersonID.value == "诺兰")
            let byScrapedName = try #require(
                try ChineseNameRecord.fetchOne(db, key: "alias#director#Christopher Nolan")
            )
            #expect(byScrapedName.value == "诺兰")
        }

        // 下一轮刮削 TMDB 仍返回外文名时，继续沿用手动名
        let credits = [TMDBPersonInfo(name: "Christopher Nolan", tmdbId: 61, profilePath: nil)]
        let localized = await ChineseNameStore.localizeCredits(directors: credits, casts: [], dbQueue: dbQueue)
        #expect(localized.directors.map { $0.name } == ["诺兰"])
    }

    @Test("Person rename merges into an existing Chinese profile and keeps its poster")
    func personRenameMergesIntoExistingProfile() async throws {
        let dbQueue = try makeTestDatabase()

        try await dbQueue.write { db in
            try CategoryFolder(
                id: CategoryFolder.folderID(kind: "cast", name: "Leonardo DiCaprio"),
                kind: "cast",
                name: "Leonardo DiCaprio",
                posterPath: "/legacy-poster.jpg",
                updatedAt: 1
            ).insert(db)
            try CategoryFolder(
                id: CategoryFolder.folderID(kind: "cast", name: "莱昂纳多·迪卡普里奥"),
                kind: "cast",
                name: "莱昂纳多·迪卡普里奥",
                posterPath: "/chosen-poster.jpg",
                updatedAt: 2
            ).insert(db)
        }

        let renamed = await ChineseNameStore.applyPersonRename(
            kind: "cast",
            oldName: "Leonardo DiCaprio",
            newName: "莱昂纳多·迪卡普里奥",
            dbQueue: dbQueue
        )
        #expect(renamed)

        try await dbQueue.read { db in
            let folder = try #require(try CategoryFolder.fetchOne(
                db,
                key: CategoryFolder.folderID(kind: "cast", name: "莱昂纳多·迪卡普里奥")
            ))
            // 用户已手选过封面时不被旧文件夹封面覆盖
            #expect(folder.posterPath == "/chosen-poster.jpg")
            #expect(try CategoryFolder.fetchOne(
                db,
                key: CategoryFolder.folderID(kind: "cast", name: "Leonardo DiCaprio")
            ) == nil)
        }
    }

    @Test("Person Chinese name prefers localized name, then translation, then alias")
    func chinesePersonNameSelection() throws {
        // 已有 zh-CN 本地化名称时直接使用，并统一转成简体
        let localized = try decode(
            """
            {"name":"克里斯托弗·諾蘭","also_known_as":["クリストファー・ノーラン"]}
            """,
            as: TMDBPersonDetailResponse.self
        )
        #expect(TMDBService.chinesePersonName(from: localized) == "克里斯托弗·诺兰")

        // 本地化名称仍是原文时，用中文译名
        let translated = try decode(
            """
            {"name":"Christopher Nolan","also_known_as":["クリストファー・ノーラン"],
             "translations":{"translations":[
               {"iso_639_1":"ja","iso_3166_1":"JP","data":{"name":"クリストファー・ノーラン"}},
               {"iso_639_1":"zh","iso_3166_1":"CN","data":{"name":"克里斯托弗·诺兰"}}]}}
            """,
            as: TMDBPersonDetailResponse.self
        )
        #expect(TMDBService.chinesePersonName(from: translated) == "克里斯托弗·诺兰")

        // 没有译名时退到中文别名
        let aliasOnly = try decode(
            """
            {"name":"Hans Zimmer","also_known_as":["漢斯·齊瑪默","Ханс Циммер"]}
            """,
            as: TMDBPersonDetailResponse.self
        )
        #expect(TMDBService.chinesePersonName(from: aliasOnly) == "汉斯·齐玛默")

        // 完全找不到中文条目时返回 nil，由调用方写负缓存
        let none = try decode(
            """
            {"name":"Hans Zimmer","also_known_as":["Ханс Циммер"]}
            """,
            as: TMDBPersonDetailResponse.self
        )
        #expect(TMDBService.chinesePersonName(from: none) == nil)
    }

    @Test("Alternative titles prefer mainland Chinese titles and simplify traditional")
    func chineseAlternativeTitleSelection() throws {
        let movie = try decode(
            """
            {"id":27205,"titles":[
              {"iso_3166_1":"US","title":"Inception","type":"alternative"},
              {"iso_3166_1":"TW","title":"全面启动","type":""},
              {"iso_3166_1":"CN","title":"盗梦空间","type":""}]}
            """,
            as: TMDBAlternativeTitlesResponse.self
        )
        #expect(TMDBService.chineseAlternativeTitle(from: movie) == "盗梦空间")

        // 剧集别名在 results 里，只有繁体时也转简体
        let tv = try decode(
            """
            {"id":1396,"results":[{"iso_3166_1":"HK","title":"鐵漢柔情","type":""}]}
            """,
            as: TMDBAlternativeTitlesResponse.self
        )
        #expect(TMDBService.chineseAlternativeTitle(from: tv) == "铁汉柔情")

        let foreignOnly = try decode(
            """
            {"id":1,"titles":[{"iso_3166_1":"US","title":"Some Movie","type":"working title"}]}
            """,
            as: TMDBAlternativeTitlesResponse.self
        )
        #expect(TMDBService.chineseAlternativeTitle(from: foreignOnly) == nil)
    }

    // 独立临时库，避免与其他测试套件抢占 AppDatabase.shared
    private func decode<T: Decodable>(_ json: String, as type: T.Type) throws -> T {
        try JSONDecoder().decode(type, from: Data(json.utf8))
    }

    private func makeTestDatabase() throws -> DatabaseQueue {
        let url = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("omniplay-chinesename-test-\(UUID().uuidString).sqlite")
        let dbQueue = try DatabaseQueue(path: url.path)
        try dbQueue.write { db in
            try db.create(table: "movie") { t in
                t.primaryKey("id", .integer)
                t.column("title", .text).notNull()
                t.column("releaseDate", .text)
                t.column("overview", .text)
                t.column("posterPath", .text)
                t.column("voteAverage", .double)
                t.column("isLocked", .boolean).notNull().defaults(to: false)
                t.column("directors", .text)
                t.column("casts", .text)
            }
            try db.create(table: "categoryFolder") { t in
                t.primaryKey("id", .text)
                t.column("kind", .text).notNull()
                t.column("name", .text).notNull()
                t.column("posterPath", .text)
                t.column("updatedAt", .double)
            }
            try db.create(table: "personProfile") { t in
                t.primaryKey("id", .text)
                t.column("name", .text).notNull()
                t.column("kind", .text).notNull()
                t.column("tmdbPersonId", .integer)
                t.column("photoPath", .text)
                t.column("filmographyJson", .text)
                t.column("updatedAt", .double)
            }
            try db.create(table: "chineseNameCache") { t in
                t.primaryKey("key", .text)
                t.column("value", .text)
                t.column("updatedAt", .double).notNull()
            }
        }
        return dbQueue
    }
}
