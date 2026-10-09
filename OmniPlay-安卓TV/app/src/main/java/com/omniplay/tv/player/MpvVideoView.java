package com.omniplay.tv.player;

import android.content.Context;
import android.graphics.SurfaceTexture;
import android.view.Surface;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.TextureView;
import android.view.ViewGroup;
import android.widget.FrameLayout;

public final class MpvVideoView extends FrameLayout {
    public interface PlaybackListener {
        void onLoaded();

        void onError(String message);
    }

    private final MpvPlayer player = new MpvPlayer();
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
    private boolean notifiedPlaybackStarted;

    public MpvVideoView(Context context) {
        this(context, false);
    }

    public MpvVideoView(Context context, boolean directPlaybackMode) {
        super(context);
        player.setDirectPlaybackMode(directPlaybackMode);
        setFocusable(true);
        setFocusableInTouchMode(false);
        setClipChildren(false);
        setClipToPadding(false);
        if (directPlaybackMode) {
            attachSurfaceView(context);
        } else {
            attachTextureView(context);
        }
        player.setPlaybackEventListener(new MpvPlayer.PlaybackEventListener() {
            @Override
            public void onPlaybackStarted() {
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
        playbackListener = null;
        player.setPlaybackEventListener(null);
        surfaceReady = false;
        player.detachSurface();
        releaseTextureSurface();
        player.destroy();
    }

    private void attachSurfaceView(Context context) {
        SurfaceView surfaceView = new SurfaceView(context);
        surfaceView.setFocusable(false);
        surfaceView.setFocusableInTouchMode(false);
        surfaceView.setZOrderOnTop(false);
        surfaceView.setZOrderMediaOverlay(false);
        addView(surfaceView, new LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
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
        addView(textureView, new LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
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
