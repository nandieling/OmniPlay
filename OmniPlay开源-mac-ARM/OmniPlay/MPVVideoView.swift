import SwiftUI
import AppKit
import QuartzCore
import Libmpv

private final class MPVMetalLayer: CAMetalLayer {
    override var drawableSize: CGSize {
        get { super.drawableSize }
        set {
            // MoltenVK can briefly force 1x1 during resize; ignoring that keeps mpv's
            // render target from getting stuck at a stale tiny size.
            guard newValue.width > 1, newValue.height > 1 else { return }
            super.drawableSize = newValue
        }
    }
}

private final class MPVContainerView: NSView {
    weak var playerManager: MPVPlayerManager?
    private var lastLayoutSize: CGSize = .zero
    private var windowObservationTokens: [NSObjectProtocol] = []
    private var isLiveResizing = false
    private var autoPausedForLiveResize = false
    private var resumeAfterLiveResizeWorkItem: DispatchWorkItem?
    private var videoOutputReinitWork: DispatchWorkItem?

    deinit {
        resumeAfterLiveResizeWorkItem?.cancel()
        videoOutputReinitWork?.cancel()
        removeWindowObservers()
    }

    override func layout() {
        super.layout()
        handleGeometryChange(rebuildLayer: false)
    }

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        handleGeometryChange(rebuildLayer: false)
    }

    override func setBoundsSize(_ newSize: NSSize) {
        super.setBoundsSize(newSize)
        handleGeometryChange(rebuildLayer: false)
    }

    override func viewDidChangeBackingProperties() {
        super.viewDidChangeBackingProperties()
        scheduleDrawableRefresh(delays: [0.0, 0.05], rebind: true)
        // 换屏/换倍率同理：像素尺寸变了，mpv 不会自己追
        scheduleVideoOutputReinit(reason: "backing-scale")
    }

    override func viewWillStartLiveResize() {
        super.viewWillStartLiveResize()
        beginLiveResize()
    }

    override func viewDidEndLiveResize() {
        super.viewDidEndLiveResize()
        endLiveResize()
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        installWindowObservers()
        scheduleDrawableRefresh(delays: [0.0, 0.12], rebind: true)
    }

    func ensureMetalLayer(rebind: Bool) {
        let metalLayer: CAMetalLayer
        if let existingLayer = layer as? CAMetalLayer {
            metalLayer = existingLayer
        } else {
            metalLayer = MPVMetalLayer()
            layer = metalLayer
        }
        configure(metalLayer)
        lastLayoutSize = bounds.size
        if rebind {
            playerManager?.setDrawable(self, force: true)
        }
    }

    private func installWindowObservers() {
        removeWindowObservers()
        guard let window else { return }
        let center = NotificationCenter.default
        let events: [(Notification.Name, [TimeInterval], Bool)] = [
            (NSWindow.didResizeNotification, [0.0, 0.016, 0.05], false),
            (NSWindow.didEnterFullScreenNotification, [0.0, 0.08, 0.22, 0.45], true),
            (NSWindow.didExitFullScreenNotification, [0.0, 0.08, 0.22, 0.45, 0.8], true)
        ]
        windowObservationTokens = events.map { eventName, delays, shouldRebind in
            center.addObserver(forName: eventName, object: window, queue: .main) { [weak self] _ in
                guard let self else { return }
                self.scheduleDrawableRefresh(delays: delays, rebind: shouldRebind || self.autoPausedForLiveResize)
            }
        }
        // 全屏动画彻底落定后重建一次视频输出。上面那些 rebind 只重设 wid，而 mpv 的 gpu-next
        // 在只有 CAMetalLayer 的嵌入路径上只在视频输出初始化时读图层尺寸，不重来就继续按旧尺寸出画：
        // 退出全屏被裁成一角、从小窗进全屏只剩左上角，所以两个方向都要重建。
        let reinitEvents: [(NSNotification.Name, String)] = [
            (NSWindow.didEnterFullScreenNotification, "enter-fullscreen"),
            (NSWindow.didExitFullScreenNotification, "exit-fullscreen")
        ]
        windowObservationTokens.append(contentsOf: reinitEvents.map { eventName, reason in
            center.addObserver(forName: eventName, object: window, queue: .main) { [weak self] _ in
                self?.scheduleVideoOutputReinit(reason: reason)
            }
        })
        let liveResizeTokens: [NSObjectProtocol] = [
            center.addObserver(forName: NSWindow.willStartLiveResizeNotification, object: window, queue: .main) { [weak self] _ in
                self?.beginLiveResize()
            },
            center.addObserver(forName: NSWindow.didEndLiveResizeNotification, object: window, queue: .main) { [weak self] _ in
                self?.endLiveResize()
                // 拖拽过程中一直按旧尺寸出画，松手后花一次重建的代价换回正确画面
                self?.scheduleVideoOutputReinit(reason: "end-live-resize")
            }
        ]
        windowObservationTokens.append(contentsOf: liveResizeTokens)
    }

    private func removeWindowObservers() {
        let center = NotificationCenter.default
        for token in windowObservationTokens {
            center.removeObserver(token)
        }
        windowObservationTokens = []
    }

    private func configure(_ metalLayer: CAMetalLayer) {
        let scale = window?.backingScaleFactor ?? NSScreen.main?.backingScaleFactor ?? 2.0
        let pointSize = bounds.size
        let drawableSize = NSSize(
            width: pointSize.width * scale,
            height: pointSize.height * scale
        )
        metalLayer.actions = [
            "bounds": NSNull(),
            "position": NSNull(),
            "frame": NSNull(),
            "contentsScale": NSNull(),
            "drawableSize": NSNull()
        ]
        metalLayer.backgroundColor = NSColor.black.cgColor
        metalLayer.frame = CGRect(origin: .zero, size: pointSize)
        metalLayer.contentsScale = scale
        if drawableSize.width > 1, drawableSize.height > 1 {
            metalLayer.drawableSize = drawableSize
        }
        metalLayer.presentsWithTransaction = false
        metalLayer.allowsNextDrawableTimeout = false
        metalLayer.framebufferOnly = true
        metalLayer.isOpaque = true
        metalLayer.masksToBounds = true
        metalLayer.autoresizingMask = [.layerWidthSizable, .layerHeightSizable]
        metalLayer.contentsGravity = .resize
        metalLayer.needsDisplayOnBoundsChange = true
        metalLayer.setNeedsDisplay()
    }

    private func handleGeometryChange(rebuildLayer: Bool) {
        refreshDrawableSize()

        let size = bounds.size
        guard size.width > 0, size.height > 0 else { return }
        if rebuildLayer || size != lastLayoutSize {
            lastLayoutSize = size
            let delays: [TimeInterval] = isLiveResizing ? [0.0, 0.016] : [0.0, 0.05]
            scheduleDrawableRefresh(
                delays: rebuildLayer ? [0.0, 0.08, 0.22, 0.45] : delays,
                rebind: rebuildLayer || autoPausedForLiveResize
            )
        }
    }

    private func beginLiveResize() {
        resumeAfterLiveResizeWorkItem?.cancel()
        resumeAfterLiveResizeWorkItem = nil
        isLiveResizing = true
        // 视频输出还没拿到图层（直开播放正等全屏动画落定）：屏幕上没有画面会被拉变形，
        // 为这次尺寸变化按暂停就是白等一秒多。
        if !autoPausedForLiveResize, playerManager?.isRenderingVideoOutput == true, playerManager?.isPlaying == true {
            autoPausedForLiveResize = true
            // 暂停与续播都交给播放器：它会在视频输出按新尺寸重建完成后自己续播
            playerManager?.pauseForGeometryChange(reason: "live-resize")
        }
        scheduleDrawableRefresh(delays: [0.0, 0.016, 0.033], rebind: autoPausedForLiveResize)
    }

    private func endLiveResize() {
        isLiveResizing = false
        scheduleDrawableRefresh(delays: [0.0, 0.08, 0.22, 0.45], rebind: true)
        resumeAfterLiveResizeIfNeeded()
    }

    private func resumeAfterLiveResizeIfNeeded() {
        guard autoPausedForLiveResize else { return }
        resumeAfterLiveResizeWorkItem?.cancel()
        let workItem = DispatchWorkItem { [weak self] in
            guard let self, self.window != nil, self.autoPausedForLiveResize else { return }
            self.refreshDrawable(rebind: true)
            self.autoPausedForLiveResize = false
            self.resumeAfterLiveResizeWorkItem = nil
            guard self.playerManager?.isRecoveringVideoGeometry != true else {
                // 尺寸纠偏还在进行中，续播由播放器在输出尺寸对上新尺寸之后自己触发
                return
            }
            self.playerManager?.setPaused(false)
        }
        resumeAfterLiveResizeWorkItem = workItem
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5, execute: workItem)
    }

    func refreshDrawable(rebind: Bool) {
        refreshDrawableSize()
        if rebind {
            playerManager?.setDrawable(self, force: true)
        }
    }

    private func refreshDrawableSize() {
        if let metalLayer = layer as? CAMetalLayer {
            configure(metalLayer)
        } else {
            ensureMetalLayer(rebind: false)
        }
    }

    /// 等布局稳定后重建 mpv 的视频输出，让它重新读一次图层尺寸。
    /// 一次全屏过渡会连着发来 enter/exit、live-resize、resize 好几个事件，全部合并成落定后的一次。
    private func scheduleVideoOutputReinit(reason: String) {
        videoOutputReinitWork?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self, self.window != nil else { return }
            self.layoutSubtreeIfNeeded()
            self.refreshDrawableSize()
            self.playerManager?.reinitVideoOutput(reason: reason)
        }
        videoOutputReinitWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.7, execute: work)
    }

    private func scheduleDrawableRefresh(delays: [TimeInterval], rebind: Bool) {
        for delay in delays {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
                guard let self, self.window != nil else { return }
                self.layoutSubtreeIfNeeded()
                self.refreshDrawable(rebind: rebind)
            }
        }
    }
}

struct MPVVideoView: NSViewRepresentable {
    let playerManager: MPVPlayerManager
    /// 直开播放的图层交接件。有值且全屏动画还没落定时先不绑图层。
    var handoff: DirectPlayerHandoff? = nil

    final class Coordinator {
        var lastSize: CGSize = .zero
    }

    func makeCoordinator() -> Coordinator {
        Coordinator()
    }

    func makeNSView(context: Context) -> NSView {
        let view = MPVContainerView(frame: .zero)
        view.playerManager = playerManager
        view.wantsLayer = true
        view.autoresizingMask = [.width, .height]
        view.layerContentsRedrawPolicy = .duringViewResize

        view.ensureMetalLayer(rebind: false)
        context.coordinator.lastSize = view.bounds.size

        print("[MPVVideoView] makeNSView created")
        handoff?.attach(playerManager)
        playerManager.setDrawable(view)

        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        let newSize = nsView.bounds.size
        if newSize != context.coordinator.lastSize {
            context.coordinator.lastSize = newSize
            if let videoView = nsView as? MPVContainerView {
                videoView.refreshDrawable(rebind: false)
            }
        }
    }
}
