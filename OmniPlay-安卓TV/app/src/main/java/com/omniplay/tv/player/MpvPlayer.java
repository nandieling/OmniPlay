package com.omniplay.tv.player;

import android.os.Handler;
import android.os.Looper;
import android.view.Surface;

import java.util.ArrayList;

public final class MpvPlayer {
    // 进程内所有还活着的实例。换模式、换片子时任何一条路径漏了释放，实例就会躲在后台继续
    // 解码出声（电视上表现为退出软件后声音还在循环），留一份名册才好在退出时一网打尽。
    private static final ArrayList<MpvPlayer> LIVE = new ArrayList<>();

    public static void destroyAll() {
        ArrayList<MpvPlayer> snapshot;
        synchronized (LIVE) {
            snapshot = new ArrayList<>(LIVE);
        }
        for (MpvPlayer player : snapshot) {
            player.destroy();
        }
    }

    public interface PlaybackEventListener {
        void onPlaybackStarted();

        void onPlaybackError(String message);

        /**
         * 画面几何变了（换片、换分辨率、换显示模式）：显示比例得照新的尺寸再算一遍。
         * 不实现也没关系，这条只是给「保持比例 / 拉伸」那个开关用的。
         */
        default void onVideoGeometryChanged() {
        }
    }

    private final Handler main = new Handler(Looper.getMainLooper());
    private long handle;
    private boolean initialized;
    private boolean paused;
    private boolean directPlaybackMode;
    private String lastError;
    private PlaybackEventListener playbackEventListener;
    private final MpvBridge.EventListener nativeEventListener = (eventName, detail) -> main.post(() -> {
        if ("playback-restart".equals(eventName)) {
            PlaybackEventListener listener = playbackEventListener;
            if (listener != null) {
                listener.onPlaybackStarted();
                listener.onVideoGeometryChanged();
            }
        } else if ("video-reconfig".equals(eventName)) {
            PlaybackEventListener reconfigured = playbackEventListener;
            if (reconfigured != null) {
                reconfigured.onVideoGeometryChanged();
            }
        } else if ("error".equals(eventName)) {
            if (detail != null && !detail.isEmpty()) {
                lastError = detail;
            }
            PlaybackEventListener listener = playbackEventListener;
            if (listener != null) {
                listener.onPlaybackError(lastError == null || lastError.isEmpty() ? "播放失败。" : lastError);
            }
        }
    });

    private boolean ensureHandle() {
        if (handle != 0) {
            return true;
        }

        handle = MpvBridge.nativeCreate();
        if (handle == 0) {
            lastError = "无法创建 libmpv 播放器。";
            return false;
        }
        synchronized (LIVE) {
            if (!LIVE.contains(this)) {
                LIVE.add(this);
            }
        }
        MpvBridge.nativeSetPlaybackMode(handle, directPlaybackMode);
        MpvBridge.nativeSetEventListener(handle, nativeEventListener);
        return true;
    }

    public void setPlaybackEventListener(PlaybackEventListener listener) {
        playbackEventListener = listener;
        if (handle != 0) {
            MpvBridge.nativeSetEventListener(handle, listener == null ? null : nativeEventListener);
        }
    }

    public void setDirectPlaybackMode(boolean enabled) {
        directPlaybackMode = enabled;
        if (handle != 0 && !initialized) {
            MpvBridge.nativeSetPlaybackMode(handle, enabled);
        }
    }

    public boolean initialize() {
        if (initialized) {
            return true;
        }

        try {
            initialized = ensureHandle() && MpvBridge.nativeInitialize(handle);
            if (!initialized) {
                lastError = "无法初始化 libmpv，请确认 APK 内包含当前 ABI 的 libmpv.so。";
                if (handle != 0) {
                    MpvBridge.nativeDestroy(handle);
                    handle = 0;
                }
            }
            return initialized;
        } catch (UnsatisfiedLinkError error) {
            lastError = "无法加载 mpvbridge/libmpv：" + error.getMessage();
            return false;
        }
    }

    public void attachSurface(Surface surface, int width, int height) {
        try {
            if (!ensureHandle()) {
                return;
            }
            MpvBridge.nativeAttachSurface(handle, surface, Math.max(0, width), Math.max(0, height));
            initialize();
        } catch (UnsatisfiedLinkError error) {
            lastError = "无法加载 mpvbridge/libmpv：" + error.getMessage();
        }
    }

    public void detachSurface() {
        if (initialized) {
            MpvBridge.nativeDetachSurface(handle);
        }
    }

    public boolean load(String url, String cookieHeader, double startSeconds) {
        return load(url, cookieHeader, startSeconds, false);
    }

    public boolean load(String url, String cookieHeader, double startSeconds, boolean isoPlayback) {
        if (!initialize()) {
            return false;
        }

        paused = false;
        boolean loaded = MpvBridge.nativeLoad(handle, url, cookieHeader, "OmniPlay-Android/0.1", startSeconds, isoPlayback);
        if (!loaded) {
            String detail = MpvBridge.nativeLastError(handle);
            lastError = detail == null || detail.isEmpty()
                    ? "libmpv 加载播放地址失败。"
                    : "libmpv 加载播放地址失败：" + detail;
        }
        return loaded;
    }

    public boolean addSubtitle(String url, String cookieHeader) {
        return initialized && MpvBridge.nativeAddSubtitle(handle, url, cookieHeader);
    }

    public void togglePaused() {
        if (!initialized) {
            return;
        }

        paused = !paused;
        MpvBridge.nativeSetPaused(handle, paused);
    }

    public void seek(double seconds) {
        if (initialized) {
            MpvBridge.nativeSeek(handle, seconds);
        }
    }

    public void seekTo(double seconds) {
        if (initialized) {
            MpvBridge.nativeSeekAbsolute(handle, Math.max(0, seconds));
        }
    }

    public void setAudioTrack(String value) {
        if (initialized) {
            MpvBridge.nativeSetString(handle, "aid", value == null || value.isEmpty() ? "auto" : value);
        }
    }

    /**
     * 播放画质模式: trades demuxer buffering against startup latency and stalls.
     * {@code smooth} buffers less on weak boxes, {@code quality} read-aheads further
     * so high-bitrate remuxes keep a stable frame rate.
     */
    public void setQualityProfile(String mode) {
        if (!initialized) {
            return;
        }
        String maxBytes = "128MiB";
        String backBytes = "32MiB";
        String readahead = "20";
        if ("smooth".equals(mode)) {
            maxBytes = "64MiB";
            backBytes = "16MiB";
            readahead = "10";
        } else if ("quality".equals(mode)) {
            maxBytes = "512MiB";
            backBytes = "128MiB";
            readahead = "60";
        }
        MpvBridge.nativeSetString(handle, "demuxer-max-bytes", maxBytes);
        MpvBridge.nativeSetString(handle, "demuxer-max-back-bytes", backBytes);
        MpvBridge.nativeSetString(handle, "demuxer-readahead-secs", readahead);
    }

    public void setSubtitleTrack(String value) {
        if (initialized) {
            MpvBridge.nativeSetString(handle, "sid", value == null || value.isEmpty() ? "auto" : value);
        }
    }

    /**
     * 只有视频真正处于播放中（未暂停、未播放结束）才返回 true。
     * 用于同步 FLAG_KEEP_SCREEN_ON：暂停或播完后允许屏幕按系统策略息屏。
     */
    public boolean isPlaying() {
        if (!initialized || paused) {
            return false;
        }
        return "yes".equals(getStringProperty("idle", "no"));
    }

    /**
     * 显示比例。{@code stretchToFill} 时把显示宽高比强行写成屏幕比例（mpv 的 aspect 覆盖，
     * 画面会变形但铺满），否则交回 auto 让 mpv 自己留黑边。
     *
     * <p>直出模式（{@code vo=mediacodec_embed}）下 mpv 根本不渲染，这里只是不让它留着
     * 上一次设定的尾巴；那一路的黑边由 {@code SurfaceView.setAspectRatio} 负责。</p>
     */
    public void setAspectMode(boolean stretchToFill, String displayAspect) {
        if (!initialized) {
            return;
        }
        MpvBridge.nativeSetString(handle, "panscan", "0");
        MpvBridge.nativeSetString(handle, "aspect",
                stretchToFill && displayAspect != null && !displayAspect.isEmpty()
                        ? displayAspect : "auto");
    }

    /** 画面在屏幕上应该占的比例（已含像素宽高比）；读不到时返回 0，等几何事件再补一次。 */
    public double displayAspectRatio() {
        if (!initialized) {
            return 0;
        }
        double width = getDoubleProperty("dwidth", 0);
        double height = getDoubleProperty("dheight", 0);
        if (width > 1 && height > 1) {
            return width / height;
        }
        // dwidth / dheight 在部分构建里读不到，退回编码尺寸（字符串形如 "1920x1080"）。
        String size = getStringProperty("video-size", "");
        int at = size.indexOf('x');
        if (at <= 0) {
            return 0;
        }
        try {
            double codedWidth = Double.parseDouble(size.substring(0, at).trim());
            double codedHeight = Double.parseDouble(size.substring(at + 1).trim());
            return codedWidth > 1 && codedHeight > 1 ? codedWidth / codedHeight : 0;
        } catch (NumberFormatException malformed) {
            return 0;
        }
    }

    public String getStringProperty(String property, String fallback) {
        if (!initialized) {
            return fallback;
        }
        return MpvBridge.nativeGetString(handle, property, fallback);
    }

    public double getDoubleProperty(String property, double fallback) {
        if (!initialized) {
            return fallback;
        }
        return MpvBridge.nativeGetDouble(handle, property, fallback);
    }

    public double currentTimeSeconds() {
        if (!initialized) {
            return 0;
        }

        double playbackTime = MpvBridge.nativeGetDouble(handle, "playback-time", -1);
        if (Double.isFinite(playbackTime) && playbackTime > 0) {
            return playbackTime;
        }

        double timePosition = MpvBridge.nativeGetDouble(handle, "time-pos", -1);
        if (Double.isFinite(timePosition) && timePosition > 0) {
            return timePosition;
        }

        return Double.isFinite(playbackTime) && playbackTime >= 0 ? playbackTime : 0;
    }

    public double durationSeconds() {
        return initialized ? MpvBridge.nativeGetDouble(handle, "duration", 0) : 0;
    }

    public double remainingTimeSeconds() {
        if (!initialized) {
            return -1;
        }

        double playtimeRemaining = MpvBridge.nativeGetDouble(handle, "playtime-remaining", -1);
        if (Double.isFinite(playtimeRemaining) && playtimeRemaining >= 0) {
            return playtimeRemaining;
        }

        double timeRemaining = MpvBridge.nativeGetDouble(handle, "time-remaining", -1);
        return Double.isFinite(timeRemaining) && timeRemaining >= 0 ? timeRemaining : -1;
    }

    public double percentPosition() {
        double value = initialized ? MpvBridge.nativeGetDouble(handle, "percent-pos", -1) : -1;
        return Double.isFinite(value) ? Math.max(0, Math.min(100, value)) : -1;
    }

    public String lastError() {
        return lastError;
    }

    public void destroy() {
        synchronized (LIVE) {
            LIVE.remove(this);
        }
        if (handle != 0) {
            MpvBridge.nativeSetEventListener(handle, null);
            MpvBridge.nativeDestroy(handle);
            handle = 0;
            initialized = false;
        }
        playbackEventListener = null;
    }
}
