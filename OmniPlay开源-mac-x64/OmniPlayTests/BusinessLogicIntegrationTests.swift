//
//  BusinessLogicIntegrationTests.swift
//  OmniPlayTests
//
//  Created by Codex on 2026/4/6.
//

import Foundation
import GRDB
import XCTest
import Testing
@testable import OmniPlay

@MainActor
@Suite("Business Logic Integration", .serialized)
struct BusinessLogicIntegrationTests {

    @Test("Media source protocol normalization and validation")
    func mediaSourceProtocolNormalizationAndValidation() {
        #expect(MediaSourceProtocol.local.normalizedBaseURL("/Volumes/Media///") == "/Volumes/Media")
        #expect(MediaSourceProtocol.local.isValidBaseURL("/Volumes/Media"))

        #expect(MediaSourceProtocol.webdav.normalizedBaseURL("https://nas.local:5006/dav/Media/") == "https://nas.local:5006/dav/Media")
        #expect(MediaSourceProtocol.webdav.isValidBaseURL("https://nas.local:5006/dav/Media/"))
        #expect(!MediaSourceProtocol.webdav.isValidBaseURL("ftp://nas.local/share"))
        #expect(MediaSourceProtocol.webdav.webDAVPathValidationError("https://nas.local:5006") != nil)
        #expect(MediaSourceProtocol.webdav.webDAVPathValidationError("https://nas.local:5006/dav") != nil)
        #expect(MediaSourceProtocol.webdav.webDAVPathValidationError("https://nas.local:5006/电影") == nil)

        #expect(MediaSourceProtocol.direct.normalizedBaseURL("/anything") == "/")
        #expect(MediaSourceProtocol.direct.isValidBaseURL("/"))

        let webdavSource = MediaSource(
            id: 1,
            name: "NAS",
            protocolType: MediaSourceProtocol.webdav.rawValue,
            baseUrl: "http://user:pass@192.168.0.100:5005/%E7%94%B5%E5%BD%B1",
            authConfig: nil
        )
        #expect(webdavSource.displayBaseURL() == "http://192.168.0.100:5005/电影")
    }

    @Test("Media name parser extracts metadata from mixed Chinese/English filenames")
    func mediaNameParserExtractsMetadata() {
        let samplePath = "/媒体库/霸王别姬/Farewell.My.Concubine.1993.1080p.BluRay.x264.mkv"
        let metadata = MediaNameParser.extractSearchMetadata(from: samplePath)

        #expect(metadata.year == "1993")
        #expect(metadata.chineseTitle?.contains("霸王别姬") == true)
        #expect(metadata.foreignTitle?.localizedCaseInsensitiveContains("Farewell") == true)
        #expect(metadata.fullCleanTitle?.contains("1080p") == false)

        let bdmvSample = "Gone.with.the.Wind.1939.1080p.75th.Anniversary.Edition.Blu-ray.AVC.DTS-HD.MA.5.1-DIY@HDHome/Disc 2 - Gone with the Wind - Bonus/BDMV/STREAM/00027.m2ts"
        let bdmvMetadata = MediaNameParser.extractSearchMetadata(from: bdmvSample)
        #expect(bdmvMetadata.foreignTitle?.localizedCaseInsensitiveContains("Gone with the Wind") == true)
        #expect(!(bdmvMetadata.foreignTitle ?? "").localizedCaseInsensitiveContains("Bonus"))

        let plainDiscSample = "Gone.with.the.Wind.1939/Disc - Gone with the Wind/BDMV/STREAM/00027.m2ts"
        let plainDiscMetadata = MediaNameParser.extractSearchMetadata(from: plainDiscSample)
        #expect(plainDiscMetadata.foreignTitle?.localizedCaseInsensitiveContains("Gone with the Wind") == true)
        #expect(!(plainDiscMetadata.foreignTitle ?? "").localizedCaseInsensitiveContains("Disc"))

        let jagtenSample = "/电影/Jagten.AKA.The.Hunt.2012.1080p.USA.Repack.Blu-ray.AVC.DTS-HD.MA.5.1-bb@HDSky.iso"
        let jagtenMetadata = MediaNameParser.extractSearchMetadata(from: jagtenSample)
        #expect(jagtenMetadata.year == "2012")
        #expect(jagtenMetadata.foreignTitle == "Jagten AKA The Hunt")

        let lokiSample = "/电影/Loki.S01E04.2023.2160p.UHD.Blu-ray.Remux.HEVC.TrueHD.7.1.Atmos-HDS.mkv"
        let lokiMetadata = MediaNameParser.extractSearchMetadata(from: lokiSample)
        #expect(lokiMetadata.foreignTitle == "Loki")
        #expect(MediaNameParser.isLikelyTVEpisodePath(lokiSample))
        #expect(MediaNameParser.parsePreferredSeason(from: lokiSample) == 1)

        let casablancaAnniversary = "/电影/卡萨布兰卡（70周年纪念版）.iso"
        let casablancaAnniversaryMetadata = MediaNameParser.extractSearchMetadata(from: casablancaAnniversary)
        #expect(casablancaAnniversaryMetadata.chineseTitle == "卡萨布兰卡")

        let casablancaBonus = "/电影/卡萨布兰卡（花絮）.iso"
        let casablancaBonusMetadata = MediaNameParser.extractSearchMetadata(from: casablancaBonus)
        #expect(casablancaBonusMetadata.chineseTitle == "卡萨布兰卡")

        let hometownEpisode = "/纪录片/[他乡的童年].Ta.Xiang.De.Tong.Nian.2019.S01.Complete.WEB-DL.4K.HQ.HEVC.10bit.AAC-CMCTV/[他乡的童年].Ta.Xiang.De.Tong.Nian.2019.S01E01.WEB-DL.4K.HQ.HEVC.10bit.AAC-CMCTV.mp4"
        #expect(MediaNameParser.extractParentFolderChineseTitle(from: hometownEpisode) == "他乡的童年")

        let aerialChinaSample = "/纪录片/CCTV4K.Aerial.China.S01E06.2020.UHDTV.HEVC.HLG.DD5.1-CMCTV.ts"
        let aerialChinaMetadata = MediaNameParser.extractSearchMetadata(from: aerialChinaSample)
        #expect(aerialChinaMetadata.foreignTitle == "Aerial China")

        let seoulSpringSample = "/电影/12.12.The.Day.2023.HKG.Blu-ray.1080p.AVC.TrueHD.5.1-Breeze@Sunny/BDMV/STREAM/00003.m2ts"
        let seoulSpringMetadata = MediaNameParser.extractSearchMetadata(from: seoulSpringSample)
        #expect(seoulSpringMetadata.foreignTitle?.localizedCaseInsensitiveContains("The Day") == true)
        #expect(seoulSpringMetadata.year == "2023")

        let yuruCampSample = "/动画/映画 ゆるキャン△ 2022 2160P ULTRA-HD Blu-ray HEVC Atmos 7.1-SweetDreamDay/BDMV/STREAM/00004.m2ts"
        let yuruCampMetadata = MediaNameParser.extractSearchMetadata(from: yuruCampSample)
        #expect(yuruCampMetadata.chineseTitle != "映画")
        #expect(!(yuruCampMetadata.fullCleanTitle ?? "").contains("映画"))

        let fateSample = "/动画剧场版/Fate / stay night [Heaven's Feel] II.lost butterfly 2019 1080P Blu-ray AVC DTS-HD MA 5.1-SweetDreamDay/BDMV/STREAM/00000.m2ts"
        let fateMetadata = MediaNameParser.extractSearchMetadata(from: fateSample)
        #expect(!(fateMetadata.chineseTitle ?? "").contains("剧场版"))
        #expect(!(fateMetadata.fullCleanTitle ?? "").contains("剧场版"))
        #expect(!(fateMetadata.fullCleanTitle ?? "").contains("劇場版"))
        #expect(fateMetadata.year == "2019")

        let killBillDiscSample = "/电影/杀死比尔.Kill.Bill.Vol.1-2.2003-2004.Blu-ray.1080p.AVC.LPCM5.1-CMCT/VOL_2/BDMV/STREAM/00001.m2ts"
        let killBillDiscMetadata = MediaNameParser.extractSearchMetadata(from: killBillDiscSample)
        #expect(killBillDiscMetadata.foreignTitle?.localizedCaseInsensitiveContains("Kill Bill") == true)
        #expect(!(killBillDiscMetadata.foreignTitle ?? "").localizedCaseInsensitiveContains("VOL 2"))

        let xxxHolicSample = "/动漫/四月一日灵异事件簿·笼：徒梦.xxxHolic.Rô.Adayume.2011.1080p.Blu-ray.x264.FLAC2.0-LuckAni/四月一日灵异事件簿·笼：徒梦.xxxHolic.Rô.Adayume.2011.1080p.Blu-ray.x264.FLAC2.0-LuckAni.mkv"
        let xxxHolicMetadata = MediaNameParser.extractSearchMetadata(from: xxxHolicSample)
        #expect(xxxHolicMetadata.foreignTitle?.localizedCaseInsensitiveContains("xxxHolic") == true)

        let goneWindFeatures = "Gone.with.the.Wind.1939.1080p.75th.Anniversary.Edition.Blu-ray.AVC.DTS-HDMA5.1-DIY@HDHome/Disc 3 - Gone with the Wind - 75th Anniversary Edition Special Features/BDMV/STREAM/00048.m2ts"
        let goneWindFeaturesMetadata = MediaNameParser.extractSearchMetadata(from: goneWindFeatures)
        #expect(goneWindFeaturesMetadata.foreignTitle?.localizedCaseInsensitiveContains("Gone with the Wind") == true)
        #expect(!(goneWindFeaturesMetadata.foreignTitle ?? "").localizedCaseInsensitiveContains("Special Features"))

        let fateBdromSample = "/动画/剧场版「Fate / stay night [Heaven's Feel]」III.spring song 2020 1080P Blu-ray AVC DTS-HD MA 5.1-SweetDreamDay/BDROM/BDMV/STREAM/00004.m2ts"
        let fateBdromMetadata = MediaNameParser.extractSearchMetadata(from: fateBdromSample)
        #expect(fateBdromMetadata.foreignTitle?.localizedCaseInsensitiveContains("stay night") == true)
        #expect(!(fateBdromMetadata.fullCleanTitle ?? "").localizedCaseInsensitiveContains("BDROM"))

        let y1923Sample = "1923.S01.2022.1080p.BluRay.Remux.AVC.TrueHD.5.1-ADE/1923.S01E08.2022.1080p.BluRay.Remux.AVC.TrueHD.5.1-ADE.mkv"
        let y1923Metadata = MediaNameParser.extractSearchMetadata(from: y1923Sample)
        #expect((y1923Metadata.fullCleanTitle ?? "").hasPrefix("1923"))
        #expect(y1923Metadata.year == "2022")

        let y1917Sample = "1917 逆战救兵 2019 UHD Blu-ray 2160p HEVC TrueHD Atmos 7.1-Pete@HDSky/1917 4K Ultra HD.iso"
        let y1917Metadata = MediaNameParser.extractSearchMetadata(from: y1917Sample)
        #expect(y1917Metadata.chineseTitle == "逆战救兵")
        #expect(y1917Metadata.year == "2019")

    }

    @Test("Episode parsing and season detection should match common release names")
    func mediaNameParserEpisodeInfo() {
        let parsed = MediaNameParser.parseEpisodeInfo(from: "The.Show.S02E11.2160p.mkv", fallbackIndex: 0)
        #expect(parsed.isTVShow)
        #expect(parsed.season == 2)
        #expect(parsed.episode == 11)
        #expect(parsed.detectedSubtitle == nil)

        let partOne = MediaNameParser.parseEpisodeInfo(from: "Variety.Show.S01E01.part1.mkv", fallbackIndex: 0)
        #expect(partOne.isTVShow)
        #expect(partOne.season == 1)
        #expect(partOne.episode == 1)
        #expect(partOne.detectedSubtitle == "Part 1")
        #expect(partOne.displayName == "第 1 集 · Part 1")

        let zeroSeasonPart = MediaNameParser.parseEpisodeInfo(from: "Variety.Show.S0E01.part2.mkv", fallbackIndex: 0)
        #expect(zeroSeasonPart.season == 1)
        #expect(zeroSeasonPart.episode == 1)
        #expect(zeroSeasonPart.detectedSubtitle == "Part 2")

        let subtitleEpisode = MediaNameParser.parseEpisodeInfo(from: "Variety.Show.S01E01.开场嘉宾.2160p.WEB-DL.mkv", fallbackIndex: 0)
        #expect(subtitleEpisode.season == 1)
        #expect(subtitleEpisode.episode == 1)
        #expect(subtitleEpisode.detectedSubtitle == "开场嘉宾")

        let subtitleMetadata = MediaNameParser.extractSearchMetadata(from: "/TV/Variety.Show/Variety.Show.S01E01.开场嘉宾.2160p.WEB-DL.mkv")
        #expect(subtitleMetadata.foreignTitle == "Variety Show")

        let seasonFromPath = MediaNameParser.parsePreferredSeason(from: "/TV/The Show/Season 03/episode.mkv")
        #expect(seasonFromPath == 3)
        #expect(MediaNameParser.isLikelyTVEpisodePath("/TV/The.Show.S02E11.2160p.mkv"))

        let gintamaPath = "Gintama.S01-11.2006.1080p.Hami.WEB-DL.H264.AAC-HHWEB/Season 11/Gintama.S11E01.2018.1080p.Hami.WEB-DL.H264.AAC-HHWEB.mkv"
        let resolvedSeason = MediaNameParser.resolvePreferredSeason(
            from: gintamaPath,
            fileName: "Gintama.S11E01.2018.1080p.Hami.WEB-DL.H264.AAC-HHWEB.mkv"
        )
        #expect(resolvedSeason == 11)
    }

    @Test("Legacy WebDAV auth config decoder supports JSON and colon formats")
    func webDAVLegacyCredentialDecode() {
        let store = WebDAVCredentialStore.shared

        let json = #"{"username":"alice","password":"secret"}"#
        let jsonCredential = store.decodeLegacyCredential(from: json)
        #expect(jsonCredential?.username == "alice")
        #expect(jsonCredential?.password == "secret")

        let colonCredential = store.decodeLegacyCredential(from: "bob:pass123")
        #expect(colonCredential?.username == "bob")
        #expect(colonCredential?.password == "pass123")
    }

    @Test("MediaLibraryManager DB integration: fetch filters direct, keeps mounted sources, rematch cleans fake movie")
    func mediaLibraryManagerDatabaseIntegration() async throws {
        let dbURL = makeTempDBURL()
        defer { cleanupDBFiles(at: dbURL) }

        let dbQueue = try DatabaseQueue(path: dbURL.path)
        do {
            try await prepareTestSchema(on: dbQueue)
        } catch {
            Issue.record("prepareTestSchema failed: \(error.localizedDescription)")
            return
        }

        do {
            try await dbQueue.write { db in
                try db.execute(
                    sql: "INSERT INTO mediaSource (id, name, protocolType, baseUrl) VALUES (?, ?, ?, ?)",
                    arguments: [1, "本地源", "local", "/tmp/library"]
                )
                try db.execute(
                    sql: "INSERT INTO mediaSource (id, name, protocolType, baseUrl, isEnabled, disabledAt) VALUES (?, ?, ?, ?, ?, ?)",
                    arguments: [2, "已关闭源", "local", "/tmp/disabled-library", false, Date().timeIntervalSince1970]
                )
                try db.execute(sql: "INSERT INTO movie (id, title, isLocked) VALUES (?, ?, 0)", arguments: [1001, "DirectOnly"])
                try db.execute(sql: "INSERT INTO movie (id, title, isLocked) VALUES (?, ?, 0)", arguments: [1002, "ScannedMovie"])
                try db.execute(sql: "INSERT INTO movie (id, title, isLocked) VALUES (?, ?, 0)", arguments: [1003, "DisabledSourceMovie"])
                try db.execute(sql: "INSERT INTO movie (id, title, isLocked) VALUES (?, ?, 0)", arguments: [-100, "FakeMovie"])
                try db.execute(sql: "INSERT INTO movie (id, title, isLocked) VALUES (?, ?, 0)", arguments: [2001, "RealMovie"])
                try db.execute(
                    sql: """
                    INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, playProgress, duration)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    arguments: ["vf-direct", 1, "direct.mov", "direct.mov", "direct", 1001, 0.0, 0.0]
                )
                try db.execute(
                    sql: """
                    INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, playProgress, duration)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    arguments: ["vf-movie", 1, "movie.mkv", "movie.mkv", "movie", 1002, 0.0, 0.0]
                )
                try db.execute(
                    sql: """
                    INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, playProgress, duration)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    arguments: ["vf-disabled", 2, "disabled.mkv", "disabled.mkv", "movie", 1003, 0.0, 0.0]
                )
                try db.execute(
                    sql: """
                    INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    arguments: ["vf-unmatched", 1, "unknown.mkv", "unknown.mkv", "unmatched", -100, nil, 0.0, 0.0]
                )
            }
        } catch {
            Issue.record("seed data write failed: \(error.localizedDescription)")
            return
        }

        let manager = MediaLibraryManager(dbQueue: dbQueue)
        let movies: [Movie]
        do {
            movies = try manager.fetchAllMovies()
        } catch {
            Issue.record("fetchAllMovies failed: \(error.localizedDescription)")
            return
        }
        let ids = Set(movies.compactMap(\.id))
        #expect(ids.contains(1002))
        #expect(!ids.contains(1001))
        #expect(ids.contains(1003))

        do {
            try manager.updateVideoFileMatch(fileId: "vf-unmatched", newTMDBMovieId: 2001)
        } catch {
            Issue.record("updateVideoFileMatch failed: \(error.localizedDescription)")
            return
        }

        let fileAfter: VideoFile?
        do {
            fileAfter = try await dbQueue.read { db in
                try VideoFile.fetchOne(db, key: "vf-unmatched")
            }
        } catch {
            Issue.record("load updated file failed: \(error.localizedDescription)")
            return
        }
        #expect(fileAfter?.mediaType == "movie")
        #expect(fileAfter?.movieId == 2001)
        #expect(fileAfter?.episodeId == nil)

        let fakeMovie: Movie?
        do {
            fakeMovie = try await dbQueue.read { db in
                try Movie.fetchOne(db, key: -100)
            }
        } catch {
            Issue.record("load fake movie failed: \(error.localizedDescription)")
            return
        }
        #expect(fakeMovie == nil)
    }

    private func prepareTestSchema(on dbQueue: DatabaseQueue) async throws {
        try await dbQueue.write { db in
            try db.create(table: "mediaSource") { t in
                t.autoIncrementedPrimaryKey("id")
                t.column("name", .text).notNull()
                t.column("protocolType", .text).notNull()
                t.column("baseUrl", .text).notNull()
                t.column("authConfig", .text)
                t.column("isEnabled", .boolean).notNull().defaults(to: true)
                t.column("disabledAt", .double)
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
                t.column("duration", .double).notNull().defaults(to: 0.0)
                t.column("customSubtitle", .text)
            }
        }
    }

    private func makeTempDBURL() -> URL {
        URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("omniplay-test-\(UUID().uuidString).sqlite")
    }

    private func cleanupDBFiles(at dbURL: URL) {
        let fm = FileManager.default
        let path = dbURL.path
        try? fm.removeItem(atPath: path)
        try? fm.removeItem(atPath: "\(path)-shm")
        try? fm.removeItem(atPath: "\(path)-wal")
    }
}
