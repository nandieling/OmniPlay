package com.omniplay.tv;

import android.app.Activity;
import android.content.pm.ActivityInfo;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.ArrayAdapter;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.GridLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.TextView;

import com.omniplay.tv.data.Models;
import com.omniplay.tv.data.LocalPlayApi;
import com.omniplay.tv.iso.RemoteIsoStreamServer;
import com.omniplay.tv.local.SmbStreamServer;
import com.omniplay.tv.player.MpvVideoView;
import com.omniplay.tv.ui.ImageLoader;

import java.io.IOException;
import java.util.ArrayList;
import java.util.List;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class PhoneMainActivity extends Activity {
    private static final int COLOR_BACKGROUND = Color.rgb(9, 14, 20);
    private static final int COLOR_SURFACE = Color.rgb(19, 28, 38);
    private static final int COLOR_SURFACE_ALT = Color.rgb(26, 38, 50);
    private static final int COLOR_TEXT = Color.rgb(246, 248, 250);
    private static final int COLOR_MUTED = Color.rgb(166, 176, 188);
    private static final int COLOR_FOCUS = Color.rgb(45, 212, 191);
    private static final int COLOR_DANGER = Color.rgb(248, 113, 113);

    private final Handler main = new Handler(Looper.getMainLooper());
    private final ExecutorService io = Executors.newFixedThreadPool(4);
    private FrameLayout root;
    private LocalPlayApi api;
    private final Map<String, Button> kindToggleButtons = new LinkedHashMap<>();
    private ImageLoader imageLoader;
    private Models.LibraryDetail currentDetail;
    private MpvVideoView playerView;
    private Models.VideoFile activePlaybackFile;
    private Runnable progressReporter;
    private Screen screen = Screen.HOME;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        getWindow().setStatusBarColor(COLOR_BACKGROUND);
        getWindow().setNavigationBarColor(COLOR_BACKGROUND);

        api = new LocalPlayApi(this);
        imageLoader = new ImageLoader(this);
        root = new FrameLayout(this);
        root.setBackgroundColor(COLOR_BACKGROUND);
        setContentView(root);

        // 独立版没有服务端要问，也不该有第二个「首次运行页」：海报墙永远是首屏。
        // 一个媒体源都没有时，墙上自带「添加媒体源」的入口（emptyLibraryCallToAction），
        // 设置页退成二级页，从右上角「设置」或那个入口进来。
        showHome();
    }

    private void showSetup(String message) {
        screen = Screen.SETUP;
        currentDetail = null;
        destroyPlayer();
        setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED);
        root.removeAllViews();
        root.setBackgroundColor(COLOR_BACKGROUND);

        ScrollView scroll = new ScrollView(this);
        root.addView(scroll, match());
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(22), dp(34), dp(22), dp(34));
        scroll.addView(page, matchWidth());

        LinearLayout setupHeader = new LinearLayout(this);
        setupHeader.setGravity(Gravity.CENTER_VERTICAL);
        page.addView(setupHeader, matchWidth());
        LinearLayout setupHeading = new LinearLayout(this);
        setupHeading.setOrientation(LinearLayout.VERTICAL);
        setupHeader.addView(setupHeading, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        setupHeading.addView(title("OmniPlay"));
        setupHeading.addView(body("独立版：添加本地目录、WebDAV 或 SMB 媒体源，自动扫描并用 TMDB 刮削海报。"));
        Button backToHome = compactButton("返回");
        setupHeader.addView(backToHome, new LinearLayout.LayoutParams(dp(88), dp(42)));
        backToHome.setOnClickListener(view -> showHome());

        TextView sourceSummary = text(api.engine().sources().isEmpty()
                ? "当前没有媒体源。"
                : "当前媒体源：" + api.engine().sources().size() + " 个。", 13, Typeface.BOLD, COLOR_MUTED);
        page.addView(sourceSummary, margin(matchWidth(), 0, dp(14), 0, 0));

        final TextView pathHint = text(kindHint("webdav"), 11, Typeface.NORMAL, COLOR_MUTED);
        LinearLayout kindRow = new LinearLayout(this);
        kindRow.setGravity(Gravity.CENTER_VERTICAL);
        page.addView(kindRow, margin(matchWidth(), 0, dp(6), 0, dp(8)));
        kindToggleButtons.clear();
        final String[] selectedKind = {"webdav"};
        Button localKind = kindToggleButton("local", "本地目录", selectedKind, pathHint);
        Button webDavKind = kindToggleButton("webdav", "WebDAV", selectedKind, pathHint);
        Button smbKind = kindToggleButton("smb", "SMB", selectedKind, pathHint);
        kindRow.addView(webDavKind, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(44)));
        kindRow.addView(smbKind, margin(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(44)), dp(12), 0, 0, 0));
        kindRow.addView(localKind, margin(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(44)), dp(12), 0, 0, 0));

        page.addView(pathHint, margin(matchWidth(), 0, dp(4), 0, 0));
        EditText pathInput = input("例如 /storage/emulated/0/Movies、https://nas:5006/dav 或 smb://192.168.0.10/video", "");
        page.addView(pathInput, margin(matchWidth(), 0, dp(8), 0, 0));
        EditText nameInput = input("显示名称（可选）", "");
        page.addView(nameInput, margin(matchWidth(), 0, dp(8), 0, 0));
        EditText usernameInput = input("用户名（WebDAV / SMB 可选）", "");
        page.addView(usernameInput, margin(matchWidth(), 0, dp(8), 0, 0));
        EditText passwordInput = input("密码（WebDAV / SMB 可选）", "");
        passwordInput.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        page.addView(passwordInput, margin(matchWidth(), 0, dp(8), 0, 0));

        EditText tmdbKey = input("TMDB API Key（或留空用反代）", api.engine().tmdb().apiKey());
        page.addView(tmdbKey, margin(matchWidth(), 0, dp(8), 0, 0));
        EditText tmdbProxy = inputWithLeadingIcon("留空直连官方 API；无法直连时可填写反向代理地址（支持带路径前缀）", api.engine().tmdb().proxyUrl());
        page.addView(tmdbProxy, margin(matchWidth(), 0, dp(12), 0, 0));

        if (message != null && !message.isEmpty()) {
            TextView error = body(message);
            error.setTextColor(COLOR_DANGER);
            page.addView(error);
        }
        TextView setupStatus = text("", 12, Typeface.BOLD, COLOR_MUTED);
        page.addView(setupStatus, margin(matchWidth(), 0, dp(8), 0, 0));

        Button enter = button("进入媒体库");
        page.addView(enter, margin(matchWidth(), 0, dp(14), 0, 0));
        enter.setOnClickListener(view -> showHome());

        Button saveAndScan = button("保存并扫描");
        page.addView(saveAndScan, margin(matchWidth(), 0, dp(10), 0, 0));
        saveAndScan.setOnClickListener(view -> {
            String path = pathInput.getText().toString().trim();
            if (path.isEmpty()) {
                setupStatus.setText("请填写媒体源地址。");
                return;
            }
            api.engine().tmdb().setApiKey(tmdbKey.getText().toString().trim());
            api.engine().tmdb().setProxyUrl(tmdbProxy.getText().toString().trim());
            String kind = selectedKind[0];
            api.engine().saveSource(kind, nameInput.getText().toString().trim(), path,
                    usernameInput.getText().toString().trim(), passwordInput.getText().toString().trim());
            showLoading("正在扫描媒体源");
            runAsync(
                    () -> api.engine().scanAll(status -> main.post(() -> {
                        if (screen == Screen.SETUP) {
                            setupStatus.setText(status);
                        }
                    })),
                    report -> {
                        if (screen != Screen.SETUP) {
                            return;
                        }
                        setupStatus.setText(report.message + " 待刮削 " + api.engine().scrapeQueueCount() + " 个条目。");
                        sourceSummary.setText("当前媒体源：" + api.engine().sources().size() + " 个。");
                        if (report.success) {
                            showHome();
                        }
                    },
                    error -> setupStatus.setText("扫描失败：" + error.getMessage()));
        });

        Button rescan = button("重新扫描");
        page.addView(rescan, margin(matchWidth(), 0, dp(10), 0, 0));
        rescan.setOnClickListener(view -> {
            api.engine().tmdb().setApiKey(tmdbKey.getText().toString().trim());
            api.engine().tmdb().setProxyUrl(tmdbProxy.getText().toString().trim());
            showLoading("正在扫描媒体源");
            runAsync(
                    () -> api.engine().scanAll(null),
                    report -> {
                        if (screen != Screen.SETUP) {
                            return;
                        }
                        setupStatus.setText(report.message + " 待刮削 " + api.engine().scrapeQueueCount() + " 个条目。");
                        if (report.success) {
                            showHome();
                        }
                    },
                    error -> setupStatus.setText("扫描失败：" + error.getMessage()));
        });

        Button scrape = button("刮削海报");
        page.addView(scrape, margin(matchWidth(), 0, dp(6), 0, 0));
        scrape.setOnClickListener(view -> {
            api.engine().tmdb().setApiKey(tmdbKey.getText().toString().trim());
            api.engine().tmdb().setProxyUrl(tmdbProxy.getText().toString().trim());
            if (!api.engine().tmdb().isConfigured()) {
                setupStatus.setText(api.engine().tmdbStatusMessage());
                return;
            }
            setupStatus.setText("正在刮削海报...");
            runAsync(
                    () -> {
                        api.engine().scrapePending(status -> main.post(() -> {
                            if (screen == Screen.SETUP) {
                                setupStatus.setText(status);
                            }
                        }));
                        return null;
                    },
                    ignored -> {
                        if (screen == Screen.SETUP) {
                            showHome();
                        }
                    },
                    error -> setupStatus.setText("刮削失败：" + error.getMessage()));
        });

        Button testConnection = button("测试连接");
        page.addView(testConnection, margin(matchWidth(), 0, dp(10), 0, 0));
        testConnection.setOnClickListener(view -> {
            String kind = selectedKind[0];
            String path = pathInput.getText().toString().trim();
            if (kind.equals("local")) {
                java.io.File dir = new java.io.File(path);
                setupStatus.setText(dir.isDirectory()
                        ? "本地目录可访问：" + dir.getAbsolutePath()
                        : "本地目录不存在或不可读。");
                return;
            }
            if (path.isEmpty()) {
                setupStatus.setText("请填写媒体源地址后再测试连接。");
                return;
            }
            String username = usernameInput.getText().toString().trim();
            String password = passwordInput.getText().toString().trim();
            setupStatus.setText("正在测试连接...");
            testConnection.setEnabled(false);
            runAsync(
                    () -> {
                        if (kind.equals("smb")) {
                            api.engine().testSmbConnection(path, username, password);
                        } else {
                            com.omniplay.tv.local.WebDavClient client =
                                    new com.omniplay.tv.local.WebDavClient(
                                            path, new com.omniplay.tv.local.WebDavClient.Credential(username, password));
                            client.list(client.rootUrl());
                        }
                        return null;
                    },
                    ignored -> {
                        testConnection.setEnabled(true);
                        setupStatus.setText("连接成功，可以保存并扫描。");
                    },
                    error -> {
                        testConnection.setEnabled(true);
                        setupStatus.setText("连接失败：" + error.getMessage());
                    });
        });
    }

    private void showHome() {
        screen = Screen.HOME;
        currentDetail = null;
        destroyPlayer();
        setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED);
        showLoading("正在加载媒体库");
        runAsync(
                () -> api.getLibraryItems(),
                this::renderHome,
                error -> showSetup(error.getMessage()));
    }

    private void renderHome(List<Models.LibraryItem> items) {
        root.removeAllViews();
        root.setBackgroundColor(COLOR_BACKGROUND);

        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(16), dp(22), dp(16), 0);
        root.addView(page, match());

        LinearLayout header = new LinearLayout(this);
        header.setGravity(Gravity.CENTER_VERTICAL);
        page.addView(header, margin(matchWidth(), 0, 0, 0, dp(16)));

        LinearLayout heading = new LinearLayout(this);
        heading.setOrientation(LinearLayout.VERTICAL);
        header.addView(heading, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        heading.addView(title("媒体库"));
        TextView server = text(api.engine().sources().isEmpty()
                ? "独立版 · 本地媒体库"
                : "独立版 · " + api.engine().sources().size() + " 个媒体源", 13, Typeface.NORMAL, COLOR_MUTED);
        server.setSingleLine(true);
        server.setEllipsize(TextUtils.TruncateAt.MIDDLE);
        heading.addView(server);

        Button refresh = compactButton("刷新");
        header.addView(refresh, new LinearLayout.LayoutParams(dp(76), dp(42)));
        refresh.setOnClickListener(view -> showHome());

        Button settings = compactButton("设置");
        header.addView(settings, margin(new LinearLayout.LayoutParams(dp(76), dp(42)), dp(8), 0, 0, 0));
        settings.setOnClickListener(view -> showSetup(null));

        ScrollView scroll = new ScrollView(this);
        page.addView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f));
        GridLayout grid = new GridLayout(this);
        grid.setColumnCount(2);
        scroll.addView(grid);

        for (Models.LibraryItem item : items) {
            grid.addView(posterCard(item));
        }

        if (items.isEmpty()) {
            page.addView(emptyLibraryCallToAction());
        }
    }

    /**
     * 海报墙为空时自带的入口。以前一个媒体源都没有会先甩一整屏设置页，
     * 现在首屏就是墙，这里用一颗按钮通向同一张表单。
     */
    private View emptyLibraryCallToAction() {
        final boolean noSources = api.engine().sources().isEmpty();
        LinearLayout block = new LinearLayout(this);
        block.setOrientation(LinearLayout.VERTICAL);
        block.setGravity(Gravity.CENTER_HORIZONTAL);
        block.addView(body(noSources
                ? "还没有媒体源。添加本地目录、WebDAV 或 SMB 后会自动扫描刮削。"
                : "媒体库还是空的，可能正在扫描刮削。"), matchWidth());
        Button action = button(noSources ? "添加媒体源" : "刷新");
        block.addView(action, margin(new LinearLayout.LayoutParams(dp(188), dp(52)), 0, dp(12), 0, 0));
        action.setOnClickListener(view -> {
            if (noSources) {
                showSetup(null);
            } else {
                showHome();
            }
        });
        return block;
    }

    private View posterCard(Models.LibraryItem item) {
        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setClickable(true);
        card.setPadding(dp(6), dp(6), dp(6), dp(12));
        card.setBackground(rounded(COLOR_SURFACE, 0, 0, dp(10)));
        GridLayout.LayoutParams params = new GridLayout.LayoutParams();
        params.width = (getResources().getDisplayMetrics().widthPixels - dp(48)) / 2;
        params.setMargins(dp(4), dp(4), dp(4), dp(14));
        card.setLayoutParams(params);
        int posterHeight = dp(238);

        ImageView poster = new ImageView(this);
        poster.setScaleType(ImageView.ScaleType.CENTER_CROP);
        card.addView(poster, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, posterHeight));
        if (item.posterAssetId != null) {
            imageLoader.load(poster, api.posterUrl(item.posterAssetId), api.cookieHeader(), params.width, posterHeight);
        } else {
            imageLoader.load(poster, null, api.cookieHeader(), params.width, posterHeight);
        }

        TextView name = text(item.title, 15, Typeface.BOLD, COLOR_TEXT);
        name.setMaxLines(2);
        name.setEllipsize(TextUtils.TruncateAt.END);
        card.addView(name, margin(matchWidth(), 0, dp(8), 0, 0));

        String meta = item.releaseDate == null || item.releaseDate.length() < 4
                ? item.itemKind
                : item.releaseDate.substring(0, 4) + " · " + item.itemKind;
        card.addView(text(meta, 12, Typeface.NORMAL, COLOR_MUTED));

        int progress = item.progressPercent();
        if (progress > 0 && progress < 95) {
            card.addView(text("已看 " + progress + "%", 12, Typeface.NORMAL, COLOR_FOCUS));
        }

        card.setOnClickListener(view -> showDetail(item.id));
        return card;
    }

    private void showDetail(String itemId) {
        screen = Screen.DETAIL;
        showLoading("正在加载详情");
        runAsync(
                () -> api.getLibraryItemDetail(itemId),
                this::renderDetail,
                error -> renderError(error.getMessage(), this::showHome));
    }

    private void renderDetail(Models.LibraryDetail detail) {
        currentDetail = detail;
        root.removeAllViews();
        root.setBackgroundColor(COLOR_BACKGROUND);

        ScrollView scroll = new ScrollView(this);
        root.addView(scroll, match());
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(16), dp(20), dp(16), dp(28));
        scroll.addView(page, matchWidth());

        Button back = compactButton("返回");
        page.addView(back, new LinearLayout.LayoutParams(dp(92), dp(42)));
        back.setOnClickListener(view -> showHome());

        ImageView poster = new ImageView(this);
        poster.setScaleType(ImageView.ScaleType.CENTER_CROP);
        int detailPosterHeight = dp(420);
        page.addView(poster, margin(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, detailPosterHeight), 0, dp(16), 0, dp(18)));
        int detailPosterWidth = Math.max(dp(1), getResources().getDisplayMetrics().widthPixels - dp(32));
        if (detail.posterAssetId != null) {
            imageLoader.load(poster, api.posterUrl(detail.posterAssetId), api.cookieHeader(), detailPosterWidth, detailPosterHeight);
        } else {
            imageLoader.load(poster, null, api.cookieHeader(), detailPosterWidth, detailPosterHeight);
        }

        page.addView(title(detail.title));
        String meta = detail.releaseDate == null || detail.releaseDate.length() < 4
                ? detail.itemKind
                : detail.releaseDate.substring(0, 4) + " · " + detail.itemKind;
        page.addView(text(meta, 15, Typeface.NORMAL, COLOR_MUTED));
        if (detail.overview != null && !detail.overview.isEmpty()) {
            TextView overview = text(detail.overview, 16, Typeface.NORMAL, COLOR_TEXT);
            overview.setLineSpacing(dp(2), 1f);
            page.addView(overview, margin(matchWidth(), 0, dp(14), 0, dp(16)));
        }

        List<Models.VideoFile> files = detail.playableFiles();
        if (!files.isEmpty()) {
            Button play = button(detail.maxProgressSeconds > 5 ? "继续播放" : "开始播放");
            page.addView(play, margin(matchWidth(), 0, dp(8), 0, dp(18)));
            play.setOnClickListener(view -> playFile(files.get(0)));
        }

        page.addView(sectionTitle(detail.itemKind.equals("tv") ? "分集" : "文件"));
        for (Models.VideoFile file : files) {
            page.addView(fileRow(file));
        }
    }

    private View fileRow(Models.VideoFile file) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.VERTICAL);
        row.setClickable(true);
        row.setPadding(dp(16), dp(14), dp(16), dp(14));
        row.setBackground(rounded(COLOR_SURFACE, 0, 0, dp(8)));
        row.setOnClickListener(view -> playFile(file));

        row.addView(text(file.label(), 17, Typeface.BOLD, COLOR_TEXT));
        String summary = file.mediaSummary();
        if (!summary.isEmpty()) {
            TextView meta = text(summary, 12, Typeface.NORMAL, COLOR_MUTED);
            meta.setSingleLine(true);
            meta.setEllipsize(TextUtils.TruncateAt.END);
            row.addView(meta, margin(matchWidth(), 0, dp(4), 0, 0));
        }

        return withBottomMargin(row, dp(10));
    }

    private void playFile(Models.VideoFile file) {
        screen = Screen.PLAYER;
        setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN);
        syncKeepScreenOn();
        root.removeAllViews();
        root.setBackgroundColor(Color.BLACK);

        playerView = new MpvVideoView(this);
        root.addView(playerView, match());

        LinearLayout top = playerOverlay(file);
        root.addView(top, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP));

        LinearLayout controls = playerControls();
        root.addView(controls, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(64), Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL));

        if (file.playbackUrl == null || file.playbackUrl.isEmpty()) {
            renderPlayerError("播放地址未配置，请重新扫描媒体源。");
            return;
        }

        activePlaybackFile = file;
        runAsync(
                () -> resolvePlaybackTarget(file),
                target -> {
                    if (screen != Screen.PLAYER || playerView == null || activePlaybackFile != file) {
                        return;
                    }

                    boolean accepted = playerView.play(target.url, target.cookieHeader, file.positionSeconds);
                    if (!accepted) {
                        renderPlayerError(playerView.lastError());
                    } else {
                        loadDefaultSubtitle(file);
                        startProgressUpdates(file);
                        syncKeepScreenOn();
                    }
                },
                error -> renderPlayerError(error.getMessage()));
    }

    private PlaybackTarget resolvePlaybackTarget(Models.VideoFile file) throws Exception {
        String streamUrl = file.playbackUrl;
        if (streamUrl.isEmpty()) {
            throw new IOException("播放地址未配置，请重新扫描媒体源。");
        }
        if (streamUrl.startsWith("smb://")) {
            String proxyUrl = SmbStreamServer.shared().prepare(
                    api.engine().smbClientForUrl(streamUrl), streamUrl);
            return new PlaybackTarget(proxyUrl, "");
        }
        if (!isIsoFile(file)) {
            return new PlaybackTarget(streamUrl, api.cookieHeader());
        }

        String proxyUrl = RemoteIsoStreamServer.shared().prepare(streamUrl, api.cookieHeader());
        if (proxyUrl == null || proxyUrl.isEmpty()) {
            throw new IOException("客户端 ISO Range 代理没有返回播放地址。");
        }
        return new PlaybackTarget(proxyUrl, "");
    }

    private boolean isIsoFile(Models.VideoFile file) {
        return file != null &&
                (hasFileExtension(file.fileName, ".iso") ||
                        hasFileExtension(file.relativePath, ".iso") ||
                        "iso".equalsIgnoreCase(file.container == null ? "" : file.container.trim()));
    }

    private boolean hasFileExtension(String value, String extension) {
        return value != null && value.trim().toLowerCase(java.util.Locale.ROOT).endsWith(extension);
    }

    private void loadDefaultSubtitle(Models.VideoFile file) {
        runAsync(
                () -> api.getSubtitles(file.id),
                tracks -> {
                    if (screen != Screen.PLAYER || playerView == null || activePlaybackFile != file) {
                        return;
                    }

                    for (Models.SubtitleTrack track : tracks) {
                        String subtitleUrl = api.subtitleUrl(track);
                        if (!subtitleUrl.isEmpty()) {
                            playerView.addSubtitle(subtitleUrl, api.cookieHeader());
                            return;
                        }
                    }
                },
                ignored -> {
                });
    }

    private void startProgressUpdates(Models.VideoFile file) {
        stopProgressUpdates();
        progressReporter = new Runnable() {
            @Override
            public void run() {
                if (screen != Screen.PLAYER || playerView == null || activePlaybackFile != file) {
                    return;
                }

                postPlaybackProgress(file);
                syncKeepScreenOn();
                main.postDelayed(this, 10000);
            }
        };
        main.postDelayed(progressReporter, 8000);
    }

    private void stopProgressUpdates() {
        if (progressReporter != null) {
            main.removeCallbacks(progressReporter);
            progressReporter = null;
        }
    }

    private void flushPlaybackProgress() {
        if (activePlaybackFile != null && playerView != null) {
            postPlaybackProgress(activePlaybackFile);
        }
        activePlaybackFile = null;
    }

    private void postPlaybackProgress(Models.VideoFile file) {
        double position = playerView == null ? 0 : playerView.currentTimeSeconds();
        double duration = playerView == null ? 0 : playerView.durationSeconds();
        if (duration <= 0) {
            duration = file.durationSeconds;
        }
        if (position <= 0 || duration <= 0) {
            return;
        }

        double finalDuration = duration;
        runAsync(
                () -> {
                    api.updatePlaybackProgress(file.id, position, finalDuration);
                    return null;
                },
                ignored -> {
                },
                ignored -> {
                });
    }

    private LinearLayout playerOverlay(Models.VideoFile file) {
        LinearLayout overlay = new LinearLayout(this);
        overlay.setOrientation(LinearLayout.VERTICAL);
        overlay.setPadding(dp(18), dp(16), dp(18), dp(16));
        overlay.setBackgroundColor(Color.argb(136, 0, 0, 0));
        overlay.addView(text(file.label(), 18, Typeface.BOLD, COLOR_TEXT));
        String summary = file.mediaSummary();
        if (!summary.isEmpty()) {
            overlay.addView(text(summary, 12, Typeface.NORMAL, COLOR_MUTED));
        }
        return overlay;
    }

    private LinearLayout playerControls() {
        LinearLayout controls = new LinearLayout(this);
        controls.setGravity(Gravity.CENTER);
        controls.setPadding(dp(12), dp(8), dp(12), dp(8));
        controls.setBackground(rounded(Color.argb(172, 0, 0, 0), 0, 0, dp(32)));

        Button rewind = compactButton("«10");
        controls.addView(rewind, new LinearLayout.LayoutParams(dp(58), dp(48)));
        rewind.setOnClickListener(view -> {
            if (playerView != null) {
                playerView.seek(-10);
            }
        });

        Button pause = compactButton("播放/暂停");
        controls.addView(pause, margin(new LinearLayout.LayoutParams(dp(132), dp(48)), dp(8), 0, dp(8), 0));
        pause.setOnClickListener(view -> {
            if (playerView != null) {
                playerView.togglePaused();
                syncKeepScreenOn();
            }
        });

        Button forward = compactButton("10»");
        controls.addView(forward, new LinearLayout.LayoutParams(dp(58), dp(48)));
        forward.setOnClickListener(view -> {
            if (playerView != null) {
                playerView.seek(10);
            }
        });

        return controls;
    }

    private void renderPlayerError(String message) {
        TextView error = text(message == null ? "播放器初始化失败。" : message, 18, Typeface.BOLD, COLOR_DANGER);
        error.setGravity(Gravity.CENTER);
        error.setBackgroundColor(Color.argb(196, 0, 0, 0));
        root.addView(error, match());
    }

    private void showLoading(String message) {
        root.removeAllViews();
        root.setBackgroundColor(COLOR_BACKGROUND);
        TextView text = text(message, 18, Typeface.BOLD, COLOR_TEXT);
        text.setGravity(Gravity.CENTER);
        root.addView(text, match());
    }

    private void renderError(String message, Runnable backAction) {
        root.removeAllViews();
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setGravity(Gravity.CENTER);
        page.setPadding(dp(24), dp(24), dp(24), dp(24));
        root.addView(page, match());

        TextView error = text(message == null ? "加载失败" : message, 18, Typeface.BOLD, COLOR_DANGER);
        error.setGravity(Gravity.CENTER);
        page.addView(error, margin(matchWidth(), 0, 0, 0, dp(18)));

        Button back = button("返回");
        page.addView(back, matchWidth());
        back.setOnClickListener(view -> backAction.run());
    }

    @Override
    public boolean onKeyDown(int keyCode, KeyEvent event) {
        if (screen == Screen.PLAYER && playerView != null) {
            if (keyCode == KeyEvent.KEYCODE_DPAD_CENTER ||
                    keyCode == KeyEvent.KEYCODE_ENTER ||
                    keyCode == KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE ||
                    keyCode == KeyEvent.KEYCODE_SPACE) {
                playerView.togglePaused();
                syncKeepScreenOn();
                return true;
            }
            if (keyCode == KeyEvent.KEYCODE_DPAD_LEFT || keyCode == KeyEvent.KEYCODE_MEDIA_REWIND) {
                playerView.seek(-10);
                return true;
            }
            if (keyCode == KeyEvent.KEYCODE_DPAD_RIGHT || keyCode == KeyEvent.KEYCODE_MEDIA_FAST_FORWARD) {
                playerView.seek(10);
                return true;
            }
        }
        return super.onKeyDown(keyCode, event);
    }

    @Override
    public void onBackPressed() {
        if (screen == Screen.PLAYER) {
            closePlayer();
            return;
        }
        if (screen == Screen.DETAIL || screen == Screen.SETUP) {
            // 设置页现在是海报墙的二级页：返回键回墙，不是退出应用
            showHome();
            return;
        }
        super.onBackPressed();
    }

    private void closePlayer() {
        destroyPlayer();
        getWindow().clearFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN | WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED);
        if (currentDetail != null) {
            renderDetail(currentDetail);
        } else {
            showHome();
        }
    }

    /**
     * 只有播放器真正在播放时才保持屏幕常亮；暂停或播放结束后清掉
     * FLAG_KEEP_SCREEN_ON，让系统按正常策略息屏。
     */
    private void syncKeepScreenOn() {
        if (screen != Screen.PLAYER || playerView == null) {
            return;
        }
        boolean keepOn = playerView.isPlaying();
        if (keepOn) {
            getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        } else {
            getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        }
    }

    private void destroyPlayer() {
        RemoteIsoStreamServer.shared().clear();
        SmbStreamServer.shared().clear();
        if (playerView != null) {
            stopProgressUpdates();
            flushPlaybackProgress();
            playerView.destroyPlayer();
            playerView = null;
        }
    }

    private static final class PlaybackTarget {
        final String url;
        final String cookieHeader;

        PlaybackTarget(String url, String cookieHeader) {
            this.url = url == null ? "" : url;
            this.cookieHeader = cookieHeader == null ? "" : cookieHeader;
        }
    }

    @Override
    protected void onDestroy() {
        destroyPlayer();
        io.shutdownNow();
        super.onDestroy();
    }

    private Button kindToggleButton(String kind, String label, final String[] selectedKind, final TextView hint) {
        boolean isSelected = kind.equals(selectedKind[0]);
        Button button = new Button(this);
        applyKindToggleStyle(button, label, isSelected);
        button.setOnClickListener(view -> {
            selectedKind[0] = kind;
            for (Map.Entry<String, Button> entry : kindToggleButtons.entrySet()) {
                applyKindToggleStyle(entry.getValue(), kindLabel(entry.getKey()),
                        entry.getKey().equals(selectedKind[0]));
            }
            hint.setText(kindHint(selectedKind[0]));
        });
        kindToggleButtons.put(kind, button);
        return button;
    }

    private static String kindLabel(String kind) {
        if (kind.equals("webdav")) {
            return "WebDAV";
        }
        if (kind.equals("smb")) {
            return "SMB";
        }
        return "本地目录";
    }

    private static String kindHint(String kind) {
        if (kind.equals("smb")) {
            return "SMB：填写 smb://主机/共享 形式的地址，例如 smb://192.168.0.10/video（可加 :445 端口）。";
        }
        if (kind.equals("webdav")) {
            return "WebDAV：填写 https://nas:5006/dav 形式的地址前缀。";
        }
        return "本地目录：填写绝对路径，例如 /storage/emulated/0/Movies。";
    }

    private void applyKindToggleStyle(Button button, String label, boolean selected) {
        button.setText((selected ? "\u2713 " : "  ") + label);
        button.setTextColor(selected ? COLOR_TEXT : COLOR_MUTED);
        button.setBackground(rounded(
                selected ? COLOR_SURFACE_ALT : COLOR_SURFACE,
                selected ? COLOR_FOCUS : 0,
                selected ? dp(1) : 0, dp(8)));
    }

    private <T> void runAsync(Task<T> task, Success<T> success, Failure failure) {
        io.execute(() -> {
            try {
                T result = task.run();
                main.post(() -> success.accept(result));
            } catch (Exception error) {
                main.post(() -> failure.accept(error));
            }
        });
    }

    private EditText input(String hint, String value) {
        EditText editText = new EditText(this);
        editText.setText(value);
        editText.setHint(hint);
        editText.setSingleLine(true);
        editText.setTextSize(16);
        editText.setTextColor(COLOR_TEXT);
        editText.setHintTextColor(COLOR_MUTED);
        editText.setPadding(dp(14), 0, dp(14), 0);
        editText.setBackground(rounded(COLOR_SURFACE_ALT, 0, 0, dp(8)));
        editText.setMinHeight(dp(52));
        return editText;
    }

    private EditText inputWithLeadingIcon(String hint, String value) {
        EditText editText = input(hint, value);
        try {
            android.graphics.drawable.Drawable icon = getDrawable(R.drawable.ic_proxy_gateway);
            if (icon != null) {
                int size = dp(18);
                icon.setBounds(0, 0, size, size);
                editText.setCompoundDrawables(icon, null, null, null);
                editText.setPadding(dp(14) + size + dp(8), 0, dp(14), 0);
            }
        } catch (Exception ignored) {
        }
        return editText;
    }

    private Button button(String label) {
        Button button = new Button(this);
        button.setText(label);
        button.setAllCaps(false);
        button.setTextSize(16);
        button.setTextColor(COLOR_TEXT);
        button.setBackground(rounded(COLOR_FOCUS, 0, 0, dp(8)));
        button.setMinHeight(dp(52));
        return button;
    }

    private Button compactButton(String label) {
        Button button = button(label);
        button.setTextSize(14);
        button.setBackground(rounded(COLOR_SURFACE_ALT, 0, 0, dp(8)));
        return button;
    }

    private TextView title(String value) {
        return text(value, 28, Typeface.BOLD, COLOR_TEXT);
    }

    private TextView sectionTitle(String value) {
        TextView title = text(value, 21, Typeface.BOLD, COLOR_TEXT);
        title.setPadding(0, dp(8), 0, dp(12));
        return title;
    }

    private TextView body(String value) {
        TextView text = text(value, 15, Typeface.NORMAL, COLOR_MUTED);
        text.setPadding(0, dp(8), 0, dp(8));
        return text;
    }

    private TextView text(String value, int sp, int style, int color) {
        TextView textView = new TextView(this);
        textView.setText(value == null ? "" : value);
        textView.setTextSize(sp);
        textView.setTypeface(Typeface.DEFAULT, style);
        textView.setTextColor(color);
        return textView;
    }

    private GradientDrawable rounded(int fillColor, int strokeColor, int strokeWidth, int radius) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(fillColor);
        drawable.setCornerRadius(radius);
        if (strokeWidth > 0) {
            drawable.setStroke(strokeWidth, strokeColor);
        }
        return drawable;
    }

    private View withBottomMargin(View view, int bottom) {
        view.setLayoutParams(margin(matchWidth(), 0, 0, 0, bottom));
        return view;
    }

    private FrameLayout.LayoutParams match() {
        return new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT);
    }

    private LinearLayout.LayoutParams matchWidth() {
        return new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
    }

    private LinearLayout.LayoutParams margin(LinearLayout.LayoutParams params, int left, int top, int right, int bottom) {
        params.setMargins(left, top, right, bottom);
        return params;
    }

    private int dp(float value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }

    private enum Screen {
        SETUP,
        HOME,
        DETAIL,
        PLAYER
    }

    private interface Task<T> {
        T run() throws Exception;
    }

    private interface Success<T> {
        void accept(T result);
    }

    private interface Failure {
        void accept(Exception error);
    }
}
