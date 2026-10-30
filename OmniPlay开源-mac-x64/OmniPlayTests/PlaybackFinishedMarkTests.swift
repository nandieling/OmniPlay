//
//  PlaybackFinishedMarkTests.swift
//  OmniPlayTests
//
//  播完即已看：落库形态必须能被读回成「已播」。短剧文件几乎都是 duration = 0，
//  旧实现把进度也写成 0，标记就在库里隐形了。
//

import XCTest
@testable import OmniPlay

final class PlaybackFinishedMarkTests: XCTestCase {

    private func shortDramaFile(duration: Double, progress: Double = 0) -> VideoFile {
        VideoFile(
            id: "vf-s02e95",
            sourceId: 1,
            relativePath: "持械入宋/持械入宋 - S02E95.mp4",
            fileName: "持械入宋 - S02E95.mp4",
            mediaType: "movie",
            mediaKind: ShortDrama.kindValue,
            movieId: 42,
            episodeId: nil,
            playProgress: progress,
            duration: duration
        )
    }

    /// 连播切换时的自动标记只带文件 id：库里时长还没写过的话，标记必须仍能被认成已播。
    func testMarkFinishedWithoutKnownDurationStillReadsAsWatched() {
        var file = shortDramaFile(duration: 0)

        PlayerScreen.applyFinishedPlaybackState(to: &file)

        XCTAssertGreaterThan(file.playProgress, 0)
        XCTAssertEqual(file.duration, file.playProgress)
        XCTAssertEqual(PlaybackWatchState(progress: file.playProgress, duration: file.duration), .watched)
        XCTAssertNil(file.lastPlayedAt)
    }

    /// 引擎报过真实时长时不许用兜底值，否则进度条和总时长都会被写歪。
    func testMarkFinishedPrefersEngineReportedDuration() {
        var file = shortDramaFile(duration: 0)

        PlayerScreen.applyFinishedPlaybackState(to: &file, observedDuration: 118.96, observedProgress: 118.9)

        XCTAssertEqual(file.duration, 118.96, accuracy: 0.001)
        XCTAssertEqual(file.playProgress, 118.96, accuracy: 0.001)
    }

    func testMarkFinishedPinsKnownDurationToEnd() {
        var file = shortDramaFile(duration: 105.3, progress: 40)

        PlayerScreen.applyFinishedPlaybackState(to: &file)

        XCTAssertEqual(file.playProgress, 105.3, accuracy: 0.001)
        XCTAssertEqual(PlaybackWatchState(progress: file.playProgress, duration: file.duration), .watched)
    }

    /// 脏值也不能写成 0/0：那在库里就是「没播过」。
    func testFinishedMarkNeverWritesZero() {
        for duration in [-1, 0, .nan, .infinity] {
            let mark = PlaybackWatchState.finishedMark(duration: Double(duration))
            XCTAssertGreaterThan(mark, 0, "duration=\(duration) 的已看标记不能是 0")
            XCTAssertEqual(PlaybackWatchState(progress: mark, duration: mark), .watched)
        }
    }
}
