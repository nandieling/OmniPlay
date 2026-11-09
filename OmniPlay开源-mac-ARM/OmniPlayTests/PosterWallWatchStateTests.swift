//
//  PosterWallWatchStateTests.swift
//  OmniPlayTests
//
//  首页海报下面那颗圆点必须说实话：播到一半的片子不能显示成「未播」。
//  以前卡片只有「全放完 / 其余」两档，进度写在库里但首页看不出来。
//

import XCTest
@testable import OmniPlay

final class PosterWallWatchStateTests: XCTestCase {

    private func file(progress: Double, duration: Double) -> VideoFile {
        VideoFile(
            id: "vf-\(progress)-\(duration)",
            sourceId: 1,
            relativePath: "随便/文件.mp4",
            fileName: "文件.mp4",
            mediaType: "movie",
            mediaKind: nil,
            movieId: 7,
            episodeId: nil,
            playProgress: progress,
            duration: duration
        )
    }

    /// 库里没文件时别凭空报个状态出来。
    func testNoFilesIsUnplayed() {
        XCTAssertEqual(PlaybackWatchState.aggregate(files: []), .unplayed)
    }

    /// 这就是用户报的那件事：播到 40% 的片子在首页得是「未播完」，不能是「未播」。
    func testPartiallyPlayedFileReadsAsInProgress() {
        let state = PlaybackWatchState.aggregate(files: [file(progress: 960, duration: 2400)])
        XCTAssertEqual(state, .inProgress)
        XCTAssertEqual(state.statusLabel, "未播完")
    }

    func testEveryFileFinishedReadsAsWatched() {
        let state = PlaybackWatchState.aggregate(files: [
            file(progress: 2400, duration: 2400),
            file(progress: 1_800, duration: 1_800),
        ])
        XCTAssertEqual(state, .watched)
        XCTAssertEqual(state.statusLabel, "已播")
    }

    /// 多文件条目：一集看完、另一集没动，整部还是「未播完」。
    func testMixedEpisodesReadAsInProgress() {
        let state = PlaybackWatchState.aggregate(files: [
            file(progress: 1_500, duration: 1_500),
            file(progress: 0, duration: 1_500),
        ])
        XCTAssertEqual(state, .inProgress)
    }

    /// 差最后一档没到 95% 的，仍然算未播完，不能提前盖章已播。
    func testJustUnderThresholdIsNotWatched() {
        let state = PlaybackWatchState.aggregate(files: [file(progress: 900, duration: 1_000)])
        XCTAssertEqual(state, .inProgress)
    }

    /// 短剧几乎都是 duration = 0：有进度顶多算未播完，绝不能算已播。
    func testUnknownDurationWithProgressIsInProgress() {
        let state = PlaybackWatchState.aggregate(files: [file(progress: 42, duration: 0)])
        XCTAssertEqual(state, .inProgress)
    }

    /// 脏值不能凭空造出进度，也不能崩。
    func testDirtyValuesFallBackToUnplayed() {
        for progress in [-10, 0, .nan] {
            for duration in [-1, 0, .nan, .infinity] {
                let state = PlaybackWatchState.aggregate(files: [file(progress: progress, duration: duration)])
                XCTAssertEqual(state, .unplayed, "progress=\(progress) duration=\(duration)")
            }
        }
    }
}
