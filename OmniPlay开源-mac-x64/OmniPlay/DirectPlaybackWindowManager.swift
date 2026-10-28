import SwiftUI
import AppKit

@MainActor
final class DirectPlaybackWindowManager: NSObject, NSWindowDelegate {
    static let shared = DirectPlaybackWindowManager()

    private var window: NSWindow?
    // 拉起直开播放时藏起来的其它窗口（通常就是海报墙主窗口）。还原时只还原这一批：
    // 以前是「挑一个看着像的窗口」翻出来，会把不相干的窗口显示成第二个首页。
    private var hiddenWindows: [NSWindow] = []
    // 每次换播放页内容都自增，让延迟执行的「拆播放页」不会踩到刚装上的新内容
    private var contentEpoch = 0
    private var isInFullScreenTransition = false
    // 播放器一进来就装好，让探测与缓冲跟全屏动画并行；图层延后到动画落定再绑，
    // mpv 的视频输出因此只按最终尺寸建一次，不会再出现「首帧偏在一角」。
    private var pendingHandoff: DirectPlayerHandoff?
    private var transitionSweepWork: DispatchWorkItem?
    private var fullScreenSettleWork: DispatchWorkItem?
    // 关闭过渡期间被临时摘掉全屏能力的首页窗口：全屏还开着就把首页翻上来，AppKit 会顺手
    // 把它拽进全屏 Space（实测还原后首页自己带上全屏标记、frame 变成整屏）。
    // 值是该窗口过渡前的 collectionBehavior，动画落定后原样还回去。
    private var guardedHomeWindows: [NSWindow: NSWindow.CollectionBehavior] = [:]
    private var homeFullScreenGuardWork: DispatchWorkItem?
    // 用户那张首页。WindowGroup 在「用文件打开」这类外部事件里会另建一张首页窗口
    // （实测一次直开之后窗口名册从 1 张变 2 张，级联偏移），两张首页一起露脸就是
    // 「首页多了个觅影标签页」，关掉播放页时还原的还常常是新建的那张空首页，
    // 于是也回不到用户刚才看的详情页。这张留着，后建的那张关掉。
    private weak var canonicalHomeWindow: NSWindow?
    // 关闭之后延后执行的「退出全屏」
    private var exitFullScreenWork: DispatchWorkItem?
    private let exitFullScreenDelay: TimeInterval = 1.2
    // 关闭流程只走一遍：窗口是复用的，藏起来之后再点一次不该把退出全屏重新排一遍队
    private var isClosingPlaybackWindow = false
    /// 关闭流程是否还在收尾：给「应用重新激活」那条路查岗用，
    /// 否则退出全屏期间被 AppKit 翻回可见的播放窗口会被我们再顶到最前，用户又看见那块黑。
    var isPlaybackWindowClosing: Bool { isClosingPlaybackWindow }
    // 实测全屏动画约 1.5 s，兜底定时器要排在它后面，别抢正常动画的活
    private let transitionSweepDelay: TimeInterval = 3.0
    // 帧稳定多久就认动画落定：实测全屏动画约 1.5 s，太早放行等于让 mpv 按中间尺寸建输出
    private let settleMinDelay: TimeInterval = 1.0

    override private init() {
        super.init()
    }

    private func trace(_ message: String) {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let queue = String(cString: __dispatch_queue_get_label(nil), encoding: .utf8) ?? "unknown"
        let thread = Thread.isMainThread ? "main" : "bg"
        print("[DirectPlaybackWindowManager][\(formatter.string(from: Date()))][\(thread)][q:\(queue)] \(message)")
        // 时间线要能实时看到：进程被强杀时 stdio 缓冲区里的那几行就没了
        fflush(stdout)
    }

    /// 窗口名册：查「多出来一张首页」和「莫名多个标签页」用。
    func traceWindows(_ tag: String) {
        var lines: [String] = []
        for win in NSApp.windows {
            let group = win.tabGroup
            let tabCount = group?.windows.count ?? 0
            let selectedSuffix = group?.selectedWindow === win ? "·选中" : ""
            let tabFlag = tabCount > 1 ? "标签组x\(tabCount)\(selectedSuffix)" : nil
            let flags = [
                win.isVisible ? "可见" : nil,
                win.isMiniaturized ? "最小化" : nil,
                win.styleMask.contains(.fullScreen) ? "全屏" : nil,
                win === window ? "播放窗" : nil,
                tabFlag,
                win.isRestorable ? "可恢复" : nil
            ].compactMap { $0 }
            let flagText = flags.joined(separator: ",")
            lines.append("\(String(describing: type(of: win)))[\"\(win.title)\"][\(flagText)]\(NSStringFromRect(win.frame))")
        }
        trace("windows[\(tag)] count=\(NSApp.windows.count) \(lines.joined(separator: " | "))")
    }

    func open(_ request: DirectFilePlaybackManager.PlaybackRequest) {
        trace("open movie=\(request.movie.title) fileId=\(request.fileId)")
        traceWindows("open 前")
        isClosingPlaybackWindow = false
        enforceSingleHomeWindow("拉起播放")
        // 上一轮过渡还没归还的全屏能力先收回来，别带到这一轮
        releaseHomeWindowFullScreenGuard()
        // 排在队里的「退出全屏」作废：这条全屏 Space 正好给这条片子接着用
        exitFullScreenWork?.cancel()
        exitFullScreenWork = nil

        let target: NSWindow
        if let existing = window {
            target = existing
            if existing.isMiniaturized { existing.deminiaturize(nil) }
            existing.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
        } else {
            let created = makePlaybackWindow(title: request.movie.title)
            created.delegate = self
            window = created
            target = created
            created.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
        }
        hideOtherWindows(excluding: target)

        if target.styleMask.contains(.fullScreen) && !isInFullScreenTransition {
            apply(request, to: target)
            return
        }

        let handoff = DirectPlayerHandoff()
        pendingHandoff = handoff
        apply(request, to: target, handoff: handoff)
        requestFullScreen(for: target)
    }

    func closeCurrentWindow() {
        guard let existing = window else { return }
        guard !isClosingPlaybackWindow else {
            // 收尾期间又点一下：不能再啥也不做，否则会留下一块再也点不掉的黑色播放页。
            trace("closeCurrentWindow 已在关闭流程中，再补一次藏窗")
            window?.orderOut(nil)
            return
        }
        trace("closeCurrentWindow requested fullScreen=\(existing.styleMask.contains(.fullScreen)) inTransition=\(isInFullScreenTransition)")
        isClosingPlaybackWindow = true

        // 动画还没落定就被关掉：延后的图层绑定就地作废，别留一个永远不会来的回调
        settlePendingHandoff()
        // 先藏窗再收尾：点一下就得立刻没影。落库、停机、退出全屏全都挪到看不见的窗口上做，
        // 那 1.5 s 的退出全屏动画不再挡在用户和首页中间，通知丢不丢也就不影响关闭了。
        endFullScreenTransition()
        let wasFullScreen = existing.styleMask.contains(.fullScreen)
        hide(existing)

        // 进度落库与播放器停机（播放器侧自带去重，重复广播不会写两遍库）
        NotificationCenter.default.post(name: .standalonePlayerShouldClose, object: nil)
        if wasFullScreen {
            // 退出全屏要 1.6 s，其中约 1 s 主线程被 AppKit 占着（实测），而这条 Space 拆完
            // 之前屏幕上是空的——这就是「点了关闭先黑屏、卡好几秒」。
            // 首页已经翻上来了，把退出全屏押后到用户看清首页之后再走，黑屏就不挡在中间；
            // 期间再点开一条片子就直接复用这条全屏 Space，连动画都省掉。
            exitFullScreenWork?.cancel()
            let work = DispatchWorkItem { [weak self, weak existing] in
                guard let self, let existing, self.window === existing else { return }
                guard existing.styleMask.contains(.fullScreen) else { return }
                self.trace("关闭收尾：首页已就位，现在退出全屏")
                existing.toggleFullScreen(nil)
                self.beginFullScreenTransition()
            }
            exitFullScreenWork = work
            DispatchQueue.main.asyncAfter(deadline: .now() + exitFullScreenDelay, execute: work)
            trace("退出全屏押后 \(Int(exitFullScreenDelay * 1000))ms")
        }
    }

    func toggleCurrentWindowFullScreen() {
        guard let existing = window else { return }
        guard !isInFullScreenTransition else { return }
        existing.toggleFullScreen(nil)
        beginFullScreenTransition()
    }

    func focusPlaybackWindowIfVisible() -> Bool {
        guard let existing = window, existing.isVisible else { return false }
        isClosingPlaybackWindow = false
        if existing.isMiniaturized { existing.deminiaturize(nil) }
        existing.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        trace("focusPlaybackWindowIfVisible title=\(existing.title)")
        return true
    }

    func restoreWindowForReopen() -> Bool {
        if let existing = window, isRestorableForDockReopen(existing) {
            if existing.isMiniaturized { existing.deminiaturize(nil) }
            existing.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            trace("restoreWindowForReopen managed title=\(existing.title)")
            return true
        }

        if let candidate = NSApp.windows.first(where: { isRestorableForDockReopen($0) }) {
            if candidate.isMiniaturized { candidate.deminiaturize(nil) }
            candidate.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            trace("restoreWindowForReopen fallback title=\(candidate.title)")
            return true
        }

        // 我们自己藏起来的窗口还在，优先把它翻出来；直接返回 false 的话
        // AppKit 会认为「一个窗口都没有」，另建一张首页，屏幕上就凭空多出一个软件首页
        if let restored = showWindow(from: hiddenWindows) {
            hiddenWindows.removeAll()
            NSApp.activate(ignoringOtherApps: true)
            trace("restoreWindowForReopen hidden title=\(restored.title)")
            return true
        }

        trace("restoreWindowForReopen no candidate window")
        return false
    }

    private func isRestorableForDockReopen(_ candidate: NSWindow) -> Bool {
        candidate.isVisible || candidate.isMiniaturized
    }

    private func makePlaybackWindow(title: String) -> NSWindow {
        // 起步就按整屏大小建窗：万一全屏动画没起来，画面也不会在屏幕角落里
        let screenFrame = (NSScreen.main ?? NSScreen.screens.first)?.visibleFrame
            ?? NSRect(x: 0, y: 0, width: 1200, height: 760)
        let win = NSWindow(
            contentRect: NSRect(origin: .zero, size: screenFrame.size),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        win.title = title
        win.minSize = NSSize(width: 900, height: 560)
        win.titlebarAppearsTransparent = false
        win.titleVisibility = .visible
        win.collectionBehavior.insert(.fullScreenPrimary)
        // 播放窗口绝不能被并进首页的标签组：一旦被 AppKit 自动合桌，关窗就变成「关一个标签」，
        // 用户看到的是首页换了张标签而不是播放器消失。也不参与状态恢复，免得下次启动多出一张空播放窗。
        win.isRestorable = false
        win.tabbingMode = .disallowed
        win.contentView = makeBlankContentView(size: screenFrame.size)
        win.center()
        return win
    }

    private func makeBlankContentView(size: NSSize) -> NSView {
        let blank = NSView(frame: NSRect(origin: .zero, size: size))
        blank.wantsLayer = true
        blank.layer?.backgroundColor = NSColor.black.cgColor
        return blank
    }

    /// 装上播放页。`handoff` 有值时图层先不绑，等全屏动画落定由 `settlePendingHandoff()` 补上。
    private func apply(
        _ request: DirectFilePlaybackManager.PlaybackRequest,
        to window: NSWindow,
        handoff: DirectPlayerHandoff? = nil
    ) {
        contentEpoch += 1
        let playerView = PlayerScreen(
            movie: request.movie,
            initialFileId: request.fileId,
            isStandaloneWindow: true,
            initialSourceBasePath: request.initialSourceBasePath,
            initialSourceProtocolType: request.initialSourceProtocolType,
            initialSourceAuthConfig: request.initialSourceAuthConfig,
            initialPlaylistFiles: request.initialPlaylistFiles,
            videoOutputHandoff: handoff
        )
        window.title = request.movie.title
        window.contentView = NSHostingView(rootView: AnyView(playerView.id(request.id)))
    }

    /// 全屏动画落定（或超时兜底）：放行延后的图层绑定。
    private func settlePendingHandoff() {
        guard let handoff = pendingHandoff else { return }
        pendingHandoff = nil
        handoff.settle()
    }

    // MARK: - 全屏过渡

    private func requestFullScreen(for target: NSWindow) {
        guard !target.styleMask.contains(.fullScreen) else { return }
        let watched = target
        // 等窗口真正上屏再切全屏，否则动画会被激活流程打断，留下偏一角的半屏画面
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) {
            guard self.window === watched else { return }
            if !watched.styleMask.contains(.fullScreen) && !self.isInFullScreenTransition {
                watched.toggleFullScreen(nil)
                self.beginFullScreenTransition()
            }
        }
    }

    /// 过渡标记必须有出口：通知被系统吞掉时若不清掉，之后每次「关闭」都会被吞掉。
    /// 只在真正发出 toggleFullScreen 之后调用，标记本身由 willEnter/willExit 也会补一次。
    private func beginFullScreenTransition() {
        isInFullScreenTransition = true
        transitionSweepWork?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self, self.isInFullScreenTransition else { return }
            self.trace("全屏过渡超时无通知，清掉过渡标记")
            self.isInFullScreenTransition = false
            self.settlePendingHandoff()
        }
        transitionSweepWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + transitionSweepDelay, execute: work)
        if let target = window {
            pollFullScreenSettle(target: target, startedAt: Date(), lastFrame: nil, stableSamples: 0)
        }
    }

    /// 全屏通知不保证一定送到（锁屏/熄屏切换时 AppKit 会把 didEnterFullScreen 吞掉），
    /// 所以另加一条帧稳定兜底：窗口位置连续 0.2 s 不动就认动画落定，
    /// 不用每次干等 3 s 超时才补绑图层。3 s 超时仍是最后的出口。
    private func pollFullScreenSettle(target: NSWindow, startedAt: Date, lastFrame: NSRect?, stableSamples: Int) {
        fullScreenSettleWork?.cancel()
        let work = DispatchWorkItem { [weak self, weak target] in
            guard let self, let target, self.window === target, self.isInFullScreenTransition else { return }
            let elapsed = Date().timeIntervalSince(startedAt)
            let frame = target.frame
            let stable = (lastFrame != nil && frame == lastFrame) ? stableSamples + 1 : 0
            if elapsed >= self.settleMinDelay, target.styleMask.contains(.fullScreen), stable >= 2 {
                self.trace("全屏动画落定（帧稳定 \(Int(elapsed * 1000))ms），放行延后的图层绑定")
                self.endFullScreenTransition()
                self.settlePendingHandoff()
                return
            }
            self.pollFullScreenSettle(target: target, startedAt: startedAt, lastFrame: frame, stableSamples: stable)
        }
        fullScreenSettleWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1, execute: work)
    }

    private func endFullScreenTransition() {
        isInFullScreenTransition = false
        transitionSweepWork?.cancel()
        transitionSweepWork = nil
        fullScreenSettleWork?.cancel()
        fullScreenSettleWork = nil
    }

    // MARK: - NSWindowDelegate

    func windowDidExitFullScreen(_ notification: Notification) {
        guard let target = notification.object as? NSWindow, target === window else { return }
        trace("windowDidExitFullScreen")
        traceWindows("退出全屏之后")
        endFullScreenTransition()
        settlePendingHandoff()
        // 退出全屏这条动画会把窗口重新翻回可见（AppKit 要把它落回原来的 frame）。
        // 关闭流程里必须再按回去，否则用户看到的就是「关掉之后又冒出一块黑色播放页」。
        if isClosingPlaybackWindow, target.isVisible {
            trace("windowDidExitFullScreen 退出全屏把窗口翻回可见了，再藏一次")
            target.orderOut(nil)
        }
        releaseHomeWindowFullScreenGuardSoon()
    }

    func windowDidExpose(_ notification: Notification) {
        guard let target = notification.object as? NSWindow, target === window,
              isClosingPlaybackWindow else { return }
        trace("windowDidExpose 关闭收尾期间窗口又可见了，按回去")
        target.orderOut(nil)
    }

    func windowWillEnterFullScreen(_ notification: Notification) {
        if notification.object as? NSWindow === window {
            trace("windowWillEnterFullScreen")
            beginFullScreenTransition()
        }
    }

    func windowDidEnterFullScreen(_ notification: Notification) {
        guard let target = notification.object as? NSWindow, target === window else { return }
        trace("windowDidEnterFullScreen")
        endFullScreenTransition()
        settlePendingHandoff()
    }

    func windowWillExitFullScreen(_ notification: Notification) {
        if notification.object as? NSWindow === window {
            trace("windowWillExitFullScreen")
            beginFullScreenTransition()
        }
    }

    func windowWillClose(_ notification: Notification) {
        if notification.object as? NSWindow === window {
            trace("windowWillClose")
            settlePendingHandoff()
            endFullScreenTransition()
            releaseHomeWindowFullScreenGuard()
            exitFullScreenWork?.cancel()
            exitFullScreenWork = nil
            isClosingPlaybackWindow = false
            hiddenWindows.removeAll()
            window = nil
        }
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        guard sender === window else { return true }
        trace("windowShouldClose intercepted, hide window instead of close")
        closeCurrentWindow()
        return false
    }

    private func hide(_ target: NSWindow) {
        trace("hide window orderOut")
        target.orderOut(nil)
        traceWindows("藏播放窗之后")
        // 播放页整页拆掉：窗口是复用的，留着旧内容的话，下次点开播放会先闪出
        // 上一个视频的最后一帧，等新播放器装上才换过来。趁窗口已经看不见换回黑底。
        scheduleBlankContent(in: target)
        recoverCursorVisibility()
        // 首页当场翻上来：实测首页先上屏时退出全屏只要 150 ms 上下；首页还藏着的话，
        // AppKit 要把整条 Space 的拆除动画走完，1.6 s 之后才看得见首页。
        if target.styleMask.contains(.fullScreen) {
            guardHomeWindowsOutOfFullScreenTemporarily()
        }
        restoreHiddenWindows(excluding: target)
    }

    /// 过渡期间（含兜底）摘掉首页的全屏能力，免得被拽进播放窗口那条全屏 Space。
    private func guardHomeWindowsOutOfFullScreenTemporarily() {
        let targets = hiddenWindows.filter { !$0.collectionBehavior.contains(.fullScreenNone) }
        guard !targets.isEmpty else { return }
        for win in targets {
            guardedHomeWindows[win] = win.collectionBehavior
            // None：过渡期间这张窗口绝不能进全屏，AppKit 也就不会把它拽进播放窗口那条 Space。
            win.collectionBehavior.remove(.fullScreenPrimary)
            win.collectionBehavior.insert(.fullScreenNone)
        }
        homeFullScreenGuardWork?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self, !self.guardedHomeWindows.isEmpty else { return }
            self.trace("全屏过渡兜底：归还首页全屏能力")
            self.releaseHomeWindowFullScreenGuard()
        }
        homeFullScreenGuardWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + transitionSweepDelay, execute: work)
        trace("过渡期间摘掉 \(targets.count) 张首页的全屏能力")
    }

    /// 动画落定之后再归还全屏能力，早一步 AppKit 可能还没把 Space 交回普通桌面。
    private func releaseHomeWindowFullScreenGuard() {
        homeFullScreenGuardWork?.cancel()
        homeFullScreenGuardWork = nil
        guard !guardedHomeWindows.isEmpty else { return }
        for (win, behavior) in guardedHomeWindows {
            win.collectionBehavior = behavior
        }
        let count = guardedHomeWindows.count
        guardedHomeWindows.removeAll()
        trace("归还 \(count) 张首页的全屏能力")
        traceWindows("过渡收尾")
    }

    private func releaseHomeWindowFullScreenGuardSoon() {
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) { [weak self] in
            self?.releaseHomeWindowFullScreenGuard()
        }
    }

    /// 等 stop() 在控制队列上把视频输出摘掉再拆视图：图层是 mpv 按裸指针认的，
    /// 不能抢在它前面被回收。
    private func scheduleBlankContent(in target: NSWindow) {
        let epoch = contentEpoch
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) { [weak self, weak target] in
            guard let self, let target, self.window === target,
                  self.contentEpoch == epoch else { return }
            self.installBlankContent(in: target)
        }
    }

    private func installBlankContent(in target: NSWindow) {
        contentEpoch += 1
        let size = target.contentView?.frame.size ?? NSSize(width: 1200, height: 760)
        // 拆页面之前先确认窗口是看不见的：退出全屏的动画可能刚把它翻回来，
        // 这时候换内容，用户看到的就是「黑屏闪一下」。
        if isClosingPlaybackWindow, target.isVisible {
            trace("installBlankContent 前先补一次藏窗")
            target.orderOut(nil)
        }
        let startedAt = Date()
        target.contentView = makeBlankContentView(size: size)
        trace("installBlankContent 播放页已拆掉（拆 SwiftUI 视图 \(Int(Date().timeIntervalSince(startedAt) * 1000))ms），窗口留作复用")
    }

    private func recoverCursorVisibility() {
        // 播放器收回之后系统还会补几次「恢复光标」，跟着补几轮就够。
        // 轮次要克制：每轮都是一串 WindowServer 往返，全压在关闭后的那一帧上。
        let delays: [DispatchTimeInterval] = [
            .milliseconds(0), .milliseconds(120), .milliseconds(400), .milliseconds(1000)
        ]
        for delay in delays {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) {
                NSCursor.setHiddenUntilMouseMoves(false)
                for _ in 0..<3 { NSCursor.unhide() }
                NSCursor.arrow.set()
            }
        }
        trace("recoverCursorVisibility scheduled x\(delays.count)")
    }

    // MARK: - 主窗口与播放窗口的来回切换

    /// 首页只留一张：认下最早那张，后建的重复窗口直接关掉。
    @discardableResult
    func enforceSingleHomeWindow(_ tag: String) -> Bool {
        let candidates = NSApp.windows.filter { isMainContentWindow($0) }
        guard !candidates.isEmpty else { return false }
        if let canonical = canonicalHomeWindow, !candidates.contains(where: { $0 === canonical }) {
            canonicalHomeWindow = nil   // 那张已经被用户关了，重新认一张
        }
        if canonicalHomeWindow == nil {
            let preferred = NSApp.mainWindow ?? NSApp.keyWindow
            canonicalHomeWindow = candidates.first { $0 === preferred } ?? candidates.first
        }
        guard let canonical = canonicalHomeWindow else { return false }
        // 只关「和首页同一种窗口」：重复出来的那张就是同一个场景窗口类，
        // sheet、弹层这些即便漏过上面的判据也不会被误杀。
        let extras = candidates.filter { $0 !== canonical && type(of: $0) == type(of: canonical) }
        guard !extras.isEmpty else { return false }
        for extra in extras {
            let title = extra.title
            extra.close()
            trace("关掉重复的首页窗口 \"\(title)\"（\(tag)）")
        }
        traceWindows("清掉重复首页之后")
        return true
    }

    /// 能当主窗口的候选：应用自己的普通顶层窗口。
    ///
    /// 判据必须严格。之前只看了「带标题 + 不是 NSPanel + 没有父窗口」，结果 SwiftUI 的 sheet
    /// 承载窗口（`SheetPresentationWindow`，标题为空、没有 parent）也被算成首页，
    /// 一拿到键盘就被 `enforceSingleHomeWindow` 关掉，SwiftUI 立刻重挂——设置弹窗就是这么
    /// 来回闪烁、并且关不掉的（实测每秒重复十几次「关掉重复的首页窗口 ""」）。
    /// 表窗、弹层、全屏过渡遮罩、工具栏遮罩一律排除在外。
    func isMainContentWindow(_ candidate: NSWindow) -> Bool {
        guard candidate !== window,
              candidate.styleMask.contains(.titled),
              // 只有「能当主窗口」的才是首页候选：sheet 承载窗能拿键盘但永远成不了主窗口，
              // 这一条把它们连类名比对都省了。
              candidate.canBecomeMain,
              !(candidate is NSPanel),
              candidate.parent == nil,
              candidate.sheetParent == nil,
              candidate.level == .normal else { return false }
        let className = String(describing: type(of: candidate))
        let notHome = ["Sheet", "Popover", "Overlay", "Transition", "Toolbar", "Panel", "Alert"]
        return !notHome.contains { className.contains($0) }
    }

    /// 把其它普通窗口一并藏起来，并记下到底藏了谁，还原时只还原这一批。
    private func hideOtherWindows(excluding playbackWindow: NSWindow) {
        var hidden: [NSWindow] = []
        var inventory: [String] = []
        // 当前主窗口排最前：还原时优先回到用户刚才在看的那一张，不会被别的窗口顶掉
        let preferred = NSApp.mainWindow ?? NSApp.keyWindow
        for candidate in NSApp.windows where isMainContentWindow(candidate) && candidate !== playbackWindow {
            guard candidate.isVisible || candidate.isMiniaturized else { continue }
            if candidate.isVisible { candidate.orderOut(nil) }
            inventory.append("\(type(of: candidate))\"\(candidate.title)\"\(NSStringFromRect(candidate.frame))")
            if candidate === preferred {
                hidden.insert(candidate, at: 0)
            } else {
                hidden.append(candidate)
            }
        }
        if canonicalHomeWindow == nil {
            let preferred = NSApp.mainWindow ?? NSApp.keyWindow
            canonicalHomeWindow = hidden.first { $0 === preferred } ?? hidden.first
        }
        // 已经藏过一批就并进去，别让后一次打开把先前那批忘掉，关闭时只还原一半
        hiddenWindows.append(contentsOf: hidden.filter { candidate in
            !hiddenWindows.contains(where: { $0 === candidate })
        })
        trace("hideOtherWindows hidden=\(hiddenWindows.count) windows=[\(inventory.joined(separator: " | "))]")
    }

    private func restoreHiddenWindows(excluding hiddenPlayerWindow: NSWindow) {
        let recorded = hiddenWindows
        hiddenWindows.removeAll()
        let candidates = orderedForRestore(recorded.filter { $0 !== hiddenPlayerWindow })
        if let restored = showWindow(from: candidates) {
            NSApp.activate(ignoringOtherApps: true)
            trace("restoreHiddenWindows title=\(restored.title)")
            traceWindows("还原首页之后")
            return
        }
        // 没有自己藏起来的窗口可还原时不再乱翻窗口：挑一个应用的普通窗口，
        // 一个都没有就只激活应用，剩下的交给 reopen 处理
        if let restored = showWindow(from: orderedForRestore(NSApp.windows.filter { isMainContentWindow($0) })) {
            NSApp.activate(ignoringOtherApps: true)
            trace("restoreHiddenWindows fallback title=\(restored.title)")
        } else {
            NSApp.activate(ignoringOtherApps: true)
            trace("restoreHiddenWindows no candidate window")
        }
    }

    /// 还原顺序：用户那张首页（上面还停在他看的详情页）排最前，别被后建的空首页顶掉。
    private func orderedForRestore(_ windows: [NSWindow]) -> [NSWindow] {
        guard let canonical = canonicalHomeWindow,
              let index = windows.firstIndex(where: { $0 === canonical }), index > 0 else { return windows }
        var ordered = windows
        ordered.insert(ordered.remove(at: index), at: 0)
        return ordered
    }

    /// 显示候选里第一个还活着的窗口；返回 nil 说明候选都已经关掉了。
    private func showWindow(from candidates: [NSWindow]) -> NSWindow? {
        let live = Set(NSApp.windows.map { ObjectIdentifier($0) })
        for candidate in candidates where live.contains(ObjectIdentifier(candidate)) {
            if candidate.isMiniaturized { candidate.deminiaturize(nil) }
            if !candidate.isVisible { candidate.makeKeyAndOrderFront(nil) }
            return candidate
        }
        return nil
    }
}

/// 播放器与直开窗口之间的小交接件。
///
/// 直开播放会先进全屏再出画面，动画约 1.5 s。播放器提前装好，探测与缓冲跟动画并行；
/// 但图层必须等动画落定再绑——mpv 的视频输出只在初始化那一刻读图层尺寸，早绑就会按
/// 动画中途的尺寸出画（画面偏在一角）。动画通知落定或超时兜底时调用 `settle()`。
@MainActor
final class DirectPlayerHandoff {
    private(set) var isSettled = false
    private weak var manager: MPVPlayerManager?

    func attach(_ manager: MPVPlayerManager) {
        self.manager = manager
        manager.drawableHandoff = self
    }

    func settle() {
        guard !isSettled else { return }
        isSettled = true
        manager?.allowDeferredDrawableBinding()
    }
}
