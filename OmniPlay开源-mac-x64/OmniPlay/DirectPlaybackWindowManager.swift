import SwiftUI
import AppKit

@MainActor
final class DirectPlaybackWindowManager: NSObject, NSWindowDelegate {
    static let shared = DirectPlaybackWindowManager()
    private static let playbackWindowIdentifier = NSUserInterfaceItemIdentifier("OmniPlayPlaybackWindow")

    private var window: NSWindow?
    private weak var primaryWindow: NSWindow?
    private var pendingRequestAfterExitFullScreen: DirectFilePlaybackManager.PlaybackRequest?
    private var isInFullScreenTransition = false
    private var pendingHideAfterExitFullScreen = false

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
        if let existing = window {
            if isInFullScreenTransition {
                pendingRequestAfterExitFullScreen = request
                if existing.isMiniaturized { existing.deminiaturize(nil) }
                existing.makeKeyAndOrderFront(nil)
                NSApp.activate(ignoringOtherApps: true)
                return
            }

            apply(request, to: existing)
            if existing.isMiniaturized { existing.deminiaturize(nil) }
            existing.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            hidePrimaryWindowIfNeeded(excluding: existing)
            if !existing.styleMask.contains(.fullScreen) {
                DispatchQueue.main.async {
                    guard self.window === existing else { return }
                    if !existing.styleMask.contains(.fullScreen) && !self.isInFullScreenTransition {
                        existing.toggleFullScreen(nil)
                    }
                }
            }
            return
        }

        let win = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1200, height: 760),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        win.title = request.movie.title
        win.identifier = Self.playbackWindowIdentifier
        win.minSize = NSSize(width: 900, height: 560)
        win.titlebarAppearsTransparent = false
        win.titleVisibility = .visible
        apply(request, to: win)
        win.delegate = self
        win.center()
        win.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        hidePrimaryWindowIfNeeded(excluding: win)
        DispatchQueue.main.async {
            guard self.window === win else { return }
            if !win.styleMask.contains(.fullScreen) && !self.isInFullScreenTransition {
                win.toggleFullScreen(nil)
            }
        }

        window = win
    }

    func closeCurrentWindow() {
        guard let existing = window else { return }
        trace("closeCurrentWindow requested fullScreen=\(existing.styleMask.contains(.fullScreen)) inTransition=\(isInFullScreenTransition)")

        if existing.styleMask.contains(.fullScreen) || isInFullScreenTransition {
            pendingHideAfterExitFullScreen = true
            if existing.styleMask.contains(.fullScreen) && !isInFullScreenTransition {
                trace("closeCurrentWindow trigger exit fullscreen first")
                existing.toggleFullScreen(nil)
            }
            return
        }

        finishClosing(existing)
    }

    func toggleCurrentWindowFullScreen() {
        guard let existing = window else { return }
        guard !isInFullScreenTransition else { return }
        existing.toggleFullScreen(nil)
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
        if let existing = window, existing.isVisible {
            if existing.isMiniaturized { existing.deminiaturize(nil) }
            existing.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            trace("restoreWindowForReopen managed title=\(existing.title)")
            return true
        } else if window != nil {
            trace("restoreWindowForReopen ignored hidden playback window")
        }

        if let candidate = NSApp.windows.first(where: { $0.identifier != Self.playbackWindowIdentifier }) {
            if candidate.isMiniaturized { candidate.deminiaturize(nil) }
            candidate.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            trace("restoreWindowForReopen fallback title=\(candidate.title)")
            return true
        }

        trace("restoreWindowForReopen no candidate window")
        return false
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
        window.contentView = NSHostingView(rootView: playerView)
    }

    func windowDidExitFullScreen(_ notification: Notification) {
        guard let target = notification.object as? NSWindow, target === window else { return }
        trace("windowDidExitFullScreen")
        isInFullScreenTransition = false
        if pendingHideAfterExitFullScreen {
            pendingHideAfterExitFullScreen = false
            DispatchQueue.main.async {
                guard self.window === target else { return }
                self.trace("windowDidExitFullScreen -> continue hide")
                self.closeCurrentWindow()
            }
            return
        }
        if let pending = pendingRequestAfterExitFullScreen {
            pendingRequestAfterExitFullScreen = nil
            DispatchQueue.main.async {
                guard self.window === target else { return }
                self.apply(pending, to: target)
                target.makeKeyAndOrderFront(nil)
                NSApp.activate(ignoringOtherApps: true)
            }
        }
    }

    func windowWillEnterFullScreen(_ notification: Notification) {
        if notification.object as? NSWindow === window {
            trace("windowWillEnterFullScreen")
            isInFullScreenTransition = true
        }
    }

    func windowDidEnterFullScreen(_ notification: Notification) {
        if notification.object as? NSWindow === window {
            trace("windowDidEnterFullScreen")
            isInFullScreenTransition = false
        }
    }

    func windowWillExitFullScreen(_ notification: Notification) {
        if notification.object as? NSWindow === window {
            trace("windowWillExitFullScreen")
            isInFullScreenTransition = true
        }
    }

    func windowWillClose(_ notification: Notification) {
        if notification.object as? NSWindow === window {
            trace("windowWillClose")
            pendingRequestAfterExitFullScreen = nil
            pendingHideAfterExitFullScreen = false
            isInFullScreenTransition = false
            primaryWindow = nil
            window = nil
        }
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        guard sender === window else { return true }
        trace("windowShouldClose intercepted, hide playback safely")
        closeCurrentWindow()
        return false
    }

    private func finishClosing(_ target: NSWindow) {
        trace("finishClosing playback window")
        NotificationCenter.default.post(name: .standalonePlayerShouldClose, object: nil)
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
