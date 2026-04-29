import SwiftUI
import GRDB
import UniformTypeIdentifiers // 🌟 引入系统类型以支持原生文件选择器

extension Notification.Name {
    static let standalonePlayerShouldClose = Notification.Name("StandalonePlayerShouldClose")
}

// ==========================================
// 🌟 长按快进/快退的手势修饰器
// ==========================================
struct ContinuousPressModifier: ViewModifier {
    let action: () -> Void
    @State private var timer: Timer?
    @State private var isPressing = false
    
    func body(content: Content) -> some View {
        content
            .scaleEffect(isPressing ? 0.85 : 1.0)
            .animation(.easeInOut(duration: 0.1), value: isPressing)
            .simultaneousGesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { _ in
                        if !isPressing {
                            isPressing = true
                            action()
                            timer = Timer.scheduledTimer(withTimeInterval: 0.2, repeats: true) { _ in action() }
                        }
                    }
                    .onEnded { _ in
                        isPressing = false
                        timer?.invalidate()
                        timer = nil
                    }
            )
    }
}

extension View {
    func onContinuousPress(action: @escaping () -> Void) -> some View { self.modifier(ContinuousPressModifier(action: action)) }
}

// ==========================================
// 🌟 核心播放器视图
// ==========================================
struct PlayerScreen: View {
    let movie: Movie
    var initialFileId: String? = nil
    var isStandaloneWindow: Bool = false
    var initialSourceBasePath: String? = nil
    var initialSourceProtocolType: String? = nil
    var initialSourceAuthConfig: String? = nil
    var initialPlaylistFiles: [VideoFile]? = nil
    
    @StateObject private var playerManager = MPVPlayerManager()
    @AppStorage("seekDuration") var seekDuration: Int = 10
    
    @State private var videoURLs: [URL] = []
    @State private var allPlaylistFiles: [VideoFile] = []
    @State private var rootFolderURL: URL?
    @State private var errorMessage: String?
    
    @State private var currentVideoFileId: String?
    @State private var startPosition: Double = 0.0
    
    @State private var isBluRayFolder: Bool = false
    @State private var blurayRootPath: String? = nil
    
    @State private var showControls = true
    @State private var isCursorHidden = false
    @State private var hideUITask: Task<Void, Never>? = nil
    @State private var menuInteractionTask: Task<Void, Never>? = nil
    @State private var isMenuInteractionActive = false
    @State private var hasIssuedLoad = false
    @State private var hasPersistedBeforeExit = false
    @State private var isClosingPlayback = false
    @Environment(\.dismiss) var dismiss
    
    private func log(_ message: String) {
        print("[PlayerScreen] \(message)")
    }

    private func traceClose(_ message: String) {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let queue = String(cString: __dispatch_queue_get_label(nil), encoding: .utf8) ?? "unknown"
        let thread = Thread.isMainThread ? "main" : "bg"
        print("[PlayerScreenClose][\(formatter.string(from: Date()))][\(thread)][q:\(queue)] \(message)")
    }
    
    // 智能推算当前播放集数标题
    private var currentPlaybackTitle: String {
        guard let id = currentVideoFileId, let file = allPlaylistFiles.first(where: { $0.id == id }) else { return movie.title }
        let resolvedInfo = EpisodeMetadataOverrideStore.shared.resolvedEpisodeInfo(
            fileId: file.id,
            fileName: file.fileName,
            fallbackIndex: 0
        )
        return resolvedInfo.isTVShow ? "\(movie.title) \(resolvedInfo.displayName)" : movie.title
    }
    
    private var playbackProgressRatio: Double {
        let dur = playerManager.duration
        guard dur > 0 else { return 0 }
        return min(max(playerManager.currentTimePos / dur, 0), 1)
    }
    
    private var hasNextEpisode: Bool {
        guard let id = currentVideoFileId,
              let currentIndex = allPlaylistFiles.firstIndex(where: { $0.id == id }) else { return false }
        return currentIndex < (allPlaylistFiles.count - 1)
    }
    
    private var isTVPlayback: Bool {
        if allPlaylistFiles.enumerated().contains(where: { idx, file in
            MediaNameParser.parseEpisodeInfo(from: file.fileName, fallbackIndex: idx).isTVShow
        }) {
            return true
        }
        return allPlaylistFiles.count > 1 && (movie.title.contains("季") || movie.title.contains("集"))
    }
    
    private var shouldShowNextEpisodeButton: Bool {
        isTVPlayback && showControls && !isCursorHidden && hasNextEpisode && playbackProgressRatio >= 0.95
    }
    
    var body: some View {
        ZStack {
            Color.black.edgesIgnoringSafeArea(.all)
            
            if !videoURLs.isEmpty {
                MPVVideoView(playerManager: playerManager)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .clipped()
                .onTapGesture { withAnimation(.easeInOut(duration: 0.3)) { showControls.toggle() }; if showControls { resetHideTimer() } }
            } else if let error = errorMessage {
                Text(error).foregroundColor(.red).font(.title2)
            } else {
                ProgressView("准备播放环境...").foregroundColor(.white)
            }
            
            if showControls {
                VStack {
                    // 顶部栏
                    HStack {
                        if !isStandaloneWindow {
                            Button(action: closePlayer) {
                                Image(systemName: "chevron.left.circle.fill").font(.system(size: 32))
                            }
                            .buttonStyle(.plain)
                            .onHover { if $0 { NSCursor.pointingHand.push() } else { NSCursor.pop() } }
                        }
                        Text(currentPlaybackTitle).font(.title3).fontWeight(.bold).padding(.leading, 10)
                        Spacer()
                    }.foregroundColor(.white).padding(20).background(LinearGradient(gradient: Gradient(colors: [.black.opacity(0.8), .clear]), startPoint: .top, endPoint: .bottom))
                    
                    Spacer()
                    
                    // 底部进度条与菜单
                    VStack(spacing: 15) {
                        if shouldShowNextEpisodeButton {
                            HStack {
                                Spacer()
                                Button(action: playNextEpisodeAndMarkCurrentWatched) {
                                    Text("播放下一集")
                                        .font(.system(size: 14, weight: .semibold))
                                        .foregroundColor(.white)
                                        .padding(.horizontal, 14)
                                        .padding(.vertical, 8)
                                        .background(Color.black.opacity(0.72))
                                        .overlay(
                                            RoundedRectangle(cornerRadius: 10)
                                                .stroke(Color.white.opacity(0.25), lineWidth: 1)
                                        )
                                        .clipShape(RoundedRectangle(cornerRadius: 10))
                                }
                                .buttonStyle(.plain)
                                .help("播放下一集")
                            }
                        }

                        // 底部播放控制区（常见播放器布局）
                        HStack(spacing: 40) {
                            Image(systemName: "backward.end.fill")
                                .font(.system(size: 34))
                                .onContinuousPress {
                                    playerManager.seekRelative(seconds: Double(-seekDuration))
                                    resetHideTimer()
                                }
                            Button(action: {
                                playerManager.playOrPause()
                                resetHideTimer()
                            }) {
                                Image(systemName: playerManager.isPlaying ? "pause.circle.fill" : "play.circle.fill")
                                    .font(.system(size: 60))
                                    .shadow(radius: 10)
                            }
                            .buttonStyle(.plain)
                            Image(systemName: "forward.end.fill")
                                .font(.system(size: 34))
                                .onContinuousPress {
                                    playerManager.seekRelative(seconds: Double(seekDuration))
                                    resetHideTimer()
                                }
                        }
                        .foregroundColor(.white)
                        .onHover { if $0 { NSCursor.pointingHand.push() } else { NSCursor.pop() } }
                        
                        HStack {
                            Text(playerManager.currentTime).font(.caption.monospacedDigit())
                            Slider(value: Binding(get: { playerManager.position }, set: { newValue in playerManager.setPosition(newValue); resetHideTimer() })).tint(.red)
                            Text(playerManager.remainingTime).font(.caption.monospacedDigit())
                        }
                        
                        HStack(spacing: 16) {
                            Spacer()
                                                                                
                            // 🌟 音轨菜单
                            Menu {
                                ForEach(0..<playerManager.audioTrackNames.count, id: \.self) { i in
                                    Button(action: {
                                        playerManager.setAudioTrack(at: i)
                                        completeMenuInteraction()
                                    }) {
                                        let isActive = i < playerManager.audioTrackIds.count && playerManager.activeAudioId == playerManager.audioTrackIds[i]
                                        Text((isActive ? "✓ " : "") + playerManager.audioTrackNames[i])
                                    }
                                }
                            } label: { HStack(spacing: 6) { Image(systemName: "waveform").font(.system(size: 16)); Text("音轨").font(.system(size: 14, weight: .bold)) }.padding(.horizontal, 16).padding(.vertical, 10).background(Color.black.opacity(0.75)).foregroundColor(.white).cornerRadius(8) }.menuStyle(.borderlessButton).fixedSize().colorScheme(.dark).simultaneousGesture(TapGesture().onEnded { beginMenuInteraction() })
                                                                                
                            // 🌟 字幕菜单 (含本地外挂及时间轴控制)
                            Menu {
                                ForEach(0..<playerManager.subtitleNames.count, id: \.self) { i in
                                    Button(action: {
                                        playerManager.setSubtitleTrack(at: i)
                                        completeMenuInteraction()
                                    }) {
                                        let isActive = i < playerManager.subtitleIds.count && playerManager.activeSubtitleId == playerManager.subtitleIds[i]
                                        Text((isActive ? "✓ " : "") + playerManager.subtitleNames[i])
                                    }
                                }
                                
                                Divider() // 分割线
                                
                                // 🌟 1. 加载本地外挂字幕
                                Button(action: {
                                    loadExternalSubtitle()
                                    completeMenuInteraction()
                                }) {
                                    HStack {
                                        Image(systemName: "doc.badge.plus")
                                        Text("加载外置字幕...")
                                    }
                                }
                                
                                Divider() // 分割线
                                
                                Menu("字幕大小") {
                                    Button(action: {
                                        setSubtitleSize(12)
                                        completeMenuInteraction()
                                    }) {
                                        Text((playerManager.currentSubtitleSize == 12 ? "✓ " : "") + "小号字体 (12)")
                                    }
                                    Button(action: {
                                        setSubtitleSize(16)
                                        completeMenuInteraction()
                                    }) {
                                        Text((playerManager.currentSubtitleSize == 16 ? "✓ " : "") + "标准字体 (16)")
                                    }
                                    Button(action: {
                                        setSubtitleSize(20)
                                        completeMenuInteraction()
                                    }) {
                                        Text((playerManager.currentSubtitleSize == 20 ? "✓ " : "") + "大号字体 (20)")
                                    }
                                    Button(action: {
                                        setSubtitleSize(24)
                                        completeMenuInteraction()
                                    }) {
                                        Text((playerManager.currentSubtitleSize == 24 ? "✓ " : "") + "特大字体 (24)")
                                    }
                                }
                                
                                Divider() // 分割线
                                
                                // 🌟 2. 时间轴同步手动控制 (G/H 国际标快捷键)
                                VStack(alignment: .leading, spacing: 6) {
                                    Text("字幕同步 (G 早 / H 晚)").font(.caption2).foregroundColor(.gray)
                                    HStack(spacing: 8) {
                                        Button(action: {
                                            playerManager.adjustSubtitleDelay(by: -0.5)
                                            completeMenuInteraction()
                                        }) {
                                            Image(systemName: "minus.circle")
                                        }.help("字幕调早 0.5 秒")
                                        
                                        Text(String(format: "同步 %.1fs", playerManager.subtitleDelay))
                                            .font(.system(size: 13, weight: .bold))
                                            .foregroundColor(.white)
                                            .frame(width: 55, alignment: .center)
                                        
                                        Button(action: {
                                            playerManager.adjustSubtitleDelay(by: 0.5)
                                            completeMenuInteraction()
                                        }) {
                                            Image(systemName: "plus.circle")
                                        }.help("字幕调晚 0.5 秒")
                                    }
                                    .padding(.horizontal, 10).padding(.vertical, 6)
                                    .background(Color.white.opacity(0.1)).cornerRadius(6)
                                }.padding(.horizontal, 10)
                                
                            } label: { HStack(spacing: 6) { Image(systemName: "captions.bubble").font(.system(size: 16)); Text("字幕").font(.system(size: 14, weight: .bold)) }.padding(.horizontal, 16).padding(.vertical, 10).background(Color.black.opacity(0.75)).foregroundColor(.white).cornerRadius(8) }.menuStyle(.borderlessButton).fixedSize().colorScheme(.dark).simultaneousGesture(TapGesture().onEnded { beginMenuInteraction() })

                        }
                    }.foregroundColor(.white).padding(.horizontal, 30).padding(.bottom, 30).padding(.top, 40).background(LinearGradient(gradient: Gradient(colors: [.clear, .black.opacity(0.9)]), startPoint: .top, endPoint: .bottom))
                }.transition(.opacity)
            }
            
            // 快捷键映射
            Button("") { playerManager.playOrPause(); resetHideTimer() }.keyboardShortcut(.space, modifiers: []).opacity(0)
            Button("") { playerManager.seekRelative(seconds: Double(-seekDuration)); resetHideTimer() }.keyboardShortcut(.leftArrow, modifiers: []).opacity(0)
            Button("") { playerManager.seekRelative(seconds: Double(seekDuration)); resetHideTimer() }.keyboardShortcut(.rightArrow, modifiers: []).opacity(0)
            Button("") { playerManager.adjustSubtitleDelay(by: -0.5); resetHideTimer() }.keyboardShortcut("g", modifiers: []).opacity(0)
            Button("") { playerManager.adjustSubtitleDelay(by: 0.5); resetHideTimer() }.keyboardShortcut("h", modifiers: []).opacity(0)
            
        }
        .toolbar(isStandaloneWindow ? .visible : .hidden, for: .windowToolbar)
        .onContinuousHover { _ in if !showControls { withAnimation(.easeInOut(duration: 0.3)) { showControls = true } }; resetHideTimer() }
        .onAppear {
            log("onAppear movie=\(movie.title) initialFileId=\(initialFileId ?? "nil") seedFiles=\(initialPlaylistFiles?.count ?? 0) seedSource=\(initialSourceBasePath ?? "nil") seedProtocol=\(initialSourceProtocolType ?? "nil")")
            ensureComfortableWindowSize()
            prepareVideoWithPermission()
            resetHideTimer()
        }
        .onDisappear {
            traceClose("onDisappear triggered")
            hideUITask?.cancel()
            menuInteractionTask?.cancel()
            setCursorHidden(false)
            persistAndStopPlaybackIfNeeded(reason: "onDisappear")
        }
        .onReceive(NotificationCenter.default.publisher(for: .standalonePlayerShouldClose)) { _ in
            guard isStandaloneWindow else { return }
            traceClose("received standalonePlayerShouldClose notification")
            persistAndStopPlaybackIfNeeded(reason: "notification")
        }
        .onChange(of: videoURLs.count) { _, newCount in
            log("videoURLs updated count=\(newCount)")
            if newCount > 0 {
                hasIssuedLoad = false
                tryStartPlaybackIfReady()
            }
        }
        .onChange(of: playerManager.drawableBindVersion) { _, _ in
            tryStartPlaybackIfReady()
        }
        .onChange(of: errorMessage) { _, newValue in
            if let newValue {
                log("errorMessage=\(newValue)")
            }
        }
        .onChange(of: showControls) { _, isVisible in setCursorHidden(!isVisible) }
        .onChange(of: playerManager.currentPlaylistIndex) { _, newIndex in
            guard allPlaylistFiles.indices.contains(newIndex) else { return }
            guard let oldId = currentVideoFileId else {
                currentVideoFileId = allPlaylistFiles[newIndex].id
                return
            }
            let newFile = allPlaylistFiles[newIndex]
            guard oldId != newFile.id else { return }
            Task.detached {
                try? await AppDatabase.shared.dbQueue.write { db in
                    if var f = try VideoFile.fetchOne(db, key: oldId) {
                        let progress = max(0, min(f.playProgress, f.duration))
                        f.playProgress = progress
                        try f.update(db)
                    }
                }
                await MainActor.run { NotificationCenter.default.post(name: .libraryUpdated, object: nil) }
            }
            currentVideoFileId = newFile.id
        }
    }
    
    // ==========================================
    // 🌟 原生加载本地外挂字幕逻辑
    // ==========================================
    private func loadExternalSubtitle() {
        let wasPlaying = playerManager.isPlaying
        if wasPlaying { playerManager.playOrPause() } // 选文件前自动暂停
        
        let panel = NSOpenPanel()
        panel.message = "选择要挂载的字幕文件"
        // 仅允许常见的字幕格式
        panel.allowedContentTypes = [UTType(filenameExtension: "srt"), UTType(filenameExtension: "ass"), UTType(filenameExtension: "ssa"), UTType(filenameExtension: "vtt")].compactMap { $0 }
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        
        if panel.runModal() == .OK, let url = panel.url {
            let subName = url.lastPathComponent
            // 发送底层挂载命令，并强制选中这根新轨道
            playerManager.executeMpvCommand(["sub-add", url.path, "select", subName, "chi"])
        }
        
        if wasPlaying { playerManager.playOrPause() } // 选完自动恢复播放
    }
    
    private func resetHideTimer() {
        hideUITask?.cancel()
        guard !isMenuInteractionActive else { return }
        hideUITask = Task {
            try? await Task.sleep(nanoseconds: 1_500_000_000)
            guard !Task.isCancelled else { return }
            await MainActor.run {
                guard !isMenuInteractionActive else { return }
                withAnimation(.easeInOut(duration: 0.5)) { showControls = false }
            }
        }
    }

    private func beginMenuInteraction() {
        hideUITask?.cancel()
        menuInteractionTask?.cancel()
        isMenuInteractionActive = true
        if !showControls {
            withAnimation(.easeInOut(duration: 0.2)) { showControls = true }
        }
        setCursorHidden(false)
        menuInteractionTask = Task {
            try? await Task.sleep(nanoseconds: 10_000_000_000)
            guard !Task.isCancelled else { return }
            await MainActor.run {
                isMenuInteractionActive = false
                resetHideTimer()
            }
        }
    }

    private func completeMenuInteraction() {
        menuInteractionTask?.cancel()
        isMenuInteractionActive = false
        resetHideTimer()
    }
    
    private func setCursorHidden(_ hidden: Bool) {
        if isClosingPlayback && hidden {
            return
        }
        let shouldHide = hidden && isPlayerWindowFullScreen()
        guard shouldHide != isCursorHidden else { return }
        if shouldHide { NSCursor.hide() } else { NSCursor.unhide() }
        isCursorHidden = shouldHide
    }

    private func forceCursorVisible() {
        // Defensively unwind possible nested hide calls across fullscreen transitions.
        NSCursor.setHiddenUntilMouseMoves(false)
        for _ in 0..<10 { NSCursor.unhide() }
        NSCursor.arrow.set()
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.08) {
            NSCursor.setHiddenUntilMouseMoves(false)
            for _ in 0..<10 { NSCursor.unhide() }
            NSCursor.arrow.set()
        }
        isCursorHidden = false
    }
    
    private func prepareVideoWithPermission() {
        let cacheDirectory = OfflineCacheManager.shared.cacheDirectory
        log("prepare start cacheDir=\(cacheDirectory?.path ?? "nil")")
        if let seedSourceBasePath = initialSourceBasePath,
           let seedFiles = initialPlaylistFiles,
           !seedFiles.isEmpty {
            let startIndex = initialFileId.flatMap { targetId in
                seedFiles.firstIndex(where: { $0.id == targetId })
            } ?? 0
            let seedProtocolType = initialSourceProtocolType ?? MediaSourceProtocol.local.rawValue
            log("prepare try seed data files=\(seedFiles.count) startIndex=\(startIndex) source=\(seedSourceBasePath) protocol=\(seedProtocolType)")
            if applyPreparedPlayback(
                files: seedFiles,
                sourceBasePath: seedSourceBasePath,
                sourceProtocolType: seedProtocolType,
                sourceAuthConfig: initialSourceAuthConfig,
                startIndex: startIndex,
                cacheDirectory: cacheDirectory
            ) {
                log("prepare seed data applied")
                return
            }
            log("prepare seed data not applied, fallback DB")
        }
        
        let movieValue = movie
        let initialFileIdValue = initialFileId
        guard let dbQueue = AppDatabase.shared.dbQueue else {
            self.errorMessage = "数据库尚未初始化"
            return
        }

        Task.detached(priority: .userInitiated) {
            do {
                print("[PlayerScreen] prepare DB snapshot start")
                let snapshot: (files: [VideoFile], sourceBasePath: String, sourceProtocolType: String, sourceAuthConfig: String?, startIndex: Int)? = try await dbQueue.read { db in
                    let fetchedFiles = try VideoFile.fetchVisibleFiles(movieId: movieValue.id, in: db)
                    let files = fetchedFiles.enumerated().sorted {
                        MediaNameParser.episodeSortKey(for: $0.element.fileName, fallbackIndex: $0.offset) <
                        MediaNameParser.episodeSortKey(for: $1.element.fileName, fallbackIndex: $1.offset)
                    }.map(\.element)
                    guard !files.isEmpty,
                          let source = try files[0].request(for: VideoFile.mediaSource).fetchOne(db) else { return nil }

                    var startIndex = 0
                    if let targetId = initialFileIdValue,
                       let idx = files.firstIndex(where: { $0.id == targetId }) {
                        startIndex = idx
                    }
                    return (
                        files: files,
                        sourceBasePath: source.baseUrl,
                        sourceProtocolType: source.protocolType,
                        sourceAuthConfig: source.authConfig,
                        startIndex: startIndex
                    )
                }
                print("[PlayerScreen] prepare DB snapshot done files=\(snapshot?.files.count ?? 0)")

                guard let snapshot else {
                    await MainActor.run { self.errorMessage = "找不到该视频的文件记录" }
                    return
                }
                
                let applied = await MainActor.run {
                    applyPreparedPlayback(
                        files: snapshot.files,
                        sourceBasePath: snapshot.sourceBasePath,
                        sourceProtocolType: snapshot.sourceProtocolType,
                        sourceAuthConfig: snapshot.sourceAuthConfig,
                        startIndex: snapshot.startIndex,
                        cacheDirectory: cacheDirectory
                    )
                }
                if !applied {
                    await MainActor.run {
                        self.errorMessage = "文件不存在。请重新连接外置硬盘/NAS，或先缓存到本地后再播放。"
                    }
                } else {
                    print("[PlayerScreen] prepare DB snapshot applied")
                }
            } catch {
                await MainActor.run {
                    self.errorMessage = "数据库读取失败：\(error.localizedDescription)"
                }
            }
        }
    }
    
    private func closePlayer() {
        persistAndStopPlaybackIfNeeded(reason: "closePlayerButton")
        dismiss()
    }

    private func persistAndStopPlaybackIfNeeded(reason: String) {
        traceClose("persistAndStop begin reason=\(reason) hasPersisted=\(hasPersistedBeforeExit)")
        guard !hasPersistedBeforeExit else { return }
        hasPersistedBeforeExit = true
        isClosingPlayback = true
        hideUITask?.cancel()
        hideUITask = nil
        showControls = true

        let progressToSave = playerManager.currentTimePos
        let durationToSave = playerManager.duration
        let fileIdToSave = currentVideoFileId
        let isTVPlaybackAtClose = isTVPlayback
        traceClose("snapshot fileId=\(fileIdToSave ?? "nil") progress=\(progressToSave) duration=\(durationToSave) isTV=\(isTVPlaybackAtClose)")
        forceCursorVisible()
        traceClose("calling playerManager.stop()")
        playerManager.stop()
        
        if let fileId = fileIdToSave {
            Task.detached {
                let formatter = ISO8601DateFormatter()
                formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
                let queue = String(cString: __dispatch_queue_get_label(nil), encoding: .utf8) ?? "unknown"
                let thread = "bg"
                print("[PlayerScreenClose][\(formatter.string(from: Date()))][\(thread)][q:\(queue)] progress save task begin fileId=\(fileId)")
                do {
                    try await AppDatabase.shared.dbQueue.write { db in
                        if var file = try VideoFile.fetchOne(db, key: fileId) {
                            let finished = durationToSave > 0 && (progressToSave / durationToSave) >= 0.95
                            let thresholded = progressToSave > 5.0 ? progressToSave : 0.0
                            file.playProgress = finished ? 0.0 : thresholded
                            file.duration = durationToSave
                            try file.update(db)
                        }
                    }
                    await MainActor.run { NotificationCenter.default.post(name: .libraryUpdated, object: nil) }
                    let doneFormatter = ISO8601DateFormatter()
                    doneFormatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
                    let doneQueue = String(cString: __dispatch_queue_get_label(nil), encoding: .utf8) ?? "unknown"
                    let doneThread = "bg"
                    print("[PlayerScreenClose][\(doneFormatter.string(from: Date()))][\(doneThread)][q:\(doneQueue)] progress save task done fileId=\(fileId)")
                } catch {}
            }
        }
    }
    
    private func toggleFullScreen() {
        guard let keyWindow = NSApplication.shared.windows.first(where: { $0.isKeyWindow }) else { return }
        let targetWindow = keyWindow.sheetParent ?? keyWindow
        targetWindow.toggleFullScreen(nil)
    }
    private func setSubtitleSize(_ size: Int) { playerManager.setSubtitleSize(size); resetHideTimer() }
    private func subtitleSizeLabel(_ size: Int) -> String { switch size { case 12: return "小"; case 16: return "标准"; case 20: return "大"; case 24: return "特大"; default: return "标准" } }
    private func ensureComfortableWindowSize() {
        guard let window = NSApplication.shared.windows.first(where: { $0.isKeyWindow }) else { return }
        guard !window.styleMask.contains(.fullScreen) else { return }
        let minSize = NSSize(width: 1100, height: 680)
        window.minSize = minSize
        let current = window.frame.size
        if current.width < minSize.width || current.height < minSize.height {
            let newRect = NSRect(
                x: window.frame.origin.x,
                y: window.frame.origin.y,
                width: max(current.width, minSize.width),
                height: max(current.height, minSize.height)
            )
            // Avoid NSWindowTransformAnimation teardown crashes on macOS 26.x.
            window.setFrame(newRect, display: true, animate: false)
        }
    }
    private func isPlayerWindowFullScreen() -> Bool {
        guard let keyWindow = NSApplication.shared.windows.first(where: { $0.isKeyWindow }) else { return false }
        let targetWindow = keyWindow.sheetParent ?? keyWindow
        return targetWindow.styleMask.contains(.fullScreen)
    }
    
    private func applyPreparedPlayback(
        files: [VideoFile],
        sourceBasePath: String,
        sourceProtocolType: String,
        sourceAuthConfig: String?,
        startIndex: Int,
        cacheDirectory: URL?
    ) -> Bool {
        log("applyPreparedPlayback files=\(files.count) startIndex=\(startIndex) source=\(sourceBasePath) protocol=\(sourceProtocolType)")
        guard !files.isEmpty else { return false }
        guard startIndex >= 0 && startIndex < files.count else { return false }

        let sourceKind = MediaSourceProtocol(rawValue: sourceProtocolType) ?? .local
        let normalizedBase = sourceKind.normalizedBaseURL(sourceBasePath)
        let sourceBaseUrl: URL
        let webDAVCredential = sourceKind == .webdav
            ? resolveWebDAVCredential(authConfig: sourceAuthConfig, baseURLString: normalizedBase)
            : nil
        switch sourceKind {
        case .webdav:
            guard let remoteBase = URL(string: normalizedBase) else { return false }
            sourceBaseUrl = remoteBase
        case .local, .direct:
            sourceBaseUrl = URL(fileURLWithPath: normalizedBase)
        }

        let targetFile = files[startIndex]
        let remainingFiles = Array(files[startIndex...])
        let isDirectOpenFile = targetFile.mediaType == "direct"

        func sourcePlaybackURL(for file: VideoFile) -> URL? {
            let relativePath = file.relativePath.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
            switch sourceKind {
            case .webdav:
                var composed = sourceBaseUrl
                if !relativePath.isEmpty {
                    for component in relativePath.split(separator: "/") {
                        composed.appendPathComponent(String(component))
                    }
                }
                if let credential = webDAVCredential,
                   var components = URLComponents(url: composed, resolvingAgainstBaseURL: false) {
                    components.user = credential.username
                    components.password = credential.password
                    return components.url
                }
                return composed
            case .local, .direct:
                return sourceBaseUrl.appendingPathComponent(relativePath)
            }
        }
        
        func localPlaybackURL(for file: VideoFile) -> URL? {
            guard let cacheDirectory else { return nil }
            let normalizedPath = file.relativePath.isEmpty
                ? file.fileName
                : file.relativePath.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
            let preferredURL = cacheDirectory
                .appendingPathComponent(String(file.sourceId))
                .appendingPathComponent(normalizedPath)
            if FileManager.default.fileExists(atPath: preferredURL.path) {
                return preferredURL
            }
            let legacyURL = cacheDirectory.appendingPathComponent(file.fileName)
            if FileManager.default.fileExists(atPath: legacyURL.path) {
                return legacyURL
            }
            return nil
        }
        
        guard let targetSourceURL = sourcePlaybackURL(for: targetFile) else { return false }
        let targetLocalURL = localPlaybackURL(for: targetFile)
        let targetExists: Bool = {
            if isDirectOpenFile { return true }
            if targetLocalURL != nil { return true }
            switch sourceKind {
            case .local:
                return FileManager.default.fileExists(atPath: targetSourceURL.path)
            case .webdav, .direct:
                return true
            }
        }()
        log("applyPreparedPlayback targetFile=\(targetFile.fileName) direct=\(isDirectOpenFile) targetLocal=\(targetLocalURL != nil) targetURL=\(targetSourceURL.path)")
        guard targetExists else { return false }
        
        var urls: [URL] = []
        var localCacheAccessCount = 0
        for file in remainingFiles {
            if let localPlaybackURL = localPlaybackURL(for: file) {
                urls.append(localPlaybackURL)
                localCacheAccessCount += 1
            } else {
                if let nasURL = sourcePlaybackURL(for: file),
                   (isDirectOpenFile || !nasURL.absoluteString.isEmpty) {
                    urls.append(nasURL)
                }
            }
        }
        guard !urls.isEmpty else { return false }
        log("applyPreparedPlayback urlsPrepared=\(urls.count) localCacheCount=\(localCacheAccessCount)")
        
        var isBDMV = false
        var bdRoot: String? = nil
        if localCacheAccessCount > 0 && targetLocalURL != nil {
            let firstURL = urls.first ?? targetSourceURL
            let isISOFile = firstURL.pathExtension.lowercased() == "iso"
            isBDMV = isISOFile
            bdRoot = isISOFile ? firstURL.path : nil
        } else if sourceBaseUrl.isFileURL, targetFile.relativePath.contains("BDMV/STREAM") {
            isBDMV = true
            let pathComps = targetFile.relativePath.components(separatedBy: "/")
            if let bdmvIdx = pathComps.firstIndex(of: "BDMV") {
                let parentComps = pathComps[0..<bdmvIdx]
                var root = sourceBaseUrl
                for comp in parentComps { root = root.appendingPathComponent(comp) }
                bdRoot = root.path
            }
        } else if sourceBaseUrl.isFileURL, targetSourceURL.pathExtension.lowercased() == "iso" {
            isBDMV = true
            bdRoot = targetSourceURL.path
        }
        
        let resolvedURLs = urls
        DispatchQueue.main.async {
            self.isBluRayFolder = isBDMV
            self.blurayRootPath = bdRoot
            self.allPlaylistFiles = remainingFiles
            self.currentVideoFileId = targetFile.id
            self.startPosition = targetFile.playProgress
            self.rootFolderURL = sourceBaseUrl
            self.videoURLs = resolvedURLs
            self.log("applyPreparedPlayback committed videoURLs=\(resolvedURLs.count) isBDMV=\(isBDMV) bdRoot=\(bdRoot ?? "nil")")
        }
        return true
    }

    private func resolveWebDAVCredential(authConfig: String?, baseURLString: String) -> (username: String, password: String)? {
        if let id = WebDAVCredentialStore.shared.credentialID(from: authConfig),
           let stored = WebDAVCredentialStore.shared.loadCredential(id: id) {
            return (stored.username, stored.password)
        }
        if let legacy = WebDAVCredentialStore.shared.decodeLegacyCredential(from: authConfig) {
            return (legacy.username, legacy.password)
        }
        if let url = URL(string: baseURLString), let user = url.user, !user.isEmpty {
            return (user, url.password ?? "")
        }
        return nil
    }
    
    private func playNextEpisodeAndMarkCurrentWatched() {
        guard let currentId = currentVideoFileId else { return }
        let duration = playerManager.duration
        let currentTime = playerManager.currentTimePos
        
        Task.detached {
            do {
                try await AppDatabase.shared.dbQueue.write { db in
                    if var file = try VideoFile.fetchOne(db, key: currentId) {
                        let resolvedDuration = max(duration, file.duration)
                        file.duration = resolvedDuration
                        file.playProgress = resolvedDuration > 0 ? resolvedDuration : max(currentTime, file.playProgress)
                        try file.update(db)
                    }
                }
                await MainActor.run {
                    NotificationCenter.default.post(name: .libraryUpdated, object: nil)
                    playerManager.executeMpvCommand(["playlist-next", "force"])
                    resetHideTimer()
                }
            } catch {
                await MainActor.run {
                    playerManager.executeMpvCommand(["playlist-next", "force"])
                    resetHideTimer()
                }
            }
        }
    }
    
    private func tryStartPlaybackIfReady() {
        guard !hasIssuedLoad else { return }
        guard !videoURLs.isEmpty else { return }
        guard playerManager.drawableBindVersion > 0 else { return }
        hasIssuedLoad = true
        log("start playback after drawable ready version=\(playerManager.drawableBindVersion)")
        playerManager.loadFiles(urls: videoURLs, startPosition: startPosition, isBluRay: isBluRayFolder, blurayRootPath: blurayRootPath)
    }
}
