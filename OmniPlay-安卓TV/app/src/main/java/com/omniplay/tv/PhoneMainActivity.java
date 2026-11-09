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
import android.widget.HorizontalScrollView;
import android.widget.GridLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.TextView;

import com.omniplay.tv.data.Models;
import com.omniplay.tv.data.LocalPlayApi;
import com.omniplay.tv.iso.RemoteIsoStreamServer;
import com.omniplay.tv.local.LocalEngine;
import com.omniplay.tv.local.SavedConnections;
import com.omniplay.tv.local.SmbStreamServer;
import com.omniplay.tv.local.TvSettings;
import com.omniplay.tv.phone.PhoneHost;
import com.omniplay.tv.phone.PhoneLoginSheet;
import com.omniplay.tv.phone.PhoneMatchSheet;
import com.omniplay.tv.phone.PhoneSettingsSheet;
import com.omniplay.tv.phone.PhoneSourceSheet;
import com.omniplay.tv.phone.PhoneSubtitleOverlay;
import com.omniplay.tv.phone.PhoneUi;
import com.omniplay.tv.player.MpvVideoView;
import com.omniplay.tv.subtitle.PgsSubtitleCue;
import com.omniplay.tv.subtitle.PgsSubtitleParser;
import com.omniplay.tv.ui.Icons;
import com.omniplay.tv.ui.ImageLoader;
import com.omniplay.tv.ui.Theme;

import java.io.IOException;
import java.util.ArrayList;
import java.util.List;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class PhoneMainActivity extends Activity implements PhoneHost {
    /** 配色全部取自 {@link Theme}，与电视端共用同一套主题（含深色），在 onCreate 里刷一次。 */
    private int COLOR_BACKGROUND;
    private int COLOR_SURFACE;
    private int COLOR_SURFACE_ALT;
    /** 首页海报墙最近一次的条目，人物作品抽屉直接用它查同演的其它片子，不再跑一次网络。 */
    private List<Models.LibraryItem> wallItems = new ArrayList<>();
    private int COLOR_TEXT;
    private int COLOR_MUTED;
    private int COLOR_FOCUS;
    private int COLOR_DANGER;

    /** 长按快退/快进时步长随按住时间爬升，档位与电视端一致（10× → 30× → 100× → 250×）。 */
    private static final double SEEK_STEP_SECONDS = 0.8;
    private long seekHoldStartedAtMs;

    /** 播放模式与电视端共用同一份偏好：电视上选过直出，手机上进来也是直出。 */
    private static final String UI_PREFS = "omniplay_tv_ui";
    private static final String KEY_PLAYBACK_MODE = "4k_dv_hdr_playback_mode";
    private static final String MODE_DIRECT = "high_performance_direct";
    private static final String MODE_COMPATIBLE = "compatible_subtitle";

    private boolean storedDirectPlayback() {
        return MODE_DIRECT.equals(getSharedPreferences(UI_PREFS, MODE_PRIVATE)
                .getString(KEY_PLAYBACK_MODE, MODE_COMPATIBLE));
    }

    private void storeDirectPlayback(boolean direct) {
        getSharedPreferences(UI_PREFS, MODE_PRIVATE).edit()
                .putString(KEY_PLAYBACK_MODE, direct ? MODE_DIRECT : MODE_COMPATIBLE)
                .apply();
    }

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
    private SavedConnections savedConnections;
    private TextView homeStatus;
    private Runnable homeStatusTicker;
    private boolean scanning;
    private boolean wallReady;
    private Thread scanThread;
    // 播放页：模式、比例、字幕覆盖层与当前选中的 sidecar
    private boolean directPlayback;
    private boolean aspectStretch;
    private PhoneSubtitleOverlay subtitleOverlay;
    private LinearLayout playerControlsBar;
    private TextView playerTimeLabel;
    private String selectedSidecarKey;
    private LocalEngine.Sidecar activeSidecar;
    private TextView playerTitleLabel;
    private TextView playerModeLabel;
    private TextView playerSubtitleLabel;
    private int selectedAudioTrackOrdinal = -1;
    private int selectedEmbeddedSubtitleOrdinal = -1;
    private String selectedExternalSubtitleId;
    private Runnable subtitleTicker;
    private Runnable chromeHider;
    private Runnable remoteSeekRepeater;
    private boolean remoteSeekHeld;
    private int remoteSeekDirection;
    private boolean seekHeld;

    // ---- 播放器实时轨道 --------------------------------------------------------------
    // 本地库存的文件对象不带轨道清单（audioTracks / subtitleStreams 恒为空表），内封轨只有
    // 等 mpv 把文件装起来之后才问得到。电视端就是靠这份 track-list 列内封字幕的，手机端补齐
    // 同一套，兼容模式才看得见内封字幕。
    private java.util.List<RuntimeTrack> runtimeAudioTracks = java.util.Collections.emptyList();
    private java.util.List<RuntimeTrack> runtimeSubtitleTracks = java.util.Collections.emptyList();
    private String selectedRuntimeAudioId;
    private String selectedRuntimeSubtitleId;
    // mpv 一条字幕轨都没报时的兜底：自己读容器里的内封字幕轨，同一部片只探一次。
    private String containerProbeKey;
    private java.util.List<com.omniplay.tv.subtitle.MatroskaSubtitleExtractor.Track> probedContainerTracks
            = java.util.Collections.emptyList();
    private Integer selectedContainerSubtitleTrack;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        // 调色板必须先定下来，后面每个视图都按它画（与电视端是同一套 Theme.VARIANTS）
        Theme.apply(new TvSettings(this).themeId());
        applyThemeColors();
        getWindow().setStatusBarColor(COLOR_BACKGROUND);
        getWindow().setNavigationBarColor(COLOR_BACKGROUND);

        api = new LocalPlayApi(this);
        savedConnections = new SavedConnections(this);
        imageLoader = new ImageLoader(this);
        root = new FrameLayout(this);
        root.setBackgroundColor(COLOR_BACKGROUND);
        // 状态栏与导航栏不让内容钻到下面：手机上页头会被状态栏吃掉半行，
        // 空库时那颗按钮也会整颗藏进导航栏后面。
        root.setOnApplyWindowInsetsListener((view, insets) -> {
            view.setPadding(insets.getSystemWindowInsetLeft(), insets.getSystemWindowInsetTop(),
                    insets.getSystemWindowInsetRight(), insets.getSystemWindowInsetBottom());
            return insets;
        });
        setContentView(root);

        // 独立版没有服务端要问，也不该有第二个「首次运行页」：海报墙永远是首屏。
        // 一个媒体源都没有时，墙上自带「添加媒体源」的入口（emptyLibraryCallToAction），
        // 设置页退成二级页，从右上角「设置」或那个入口进来。
        showHome();
        startStartupSync();
    }

    private void applyThemeColors() {
        COLOR_BACKGROUND = Theme.BACKGROUND;
        COLOR_SURFACE = Theme.SURFACE;
        COLOR_SURFACE_ALT = Theme.SURFACE_ALT;
        COLOR_TEXT = Theme.TEXT;
        COLOR_MUTED = Theme.MUTED;
        COLOR_FOCUS = Theme.ACCENT;
        COLOR_DANGER = Theme.DANGER;
    }

    /** 开机自动扫描（设置里可关）：有媒体源才跑，扫描刮削都在后台，海报一张张上墙。 */
    private void startStartupSync() {
        TvSettings settings = api.engine().settings();
        if (scanning || api.engine().sources().isEmpty()) {
            return;
        }
        if (settings.autoScanOnStartup()) {
            scanInBackground();
        }
    }

    // ---- PhoneHost：手机端几个弹层向宿主借的东西 ----------------------------

    @Override public Activity activity() {
        return this;
    }

    @Override public LocalPlayApi api() {
        return api;
    }

    @Override public LocalEngine engine() {
        return api.engine();
    }

    @Override public ImageLoader images() {
        return imageLoader;
    }

    @Override public java.util.concurrent.ExecutorService io() {
        return io;
    }

    @Override public Handler main() {
        return main;
    }

    @Override public SavedConnections savedConnections() {
        return savedConnections;
    }

    @Override public void refreshLibrary() {
        if (screen == Screen.HOME) {
            showHome();
        } else if (screen == Screen.DETAIL && currentDetail != null) {
            showDetail(currentDetail.id);
        }
    }

    /**
     * 扫描刮削走自己的线程：它一跑就是几分钟（等 TMDB 时更久），压在共享的 io 池上会把
     * 海报墙这类短查询一起拖住，电视端一直是单独一条工作线程。
     */
    @Override public void scanInBackground() {
        if (scanning) {
            return;
        }
        scanning = true;
        startHomeStatusTicker();
        Thread worker = new Thread(() -> {
            LocalEngine.ScanReport report = null;
            Exception failure = null;
            try {
                report = api.engine().scanAndScrape(status -> main.post(this::updateHomeStatus));
            } catch (Exception error) {
                failure = error;
            }
            final LocalEngine.ScanReport finished = report;
            final Exception error = failure;
            main.post(() -> {
                scanning = false;
                stopHomeStatusTicker();
                updateHomeStatus();
                if (error != null) {
                    toast("扫描失败：" + error.getMessage());
                }
                refreshLibrary();
                if (finished != null) {
                    toast("扫描完成：扫描 " + finished.scannedFiles + " 个文件，新增 "
                            + finished.newFiles + " 个");
                }
            });
        }, "omniplay-phone-scan");
        scanThread = worker;
        worker.start();
    }

    @Override public void toast(String message) {
        main.post(() -> android.widget.Toast.makeText(this, message,
                android.widget.Toast.LENGTH_SHORT).show());
    }

    /** 首页顶部那行进度：扫描与刮削各写一段，跟电视端左上角那行是同一件事。 */
    private void startHomeStatusTicker() {
        stopHomeStatusTicker();
        homeStatusTicker = new Runnable() {
            @Override
            public void run() {
                updateHomeStatus();
                if (homeStatusTicker != null) {
                    main.postDelayed(this, 900);
                }
            }
        };
        main.postDelayed(homeStatusTicker, 400);
    }

    private void stopHomeStatusTicker() {
        if (homeStatusTicker != null) {
            main.removeCallbacks(homeStatusTicker);
            homeStatusTicker = null;
        }
    }

    private void updateHomeStatus() {
        if (homeStatus == null) {
            return;
        }
        LocalEngine engine = api.engine();
        ArrayList<String> parts = new ArrayList<>();
        if (scanning || engine.isScraping()) {
            parts.add("扫描刮削进行中");
        }
        int queued = engine.scrapeQueueCount();
        if (queued > 0) {
            parts.add("待刮削 " + queued + " 条");
        }
        String status = engine.scrapeStatus();
        if (status != null && !status.trim().isEmpty()) {
            parts.add(status.trim());
        }
        if (!parts.isEmpty()) {
            homeStatus.setText(android.text.TextUtils.join(" · ", parts));
            homeStatus.setVisibility(View.VISIBLE);
        } else {
            homeStatus.setVisibility(View.GONE);
        }
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
        boolean hadWall = wallReady;
        screen = Screen.HOME;
        currentDetail = null;
        destroyPlayer();
        setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED);
        // 墙上已经有内容时不再整屏盖一层「正在加载」：挂载新源、扫描收尾都会调到这里，
        // 盖一次就是一次白屏，海报应该原地刷新（与电视端一致）。
        if (!hadWall) {
            showLoading("正在加载媒体库");
        }
        runAsync(
                () -> api.getLibraryItems(),
                this::renderHome,
                error -> {
                    if (!hadWall) {
                        showSetup(error.getMessage());
                    } else {
                        toast("媒体库刷新失败：" + error.getMessage());
                    }
                });
    }

    private void renderHome(List<Models.LibraryItem> items) {
        wallReady = true;
        wallItems = items;
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
                ? "独立版 · 还没有媒体源"
                : "独立版 · " + api.engine().sources().size() + " 个媒体源 · "
                        + api.engine().libraryItemCount() + " 部", 13, Typeface.NORMAL, COLOR_MUTED);
        server.setSingleLine(true);
        server.setEllipsize(TextUtils.TruncateAt.MIDDLE);
        heading.addView(server);
        // 扫描 / 刮削进度就写在标题下面这一行，与电视端左上角那行是同一件事
        homeStatus = text("", 12, Typeface.BOLD, COLOR_FOCUS);
        homeStatus.setVisibility(View.GONE);
        heading.addView(homeStatus, margin(matchWidth(), 0, dp(2), 0, 0));
        updateHomeStatus();

        header.addView(iconButton(Icons.Kind.FOLDER_PLUS, "媒体源", view -> PhoneSourceSheet.show(this)),
                new LinearLayout.LayoutParams(dp(52), dp(44)));
        header.addView(iconButton(Icons.Kind.SYNC, "扫描刮削", view -> scanInBackground()),
                margin(new LinearLayout.LayoutParams(dp(52), dp(44)), dp(6), 0, 0, 0));
        header.addView(iconButton(Icons.Kind.GEAR, "设置", view -> PhoneSettingsSheet.show(this)),
                margin(new LinearLayout.LayoutParams(dp(52), dp(44)), dp(6), 0, 0, 0));

        // 继续观看：横着一条抽屉放在最前，只收看到一半的，最多 12 部
        // （mac 版首页同一件事叫「继续观看」，手机上横向滑比塞进网格好点）
        List<Models.LibraryItem> resume = new ArrayList<>();
        for (Models.LibraryItem item : items) {
            int percent = item.progressPercent();
            if (percent > 0 && percent < 95) {
                resume.add(item);
                if (resume.size() >= 12) {
                    break;
                }
            }
        }
        if (!resume.isEmpty()) {
            page.addView(sectionTitle("继续观看"));
            HorizontalScrollView rail = new HorizontalScrollView(this);
            rail.setHorizontalScrollBarEnabled(false);
            rail.setClipToPadding(false);
            rail.setPadding(dp(2), 0, dp(2), dp(6));
            LinearLayout railRow = new LinearLayout(this);
            railRow.setOrientation(LinearLayout.HORIZONTAL);
            int railWidth = Math.round(dp(132));
            for (Models.LibraryItem item : resume) {
                railRow.addView(posterCard(item, railWidth, false));
            }
            rail.addView(railRow);
            page.addView(rail, new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
            page.addView(sectionTitle("全部 · " + items.size() + " 部"));
        }

        ScrollView scroll = new ScrollView(this);
        page.addView(scroll, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, 0, items.isEmpty() ? 0f : 1f));
        GridLayout grid = new GridLayout(this);
        grid.setColumnCount(2);
        scroll.addView(grid);

        for (Models.LibraryItem item : items) {
            grid.addView(posterCard(item));
        }

        if (items.isEmpty()) {
            // 空库提示占满剩下的空间并居中，别贴着屏幕底边
            page.addView(emptyLibraryCallToAction(), new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f));
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
        block.setGravity(Gravity.CENTER);
        TextView hint = body(noSources
                ? "还没有媒体源。添加本地目录、WebDAV 或 SMB 后会自动扫描刮削。"
                : "媒体库还是空的，可能正在扫描刮削。");
        hint.setGravity(Gravity.CENTER);
        block.addView(hint, matchWidth());
        Button action = button(noSources ? "添加媒体源" : "刷新");
        block.addView(action, margin(new LinearLayout.LayoutParams(dp(188), dp(52)), 0, dp(12), 0, 0));
        // 与电视端一致：添源这件事长在「媒体源」抽屉里，不是一整屏设置页
        action.setOnClickListener(view -> {
            if (noSources) {
                PhoneSourceSheet.show(this);
            } else {
                scanInBackground();
            }
        });
        return block;
    }

    // MARK: 海报卡（对齐 mac 版 MovieCardView）
    //
    // mac 版那张卡是：海报 160x240 严格 2:3、12pt 圆角，TMDB 金 / 豆瓣绿两枚评分角标并排
    // 压在海报左下角，标题两行居中，再下一行「年份 + 已看/未看」，续播进度是根 4pt 的胶囊。
    // 手机端把 hover 换成整卡可点，尺寸按列宽等比，两列在任何屏幕上都对得齐。

    private View posterCard(Models.LibraryItem item) {
        int width = (getResources().getDisplayMetrics().widthPixels - dp(48)) / 2;
        return posterCard(item, width, true);
    }

    /** 一张海报卡。{@code showProgressText} 在横向抽屉里关掉，那里已经写着「看到 x%」。 */
    private View posterCard(Models.LibraryItem item, int cardWidth, boolean showProgressText) {
        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setClickable(true);
        card.setPadding(dp(6), dp(6), dp(6), dp(10));
        card.setBackground(rounded(COLOR_SURFACE, 0, 0, dp(12)));
        GridLayout.LayoutParams params = new GridLayout.LayoutParams();
        params.width = cardWidth;
        params.setMargins(dp(4), dp(4), dp(4), dp(14));
        card.setLayoutParams(params);
        int posterHeight = Math.round(cardWidth * 1.5f);   // mac 版 160x240，就是 2:3

        FrameLayout cover = new FrameLayout(this);
        ImageView poster = new ImageView(this);
        poster.setScaleType(ImageView.ScaleType.CENTER_CROP);
        poster.setBackground(rounded(COLOR_SURFACE_ALT, 0, 0, dp(12)));
        poster.setClipToOutline(true);
        cover.addView(poster, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        if (item.posterAssetId != null) {
            imageLoader.load(poster, api.posterUrl(item.posterAssetId), api.cookieHeader(),
                    cardWidth, posterHeight);
        } else {
            imageLoader.load(poster, null, api.cookieHeader(), cardWidth, posterHeight);
        }

        String tmdb = ratingText(item.voteAverage);
        String douban = ratingText(item.doubanRating);
        if (tmdb != null || douban != null) {
            LinearLayout badges = new LinearLayout(this);
            badges.setOrientation(LinearLayout.HORIZONTAL);
            if (tmdb != null) {
                badges.addView(ratingBadge(tmdb, Theme.RATING));
            }
            if (douban != null) {
                badges.addView(ratingBadge(douban, Theme.RATING_DOUBAN),
                        margin(matchWidth(), dp(5), 0, 0, 0));
            }
            FrameLayout.LayoutParams badgeParams = new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT,
                    Gravity.BOTTOM | Gravity.START);
            badgeParams.setMargins(dp(8), 0, 0, dp(8));
            cover.addView(badges, badgeParams);
        }

        int percent = item.progressPercent();
        if (percent > 0 && percent < 95) {
            cover.addView(progressCapsule(percent), new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, dp(4), Gravity.BOTTOM));
        }
        card.addView(cover, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, posterHeight));

        TextView name = text(item.title, 15, Typeface.BOLD, COLOR_TEXT);
        name.setMaxLines(2);
        name.setGravity(Gravity.CENTER);
        name.setEllipsize(TextUtils.TruncateAt.END);
        card.addView(name, margin(matchWidth(), 0, dp(8), 0, dp(3)));

        // 年份居中，已看/未看图标贴在年份右手边，整组一起居中（mac 版同一行）
        LinearLayout metaRow = new LinearLayout(this);
        metaRow.setGravity(Gravity.CENTER);
        String year = item.releaseDate != null && item.releaseDate.length() >= 4
                ? item.releaseDate.substring(0, 4)
                : ("tv".equals(item.itemKind) ? "剧集" : "电影");
        metaRow.addView(text(year, 12, Typeface.NORMAL, COLOR_MUTED));
        LinearLayout.LayoutParams watchParams = new LinearLayout.LayoutParams(dp(14), dp(14));
        watchParams.setMargins(dp(5), 0, 0, 0);
        metaRow.addView(watchIcon(item), watchParams);
        card.addView(metaRow, matchWidth());

        if (showProgressText && percent > 0 && percent < 95) {
            card.addView(text("看到 " + percent + "%", 11, Typeface.NORMAL, COLOR_FOCUS),
                    margin(matchWidth(), 0, dp(3), 0, 0));
        }

        card.setOnClickListener(view -> showDetail(item.id));
        return card;
    }

    /** 海报上的评分角标：半透黑底 + 评分色的星和数字，跟海报谁都不抢。 */
    private View ratingBadge(String value, int tint) {
        LinearLayout badge = new LinearLayout(this);
        badge.setOrientation(LinearLayout.HORIZONTAL);
        badge.setGravity(Gravity.CENTER_VERTICAL);
        badge.setPadding(dp(6), dp(3), dp(7), dp(3));
        badge.setBackground(rounded(Theme.withAlpha(Color.BLACK, 160), 0, 0, dp(6)));
        ImageView star = new ImageView(this);
        star.setImageDrawable(Icons.of(Icons.Kind.STAR_FILL, dp(11), tint, dp(1.4f)));
        badge.addView(star, new LinearLayout.LayoutParams(dp(11), dp(11)));
        badge.addView(text(value, 11, Typeface.BOLD, tint), margin(matchWidth(), dp(3), 0, 0, 0));
        return badge;
    }

    /** mac 版那根 4pt 的续播进度胶囊：整条淡色底槽，按百分比铺强调色。 */
    private View progressCapsule(int percent) {
        LinearLayout track = new LinearLayout(this);
        track.setOrientation(LinearLayout.HORIZONTAL);
        track.setBackground(rounded(Theme.withAlpha(COLOR_TEXT, 46), 0, 0, dp(2)));
        View fill = new View(this);
        fill.setBackgroundColor(COLOR_FOCUS);
        track.addView(fill, new LinearLayout.LayoutParams(0,
                ViewGroup.LayoutParams.MATCH_PARENT, Math.max(0.02f, percent / 100f)));
        return track;
    }

    /** 详情页的年份 / 类型 / 季数用的小胶囊，替代过去那一长串「 · 」拼接。 */
    private TextView metaChip(String value) {
        TextView chip = text(value, 12, Typeface.NORMAL, COLOR_MUTED);
        chip.setPadding(dp(9), dp(4), dp(9), dp(4));
        chip.setBackground(rounded(COLOR_SURFACE_ALT, 0, 0, dp(8)));
        return chip;
    }

    /**
     * 「导演 / 主演 · 名单」这一行。点开通向这个人在本库里的其它作品——mac 版有独立的
     * 人物页（PersonProfileStore 会去 TMDB 拉作品列表），本机版只在库里查，不额外发请求。
     */
    private View personRow(String role, List<String> names) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setClickable(true);
        row.setPadding(dp(10), dp(8), dp(10), dp(8));
        row.setBackground(rounded(COLOR_SURFACE, 0, 0, dp(10)));
        row.addView(text(role, 12, Typeface.BOLD, COLOR_FOCUS));
        TextView nameList = text(TextUtils.join("、", names), 13, Typeface.NORMAL, COLOR_MUTED);
        nameList.setMaxLines(2);
        nameList.setEllipsize(TextUtils.TruncateAt.END);
        row.addView(nameList, margin(matchWidth(), dp(8), 0, dp(8), 0));
        ImageView chevron = new ImageView(this);
        chevron.setImageDrawable(Icons.of(Icons.Kind.CHEVRONS, dp(14), COLOR_MUTED, dp(1.6f)));
        row.addView(chevron, new LinearLayout.LayoutParams(dp(14), dp(14)));
        List<String> copy = new ArrayList<>(names);
        row.setOnClickListener(view -> showPersonSheet(role, copy));
        return row;
    }

    /** 人物作品抽屉：库里含这个人名的其它条目，点一条直接进详情。 */
    private void showPersonSheet(String role, List<String> names) {
        List<Models.LibraryItem> related = new ArrayList<>();
        String currentId = currentDetail == null ? null : currentDetail.id;
        for (Models.LibraryItem item : wallItems) {
            if (currentId != null && currentId.equals(item.id)) {
                continue;
            }
            for (String person : names) {
                if (containsName(item.directors, person) || containsName(item.casts, person)) {
                    related.add(item);
                    break;
                }
            }
            if (related.size() >= 40) {
                break;
            }
        }
        final PhoneUi.Sheet sheet = PhoneUi.sheet(this, TextUtils.join("、", names),
                role + " · 本库里还有 " + related.size() + " 部");
        if (related.isEmpty()) {
            sheet.body.addView(PhoneUi.body(this, "库里还没有这个人物的其它作品。"),
                    PhoneUi.block(this));
            return;
        }
        for (Models.LibraryItem item : related) {
            LinearLayout row = new LinearLayout(this);
            row.setOrientation(LinearLayout.HORIZONTAL);
            row.setGravity(Gravity.CENTER_VERTICAL);
            row.setClickable(true);
            row.setPadding(dp(4), dp(8), dp(4), dp(8));
            ImageView thumb = new ImageView(this);
            thumb.setScaleType(ImageView.ScaleType.CENTER_CROP);
            thumb.setBackground(rounded(COLOR_SURFACE_ALT, 0, 0, dp(6)));
            thumb.setClipToOutline(true);
            row.addView(thumb, new LinearLayout.LayoutParams(dp(34), dp(51)));
            if (item.posterAssetId != null) {
                imageLoader.load(thumb, api.posterUrl(item.posterAssetId), api.cookieHeader(),
                        dp(34), dp(51));
            }
            LinearLayout texts = new LinearLayout(this);
            texts.setOrientation(LinearLayout.VERTICAL);
            row.addView(texts, margin(new LinearLayout.LayoutParams(0,
                    ViewGroup.LayoutParams.WRAP_CONTENT, 1f), dp(10), 0, 0, 0));
            TextView name = text(item.title, 15, Typeface.BOLD, COLOR_TEXT);
            name.setMaxLines(2);
            name.setEllipsize(TextUtils.TruncateAt.END);
            texts.addView(name, matchWidth());
            texts.addView(text((item.releaseDate != null && item.releaseDate.length() >= 4
                    ? item.releaseDate.substring(0, 4) + " · " : "")
                    + ("tv".equals(item.itemKind) ? "剧集" : "电影"),
                    12, Typeface.NORMAL, COLOR_MUTED), matchWidth());
            row.setOnClickListener(view -> {
                sheet.close();
                showDetail(item.id);
            });
            sheet.body.addView(row, PhoneUi.block(this));
        }
    }

    /** 名单里有没有这个人：忽略大小写与多余空格，「张三 / 張三」这类不同写法不在本库版范围内。 */
    private static boolean containsName(List<String> people, String name) {
        if (people == null || name == null) {
            return false;
        }
        String needle = name.trim();
        for (String person : people) {
            if (person != null && person.trim().equalsIgnoreCase(needle)) {
                return true;
            }
        }
        return false;
    }

    /** 评分的写法：没有、0、NaN 都当成「没评分」，与电视端一致。 */
    private static String ratingText(Double value) {
        return value != null && value > 0 && Double.isFinite(value)
                ? String.format(java.util.Locale.ROOT, "%.1f", value) : null;
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

        LinearLayout topRow = new LinearLayout(this);
        topRow.setGravity(Gravity.CENTER_VERTICAL);
        page.addView(topRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT));
        Button back = compactButton("返回");
        topRow.addView(back, new LinearLayout.LayoutParams(dp(92), dp(42)));
        back.setOnClickListener(view -> showHome());
        topRow.addView(new View(this), margin(new LinearLayout.LayoutParams(0,
                dp(1), ViewGroup.LayoutParams.WRAP_CONTENT), 0, 0, 0, 0));
        // 与电视端一致：手动匹配与自定义编辑是详情页右上角两颗图标
        topRow.addView(iconButton(Icons.Kind.MATCH, "手动匹配",
                view -> PhoneMatchSheet.showManualMatch(this, detail)),
                margin(new LinearLayout.LayoutParams(dp(52), dp(44)), dp(6), 0, 0, 0));
        topRow.addView(iconButton(Icons.Kind.PENCIL, "自定义编辑",
                view -> PhoneMatchSheet.showCustomEdit(this, detail)),
                margin(new LinearLayout.LayoutParams(dp(52), dp(44)), dp(6), 0, 0, 0));

        // 主视觉：海报铺满屏宽、底部压一层黑色渐变，片名和评分直接写在海报上，
        // 与 mac 版 MovieDetailView 的 hero 同一个做法（手机上不另找 backdrop，省一次下载）
        int heroHeight = Math.round(getResources().getDisplayMetrics().widthPixels * 1.25f);
        FrameLayout hero = new FrameLayout(this);
        page.addView(hero, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, heroHeight));
        ImageView poster = new ImageView(this);
        poster.setScaleType(ImageView.ScaleType.CENTER_CROP);
        hero.addView(poster, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        int heroWidth = getResources().getDisplayMetrics().widthPixels;
        if (detail.posterAssetId != null) {
            imageLoader.load(poster, api.posterUrl(detail.posterAssetId), api.cookieHeader(),
                    heroWidth, heroHeight);
        } else {
            imageLoader.load(poster, null, api.cookieHeader(), heroWidth, heroHeight);
        }
        View scrim = new View(this);
        scrim.setBackground(new GradientDrawable(GradientDrawable.Orientation.BOTTOM_TOP, new int[]{
                Theme.withAlpha(Color.BLACK, 214), Theme.withAlpha(Color.BLACK, 120),
                Color.TRANSPARENT}));
        hero.addView(scrim, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,
                Math.round(heroHeight * 0.66f), Gravity.BOTTOM));

        LinearLayout heroTexts = new LinearLayout(this);
        heroTexts.setOrientation(LinearLayout.VERTICAL);
        heroTexts.setPadding(dp(16), 0, dp(16), dp(14));
        TextView heroTitle = text(detail.title, 24, Typeface.BOLD, Color.WHITE);
        heroTitle.setShadowLayer(dp(5), 0, dp(1), Theme.withAlpha(Color.BLACK, 200));
        heroTitle.setMaxLines(2);
        heroTitle.setEllipsize(TextUtils.TruncateAt.END);
        heroTexts.addView(heroTitle, matchWidth());
        String tmdbScore = ratingText(detail.voteAverage);
        String doubanScore = ratingText(detail.doubanRating);
        LinearLayout heroBadges = new LinearLayout(this);
        heroBadges.setOrientation(LinearLayout.HORIZONTAL);
        if (tmdbScore != null) {
            heroBadges.addView(ratingBadge("TMDB " + tmdbScore, Theme.RATING));
        }
        if (doubanScore != null) {
            heroBadges.addView(ratingBadge("豆瓣 " + doubanScore, Theme.RATING_DOUBAN),
                    margin(matchWidth(), dp(6), 0, 0, 0));
        }
        if (tmdbScore != null || doubanScore != null) {
            heroTexts.addView(heroBadges, margin(matchWidth(), 0, dp(8), 0, 0));
        }
        hero.addView(heroTexts, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT,
                Gravity.BOTTOM));

        String meta = detail.releaseDate == null || detail.releaseDate.length() < 4
                ? ("tv".equals(detail.itemKind) ? "剧集" : "电影")
                : detail.releaseDate.substring(0, 4) + " · "
                        + ("tv".equals(detail.itemKind) ? "剧集" : "电影");
        ArrayList<String> chips = new ArrayList<>();
        chips.add(meta);
        chips.addAll(detail.genres);
        // 年份 / 类型做成一排小胶囊，比一长串「 · 」好读，也跟 mac 版那排 chip 一致
        LinearLayout chipRow = new LinearLayout(this);
        chipRow.setOrientation(LinearLayout.HORIZONTAL);
        for (String chip : chips) {
            LinearLayout.LayoutParams chipParams = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            chipParams.setMargins(0, 0, dp(6), 0);
            chipRow.addView(metaChip(chip), chipParams);
        }
        HorizontalScrollView chipScroll = new HorizontalScrollView(this);
        chipScroll.setHorizontalScrollBarEnabled(false);
        chipScroll.addView(chipRow);
        page.addView(chipScroll, margin(matchWidth(), 0, dp(12), 0, 0));
        if (!detail.directors.isEmpty()) {
            page.addView(personRow("导演", detail.directors), margin(matchWidth(), 0, dp(4), 0, 0));
        }
        if (!detail.casts.isEmpty()) {
            page.addView(personRow("主演", detail.casts), margin(matchWidth(), 0, dp(4), 0, 0));
        }
        if (detail.douban != null && detail.douban.subjectUrl != null
                && !detail.douban.subjectUrl.isEmpty()) {
            page.addView(text("已绑定豆瓣 " + detail.douban.subjectId, 12,
                    Typeface.NORMAL, COLOR_MUTED), margin(matchWidth(), 0, dp(4), 0, 0));
        }

        if (detail.overview != null && !detail.overview.isEmpty()) {
            // 简介整段摊开，不再做「展开简介」那种折叠
            TextView overview = text(detail.overview, 16, Typeface.NORMAL, COLOR_TEXT);
            overview.setLineSpacing(dp(2), 1f);
            page.addView(overview, margin(matchWidth(), 0, dp(14), 0, dp(10)));
        }

        // 观看状态 + 进度：图标在左，进度条在中，已看 / 剩余时间分列两端
        int percent = detail.progressPercent();
        LinearLayout progressRow = new LinearLayout(this);
        progressRow.setGravity(Gravity.CENTER_VERTICAL);
        page.addView(progressRow, margin(matchWidth(), 0, dp(6), 0, 0));
        progressRow.addView(watchIcon(detail), new LinearLayout.LayoutParams(dp(18), dp(18)));
        LinearLayout bar = new LinearLayout(this);
        bar.setOrientation(LinearLayout.HORIZONTAL);
        bar.setBackground(rounded(COLOR_SURFACE_ALT, 0, 0, dp(3)));
        progressRow.addView(bar, margin(new LinearLayout.LayoutParams(0, dp(6), 1f), dp(10), 0, dp(10), 0));
        bar.addView(new View(this), new LinearLayout.LayoutParams(
                Math.max(0, (int) (dp(200) * percent / 100f)), dp(6)));
        bar.getChildAt(0).setBackgroundColor(COLOR_FOCUS);
        progressRow.addView(text(formatClock(detail.maxProgressSeconds) + " / 剩 "
                        + formatClock(Math.max(0, detail.maxDurationSeconds - detail.maxProgressSeconds)),
                12, Typeface.NORMAL, COLOR_MUTED));

        List<Models.VideoFile> files = detail.playableFiles();
        if (!files.isEmpty()) {
            Button play = button(detail.maxProgressSeconds > 5 ? "继续播放" : "开始播放");
            page.addView(play, margin(matchWidth(), 0, dp(12), 0, dp(18)));
            play.setOnClickListener(view -> playFile(files.get(0)));
        }

        // 分集：剧照 + 集号 + 分集标题 + 进度；同一集有多个版本时都排在同一行下面
        boolean renderedEpisodes = false;
        for (Models.Season season : detail.seasons) {
            if (season.episodes == null || season.episodes.isEmpty()) {
                continue;
            }
            if (!renderedEpisodes) {
                page.addView(sectionTitle("分集"));
                renderedEpisodes = true;
            }
            page.addView(text("第 " + season.seasonNumber + " 季", 14, Typeface.BOLD, COLOR_MUTED),
                    margin(matchWidth(), 0, dp(10), 0, dp(4)));
            for (Models.Episode episode : season.episodes) {
                page.addView(episodeRow(episode));
            }
        }

        page.addView(sectionTitle(renderedEpisodes ? "文件" : (detail.itemKind.equals("tv") ? "分集" : "文件")));
        for (Models.VideoFile file : files) {
            page.addView(fileRow(file));
        }
    }

    /** 一集：左剧照，右边集号 / 标题 / 观看图标与进度。 */
    private View episodeRow(Models.Episode episode) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setClickable(true);
        row.setPadding(dp(12), dp(10), dp(12), dp(10));
        row.setBackground(rounded(COLOR_SURFACE, 0, 0, dp(10)));
        row.setLayoutParams(margin(matchWidth(), 0, dp(6), 0, 0));

        ImageView still = new ImageView(this);
        still.setScaleType(ImageView.ScaleType.CENTER_CROP);
        row.addView(still, new LinearLayout.LayoutParams(dp(120), dp(68)));
        if (episode.stillAssetId != null && !episode.stillAssetId.isEmpty()) {
            imageLoader.load(still, api.posterUrl(episode.stillAssetId), api.cookieHeader(),
                    dp(120), dp(68));
        }

        LinearLayout texts = new LinearLayout(this);
        texts.setOrientation(LinearLayout.VERTICAL);
        row.addView(texts, margin(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f),
                dp(12), 0, 0, 0));
        TextView name = text(episode.label(), 15, Typeface.BOLD, COLOR_TEXT);
        name.setMaxLines(2);
        name.setEllipsize(TextUtils.TruncateAt.END);
        texts.addView(name, matchWidth());
        String overview = episode.subtitleOverride != null && !episode.subtitleOverride.isEmpty()
                ? episode.subtitleOverride
                : (episode.overview == null ? "" : episode.overview);
        if (!overview.isEmpty()) {
            TextView blurb = text(overview, 12, Typeface.NORMAL, COLOR_MUTED);
            blurb.setMaxLines(2);
            blurb.setEllipsize(TextUtils.TruncateAt.END);
            texts.addView(blurb, margin(matchWidth(), 0, dp(2), 0, 0));
        }
        if (episode.videoFiles != null && episode.videoFiles.size() > 1) {
            // 同一集有不止一个版本（不同片源 / 不同压制）时列出来，各点各的播
            texts.addView(text(episode.videoFiles.size() + " 个版本", 11, Typeface.BOLD, COLOR_FOCUS),
                    margin(matchWidth(), 0, dp(3), 0, 0));
        }
        List<Models.VideoFile> options = episode.videoFiles != null && !episode.videoFiles.isEmpty()
                ? episode.videoFiles
                : (episode.videoFile == null ? java.util.Collections.<Models.VideoFile>emptyList()
                        : java.util.Collections.singletonList(episode.videoFile));
        if (!options.isEmpty()) {
            row.setOnClickListener(view -> playFile(options.get(0)));
        }
        return row;
    }

    /** 秒 → ``1:23:45`` 或 ``12:34``。 */
    private static String formatClock(double seconds) {
        long total = Math.max(0, Math.round(seconds));
        long hours = total / 3600;
        long minutes = (total % 3600) / 60;
        long secs = total % 60;
        if (hours > 0) {
            return String.format(java.util.Locale.ROOT, "%d:%02d:%02d", hours, minutes, secs);
        }
        return String.format(java.util.Locale.ROOT, "%d:%02d", minutes, secs);
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

        directPlayback = storedDirectPlayback();
        aspectStretch = api.engine().settings().playerStretchToFill();
        mountPlayerView();

        LinearLayout top = playerOverlay(file);
        root.addView(top, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP));

        LinearLayout controls = playerControls();
        HorizontalScrollView controlsScroll = new HorizontalScrollView(this);
        controlsScroll.setHorizontalScrollBarEnabled(false);
        controlsScroll.addView(controls, new HorizontalScrollView.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        controlsScroll.setPadding(dp(8), 0, dp(8), dp(6));
        root.addView(controlsScroll, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, dp(64), Gravity.BOTTOM));

        applyDefaultAudioTrack(file);
        if (file.playbackUrl == null || file.playbackUrl.isEmpty()) {
            renderPlayerError("播放地址未配置，请重新扫描媒体源。");
            return;
        }
        // 先按电视端的口径去找媒体源里现成的外挂字幕 / NAS 转档出来的 sidecar
        runAsync(() -> api.engine().discoverSidecars(file.id, file.relativePath, file.subtitleStreams),
                found -> {
                    if (screen != Screen.PLAYER || activePlaybackFile != file) {
                        return;
                    }
                    autoLoadFirstSidecar(file);
                },
                ignored -> {
                });

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
                        // 文件装载完才问得到 track-list：默认音轨要在这一步之后才落得准
                        resetRuntimeTrackState();
                        applyDefaultAudioTrack(file);
                        // 直出模式下 mpv 不画字幕，外挂字幕交给覆盖层，别白费劲
                        if (!directPlayback) {
                            loadDefaultSubtitle(file);
                        }
                        startProgressUpdates(file);
                        updatePlayerStatusLine(file);
                        showChromeTemporarily();
                        syncKeepScreenOn();
                    }
                },
                error -> renderPlayerError(error.getMessage()));
    }

    /** 默认音轨：设置里填了语言就按语言挑，没填就交给播放器自己选第一条。 */
    private void applyDefaultAudioTrack(Models.VideoFile file) {
        String preferred = api.engine().settings().defaultAudioLanguage();
        refreshRuntimeTracks();
        if (!runtimeAudioTracks.isEmpty()) {
            RuntimeTrack pick = chooseRuntimeAudioTrack(preferred);
            selectedRuntimeAudioId = pick == null ? null : runtimeTrackKey(pick);
            selectedAudioTrackOrdinal = -1;
            if (playerView != null) {
                playerView.setAudioTrack(pick == null ? "auto" : pick.mpvId);
            }
            return;
        }
        selectedRuntimeAudioId = null;
        Models.VideoFileStream pick = null;
        if (preferred != null && !preferred.trim().isEmpty()
                && !preferred.equalsIgnoreCase("auto") && !preferred.equalsIgnoreCase("smart")) {
            String wanted = preferred.trim().toLowerCase(java.util.Locale.ROOT);
            for (Models.VideoFileStream track : file.audioTracks) {
                String language = track.language == null ? "" : track.language.toLowerCase(java.util.Locale.ROOT);
                if (language.startsWith(wanted)) {
                    pick = track;
                    break;
                }
            }
        }
        if (pick == null && !file.audioTracks.isEmpty()) {
            pick = file.audioTracks.get(0);
        }
        selectedAudioTrackOrdinal = pick == null ? -1 : file.audioTracks.indexOf(pick);
        if (playerView != null) {
            playerView.setAudioTrack(selectedAudioTrackOrdinal < 0
                    ? "auto" : String.valueOf(selectedAudioTrackOrdinal + 1));
        }
    }

    /** 按设置里的语言偏好挑一条 mpv 音轨：先语言，再容器标了默认的，再不行第一条。 */
    private RuntimeTrack chooseRuntimeAudioTrack(String preferred) {
        if (runtimeAudioTracks.isEmpty()) {
            return null;
        }
        String wanted = preferred == null ? "" : preferred.trim().toLowerCase(java.util.Locale.ROOT);
        if (!wanted.isEmpty() && !wanted.equals("auto") && !wanted.equals("smart")) {
            for (RuntimeTrack track : runtimeAudioTracks) {
                String language = track.language.trim().toLowerCase(java.util.Locale.ROOT);
                if (!language.isEmpty() && (language.startsWith(wanted) || wanted.startsWith(language))) {
                    return track;
                }
            }
        }
        for (RuntimeTrack track : runtimeAudioTracks) {
            if (track.isDefault) {
                return track;
            }
        }
        return runtimeAudioTracks.get(0);
    }

    /** 菜单里默认挑一条：优先带中文的，其次第一条能用的。 */
    private void autoLoadFirstSidecar(Models.VideoFile file) {
        if (selectedSidecarKey != null) {
            return;
        }
        String preference = api.engine().settings().defaultSubtitleLanguage();
        if (com.omniplay.tv.local.SubtitleSelection.subtitlePreferenceOff(preference)) {
            // 设置里明确要关字幕，就别自作主张挂上一条。
            return;
        }
        java.util.List<LocalEngine.Sidecar> usable = new java.util.ArrayList<>();
        for (LocalEngine.Sidecar sidecar : api.engine().sidecarsFor(file.id)) {
            if (sidecar.usable()) {
                usable.add(sidecar);
            }
        }
        if (usable.isEmpty()) {
            return;
        }
        // 与电视端同一套挑法：先偏好语言，同语言里先文本字幕（位图要逐条解码，更贵）。
        String preferred = com.omniplay.tv.subtitle.MatroskaSubtitleExtractor.normalizeLanguage(preference);
        LocalEngine.Sidecar best = com.omniplay.tv.local.SubtitleSelection.prefer(usable, preferred, false);
        if (best == null) {
            best = com.omniplay.tv.local.SubtitleSelection.prefer(usable, preferred, true);
        }
        if (best == null) {
            best = com.omniplay.tv.local.SubtitleSelection.prefer(usable, null, false);
        }
        if (best == null) {
            best = com.omniplay.tv.local.SubtitleSelection.prefer(usable, null, true);
        }
        selectSubtitleSidecar(file, best != null ? best : usable.get(0));
    }

    /** 用 NAS 转档 / 媒体源里现成的字幕：读进来交给覆盖层，并关掉 mpv 那条内封轨。 */
    private void selectSubtitleSidecar(Models.VideoFile file, LocalEngine.Sidecar sidecar) {
        if (file == null || sidecar == null || playerView == null) {
            return;
        }
        clearSubtitleTracks();
        selectedSidecarKey = sidecarMenuKey(sidecar);
        activeSidecar = sidecar;
        runAsync(() -> {
            byte[] body = api.engine().readSidecar(sidecar);
            if (body == null || body.length == 0) {
                throw new IOException("读不到转档出来的字幕文件。");
            }
            return body;
        }, body -> {
            if (playerView == null || activePlaybackFile != file
                    || !sidecarMenuKey(sidecar).equals(selectedSidecarKey)) {
                return;
            }
            try {
                if ("sup".equals(sidecar.format)) {
                    java.util.List<PgsSubtitleCue> supCues =
                            PgsSubtitleParser.parse(new java.io.ByteArrayInputStream(body));
                    if (subtitleOverlay != null) {
                        subtitleOverlay.showPgs(supCues);
                        syncSubtitleInset();
                    }
                } else {
                    if (subtitleOverlay != null) {
                        subtitleOverlay.showText(new String(body, java.nio.charset.StandardCharsets.UTF_8),
                                sidecar.format);
                        syncSubtitleInset();
                    }
                }
            } catch (Exception invalid) {
                toast("这份字幕读不了：" + invalid.getMessage());
                return;
            }
            startSubtitleTick();
            toast("已用 " + sidecarMenuLabel(sidecar));
        }, failure -> {
            selectedSidecarKey = null;
            activeSidecar = null;
            toast(failure.getMessage() == null ? "读不到转档出来的字幕。" : failure.getMessage());
        });
    }

    private String sidecarMenuKey(LocalEngine.Sidecar sidecar) {
        return sidecar.videoFileId + "#" + sidecar.trackNumber + "#" + sidecar.format;
    }

    private String sidecarMenuLabel(LocalEngine.Sidecar sidecar) {
        String language = sidecarLanguageName(sidecar.language);
        String format = sidecar.format == null ? "" : sidecar.format.toUpperCase(java.util.Locale.ROOT);
        if (sidecar.external) {
            return "媒体源外挂字幕 · " + language + "（" + format + "）";
        }
        return "已存媒体源 · " + language + "（" + format + " · " + sidecar.cueCount + " 条"
                + ("partial".equals(sidecar.status) ? " · 还在后台转" : "") + "）";
    }

    private static String sidecarLanguageName(String code) {
        switch (code == null ? "" : code) {
            case "zh":
                return "中文";
            case "en":
                return "英语";
            case "ja":
                return "日语";
            case "ko":
                return "韩语";
            default:
                return code == null || code.isEmpty() ? "未标注" : code;
        }
    }

    /** 关掉 mpv 自己那几条字幕轨，覆盖层才不会被内封字幕重复画一遍。 */
    private void clearSubtitleTracks() {
        if (playerView != null) {
            playerView.setSubtitleTrack("no");
        }
        if (subtitleOverlay != null) {
            subtitleOverlay.clear();
        }
        selectedEmbeddedSubtitleOrdinal = -1;
        selectedExternalSubtitleId = null;
        selectedRuntimeSubtitleId = null;
        selectedContainerSubtitleTrack = null;
    }

    private void syncSubtitleInset() {
        if (subtitleOverlay == null || playerView == null) {
            return;
        }
        int controls = playerControlsBar == null ? 0 : playerControlsBar.getHeight();
        subtitleOverlay.setBottomInset(controls + dp(6), playerView.letterboxInset());
    }

    /** 覆盖层按播放位置取当前那条；PGS 也在这条链路上自己定时。 */
    private void startSubtitleTick() {
        if (subtitleTicker != null) {
            main.removeCallbacks(subtitleTicker);
        }
        subtitleTicker = new Runnable() {
            @Override
            public void run() {
                if (screen != Screen.PLAYER || playerView == null || subtitleOverlay == null) {
                    return;
                }
                subtitleOverlay.update(playerView.currentTimeSeconds());
                main.postDelayed(this, 250);
            }
        };
        main.postDelayed(subtitleTicker, 250);
    }

    private void stopSubtitleTick() {
        if (subtitleTicker != null) {
            main.removeCallbacks(subtitleTicker);
            subtitleTicker = null;
        }
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
                updatePlayerStatusLine(file);
                syncKeepScreenOn();
                main.postDelayed(this, 2000);
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
        // 左上角写刮削出来的片名，S01E01 这类文件名只出现在下面的摘要里
        playerTitleLabel = text(playerHeadingFor(file), 18, Typeface.BOLD, COLOR_TEXT);
        overlay.addView(playerTitleLabel);
        String summary = playerSummaryFor(file);
        if (!summary.isEmpty()) {
            overlay.addView(text(summary, 12, Typeface.NORMAL, COLOR_MUTED));
        }
        // 右上：当前模式 + 当前字幕/音轨，一眼看得出现在走的是哪条链路
        playerModeLabel = text(directPlayback ? "高性能直出" : "兼容字幕播放", 12, Typeface.BOLD, 0xFFFFFFFF);
        playerModeLabel.setPadding(dp(10), dp(5), dp(10), dp(5));
        playerModeLabel.setBackground(rounded(Color.argb(150, 0, 0, 0), 0, 0, dp(14)));
        FrameLayout.LayoutParams modeParams = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP | Gravity.END);
        modeParams.setMargins(dp(0), dp(14), dp(16), 0);
        root.addView(playerModeLabel, modeParams);

        playerSubtitleLabel = text("", 12, Typeface.NORMAL, 0xFFFFFFFF);
        playerSubtitleLabel.setPadding(dp(10), dp(4), dp(10), dp(4));
        playerSubtitleLabel.setBackground(rounded(Color.argb(120, 0, 0, 0), 0, 0, dp(12)));
        FrameLayout.LayoutParams subParams = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP | Gravity.END);
        subParams.setMargins(0, dp(46), dp(16), 0);
        root.addView(playerSubtitleLabel, subParams);

        playerTimeLabel = text("", 13, Typeface.BOLD, 0xFFFFFFFF);
        playerTimeLabel.setPadding(dp(10), dp(4), dp(10), dp(4));
        playerTimeLabel.setBackground(rounded(Color.argb(120, 0, 0, 0), 0, 0, dp(12)));
        FrameLayout.LayoutParams timeParams = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP | Gravity.START);
        timeParams.setMargins(dp(16), dp(64), 0, 0);
        root.addView(playerTimeLabel, timeParams);
        return overlay;
    }

    /**
     * 标题走详情页片名；没刮到资料才退回文件标签（与电视端 playerHeadingFor 一致）。
     * 电影被解析成 {@code S00E01} 这类光秃秃的编号时（itemKind 对不上），也宁可写片名：
     * 左上角给观众看的是「在放哪部片」，不是分集号。
     */
    private String playerHeadingFor(Models.VideoFile file) {
        Models.LibraryDetail detail = currentDetail;
        String label = file.label();
        if (detail != null && detail.title != null && !detail.title.isEmpty()) {
            String kind = file.episodeNumber != null ? "tv" : "movie";
            if (kind.equals(detail.itemKind)) {
                return detail.title;
            }
            if (label == null || label.trim().isEmpty() || label.trim().matches("S\\d{2}E\\d{2}")) {
                return detail.title;
            }
        }
        return label == null || label.trim().isEmpty() ? "正在播放" : label;
    }

    /** 第二行写分集信息与封装 / 编码：编号只作为分集标签出现在这里。 */
    private String playerSummaryFor(Models.VideoFile file) {
        ArrayList<String> parts = new ArrayList<>();
        if (file.seasonNumber != null && file.episodeNumber != null) {
            parts.add(episodeDisplayLabel(file.seasonNumber, file.episodeNumber));
            if (file.episodeTitle != null && !file.episodeTitle.isEmpty()) {
                parts.add(file.episodeTitle);
            }
        }
        String media = file.mediaSummary();
        if (!media.isEmpty()) {
            parts.add(media);
        }
        return TextUtils.join(" · ", parts);
    }

    /** S00 是特别季，标签写作「特别篇第 N 集」。 */
    private static String episodeDisplayLabel(int season, int episode) {
        if (season == 0) {
            return "特别篇第 " + episode + " 集";
        }
        return "第 " + season + " 季第 " + episode + " 集";
    }

    private void updatePlayerStatusLine(Models.VideoFile file) {
        if (playerTimeLabel == null || file == null) {
            return;
        }
        double position = playerView == null ? file.positionSeconds : playerView.currentTimeSeconds();
        double duration = playerView == null ? file.durationSeconds : playerView.durationSeconds();
        if (duration <= 0) {
            duration = file.durationSeconds;
        }
        if (duration <= 0) {
            playerTimeLabel.setText("已看 " + formatClock(position));
            return;
        }
        playerTimeLabel.setText("已看 " + formatClock(position) + " / 剩 "
                + formatClock(Math.max(0, duration - position)));
        if (playerSubtitleLabel != null) {
            playerSubtitleLabel.setText(subtitleTrackSummary(file));
        }
    }

    private String subtitleTrackSummary(Models.VideoFile file) {
        StringBuilder builder = new StringBuilder();
        if (activeSidecar != null) {
            builder.append("字幕 ").append(sidecarLanguageName(activeSidecar.language))
                    .append(" · ").append(activeSidecar.format == null ? ""
                            : activeSidecar.format.toUpperCase(java.util.Locale.ROOT));
        } else if (selectedEmbeddedSubtitleOrdinal >= 0
                && selectedEmbeddedSubtitleOrdinal < file.subtitleStreams.size()) {
            Models.VideoFileStream stream = file.subtitleStreams.get(selectedEmbeddedSubtitleOrdinal);
            builder.append("字幕 ").append(trackLanguageName(stream.language));
            if (stream.title != null && !stream.title.trim().isEmpty()) {
                builder.append(" · ").append(stream.title.trim());
            }
        } else if (selectedContainerSubtitleTrack != null) {
            builder.append("字幕 内封 ").append(selectedContainerSubtitleTrack);
        } else if (selectedRuntimeSubtitleId != null) {
            RuntimeTrack picked = findRuntimeSubtitle(selectedRuntimeSubtitleId);
            builder.append("字幕 内封 ").append(trackLanguageName(picked == null ? "" : picked.language));
            if (picked != null && !picked.codec.isEmpty()) {
                builder.append(subtitleFormatLabel(picked.codec));
            }
        } else if (selectedExternalSubtitleId != null) {
            builder.append("字幕 外挂");
        } else {
            builder.append("字幕 关闭");
        }
        RuntimeTrack audio = findRuntimeAudio(selectedRuntimeAudioId);
        if (audio != null) {
            builder.append("  音轨 ").append(trackLanguageName(audio.language));
        } else if (selectedAudioTrackOrdinal >= 0 && selectedAudioTrackOrdinal < file.audioTracks.size()) {
            builder.append("  音轨 ").append(trackLanguageName(file.audioTracks.get(selectedAudioTrackOrdinal).language));
        }
        return builder.toString();
    }

    private RuntimeTrack findRuntimeSubtitle(String key) {
        if (key == null) {
            return null;
        }
        for (RuntimeTrack track : runtimeSubtitleTracks) {
            if (key.equals(runtimeTrackKey(track))) {
                return track;
            }
        }
        return null;
    }

    private RuntimeTrack findRuntimeAudio(String key) {
        if (key == null) {
            return null;
        }
        for (RuntimeTrack track : runtimeAudioTracks) {
            if (key.equals(runtimeTrackKey(track))) {
                return track;
            }
        }
        return null;
    }

    /** 轨道语言码念成人话。容器里归一化出来的是 zh/en 这种两位码，两种写法都得认。 */
    private static String trackLanguageName(String code) {
        String normalized = code == null ? "" : code.trim().toLowerCase(java.util.Locale.ROOT);
        switch (normalized) {
            case "":
            case "und":
                return "未标注";
            case "zh":
            case "zho":
            case "chi":
            case "chs":
            case "cht":
            case "zhs":
            case "zht":
            case "zh-cn":
            case "zh-tw":
            case "zh-hk":
            case "zh-hans":
            case "zh-hant":
                return "中文";
            case "en":
            case "eng":
                return "英语";
            case "ja":
            case "jpn":
                return "日语";
            case "ko":
            case "kor":
                return "韩语";
            case "fr":
            case "fra":
            case "fre":
                return "法语";
            case "de":
            case "deu":
            case "ger":
                return "德语";
            case "es":
            case "spa":
                return "西语";
            case "ru":
            case "rus":
                return "俄语";
            case "it":
            case "ita":
                return "意语";
            case "pt":
            case "por":
                return "葡语";
            case "th":
            case "tha":
                return "泰语";
            case "vi":
            case "vie":
                return "越语";
            default:
                return normalized;
        }
    }

    /**
     * 摆好播放器与字幕覆盖层。手机上点一下画面就把控制条与状态条叫回来：电视端有方向键
     * 触发 showPlayerChromeTemporarily，手机没有遥控器，只能靠点画面。
     */
    private void mountPlayerView() {
        playerView = new MpvVideoView(this, directPlayback, directPlayback);
        playerView.setStretchToFill(aspectStretch);
        playerView.setClickable(true);
        playerView.setOnClickListener(view -> showChromeTemporarily());
        root.addView(playerView, match());
        subtitleOverlay = new PhoneSubtitleOverlay(this, root);
    }

    /** 菜单、状态条这些覆盖层一起显隐，播起来 4 秒后自动收干净。 */
    private void setPlayerChromeVisible(boolean visible) {
        if (playerControlsBar != null) {
            playerControlsBar.setVisibility(visible ? View.VISIBLE : View.GONE);
        }
        if (playerTitleLabel != null && playerTitleLabel.getParent() instanceof View) {
            ((View) playerTitleLabel.getParent()).setVisibility(visible ? View.VISIBLE : View.GONE);
        }
        if (playerModeLabel != null) {
            playerModeLabel.setVisibility(visible ? View.VISIBLE : View.GONE);
        }
        if (playerSubtitleLabel != null) {
            playerSubtitleLabel.setVisibility(visible ? View.VISIBLE : View.GONE);
        }
        if (playerTimeLabel != null) {
            playerTimeLabel.setVisibility(visible ? View.VISIBLE : View.GONE);
        }
        syncSubtitleInset();
    }

    private LinearLayout playerControls() {
        LinearLayout controls = new LinearLayout(this);
        controls.setGravity(Gravity.CENTER);
        controls.setPadding(dp(12), dp(8), dp(12), dp(8));
        controls.setBackground(rounded(Color.argb(172, 0, 0, 0), 0, 0, dp(24)));
        playerControlsBar = controls;

        Button rewind = compactButton("«10");
        controls.addView(rewind, new LinearLayout.LayoutParams(dp(62), dp(48)));
        attachSeekHold(rewind, -1);

        Button replay = compactButton("↺10");
        controls.addView(replay, margin(new LinearLayout.LayoutParams(dp(62), dp(48)), dp(6), 0, 0, 0));
        attachSeekHold(replay, -1);

        Button pause = compactButton("暂停");
        controls.addView(pause, margin(new LinearLayout.LayoutParams(dp(92), dp(48)), dp(6), 0, 0, 0));
        pause.setOnClickListener(view -> {
            if (playerView != null) {
                playerView.togglePaused();
                syncKeepScreenOn();
            }
        });

        Button forward = compactButton("10»");
        controls.addView(forward, margin(new LinearLayout.LayoutParams(dp(62), dp(48)), dp(6), 0, 0, 0));
        attachSeekHold(forward, 1);

        Button subtitle = compactButton("字幕");
        controls.addView(subtitle, margin(new LinearLayout.LayoutParams(dp(76), dp(48)), dp(10), 0, 0, 0));
        subtitle.setOnClickListener(view -> showSubtitleMenu(activePlaybackFile == null
                ? null : activePlaybackFile, subtitle));

        Button audio = compactButton("音轨");
        controls.addView(audio, margin(new LinearLayout.LayoutParams(dp(76), dp(48)), dp(6), 0, 0, 0));
        audio.setOnClickListener(view -> showAudioMenu(activePlaybackFile == null
                ? null : activePlaybackFile, audio));

        Button mode = compactButton("模式");
        controls.addView(mode, margin(new LinearLayout.LayoutParams(dp(76), dp(48)), dp(6), 0, 0, 0));
        mode.setOnClickListener(view -> showPlaybackModeMenu());

        Button aspect = compactButton("比例");
        controls.addView(aspect, margin(new LinearLayout.LayoutParams(dp(76), dp(48)), dp(6), 0, 0, 0));
        aspect.setOnClickListener(view -> showAspectMenu());

        return controls;
    }

    /**
     * 长按快退/快进：步长随按住时间从 10× 爬到 250× 实时速度，点按仍是 ±10 秒。
     * 与电视端是同一套档位。
     */
    private void attachSeekHold(Button button, int direction) {
        button.setOnTouchListener((view, event) -> {
            if (playerView == null) {
                return false;
            }
            int action = event.getActionMasked();
            if (action == android.view.MotionEvent.ACTION_DOWN) {
                seekHoldStartedAtMs = android.os.SystemClock.elapsedRealtime();
                seekHeld = true;
                runSeekStep(direction);
                return false;
            }
            if (action == android.view.MotionEvent.ACTION_UP
                    || action == android.view.MotionEvent.ACTION_CANCEL) {
                seekHeld = false;
                return false;
            }
            return false;
        });
    }

    private void runSeekStep(int direction) {
        if (!seekHeld || playerView == null) {
            return;
        }
        playerView.seek(direction * seekStepSeconds(android.os.SystemClock.elapsedRealtime() - seekHoldStartedAtMs));
        if (activePlaybackFile != null) {
            updatePlayerStatusLine(activePlaybackFile);
        }
        main.postDelayed(() -> runSeekStep(direction), 120);
    }

    private double seekStepSeconds(long heldMs) {
        if (heldMs < 700) {
            return SEEK_STEP_SECONDS;
        }
        if (heldMs < 1800) {
            return 2.4d;
        }
        if (heldMs < 3500) {
            return 8d;
        }
        return 20d;
    }

    private void showChromeTemporarily() {
        setPlayerChromeVisible(true);
        if (chromeHider != null) {
            main.removeCallbacks(chromeHider);
        }
        chromeHider = () -> {
            if (screen == Screen.PLAYER) {
                setPlayerChromeVisible(false);
            }
        };
        main.postDelayed(chromeHider, 4000);
    }

    // ---- 播放器实时轨道：与电视端同一套 track-list 读法 ------------------------------------

    private static final class RuntimeTrack {
        final int ordinal;
        final String mpvId;
        final String type;
        final String codec;
        final String language;
        final String title;
        final Integer channels;
        final boolean selected;
        final boolean isDefault;

        RuntimeTrack(int ordinal, String mpvId, String type, String codec, String language,
                     String title, Integer channels, boolean selected, boolean isDefault) {
            this.ordinal = Math.max(0, ordinal);
            this.mpvId = mpvId == null || mpvId.isEmpty() ? String.valueOf(this.ordinal + 1) : mpvId;
            this.type = type == null ? "" : type;
            this.codec = codec == null ? "" : codec;
            this.language = language == null ? "" : language;
            this.title = title == null ? "" : title;
            this.channels = channels;
            this.selected = selected;
            this.isDefault = isDefault;
        }
    }

    /** 问 mpv 要一份现在的轨道清单；播放器还没报数就保留上一次的结果。 */
    private void refreshRuntimeTracks() {
        if (playerView == null) {
            runtimeAudioTracks = java.util.Collections.emptyList();
            runtimeSubtitleTracks = java.util.Collections.emptyList();
            return;
        }
        int count = (int) Math.round(playerView.getDoubleProperty("track-list/count", 0));
        if (count <= 0) {
            return;
        }
        java.util.List<RuntimeTrack> audio = new java.util.ArrayList<>();
        java.util.List<RuntimeTrack> subtitles = new java.util.ArrayList<>();
        int audioOrdinal = 0;
        int subtitleOrdinal = 0;
        for (int index = 0; index < Math.min(count, 128); index++) {
            String prefix = "track-list/" + index + "/";
            String type = runtimePropertyString(prefix + "type");
            if ("audio".equals(type)) {
                audio.add(readRuntimeTrack(prefix, audioOrdinal++, "audio"));
            } else if ("sub".equals(type) || "subtitle".equals(type)) {
                subtitles.add(readRuntimeTrack(prefix, subtitleOrdinal++, "sub"));
            }
        }
        runtimeAudioTracks = audio;
        runtimeSubtitleTracks = subtitles;
    }

    private RuntimeTrack readRuntimeTrack(String prefix, int ordinal, String type) {
        String mpvId = runtimePropertyString(prefix + "id");
        if (mpvId.isEmpty()) {
            double idValue = playerView == null ? -1 : playerView.getDoubleProperty(prefix + "id", -1);
            if (Double.isFinite(idValue) && idValue > 0) {
                mpvId = Math.abs(idValue - Math.rint(idValue)) < 0.001d
                        ? String.valueOf((int) Math.round(idValue)) : String.valueOf(idValue);
            }
        }
        if (mpvId.isEmpty()) {
            mpvId = String.valueOf(ordinal + 1);
        }
        Integer channels = null;
        double channelValue = playerView == null ? -1 : playerView.getDoubleProperty(prefix + "demux-channel-count", -1);
        if (Double.isFinite(channelValue) && channelValue > 0) {
            channels = (int) Math.round(channelValue);
        }
        return new RuntimeTrack(ordinal, mpvId, type,
                runtimePropertyString(prefix + "codec"),
                runtimePropertyString(prefix + "lang"),
                runtimePropertyString(prefix + "title"),
                channels,
                runtimePropertyBoolean(prefix + "selected"),
                runtimePropertyBoolean(prefix + "default"));
    }

    private String runtimePropertyString(String property) {
        if (playerView == null) {
            return "";
        }
        String value = playerView.getStringProperty(property, "");
        return value == null ? "" : value.trim();
    }

    private boolean runtimePropertyBoolean(String property) {
        if (playerView == null) {
            return false;
        }
        String value = runtimePropertyString(property).toLowerCase(java.util.Locale.ROOT);
        if (value.equals("yes") || value.equals("true") || value.equals("1")) {
            return true;
        }
        if (value.equals("no") || value.equals("false") || value.equals("0")) {
            return false;
        }
        double numeric = playerView.getDoubleProperty(property, 0);
        return Double.isFinite(numeric) && numeric > 0.5d;
    }

    private String runtimeTrackKey(RuntimeTrack track) {
        return track == null ? "" : track.type + "|" + track.mpvId + "|" + track.ordinal;
    }

    private boolean isRuntimeSubtitleSelected(RuntimeTrack track) {
        return selectedRuntimeSubtitleId != null
                && selectedRuntimeSubtitleId.equals(runtimeTrackKey(track));
    }

    private boolean isRuntimeAudioSelected(RuntimeTrack track) {
        String key = runtimeTrackKey(track);
        return key.equals(selectedRuntimeAudioId)
                || (selectedRuntimeAudioId == null && selectedAudioTrackOrdinal == track.ordinal && track.selected);
    }

    /** mpv 没报字幕轨时自己去容器里数一遍；同一部片只探一次，别把 WebDAV 的 HEAD 打爆。 */
    private java.util.List<com.omniplay.tv.subtitle.MatroskaSubtitleExtractor.Track> containerSubtitleTracksOf(
            Models.VideoFile file) {
        String key = file.id + "|" + file.relativePath;
        if (!key.equals(containerProbeKey)) {
            containerProbeKey = key;
            try {
                probedContainerTracks = api.engine().embeddedSubtitleTracks(file.id, file.relativePath);
            } catch (Exception error) {
                probedContainerTracks = java.util.Collections.emptyList();
            }
        }
        return probedContainerTracks;
    }

    private boolean isContainerSubtitleSelected(com.omniplay.tv.subtitle.MatroskaSubtitleExtractor.Track track) {
        return selectedContainerSubtitleTrack != null
                && selectedContainerSubtitleTrack == track.trackNumber;
    }

    private static String subtitleFormatLabel(String format) {
        if (format == null || format.trim().isEmpty()) {
            return "";
        }
        String normalized = format.trim().toLowerCase(java.util.Locale.ROOT);
        if (normalized.equals("sup") || normalized.contains("pgs")) {
            return " · PGS";
        }
        return " · " + normalized.toUpperCase(java.util.Locale.ROOT);
    }

    private void resetRuntimeTrackState() {
        runtimeAudioTracks = java.util.Collections.emptyList();
        runtimeSubtitleTracks = java.util.Collections.emptyList();
        selectedRuntimeAudioId = null;
        selectedRuntimeSubtitleId = null;
        selectedContainerSubtitleTrack = null;
        containerProbeKey = null;
        probedContainerTracks = java.util.Collections.emptyList();
    }

    /** mpv 报出来的内封字幕：直接交给 mpv 自己渲染（兼容模式下 vo=gpu，字幕它画得来）。 */
    private void selectRuntimeSubtitleTrack(Models.VideoFile file, RuntimeTrack track) {
        if (playerView == null) {
            return;
        }
        clearSubtitleTracks();
        playerView.setSubtitleTrack(track.mpvId);
        selectedRuntimeSubtitleId = runtimeTrackKey(track);
        updatePlayerStatusLine(file);
        showChromeTemporarily();
    }

    /**
     * 只在容器里看得见的内封字幕（mpv 没报 track-list 时的兜底）。文本轨当场抽出来交给覆盖层画；
     * 位图轨整轨太大，交给后台转档落盘，转完就是一条 .sup sidecar，走的还是现成那条链路。
     */
    private void selectContainerSubtitle(Models.VideoFile file, int trackNumber, String format) {
        if ("sup".equals(format)) {
            toast("这条是位图字幕，正在后台抽出来。");
            runAsync(() -> api.engine().transcodeSidecar(file.id, file.relativePath, trackNumber),
                    report -> {
                        if (report != null) {
                            toast(report.message);
                            if (report.converted) {
                                selectedContainerSubtitleTrack = trackNumber;
                                autoLoadFirstSidecar(file);
                            }
                        }
                        updatePlayerStatusLine(file);
                    },
                    ignored -> toast("位图字幕抽取失败，可以先用后台转档。"));
            showChromeTemporarily();
            return;
        }
        if (playerView != null) {
            playerView.setSubtitleTrack("no");
        }
        if (subtitleOverlay != null) {
            subtitleOverlay.clear();
        }
        stopSubtitleTick();
        selectedSidecarKey = null;
        activeSidecar = null;
        selectedEmbeddedSubtitleOrdinal = -1;
        selectedExternalSubtitleId = null;
        selectedRuntimeSubtitleId = null;
        selectedContainerSubtitleTrack = trackNumber;
        runAsync(() -> api.engine().readEmbeddedSubtitle(file.id, file.relativePath, trackNumber),
                subtitle -> {
                    if (screen != Screen.PLAYER || activePlaybackFile != file
                            || !Integer.valueOf(trackNumber).equals(selectedContainerSubtitleTrack)) {
                        return;
                    }
                    if (subtitleOverlay == null) {
                        return;
                    }
                    if (subtitle.sup != null) {
                        try {
                            subtitleOverlay.showPgs(PgsSubtitleParser.parse(
                                    new java.io.ByteArrayInputStream(subtitle.sup)));
                        } catch (Exception invalid) {
                            selectedContainerSubtitleTrack = null;
                            toast("这条位图字幕读不了：" + invalid.getMessage());
                            return;
                        }
                    } else {
                        subtitleOverlay.showText(subtitle.text == null ? "" : subtitle.text, subtitle.format);
                    }
                    syncSubtitleInset();
                    startSubtitleTick();
                    updatePlayerStatusLine(file);
                    toast("已用内封字幕 · " + trackLanguageName(subtitle.language));
                },
                failure -> {
                    if (!Integer.valueOf(trackNumber).equals(selectedContainerSubtitleTrack)) {
                        return;
                    }
                    selectedContainerSubtitleTrack = null;
                    toast("读内封字幕失败：" + (failure.getMessage() == null ? "格式不支持" : failure.getMessage()));
                });
        showChromeTemporarily();
    }

    private void selectRuntimeAudioTrack(Models.VideoFile file, RuntimeTrack track) {
        if (playerView == null) {
            return;
        }
        playerView.setAudioTrack(track.mpvId);
        selectedRuntimeAudioId = runtimeTrackKey(track);
        selectedAudioTrackOrdinal = -1;
        updatePlayerStatusLine(file);
        showChromeTemporarily();
    }

    private void showSubtitleMenu(Models.VideoFile file, View anchor) {
        if (file == null) {
            return;
        }
        setPlayerChromeVisible(true);
        java.util.List<String> labels = new java.util.ArrayList<>();
        java.util.List<Runnable> actions = new java.util.ArrayList<>();
        refreshRuntimeTracks();
        // 直出模式（vo=mediacodec_embed）里 mpv 整个不参与画面，内封轨点中也没反应，
        // 这一档只列覆盖层读得懂的：媒体源外挂字幕与 NAS 转档出来的 sidecar。
        // 内封轨的来源按可信度排：mpv 报的 track-list 最准；它没报就直接读容器
        // （本地库里 file.subtitleStreams 恒为空，只靠那一份是列不出内封字幕的）。
        boolean useRuntimeSubtitles = !runtimeSubtitleTracks.isEmpty();
        java.util.List<com.omniplay.tv.subtitle.MatroskaSubtitleExtractor.Track> containerTracks
                = useRuntimeSubtitles ? java.util.Collections.<com.omniplay.tv.subtitle.MatroskaSubtitleExtractor.Track>emptyList()
                : containerSubtitleTracksOf(file);
        int embeddedCount = useRuntimeSubtitles ? runtimeSubtitleTracks.size()
                : (containerTracks.isEmpty() ? file.subtitleStreams.size() : containerTracks.size());
        if (!directPlayback) {
            if (useRuntimeSubtitles) {
                for (RuntimeTrack track : runtimeSubtitleTracks) {
                    labels.add((isRuntimeSubtitleSelected(track) ? "✓ " : "    ")
                            + "内封 " + (track.ordinal + 1) + " · " + trackLanguageName(track.language)
                            + (track.title.trim().isEmpty() ? "" : " · " + track.title.trim())
                            + subtitleFormatLabel(track.codec)
                            + (track.isDefault ? "（默认）" : ""));
                    actions.add(() -> selectRuntimeSubtitleTrack(file, track));
                }
            } else if (!containerTracks.isEmpty()) {
                for (com.omniplay.tv.subtitle.MatroskaSubtitleExtractor.Track track : containerTracks) {
                    labels.add((isContainerSubtitleSelected(track) ? "✓ " : "    ")
                            + "内封 " + (track.trackNumber) + " · " + trackLanguageName(track.language)
                            + (track.name == null || track.name.trim().isEmpty()
                                    ? "" : " · " + track.name.trim())
                            + subtitleFormatLabel(track.format)
                            + (track.isDefault ? "（默认）" : ""));
                    int trackNumber = track.trackNumber;
                    String format = track.format;
                    actions.add(() -> selectContainerSubtitle(file, trackNumber, format));
                }
            } else {
                for (int index = 0; index < file.subtitleStreams.size(); index++) {
                    Models.VideoFileStream stream = file.subtitleStreams.get(index);
                    int ordinal = index;
                    labels.add((selectedEmbeddedSubtitleOrdinal == index ? "✓ " : "    ")
                            + "内封 " + (index + 1) + " · " + trackLanguageName(stream.language)
                            + (stream.title == null || stream.title.trim().isEmpty()
                                    ? "" : " · " + stream.title.trim())
                            + (stream.isDefault ? "（默认）" : ""));
                    actions.add(() -> selectEmbeddedSubtitle(file, ordinal));
                }
            }
        }
        for (LocalEngine.Sidecar sidecar : api.engine().sidecarsFor(file.id)) {
            if (!sidecar.usable()) {
                continue;
            }
            String key = sidecarMenuKey(sidecar);
            labels.add((key.equals(selectedSidecarKey) ? "✓ " : "    ") + sidecarMenuLabel(sidecar));
            actions.add(() -> selectSubtitleSidecar(file, sidecar));
        }
        labels.add("    关闭字幕");
        actions.add(() -> {
            clearSubtitleTracks();
            selectedSidecarKey = null;
            activeSidecar = null;
            showChromeTemporarily();
        });
        labels.add("    再找一遍媒体源里的外挂字幕");
        actions.add(() -> lookForSidecars(file));
        labels.add("    后台转档这条的内封字幕");
        actions.add(() -> transcodeSidecarNow(file));
        if (directPlayback && embeddedCount > 0) {
            labels.add("    直出不渲染内封字幕（已收起 " + embeddedCount + " 条）· 改用兼容字幕播放");
            actions.add(() -> switchPlaybackModeDuringPlayback(false));
        }
        if (labels.isEmpty()) {
            labels.add("    暂无可切换字幕");
            actions.add(() -> {
            });
        }
        showPlayerMenu("字幕", labels, actions);
    }

    private void showAudioMenu(Models.VideoFile file, View anchor) {
        if (file == null) {
            return;
        }
        setPlayerChromeVisible(true);
        java.util.List<String> labels = new java.util.ArrayList<>();
        java.util.List<Runnable> actions = new java.util.ArrayList<>();
        refreshRuntimeTracks();
        if (!runtimeAudioTracks.isEmpty()) {
            for (RuntimeTrack track : runtimeAudioTracks) {
                labels.add((isRuntimeAudioSelected(track) ? "✓ " : "    ")
                        + "音轨 " + (track.ordinal + 1) + " · " + trackLanguageName(track.language)
                        + (track.codec.isEmpty() ? "" : " · " + track.codec)
                        + (track.channels == null ? "" : " · " + track.channels + " 声道")
                        + (track.title.trim().isEmpty() ? "" : " · " + track.title.trim()));
                actions.add(() -> selectRuntimeAudioTrack(file, track));
            }
        } else {
            for (int index = 0; index < file.audioTracks.size(); index++) {
                Models.VideoFileStream track = file.audioTracks.get(index);
                int ordinal = index;
                labels.add((selectedAudioTrackOrdinal == index ? "✓ " : "    ")
                        + "音轨 " + (index + 1) + " · " + trackLanguageName(track.language)
                        + (track.codec == null || track.codec.isEmpty() ? "" : " · " + track.codec)
                        + (track.channels == null ? "" : " · " + track.channels + " 声道")
                        + (track.title == null || track.title.trim().isEmpty() ? "" : " · " + track.title.trim()));
                actions.add(() -> {
                    if (playerView != null) {
                        playerView.setAudioTrack(String.valueOf(ordinal + 1));
                        selectedAudioTrackOrdinal = ordinal;
                        selectedRuntimeAudioId = null;
                        updatePlayerStatusLine(file);
                    }
                });
            }
        }
        if (labels.isEmpty()) {
            // mpv 要等文件装载完才报得出 track-list；本地库里的 audioTracks 是空表，帮不上忙
            labels.add("    播放器还没报音轨，稍等一下再打开");
            actions.add(() -> {
            });
        }
        showPlayerMenu("音轨", labels, actions);
    }

    private void selectEmbeddedSubtitle(Models.VideoFile file, int ordinal) {
        if (playerView == null) {
            return;
        }
        if (subtitleOverlay != null) {
            subtitleOverlay.clear();
        }
        stopSubtitleTick();
        selectedSidecarKey = null;
        activeSidecar = null;
        selectedExternalSubtitleId = null;
        playerView.setSubtitleTrack(String.valueOf(ordinal + 1));
        selectedEmbeddedSubtitleOrdinal = ordinal;
        updatePlayerStatusLine(file);
        showChromeTemporarily();
    }

    /** 手动重找：媒体源里现成的外挂字幕、NAS 刚写回的转档结果都算。 */
    private void lookForSidecars(Models.VideoFile file) {
        if (file == null) {
            return;
        }
        runAsync(() -> api.engine().discoverSidecars(file.id, file.relativePath, file.subtitleStreams),
                found -> {
                    if (screen != Screen.PLAYER || activePlaybackFile != file) {
                        return;
                    }
                    toast(found > 0
                            ? "找到 " + found + " 条现成的外挂字幕，已经收进字幕菜单。"
                            : "媒体源里没有这条的外挂字幕，可以先后台转档这条。");
                    if (found > 0) {
                        autoLoadFirstSidecar(file);
                    }
                    showSubtitleMenu(file, null);
                },
                ignored -> toast("找外挂字幕失败了。"));
    }

    private void transcodeSidecarNow(Models.VideoFile file) {
        if (file == null) {
            return;
        }
        toast("开始后台转档这条的内封字幕。");
        runAsync(() -> api.engine().transcodeSidecar(file.id, file.relativePath),
                report -> {
                    if (report != null) {
                        toast(report.message);
                        if (report.converted) {
                            selectedSidecarKey = null;
                            autoLoadFirstSidecar(file);
                        }
                    }
                },
                ignored -> toast("字幕转档失败。"));
    }

    private void switchPlaybackModeDuringPlayback(boolean highPerformanceDirect) {
        if (activePlaybackFile == null || playerView == null) {
            return;
        }
        if (highPerformanceDirect == directPlayback) {
            return;
        }
        Models.VideoFile file = activePlaybackFile;
        double resumeAt = playerView.currentTimeSeconds();
        if (resumeAt <= 0) {
            resumeAt = file.positionSeconds;
        }
        destroyPlayer();
        root.removeAllViews();
        directPlayback = highPerformanceDirect;
        storeDirectPlayback(directPlayback);
        mountPlayerView();
        root.addView(playerOverlay(file), new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP));
        LinearLayout rebuiltControls = playerControls();
        HorizontalScrollView rebuiltScroll = new HorizontalScrollView(this);
        rebuiltScroll.setHorizontalScrollBarEnabled(false);
        rebuiltScroll.addView(rebuiltControls, new HorizontalScrollView.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        rebuiltScroll.setPadding(dp(8), 0, dp(8), dp(6));
        root.addView(rebuiltScroll, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, dp(64), Gravity.BOTTOM));
        activePlaybackFile = file;
        selectedSidecarKey = null;
        activeSidecar = null;
        selectedEmbeddedSubtitleOrdinal = -1;
        selectedAudioTrackOrdinal = -1;
        resetRuntimeTrackState();
        applyDefaultAudioTrack(file);
        final double startAt = Math.max(0, resumeAt);
        runAsync(() -> resolvePlaybackTarget(file),
                target -> {
                    if (screen != Screen.PLAYER || playerView == null || activePlaybackFile != file) {
                        return;
                    }
                    if (!playerView.play(target.url, target.cookieHeader, startAt)) {
                        renderPlayerError(playerView.lastError());
                        return;
                    }
                    resetRuntimeTrackState();
                    applyDefaultAudioTrack(file);
                    autoLoadFirstSidecar(file);
                    startProgressUpdates(file);
                    updatePlayerStatusLine(file);
                    showChromeTemporarily();
                    syncKeepScreenOn();
                },
                error -> renderPlayerError(error.getMessage()));
        toast(directPlayback ? "已切到高性能直出：视频直接进解码器，字幕走覆盖层。"
                : "已切到兼容字幕播放：字幕由播放器画。");
    }

    /** 播放模式菜单：与电视端一样带 ✓，两条摆出来看得清现在用的是哪一种。 */
    private void showPlaybackModeMenu() {
        setPlayerChromeVisible(true);
        java.util.List<String> labels = new java.util.ArrayList<>();
        java.util.List<Runnable> actions = new java.util.ArrayList<>();
        labels.add((!directPlayback ? "✓ " : "    ") + "兼容字幕播放 · 字幕由播放器画");
        actions.add(() -> switchPlaybackModeDuringPlayback(false));
        labels.add((directPlayback ? "✓ " : "    ") + "高性能直出 · 视频直接进解码器");
        actions.add(() -> switchPlaybackModeDuringPlayback(true));
        showPlayerMenu("播放模式", labels, actions);
    }

    /** 画面比例菜单：拉伸与否都写清楚，点一下立刻生效。 */
    private void showAspectMenu() {
        setPlayerChromeVisible(true);
        java.util.List<String> labels = new java.util.ArrayList<>();
        java.util.List<Runnable> actions = new java.util.ArrayList<>();
        labels.add((!aspectStretch ? "✓ " : "    ") + "保持比例 · 上下留黑边");
        actions.add(() -> setAspect(false));
        labels.add((aspectStretch ? "✓ " : "    ") + "拉伸填满 · 铺满整屏");
        actions.add(() -> setAspect(true));
        showPlayerMenu("画面比例", labels, actions);
    }

    private void setAspect(boolean stretch) {
        if (aspectStretch == stretch) {
            showChromeTemporarily();
            return;
        }
        aspectStretch = stretch;
        if (playerView != null) {
            playerView.setStretchToFill(aspectStretch);
        }
        api.engine().settings().setPlayerStretchToFill(aspectStretch);
        if (playerModeLabel != null) {
            playerModeLabel.setText((directPlayback ? "高性能直出" : "兼容字幕播放")
                    + " · " + (aspectStretch ? "拉伸填满" : "保持比例"));
        }
        syncSubtitleInset();
    }

    /** 播放页的选项菜单：手机上用居中小弹窗，一屏能看完，点一行执行。 */
    private void showPlayerMenu(String title, java.util.List<String> labels, java.util.List<Runnable> actions) {
        android.app.Dialog dialog = new android.app.Dialog(this);
        dialog.requestWindowFeature(Window.FEATURE_NO_TITLE);
        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(dp(18), dp(16), dp(18), dp(12));
        panel.setBackground(rounded(COLOR_SURFACE, 0, 0, dp(16)));
        panel.addView(text(title, 17, Typeface.BOLD, COLOR_TEXT), margin(matchWidth(), 0, 0, 0, dp(10)));

        ScrollView scroll = new ScrollView(this);
        LinearLayout list = new LinearLayout(this);
        list.setOrientation(LinearLayout.VERTICAL);
        scroll.addView(list, new ScrollView.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT));
        for (int index = 0; index < labels.size(); index++) {
            TextView row = text(labels.get(index), 15, Typeface.NORMAL, COLOR_TEXT);
            row.setPadding(dp(6), dp(14), dp(6), dp(14));
            final Runnable action = actions.get(index);
            row.setOnClickListener(view -> {
                dialog.dismiss();
                action.run();
            });
            list.addView(row, margin(matchWidth(), 0, 0, 0, dp(1)));
        }
        panel.addView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f));

        Button close = compactButton("关闭");
        panel.addView(close, margin(matchWidth(), 0, dp(10), 0, 0));
        close.setOnClickListener(view -> dialog.dismiss());

        dialog.setContentView(panel);
        if (dialog.getWindow() != null) {
            dialog.getWindow().setBackgroundDrawable(new android.graphics.drawable.ColorDrawable(Color.TRANSPARENT));
            dialog.getWindow().setLayout((int) (getResources().getDisplayMetrics().widthPixels * 0.72f),
                    (int) (getResources().getDisplayMetrics().heightPixels * 0.7f));
        }
        dialog.show();
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
                startRemoteSeek(-1);
                return true;
            }
            if (keyCode == KeyEvent.KEYCODE_DPAD_RIGHT || keyCode == KeyEvent.KEYCODE_MEDIA_FAST_FORWARD) {
                startRemoteSeek(1);
                return true;
            }
        }
        return super.onKeyDown(keyCode, event);
    }

    @Override
    public boolean onKeyUp(int keyCode, KeyEvent event) {
        if (screen == Screen.PLAYER && (keyCode == KeyEvent.KEYCODE_DPAD_LEFT
                || keyCode == KeyEvent.KEYCODE_DPAD_RIGHT
                || keyCode == KeyEvent.KEYCODE_MEDIA_REWIND
                || keyCode == KeyEvent.KEYCODE_MEDIA_FAST_FORWARD)) {
            stopSeekRepeater();
            return true;
        }
        return super.onKeyUp(keyCode, event);
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
        stopSubtitleTick();
        stopSeekRepeater();
        if (chromeHider != null) {
            main.removeCallbacks(chromeHider);
            chromeHider = null;
        }
        if (subtitleOverlay != null) {
            subtitleOverlay.clear();
            subtitleOverlay = null;
        }
        playerControlsBar = null;
        playerTimeLabel = null;
        playerModeLabel = null;
        playerSubtitleLabel = null;
        playerTitleLabel = null;
        selectedSidecarKey = null;
        activeSidecar = null;
        selectedEmbeddedSubtitleOrdinal = -1;
        selectedExternalSubtitleId = null;
        if (playerView != null) {
            stopProgressUpdates();
            flushPlaybackProgress();
            playerView.destroyPlayer();
            playerView = null;
        }
    }

    /** 遥控器左右键：按住就按爬升后的步长连发，松开停止；点按仍是 ±10 秒附近。 */
    private void startRemoteSeek(int direction) {
        if (playerView == null) {
            return;
        }
        if (remoteSeekDirection == direction && remoteSeekRepeater != null) {
            return;
        }
        stopSeekRepeater();
        remoteSeekDirection = direction;
        seekHoldStartedAtMs = android.os.SystemClock.elapsedRealtime();
        remoteSeekHeld = true;
        runRemoteSeekStep();
    }

    private void runRemoteSeekStep() {
        if (!remoteSeekHeld || playerView == null || screen != Screen.PLAYER) {
            return;
        }
        playerView.seek(remoteSeekDirection * seekStepSeconds(
                android.os.SystemClock.elapsedRealtime() - seekHoldStartedAtMs));
        if (activePlaybackFile != null) {
            updatePlayerStatusLine(activePlaybackFile);
        }
        remoteSeekRepeater = this::runRemoteSeekStep;
        main.postDelayed(remoteSeekRepeater, 120);
    }

    private void stopSeekRepeater() {
        remoteSeekHeld = false;
        if (remoteSeekRepeater != null) {
            main.removeCallbacks(remoteSeekRepeater);
            remoteSeekRepeater = null;
        }
        remoteSeekDirection = 0;
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

    /** 图标按钮：画的是 mac 版那套 SF Symbols 对应图形（{@link Icons}），带 contentDescription 当提示。 */
    private View iconButton(Icons.Kind kind, String description, View.OnClickListener onClick) {
        // Button 是 TextView，塞不进子视图，所以图标按钮直接做成可点的 ImageView：
        // 图标居中、背景自己画，提示走 contentDescription。
        android.widget.ImageView button = new android.widget.ImageView(this);
        button.setImageDrawable(Icons.of(kind, dp(22), COLOR_TEXT, dp(1.8f)));
        button.setScaleType(android.widget.ImageView.ScaleType.CENTER_INSIDE);
        button.setPadding(dp(12), dp(9), dp(12), dp(9));
        button.setContentDescription(description);
        button.setBackground(rounded(COLOR_SURFACE_ALT, 0, 0, dp(10)));
        button.setClickable(true);
        button.setOnClickListener(onClick);
        return button;
    }

    /** 观看状态：未看 = 空心圈，未看完 = 半圆，已看 = 实心勾圈。 */
    private android.widget.ImageView watchIcon(Models.LibraryItem item) {
        Icons.Kind kind;
        int color;
        int progress = item.progressPercent();
        if (item.isWatched || progress >= 95) {
            kind = Icons.Kind.WATCH_DONE;
            color = COLOR_FOCUS;
        } else if (progress > 0) {
            kind = Icons.Kind.WATCH_PART;
            color = COLOR_MUTED;
        } else {
            kind = Icons.Kind.WATCH_NONE;
            color = COLOR_MUTED;
        }
        android.widget.ImageView icon = new android.widget.ImageView(this);
        icon.setImageDrawable(Icons.of(kind, dp(15), color, dp(1.6f)));
        icon.setContentDescription(item.isWatched ? "已看" : progress > 0 ? "未看完" : "未看");
        return icon;
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
