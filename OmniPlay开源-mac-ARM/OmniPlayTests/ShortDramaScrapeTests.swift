//
//  ShortDramaScrapeTests.swift
//  OmniPlayTests
//
//  短剧识别、分集解析与豆瓣搜索解析。全部是纯逻辑，不打网络。
//

import Foundation
import GRDB
import Testing
@testable import OmniPlay

@MainActor
@Suite("Short Drama Scrape", .serialized)
struct ShortDramaScrapeTests {

    private static let shortDramaSize: Int64 = 40 * 1_048_576      // 单集几十 MB
    private static let longDramaSize: Int64 = 780 * 1_048_576      // 长剧一集几百 MB起
    /// 真实样本目录：/Volumes/1T/电影/持械入宋 S07（100集） - SKIT
    private static let s07Directory = "电影/持械入宋 S07（100集） - SKIT"

    // MARK: - 目录识别

    @Test("百集纯数字目录认成短剧")
    func numericDirectoryIsShortDrama() {
        let names = (1...100).map { String(format: "%03d.mp4", $0) }
        let verdict = ShortDramaClassifier.evaluate(
            directoryPath: "剧集/我在八零年代当后妈",
            fileNames: names,
            fileSizes: Array(repeating: Self.shortDramaSize, count: 100)
        )
        #expect(verdict.isShortDrama)
        #expect(verdict.episodeNumbers.count == 100)
    }

    @Test("中文集号命名的短剧目录也认得出")
    func chineseEpisodeNamesAreShortDrama() {
        let names = (1...30).map { "第\($0)集.mp4" }
        let verdict = ShortDramaClassifier.evaluate(
            directoryPath: "短剧/长公主在上",
            fileNames: names,
            fileSizes: Array(repeating: Self.shortDramaSize, count: 30)
        )
        #expect(verdict.isShortDrama)
    }

    @Test("24 集长剧不被误判成短剧")
    func ordinaryDramaSeasonIsNotShortDrama() {
        let names = (1...24).map { String(format: "剧名.S01E%02d.1080p.mkv", $0) }
        let verdict = ShortDramaClassifier.evaluate(
            directoryPath: "剧集/某季播剧",
            fileNames: names,
            fileSizes: Array(repeating: Self.longDramaSize, count: 24)
        )
        #expect(!verdict.isShortDrama)
    }

    @Test("76 集长篇国产剧不误判成短剧")
    func manyEpisodeLongDramaIsNotShortDrama() {
        let names = (1...76).map { String(format: "%02d.mp4", $0) }
        let verdict = ShortDramaClassifier.evaluate(
            directoryPath: "剧集/某七十六集电视剧",
            fileNames: names,
            fileSizes: Array(repeating: Self.longDramaSize, count: 76)
        )
        #expect(!verdict.isShortDrama)
    }

    @Test("已知时长能把长剧摘出去")
    func knownLongDurationsVetoShortDrama() {
        let names = (1...40).map { String(format: "%02d.mp4", $0) }
        let verdict = ShortDramaClassifier.evaluate(
            directoryPath: "剧集/某四十集电视剧",
            fileNames: names,
            durations: Array(repeating: 2_700.0, count: 40)
        )
        #expect(!verdict.isShortDrama)
    }

    @Test("合集文件不参与分集统计")
    func compilationFilesAreExcluded() {
        #expect(ShortDramaClassifier.isCompilationFile("剧名.全70集.mp4"))
        #expect(ShortDramaClassifier.isCompilationFile("剧名.第1-30集.mp4"))
        #expect(!ShortDramaClassifier.isCompilationFile("001.mp4"))
        let verdict = ShortDramaClassifier.evaluate(
            directoryPath: "剧集/某剧",
            fileNames: ["全70集.mp4"],
            fileSizes: [Self.shortDramaSize]
        )
        #expect(!verdict.isShortDrama)
    }

    @Test("灵敏度档位只放宽门槛，不放宽判据")
    func sensitivityControlsThreshold() {
        let names = (1...22).map { String(format: "%02d.mp4", $0) }
        let sizes = Array(repeating: Self.shortDramaSize, count: 22)
        let conservative = ShortDramaClassifier.evaluate(
            directoryPath: "剧集/某剧", fileNames: names, fileSizes: sizes, sensitivity: .conservative)
        let standard = ShortDramaClassifier.evaluate(
            directoryPath: "剧集/某剧", fileNames: names, fileSizes: sizes, sensitivity: .standard)
        #expect(conservative.score == standard.score)
        #expect(standard.isShortDrama)
    }

    @Test("单集 150–400MB 的模糊区间交给闸门")
    func ambiguousEpisodeSizeNeedsAggressive() {
        let names = (1...100).map { String(format: "%03d.mp4", $0) }
        let sizes: [Int64] = Array(repeating: 260 * 1_048_576, count: 100)
        let standard = ShortDramaClassifier.evaluate(
            directoryPath: "剧集/某百集剧", fileNames: names, fileSizes: sizes)
        let aggressive = ShortDramaClassifier.evaluate(
            directoryPath: "剧集/某百集剧", fileNames: names, fileSizes: sizes, sensitivity: .aggressive)
        #expect(!standard.isShortDrama)
        #expect(!standard.contentConfirmed)
        // 形状确实像短剧（一百集），所以留给灵敏度去救，不贴电影/剧集标签
        #expect(standard.blockedByContentGate)
        #expect(aggressive.isShortDrama)
    }

    @Test("二十来集的普通剧集目录不占短剧候选")
    func ordinarySeasonDirectoryIsLeftForKindStamping() {
        let verdict = ShortDramaClassifier.evaluate(
            directoryPath: "剧集/某二十集剧",
            fileNames: (1...22).map { String(format: "剧名.S01E%02d.mkv", $0) })
        #expect(!verdict.isShortDrama)
        #expect(!verdict.blockedByContentGate)
    }

    // MARK: - 电影 / 剧集

    @Test("形态判定认集号不认文件数")
    func mediaKindNeedsEpisodeMarkers() {
        // 两碟电影没有集号，不能因为两个文件就判成剧集
        #expect(MediaKindResolver.resolve(
            fileNames: ["飞屋环游记.CD1.mkv", "飞屋环游记.CD2.mkv"]
        ) == MediaKindResolver.movie)
        #expect(MediaKindResolver.resolve(fileNames: ["01.mp4", "02.mp4"]) == MediaKindResolver.movie)
        #expect(MediaKindResolver.resolve(fileNames: ["剧名.S01E02.mkv"]) == MediaKindResolver.tv)
        #expect(MediaKindResolver.resolve(fileNames: ["第7集.mp4"]) == MediaKindResolver.tv)
        #expect(MediaKindResolver.resolve(fileNames: ["Show.EP03.mkv"]) == MediaKindResolver.tv)
        #expect(MediaKindResolver.resolve(fileNames: ["disc1.iso"], title: "某剧 第第二季") == MediaKindResolver.tv)
    }

    @Test("筛选档位与 mediaKind 对得上")
    func libraryKindFilterMapsToMediaKind() {
        #expect(LibraryKindFilter.all.kindValue == nil)
        #expect(LibraryKindFilter.movie.kindValue == MediaKindResolver.movie)
        #expect(LibraryKindFilter.tv.kindValue == MediaKindResolver.tv)
        #expect(LibraryKindFilter.shortDrama.kindValue == ShortDrama.kindValue)
        #expect(LibraryKindFilter.tv.shortLabel == "剧集")
    }

    @Test("带季号的多季短剧也认得出来")
    func seasonPrefixedShortDramaIsDetected() {
        let names = (1...100).map { String(format: "持械入宋 - S07E%02d.mp4", $0) }
        let sizes: [Int64] = Array(repeating: 14 * 1_048_576, count: 100)
        let verdict = ShortDramaClassifier.evaluate(
            directoryPath: Self.s07Directory, fileNames: names, fileSizes: sizes)
        #expect(verdict.isShortDrama)
        #expect(verdict.contentConfirmed)
        #expect(ShortDramaClassifier.seasonNumber(inPath: "\(Self.s07Directory)/持械入宋 - S07E01.mp4") == 7)
        #expect(ShortDramaClassifier.seasonNumber(inPath: "短剧/我在八零年代当后妈/001.mp4") == nil)
    }

    @Test("老库重分类只动没人整理过的目录")
    func reclassificationSkipsCuratedDirectories() {
        let names = (1...100).map { String(format: "%03d.mp4", $0) }
        #expect(ShortDramaClassifier.shouldMergeExistingDirectory(
            directoryPath: "剧集/某百集短剧",
            fileNames: names,
            fileSizes: Array(repeating: Self.shortDramaSize, count: 100),
            hasValuableMetadata: false))
        #expect(!ShortDramaClassifier.shouldMergeExistingDirectory(
            directoryPath: "剧集/某百集短剧",
            fileNames: names,
            fileSizes: Array(repeating: Self.shortDramaSize, count: 100),
            hasValuableMetadata: true))
        #expect(!ShortDramaClassifier.shouldMergeExistingDirectory(
            directoryPath: "剧集/某季播剧",
            fileNames: (1...24).map { String(format: "剧名.S01E%02d.1080p.mkv", $0) },
            fileSizes: Array(repeating: Self.longDramaSize, count: 24),
            hasValuableMetadata: false))
    }

    @Test("已刮好的短剧目录只补类型标签")
    func taggingDoesNotCareAboutMetadata() {
        let names = (1...100).map { String(format: "持械入宋 - S07E%02d.mp4", $0) }
        // 有海报、绑过豆瓣也拦不住打标，会覆盖元数据的合并才要顾虑
        #expect(ShortDramaClassifier.shouldTagExistingDirectory(
            directoryPath: Self.s07Directory,
            fileNames: names,
            fileSizes: Array(repeating: 14 * 1_048_576, count: 100)))
        #expect(!ShortDramaClassifier.shouldTagExistingDirectory(
            directoryPath: Self.s07Directory,
            fileNames: names,
            fileSizes: Array(repeating: Self.longDramaSize, count: 100)))
    }

    // MARK: - 分集号

    @Test("集号解析覆盖短剧常见命名")
    func episodeNumberParsing() {
        #expect(ShortDramaClassifier.episodeNumber(from: "001.mp4") == 1)
        #expect(ShortDramaClassifier.episodeNumber(from: "012.mp4") == 12)
        #expect(ShortDramaClassifier.episodeNumber(from: "第37集.mp4") == 37)
        #expect(ShortDramaClassifier.episodeNumber(from: "EP003.mp4") == 3)
        #expect(ShortDramaClassifier.episodeNumber(from: "1080p.mp4") == nil)
        #expect(ShortDramaClassifier.episodeDisplayName(episode: 87) == "第87集")
    }

    @Test("搜索词剥掉集数与清晰度装饰")
    func searchKeywordStripsDecorations() {
        #expect(ShortDramaClassifier.searchKeyword(directoryPath: "剧集/我在八零年代当后妈-100集-1080p")
                    == "我在八零年代当后妈")
        #expect(ShortDramaClassifier.searchKeyword(directoryPath: "短剧/《授她以柄》(2024)")
                    == "授她以柄")
        #expect(ShortDramaClassifier.searchKeyword(directoryPath: Self.s07Directory) == "持械入宋 SKIT")
    }

    @Test("豆瓣条目主键与 TMDB 号段不撞")
    func doubanIDOffsetKeepsIDsApart() {
        let id = ShortDramaClassifier.doubanLocalID(subjectID: 3_677_798)
        #expect(id > ShortDramaClassifier.doubanIDOffset)
        #expect(ShortDramaClassifier.isDoubanBacked(movieID: id))
        #expect(!ShortDramaClassifier.isDoubanBacked(movieID: 96_874))
    }

    // MARK: - 豆瓣搜索解析

    /// 2026-10-26 抓的豆瓣搜索页（检索词：我在八零年代当后妈），只留 __DATA__ 那一段。
    private static let doubanSearchHTML = #"""
        <html><head><script>window.__DATA__ = {"count": 15, "error_info": "", "items": [{"abstract": "\u4e2d\u56fd\u5927\u9646 / \u5267\u60c5 / \u559c\u5267 / \u7231\u60c5 / \u77ed\u7247 / \u6211\u572880\u5e74\u4ee3\u5f53\u540e\u5988 / Hi, Mom / 2\u5206\u949f", "abstract_2": "\u738b\u4e9a / \u6ed5\u6cfd\u6587 / \u82cf\u8880\u79be", "cover_url": "https://img3.doubanio.com/view/photo/s_ratio_poster/public/p2929461610.jpg", "extra_actions": [], "id": 36777988, "interest": null, "label_actions": [], "labels": [{"color": "#00ad3f", "text": "\u5267\u96c6"}], "more_url": "onclick=\"moreurl(this,{from:'mv_subject_search',subject_id:'36777988',query:'%E6%88%91%E5%9C%A8%E5%85%AB%E9%9B%B6%E5%B9%B4%E4%BB%A3%E5%BD%93%E5%90%8E%E5%A6%88',i:'0',is_tv:'1'})\"", "rating": {"count": 22861, "rating_info": "", "star_count": 3.0, "value": 6.4}, "title": "\u6211\u5728\u516b\u96f6\u5e74\u4ee3\u5f53\u540e\u5988\u200e (2024)", "topics": [], "tpl_name": "search_subject", "url": "https://movie.douban.com/subject/36777988/"}], "report": {"qtype": "194", "tags": "\u7535\u5f71"}, "start": 0, "text": "\u6211\u5728\u516b\u96f6\u5e74\u4ee3\u5f53\u540e\u5988", "total": 1};</script></head><body>search</body></html>
        """#

    @Test("豆瓣搜索页解析出候选与短剧特征")
    func doubanSearchPageParses() throws {
        let hits = DoubanSearchParser.parse(html: Self.doubanSearchHTML)
        #expect(!hits.isEmpty)
        let hit = try #require(hits.first)
        #expect(hit.id == 36_777_988)
        #expect(hit.title == "我在八零年代当后妈")
        #expect(hit.year == 2024)
        #expect(hit.isTVSubject)
        #expect(hit.looksLikeShortDrama)
        #expect(hit.coverURL?.hasPrefix("https://") == true)
        #expect(hit.matchScore(query: "我在八零年代当后妈") >= 55)
        #expect(!hit.aliases.isEmpty)
    }

    @Test("目录名带尾巴时标题被包住也算强命中")
    func matchScoreAcceptsTitleInsideQuery() {
        let hit = DoubanSearchHit(
            id: 1, title: "持械入宋", year: 2026, labels: ["剧集"], genres: ["短片"],
            aliases: [], episodeMinutes: 2, rating: nil, coverURL: nil,
            subjectURL: "https://movie.douban.com/subject/1/")
        #expect(hit.matchScore(query: "持械入宋 SKIT") == 85)
        #expect(hit.matchScore(query: "完全不相关的片名") == 0)
        // 短到撑不起一个片名的标题不能靠子串吃命中
        let tiny = DoubanSearchHit(
            id: 2, title: "家", year: nil, labels: [], genres: [],
            aliases: [], episodeMinutes: nil, rating: nil, coverURL: nil,
            subjectURL: "https://movie.douban.com/subject/2/")
        #expect(tiny.matchScore(query: "我的家在哪里") == 0)
    }

    @Test("搜索页里没有同名条目时排序会清空候选")
    func rankingDropsUnrelatedHits() {
        let hits = DoubanSearchParser.parse(html: Self.doubanSearchHTML)
        #expect(DoubanService.rankedShortDramaHits(hits, query: "完全不存在的片名").isEmpty)
        #expect(!DoubanService.rankedShortDramaHits(hits, query: "我在八零年代当后妈").isEmpty)
    }

    // MARK: - 结构

    // MARK: - 红果补充源

    /// 2026-10-26 真机抓的红果搜索响应（检索词：持械入宋），去掉 vid_list 并截短了简介。
    private static let hongguoJSON = #"""
{"searchList":[{"doc_type":23,"name":"持械入宋：第一季","keyword":"7671966197985856536","video_data":{"series_id":"7671966197985856536","episode_cnt":100,"create_time":"1786268827","series_status":1,"pay_type":0,"series_title":"持械入宋：第一季","series_intro":"宋末年，烽火连天。一支执行任务的现代五人小队，意外坠入时空隧道，全员携械穿越到大宋。队长林昭京片子冷静果决，统帅全局。狙…","series_cover":"https://p3-novel.byteimg.com/novel-pic/3edbd9c6ab15ebff3fd0853ec74a4f27~tplv-shrink:640:0.image","episode_right_text":"全100集","hot_score_data":{"score":54430341,"text":"5443万热度"},"category_list":[{"category_id":"755","name":"脑洞"},{"category_id":"37","name":"穿越"},{"category_id":"758","name":"古代"},{"category_id":"11","name":"乡村"}],"celebrities":[],"genre":"205","favorite_text":"317.3万收藏","like_text":"415.4万点赞"},"user_info":{},"video_list":[]},{"doc_type":23,"name":"持械入宋：第十季","keyword":"7697209269287914521","video_data":{"series_id":"7697209269287914521","episode_cnt":100,"create_time":"1792146188","series_status":1,"pay_type":0,"series_title":"持械入宋：第十季","series_intro":"秦荣之事成为导火索，林昭先斩后奏，借机向西寿保泰军司发起战事，下令清河军、镇嵬军，德顺军全面推进；马振邦负责装备保障，陈…","series_cover":"https://p3-novel.byteimg.com/novel-pic/6a658d3f65d786c0c6b99322c20a487f~tplv-shrink:640:0.image","episode_right_text":"全100集","hot_score_data":{"score":71031005,"text":"7103万热度"},"category_list":[{"category_id":"755","name":"脑洞"},{"category_id":"37","name":"穿越"},{"category_id":"758","name":"古代"},{"category_id":"11","name":"乡村"}],"celebrities":[],"genre":"205","favorite_text":"64万收藏","like_text":"158.7万点赞"},"user_info":{},"video_list":[]}],"totalCount":"214","pageNum":1,"totalPages":1,"hasMore":false}
"""#

    @Test("红果响应解析出权威集数与竖版封面")
    func hongguoSearchPageParses() throws {
        let hits = HongguoSearchParser.parse(json: Data(Self.hongguoJSON.utf8))
        #expect(hits.count == 2)
        let hit = try #require(hits.first)
        #expect(hit.seriesID == "7671966197985856536")
        #expect(hit.episodeCount == 100)
        #expect(hit.categories.contains("穿越"))
        #expect(hit.coverURL?.hasPrefix("https://") == true)
        #expect(hit.year != nil)
        #expect(hit.coreTitle == "持械入宋")
        #expect(hit.season == 1)
    }

    @Test("季号比对：S07 目录不绑第一季")
    func seasonAwareMatching() {
        #expect(ShortDramaTitleMatch.splitSeason(from: "持械入宋：第七季").core == "持械入宋")
        #expect(ShortDramaTitleMatch.splitSeason(from: "持械入宋：第七季").season == 7)
        #expect(ShortDramaTitleMatch.splitSeason(from: "持械入宋：第十季").season == 10)
        #expect(ShortDramaTitleMatch.splitSeason(from: "长公主在上").season == nil)
        let query = "持械入宋 SKIT"
        let seventh = HongguoHit(
            seriesID: "1", title: "持械入宋：第七季", intro: nil, coverURL: nil,
            episodeCount: 100, year: 2026, categories: [], heatText: nil)
        #expect(seventh.matchScore(query: query, season: 7) >= 90)
        // 第一季对上 S07：基础 85 分罚到 20 分以下，会被刷掉
        let first = HongguoSearchParser.parse(json: Data(Self.hongguoJSON.utf8)).first
        #expect((first?.matchScore(query: query, season: 7) ?? 0) < 20)
        // 目录没有季号时，分季条目仍然可用
        #expect((first?.matchScore(query: query, season: nil) ?? 0) >= 20)
        #expect(HongguoService.rankedHits(
            HongguoSearchParser.parse(json: Data(Self.hongguoJSON.utf8)),
            query: "完全不存在的片名", season: nil).isEmpty)
    }

    @Test("旁表补齐只填坑，不抢已写好的出处")
    func enrichmentKeepsExistingProvenance() throws {
        let db = try DatabaseQueue()
        try db.write { database in
            try database.execute(sql: #"CREATE TABLE "movie"("id" INTEGER PRIMARY KEY, "title" TEXT NOT NULL)"#)
            try ShortDramaMetadata.createTable(database)
            try database.execute(sql: "INSERT INTO movie VALUES (7, '持械入宋')")
            try ShortDramaMetadata.save(
                db: database, movieId: 7, source: "douban", externalId: "38589478", episodeCount: 100)
            try ShortDramaMetadata.enrich(
                db: database, movieId: 7, source: "hongguo",
                externalId: "7671966197985856536", platform: "红果",
                episodeCount: 88, tags: ["穿越", "古代"], posterOrientation: "portrait")
            let record = try #require(try ShortDramaMetadata.fetchOne(database, key: 7))
            // 已有的来源、编号、集数都不该被补充源改掉
            #expect(record.source == "douban")
            #expect(record.externalId == "38589478")
            #expect(record.episodeCount == 100)
            #expect(record.platform == "红果")
            #expect(record.tagList == ["穿越", "古代"])
            #expect(record.posterOrientation == "portrait")
        }
    }

    @Test("短剧旁表随结构自愈建得起来")
    func shortDramaTableRoundTrip() throws {
        let db = try DatabaseQueue()
        try db.write { database in
            try database.execute(sql: #"CREATE TABLE "movie"("id" INTEGER PRIMARY KEY, "title" TEXT NOT NULL)"#)
            try ShortDramaMetadata.createTable(database)
            // 旁表按 movieId 外键指向条目，先有父行再写子行
            try database.execute(sql: #"INSERT INTO "movie"("id", "title") VALUES (42, ?)"#, arguments: ["持械入宋"])
            try ShortDramaMetadata.save(
                db: database,
                movieId: 42,
                source: "douban",
                externalId: "36777988",
                episodeCount: 100,
                tags: ["爱情", "短片"],
                posterOrientation: "portrait"
            )
            let saved: ShortDramaMetadata? = try ShortDramaMetadata.fetchOne(database, key: 42)
            let record = try #require(saved)
            #expect(record.source == "douban")
            #expect(record.episodeCount == 100)
            #expect(record.tagList == ["爱情", "短片"])
        }
    }

    @Test("旁表换主键不被级联吃掉，缺行能补回来")
    func metadataRekeySurvivesPlaceholderDeleteAndBackfills() throws {
        let db = try DatabaseQueue()
        try db.write { database in
            try database.execute(sql: #"CREATE TABLE "movie"("id" INTEGER PRIMARY KEY, "title" TEXT NOT NULL)"#)
            try database.execute(sql: """
                CREATE TABLE "videoFile"("id" TEXT PRIMARY KEY, "movieId" INTEGER, \
                "mediaType" TEXT NOT NULL, "mediaKind" TEXT)
                """)
            try database.execute(sql: #"CREATE TABLE "doubanMetadata"("movieId" INTEGER PRIMARY KEY, "subjectId" TEXT NOT NULL)"#)
            try ShortDramaMetadata.createTable(database)

            // 占位条目 + 一百个分集，旁表先按占位 id 记一行
            try database.execute(sql: "INSERT INTO movie VALUES (-5, '持械入宋')")
            for index in 1...100 {
                try database.execute(
                    sql: "INSERT INTO videoFile VALUES (?, -5, 'unmatched', ?)",
                    arguments: ["f\(index)", ShortDrama.kindValue])
            }
            try ShortDramaMetadata.save(db: database, movieId: -5, source: "local", episodeCount: 100)

            // 刮到真实条目：先换主键再删占位行，外键级联不该把整行带走
            try database.execute(sql: "INSERT INTO movie VALUES (336651, '持械入宋')")
            try ShortDramaMetadata.rekey(database, from: -5, to: 336651)
            try database.execute(sql: "UPDATE videoFile SET movieId = 336651, mediaType = 'movie'")
            try database.execute(sql: "DELETE FROM movie WHERE id = -5")
            #expect(try ShortDramaMetadata.fetchOne(database, key: 336651) != nil)
        }

        // 真机现状：行已经丢过一次，靠补齐扫回来，来源按证据认
        try db.write { database in
            try database.execute(sql: "DELETE FROM shortDramaMetadata")
            #expect(try ShortDramaMetadata.backfillMissingRows(db: database) == 1)
            let record = try #require(try ShortDramaMetadata.fetchOne(database, key: 336651))
            #expect(record.source == "tmdb")
            #expect(record.episodeCount == 100)
            #expect(try ShortDramaMetadata.backfillMissingRows(db: database) == 0)
        }
    }

    @Test("豆瓣落库先改指再删占位行，一百集不会掉成孤儿")
    func doubanBindingKeepsEpisodeFilesAttached() throws {
        func seed(_ database: Database) throws {
            try database.execute(sql: #"CREATE TABLE "movie"("id" INTEGER PRIMARY KEY, "title" TEXT NOT NULL)"#)
            // videoFile.movieId 是真机 schema 的 ON DELETE SET NULL，级联时机全靠它
            try database.execute(sql: #"CREATE TABLE "videoFile"("id" TEXT PRIMARY KEY, "relativePath" TEXT NOT NULL, "mediaType" TEXT NOT NULL, "movieId" INTEGER REFERENCES "movie"("id") ON DELETE SET NULL, "mediaKind" TEXT)"#)
            try database.execute(sql: #"CREATE TABLE "doubanMetadata"("movieId" INTEGER PRIMARY KEY REFERENCES "movie"("id") ON DELETE CASCADE, "subjectId" TEXT NOT NULL)"#)
            try ShortDramaMetadata.createTable(database)
            try database.execute(sql: "INSERT INTO movie VALUES (-5, '持械入宋 S01')")
            try database.execute(sql: "INSERT INTO doubanMetadata VALUES (-5, '38589478')")
            try ShortDramaMetadata.save(db: database, movieId: -5, source: "local", episodeCount: 100)
            for index in 1...100 {
                try database.execute(
                    sql: "INSERT INTO videoFile VALUES (?, ?, 'unmatched', -5, ?)",
                    arguments: ["f\(index)", "持械入宋 S01（100集） - SKIT/\(index).mp4", ShortDrama.kindValue]
                )
            }
        }
        func orphans(_ database: Database) throws -> Int {
            try Int.fetchOne(database, sql: "SELECT COUNT(*) FROM videoFile WHERE movieId IS NULL") ?? -1
        }

        let doubanID = ShortDramaClassifier.doubanLocalID(subjectID: 38589478)

        // 落库顺序：建正条目 → 分集改指 → 挪旁表 → 最后删占位行
        let db = try DatabaseQueue()
        try db.write { database in
            try seed(database)
            try database.execute(
                sql: "INSERT INTO movie SELECT ?, title FROM movie WHERE id = -5",
                arguments: [doubanID]
            )
            try database.execute(
                sql: "UPDATE videoFile SET movieId = ?, mediaType = 'movie' WHERE movieId = -5",
                arguments: [doubanID]
            )
            try database.execute(sql: "DELETE FROM doubanMetadata WHERE movieId = -5")
            try database.execute(sql: "INSERT INTO doubanMetadata VALUES (?, '38589478')", arguments: [doubanID])
            try ShortDramaMetadata.save(
                db: database,
                movieId: doubanID,
                source: "douban",
                externalId: "38589478",
                episodeCount: 100
            )
            try database.execute(sql: "DELETE FROM movie WHERE id = -5")
            // 扫描收尾会清掉没人引用的条目，这一步不该把刚建好的那条也带走
            try database.execute(sql: "DELETE FROM movie WHERE id NOT IN (SELECT DISTINCT movieId FROM videoFile WHERE movieId IS NOT NULL)")
            #expect(try orphans(database) == 0)
            #expect(try Int.fetchOne(database, sql: "SELECT COUNT(*) FROM videoFile WHERE movieId = ?", arguments: [doubanID]) == 100)
            #expect(try ShortDramaMetadata.fetchOne(database, key: doubanID)?.source == "douban")
            #expect(try Int.fetchOne(database, sql: "SELECT COUNT(*) FROM doubanMetadata WHERE movieId = ?", arguments: [doubanID]) == 1)
        }

        // 反例：先删占位行，SET NULL 会把一百集全部解绑，之后的改指就找不到人了
        let broken = try DatabaseQueue()
        try broken.write { database in
            try seed(database)
            try database.execute(sql: "DELETE FROM movie WHERE id = -5")
            try database.execute(
                sql: "UPDATE videoFile SET movieId = ?, mediaType = 'movie' WHERE movieId = -5",
                arguments: [doubanID]
            )
            #expect(try orphans(database) == 100)
            // 孤儿行的识别入口：下一轮扫描要能把它们重新收编
            #expect(try Int.fetchOne(
                database,
                sql: "SELECT COUNT(*) FROM videoFile WHERE movieId IS NULL OR movieId NOT IN (SELECT id FROM movie)"
            ) == 100)
        }
    }
}
