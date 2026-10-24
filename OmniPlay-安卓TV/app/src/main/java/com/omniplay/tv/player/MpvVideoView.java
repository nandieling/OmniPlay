package com.omniplay.tv.player;

import android.content.Context;
import android.graphics.SurfaceTexture;
import android.view.Gravity;
import android.view.Surface;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.TextureView;
import android.view.View;
import android.view.ViewGroup;
import android.widget.FrameLayout;

public final class MpvVideoView extends FrameLayout {
    public interface PlaybackListener {
        void onLoaded();

        void onError(String message);
    }

    private final MpvPlayer player = new MpvPlayer();
    private SurfaceView surfaceView;
    private TextureView textureView;
    private Surface textureSurface;
    private String pendingUrl;
    private String pendingCookie;
    private String pendingAudioTrack;
    private String pendingSubtitleTrack;
    private String pendingQualityProfile;
    private double pendingStartSeconds;
    private boolean pendingIsoPlayback;
    private boolean surfaceReady;
    private PlaybackListener playbackListener;
    private Runnable aspectChangeListener;
    private int frameHeightPx;
    private boolean stretchToFill;
    private boolean notifiedPlaybackStarted;
    private boolean released;

    public MpvVideoView(Context context) {
        this(context, false);
    }

    public MpvVideoView(Context context, boolean directPlaybackMode) {
        this(context, directPlaybackMode, directPlaybackMode);
    }

    /**
     * {@code useSurfaceView} 决定画面画在 SurfaceView 还是 TextureView 上。TextureView 每帧要多
     * 走一次 HWUI 合成与一次同步等待，4K / HDR 本就在帧预算边缘，这类文件与直出模式同一条路径。
     */
    public MpvVideoView(Context context, boolean directPlaybackMode, boolean useSurfaceView) {
        super(context);
        player.setDirectPlaybackMode(directPlaybackMode);
        setBackgroundColor(android.graphics.Color.BLACK);
        setFocusable(true);
        setFocusableInTouchMode(false);
        setClipChildren(false);
        setClipToPadding(false);
        if (useSurfaceView) {
            attachSurfaceView(context);
        } else {
            attachTextureView(context);
        }
        player.setPlaybackEventListener(new MpvPlayer.PlaybackEventListener() {
            @Override
            public void onVideoGeometryChanged() {
                // 换片、换分辨率、换模式之后视频尺寸才可知或已改变：比例要重算一次。
                applyAspectMode();
            }

            @Override
            public void onPlaybackStarted() {
                applyAspectMode();
                if (notifiedPlaybackStarted) {
                    return;
                }
                notifiedPlaybackStarted = true;
                PlaybackListener listener = playbackListener;
                if (listener != null) {
                    listener.onLoaded();
                }
            }

            @Override
            public void onPlaybackError(String message) {
                PlaybackListener listener = playbackListener;
                if (listener != null) {
                    listener.onError(message);
                }
            }
        });
    }

    public boolean play(String url, String cookieHeader, double startSeconds) {
        return play(url, cookieHeader, startSeconds, false);
    }

    public boolean play(String url, String cookieHeader, double startSeconds, boolean isoPlayback) {
        pendingUrl = url;
        pendingCookie = cookieHeader;
        pendingStartSeconds = Math.max(0, startSeconds);
        pendingIsoPlayback = isoPlayback;
        notifiedPlaybackStarted = false;
        if (surfaceReady) {
            return loadPending();
        }

        return true;
    }

    public void setPlaybackListener(PlaybackListener listener) {
        playbackListener = listener;
    }

    private static LayoutParams centeredMatch() {
        LayoutParams params = new LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT);
        params.gravity = Gravity.CENTER;
        return params;
    }

    /**
     * 显示比例：true = 拉伸铺满全屏（会变形），false = 保持影视自己的比例（留黑边）。
     *
     * <p>直出模式（{@code vo=mediacodec_embed}）是解码器把帧直接写进 SurfaceView，而
     * SurfaceView 默认把帧铺满整个视图，于是 2.35:1 的片子在 16:9 的电视上被上下拉开——
     * 「全屏画面被拉伸」就是这么来的。兼容模式那一路由 mpv 自己留黑边，拉伸靠改它的
     * {@code aspect}；两路都由下面这个开关一起管。</p>
     */
    public void setStretchToFill(boolean stretch) {
        stretchToFill = stretch;
        applyAspectMode();
    }

    public boolean isStretchToFill() {
        return stretchToFill;
    }

    /** 按当前模式与当前视频尺寸重算一次显示比例。调用多少次都没关系。 */
    public void applyAspectMode() {
        player.setAspectMode(stretchToFill, displayRatioText());
        applyChildBounds();
    }

    /**
     * 量视频视图自己：拉伸时铺满容器，保持比例时按影片比例在容器里居中摆一块，多出来的是
     * 容器这块黑底。没走 {@code SurfaceView.setAspectRatio}——那个方法在 SDK 里是隐藏的，
     * 应用侧调不到；量尺寸这招与 ExoPlayer 的 AspectRatioFrameLayout 同一个路子。
     */
    private void applyChildBounds() {
        View child = surfaceView != null ? surfaceView : textureView;
        if (child == null || !(child.getLayoutParams() instanceof FrameLayout.LayoutParams)) {
            return;
        }
        int width = ViewGroup.LayoutParams.MATCH_PARENT;
        int height = ViewGroup.LayoutParams.MATCH_PARENT;
        double videoRatio = stretchToFill ? 0 : player.displayAspectRatio();
        int frameWidth = getWidth();
        int frameHeight = getHeight();
        if (videoRatio > 0 && frameWidth > 0 && frameHeight > 0) {
            double frameRatio = (double) frameWidth / frameHeight;
            if (videoRatio >= frameRatio) {
                // 影片比屏幕扁：上下留黑边。
                width = frameWidth;
                height = Math.max(1, (int) Math.round(frameWidth / videoRatio));
            } else {
                height = frameHeight;
                width = Math.max(1, (int) Math.round(frameHeight * videoRatio));
            }
        }
        // 记下算出来的那块高度：布局还没跑完，问 child.getHeight() 会慢一步。
        frameHeightPx = height == ViewGroup.LayoutParams.MATCH_PARENT
                ? getHeight() : height;
        FrameLayout.LayoutParams params = (FrameLayout.LayoutParams) child.getLayoutParams();
        if (params.width != width || params.height != height) {
            params.width = width;
            params.height = height;
            params.gravity = Gravity.CENTER;
            child.setLayoutParams(params);
        }
        Runnable listener = aspectChangeListener;
        if (listener != null) {
            // 字幕层得知道画面现在占哪一块，否则留出来的黑边上一行字特别显眼。
            listener.run();
        }
    }

    /** 画面实际占的高度（像素）；拉伸或尺寸还不知道时就是容器高度。 */
    public int videoFrameHeight() {
        return frameHeightPx > 0 ? frameHeightPx : getHeight();
    }

    /** 画面下边缘到容器下边缘的距离（居中留的黑边高度）。 */
    public int letterboxInset() {
        int total = getHeight();
        int frame = videoFrameHeight();
        return frame > 0 && total > frame ? (total - frame) / 2 : 0;
    }

    /** 画面摆法变了的通知（含第一次布局那一下）。 */
    public void setOnAspectChanged(Runnable listener) {
        aspectChangeListener = listener;
    }

    @Override
    protected void onSizeChanged(int width, int height, int oldWidth, int oldHeight) {
        super.onSizeChanged(width, height, oldWidth, oldHeight);
        // 第一次布局时视频尺寸可能早就知道了，那一下得在这里补上。
        applyAspectMode();
    }

    /** 视图自己的宽高比（拉满时的目标比例）；还没布局时退到屏幕尺寸。 */
    private String displayRatioText() {
        int width = getWidth() > 0 ? getWidth() : getResources().getDisplayMetrics().widthPixels;
        int height = getHeight() > 0 ? getHeight() : getResources().getDisplayMetrics().heightPixels;
        if (width <= 0 || height <= 0) {
            return "";
        }
        return java.lang.String.format(java.util.Locale.ROOT, "%d:%d", width, height);
    }

    public boolean addSubtitle(String url, String cookieHeader) {
        return player.addSubtitle(url, cookieHeader);
    }

    public void togglePaused() {
        player.togglePaused();
    }

    public void seek(double seconds) {
        player.seek(seconds);
    }

    public void seekTo(double seconds) {
        player.seekTo(seconds);
    }

    public void setAudioTrack(String value) {
        pendingAudioTrack = value == null || value.isEmpty() ? "auto" : value;
        player.setAudioTrack(value);
    }

    /** Applies the 播放画质模式 buffer profile to the running player. */
    public void setQualityProfile(String mode) {
        pendingQualityProfile = mode;
        player.setQualityProfile(mode);
    }

    public void setSubtitleTrack(String value) {
        pendingSubtitleTrack = value == null || value.isEmpty() ? "auto" : value;
        player.setSubtitleTrack(value);
    }

    public String getStringProperty(String property, String fallback) {
        return player.getStringProperty(property, fallback);
    }

    public double getDoubleProperty(String property, double fallback) {
        return player.getDoubleProperty(property, fallback);
    }

    public String lastError() {
        return player.lastError();
    }

    public boolean isPlaying() {
        return player.isPlaying();
    }

    public double currentTimeSeconds() {
        return player.currentTimeSeconds();
    }

    public double durationSeconds() {
        return player.durationSeconds();
    }

    public double remainingTimeSeconds() {
        return player.remainingTimeSeconds();
    }

    public double percentPosition() {
        return player.percentPosition();
    }

    public void destroyPlayer() {
        if (released) {
            return;
        }
        released = true;
        playbackListener = null;
        player.setPlaybackEventListener(null);
        surfaceReady = false;
        player.detachSurface();
        releaseTextureSurface();
        player.destroy();
        // 渲染面已经没了，留着子视图只会让人以为还在播。
        removeAllViews();
    }

    /** 兜底：把进程里还活着的播放器全部停掉，不给后台留声音。 */
    public static void releaseAllPlayers() {
        MpvPlayer.destroyAll();
    }

    private void attachSurfaceView(Context context) {
        surfaceView = new SurfaceView(context);
        surfaceView.setFocusable(false);
        surfaceView.setFocusableInTouchMode(false);
        surfaceView.setZOrderOnTop(false);
        surfaceView.setZOrderMediaOverlay(false);
        addView(surfaceView, centeredMatch());
        surfaceView.getHolder().addCallback(new SurfaceHolder.Callback() {
            @Override
            public void surfaceCreated(SurfaceHolder holder) {
                surfaceReady = true;
                player.attachSurface(holder.getSurface(), surfaceView.getWidth(), surfaceView.getHeight());
                if (pendingUrl != null) {
                    loadPending();
                }
            }

            @Override
            public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {
                surfaceReady = true;
                player.attachSurface(holder.getSurface(), width, height);
                applyAspectMode();
            }

            @Override
            public void surfaceDestroyed(SurfaceHolder holder) {
                surfaceReady = false;
                player.detachSurface();
            }
        });
    }

    private void attachTextureView(Context context) {
        textureView = new TextureView(context);
        textureView.setOpaque(true);
        textureView.setFocusable(false);
        textureView.setFocusableInTouchMode(false);
        addView(textureView, centeredMatch());
        textureView.setSurfaceTextureListener(new TextureView.SurfaceTextureListener() {
            @Override
            public void onSurfaceTextureAvailable(SurfaceTexture surfaceTexture, int width, int height) {
                surfaceReady = true;
                if (width > 0 && height > 0) {
                    surfaceTexture.setDefaultBufferSize(width, height);
                }
                textureSurface = new Surface(surfaceTexture);
                player.attachSurface(textureSurface, width, height);
                if (pendingUrl != null) {
                    loadPending();
                }
            }

            @Override
            public void onSurfaceTextureSizeChanged(SurfaceTexture surfaceTexture, int width, int height) {
                if (width > 0 && height > 0) {
                    surfaceTexture.setDefaultBufferSize(width, height);
                }
                if (textureSurface != null) {
                    player.attachSurface(textureSurface, width, height);
                }
            }

            @Override
            public boolean onSurfaceTextureDestroyed(SurfaceTexture surfaceTexture) {
                surfaceReady = false;
                player.detachSurface();
                releaseTextureSurface();
                return true;
            }

            @Override
            public void onSurfaceTextureUpdated(SurfaceTexture surfaceTexture) {
            }
        });
    }

    private void releaseTextureSurface() {
        if (textureSurface != null) {
            textureSurface.release();
            textureSurface = null;
        }
    }

    private boolean loadPending() {
        applyPendingTracks();
        boolean loaded = player.load(pendingUrl, pendingCookie, pendingStartSeconds, pendingIsoPlayback);
        if (loaded) {
            applyPendingTracks();
        }
        PlaybackListener listener = playbackListener;
        if (!loaded && listener != null) {
            listener.onError(lastError());
        }
        return loaded;
    }

    private void applyPendingTracks() {
        if (pendingAudioTrack != null) {
            player.setAudioTrack(pendingAudioTrack);
        }
        if (pendingSubtitleTrack != null) {
            player.setSubtitleTrack(pendingSubtitleTrack);
        }
        if (pendingQualityProfile != null) {
            player.setQualityProfile(pendingQualityProfile);
        }
    }
}
