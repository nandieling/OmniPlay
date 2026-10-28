import Foundation
import AppKit
import Combine
import QuartzCore
import Libmpv

/// mpv 播完那一刻的属性并不可靠：keep-open 生效时还能读到结尾的 time-pos/duration，
/// bd:// 等源会直接回到 idle、两个值都读成 0。把「已经播完」的判断收成一个纯类型，
/// 让界面时间轴和退出时写库用的是同一个结论。
nonisolated struct PlaybackEndTracker {
    struct Observation {
        let duration: Double
        let timePos: Double
        let eofReason: String?
        let idleWithoutFile: Bool
    }

    private(set) var liveDuration: Double = 0
    private(set) var liveTimePos: Double = 0
    private(set) var endedDuration: Double = 0

    mutating func reset() { self = PlaybackEndTracker() }

    mutating func observe(_ observation: Observation) {
        let duration = observation.duration
        let timePos = observation.timePos
        if duration.isFinite && duration > 0 {
            liveDuration = duration
            // 又播回中间了（停在结尾后按播放会重头来），结尾状态作废
            if timePos.isFinite && timePos < duration - 1 { endedDuration = 0 }
        }
        if timePos.isFinite && timePos > 0 { liveTimePos = timePos }
        guard endedDuration == 0, liveDuration > 0 else { return }
        let reachedEnd = observation.eofReason == "eof"
            || (observation.idleWithoutFile && liveTimePos >= liveDuration - 5)
        if reachedEnd { endedDuration = liveDuration }
    }
}

class MPVPlayerManager: ObservableObject {
    struct PlaybackEngineSnapshot {
        let playlistIndex: Int
        let timePos: Double
        let duration: Double
        let filename: String?
    }

    private struct TrackDescriptor: Equatable {
        let kind: String
        let language: String
        let title: String
        let codec: String
        let channels: String

        var signature: String {
            [kind, language, title, codec, channels]
                .map(Self.normalized)
                .joined(separator: "\u{1F}")
        }

        private nonisolated static func normalized(_ value: String) -> String {
            value
                .trimmingCharacters(in: .whitespacesAndNewlines)
                .folding(options: [.diacriticInsensitive, .widthInsensitive], locale: .current)
                .lowercased()
        }
    }

    private static let trackPreferenceDefaultsKey = "omniplay.trackPreferences.v1"
    private static let disabledSubtitlePreference = "__off__"

    // mpv 核心句柄。收播时在控制队列上销毁它，销毁前先把句柄从属性上摘走（takeMPVHandle），
    // 这样实例之后什么时候被释放都不会再碰已经销毁的核心。
    private var mpv: OpaquePointer?
    private let mpvHandleLock = NSLock()
    private let mpvControlQueue = DispatchQueue(label: "nan.omniplay.mpv.control")
    private let controlQueueKey = DispatchSpecificKey<Bool>()
    private var lastDrawablePointer: Int64?
    private var pendingDrawablePointer: Int64?
    private var retainedDrawableLayer: CAMetalLayer?
    // 直开播放：播放器先装进窗口把探测与缓冲跑起来，图层等全屏动画落定再绑（见 DirectPlayerHandoff）。
    // 期间来的绑定请求先攒着，落定时一次性补上，mpv 的视频输出就只按最终尺寸建一次。
    weak var drawableHandoff: DirectPlayerHandoff?
    private weak var deferredDrawableView: NSView?

    /// mpv 是否已经拿到图层在出画。还没绑图层时屏幕上根本没有画面，
    /// 尺寸变化不值得为它按一次暂停（直开播放等全屏动画落定期间就是这个状态）。
    var isRenderingVideoOutput: Bool { lastDrawablePointer != nil }
    private var isStopping = false
    private var isRebuildingVideoOutput = false
    // 窗口尺寸切换（拖拽、进出全屏）期间的自动暂停：等视频输出按新尺寸重建完成再自动续播
    private(set) var isRecoveringVideoGeometry = false
    private var geometryRecoveryGeneration = 0
    private var geometryRecoveryLoopRunning = false
    private var geometryRecoveryResumePlay = false
    private var geometryRecoveryReinitSeen = false
    private var geometryRecoveryMatchStreak = 0
    private var geometryRecoveryArmedAt = Date.distantPast
    private var geometryRecoveryDeadline = Date.distantPast
    // 图层还没绑上就先按「无视频轨」加载，图层就绪后靠它把视频轨恢复回来。
    // 这两个都只在控制队列上读写：绑定和加载都在同一条串行队列上，判定才不会有先后 races。
    private var isDrawableBoundOnControlQueue = false
    private var pendingVideoTrackAfterDrawable: String?
    private var loadIssuedAt: Date?
    private var playbackActivity: NSObjectProtocol?
    
    @Published var isPlaying = false {
        didSet {
            guard isPlaying != oldValue else { return }
            updatePlaybackActivity()
        }
    }
    @Published var position: Float = 0.0
    // Cached on the main thread by the state poll. Reading mpv synchronously
    // from SwiftUI can observe a transient zero/NaN while a file is loading or
    // after a seek, which makes the playback timeline appear stuck.
    @Published private(set) var currentTimePosition: Double = 0.0
    @Published var currentTime: String = "00:00"
    @Published var remainingTime: String = "00:00"
    @Published var currentSubtitleSize: Int = 16
    @Published var currentFilename: String = ""
    @Published var currentPlaylistIndex: Int = 0
    @Published var audioTrackNames: [String] = ["默认音轨"]
    @Published var subtitleNames: [String] = ["默认字幕"]
    @Published var drawableBindVersion: Int = 0
    
    // 🌟 新增：追踪当前正在播放的真实轨道 ID，用来在菜单打对号！
    @Published var activeAudioId: Int64 = -1
    @Published var activeSubtitleId: Int64 = -1
    
    // 🌟 Phase 2 新增：时间轴延迟 (秒，正数代表字幕调早，负数代表调晚)
    @Published var subtitleDelay: Double = 0.0
    
    var audioTrackIds: [Int64] = []
    var subtitleIds: [Int64] = []
    private var audioTrackDescriptors: [TrackDescriptor] = []
    private var subtitleTrackDescriptors: [TrackDescriptor] = []
    private var currentTrackPreferenceKey: String?
    private var lastTrackCount: Int64 = 0
    private var hasAppliedDefaultSubtitleForCurrentLoad = false
    private var hasUserSelectedSubtitleTrack = false
    private var timer: Timer?
    private var isPollingState = false
    private var pendingInitialSeekPosition: Double?
    private var pendingInitialSeekFilename: String?
    private var pendingInitialSeekAttempts = 0
    private var pendingInitialSeekDeadline: Date?
    private let initialSeekTimeout: TimeInterval = 8.0
    // mpv applies pause changes asynchronously. Keep the button responsive
    // until the next poll observes the same engine state.
    private var pendingIsPlayingOverride: (value: Bool, until: Date)?
    var duration: Double = 0.0
    // 只在 mpvControlQueue 上读写：播完时引擎属性会读成 0，需要它给出确定结论。
    private var playbackEndTracker = PlaybackEndTracker()
    
    private func log(_ message: String) {
        print("[MPVPlayerManager] \(message)")
    }

    private func traceLifecycle(_ message: String) {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let queue = String(cString: __dispatch_queue_get_label(nil), encoding: .utf8) ?? "unknown"
        let thread = Thread.isMainThread ? "main" : "bg"
        print("[MPVLifecycle][\(formatter.string(from: Date()))][\(thread)][q:\(queue)] \(message)")
        fflush(stdout)
    }

    private func updatePlaybackActivity() {
        let applyActivityState = { [weak self] in
            guard let self else { return }
            if self.isPlaying {
                guard self.playbackActivity == nil else { return }
                self.playbackActivity = ProcessInfo.processInfo.beginActivity(
                    options: [.userInitiated, .idleDisplaySleepDisabled, .idleSystemSleepDisabled],
                    reason: "Video playback in OmniPlay"
                )
                self.traceLifecycle("playback activity started")
            } else if let activity = self.playbackActivity {
                ProcessInfo.processInfo.endActivity(activity)
                self.playbackActivity = nil
                self.traceLifecycle("playback activity ended")
            }
        }

        if Thread.isMainThread {
            applyActivityState()
        } else {
            DispatchQueue.main.async(execute: applyActivityState)
        }
    }
    
    var currentTimePos: Double {
        currentTimePosition
    }

    var playbackEngineSnapshot: PlaybackEngineSnapshot {
        let readSnapshot = { [weak self] in
            guard let self, let mpv = self.mpv else {
                return PlaybackEngineSnapshot(playlistIndex: 0, timePos: 0, duration: 0, filename: nil)
            }
            var timePos: Double = 0.0
            var dur: Double = 0.0
            var playlistPos: Int64 = 0
            mpv_get_property(mpv, "time-pos", MPV_FORMAT_DOUBLE, &timePos)
            mpv_get_property(mpv, "duration", MPV_FORMAT_DOUBLE, &dur)
            mpv_get_property(mpv, "playlist-pos", MPV_FORMAT_INT64, &playlistPos)
            let filename = self.getMpvStringProperty("filename")
            var resolvedTimePos = timePos
            var resolvedDuration = dur
            if resolvedDuration <= 0, self.playbackEndTracker.endedDuration > 0 {
                // 引擎已经播完并清掉了当前文件，进度仍要算成「看到结尾」
                resolvedDuration = self.playbackEndTracker.endedDuration
                resolvedTimePos = self.playbackEndTracker.endedDuration
            }
            return PlaybackEngineSnapshot(
                playlistIndex: max(0, Int(playlistPos)),
                timePos: resolvedTimePos,
                duration: resolvedDuration,
                filename: filename
            )
        }

        if DispatchQueue.getSpecific(key: controlQueueKey) == true {
            return readSnapshot()
        }
        return mpvControlQueue.sync(execute: readSnapshot)
    }
    
    private func runOnControlQueue(_ block: @escaping () -> Void) {
        if DispatchQueue.getSpecific(key: controlQueueKey) == true {
            block()
        } else {
            mpvControlQueue.async(execute: block)
        }
    }

    /// 取走 mpv 句柄并清空属性：调用方从此独占核心并负责销毁它。
    private func takeMPVHandle() -> OpaquePointer? {
        mpvHandleLock.lock()
        defer { mpvHandleLock.unlock() }
        let handle = mpv
        mpv = nil
        return handle
    }

    private func mpvLoadArgument(for url: URL, stripUserInfo: Bool = false) -> String {
        if url.isFileURL {
            return url.path
        }
        if stripUserInfo,
           var components = URLComponents(url: url, resolvingAgainstBaseURL: false) {
            components.user = nil
            components.password = nil
            return components.string ?? url.absoluteString
        }
        return url.absoluteString
    }

    private func mpvStringArgument(_ value: String, stripUserInfo: Bool = false) -> String {
        guard stripUserInfo,
              var components = URLComponents(string: value),
              let scheme = components.scheme,
              scheme.caseInsensitiveCompare("file") != .orderedSame else {
            return value
        }
        components.user = nil
        components.password = nil
        return components.string ?? value
    }

    private func mpvLogArgument(for url: URL) -> String {
        if url.isFileURL {
            return url.path
        }
        guard var components = URLComponents(url: url, resolvingAgainstBaseURL: false) else {
            return url.absoluteString
        }
        components.user = nil
        components.password = nil
        redactSensitiveQueryItems(in: &components)
        return components.string ?? url.absoluteString
    }

    private func mpvLogArgument(for value: String) -> String {
        guard var components = URLComponents(string: value),
              let scheme = components.scheme,
              scheme.caseInsensitiveCompare("file") != .orderedSame else {
            return value
        }
        components.user = nil
        components.password = nil
        redactSensitiveQueryItems(in: &components)
        return components.string ?? value
    }

    private func redactSensitiveQueryItems(in components: inout URLComponents) {
        guard let queryItems = components.queryItems else { return }
        components.queryItems = queryItems.map { item in
            let name = item.name.lowercased()
            if name == "x-plex-token" || name == "api_key" {
                return URLQueryItem(name: item.name, value: "<redacted>")
            }
            return item
        }
    }

    private func plexToken(from urls: [URL]) -> String? {
        for url in urls where !url.isFileURL {
            guard let components = URLComponents(url: url, resolvingAgainstBaseURL: false) else { continue }
            guard let token = components.queryItems?.first(where: {
                $0.name.caseInsensitiveCompare("X-Plex-Token") == .orderedSame
            })?.value?.trimmingCharacters(in: .whitespacesAndNewlines), !token.isEmpty else {
                continue
            }
            return token
        }
        return nil
    }

    private func embyCompatibleToken(from urls: [URL]) -> String? {
        for url in urls where !url.isFileURL {
            guard let components = URLComponents(url: url, resolvingAgainstBaseURL: false) else { continue }
            guard let token = components.queryItems?.first(where: {
                $0.name.caseInsensitiveCompare("api_key") == .orderedSame
            })?.value?.trimmingCharacters(in: .whitespacesAndNewlines), !token.isEmpty else {
                continue
            }
            return token
        }
        return nil
    }

    private func basicAuthorizationHeader(from urls: [URL]) -> String? {
        for url in urls where !url.isFileURL {
            guard let components = URLComponents(url: url, resolvingAgainstBaseURL: false),
                  let username = components.user?.trimmingCharacters(in: .whitespacesAndNewlines),
                  !username.isEmpty else {
                continue
            }
            let password = components.password ?? ""
            guard let data = "\(username):\(password)".data(using: .utf8) else { continue }
            return "Authorization: Basic \(data.base64EncodedString())"
        }
        return nil
    }

    @discardableResult
    private func applyRemotePlaybackHeaders(for urls: [URL]) -> Bool {
        if let header = basicAuthorizationHeader(from: urls) {
            mpv_set_property_string(mpv, "http-header-fields", header)
            log("applied Basic Authorization header for remote playback")
            return true
        }

        if let token = plexToken(from: urls) {
            let headers = [
                "X-Plex-Product: OmniPlay",
                "X-Plex-Version: 1.0",
                "X-Plex-Client-Identifier: omniplay-mac",
                "X-Plex-Device-Name: OmniPlay",
                "X-Plex-Platform: macOS",
                "X-Plex-Token: \(token)"
            ].joined(separator: ",")
            mpv_set_property_string(mpv, "http-header-fields", headers)
            log("applied Plex HTTP headers for remote playback")
            return false
        }

        if let token = embyCompatibleToken(from: urls) {
            let headers = [
                "X-Emby-Token: \(token)",
                "X-MediaBrowser-Token: \(token)"
            ].joined(separator: ",")
            mpv_set_property_string(mpv, "http-header-fields", headers)
            log("applied Jellyfin/Emby HTTP headers for remote playback")
            return false
        }

        mpv_set_property_string(mpv, "http-header-fields", "")
        return false
    }

    private func applyPlaybackQualityModeAsOption() {
        let mode = UserDefaults.standard.string(forKey: "playbackQualityMode") ?? "balanced"
        switch mode {
        case "smooth":
            mpv_set_option_string(mpv, "profile", "fast")
            mpv_set_option_string(mpv, "scale", "bilinear")
            mpv_set_option_string(mpv, "cscale", "bilinear")
            mpv_set_option_string(mpv, "video-sync", "audio")
        case "quality":
            mpv_set_option_string(mpv, "profile", "high-quality")
            mpv_set_option_string(mpv, "scale", "ewa_lanczossharp")
            mpv_set_option_string(mpv, "cscale", "ewa_lanczossoft")
            mpv_set_option_string(mpv, "video-sync", "display-resample")
        default:
            mpv_set_option_string(mpv, "profile", "gpu-hq")
            mpv_set_option_string(mpv, "scale", "spline36")
            mpv_set_option_string(mpv, "cscale", "spline36")
            mpv_set_option_string(mpv, "video-sync", "audio")
        }
    }

    private func applyPlaybackQualityModeAtRuntime() {
        let mode = UserDefaults.standard.string(forKey: "playbackQualityMode") ?? "balanced"
        switch mode {
        case "smooth":
            mpv_command_string(mpv, "apply-profile fast")
            mpv_set_property_string(mpv, "scale", "bilinear")
            mpv_set_property_string(mpv, "cscale", "bilinear")
            mpv_set_property_string(mpv, "video-sync", "audio")
        case "quality":
            mpv_command_string(mpv, "apply-profile high-quality")
            mpv_set_property_string(mpv, "scale", "ewa_lanczossharp")
            mpv_set_property_string(mpv, "cscale", "ewa_lanczossoft")
            mpv_set_property_string(mpv, "video-sync", "display-resample")
        default:
            mpv_command_string(mpv, "apply-profile gpu-hq")
            mpv_set_property_string(mpv, "scale", "spline36")
            mpv_set_property_string(mpv, "cscale", "spline36")
            mpv_set_property_string(mpv, "video-sync", "audio")
        }
    }
    
    init() {
        setenv("GC_DISABLE_GAMECONTROLLER", "1", 1)
        setenv("SDL_JOYSTICK_DISABLE", "1", 1)
        setenv("MVK_CONFIG_LOG_LEVEL", "0", 1)
        
        mpv = mpv_create()
        mpvControlQueue.setSpecific(key: controlQueueKey, value: true)
        mpv_set_option_string(mpv, "hwdec", "videotoolbox")
        applyPlaybackQualityModeAsOption()
        // 这套 libmpv 的 audio-buffer 默认只有 0.2 秒，暂停/恢复时音频输出来不及续上
        // 就是一下破音；播放器不需要低延迟，多给点余量换边界干净
        mpv_set_option_string(mpv, "audio-buffer", "0.5")
        // 指定音频输出顺序：走 AVFoundation，coreaudio 只作兜底。
        // 原因：libmpv 的 coreaudio 驱动在 CoreAudio 设备变动（插拔耳机、切默认输出、
        // 蓝牙耳机连断）时会在 HAL 的通知队列里回调 hotplug_cb，而它给监听器登记的
        // ao 上下文可能已经被拆掉（init/hotplug_init 各注册一次、只反注册一次），
        // 于是读 ao->log 解引用空指针直接 SIGSEGV。mpv 上游目前没修。
        mpv_set_option_string(mpv, "ao", "avfoundation,coreaudio")
        mpv_set_option_string(mpv, "target-colorspace-hint", "yes")
        mpv_set_option_string(mpv, "vo", "gpu-next")
        mpv_set_option_string(mpv, "gpu-api", "metal")
        mpv_set_option_string(mpv, "osc", "no")
        mpv_set_option_string(mpv, "osd-bar", "no")
        mpv_set_option_string(mpv, "osd-on-seek", "no")
        
        // 🌟 核心升级：接管默认轨道优先级逻辑
        let defaultAudio = UserDefaults.standard.string(forKey: "defaultAudio") ?? "auto"
        if defaultAudio != "auto" { mpv_set_option_string(mpv, "alang", "\(defaultAudio),eng,chi") }
        
        let defaultSub = UserDefaults.standard.string(forKey: "defaultSub") ?? "chi"
        if defaultSub == "no" {
            mpv_set_option_string(mpv, "subs-fallback", "no")
        } else {
            // 简繁体中文全面兜底支持
            let subFallback = defaultSub == "chi"
                ? "zh-hans,zh-cn,zh-sg,chi,zho,zh,cmn,zh-hant,zh-tw,zh-hk,zh-mo,eng,en"
                : "eng,en,zh-hans,zh-cn,chi,zho,zh"
            mpv_set_option_string(mpv, "slang", subFallback)
        }
        
        mpv_initialize(mpv)
        timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in self?.updatePlaybackState() }
    }

    deinit {
        timer?.invalidate()
        timer = nil
        if let activity = playbackActivity {
            ProcessInfo.processInfo.endActivity(activity)
            playbackActivity = nil
        }
        // stop() 已经把句柄摘走并在控制队列上销毁了，这里只会是空转；
        // 兜的是「从没走到 stop 就被释放」那种情况。
        guard let mpv = takeMPVHandle() else { return }
        // 播放页一拆，那层 CAMetalLayer 就没了，而 mpv 是按裸指针（wid）认它的。
        // 不销毁核心，MoltenVK 的 swapchain 还指着已释放的图层，下一次 GPU 完成回调
        // 就踩在脏内存上（崩溃点固定在 MVKCommandEncoder::finishQueries）。
        // 收的顺序：停播 → 摘掉视频输出 → 销毁核心，顺带不再漏一整个 mpv 实例。
        mpv_command_string(mpv, "stop")
        mpv_set_property_string(mpv, "vo", "null")
        mpv_terminate_destroy(mpv)
    }
    
    func setDrawable(_ view: NSView, force: Bool = false) {
        guard !isStopping else {
            traceLifecycle("setDrawable skipped because isStopping=true")
            return
        }
        log("setDrawable called view=\(type(of: view))")
        if !Thread.isMainThread {
            DispatchQueue.main.async { [weak self, weak view] in
                guard let self, let view else { return }
                self.setDrawable(view, force: force)
            }
            return
        }
        guard let layer = view.layer as? CAMetalLayer else { return }
        if let handoff = drawableHandoff, !handoff.isSettled {
            deferredDrawableView = view
            traceLifecycle("setDrawable 延后：等全屏动画落定再绑图层")
            return
        }
        let pointer = Unmanaged.passUnretained(layer).toOpaque()
        let wid = Int64(Int(bitPattern: pointer))
        retainedDrawableLayer = layer
        guard force || lastDrawablePointer != wid else { return }
        guard force || pendingDrawablePointer != wid else { return }
        pendingDrawablePointer = wid
        runOnControlQueue { [weak self] in
            guard let self else { return }
            guard let mpv = self.mpv else {
                DispatchQueue.main.async {
                    if self.pendingDrawablePointer == wid {
                        self.pendingDrawablePointer = nil
                    }
                }
                return
            }
            var mutableWid = wid
            let result = mpv_set_property(mpv, "wid", MPV_FORMAT_INT64, &mutableWid)
            self.log("setDrawable wid=\(wid) result=\(result)")
            if result >= 0 {
                self.isDrawableBoundOnControlQueue = true
                // 账本和引擎实际状态认一个就行：视频轨还摘着就必须放回来。
                // 只认账本的话，起播时那次续播进度 seek 会顺手把账本清掉，
                // 结果图层绑上了、视频轨却没放回来，整场只有声音和黑屏。
                let vidBeforeBind = Self.stringProperty(mpv, "vid")
                if self.pendingVideoTrackAfterDrawable != nil
                    || (vidBeforeBind == "no" && !self.isRebuildingVideoOutput) {
                    self.pendingVideoTrackAfterDrawable = nil
                    mpv_set_property_string(mpv, "vid", "auto")
                    self.log("图层已绑定，补上视频轨（绑定前 vid=\(vidBeforeBind ?? "nil")）")
                }
            }
            DispatchQueue.main.async {
                if self.pendingDrawablePointer == wid {
                    self.pendingDrawablePointer = nil
                }
                if result >= 0 {
                    self.lastDrawablePointer = wid
                    self.drawableBindVersion += 1
                }
            }
        }
    }
    
    /// 全屏动画落定（或超时兜底）：补上之前延后的图层绑定。
    func allowDeferredDrawableBinding() {
        guard let view = deferredDrawableView else { return }
        deferredDrawableView = nil
        traceLifecycle("全屏动画落定，补绑图层")
        setDrawable(view, force: true)
    }

    /// 全屏与窗口来回切时，gpu-next 在「只有一层 CAMetalLayer、没有真窗口」这条嵌入路径上
    /// 只在视频输出初始化那一刻读一次图层尺寸：之后光改 drawableSize、甚至把同一个 wid 再设一遍
    /// 它都不理，于是退出全屏后仍按全屏尺寸出画（画面只剩一角），从小窗进全屏又只画左上角。
    /// 实测（libmpv 0.41 独立探针）只有让它真正经历一段「没有视频轨」的中间状态，重建时才会重新
    /// 读尺寸：所以 vid=no 和恢复轨道之间必须隔开一段真实时间，挨在一起会被 mpv 合成线程
    /// 当成「没变化」，等于什么都没做。
    /// 这段间隔只能异步等：在控制队列上 sleep 会连带堵住主线程的同步读（engineSnapshot 走
    /// mpvControlQueue.sync）和暂停命令，按空格就成了卡死。
    func reinitVideoOutput(reason: String) {
        reinitVideoOutput(reason: reason, attempt: 0)
    }

    private func reinitVideoOutput(reason: String, attempt: Int) {
        guard !isStopping, !isRebuildingVideoOutput, let layer = retainedDrawableLayer else { return }
        runOnControlQueue { [weak self] in
            guard let self, let mpv = self.mpv else { return }
            let expected = Self.drawablePixelSize(layer)
            // mpv 还没画面（没加载、刚 eof）：没有需要纠偏的视频输出
            guard let rendered = Self.renderedOutputSize(mpv) else {
                // 图层明明绑上了却拿不到输出尺寸，八成是视频轨还摘着（延后绑图层时先按
                // 「无视频轨」打开）：就地补回轨道，别留下「有声音 + 黑屏」的一整场。
                if !self.isRebuildingVideoOutput, self.isDrawableBoundOnControlQueue,
                   Self.stringProperty(mpv, "vid") == "no" {
                    mpv_set_property_string(mpv, "vid", "auto")
                    self.log("重建视频输出 \(reason)：图层已绑但视频轨还摘着，补回 vid=auto")
                } else {
                    self.log("重建视频输出 \(reason) 跳过：mpv 暂无输出尺寸")
                }
                return
            }
            // 尺寸本来就对（刚 loadfile 完、或上一次重建已生效）就别白折腾一次解码器重建
            if abs(rendered.width - expected.width) <= 2, abs(rendered.height - expected.height) <= 2 {
                self.log("重建视频输出 \(reason) 跳过：mpv 输出已是 \(Int(rendered.width))x\(Int(rendered.height))")
                return
            }
            // 首次加载还没出画面时不重建：vid 写入是同步命令，会排在探测/解码后面把
            // 控制队列占住，主线程读进度就跟着转圈。等出画面再说。
            if Self.isStillLoading(mpv), attempt < 12 {
                self.log("重建视频输出 \(reason) 延后：还在加载（第\(attempt + 1)次）")
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) {
                    self.reinitVideoOutput(reason: reason, attempt: attempt + 1)
                }
                return
            }
            self.beginGeometryRecoveryForRebuild(mpv: mpv, reason: reason)
            self.isRebuildingVideoOutput = true
            // 多视频轨的片子（画中画等）要把当前选的轨原样还回去，不能一律退回 auto
            let restore = Self.videoTrackSelectionValue(Self.currentVideoTrackProperty(mpv))
            let closedAt = Date()
            mpv_set_property_string(mpv, "vid", "no")
            self.log("重建视频输出 reason=\(reason) 先关视频轨 restore=\(restore) 图层=\(Int(expected.width))x\(Int(expected.height)) "
                + "mpv原输出=\(Int(rendered.width))x\(Int(rendered.height)) 关轨耗时=\(Int(Date().timeIntervalSince(closedAt) * 1000))ms")
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) {
                self.finishVideoOutputReinit(reason: reason, restore: restore, layer: layer, attempt: 1)
            }
        }
    }

    /// 重建的后半程：主线程坐实图层尺寸 → 控制队列确认 mpv 真的落到「无视频轨」再恢复轨道。
    /// 没落到就再等一拍，最多三次，避免两次写又被挤成一次「没变化」。
    private func finishVideoOutputReinit(reason: String, restore: String, layer: CAMetalLayer, attempt: Int) {
        DispatchQueue.main.async {
            guard !self.isStopping else {
                self.isRebuildingVideoOutput = false
                return
            }
            // 恢复轨道前重写一次图层尺寸：系统会在 resize 过程中把 drawableSize 短暂写成很小的值
            Self.reassertLayerGeometry(layer)
            self.runOnControlQueue {
                guard let mpv = self.mpv, !self.isStopping else {
                    self.isRebuildingVideoOutput = false
                    return
                }
                let observed = Self.currentVideoTrackProperty(mpv) == "no"
                if !observed && attempt < 3 {
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) {
                        self.finishVideoOutputReinit(reason: reason, restore: restore, layer: layer, attempt: attempt + 1)
                    }
                    return
                }
                let restoredAt = Date()
                let result = mpv_set_property_string(mpv, "vid", restore)
                self.log("重建视频输出 reason=\(reason) 恢复 observed=\(observed) attempt=\(attempt) result=\(result) "
                    + "恢复耗时=\(Int(Date().timeIntervalSince(restoredAt) * 1000))ms "
                    + "即时输出=\(Self.stringProperty(mpv, "osd-dimensions") ?? "<nil>")（重建是异步的，这一项稍后才更新）")
                self.isRebuildingVideoOutput = false
            }
        }
    }

    /// 窗口尺寸要变了（拖拽改大小、进出全屏）：正在播就先暂停，交给重建完成后自动续播。
    /// 原来是固定延时 0.5 秒就续播，比重建本身还早，等于先把错比例的画面放出来。
    func pauseForGeometryChange(reason: String) {
        guard !isStopping else { return }
        runOnControlQueue { [weak self] in
            guard let self, let mpv = self.mpv else { return }
            if self.isRecoveringVideoGeometry {
                self.geometryRecoveryDeadline = Date().addingTimeInterval(4)
                return
            }
            self.armGeometryRecovery(mpv: mpv, reason: reason)
        }
    }

    /// 重建已经确定要做：先补一次暂停，再进入「等输出尺寸对上图层」的轮询
    private func beginGeometryRecoveryForRebuild(mpv: OpaquePointer, reason: String) {
        if !isRecoveringVideoGeometry {
            armGeometryRecovery(mpv: mpv, reason: reason)
        }
        geometryRecoveryReinitSeen = true
        geometryRecoveryMatchStreak = 0
        geometryRecoveryDeadline = Date().addingTimeInterval(4)
    }

    /// 只能在控制队列上调用
    private func armGeometryRecovery(mpv: OpaquePointer, reason: String) {
        let wasPlaying = Self.stringProperty(mpv, "pause") != "yes"
        geometryRecoveryGeneration += 1
        geometryRecoveryResumePlay = wasPlaying
        geometryRecoveryReinitSeen = false
        geometryRecoveryMatchStreak = 0
        geometryRecoveryArmedAt = Date()
        geometryRecoveryDeadline = Date().addingTimeInterval(4)
        isRecoveringVideoGeometry = true
        if wasPlaying {
            mpv_set_property_string(mpv, "pause", "yes")
            DispatchQueue.main.async {
                self.pendingIsPlayingOverride = (false, Date().addingTimeInterval(2))
                self.isPlaying = false
            }
        }
        log("尺寸切换 \(reason)：先暂停，等视频输出按新尺寸重建完成后续播")
        guard !geometryRecoveryLoopRunning else { return }
        geometryRecoveryLoopRunning = true
        runGeometryRecoveryTick(generation: geometryRecoveryGeneration)
    }

    /// 每 0.15 秒问一次 mpv 的输出尺寸；连续两次对上图层尺寸才续播，最长等 4 秒
    private func runGeometryRecoveryTick(generation: Int) {
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) {
            guard !self.isStopping, self.isRecoveringVideoGeometry, generation == self.geometryRecoveryGeneration else {
                self.geometryRecoveryLoopRunning = false
                return
            }
            self.runOnControlQueue {
                guard let mpv = self.mpv, !self.isStopping else {
                    self.geometryRecoveryLoopRunning = false
                    self.finishGeometryRecovery(note: "播放器已停止")
                    return
                }
                let elapsed = Date().timeIntervalSince(self.geometryRecoveryArmedAt)
                let rendered = Self.renderedOutputSize(mpv)
                if self.isRebuildingVideoOutput {
                    self.geometryRecoveryMatchStreak = 0
                } else if let layer = self.retainedDrawableLayer, let rendered {
                    let expected = Self.drawablePixelSize(layer)
                    let matched = abs(rendered.width - expected.width) <= 2 && abs(rendered.height - expected.height) <= 2
                    self.geometryRecoveryMatchStreak = matched ? self.geometryRecoveryMatchStreak + 1 : 0
                } else {
                    self.geometryRecoveryMatchStreak = 0
                }
                let settled = elapsed >= 0.6 && self.geometryRecoveryMatchStreak >= 2
                let nothingToFix = elapsed >= 1.2 && !self.geometryRecoveryReinitSeen
                    && !self.isRebuildingVideoOutput && rendered == nil
                let timedOut = Date() >= self.geometryRecoveryDeadline
                if settled || nothingToFix || timedOut {
                    let note = settled ? "输出尺寸已跟上图层" : (nothingToFix ? "没有需要纠偏的画面" : "等待超时")
                    self.log("尺寸切换续播：\(note)，暂停了 \(Int(elapsed * 1000))ms")
                    self.geometryRecoveryLoopRunning = false
                    self.finishGeometryRecovery(note: note)
                    return
                }
                self.runGeometryRecoveryTick(generation: generation)
            }
        }
    }

    /// 只有「暂停是我们按的、期间用户没自己动过播放键」才自动续播
    private func finishGeometryRecovery(note: String) {
        guard isRecoveringVideoGeometry else { return }
        isRecoveringVideoGeometry = false
        let didRebuild = geometryRecoveryReinitSeen
        geometryRecoveryReinitSeen = false
        geometryRecoveryMatchStreak = 0
        guard geometryRecoveryResumePlay else { return }
        geometryRecoveryResumePlay = false
        if let mpv {
            // 重建过视频输出的，音频管线里还压着旧尺寸那段时间的音频，直接续播会音画错位。
            // 停在原地做一次精确 seek，把音视频两条管线一起冲干净（位置不变，实测可接受）。
            if didRebuild, let raw = Self.stringProperty(mpv, "playback-time"),
               let position = Double(raw) {
                let rc = mpv_command_string(mpv, "seek \(position) absolute+exact")
                if rc != 0 {
                    log("尺寸切换续播：原地 seek 未执行（\(String(cString: mpv_error_string(rc)))），音画可能短暂错位")
                }
            }
            mpv_set_property_string(mpv, "pause", "no")
        }
        DispatchQueue.main.async {
            self.pendingIsPlayingOverride = (true, Date().addingTimeInterval(0.75))
            self.isPlaying = true
        }
    }

    private static func drawablePixelSize(_ layer: CAMetalLayer) -> CGSize {
        CGSize(width: layer.drawableSize.width.rounded(), height: layer.drawableSize.height.rounded())
    }

    /// mpv 当前视频输出的像素尺寸（osd-dimensions 里的 w/h）
    private static func renderedOutputSize(_ mpv: OpaquePointer?) -> CGSize? {
        guard let json = stringProperty(mpv, "osd-dimensions"),
              let data = json.data(using: .utf8),
              let node = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let width = node["w"] as? Int, let height = node["h"] as? Int,
              width > 1, height > 1 else { return nil }
        return CGSize(width: width, height: height)
    }

    /// 已经 loadfile 但还没出第一帧：time-pos 读不到，而播放器又不是空闲状态
    private static func isStillLoading(_ mpv: OpaquePointer?) -> Bool {
        guard stringProperty(mpv, "idle-active") != "yes" else { return false }
        return stringProperty(mpv, "time-pos") == nil
    }

    /// 用图层当前 bounds 重算并写回 drawableSize（主线程调用）
    private static func reassertLayerGeometry(_ layer: CAMetalLayer) {
        let size = layer.bounds.size
        guard size.width > 1, size.height > 1 else { return }
        let scale = layer.contentsScale > 0 ? layer.contentsScale : (NSScreen.main?.backingScaleFactor ?? 2)
        layer.drawableSize = CGSize(width: (size.width * scale).rounded(), height: (size.height * scale).rounded())
    }

    private static func drawableSizeString(_ layer: CAMetalLayer) -> String {
        "\(Int(layer.drawableSize.width))x\(Int(layer.drawableSize.height))"
    }

    /// 读 mpv 的字符串属性，内部负责释放返回的 C 字符串
    private static func currentVideoTrackProperty(_ mpv: OpaquePointer?) -> String? {
        stringProperty(mpv, "vid")
    }

    static func stringProperty(_ mpv: OpaquePointer?, _ name: String) -> String? {
        guard let raw = mpv_get_property_string(mpv, name) else { return nil }
        let value = String(cString: raw)
        mpv_free(UnsafeMutableRawPointer(raw))
        return value
    }

    /// vid 的取值是 "auto"/"no" 或轨道 ID：只有正数 ID 值得原样恢复，其余一律回 auto
    static func videoTrackSelectionValue(_ raw: String?) -> String {
        guard let raw, let id = Int64(raw), id > 0 else { return "auto" }
        return String(id)
    }

    func loadFiles(
        urls: [URL],
        startPosition: Double = 0.0,
        isBluRay: Bool = false,
        blurayRootPath: String? = nil,
        trackPreferenceKey: String? = nil
    ) {
        guard !urls.isEmpty else { return }
        isStopping = false
        if timer == nil {
            timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
                self?.updatePlaybackState()
            }
        }
        let hasDrawable = drawableBindVersion > 0
        log("loadFiles urls=\(urls.count) first=\(mpvLogArgument(for: urls[0])) start=\(startPosition) isBluRay=\(isBluRay) blurayRoot=\(blurayRootPath.map { mpvLogArgument(for: $0) } ?? "nil") drawable=\(hasDrawable)")
        hasAppliedDefaultSubtitleForCurrentLoad = false
        hasUserSelectedSubtitleTrack = false
        currentTrackPreferenceKey = trackPreferenceKey
        audioTrackDescriptors = []
        subtitleTrackDescriptors = []
        let firstName = urls[0].lastPathComponent.lowercased()
        let isDolbyVisionLike = firstName.contains("dvhe.05")
            || firstName.contains("dovi")
            || firstName.contains("dolby vision")
            || firstName.contains("dolby.vision")
            || firstName.contains(".dv.")
            || firstName.hasSuffix(".dv.mkv")
            || firstName.contains("-dv-")
            || firstName.hasPrefix("dv.")
            || firstName.contains("profile 5")
        runOnControlQueue { [weak self] in
            guard let self else { return }
            // stop() 会把视频输出摘掉（播放页拆了不能还往旧图层画），复用同一个实例
            // 再播时先装回来；值没变时 mpv 内部会自己比掉，不会多重建一次
            mpv_set_property_string(self.mpv, "vo", "gpu-next")
            self.playbackEndTracker.reset()
            // 单文件播放才让 mpv 停在结尾（保住最后一帧和结尾的 time-pos）；
            // 多文件播放表和蓝光标题要照常自动切下一个，不能被 keep-open 拦下。
            let keepOpen = (urls.count == 1 && !isBluRay) ? "yes" : "no"
            let keepOpenResult = mpv_set_property_string(self.mpv, "options/keep-open", keepOpen)
            if keepOpenResult < 0 {
                self.log("设置 options/keep-open=" + keepOpen + " 失败：" + String(cString: mpv_error_string(keepOpenResult)))
            }
            self.applyPlaybackQualityModeAtRuntime()
            mpv_set_property_string(self.mpv, "vd-lavc-o", isDolbyVisionLike ? "enable_dovi=0" : "")
            mpv_set_property_string(self.mpv, "hwdec", "videotoolbox")
            let shouldStripUserInfo = self.applyRemotePlaybackHeaders(for: urls)
            self.log("decode profile isDVLike=\(isDolbyVisionLike) hwdec=videotoolbox")
            self.pendingInitialSeekPosition = startPosition > 0 ? startPosition : nil
            // bd:// exposes an internal playlist filename that does not match
            // the source m2ts path, so filename matching would discard a valid
            // resume seek for Blu-ray folders/ISO images.
            self.pendingInitialSeekFilename = (startPosition > 0 && urls[0].isFileURL && !isBluRay) ? urls[0].lastPathComponent : nil
            self.pendingInitialSeekAttempts = 0
            self.pendingInitialSeekDeadline = startPosition > 0
                ? Date().addingTimeInterval(self.initialSeekTimeout)
                : nil
            mpv_set_property_string(self.mpv, "start", "0")
            if self.isDrawableBoundOnControlQueue {
                self.pendingVideoTrackAfterDrawable = nil
            } else {
                // 图层还没绑上：先按「没有视频轨」打开，容器探测和音频缓冲就能跟
                // 建窗口、绑图层并行；setDrawable 成功后立刻把视频轨放回来。
                self.pendingVideoTrackAfterDrawable = "auto"
                mpv_set_property_string(self.mpv, "vid", "no")
            }
            self.loadIssuedAt = Date()

            if isBluRay, let rootPath = blurayRootPath {
                let rootArgument = self.mpvStringArgument(rootPath, stripUserInfo: shouldStripUserInfo)
                mpv_set_property_string(self.mpv, "bluray-device", rootArgument)
                self.executeMpvCommand(["loadfile", "bd://", "replace"])
            } else {
                var firstLoadCommand = ["loadfile", self.mpvLoadArgument(for: urls[0], stripUserInfo: shouldStripUserInfo), "replace"]
                if startPosition > 0 {
                    firstLoadCommand.append(contentsOf: ["-1", String(format: "start=%.3f", startPosition)])
                }
                self.executeMpvCommand(firstLoadCommand)
                for url in urls.dropFirst() {
                    self.executeMpvCommand(["loadfile", self.mpvLoadArgument(for: url, stripUserInfo: shouldStripUserInfo), "append"])
                }
            }
            
            let defaultSub = UserDefaults.standard.string(forKey: "defaultSub") ?? "chi"
            if defaultSub == "no" { mpv_set_property_string(self.mpv, "sid", "no") } else { mpv_set_property_string(self.mpv, "sid", "auto") }
            
            DispatchQueue.main.async {
                self.isPlaying = true
                self.currentTimePosition = 0
                self.position = 0
                self.currentTime = "00:00"
                self.remainingTime = "00:00"
                self.duration = 0
                self.lastTrackCount = 0
                self.currentPlaylistIndex = 0
            }
        }
    }
    
    func executeMpvCommand(_ cmd: [String]) {
        runOnControlQueue { [weak self] in
            guard let self else { return }
            if let first = cmd.first, first == "loadfile" || first == "playlist-next" {
                if first == "loadfile", cmd.count >= 2, let url = URL(string: cmd[1]), !url.isFileURL,
                   var components = URLComponents(url: url, resolvingAgainstBaseURL: false) {
                    components.user = nil
                    components.password = nil
                    self.redactSensitiveQueryItems(in: &components)
                    var masked = cmd
                    masked[1] = components.string ?? cmd[1]
                    self.log("executeMpvCommand \(masked.joined(separator: " "))")
                } else {
                    self.log("executeMpvCommand \(cmd.joined(separator: " "))")
                }
            }
            if cmd.first == "playlist-next" {
                mpv_set_property_string(self.mpv, "start", "0")
            }
            var cCmd: [UnsafePointer<CChar>?] = cmd.map { UnsafePointer(strdup($0)) }
            cCmd.append(nil)
            mpv_command(self.mpv, &cCmd)
            for ptr in cCmd.dropLast() { if let p = ptr { free(UnsafeMutableRawPointer(mutating: p)) } }
        }
    }
    
    func playOrPause() {
        guard Thread.isMainThread else {
            DispatchQueue.main.async { [weak self] in self?.playOrPause() }
            return
        }
        // Decide the target from the published state on the main thread. The
        // mpv `pause` property can lag behind a previous command, so deriving
        // a toggle from it may issue the wrong transition and make the button
        // flicker when the next poll arrives.
        let targetIsPlaying = !isPlaying
        geometryRecoveryResumePlay = false
        pendingIsPlayingOverride = (targetIsPlaying, Date().addingTimeInterval(0.75))
        isPlaying = targetIsPlaying
        runOnControlQueue { [weak self] in
            guard let self else { return }
            mpv_set_property_string(self.mpv, "pause", targetIsPlaying ? "no" : "yes")
        }
    }

    func setPaused(_ paused: Bool) {
        guard !isStopping else { return }
        geometryRecoveryResumePlay = false
        DispatchQueue.main.async {
            self.pendingIsPlayingOverride = (!paused, Date().addingTimeInterval(0.75))
            self.isPlaying = !paused
        }
        runOnControlQueue { [weak self] in
            guard let self else { return }
            mpv_set_property_string(self.mpv, "pause", paused ? "yes" : "no")
            DispatchQueue.main.async {
                self.pendingIsPlayingOverride = (!paused, Date().addingTimeInterval(0.75))
                self.isPlaying = !paused
            }
        }
    }

    func stop() {
        guard !isStopping else { return }
        traceLifecycle("stop begin")
        isStopping = true
        timer?.invalidate()
        timer = nil

        // 句柄先摘走：之后这个实例什么时候被释放都跟 mpv 核心无关了。
        guard let handle = takeMPVHandle() else {
            DispatchQueue.main.async { self.releaseDrawableReferences() }
            return
        }

        // 停播 → 摘视频输出 → 销毁核心，整段留在控制队列上做强引用执行。
        // mpv_terminate_destroy 要等 mpv 自己的线程收工，网络源的 demuxer 能拖上百毫秒，
        // 以前这事发生在主线程的 deinit 里（播放页拆掉的那一刻同步执行），
        // 表现就是「关掉播放页先卡一下，首页才回来」。
        mpvControlQueue.async { [self] in
            let stoppedAt = Date()
            mpv_command_string(handle, "stop")
            // 先让 mpv 不再碰图层，再放掉我们手里的图层强引用；反过来就有一段
            // 「vo 还在画、图层已经被 AppKit 收走」的空档，崩的就是那里
            let unloadResult = mpv_set_property_string(handle, "vo", "null")
            let unloadedAt = Date()
            mpv_terminate_destroy(handle)
            traceLifecycle("stop 控制队列收尾：stop+vo=null result=\(unloadResult) 耗时 \(Int(unloadedAt.timeIntervalSince(stoppedAt) * 1000))ms，销毁核心耗时 \(Int(Date().timeIntervalSince(unloadedAt) * 1000))ms")
            DispatchQueue.main.async { [weak self] in
                self?.releaseDrawableReferences()
            }
        }

        DispatchQueue.main.async {
            self.pendingIsPlayingOverride = nil
            self.isRecoveringVideoGeometry = false
            self.geometryRecoveryResumePlay = false
            self.pendingVideoTrackAfterDrawable = nil
            self.isPlaying = false
            self.traceLifecycle("stop main-thread cleanup done")
        }
    }

    /// vo 已经摘掉之后才放掉图层的强引用：图层是播放页的 backing layer，
    /// mpv 拿的是裸指针，我们必须保证它比视频输出活得久。
    private func releaseDrawableReferences() {
        lastDrawablePointer = nil
        pendingDrawablePointer = nil
        retainedDrawableLayer = nil
    }
    
    func captureCurrentFrameAsThumbnail(for fileId: String) {
        let destURL = ThumbnailManager.shared.thumbDirectory.appendingPathComponent("\(fileId).jpg")
        try? FileManager.default.removeItem(at: destURL)
        runOnControlQueue { [weak self] in
            guard let self else { return }
            let shotCmd = ["screenshot-to-file", destURL.path, "window"]
            var cCmd: [UnsafePointer<CChar>?] = shotCmd.map { UnsafePointer(strdup($0)) }
            cCmd.append(nil)
            mpv_command(self.mpv, &cCmd)
            for ptr in cCmd.dropLast() {
                if let p = ptr {
                    free(UnsafeMutableRawPointer(mutating: p))
                }
            }
        }
        DispatchQueue.global(qos: .userInitiated).async { var isWritten = false; for _ in 0..<30 { Thread.sleep(forTimeInterval: 0.1); if let attr = try? FileManager.default.attributesOfItem(atPath: destURL.path), let size = attr[.size] as? Int64, size > 1000 { isWritten = true; break } }; if isWritten { DispatchQueue.main.async { NotificationCenter.default.post(name: NSNotification.Name("ThumbnailGenerated_\(fileId)"), object: nil) } } }
    }
    
    func setPosition(_ newPosition: Float) {
        let targetTime = Double(newPosition) * duration
        runOnControlQueue { [weak self] in
            guard let self else { return }
            self.clearPendingInitialSeek()
            mpv_command_string(self.mpv, String(format: "seek %.2f absolute", targetTime))
        }
    }

    func seekAbsolute(seconds: Double) {
        runOnControlQueue { [weak self] in
            guard let self else { return }
            self.clearPendingInitialSeek()
            mpv_command_string(self.mpv, String(format: "seek %.2f absolute", max(0, seconds)))
        }
    }

    func playPlaylistIndex(_ index: Int, startPosition: Double = 0) {
        runOnControlQueue { [weak self] in
            guard let self else { return }
            self.clearPendingInitialSeek()
            mpv_command_string(self.mpv, "playlist-play-index \(max(0, index))")
            if startPosition > 0 {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) {
                    self.seekAbsolute(seconds: startPosition)
                }
            }
        }
    }

    func seekRelative(seconds: Double) {
        runOnControlQueue { [weak self] in
            guard let self else { return }
            self.clearPendingInitialSeek()
            mpv_command_string(self.mpv, String(format: "seek %.2f relative", seconds))
        }
    }

    private func clearPendingInitialSeek() {
        // 「图层绑上之后要把视频轨放回来」不属于 seek 的账：延后绑图层期间，
        // 起播进度 seek 往往比图层绑定先到，在这里清掉就等于永远不放视频轨了。
        loadIssuedAt = nil
        pendingInitialSeekPosition = nil
        pendingInitialSeekFilename = nil
        pendingInitialSeekAttempts = 0
        pendingInitialSeekDeadline = nil
    }

    private func applyPendingInitialSeekIfNeeded(timePos: Double, duration: Double, playlistPos: Int64, filename: String?) {
        guard let target = pendingInitialSeekPosition else { return }
        if let deadline = pendingInitialSeekDeadline, Date() >= deadline {
            log("initial resume seek expired before media became ready")
            clearPendingInitialSeek()
            return
        }
        guard playlistPos <= 0 else {
            clearPendingInitialSeek()
            return
        }
        if let expected = pendingInitialSeekFilename,
           let current = filename,
           !current.isEmpty,
           current != expected {
            pendingInitialSeekAttempts += 1
            if pendingInitialSeekAttempts > 40 {
                clearPendingInitialSeek()
            }
            return
        }

        let requestedTarget = max(target, 0)
        guard requestedTarget > 0 else {
            clearPendingInitialSeek()
            return
        }

        if timePos.isFinite && abs(timePos - requestedTarget) <= 1.5 {
            clearPendingInitialSeek()
            return
        }

        guard duration.isFinite && duration > 0 else {
            pendingInitialSeekAttempts += 1
            if pendingInitialSeekAttempts > 40 {
                clearPendingInitialSeek()
            }
            return
        }

        pendingInitialSeekAttempts += 1
        guard pendingInitialSeekAttempts <= 40 else {
            clearPendingInitialSeek()
            return
        }

        let targetForSeek = min(requestedTarget, max(duration - 2.0, 0))
        guard targetForSeek > 0 else {
            clearPendingInitialSeek()
            return
        }
        log(String(format: "apply initial resume seek once %.2f / %.2f attempt=%d", targetForSeek, duration, pendingInitialSeekAttempts))
        mpv_command_string(mpv, String(format: "seek %.2f absolute", targetForSeek))
        // mpv accepts seek asynchronously. Do not issue the same seek from every
        // state-poll tick: remote HTTP/ISO streams can report time-pos=0 for a
        // while after the request, and repeating it makes playback jump back.
        clearPendingInitialSeek()
    }

    func setSubtitleSize(_ size: Int) {
        DispatchQueue.main.async {
            self.currentSubtitleSize = size
        }
        runOnControlQueue { [weak self] in
            guard let self else { return }
            mpv_command_string(self.mpv, String(format: "set sub-scale %.2f", Double(size) / 16.0))
        }
    }
    
    private func updatePlaybackState() {
        guard !isStopping else { return }
        guard !isPollingState else { return }
        isPollingState = true

        mpvControlQueue.async { [weak self] in
            guard let self else { return }
            guard let mpv = self.mpv else {
                DispatchQueue.main.async { self.isPollingState = false }
                return
            }

            var timePos: Double = 0.0
            var dur: Double = 0.0
            var currentTrackCount: Int64 = 0
            var aid: Int64 = -1
            var sid: Int64 = -1
            var delay: Double = 0.0
            var playlistPos: Int64 = 0
            var isPaused: Int32 = 0
            var isIdleActive: Int32 = 0

            mpv_get_property(mpv, "time-pos", MPV_FORMAT_DOUBLE, &timePos)
            mpv_get_property(mpv, "duration", MPV_FORMAT_DOUBLE, &dur)
            mpv_get_property(mpv, "track-list/count", MPV_FORMAT_INT64, &currentTrackCount)
            mpv_get_property(mpv, "aid", MPV_FORMAT_INT64, &aid)
            mpv_get_property(mpv, "sid", MPV_FORMAT_INT64, &sid)
            mpv_get_property(mpv, "sub-delay", MPV_FORMAT_DOUBLE, &delay)
            mpv_get_property(mpv, "playlist-pos", MPV_FORMAT_INT64, &playlistPos)
            mpv_get_property(mpv, "pause", MPV_FORMAT_FLAG, &isPaused)
            mpv_get_property(mpv, "idle-active", MPV_FORMAT_FLAG, &isIdleActive)
            let filename = self.getMpvStringProperty("filename")
            let eofReason = self.getMpvStringProperty("eof-reason")
            self.playbackEndTracker.observe(PlaybackEndTracker.Observation(
                duration: dur,
                timePos: timePos,
                eofReason: eofReason,
                idleWithoutFile: isIdleActive == 1 && (filename?.isEmpty ?? true)
            ))
            if let startedAt = self.loadIssuedAt, dur.isFinite, dur > 0 {
                self.loadIssuedAt = nil
                self.log(String(format: "起播计时：loadfile→拿到时长 %.0fms", Date().timeIntervalSince(startedAt) * 1000))
            }
            self.applyPendingInitialSeekIfNeeded(timePos: timePos, duration: dur, playlistPos: playlistPos, filename: filename)

            DispatchQueue.main.async {
                defer { self.isPollingState = false }
                let validTimePos = timePos.isFinite && timePos >= 0 ? timePos : self.currentTimePosition
                let validDuration = dur.isFinite && dur > 0 ? dur : 0
                self.currentTimePosition = validTimePos
                // `idle` is not a reliable playback-state signal for bd:// and
                // some remote/ISO sources: it may remain set while the active
                // file is still playing. The pause property is authoritative
                // for the play/pause button.
                // 播放结束后 mpv 会回到 idle 且不再有任何当前文件，此时必须复位
                // isPlaying，否则息屏断言不会释放。bd:// 等源播放途中也可能报告 idle，
                // 所以只在「没有当前文件」时才当作播放结束。
                let playbackEnded = isIdleActive == 1 && (filename?.isEmpty ?? true)
                let observedIsPlaying = isPaused == 0 && !playbackEnded
                if let pending = self.pendingIsPlayingOverride {
                    if observedIsPlaying == pending.value || Date() >= pending.until {
                        self.pendingIsPlayingOverride = nil
                        self.isPlaying = observedIsPlaying
                    } else {
                        self.isPlaying = pending.value
                    }
                } else {
                    self.isPlaying = observedIsPlaying
                }
                if validDuration > 0 {
                    self.duration = validDuration
                    self.position = Float(min(max(validTimePos / validDuration, 0), 1))
                    self.currentTime = self.formatTime(validTimePos)
                    self.remainingTime = "-" + self.formatTime(max(0, validDuration - validTimePos))
                } else if self.playbackEndTracker.endedDuration > 0 {
                    // 引擎已经丢掉当前文件：UI 钉在结尾，别让时间轴停在半路
                    let endedDuration = self.playbackEndTracker.endedDuration
                    self.duration = endedDuration
                    self.position = 1
                    self.currentTime = self.formatTime(endedDuration)
                    self.remainingTime = "-00:00"
                }
                if let name = filename, name != self.currentFilename {
                    self.currentFilename = name
                }
                let resolvedPlaylistIndex = max(0, Int(playlistPos))
                if resolvedPlaylistIndex != self.currentPlaylistIndex {
                    self.currentPlaylistIndex = resolvedPlaylistIndex
                }
                if currentTrackCount > 0 && currentTrackCount != self.lastTrackCount {
                    self.lastTrackCount = currentTrackCount
                    self.fetchTracksFromEngine()
                }
                if aid != self.activeAudioId { self.activeAudioId = aid }
                if sid != self.activeSubtitleId { self.activeSubtitleId = sid }
                if -delay != self.subtitleDelay { self.subtitleDelay = -delay }
            }
        }
    }
    
    private func fetchTracksFromEngine() {
        let trackCount = lastTrackCount
        guard trackCount > 0 else { return }
        mpvControlQueue.async { [weak self] in
            guard let self else { return }
            var newAudioNames: [String] = []
            var newAudioIds: [Int64] = []
            var newAudioDescriptors: [TrackDescriptor] = []
            var newSubNames: [String] = ["关闭字幕"]
            var newSubIds: [Int64] = [-1]
            var newSubtitleDescriptors: [TrackDescriptor] = []
            var subtitleTracks: [(id: Int64, lang: String, title: String)] = []

            for i in 0..<trackCount {
                guard let type = self.getMpvStringProperty("track-list/\(i)/type") else { continue }
                var id: Int64 = 0
                mpv_get_property(self.mpv, "track-list/\(i)/id", MPV_FORMAT_INT64, &id)
                let rawTitle = self.getMpvStringProperty("track-list/\(i)/title") ?? ""
                let rawLang = self.getMpvStringProperty("track-list/\(i)/lang") ?? ""
                let rawCodec = self.getMpvStringProperty("track-list/\(i)/codec") ?? ""
                let channels = self.getMpvStringProperty("track-list/\(i)/audio-channels") ?? ""
                let niceLang = self.translateLangCode(rawLang)
                var niceCodec = self.formatCodec(rawCodec)
                var elements: [String] = []

                if !niceLang.isEmpty { elements.append(niceLang) }
                if !rawTitle.isEmpty && rawTitle.lowercased() != rawLang.lowercased() && rawTitle != "Surround 5.1" {
                    elements.append(rawTitle)
                }
                var baseName = elements.joined(separator: " - ")

                if type == "audio" {
                    if !channels.isEmpty {
                        let fmtCh = self.formatAudioChannels(channels)
                        niceCodec += " \(fmtCh)"
                    }
                    if baseName.isEmpty { baseName = "音轨 \(id)" }
                    if !niceCodec.isEmpty { baseName += " (\(niceCodec))" }
                    newAudioNames.append(baseName)
                    newAudioIds.append(id)
                    newAudioDescriptors.append(TrackDescriptor(
                        kind: "audio",
                        language: rawLang,
                        title: rawTitle,
                        codec: rawCodec,
                        channels: channels
                    ))
                } else if type == "sub" {
                    if baseName.isEmpty { baseName = "字幕 \(id)" }
                    if !niceCodec.isEmpty { baseName += " (\(niceCodec))" }
                    subtitleTracks.append((id: id, lang: rawLang, title: rawTitle))
                    newSubNames.append(baseName)
                    newSubIds.append(id)
                    newSubtitleDescriptors.append(TrackDescriptor(
                        kind: "sub",
                        language: rawLang,
                        title: rawTitle,
                        codec: rawCodec,
                        channels: ""
                    ))
                }
            }

            let defaultSub = UserDefaults.standard.string(forKey: "defaultSub") ?? "chi"
            let savedAudioSignature = self.savedTrackPreference(kind: "audio")
            let savedSubtitleSignature = self.savedTrackPreference(kind: "sub")
            if let savedAudioSignature,
               let savedAudioIndex = newAudioDescriptors.firstIndex(where: { $0.signature == savedAudioSignature }) {
                let savedAudioId = newAudioIds[savedAudioIndex]
                mpv_set_property_string(self.mpv, "aid", "\(savedAudioId)")
                self.log("restored audio track id=\(savedAudioId)")
            }

            var preferredSubtitleId: Int64?
            var restoredSubtitlePreference = false
            if savedSubtitleSignature == Self.disabledSubtitlePreference {
                mpv_set_property_string(self.mpv, "sid", "no")
                restoredSubtitlePreference = true
                self.log("restored subtitles disabled")
            } else if let savedSubtitleSignature,
                      let savedSubtitleIndex = newSubtitleDescriptors.firstIndex(where: { $0.signature == savedSubtitleSignature }) {
                preferredSubtitleId = newSubIds[savedSubtitleIndex + 1]
                mpv_set_property_string(self.mpv, "sid", "\(preferredSubtitleId!)")
                restoredSubtitlePreference = true
                self.log("restored subtitle id=\(preferredSubtitleId!)")
            } else if !self.hasAppliedDefaultSubtitleForCurrentLoad && !self.hasUserSelectedSubtitleTrack {
                preferredSubtitleId = Self.preferredSubtitleId(defaultSub: defaultSub, subtitleTracks: subtitleTracks)
                if let preferredSubtitleId {
                    mpv_set_property_string(self.mpv, "sid", "\(preferredSubtitleId)")
                    self.log("auto selected subtitle id=\(preferredSubtitleId) defaultSub=\(defaultSub)")
                }
            }

            DispatchQueue.main.async {
                self.audioTrackNames = newAudioNames.isEmpty ? ["默认音轨"] : newAudioNames
                self.audioTrackIds = newAudioIds
                self.subtitleNames = newSubNames
                self.subtitleIds = newSubIds
                self.audioTrackDescriptors = newAudioDescriptors
                self.subtitleTrackDescriptors = newSubtitleDescriptors
                if restoredSubtitlePreference {
                    self.hasAppliedDefaultSubtitleForCurrentLoad = true
                }
                if let preferredSubtitleId {
                    self.activeSubtitleId = preferredSubtitleId
                    self.hasAppliedDefaultSubtitleForCurrentLoad = true
                }
            }
        }
    }
    
    nonisolated static func preferredSubtitleId(
        defaultSub: String,
        subtitleTracks: [(id: Int64, lang: String, title: String)]
    ) -> Int64? {
        guard defaultSub != "no" else { return nil }

        var best: (id: Int64, score: Int, index: Int)?
        for (index, track) in subtitleTracks.enumerated() {
            guard let score = subtitlePreferenceScore(defaultSub: defaultSub, lang: track.lang, title: track.title) else {
                continue
            }
            if best == nil || (score, index) < (best!.score, best!.index) {
                best = (track.id, score, index)
            }
        }
        return best?.id ?? subtitleTracks.last?.id
    }

    nonisolated private static func subtitlePreferenceScore(defaultSub: String, lang: String, title: String) -> Int? {
        switch defaultSub {
        case "chi":
            if isChineseSubtitle(lang: lang, title: title) {
                return chineseScriptPreferenceScore(lang: lang, title: title)
            }
            return isEnglishSubtitle(lang: lang, title: title) ? 100 : nil
        case "eng":
            if isEnglishSubtitle(lang: lang, title: title) {
                return 0
            }
            return isChineseSubtitle(lang: lang, title: title) ? 100 + chineseScriptPreferenceScore(lang: lang, title: title) : nil
        default:
            return nil
        }
    }

    nonisolated private static func isChineseSubtitle(lang: String, title: String) -> Bool {
        let primaryCode = normalizedLanguagePrimaryCode(lang)
        if ["chi", "zho", "zh", "cmn", "yue"].contains(primaryCode) {
            return true
        }

        let lowerTitle = title.lowercased()
        if title.contains("中文") || title.contains("简体") || title.contains("繁體") || title.contains("繁体") || title.contains("中字") {
            return true
        }
        if lowerTitle.contains("chinese") {
            return true
        }
        let tokens = normalizedTokens(from: title)
        return !tokens.isDisjoint(with: ["chs", "cht", "zho", "chi", "cmn"])
    }

    nonisolated private static func isEnglishSubtitle(lang: String, title: String) -> Bool {
        let primaryCode = normalizedLanguagePrimaryCode(lang)
        if ["eng", "en"].contains(primaryCode) {
            return true
        }
        if title.contains("英语") || title.contains("英文") {
            return true
        }
        let tokens = normalizedTokens(from: title)
        return !tokens.isDisjoint(with: ["eng", "english"])
    }

    nonisolated private static func chineseScriptPreferenceScore(lang: String, title: String) -> Int {
        let normalizedLang = lang.lowercased().replacingOccurrences(of: "_", with: "-")
        let lowerTitle = title.lowercased()
        let tokens = normalizedTokens(from: "\(lang) \(title)")

        if normalizedLang.contains("hans")
            || normalizedLang.contains("zh-cn")
            || normalizedLang.contains("zh-sg")
            || title.contains("简")
            || lowerTitle.contains("simplified")
            || tokens.contains("chs")
            || tokens.contains("gb") {
            return 0
        }

        if normalizedLang.contains("hant")
            || normalizedLang.contains("zh-tw")
            || normalizedLang.contains("zh-hk")
            || normalizedLang.contains("zh-mo")
            || title.contains("繁")
            || lowerTitle.contains("traditional")
            || tokens.contains("cht")
            || tokens.contains("big5") {
            return 2
        }

        return 1
    }

    nonisolated private static func normalizedLanguagePrimaryCode(_ value: String) -> String {
        let normalized = value
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()
            .replacingOccurrences(of: "_", with: "-")
        return normalized.split(separator: "-").first.map(String.init) ?? normalized
    }

    nonisolated private static func normalizedTokens(from value: String) -> Set<String> {
        let lower = value.lowercased().replacingOccurrences(of: "_", with: "-")
        return Set(lower.components(separatedBy: CharacterSet.alphanumerics.inverted).filter { !$0.isEmpty })
    }

    nonisolated static func translatedLanguageLabel(_ lang: String) -> String {
        let trimmed = lang.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return "" }
        let normalized = trimmed
            .lowercased()
            .replacingOccurrences(of: "_", with: "-")
        let primaryCode = normalized.split(separator: "-").first.map(String.init) ?? normalized

        if ["chi", "zho", "zh", "cmn", "yue"].contains(primaryCode) {
            return "🇨🇳 中文"
        }

        switch primaryCode {
        case "eng", "en": return "🇺🇸 英语"
        case "jpn", "ja": return "🇯🇵 日语"
        case "kor", "ko": return "🇰🇷 韩语"
        case "fre", "fra", "fr": return "🇫🇷 法语"
        case "spa", "es": return "🇪🇸 西语"
        case "ger", "deu", "de": return "🇩🇪 德语"
        case "rus", "ru": return "🇷🇺 俄语"
        case "ita", "it": return "🇮🇹 意语"
        case "por", "pt": return "🇵🇹 葡语"
        case "tha", "th": return "🇹🇭 泰语"
        case "vie", "vi": return "🇻🇳 越南语"
        default: return trimmed.uppercased()
        }
    }

    private func translateLangCode(_ lang: String) -> String {
        Self.translatedLanguageLabel(lang)
    }
    private func formatAudioChannels(_ channels: String) -> String {
        let normalized = channels.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        switch normalized {
        case "mono": return "1.0"
        case "stereo": return "2.0"
        default: break
        }
        if let count = Int(normalized) {
            switch count {
            case 1: return "1.0"
            case 2: return "2.0"
            case 3: return "3.0"
            case 4: return "4.0"
            case 5: return "5.0"
            case 6: return "5.1"
            case 7: return "6.1"
            case 8: return "7.1"
            default: return "\(count)ch"
            }
        }
        return normalized.replacingOccurrences(of: #"\s+"#, with: "", options: .regularExpression)
    }
    private func formatCodec(_ codec: String) -> String { let c = codec.lowercased(); if c.contains("truehd") { return "TrueHD Atmos" }; if c.contains("dts-hd") || c.contains("dtshd") { return "DTS-HD MA" }; if c.contains("dts") { return "DTS" }; if c.contains("eac3") { return "E-AC3" }; if c.contains("ac3") { return "Dolby AC3" }; if c.contains("aac") { return "AAC" }; if c.contains("flac") { return "FLAC" }; if c.contains("pgs") { return "PGS" }; if c.contains("srt") || c.contains("subrip") { return "SRT" }; if c.contains("ass") { return "ASS" }; return c.uppercased() }
    private func getMpvStringProperty(_ name: String) -> String? { if let cString = mpv_get_property_string(mpv, name) { let result = String(cString: cString); mpv_free(UnsafeMutableRawPointer(mutating: cString)); return result }; return nil }
    func setAudioTrack(at index: Int) {
        guard index >= 0 && index < audioTrackIds.count else { return }
        let targetId = audioTrackIds[index]
        if audioTrackDescriptors.indices.contains(index) {
            saveTrackPreference(kind: "audio", signature: audioTrackDescriptors[index].signature)
        }
        runOnControlQueue { [weak self] in
            guard let self else { return }
            mpv_set_property_string(self.mpv, "aid", "\(targetId)")
        }
    }

    func setSubtitleTrack(at index: Int) {
        guard index >= 0 && index < subtitleIds.count else { return }
        let id = subtitleIds[index]
        hasUserSelectedSubtitleTrack = true
        if id == -1 {
            saveTrackPreference(kind: "sub", signature: Self.disabledSubtitlePreference)
        } else if subtitleTrackDescriptors.indices.contains(index - 1) {
            saveTrackPreference(kind: "sub", signature: subtitleTrackDescriptors[index - 1].signature)
        }
        runOnControlQueue { [weak self] in
            guard let self else { return }
            if id == -1 {
                mpv_set_property_string(self.mpv, "sid", "no")
            } else {
                mpv_set_property_string(self.mpv, "sid", "\(id)")
            }
        }
    }

    func addExternalSubtitle(url: URL, title: String, language: String = "chi") {
        hasUserSelectedSubtitleTrack = true
        executeMpvCommand(["sub-add", url.path, "select", title, language])
    }

    private func savedTrackPreference(kind: String) -> String? {
        guard let currentTrackPreferenceKey,
              !currentTrackPreferenceKey.isEmpty,
              let values = UserDefaults.standard.dictionary(forKey: Self.trackPreferenceDefaultsKey) as? [String: String] else {
            return nil
        }
        return values["\(currentTrackPreferenceKey)|\(kind)"]
    }

    private func saveTrackPreference(kind: String, signature: String) {
        guard let currentTrackPreferenceKey,
              !currentTrackPreferenceKey.isEmpty else {
            return
        }
        var values = UserDefaults.standard.dictionary(forKey: Self.trackPreferenceDefaultsKey) as? [String: String] ?? [:]
        values["\(currentTrackPreferenceKey)|\(kind)"] = signature
        UserDefaults.standard.set(values, forKey: Self.trackPreferenceDefaultsKey)
    }
    private func formatTime(_ time: Double) -> String { if time.isNaN || time < 0 { return "00:00" }; let t = Int(time); return t / 3600 > 0 ? String(format: "%02d:%02d:%02d", t/3600, (t%3600)/60, t%60) : String(format: "%02d:%02d", (t%3600)/60, t%60) }
    
    // ==========================================
    // 🌟 Phase 2 新增：手动调整字幕时间轴
    // ==========================================
    
    /// 调整字幕时间轴 (秒)
    /// 正数会让字幕调早（更前），负数调晚（更后）
    func adjustSubtitleDelay(by seconds: Double) {
        // 使用 IINA/VLC 兼容的逻辑：正数调早，负数调晚。
        // MPV 本身底层的 sub-delay 属性正好是反着的，所以我们取负数发送。
        let newMpvDelay = -(subtitleDelay + seconds)
        runOnControlQueue { [weak self] in
            guard let self else { return }
            mpv_set_property_string(self.mpv, "sub-delay", "\(newMpvDelay)")
        }
        // State 会在 updatePlaybackState 的下一帧被更新，从而同步 UI
    }
}
