import Testing
import Foundation
import UniformTypeIdentifiers
@testable import OmniPlay

// 直开视频文件这条链路上最容易坏的是「系统认不认这些容器」和
// 「访达愿不愿意把文件交给觅影」，所以这两件事都写成断言。
@MainActor
@Suite("直开视频文件")
struct DirectFilePlaybackTests {
    @Test("支持的扩展名都能解析成系统认识的类型")
    func extensionsResolve() {
        #expect(DirectFilePlaybackManager.supportedVideoExtensions.count >= 12)
        for ext in DirectFilePlaybackManager.supportedVideoExtensions {
            #expect(UTType(filenameExtension: ext) != nil, "\(ext) 解析不出 UTI")
        }
        #expect(DirectFilePlaybackManager.supportedExtensionSet.isSuperset(of: ["mp4", "mkv", "iso"]))
    }

    @Test("支持类型都归到视频或光盘镜像之下")
    func supportedTypesConformToDeclaredFamilies() {
        let families: [UTType] = [.movie, .audiovisualContent] + ([UTType("public.iso-image")].compactMap { $0 })
        #expect(!families.isEmpty)
        for type in DirectFilePlaybackManager.supportedContentTypes {
            let covered = families.contains { type.conforms(to: $0) }
            #expect(covered, "\(type.identifier) 不在声明的类型族之下")
        }
    }

    @Test("Info.plist 声明了视频类型，访达才会给出打开方式")
    func bundleDeclaresMovieTypes() throws {
        let types = try #require(Bundle.main.infoDictionary?["CFBundleDocumentTypes"] as? [[String: Any]])
        let declared = types.flatMap { ($0["LSItemContentTypes"] as? [String]) ?? [] }
        #expect(declared.contains("public.movie"))
        #expect(declared.contains("public.iso-image"))
        #expect(types.contains { ($0["CFBundleTypeRole"] as? String) == "Viewer" })
    }
}

// 播完之后的时间轴、剩余时间、写回库里的进度都取自同一个判断，
// 而 mpv 在播完那一刻读到的属性会骗人，所以单独把这段判断写死。
@Suite("播完状态判定")
struct PlaybackEndTrackerTests {
    private func observation(
        duration: Double,
        timePos: Double,
        eofReason: String? = nil,
        idleWithoutFile: Bool = false
    ) -> PlaybackEndTracker.Observation {
        PlaybackEndTracker.Observation(
            duration: duration,
            timePos: timePos,
            eofReason: eofReason,
            idleWithoutFile: idleWithoutFile
        )
    }

    @Test("keep-open 停在结尾时认得出已播完")
    func eofReasonMarksEnd() {
        var tracker = PlaybackEndTracker()
        tracker.observe(observation(duration: 100, timePos: 42))
        tracker.observe(observation(duration: 100, timePos: 100, eofReason: "eof"))
        #expect(tracker.endedDuration == 100)
    }

    @Test("引擎回到 idle 时用最后看到的结尾兜底")
    func idleFallbackMarksEnd() {
        var tracker = PlaybackEndTracker()
        tracker.observe(observation(duration: 100, timePos: 97))
        tracker.observe(observation(duration: 0, timePos: 0, idleWithoutFile: true))
        #expect(tracker.endedDuration == 100)
    }

    @Test("半路 idle（切源、出错）不算播完")
    func idleInMiddleIsNotEnd() {
        var tracker = PlaybackEndTracker()
        tracker.observe(observation(duration: 100, timePos: 30))
        tracker.observe(observation(duration: 0, timePos: 0, idleWithoutFile: true))
        #expect(tracker.endedDuration == 0)
    }

    @Test("从结尾重新播之后结尾状态作废")
    func resumingClearsEndState() {
        var tracker = PlaybackEndTracker()
        tracker.observe(observation(duration: 100, timePos: 100, eofReason: "eof"))
        #expect(tracker.endedDuration == 100)
        tracker.observe(observation(duration: 100, timePos: 2))
        #expect(tracker.endedDuration == 0)
    }

    @Test("刚换片还没有时长时不算播完")
    func freshLoadWithoutDurationIsNotEnd() {
        var tracker = PlaybackEndTracker()
        tracker.observe(observation(duration: 0, timePos: 0, idleWithoutFile: true))
        #expect(tracker.endedDuration == 0)
        tracker.reset()
        #expect(tracker.endedDuration == 0)
        #expect(tracker.liveDuration == 0)
    }
}

// 退出全屏后画面被裁成一小块，靠的是重建 mpv 的视频输出；重建时不能顺手把
// 用户选好的视频轨抹掉，所以「恢复哪个 vid 值」这条判断单独钉住。
@MainActor
@Suite("重建视频输出时的轨道选择")
struct VideoOutputReinitTests {
    @Test("正数轨道 ID 原样恢复，其余一律回 auto")
    func restoresSelectedTrack() {
        #expect(MPVPlayerManager.videoTrackSelectionValue("3") == "3")
        #expect(MPVPlayerManager.videoTrackSelectionValue("auto") == "auto")
        #expect(MPVPlayerManager.videoTrackSelectionValue("no") == "auto")
        #expect(MPVPlayerManager.videoTrackSelectionValue("0") == "auto")
        #expect(MPVPlayerManager.videoTrackSelectionValue("-1") == "auto")
        #expect(MPVPlayerManager.videoTrackSelectionValue(nil) == "auto")
    }
}
