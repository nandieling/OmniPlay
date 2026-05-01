import SwiftUI
import AppKit
import QuartzCore
import Libmpv

private final class MPVContainerView: NSView {
    override func layout() {
        super.layout()
        layer?.frame = bounds
        if let metalLayer = layer as? CAMetalLayer {
            let scale = window?.backingScaleFactor ?? NSScreen.main?.backingScaleFactor ?? 2.0
            metalLayer.contentsScale = scale
            metalLayer.drawableSize = NSSize(width: bounds.width * scale, height: bounds.height * scale)
        }
    }
}

struct MPVVideoView: NSViewRepresentable {
    let playerManager: MPVPlayerManager

    final class Coordinator {
        var lastSize: CGSize = .zero
    }

    func makeCoordinator() -> Coordinator {
        Coordinator()
    }

    func makeNSView(context: Context) -> NSView {
        let view = MPVContainerView(frame: .zero)
        view.wantsLayer = true
        view.autoresizingMask = [.width, .height]

        let metalLayer = CAMetalLayer()
        metalLayer.backgroundColor = NSColor.black.cgColor
        metalLayer.frame = view.bounds
        view.layer = metalLayer
        context.coordinator.lastSize = view.bounds.size

        print("[MPVVideoView] makeNSView created")
        // Bind immediately so PlayerScreen can wait for drawable-ready before loadFiles.
        playerManager.setDrawable(view)

        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        let newSize = nsView.bounds.size
        if newSize != context.coordinator.lastSize {
            context.coordinator.lastSize = newSize
            // Rebind on fullscreen/resize to avoid stale drawable region (black area on right/bottom).
            playerManager.setDrawable(nsView)
        }
    }
}
