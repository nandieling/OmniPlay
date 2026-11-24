//
//  ScrapeFailureTrackerTests.swift
//  OmniPlayTests
//
//  自动刮削失败账本：连续失败 3 次停止自动重试，手动扫描清零。纯逻辑，不打网络。
//

import Foundation
import AppKit
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

// 海报解码都走 PosterImageLoader：后台读盘 + 降采样 + 内存缓存 + 同图并发合并。
// 卡片「先一片空白海报、过几秒才出图」就出在这条链上，所以这里盯两件事：
// 同一张图被并发要时谁都不许被吊住，以及内容不是图片的文件要清掉让上层重下。
@Suite("Poster Image Loader")
struct PosterImageLoaderTests {

    @Test("同一张海报并发只解一次，坏文件返回 nil 并自删")
    func coalescesConcurrentRequestsAndDropsBrokenFiles() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }

        let url = directory.appendingPathComponent("poster.png")
        try PNGSamples.large.write(to: url)

        let first = await PosterImageLoader.shared.image(at: url)
        #expect(first != nil, "正常 PNG 应该解得出图")
        #expect((first?.width ?? 0) <= Int(PosterImageLoader.displayMaxPixelSize), "要降采样到显示尺寸")
        #expect(PosterImageLoader.shared.cachedImage(for: url) != nil, "解完要进内存缓存")

        async let a = PosterImageLoader.shared.image(at: url)
        async let b = PosterImageLoader.shared.image(at: url)
        async let c = PosterImageLoader.shared.image(at: url)
        let concurrent = await [a, b, c]
        #expect(concurrent.allSatisfy { $0 != nil }, "挂在同一个解码任务上的 waiter 都要拿到结果")

        let broken = directory.appendingPathComponent("broken.png")
        try Data("not an image".utf8).write(to: broken)
        #expect(await PosterImageLoader.shared.image(at: broken) == nil)
        #expect(!FileManager.default.fileExists(atPath: broken.path), "坏文件要删掉，让 CachedPosterView 去重下")
    }

    @Test("预取把还没滚到跟前的海报先解码进内存缓存")
    func prefetchWarmsMemoryCacheAheadOfScroll() async throws {
        // 走真实路径解析：相对路径会映射到海报缓存目录里的固定文件名
        let posterPath = "uitest-\(UUID().uuidString)/poster.jpg"
        let url = PosterManager.shared.cacheDirectory
            .appendingPathComponent(PosterManager.shared.cacheFileName(for: posterPath))
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try PNGSamples.large.write(to: url)
        defer {
            try? FileManager.default.removeItem(at: url)
            PosterImageLoader.shared.forget(url)
        }

        PosterImageLoader.shared.prefetchPosterPaths([posterPath])

        // 预取排在后台优先级队列上，给它最多 5 秒落地
        let deadline = Date().addingTimeInterval(5)
        while PosterImageLoader.shared.cachedImage(for: url) == nil && Date() < deadline {
            try await Task.sleep(nanoseconds: 20_000_000)
        }
        #expect(PosterImageLoader.shared.cachedImage(for: url) != nil, "卡片入屏前海报就该在内存缓存里")
    }
}

private enum PNGSamples {
    /// 一张比显示尺寸还大的位图，用来验证降采样确实生效。
    static let large: Data = {
        let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil, pixelsWide: 900, pixelsHigh: 1_350,
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)
        guard let rep, let data = rep.representation(using: .png, properties: [:]) else { return Data() }
        return data
    }()
}
