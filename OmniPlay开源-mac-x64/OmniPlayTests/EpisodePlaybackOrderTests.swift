//
//  EpisodePlaybackOrderTests.swift
//  OmniPlayTests
//
//  剧集连播的两件事：一是「按集序播」，二是「谁播完标谁」。
//  以前播放表和分集墙各排各的，mpv 的下一条就可能不是墙上的下一集；
//  自动切集那一瞬，上一集的片尾值又会被当成下一集的进度，没看的一集直接变已播。
//

import XCTest
@testable import OmniPlay

final class EpisodePlaybackOrderTests: XCTestCase {

    private struct Item {
        let id: String
        let relativePath: String
        let fileName: String
    }

    private func sorted(_ items: [Item], overrides: [String: MediaNameParser.EpisodeOrderOverride] = [:]) -> [String] {
        MediaNameParser.sortedForEpisodePlayback(
            items,
            relativePath: { $0.relativePath },
            fileName: { $0.fileName },
            fileId: { $0.id },
            override: { id in overrides[id] }
        ).map(\.id)
    }

    // MARK: - 集序

    /// 两季放在一个条目下、集号写在文件名里时，季号必须以目录为准：
    /// 以前两季被压成同一季按集号交错，播完第 1 季第 1 集就跳去第 2 季第 1 集。
    func testSeasonFolderDecidesEpisodeOrderAcrossSeasons() {
        var items: [Item] = []
        for season in [1, 2] {
            for episode in 1...4 {
                let name = String(format: "琅琊榜 第%02d集.mp4", episode)
                items.append(Item(id: "s\(season)e\(episode)", relativePath: "琅琊榜/Season \(season)/\(name)", fileName: name))
            }
        }

        XCTAssertEqual(
            sorted(items),
            ["s1e1", "s1e2", "s1e3", "s1e4", "s2e1", "s2e2", "s2e3", "s2e4"])
    }

    /// 文件名自己写死 SxxExx 时以文件名为准，目录名写错季号也不该把它拽走。
    func testExplicitFileNameSeasonWinsOverFolder() {
        let items = [
            Item(id: "a", relativePath: "剧名/Season 1/Show.S02E03.mkv", fileName: "Show.S02E03.mkv"),
            Item(id: "b", relativePath: "剧名/Season 1/Show.S01E09.mkv", fileName: "Show.S01E09.mkv"),
        ]
        XCTAssertEqual(sorted(items), ["b", "a"])
    }

    /// 字幕组命名认不出集号时，用户在分集弹窗里改过的季/集必须真的改变顺序——
    /// 以前墙上的标签跟着修正走、顺序却不跟，第 3 集会排在第 1 集前面。
    func testManualEpisodeOverrideDrivesOrder() {
        let items = [
            Item(id: "one", relativePath: "有翡/[NC] 有翡 - 01 [1080p].mkv", fileName: "[NC] 有翡 - 01 [1080p].mkv"),
            Item(id: "two", relativePath: "有翡/[NC] 有翡 - 02 [1080p].mkv", fileName: "[NC] 有翡 - 02 [1080p].mkv"),
            Item(id: "three", relativePath: "有翡/[NC] 有翡 - 03 [1080p].mkv", fileName: "[NC] 有翡 - 03 [1080p].mkv"),
        ]
        let overrides = [
            "one": MediaNameParser.EpisodeOrderOverride(season: 1, episode: 3),
            "two": MediaNameParser.EpisodeOrderOverride(season: 1, episode: 2),
            "three": MediaNameParser.EpisodeOrderOverride(season: 1, episode: 1),
        ]
        XCTAssertEqual(sorted(items, overrides: overrides), ["three", "two", "one"])
    }

    func testResolvedSeasonPrecedence() {
        // 手工修正 > 文件名写死的 Sxx > 目录季号 > 默认第 1 季
        XCTAssertEqual(
            MediaNameParser.resolvedEpisodeSeason(relativePath: "A/Season 3/x.mkv", fileName: "S02E05 x.mkv", fallbackIndex: 0, overrideSeason: 7), 7)
        XCTAssertEqual(
            MediaNameParser.resolvedEpisodeSeason(relativePath: "A/Season 3/x.mkv", fileName: "S02E05 x.mkv", fallbackIndex: 0), 2)
        XCTAssertEqual(
            MediaNameParser.resolvedEpisodeSeason(relativePath: "A/Season 3/第05集.mkv", fileName: "第05集.mkv", fallbackIndex: 4), 3)
        XCTAssertEqual(
            MediaNameParser.resolvedEpisodeSeason(relativePath: "A/x.mkv", fileName: "第05集.mkv", fallbackIndex: 4), 1)
    }

    // MARK: - 结尾值归属

    /// 自动切到下一集：filename 已经指向下一集，上一集的片尾值不能再发出去，
    /// 否则关闭播放窗时会把一秒没看的下一集写成已播。
    func testEndedMarkIsNotLeakedToTheNextPlaylistEntry() {
        var tracker = PlaybackEndTracker()
        tracker.observe(.init(duration: 1_200, timePos: 1_200, eofReason: "eof", idleWithoutFile: false,
                              currentFile: "/nas/S01E01.mkv", playlistPos: 0))
        XCTAssertEqual(tracker.endedDuration, 1_200)

        // mpv 已切到下一条，还在缓冲（读不到时长）
        XCTAssertEqual(tracker.endedMark(forCurrentFile: "/nas/S01E02.mkv", playlistPos: 1), 0)
        // 连文件名都还没报出来，也不能算成上一条的结尾
        XCTAssertEqual(tracker.endedMark(forCurrentFile: nil, playlistPos: 1), 0)
    }

    /// 整表放完：mpv 清空当前条目、playlist-pos 落到 -1，这时仍然要按「看到结尾」算，
    /// 不然最后一集关掉播放窗时进度会被写成 0。
    func testEndedMarkSurvivesEngineGoingIdle() {
        var tracker = PlaybackEndTracker()
        tracker.observe(.init(duration: 1_200, timePos: 1_200, eofReason: "eof", idleWithoutFile: false,
                              currentFile: "/nas/S01E03.mkv", playlistPos: 2))
        XCTAssertEqual(tracker.endedMark(forCurrentFile: nil, playlistPos: -1), 1_200)
        XCTAssertEqual(tracker.endedMark(forCurrentFile: "", playlistPos: -1), 1_200)
        // 同一个文件还在当前条目上（keep-open 停在片尾）也照发
        XCTAssertEqual(tracker.endedMark(forCurrentFile: "/nas/S01E03.mkv", playlistPos: 2), 1_200)
    }

    /// 远程 URL 带签名参数时也要认出是同一个文件。
    func testEndedMatchesRemoteURLAndPercentEncodedNames() {
        XCTAssertTrue(PlaybackEndTracker.isSameMediaFile(
            "https://nas/d/%E7%AC%AC01%E9%9B%86.mkv?token=abc", "/nas/第01集.MKV"))
        XCTAssertFalse(PlaybackEndTracker.isSameMediaFile(
            "https://nas/d/ep01.mkv?token=abc", "https://nas/d/ep02.mkv?token=abc"))
    }

    // MARK: - 区间连坐

    /// mpv 自己跳条（打不开、或轮询漏了一次切换）时，中间那些一秒没放的条目不能跟着被写成已播。
    func testSkippedPlaylistEntriesAreNotMarkedFinished() {
        let watched = PlaybackWatchState(progress: 1_200, duration: 1_200)
        let untouched = PlaybackWatchState(progress: 0, duration: 1_200)

        XCTAssertTrue(PlayerScreen.shouldMarkPlaylistFileFinished(
            fileId: "ep1", justPlayedFileId: "ep1", storedWatchState: untouched))
        XCTAssertFalse(PlayerScreen.shouldMarkPlaylistFileFinished(
            fileId: "ep2", justPlayedFileId: "ep1", storedWatchState: untouched))
        XCTAssertFalse(PlayerScreen.shouldMarkPlaylistFileFinished(
            fileId: "ep3", justPlayedFileId: "ep1", storedWatchState: untouched))
        // 库里本来就记着已播的，保持原样
        XCTAssertTrue(PlayerScreen.shouldMarkPlaylistFileFinished(
            fileId: "ep3", justPlayedFileId: "ep1", storedWatchState: watched))
    }
}
