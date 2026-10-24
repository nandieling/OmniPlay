import SwiftUI
import AppKit

@MainActor
final class DirectPlaybackWindowManager: NSObject, NSWindowDelegate {
    static let shared = DirectPlaybackWindowManager()

    private var window: NSWindow?
    private weak var primaryWindow: NSWindow?
    private var isInFullScreenTransition = false
    private var pendingHideAfterExitFullScreen = false
    // 全屏动画期间窗口尺寸一直在变，这时候装播放器会让 mpv 按中间尺寸绑定画面，
    // 首帧偏在一角、开局也卡。等动画落定（或超时）后再装。
    private var pendingContentRequest: DirectFilePlaybackManager.PlaybackRequest?
    private var pendingContentDeadline: Date?
    private var contentInstallWork: DispatchWorkItem?
    private var transitionSweepWork: DispatchWorkItem?
    private var closeWatchdogWork: DispatchWorkItem?
    private var closeWatchdogAttempts = 0
    // 实测全屏动画约 1.5 s，所有兜底定时器都要排在它后面，别抢正常动画的活
    private let contentInstallTimeout: TimeInterval = 2.5
    private let transitionSweepDelay: TimeInterval = 3.0
    private let closeWatchdogDelay: TimeInterval = 2.5

    override private init() {
        super.init()
    }

    private func trace(_ message: String) {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let queue = String(cString: __dispatch_queue_get_label(nil), encoding: .utf8) ?? "unknown"
        let thread = Thread.isMainThread ? "main" : "bg"
        print("[DirectPlaybackWindowManager][\(formatter.string(from: Date()))][\(thread)][q:\(queue)] \(message)")
    }

    func open(_ request: DirectFilePlaybackManager.PlaybackRequest) {
        trace("open movie=\(request.movie.title) fileId=\(request.fileId)")
        cancelCloseFlow()

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
        hidePrimaryWindowIfNeeded(excluding: target)

        if target.styleMask.contains(.fullScreen) && !isInFullScreenTransition {
            apply(request, to: target)
            cancelContentInstall()
            return
        }

        pendingContentRequest = request
        requestFullScreen(for: target)
        scheduleContentInstall(for: target)
    }

    func closeCurrentWindow() {
        guard let existing = window else { return }
        trace("closeCurrentWindow requested fullScreen=\(existing.styleMask.contains(.fullScreen)) inTransition=\(isInFullScreenTransition)")

        // 还没装上播放器就取消掉，否则窗口都藏了 mpv 才开始出声
        pendingContentRequest = nil
        cancelContentInstall()
        // 落库和停机与窗口动画无关，先无条件做掉，动画再怎么出意外进度也不会丢
        NotificationCenter.default.post(name: .standalonePlayerShouldClose, object: nil)

        if isInFullScreenTransition || existing.styleMask.contains(.fullScreen) {
            pendingHideAfterExitFullScreen = true
            if existing.styleMask.contains(.fullScreen) && !isInFullScreenTransition {
                trace("closeCurrentWindow trigger exit fullscreen first")
                existing.toggleFullScreen(nil)
                beginFullScreenTransition()
            }
            armCloseWatchdog(for: existing)
            return
        }

        cancelCloseFlow()
        hide(existing)
    }

    func toggleCurrentWindowFullScreen() {
        guard let existing = window else { return }
        guard !isInFullScreenTransition else { return }
        existing.toggleFullScreen(nil)
        beginFullScreenTransition()
    }

    func focusPlaybackWindowIfVisible() -> Bool {
        guard let existing = window, existing.isVisible else { return false }
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
        win.contentView = NSView(frame: NSRect(origin: .zero, size: screenFrame.size))
        win.contentView?.wantsLayer = true
        win.contentView?.layer?.backgroundColor = NSColor.black.cgColor
        win.center()
        return win
    }

    private func apply(_ request: DirectFilePlaybackManager.PlaybackRequest, to window: NSWindow) {
        let playerView = PlayerScreen(
            movie: request.movie,
            initialFileId: request.fileId,
            isStandaloneWindow: true,
            initialSourceBasePath: request.initialSourceBasePath,
            initialSourceProtocolType: request.initialSourceProtocolType,
            initialSourceAuthConfig: request.initialSourceAuthConfig,
            initialPlaylistFiles: request.initialPlaylistFiles
        )
        window.title = request.movie.title
        window.contentView = NSHostingView(rootView: AnyView(playerView.id(request.id)))
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
            if let target = self.window { self.installPendingContent(in: target) }
        }
        transitionSweepWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + transitionSweepDelay, execute: work)
    }

    private func endFullScreenTransition() {
        isInFullScreenTransition = false
        transitionSweepWork?.cancel()
        transitionSweepWork = nil
    }

    private func scheduleContentInstall(for target: NSWindow) {
        pendingContentDeadline = Date().addingTimeInterval(contentInstallTimeout)
        pumpContentInstall(for: target, delay: 0.2)
    }

    private func pumpContentInstall(for target: NSWindow, delay: TimeInterval) {
        contentInstallWork?.cancel()
        let work = DispatchWorkItem { [weak self, weak target] in
            guard let self, let target, self.window === target, self.pendingContentRequest != nil else { return }
            let deadline = self.pendingContentDeadline ?? Date()
            if self.isInFullScreenTransition && Date() < deadline {
                self.pumpContentInstall(for: target, delay: 0.2)
                return
            }
            self.installPendingContent(in: target)
        }
        contentInstallWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: work)
    }

    private func installPendingContent(in target: NSWindow) {
        guard let request = pendingContentRequest else { return }
        pendingContentRequest = nil
        cancelContentInstall()
        trace("install player fullScreen=\(target.styleMask.contains(.fullScreen))")
        apply(request, to: target)
    }

    private func cancelContentInstall() {
        pendingContentDeadline = nil
        contentInstallWork?.cancel()
        contentInstallWork = nil
    }

    // MARK: - 关闭兜底

    private func cancelCloseFlow() {
        pendingHideAfterExitFullScreen = false
        closeWatchdogAttempts = 0
        closeWatchdogWork?.cancel()
        closeWatchdogWork = nil
    }

    /// 退出全屏的通知不一定来，等不到就自己再试一次，最后强制藏窗，别留下关不掉的窗口。
    private func armCloseWatchdog(for target: NSWindow) {
        closeWatchdogWork?.cancel()
        let work = DispatchWorkItem { [weak self, weak target] in
            guard let self, let target, self.window === target, self.pendingHideAfterExitFullScreen else { return }
            self.trace("close watchdog fired fullScreen=\(target.styleMask.contains(.fullScreen)) inTransition=\(self.isInFullScreenTransition) attempts=\(self.closeWatchdogAttempts)")
            if !target.styleMask.contains(.fullScreen) {
                self.cancelCloseFlow()
                self.endFullScreenTransition()
                self.hide(target)
                return
            }
            if self.closeWatchdogAttempts < 2 {
                self.closeWatchdogAttempts += 1
                self.endFullScreenTransition()
                target.toggleFullScreen(nil)
                self.beginFullScreenTransition()
                self.armCloseWatchdog(for: target)
                return
            }
            self.trace("close watchdog 放弃退出全屏，直接藏窗")
            self.cancelCloseFlow()
            self.endFullScreenTransition()
            self.hide(target)
        }
        closeWatchdogWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + closeWatchdogDelay, execute: work)
    }

    // MARK: - NSWindowDelegate

    func windowDidExitFullScreen(_ notification: Notification) {
        guard let target = notification.object as? NSWindow, target === window else { return }
        trace("windowDidExitFullScreen")
        endFullScreenTransition()
        if pendingHideAfterExitFullScreen {
            cancelCloseFlow()
            DispatchQueue.main.async {
                guard self.window === target else { return }
                self.trace("windowDidExitFullScreen -> continue hide")
                self.hide(target)
            }
            return
        }
        installPendingContent(in: target)
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
        installPendingContent(in: target)
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
            pendingContentRequest = nil
            cancelContentInstall()
            cancelCloseFlow()
            endFullScreenTransition()
            primaryWindow = nil
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
        recoverCursorVisibility()
        restorePrimaryWindow(excluding: target)
    }

    private func recoverCursorVisibility() {
        let delays: [DispatchTimeInterval] = [
            .milliseconds(0), .milliseconds(60), .milliseconds(180), .milliseconds(360),
            .milliseconds(700), .milliseconds(1200), .milliseconds(1800), .milliseconds(2600)
        ]
        for delay in delays {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) {
                NSCursor.setHiddenUntilMouseMoves(false)
                for _ in 0..<10 { NSCursor.unhide() }
                NSCursor.arrow.set()
            }
        }
        trace("recoverCursorVisibility scheduled x\(delays.count)")
    }

    private func hidePrimaryWindowIfNeeded(excluding playbackWindow: NSWindow) {
        if primaryWindow == nil {
            primaryWindow = NSApp.windows.first(where: { $0 !== playbackWindow && $0.isVisible })
        }
        if let primaryWindow, primaryWindow !== playbackWindow {
            primaryWindow.orderOut(nil)
            trace("hidePrimaryWindowIfNeeded title=\(primaryWindow.title)")
        }
    }

    private func restorePrimaryWindow(excluding hiddenPlayerWindow: NSWindow) {
        if let primaryWindow, primaryWindow !== hiddenPlayerWindow {
            if primaryWindow.isMiniaturized { primaryWindow.deminiaturize(nil) }
            primaryWindow.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            trace("restorePrimaryWindow title=\(primaryWindow.title)")
            return
        }
        let fallback = NSApp.windows.first(where: { $0 !== hiddenPlayerWindow })
        if let fallback {
            if fallback.isMiniaturized { fallback.deminiaturize(nil) }
            fallback.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            trace("restorePrimaryWindow fallback title=\(fallback.title)")
        } else {
            NSApp.activate(ignoringOtherApps: true)
            trace("restorePrimaryWindow no candidate window")
        }
    }
}
