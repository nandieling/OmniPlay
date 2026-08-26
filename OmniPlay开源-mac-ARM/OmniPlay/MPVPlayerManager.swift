import Foundation
import AppKit
import Combine
import QuartzCore
import Libmpv

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

    var mpv: OpaquePointer?
    private let mpvControlQueue = DispatchQueue(label: "nan.omniplay.mpv.control")
    private let controlQueueKey = DispatchSpecificKey<Bool>()
    private var lastDrawablePointer: Int64?
    private var pendingDrawablePointer: Int64?
    private var retainedDrawableLayer: CAMetalLayer?
    private var isStopping = false
    private var playbackActivity: NSObjectProtocol?
    
    @Published var isPlaying = false {
        didSet {
            guard isPlaying != oldValue else { return }
            updatePlaybackActivity()
        }
    }
    @Published var position: Float = 0.0
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
    var duration: Double = 0.0
    
    private func log(_ message: String) {
        print("[MPVPlayerManager] \(message)")
    }

    private func traceLifecycle(_ message: String) {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let queue = String(cString: __dispatch_queue_get_label(nil), encoding: .utf8) ?? "unknown"
        let thread = Thread.isMainThread ? "main" : "bg"
        print("[MPVLifecycle][\(formatter.string(from: Date()))][\(thread)][q:\(queue)] \(message)")
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
        if DispatchQueue.getSpecific(key: controlQueueKey) == true {
            var timePos: Double = 0.0
            mpv_get_property(mpv, "time-pos", MPV_FORMAT_DOUBLE, &timePos)
            return timePos
        }
        return mpvControlQueue.sync {
            var timePos: Double = 0.0
            mpv_get_property(mpv, "time-pos", MPV_FORMAT_DOUBLE, &timePos)
            return timePos
        }
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
            return PlaybackEngineSnapshot(
                playlistIndex: max(0, Int(playlistPos)),
                timePos: timePos,
                duration: dur,
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
        log("loadFiles urls=\(urls.count) first=\(mpvLogArgument(for: urls[0])) start=\(startPosition) isBluRay=\(isBluRay) blurayRoot=\(blurayRootPath.map { mpvLogArgument(for: $0) } ?? "nil")")
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
            self.applyPlaybackQualityModeAtRuntime()
            mpv_set_property_string(self.mpv, "vd-lavc-o", isDolbyVisionLike ? "enable_dovi=0" : "")
            mpv_set_property_string(self.mpv, "hwdec", "videotoolbox")
            let shouldStripUserInfo = self.applyRemotePlaybackHeaders(for: urls)
            self.log("decode profile isDVLike=\(isDolbyVisionLike) hwdec=videotoolbox")
            self.pendingInitialSeekPosition = startPosition > 5.0 ? startPosition : nil
            self.pendingInitialSeekFilename = (startPosition > 5.0 && urls[0].isFileURL) ? urls[0].lastPathComponent : nil
            self.pendingInitialSeekAttempts = 0
            self.pendingInitialSeekDeadline = startPosition > 5.0
                ? Date().addingTimeInterval(self.initialSeekTimeout)
                : nil
            mpv_set_property_string(self.mpv, "start", "0")
            
            if isBluRay, let rootPath = blurayRootPath {
                let rootArgument = self.mpvStringArgument(rootPath, stripUserInfo: shouldStripUserInfo)
                mpv_set_property_string(self.mpv, "bluray-device", rootArgument)
                self.executeMpvCommand(["loadfile", "bd://", "replace"])
            } else {
                self.executeMpvCommand(["loadfile", self.mpvLoadArgument(for: urls[0], stripUserInfo: shouldStripUserInfo), "replace"])
                for url in urls.dropFirst() {
                    self.executeMpvCommand(["loadfile", self.mpvLoadArgument(for: url, stripUserInfo: shouldStripUserInfo), "append"])
                }
            }
            
            let defaultSub = UserDefaults.standard.string(forKey: "defaultSub") ?? "chi"
            if defaultSub == "no" { mpv_set_property_string(self.mpv, "sid", "no") } else { mpv_set_property_string(self.mpv, "sid", "auto") }
            
            DispatchQueue.main.async {
                self.isPlaying = true
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
        runOnControlQueue { [weak self] in
            guard let self else { return }
            var pause: Int32 = 0
            mpv_get_property(self.mpv, "pause", MPV_FORMAT_FLAG, &pause)
            var newPause: Int32 = (pause == 1) ? 0 : 1
            mpv_set_property(self.mpv, "pause", MPV_FORMAT_FLAG, &newPause)
            DispatchQueue.main.async {
                self.isPlaying = (newPause == 0)
            }
        }
    }

    func setPaused(_ paused: Bool) {
        guard !isStopping else { return }
        DispatchQueue.main.async {
            self.isPlaying = !paused
        }
        runOnControlQueue { [weak self] in
            guard let self else { return }
            var pause: Int32 = paused ? 1 : 0
            mpv_set_property(self.mpv, "pause", MPV_FORMAT_FLAG, &pause)
            DispatchQueue.main.async {
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
        runOnControlQueue { [weak self] in
            guard let self else { return }
            guard let mpv = self.mpv else { return }
            self.traceLifecycle("stop on control queue: stop command")
            mpv_command_string(mpv, "stop")
        }

        DispatchQueue.main.async {
            self.lastDrawablePointer = nil
            self.pendingDrawablePointer = nil
            self.retainedDrawableLayer = nil
            self.isPlaying = false
            self.traceLifecycle("stop main-thread cleanup done")
        }
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
        guard isPlaying else { return }
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

            mpv_get_property(mpv, "time-pos", MPV_FORMAT_DOUBLE, &timePos)
            mpv_get_property(mpv, "duration", MPV_FORMAT_DOUBLE, &dur)
            mpv_get_property(mpv, "track-list/count", MPV_FORMAT_INT64, &currentTrackCount)
            mpv_get_property(mpv, "aid", MPV_FORMAT_INT64, &aid)
            mpv_get_property(mpv, "sid", MPV_FORMAT_INT64, &sid)
            mpv_get_property(mpv, "sub-delay", MPV_FORMAT_DOUBLE, &delay)
            mpv_get_property(mpv, "playlist-pos", MPV_FORMAT_INT64, &playlistPos)
            let filename = self.getMpvStringProperty("filename")
            self.applyPendingInitialSeekIfNeeded(timePos: timePos, duration: dur, playlistPos: playlistPos, filename: filename)

            DispatchQueue.main.async {
                defer { self.isPollingState = false }
                self.duration = dur
                if dur > 0 {
                    self.position = Float(timePos / dur)
                    self.currentTime = self.formatTime(timePos)
                    self.remainingTime = "-" + self.formatTime(dur - timePos)
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
