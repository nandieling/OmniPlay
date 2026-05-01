import Foundation
import AppKit
import Combine
import QuartzCore
import _MPVKit_GPL
import Libmpv

class MPVPlayerManager: ObservableObject {
    var mpv: OpaquePointer?
    private let mpvControlQueue = DispatchQueue(label: "nan.omniplay.mpv.control")
    private let controlQueueKey = DispatchSpecificKey<Bool>()
    private var lastDrawablePointer: Int64?
    private var pendingDrawablePointer: Int64?
    private var retainedDrawableLayer: CAMetalLayer?
    private var isStopping = false
    
    @Published var isPlaying = false
    @Published var position: Float = 0.0
    @Published var currentTime: String = "00:00"
    @Published var remainingTime: String = "00:00"
    @Published var currentSubtitleSize: Int = 16
    @Published var currentFilename: String = ""
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
    private var lastTrackCount: Int64 = 0
    private var timer: Timer?
    private var isPollingState = false
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
    
    private func runOnControlQueue(_ block: @escaping () -> Void) {
        if DispatchQueue.getSpecific(key: controlQueueKey) == true {
            block()
        } else {
            mpvControlQueue.async(execute: block)
        }
    }

    private func mpvLoadArgument(for url: URL) -> String {
        if url.isFileURL {
            return url.path
        }
        return url.absoluteString
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
        return components.string ?? url.absoluteString
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

    private func applySafeSDRColorPipelineAsOption() {
        // Force a predictable HDR->SDR path to avoid highlight clipping on DV/HDR sources.
        mpv_set_option_string(mpv, "target-prim", "bt.709")
        mpv_set_option_string(mpv, "target-trc", "gamma2.2")
        mpv_set_option_string(mpv, "tone-mapping", "bt.2390")
    }

    // Reserved for follow-up: detect HDR/DV by stream metadata instead of filename hints.
    private func currentStreamLooksHDRLike() -> Bool {
        let gamma = getMpvStringProperty("video-params/gamma")?.lowercased() ?? ""
        if gamma.contains("pq") || gamma.contains("hlg") || gamma.contains("smpte2084") {
            return true
        }
        let primaries = getMpvStringProperty("video-params/primaries")?.lowercased() ?? ""
        if primaries.contains("bt.2020") || primaries.contains("2020") {
            return true
        }
        return false
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
        applySafeSDRColorPipelineAsOption()
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
            let subFallback = defaultSub == "chi" ? "chi,zho,zh,zh-cn,zh-tw,eng" : "eng,chi,zho"
            mpv_set_option_string(mpv, "slang", subFallback)
        }
        
        mpv_initialize(mpv)
        timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in self?.updatePlaybackState() }
    }
    
    func setDrawable(_ view: NSView) {
        guard !isStopping else {
            traceLifecycle("setDrawable skipped because isStopping=true")
            return
        }
        log("setDrawable called view=\(type(of: view))")
        if !Thread.isMainThread {
            DispatchQueue.main.async { [weak self, weak view] in
                guard let self, let view else { return }
                self.setDrawable(view)
            }
            return
        }
        guard let layer = view.layer as? CAMetalLayer else { return }
        let pointer = Unmanaged.passUnretained(layer).toOpaque()
        let wid = Int64(Int(bitPattern: pointer))
        retainedDrawableLayer = layer
        guard lastDrawablePointer != wid else { return }
        guard pendingDrawablePointer != wid else { return }
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
    
    func loadFiles(urls: [URL], startPosition: Double = 0.0, isBluRay: Bool = false, blurayRootPath: String? = nil) {
        guard !urls.isEmpty else { return }
        isStopping = false
        if timer == nil {
            timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
                self?.updatePlaybackState()
            }
        }
        log("loadFiles urls=\(urls.count) first=\(mpvLogArgument(for: urls[0])) start=\(startPosition) isBluRay=\(isBluRay) blurayRoot=\(blurayRootPath ?? "nil")")
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
            self.log("decode profile isDVLike=\(isDolbyVisionLike) hwdec=videotoolbox")
            if startPosition > 0 { mpv_set_property_string(self.mpv, "start", "\(startPosition)") } else { mpv_set_property_string(self.mpv, "start", "0") }
            
            if isBluRay, let rootPath = blurayRootPath {
                mpv_set_property_string(self.mpv, "bluray-device", rootPath)
                self.executeMpvCommand(["loadfile", "bd://", "replace"])
            } else {
                self.executeMpvCommand(["loadfile", self.mpvLoadArgument(for: urls[0]), "replace"])
                for url in urls.dropFirst() {
                    self.executeMpvCommand(["loadfile", self.mpvLoadArgument(for: url), "append"])
                }
            }
            
            let defaultSub = UserDefaults.standard.string(forKey: "defaultSub") ?? "chi"
            if defaultSub == "no" { mpv_set_property_string(self.mpv, "sid", "no") } else { mpv_set_property_string(self.mpv, "sid", "auto") }
            
            DispatchQueue.main.async {
                self.isPlaying = true
                self.lastTrackCount = 0
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
                    var masked = cmd
                    masked[1] = components.string ?? cmd[1]
                    self.log("executeMpvCommand \(masked.joined(separator: " "))")
                } else {
                    self.log("executeMpvCommand \(cmd.joined(separator: " "))")
                }
            }
            var cCmd: [UnsafePointer<CChar>?] = cmd.map { UnsafePointer(strdup($0)) }
            cCmd.append(nil)
            let result = mpv_command(self.mpv, &cCmd)
            if result < 0 {
                if let errPtr = mpv_error_string(result) {
                    self.log("executeMpvCommand failed code=\(result) error=\(String(cString: errPtr))")
                } else {
                    self.log("executeMpvCommand failed code=\(result)")
                }
            }
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
            mpv_command_string(self.mpv, String(format: "seek %.2f absolute", targetTime))
        }
    }

    func seekRelative(seconds: Double) {
        runOnControlQueue { [weak self] in
            guard let self else { return }
            mpv_command_string(self.mpv, String(format: "seek %.2f relative", seconds))
        }
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

            mpv_get_property(mpv, "time-pos", MPV_FORMAT_DOUBLE, &timePos)
            mpv_get_property(mpv, "duration", MPV_FORMAT_DOUBLE, &dur)
            mpv_get_property(mpv, "track-list/count", MPV_FORMAT_INT64, &currentTrackCount)
            mpv_get_property(mpv, "aid", MPV_FORMAT_INT64, &aid)
            mpv_get_property(mpv, "sid", MPV_FORMAT_INT64, &sid)
            mpv_get_property(mpv, "sub-delay", MPV_FORMAT_DOUBLE, &delay)
            let filename = self.getMpvStringProperty("filename")

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
            var newSubNames: [String] = ["关闭字幕"]
            var newSubIds: [Int64] = [-1]

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
                        let fmtCh = channels == "stereo" ? "2.0" : (channels == "mono" ? "1.0" : channels)
                        niceCodec += " \(fmtCh)"
                    }
                    if baseName.isEmpty { baseName = "音轨 \(id)" }
                    if !niceCodec.isEmpty { baseName += " (\(niceCodec))" }
                    newAudioNames.append(baseName)
                    newAudioIds.append(id)
                } else if type == "sub" {
                    if baseName.isEmpty { baseName = "字幕 \(id)" }
                    if !niceCodec.isEmpty { baseName += " (\(niceCodec))" }
                    newSubNames.append(baseName)
                    newSubIds.append(id)
                }
            }

            DispatchQueue.main.async {
                self.audioTrackNames = newAudioNames.isEmpty ? ["默认音轨"] : newAudioNames
                self.audioTrackIds = newAudioIds
                self.subtitleNames = newSubNames
                self.subtitleIds = newSubIds
            }
        }
    }
    
    private func translateLangCode(_ lang: String) -> String { guard !lang.isEmpty else { return "" }; switch lang.lowercased() { case "chi", "zho", "zh", "zh-cn", "zh-tw", "zh-hk": return "🇨🇳 中文"; case "eng", "en": return "🇺🇸 英语"; case "jpn", "ja": return "🇯🇵 日语"; case "kor", "ko": return "🇰🇷 韩语"; case "fre", "fra", "fr": return "🇫🇷 法语"; case "spa", "es": return "🇪🇸 西语"; case "ger", "deu", "de": return "🇩🇪 德语"; case "rus", "ru": return "🇷🇺 俄语"; case "ita", "it": return "🇮🇹 意语"; case "por", "pt": return "🇵🇹 葡语"; case "tha", "th": return "🇹🇭 泰语"; case "vie", "vi": return "🇻🇳 越南语"; default: return lang.uppercased() } }
    private func formatCodec(_ codec: String) -> String { let c = codec.lowercased(); if c.contains("truehd") { return "TrueHD Atmos" }; if c.contains("dts-hd") || c.contains("dtshd") { return "DTS-HD MA" }; if c.contains("dts") { return "DTS" }; if c.contains("eac3") { return "E-AC3" }; if c.contains("ac3") { return "Dolby AC3" }; if c.contains("aac") { return "AAC" }; if c.contains("flac") { return "FLAC" }; if c.contains("pgs") { return "PGS 图形字幕" }; if c.contains("srt") || c.contains("subrip") { return "SRT" }; if c.contains("ass") { return "ASS" }; return c.uppercased() }
    private func getMpvStringProperty(_ name: String) -> String? { if let cString = mpv_get_property_string(mpv, name) { let result = String(cString: cString); mpv_free(UnsafeMutableRawPointer(mutating: cString)); return result }; return nil }
    func setAudioTrack(at index: Int) {
        guard index >= 0 && index < audioTrackIds.count else { return }
        let targetId = audioTrackIds[index]
        runOnControlQueue { [weak self] in
            guard let self else { return }
            mpv_set_property_string(self.mpv, "aid", "\(targetId)")
        }
    }

    func setSubtitleTrack(at index: Int) {
        guard index >= 0 && index < subtitleIds.count else { return }
        let id = subtitleIds[index]
        runOnControlQueue { [weak self] in
            guard let self else { return }
            if id == -1 {
                mpv_set_property_string(self.mpv, "sid", "no")
            } else {
                mpv_set_property_string(self.mpv, "sid", "\(id)")
            }
        }
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
