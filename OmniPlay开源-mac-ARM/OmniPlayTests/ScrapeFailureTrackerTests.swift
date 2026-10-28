//
//  ScrapeFailureTrackerTests.swift
//  OmniPlayTests
//
//  自动刮削失败账本：连续失败 3 次停止自动重试，手动扫描清零。纯逻辑，不打网络。
//

import Foundation
import Testing
@testable import OmniPlay

@MainActor
@Suite("Scrape Failure Tracker", .serialized)
struct ScrapeFailureTrackerTests {

    private var tracker: ScrapeFailureTracker { .shared }

    @Test("自动刮削连续失败 3 次后不再重试")
    func autoAttemptsStopAfterThreeFailures() {
        tracker.resetAll()
        let poster = ScrapeFailureTracker.Target.poster("test-poster-a")
        #expect(tracker.allowsAutoAttempt(poster))
        tracker.recordFailure(poster)
        tracker.recordFailure(poster)
        #expect(tracker.allowsAutoAttempt(poster), "失败 2 次还应该给一次机会")
        #expect(tracker.recordFailure(poster) == 3)
        #expect(!tracker.allowsAutoAttempt(poster))
    }

    @Test("抓取成功即销账")
    func successClearsTheLedger() {
        tracker.resetAll()
        let still = ScrapeFailureTracker.Target.still("test-file-b")
        for _ in 0..<ScrapeFailureTracker.maxAutoAttempts { tracker.recordFailure(still) }
        #expect(!tracker.allowsAutoAttempt(still))
        tracker.clear(still)
        #expect(tracker.allowsAutoAttempt(still))
        #expect(tracker.failureCount(still) == 0)
    }

    @Test("超额失败收敛回上限，不会无限累加")
    func failuresClampAtTheLimit() {
        tracker.resetAll()
        let scrape = ScrapeFailureTracker.Target.scrape("test-file-c")
        for _ in 0..<10 { tracker.recordFailure(scrape) }
        #expect(tracker.failureCount(scrape) == ScrapeFailureTracker.maxAutoAttempts)
    }

    @Test("三类目标各自独立记账")
    func targetsAreIndependent() {
        tracker.resetAll()
        let still = ScrapeFailureTracker.Target.still("test-file-d")
        for _ in 0..<ScrapeFailureTracker.maxAutoAttempts { tracker.recordFailure(still) }
        #expect(!tracker.allowsAutoAttempt(still))
        #expect(tracker.allowsAutoAttempt(.poster("test-poster-d")))
        #expect(tracker.allowsAutoAttempt(.scrape("test-file-d")))
        #expect(tracker.gaveUpTargetCount == 1)
    }

    @Test("手动扫描清零后所有目标重新计数")
    func manualScanResetsEverything() {
        tracker.resetAll()
        let targets: [ScrapeFailureTracker.Target] = [
            .poster("test-poster-e"), .still("test-file-e"), .scrape("test-file-e"),
        ]
        for target in targets {
            for _ in 0..<ScrapeFailureTracker.maxAutoAttempts { tracker.recordFailure(target) }
            #expect(!tracker.allowsAutoAttempt(target))
        }
        tracker.resetAll()
        for target in targets {
            #expect(tracker.allowsAutoAttempt(target))
        }
        #expect(tracker.gaveUpTargetCount == 0)
    }
}
