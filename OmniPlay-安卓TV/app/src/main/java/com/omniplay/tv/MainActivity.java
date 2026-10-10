package com.omniplay.tv;

import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Typeface;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.LayerDrawable;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.text.Html;
import android.text.Editable;
import android.text.InputType;
import android.text.SpannableStringBuilder;
import android.text.Spanned;
import android.text.TextUtils;
import android.text.TextWatcher;
import android.text.style.ForegroundColorSpan;
import android.text.style.RelativeSizeSpan;
import android.view.Display;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.view.ViewParent;
import android.view.ViewConfiguration;
import android.view.Window;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.ArrayAdapter;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.GridLayout;
import android.widget.ImageButton;
import android.widget.ImageView;
import android.widget.HorizontalScrollView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.TextView;

import android.os.Environment;

import com.omniplay.tv.data.Models;
import com.omniplay.tv.data.LocalPlayApi;
import com.omniplay.tv.iso.RemoteIsoStreamServer;
import com.omniplay.tv.local.LanDiscovery;
import com.omniplay.tv.local.LocalEngine;
import com.omniplay.tv.local.PhoneInputServer;
import com.omniplay.tv.local.QrCode;
import com.omniplay.tv.local.BrowseEntry;
import com.omniplay.tv.local.SavedConnections;
import com.omniplay.tv.local.CrashLog;
import com.omniplay.tv.local.SmbClient;
import com.omniplay.tv.local.SmbStreamServer;
import com.omniplay.tv.local.TvSettings;
import com.omniplay.tv.local.TmdbClient;
import com.omniplay.tv.local.UrlLabels;
import com.omniplay.tv.local.WebDavClient;
import com.omniplay.tv.player.MpvVideoView;
import com.omniplay.tv.subtitle.PgsSubtitleCue;
import com.omniplay.tv.subtitle.PgsSubtitleParser;
import com.omniplay.tv.subtitle.PgsSubtitleView;
import com.omniplay.tv.ui.ImageLoader;
import com.omniplay.tv.ui.FocusGrid;
import com.omniplay.tv.ui.KindIcons;
import com.omniplay.tv.ui.Overlays;
import com.omniplay.tv.ui.Theme;
import com.omniplay.tv.ui.Widgets;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.BufferedInputStream;
import java.io.BufferedReader;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.net.HttpURLConnection;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.InterfaceAddress;
import java.net.NetworkInterface;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.text.Collator;
import java.text.Normalizer;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.Comparator;
import java.util.Enumeration;
import java.util.HashSet;
import java.util.HashMap;
import java.util.LinkedHashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ExecutorCompletionService;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.TimeUnit;
import java.util.function.Supplier;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

public final class MainActivity extends Activity {
    private static final String UI_PREFS = "omniplay_tv_ui";
    private static final String KEY_4K_DV_HDR_PLAYBACK_MODE = "4k_dv_hdr_playback_mode";
    private static final String PLAYBACK_MODE_COMPATIBLE_SUBTITLE = "compatible_subtitle";
    private static final String PLAYBACK_MODE_HIGH_PERFORMANCE_DIRECT = "high_performance_direct";
    // The palette lives in ui/Theme; these aliases exist only so older call sites keep reading.
    // Snapshots of the applied variant, refreshed by {@link #syncThemeColors()}: the screens use
    // these names, so switching themes never needs a rebuild of the call sites.
    private static int COLOR_SURFACE = Theme.SURFACE;
    private static int COLOR_SURFACE_ALT = Theme.SURFACE_ALT;
    private static int COLOR_BORDER = Theme.BORDER;
    private static int COLOR_TEXT = Theme.TEXT;
    private static int COLOR_MUTED = Theme.MUTED;
    private static int COLOR_SUBTLE = Theme.SUBTLE;
    private static int COLOR_FOCUS = Theme.FOCUS;
    private static int COLOR_ACCENT = Theme.ACCENT;
    private static int COLOR_FOCUS_SOFT = Theme.FOCUS_SOFT;
    private static int COLOR_DANGER = Theme.DANGER;
    private static int COLOR_PLAYER_TEXT = Theme.PLAYER_TEXT;
    private static int COLOR_PLAYER_MUTED = Theme.PLAYER_MUTED;
    private static final int UNKNOWN_MOVIE_PART_INDEX = Integer.MAX_VALUE;
    private static final int PLAYER_SEEK_REPEAT_MS = 80;
    private static final double PLAYER_SEEK_STEP_SECONDS = 0.8d;
    private static int FOCUS_STROKE_PX = Theme.STROKE;

    /** Re-reads the applied variant into the aliases above; call it after Theme.apply(). */
    private void syncThemeColors() {
        COLOR_SURFACE = Theme.SURFACE;
        COLOR_SURFACE_ALT = Theme.SURFACE_ALT;
        COLOR_BORDER = Theme.BORDER;
        COLOR_TEXT = Theme.TEXT;
        COLOR_MUTED = Theme.MUTED;
        COLOR_SUBTLE = Theme.SUBTLE;
        COLOR_FOCUS = Theme.FOCUS;
        COLOR_ACCENT = Theme.ACCENT;
        COLOR_FOCUS_SOFT = Theme.FOCUS_SOFT;
        COLOR_DANGER = Theme.DANGER;
        COLOR_PLAYER_TEXT = Theme.PLAYER_TEXT;
        COLOR_PLAYER_MUTED = Theme.PLAYER_MUTED;
        FOCUS_STROKE_PX = Theme.STROKE;
    }
    private static final int HOME_PAGE_HORIZONTAL_PADDING_DP = 44;
    private static final int HOME_POSTER_COLUMNS = 6;
    private static final int HOME_POSTER_CARD_MARGIN_DP = 4;
    private static final int HOME_POSTER_CARD_PADDING_DP = 8;
    private static final int HOME_POSTER_ROW_BOTTOM_DP = 18;
    private static final int DETAIL_POSTER_WIDTH_DP = 230;
    private static final int DETAIL_POSTER_HEIGHT_DP = 342;
    private static final int EPISODE_CARD_WIDTH_DP = 286;
    private static final int EPISODE_STILL_HEIGHT_DP = 152;
    private static final Pattern SUBTITLE_TIME_LINE = Pattern.compile(
            "^\\s*((?:\\d{1,2}:)?\\d{1,2}:\\d{2}[,.]\\d{1,3})\\s*-->\\s*((?:\\d{1,2}:)?\\d{1,2}:\\d{2}[,.]\\d{1,3})(?:\\s+.*)?$");
    private static final String[] MOVIE_PART_PREFIXES = {"volume", "part", "disc", "disk", "dvd", "vol", "pt", "cd"};

    private final Handler main = new Handler(Looper.getMainLooper());
    private final ExecutorService io = Executors.newFixedThreadPool(4);
    private final Collator titleCollator = Collator.getInstance(Locale.CHINA);
    private FrameLayout root;
    private LocalPlayApi api;
    private ImageLoader imageLoader;
    private Theme theme;
    private Widgets widgets;
    private FocusGrid focusGrid;
    private Overlays overlays;
    private SharedPreferences uiPreferences;
    private List<Models.LibraryItem> libraryItems = Collections.emptyList();
    private final Map<String, Models.LibraryDetail> detailCache = new ConcurrentHashMap<>();
    private final Map<String, Models.SubtitleCacheStatus> subtitleCacheStatusCache = new ConcurrentHashMap<>();
    private final Map<String, PlaybackTimeline> localPlaybackProgress = new ConcurrentHashMap<>();
    private final Set<String> pendingDetailPrefetches = Collections.newSetFromMap(new ConcurrentHashMap<>());
    private Models.LibraryDetail currentDetail;
    private MpvVideoView playerView;
    private ScrollView homeScrollView;
    private Overlays.Overlay settingsOverlay;
    private View settingsRestoreFocus;
    private View sourcesRestoreFocus;
    private Models.VideoFile activePlaybackFile;
    private List<Models.VideoFile> activePlaybackQueue = Collections.emptyList();
    private int activePlaybackIndex;
    private String activePlaybackTicket;
    private String activeHlsSessionId;
    private List<Models.SubtitleTrack> activeExternalSubtitles = Collections.emptyList();
    private List<RuntimeTrack> activeRuntimeAudioTracks = Collections.emptyList();
    private List<RuntimeTrack> activeRuntimeSubtitleTracks = Collections.emptyList();
    private List<SubtitleCue> activeSubtitleCues = Collections.emptyList();
    private List<PgsSubtitleCue> activePgsSubtitleCues = Collections.emptyList();
    private View playerMenuPanel;
    private View playerTopOverlay;
    private View playerControlsPanel;
    private TextView playerCurrentTime;
    private TextView playerTotalTime;
    private TextView playerStatusText;
    private TextView playerSubtitleOverlay;
    private PgsSubtitleView playerPgsSubtitleOverlay;
    private ImageButton playerPlayPauseButton;
    private ImageButton playerAudioButton;
    private ImageButton playerSubtitleButton;
    private View playerProgressFill;
    private FrameLayout playerProgressTrack;
    private final List<View> playerProgressSegmentViews = new ArrayList<>();
    private Runnable playerUiUpdater;
    private Runnable progressReporter;
    private Runnable playerChromeHider;
    private Runnable subtitleOverlayUpdater;
    private Runnable playerSeekStarter;
    private Runnable playerSeekRepeater;
    private long playerLoadStartedAtMs;
    private double playerSeekPreviewPosition = -1;
    private Runnable pendingSearchRender;
    /** 整面海报墙的重建请求：刮削期间条目数一直变，合并成最多两秒一次。 */
    private Runnable pendingWallRebuild;
    /** 首页横幅当前显示的句子，以及已经被自动收起的那句。 */
    private String homeStatusText = "";
    private String homeStatusHiddenMessage = "";
    /** 引擎进度回调的节流时间戳。 */
    private volatile long lastProgressPaintAt;
    private int homeScrollY;
    // Persistent home chrome: rebuilt once, so sort / search toggles never tear the page down.
    private LinearLayout homeControls;
    private LinearLayout homeBody;
    private Button homeSearchToggle;
    private Button homeSortToggle;
    private Button homeDirectionToggle;
    private Button homeScanToggle;
    private Button homeSourcesToggle;
    private Button homeSettingsToggle;
    private EditText homeSearchInput;
    private boolean advancingPlaybackPart;
    private Screen screen = Screen.SETUP;
    private boolean isSearchOpen;
    private boolean isSortOpen;
    private boolean isSettingsOpen;
    private final Map<String, Button> kindToggleButtons = new LinkedHashMap<>();
    private boolean isSourcesOpen;
    private Overlays.Overlay sourcesOverlay;
    private boolean isPhoneInputOpen;
    private Overlays.Overlay phoneInputOverlay;
    private Overlays.Overlay textInputOverlay;
    private TextView phoneInputStatus;
    private TextView phoneInputUrl;
    private ImageView phoneInputQr;
    private Button phoneInputCloseButton;
    private Button setupPhoneInputBtn;
    private PhoneInputServer phoneInput;
    private TextView homeStatusView;
    /** 在屏海报卡片，按条目 id 索引；刮削过程中就地刷新用。 */
    private final Map<String, List<PosterViews>> homePosterViews = new HashMap<>();
    private boolean libraryPatchInFlight;
    private long lastLibraryPatchAt;
    private boolean startupSyncStarted;
    private final Set<String> episodeMetaAttempts = ConcurrentHashMap.newKeySet();

    // ---- setup screen media-source flows (LAN scan / network login / folder browser) ----
    private Button setupLocalBtn, setupWebDavBtn, setupSmbBtn, setupEnterBtn, setupRescanBtn, setupScrapeBtn, setupRescanLanBtn;
    private Button setupPickLocalBtn, setupLoginBtn;
    private EditText setupAddressInput;
    private TextView setupStatusView, sourceSummaryView;
    private LinearLayout lanListContainer;
    private TextView lanStatus;
    private final List<View> lanDeviceButtons = new ArrayList<>();
    private final Object lanLock = new Object();
    private boolean lanDiscovering;
    private final List<LanDiscovery.LanDevice> lanDevices = new ArrayList<>();
    /** 海报下的观看状态：○ 未看、◐ 未看完、✓ 已看，只有图形没有文字。 */
    private static final String WATCHED_GLYPH_NONE = "\u25CB";
    private static final String WATCHED_GLYPH_PART = "\u25D0";
    private static final String WATCHED_GLYPH_DONE = "\u2713";
    private int lanScanToken = 0;
    /** 扫描结果往哪儿摆：首屏设置页，还是媒体源弹窗。 */
    private static final int LAN_SETUP = 0;
    private static final int LAN_SOURCES = 1;
    private int lanScanTarget = LAN_SETUP;
    /** 扫描 + 刮削同一时间只跑一轮，免得两拨进度互相盖掉。 */
    private boolean scanRunning;
    private boolean isNetworkLoginOpen;
    private boolean isFolderBrowserOpen;
    private Overlays.Overlay loginOverlay;
    private Overlays.Overlay browserOverlay;
    private final String[] selectedKind = {"webdav"};
    private String loginDialogKind = "webdav";
    private boolean loginRunning;
    private String loginDialogLabel = "";
    private String browserKind;
    private String browserRoot;
    private String browserCurrent;
    private String browserUser = "";
    /** 这次挂载要用的显示名称：登录小窗里填的（或设备名）。 */
    private String browserLabel = "";
    private String browserPass = "";
    private final List<BrowseEntry> browserItems = new ArrayList<>();
    private final List<Button> browserItemButtons = new ArrayList<>();
    private LinearLayout savedConnectionContainer;
    private final List<View> savedConnectionButtons = new ArrayList<>();
    private SavedConnections savedConnections;
    private LinearLayout browserListContainer;
    private TextView browserPathText, browserMessage;
    private Button browserBackButton, browserMountButton, browserCancelButton;
    private EditText loginName, loginAddress, loginUser, loginPass;
    private TextView loginStatus;
    private Button loginButton, loginCancelButton;
    // One task per host+port during the LAN scan, so the pool needs to be wide.
    private final ExecutorService lanProbeExecutor = Executors.newFixedThreadPool(96);
    private boolean showEpisodeDetails = true;
    private boolean highPerformanceDirectPlayback;
    private boolean activeDirectPlayback;
    private boolean playerChromeHidden;
    private boolean playerPaused;
    private boolean playerHorizontalKeyDown;
    private boolean playerHorizontalKeyWasChromeHidden;
    private boolean playerSeekLongPressActive;
    private int playerHorizontalKeyCode;
    private int playerSeekDirection;
    private int selectedAudioTrackOrdinal = -1;
    private int selectedEmbeddedSubtitleOrdinal = -1;
    private String selectedRuntimeAudioId;
    private String selectedRuntimeSubtitleId;
    private String selectedExternalSubtitleId;
    private int pendingEmbeddedSubtitleOrdinal = -1;
    private String pendingRuntimeSubtitleId;
    private String pendingExternalSubtitleId;
    private int subtitleLoadGeneration;
    private long subtitleLoadStartedAtMs;
    private double lastKnownPlaybackPositionSeconds;
    private double lastKnownPlaybackDurationSeconds;
    private String searchText = "";
    private SortKey sortKey = SortKey.YEAR;
    private boolean sortDescending = true;
    private String selectedSeasonId = "";
    private String requestedDetailItemId = "";

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        getWindow().setFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN, WindowManager.LayoutParams.FLAG_FULLSCREEN);
        preferHighestResolutionDisplayMode();
        // A TV has no console: record whatever kills a worker thread so the next launch can say why.
        CrashLog.install(this);
        // The palette has to be chosen before a single view is built.
        Theme.apply(new TvSettings(this).themeId());

        api = new LocalPlayApi(this);
        savedConnections = new SavedConnections(this);
        imageLoader = new ImageLoader(this);
        theme = new Theme(this);
        syncThemeColors();
        widgets = new Widgets(theme);
        focusGrid = new FocusGrid();
        uiPreferences = getApplicationContext().getSharedPreferences(UI_PREFS, MODE_PRIVATE);
        showEpisodeDetails = api.engine().settings().showEpisodeDetails();
        highPerformanceDirectPlayback = PLAYBACK_MODE_HIGH_PERFORMANCE_DIRECT.equals(uiPreferences.getString(
                KEY_4K_DV_HDR_PLAYBACK_MODE,
                PLAYBACK_MODE_COMPATIBLE_SUBTITLE));
        root = new FrameLayout(this);
        root.setClipChildren(false);
        root.setClipToPadding(false);
        overlays = new Overlays(root, theme, widgets, focusGrid);
        applyAppBackground();
        setContentView(root);

        // 独立版没有服务端要问，也没有第二个「首次运行页」：海报墙永远是首屏。
        // 一个媒体源都没有时，墙上自带「添加媒体源」的入口（以前这里会甩一整屏设置页）。
        showHome();
        String crash = CrashLog.take(this);
        if (!crash.isEmpty()) {
            showToast("上次异常退出：" + crash.replace('\n', ' ').trim());
        }
    }

    private void preferHighestResolutionDisplayMode() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M) {
            return;
        }

        Display display = getWindowManager().getDefaultDisplay();
        if (display == null) {
            return;
        }

        Display.Mode current = display.getMode();
        Display.Mode best = current;
        for (Display.Mode mode : display.getSupportedModes()) {
            if (mode == null) {
                continue;
            }
            if (best == null || displayModeRank(mode) > displayModeRank(best)) {
                best = mode;
            }
        }

        if (best != null && current != null && best.getModeId() != current.getModeId()) {
            WindowManager.LayoutParams params = getWindow().getAttributes();
            params.preferredDisplayModeId = best.getModeId();
            getWindow().setAttributes(params);
        }
    }

    private long displayModeRank(Display.Mode mode) {
        long pixels = (long) mode.getPhysicalWidth() * (long) mode.getPhysicalHeight();
        long refresh = Math.round(mode.getRefreshRate());
        return pixels * 1000L + refresh;
    }

    @Override
    protected void onStop() {
        if (screen == Screen.PLAYER && playerView != null) {
            closePlayer();
        }
        super.onStop();
    }

    private void showSetup(String message) {
        screen = Screen.SETUP;
        currentDetail = null;
        destroyPlayer();
        applyImmersiveMode(true);
        closeAllOverlays();
        root.removeAllViews();
        applyAppBackground();

        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setClipChildren(false);
        scroll.setClipToPadding(false);
        scroll.setFocusable(false);
        scroll.setFocusableInTouchMode(false);
        root.addView(scroll, match());

        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(sdp(64), sdp(44), sdp(64), sdp(36));
        page.setClipChildren(false);
        page.setClipToPadding(false);
        scroll.addView(page, matchWidth());

        page.addView(title("OmniPlay TV"));
        page.addView(body("独立版：添加本地目录、WebDAV 或 SMB 媒体源，自动扫描并用 TMDB 刮削海报。"));

        sourceSummaryView = text(api.engine().sources().isEmpty()
                ? "当前没有媒体源。"
                : "当前媒体源：" + api.engine().sources().size() + " 个。", 14, Typeface.BOLD, COLOR_SUBTLE);
        page.addView(sourceSummaryView, margin(matchWidth(), 0, sdp(10), 0, 0));

        if (message != null && !message.isEmpty()) {
            TextView error = body(message);
            error.setTextColor(COLOR_DANGER);
            page.addView(error);
        }

        // Anything that killed the app since the last visit is reported once, here.
        String crash = CrashLog.take(this);
        if (!crash.isEmpty()) {
            TextView reported = body("上次异常退出：" + crash.replace('\n', ' ').trim());
            reported.setTextColor(COLOR_DANGER);
            page.addView(reported);
        }

        page.addView(text("媒体源", 15, Typeface.BOLD, COLOR_MUTED), margin(matchWidth(), 0, 0, 0, sdp(8)));
        final TextView pathHint = text(kindHint(selectedKind[0]), 12, Typeface.NORMAL, COLOR_SUBTLE);
        LinearLayout kindRow = new LinearLayout(this);
        kindRow.setGravity(Gravity.CENTER_VERTICAL);
        kindRow.setClipChildren(false);
        kindRow.setClipToPadding(false);
        page.addView(kindRow, margin(matchWidth(), 0, sdp(4), 0, 0));
        kindToggleButtons.clear();
        setupLocalBtn = kindToggleButton("local", "本地目录", selectedKind, pathHint);
        setupWebDavBtn = kindToggleButton("webdav", "WebDAV", selectedKind, pathHint);
        setupSmbBtn = kindToggleButton("smb", "SMB", selectedKind, pathHint);
        // WebDAV first, then SMB, then the local folder: the two network kinds are what a TV needs most.
        kindRow.addView(setupWebDavBtn, new LinearLayout.LayoutParams(0, sdp(58), 1f));
        kindRow.addView(setupSmbBtn, margin(new LinearLayout.LayoutParams(0, sdp(58), 1f), sdp(10), 0, 0, 0));
        kindRow.addView(setupLocalBtn, margin(new LinearLayout.LayoutParams(0, sdp(58), 1f), sdp(10), 0, 0, 0));
        pathHint.setLineSpacing(sdp(2), 1f);
        page.addView(pathHint, margin(matchWidth(), 0, sdp(8), 0, sdp(6)));

        // Connections that already logged in once: one key press reconnects them.
        savedConnectionContainer = new LinearLayout(this);
        savedConnectionContainer.setOrientation(LinearLayout.VERTICAL);
        savedConnectionContainer.setClipChildren(false);
        savedConnectionContainer.setClipToPadding(false);
        savedConnectionContainer.setVisibility(View.GONE);
        page.addView(savedConnectionContainer, margin(matchWidth(), 0, sdp(2), 0, sdp(14)));

        setupPickLocalBtn = button("选择本地目录");
        LinearLayout.LayoutParams pickParams = matchWidth();
        pickParams.height = sdp(60);
        setupPickLocalBtn.setOnClickListener(view -> openLocalFolderPicker());
        page.addView(setupPickLocalBtn, margin(pickParams, 0, 0, 0, sdp(10)));

        setupAddressInput = input("WebDAV 地址，例如 https://192.168.0.10:5006 或 https://nas:5006/webdav", "");
        page.addView(setupAddressInput, margin(matchWidth(), 0, 0, 0, sdp(10)));
        setupLoginBtn = button("登录");
        LinearLayout.LayoutParams loginParams = matchWidth();
        loginParams.height = sdp(60);
        setupLoginBtn.setOnClickListener(view -> openLoginDialog(
                selectedKind[0],
                setupAddressInput.getText().toString().trim(),
                "", "", ""));
        page.addView(setupLoginBtn, margin(loginParams, 0, 0, 0, sdp(24)));
        setupPhoneInputBtn = button("手机输入");
        LinearLayout.LayoutParams phoneParams = matchWidth();
        phoneParams.height = sdp(56);
        setupPhoneInputBtn.setOnClickListener(view -> openPhoneInputOverlay("填写媒体源地址",
                "填好后回到电视点“登录”。",
                Collections.singletonList(boundField("address", "媒体源地址", () -> setupAddressInput, false)),
                true));
        page.addView(setupPhoneInputBtn, margin(phoneParams, 0, 0, 0, sdp(24)));

        LinearLayout lanHeader = new LinearLayout(this);
        lanHeader.setGravity(Gravity.CENTER_VERTICAL);
        lanHeader.setClipChildren(false);
        lanHeader.setClipToPadding(false);
        page.addView(lanHeader, margin(matchWidth(), 0, 0, 0, sdp(8)));
        lanHeader.addView(text("局域网设备（自动扫描）", 15, Typeface.BOLD, COLOR_MUTED),
                new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        setupRescanLanBtn = compactButton("重新扫描局域网");
        setupRescanLanBtn.setOnClickListener(view -> startLanScan());
        lanHeader.addView(setupRescanLanBtn, new LinearLayout.LayoutParams(sdp(150), sdp(Widgets.HEIGHT_COMPACT)));
        lanStatus = text("正在扫描局域网...", 12, Typeface.NORMAL, COLOR_SUBTLE);
        page.addView(lanStatus, margin(matchWidth(), 0, sdp(4), 0, sdp(8)));
        lanListContainer = new LinearLayout(this);
        lanListContainer.setOrientation(LinearLayout.VERTICAL);
        lanListContainer.setClipChildren(false);
        lanListContainer.setClipToPadding(false);
        page.addView(lanListContainer, margin(matchWidth(), 0, 0, 0, sdp(24)));

        LinearLayout actions = new LinearLayout(this);
        actions.setGravity(Gravity.CENTER_VERTICAL);
        actions.setClipChildren(false);
        actions.setClipToPadding(false);
        page.addView(actions, margin(matchWidth(), 0, sdp(6), 0, 0));
        setupEnterBtn = button("进入");
        setupEnterBtn.setOnClickListener(view -> {
            if (api.hasServerUrl()) {
                showHome();
            } else {
                setupStatusView.setText("请先添加媒体源。");
            }
        });
        actions.addView(setupEnterBtn, new LinearLayout.LayoutParams(0, sdp(56), 1f));
        setupRescanBtn = button("重新扫描");
        setupRescanBtn.setOnClickListener(view -> {
            showLoading("正在扫描媒体源");
            runAsync(
                    () -> api.engine().scanAll(status -> main.post(() -> {
                        if (screen == Screen.SETUP) {
                            setupStatusView.setText(status);
                        }
                    })),
                    report -> {
                        if (screen != Screen.SETUP) {
                            return;
                        }
                        setupStatusView.setText(report.message + " 待刮削 " + api.engine().scrapeQueueCount() + " 个条目。");
                        sourceSummaryView.setText("当前媒体源：" + api.engine().sources().size() + " 个。");
                        if (report.success) {
                            showHome();
                        }
                    },
                    error -> setupStatusView.setText("扫描失败：" + error.getMessage()));
        });
        actions.addView(setupRescanBtn, margin(new LinearLayout.LayoutParams(0, sdp(56), 1f), sdp(12), 0, 0, 0));
        setupScrapeBtn = button("刮削海报");
        setupScrapeBtn.setOnClickListener(view -> {
            if (!api.engine().tmdb().isConfigured()) {
                setupStatusView.setText(api.engine().tmdbStatusMessage());
                return;
            }
            setupStatusView.setText("正在刮削海报...");
            runAsync(
                    () -> {
                        api.engine().scrapePending(status -> main.post(() -> {
                            if (screen == Screen.SETUP) {
                                setupStatusView.setText(status);
                            }
                        }));
                        return null;
                    },
                    ignored -> {
                        if (screen == Screen.SETUP) {
                            showHome();
                        }
                    },
                    error -> setupStatusView.setText("刮削失败：" + error.getMessage()));
        });
        actions.addView(setupScrapeBtn, margin(new LinearLayout.LayoutParams(0, sdp(56), 1f), sdp(12), 0, 0, 0));

        setupStatusView = text("", 13, Typeface.BOLD, COLOR_SUBTLE);
        page.addView(setupStatusView, margin(matchWidth(), 0, sdp(12), 0, 0));

        updateSetupKindUi();
        renderSavedConnections();
        rebuildSetupDpadGrid();
        startLanScan();
        // Load the SMB library now: if it cannot start on this device, say so here rather than
        // waiting for the user to press SMB and be thrown back to the launcher.
        runAsync(SmbClient::warmUp,
                library -> {
                },
                error -> {
                    if (setupStatusView != null) {
                        setupStatusView.setText("SMB 组件不可用：" + friendlyLoginError(error));
                    }
                });
        View initial = api.hasServerUrl() ? setupEnterBtn
                : (!savedConnectionButtons.isEmpty() ? savedConnectionButtons.get(0) : setupWebDavBtn);
        initial.requestFocus();
    }

    /**
     * Declares the D-pad layout of the current screen: rows top-to-bottom, each row
     * left-to-right. The next-focus links, the arrow routing and the scroll-following all
     * live in {@link FocusGrid}, which also keeps a stack of grids so an overlay can hand
     * navigation back to whatever opened it.
     */
    private void applyDpadRows(List<List<View>> rows) {
        focusGrid.apply(rows);
    }

    /** Moves D-pad focus across the active grid. */
    private boolean handleDpadChain(KeyEvent event) {
        return focusGrid.handleKey(event, getCurrentFocus());
    }

    private Button kindToggleButton(String kind, String label, final String[] selectedKind, final TextView hint) {
        boolean isSelected = kind.equals(selectedKind[0]);
        Button button = new Button(this);
        button.setTextSize(ssp(16));
        button.setAllCaps(false);
        button.setIncludeFontPadding(false);
        button.setGravity(Gravity.CENTER);
        button.setMinWidth(0);
        button.setMinHeight(0);
        button.setMinimumWidth(0);
        button.setMinimumHeight(0);
        button.setBackgroundTintList(null);
        button.setStateListAnimator(null);
        button.setClipToOutline(true);
        button.setFocusable(true);
        button.setFocusableInTouchMode(false);
        applyKindToggleStyle(button, label, isSelected);
        button.setOnFocusChangeListener((focusedView, hasFocus) -> {
            animateFocusScale(focusedView, hasFocus);
            boolean selected = kind.equals(selectedKind[0]);
            button.setTextColor(hasFocus ? COLOR_FOCUS : (selected ? COLOR_TEXT : COLOR_MUTED));
            int restFill = selected ? COLOR_FOCUS_SOFT : COLOR_SURFACE;
            focusedView.setBackground(rounded(
                    hasFocus ? Theme.darker(restFill, Theme.FOCUS_DARKEN) : restFill,
                    hasFocus || selected ? COLOR_FOCUS : COLOR_BORDER,
                    dp(1), dp(10)));
            focusedView.setElevation(hasFocus ? dp(6) : 0);
        });
        button.setOnClickListener(view -> {
            selectedKind[0] = kind;
            for (Map.Entry<String, Button> entry : kindToggleButtons.entrySet()) {
                applyKindToggleStyle(entry.getValue(), kindLabel(entry.getKey()),
                        entry.getKey().equals(selectedKind[0]));
            }
            hint.setText(kindHint(selectedKind[0]));
            updateSetupKindUi();
            rebuildSetupDpadGrid();
            button.requestFocus();
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
            return "SMB：可从下方局域网扫描结果中点选，或手动填写 smb://主机 形式的地址后点击“登录”，再选择要挂载的共享目录。";
        }
        if (kind.equals("webdav")) {
            return "WebDAV：可从下方局域网扫描结果中点选，或手动填写 https://nas:5006 形式的地址后点击“登录”，再选择要挂载的目录。";
        }
        return "本地目录：点击“选择本地目录”，在文件夹管理器中浏览并选择影片目录。";
    }

    /** 选中的类型不加对号：整颗按钮染上主题色，比旁边几颗深一档就是「当前这一颗」。 */
    private void applyKindToggleStyle(Button button, String label, boolean selected) {
        button.setText(label);
        button.setTextColor(selected ? COLOR_TEXT : COLOR_MUTED);
        button.setBackground(rounded(
                selected ? COLOR_FOCUS_SOFT : COLOR_SURFACE,
                selected ? COLOR_FOCUS : COLOR_BORDER,
                dp(1), dp(10)));
    }


    // ---- setup screen helpers: immersive, LAN scan, focus grid, overlays ----

    private void applyImmersiveMode(boolean immersive) {
        try {
            if (Build.VERSION.SDK_INT >= 30) {
                WindowInsetsController controller = getWindow().getInsetsController();
                if (controller == null) {
                    return;
                }
                int type = WindowInsets.Type.statusBars() | WindowInsets.Type.navigationBars();
                if (immersive) {
                    controller.hide(type);
                    controller.setSystemBarsBehavior(
                            WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
                } else {
                    controller.show(type);
                    controller.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_DEFAULT);
                }
            } else {
                int flags = immersive
                        ? (View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                                | View.SYSTEM_UI_FLAG_FULLSCREEN
                                | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION)
                        : View.SYSTEM_UI_FLAG_VISIBLE;
                getWindow().getDecorView().setSystemUiVisibility(flags);
            }
        } catch (Exception ignored) {
        }
    }

    /** Toggles the kind-specific action block (local folder picker vs address + login). */
    private void updateSetupKindUi() {
        boolean local = selectedKind[0].equals("local");
        setupPickLocalBtn.setVisibility(local ? View.VISIBLE : View.GONE);
        setupAddressInput.setVisibility(local ? View.GONE : View.VISIBLE);
        setupLoginBtn.setVisibility(local ? View.GONE : View.VISIBLE);
        setupPhoneInputBtn.setVisibility(local ? View.GONE : View.VISIBLE);
        setupAddressInput.setHint(selectedKind[0].equals("smb")
                ? "SMB 地址，例如 smb://192.168.0.10 或 smb://192.168.0.10/video"
                : "WebDAV 地址，例如 https://192.168.0.10:5006 或 https://nas:5006/webdav");
        setupLoginBtn.setText(selectedKind[0].equals("smb") ? "SMB 登录" : "WebDAV 登录");
    }

    /** Rebuilds the D-pad grid for the setup screen from the currently visible controls. */
    private void rebuildSetupDpadGrid() {
        List<List<View>> rows = new ArrayList<>();
        rows.add(Arrays.asList(setupWebDavBtn, setupSmbBtn, setupLocalBtn));
        for (View button : savedConnectionButtons) {
            rows.add(Collections.singletonList(button));
        }
        if (selectedKind[0].equals("local")) {
            rows.add(Collections.singletonList(setupPickLocalBtn));
        } else {
            rows.add(Collections.singletonList(setupAddressInput));
            rows.add(Collections.singletonList(setupLoginBtn));
            rows.add(Collections.singletonList(setupPhoneInputBtn));
        }
        rows.add(Collections.singletonList(setupRescanLanBtn));
        for (View button : lanDeviceButtons) {
            rows.add(Collections.singletonList(button));
        }
        rows.add(Arrays.asList(setupEnterBtn, setupRescanBtn, setupScrapeBtn));
        applyDpadRows(rows);
    }

    private void startLanScan() {
        lanScanTarget = sourcesOverlay != null ? LAN_SOURCES : LAN_SETUP;
        synchronized (lanLock) {
            lanDiscovering = true;
            lanDevices.clear();
            lanDeviceButtons.clear();
        }
        if (lanListContainer != null) {
            lanListContainer.removeAllViews();
        }
        if (lanStatus != null) {
            lanStatus.setText("正在扫描局域网中的 WebDAV / SMB 设备...");
        }
        LanDiscovery.cancel(lanScanToken);
        final int[] tokenRef = new int[1];
        final int target = lanScanTarget;
        LanDiscovery.Listener listener = new LanDiscovery.Listener() {
            @Override
            public void onDevice(LanDiscovery.LanDevice device) {
                main.post(() -> {
                    if (lanScanToken != tokenRef[0] || !lanTargetAlive(target)) {
                        return;
                    }
                    addLanDeviceButton(device);
                });
            }

            @Override
            public void onDone(List<LanDiscovery.LanDevice> devices) {
                main.post(() -> {
                    // 计数必须归零，否则宿主换掉之后下一次扫描再也发不起来。
                    lanDiscovering = false;
                    if (lanScanToken != tokenRef[0] || !lanTargetAlive(target)) {
                        return;
                    }
                    if (lanStatus != null) {
                        lanStatus.setText(devices.isEmpty()
                                ? "未扫描到 WebDAV / SMB 设备，可手动填写地址后登录。"
                                : "扫描完成，发现 " + devices.size() + " 个设备，点选后登录。");
                    }
                    refreshLanFocusRows();
                });
            }
        };
        tokenRef[0] = LanDiscovery.start(this, lanProbeExecutor, listener);
        lanScanToken = tokenRef[0];
    }

    /** 扫描结果要有地方放：要么设置页还在，要么媒体源弹窗还开着。 */
    private boolean lanTargetAlive(int target) {
        if (target == LAN_SOURCES) {
            return sourcesOverlay != null && lanListContainer != null
                    && lanListContainer.isAttachedToWindow();
        }
        return screen == Screen.SETUP;
    }

    /** 媒体源弹窗接管局域网列表：先接上已在飞的扫描结果，没有就现扫一次。 */
    private void attachLanListContainer(LinearLayout container) {
        lanListContainer = container;
        boolean inFlight;
        synchronized (lanLock) {
            inFlight = lanDiscovering && lanTargetAlive(lanScanTarget);
            if (inFlight) {
                for (LanDiscovery.LanDevice device : lanDevices) {
                    container.addView(lanDeviceButton(device), listItemParams());
                }
            }
        }
        if (inFlight) {
            if (lanStatus != null) {
                lanStatus.setText("正在扫描局域网中的 WebDAV / SMB 设备...");
            }
            return;
        }
        startLanScan();
    }

    /** 新扫到的设备追加到列表尾部，并重排遥控器方向键顺序。 */
    private void addLanDeviceButton(LanDiscovery.LanDevice device) {
        synchronized (lanLock) {
            lanDevices.add(device);
        }
        if (lanListContainer == null) {
            return;
        }
        Button button = lanDeviceButton(device);
        lanListContainer.addView(button, listItemParams());
        lanDeviceButtons.add(button);
        refreshLanFocusRows();
    }

    private Button lanDeviceButton(LanDiscovery.LanDevice device) {
        // 第二行永远是探到的那条地址（WebDAV 存的是编码过的 URL），整行照 mac 版
        // 「图标 + 设备名 + 地址」的读法；保存过的账号只在点开登录框时回填，不占列表位置。
        Button button = widgets.listRow(device.label(), UrlLabels.displayPath(device.url),
                KindIcons.of(device.kind, sdp(22), COLOR_ACCENT, dp(1.5f)));
        button.setOnClickListener(view -> connectDiscoveredDevice(device));
        return button;
    }

    /** 局域网行是动态插进来的，插完要把当前面板的方向键顺序重新声明一次。 */
    private void refreshLanFocusRows() {
        if (lanScanTarget == LAN_SOURCES && sourcesOverlay != null) {
            sourcesOverlay.redeclare(FocusGrid.rowsFrom(sourcesOverlay.body));
            return;
        }
        rebuildSetupDpadGrid();
    }

    private LinearLayout.LayoutParams listItemParams() {
        return widgets.listRowParams();
    }

    /** A list row with a title and a quieter second line underneath it. */
    private Button listRowButton(String title, String detail) {
        return widgets.listRow(title, detail);
    }

    private static CharSequence stackedLabel(String title, String detail) {
        return Widgets.stacked(title, detail);
    }

    /** Renders the "已保存的连接" block; hidden while nothing has logged in successfully. */
    private void renderSavedConnections() {
        if (savedConnectionContainer == null || savedConnections == null) {
            return;
        }
        rememberExistingSources();
        List<SavedConnections.Entry> entries = savedConnections.all();
        savedConnectionContainer.removeAllViews();
        savedConnectionButtons.clear();
        if (entries.isEmpty()) {
            savedConnectionContainer.setVisibility(View.GONE);
            return;
        }
        savedConnectionContainer.setVisibility(View.VISIBLE);
        savedConnectionContainer.addView(text("已保存的连接（点选直接连接）", 15, Typeface.BOLD, COLOR_MUTED),
                margin(matchWidth(), 0, 0, 0, sdp(6)));
        for (SavedConnections.Entry entry : entries) {
            Button button = listRowButton(entry.title(), entry.detail());
            button.setOnClickListener(view -> connectSavedConnection(entry));
            savedConnectionContainer.addView(button, listItemParams());
            savedConnectionButtons.add(button);
        }
    }

    /** Connections saved by earlier builds become one-tap rows the first time setup renders. */
    private void rememberExistingSources() {
        for (LocalEngine.Source source : api.engine().sources()) {
            if (!"webdav".equals(source.kind) && !"smb".equals(source.kind)) {
                continue;
            }
            if (savedConnections.find(source.kind, source.path) == null) {
                savedConnections.recordMount(source.kind, source.path,
                        source.name, source.username, source.password);
            }
        }
    }

    /** Reconnect a saved server; jump straight to the folder that was mounted last time. */
    private void connectSavedConnection(SavedConnections.Entry entry) {
        String path = entry.primaryPath();
        setupStatusView.setText("正在连接 " + entry.title() + "...");
        if (!path.isEmpty()) {
            mountMediaSource(entry.kind, path, entry.username, entry.password, entry.label);
            return;
        }
        openLoginDialog(entry.kind, entry.hostKey, entry.username, entry.password, entry.label);
    }

    /**
     * 局域网那一行点的是「去登录」，不是直接挂载：mac 版同样是先开登录表单（回填已存账号），
     * 登录通过后再挑要挂载的目录。以前扫到的设备一按就挂上上次那个目录，用户根本没机会换目录。
     */
    private void connectDiscoveredDevice(LanDiscovery.LanDevice device) {
        SavedConnections.Entry saved = savedConnections == null
                ? null : savedConnections.find(device.kind, device.url);
        openLoginDialog(device.kind, device.url,
                saved == null ? "" : saved.username,
                saved == null ? "" : saved.password,
                device.label());
    }

    // ---- network login dialog ----

    private void openLoginDialog(String kind, String address, String username, String password,
                                 String deviceTitle) {
        if (loginOverlay != null) {
            return;
        }
        isNetworkLoginOpen = true;
        loginDialogKind = kind;
        loginDialogLabel = deviceTitle == null ? "" : deviceTitle;

        String kindLabel = "smb".equals(kind) ? "SMB" : "WebDAV";
        SavedConnections.Entry remembered = savedConnections == null
                ? null : savedConnections.find(kind, address);
        String initialUser = username == null || username.isEmpty()
                ? (remembered == null ? "" : remembered.username) : username;
        String initialPass = password == null || password.isEmpty()
                ? (remembered == null ? "" : remembered.password) : password;
        String prefix = deviceTitle == null || deviceTitle.isEmpty() ? "" : deviceTitle + "：";

        Overlays.Options options = new Overlays.Options();
        options.title = "登录 " + kindLabel;
        options.subtitle = remembered == null
                ? prefix + "输入账号密码后登录，登录成功后再选择要挂载的目录。"
                : prefix + "已保存这台服务器的账号，直接点“登录”即可；需要换账号时再修改。";
        options.widthDp = 620;
        options.restoreFocus = getCurrentFocus();
        Overlays.Overlay overlay = overlays.open(options);
        overlay.onClose = () -> {
            if (loginOverlay == overlay) {
                loginOverlay = null;
                isNetworkLoginOpen = false;
            }
        };
        loginOverlay = overlay;
        LinearLayout panel = loginOverlay.body;

        loginName = input("显示名称（可选），例如「NAS 电影」", deviceTitle == null ? "" : deviceTitle);
        panel.addView(loginName, margin(matchWidth(), 0, 0, 0, sdp(4)));
        loginAddress = input("smb".equals(kind)
                ? "SMB 地址，例如 smb://192.168.0.10"
                : "WebDAV 地址，例如 https://192.168.0.10:5006", address);
        panel.addView(loginAddress, margin(matchWidth(), 0, 0, 0, sdp(4)));
        loginUser = input("用户名（匿名访问可留空）", initialUser);
        panel.addView(loginUser, margin(matchWidth(), 0, 0, 0, sdp(4)));
        loginPass = input("密码（匿名访问可留空）", initialPass);
        loginPass.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        panel.addView(loginPass, matchWidth());

        loginStatus = loginOverlay.statusLine();

        Button loginPhoneInput = button("手机输入");
        loginPhoneInput.setOnClickListener(view -> openPhoneInputOverlay(
                "登录 " + ("smb".equals(loginDialogKind) ? "SMB" : "WebDAV"),
                "只填需要改的项，留空的项不会被改动。",
                Arrays.asList(
                        boundField("address", "媒体源地址", () -> loginAddress, false),
                        boundField("username", "用户名（匿名可留空）", () -> loginUser, false),
                        boundField("password", "密码（匿名可留空）", () -> loginPass, true)),
                false));
        loginCancelButton = button("取消");
        loginCancelButton.setOnClickListener(view -> closeLoginDialog());
        loginButton = button("登录并选择目录");
        loginButton.setOnClickListener(view -> runLogin());
        overlays.footer(panel, loginCancelButton, loginPhoneInput, loginButton);

        loginOverlay.focus(Arrays.asList(
                Collections.singletonList(loginName),
                Collections.singletonList(loginAddress),
                Collections.singletonList(loginUser),
                Collections.singletonList(loginPass),
                Arrays.asList(loginCancelButton, loginPhoneInput, loginButton)), loginButton);
    }

    private void closeLoginDialog() {
        if (loginOverlay == null) {
            return;
        }
        Overlays.Overlay closing = loginOverlay;
        loginOverlay = null;
        isNetworkLoginOpen = false;
        loginRunning = false;
        loginName = null;
        loginAddress = null;
        loginUser = null;
        loginPass = null;
        loginStatus = null;
        loginButton = null;
        loginCancelButton = null;
        loginDialogLabel = "";
        closing.close();
    }

    private void runLogin() {
        if (loginRunning) {
            return;
        }
        String kind = loginDialogKind;
        String address = loginAddress.getText().toString().trim();
        if (address.isEmpty()) {
            loginStatus.setText("请填写地址。");
            return;
        }
        final String username = loginUser.getText().toString().trim();
        final String password = loginPass.getText().toString();
        String typedName = loginName == null ? "" : loginName.getText().toString().trim();
        if (!typedName.isEmpty()) {
            // 弹窗里写了名字就用它，媒体源管理里看到的就是这个名字。
            loginDialogLabel = typedName;
        }
        loginRunning = true;
        loginStatus.setText("正在登录...");
        runAsync(
                () -> {
                    if (kind.equals("smb")) {
                        SmbClient client = new SmbClient(address, username, password);
                        List<BrowseEntry> shares = client.listShares(address);
                        if (shares.isEmpty()) {
                            throw new IOException("连接成功，但该服务器下没有可见的共享。");
                        }
                        return SmbClient.normalize(address);
                    }
                    return probeWebDavRoot(address, new WebDavClient.Credential(username, password));
                },
                resolved -> {
                    loginRunning = false;
                    if (!isNetworkLoginOpen) {
                        return;
                    }
                    String resolvedRoot = (String) resolved;
                    browserLabel = loginDialogLabel;
                    if (savedConnections != null) {
                        // The credentials worked: remember them so the next time is one key press.
                        savedConnections.recordLogin(kind, resolvedRoot, loginDialogLabel, username, password);
                    }
                    closeLoginDialog();
                    openFolderBrowser(kind, resolvedRoot, resolvedRoot, username, password);
                    renderSavedConnections();
                },
                error -> {
                    loginRunning = false;
                    if (loginStatus != null && isNetworkLoginOpen) {
                        loginStatus.setText("登录失败：" + friendlyLoginError(error));
                    }
                });
    }

    /**
     * Probes candidate WebDAV roots (the entered url plus common path suffixes
     * when it is a bare authority) and returns the first that answers PROPFIND.
     */
    private String probeWebDavRoot(String address, WebDavClient.Credential credential) throws IOException {
        String base = WebDavClient.normalizeRoot(address);
        List<String> candidates = new ArrayList<>();
        candidates.add(base);
        String authority = base.replaceFirst("^https?://", "");
        if (!authority.contains("/")) {
            for (String path : new String[]{"/webdav", "/dav", "/remote.php/dav/files"}) {
                candidates.add(base + path);
            }
        }
        IOException last = null;
        for (String url : candidates) {
            try {
                new WebDavClient(url, credential).list(url);
                return url;
            } catch (IOException error) {
                last = error;
            }
        }
        throw last == null ? new IOException("无法连接 WebDAV 服务。") : last;
    }

    private static String friendlyLoginError(Throwable error) {
        String message = error.getMessage() == null || error.getMessage().isEmpty()
                ? CrashLog.summarize(error) : error.getMessage();
        // Linkage failures name a class, which is useless to a user; keep the reason short.
        if (error instanceof LinkageError || error instanceof VirtualMachineError) {
            return CrashLog.summarize(error);
        }
        if (message.contains("401") || message.contains("403")) {
            return "认证失败，请检查用户名和密码（或留空尝试匿名访问）。";
        }
        if (message.contains("404") || message.contains("405") || message.contains("501")) {
            return "该地址下没有可用的 WebDAV 入口，请检查地址（例如是否需要 /webdav 路径）。";
        }
        if (message.toLowerCase(Locale.ROOT).contains("unknownhost")) {
            return "无法解析该地址。";
        }
        if (message.toLowerCase(Locale.ROOT).contains("ssl")
                || message.toLowerCase(Locale.ROOT).contains("certificate")
                || message.toLowerCase(Locale.ROOT).contains("handshake")) {
            return "HTTPS 证书不可信，可改用 http 端口（例如 5005）重试。";
        }
        if (message.toUpperCase(Locale.ROOT).contains("LOGON_FAILURE")
                || message.toUpperCase(Locale.ROOT).contains("BAD_NETWORK_NAME")
                || message.toLowerCase(Locale.ROOT).contains("logon failure")
                || message.toLowerCase(Locale.ROOT).contains("bad password")
                || message.toLowerCase(Locale.ROOT).contains("access denied")) {
            return "认证失败，请检查用户名和密码（或留空尝试匿名访问）。";
        }
        if (message.contains("refused") || message.toLowerCase(Locale.ROOT).contains("timeout")
                || message.toLowerCase(Locale.ROOT).contains("timed out")) {
            return "无法连接到服务器，请检查网络。";
        }
        return message;
    }

    // ---- folder browser (local / webdav / smb share picker) ----

    private void openLocalFolderPicker() {
        browserLabel = "";
        File start = Environment.getExternalStorageDirectory();
        if (start == null || !start.isDirectory()) {
            File fallback = new File("/storage/emulated/0");
            start = fallback.isDirectory() ? fallback : new File(Environment.getDataDirectory(), "media");
        }
        openFolderBrowser("local", "/", start.getAbsolutePath(), "", "");
    }

    private void openFolderBrowser(String kind, String rootUrl, String current, String username, String password) {
        closeLoginDialog();
        if (browserOverlay != null) {
            return;
        }
        isFolderBrowserOpen = true;
        browserKind = kind;
        browserRoot = rootUrl;
        browserCurrent = current;
        browserUser = username == null ? "" : username;
        browserPass = password == null ? "" : password;
        browserItems.clear();
        browserItemButtons.clear();

        Overlays.Options options = new Overlays.Options();
        options.title = "选择媒体目录";
        options.widthDp = 720;
        options.scrollable = false;
        options.restoreFocus = screen == Screen.SETUP ? getCurrentFocus() : null;
        Overlays.Overlay overlay = overlays.open(options);
        overlay.onClose = () -> {
            if (browserOverlay == overlay) {
                browserOverlay = null;
                isFolderBrowserOpen = false;
            }
        };
        browserOverlay = overlay;
        LinearLayout panel = browserOverlay.body;

        browserPathText = widgets.caption(displayPathForBrowser(current), COLOR_SUBTLE);
        browserPathText.setMaxLines(2);
        browserPathText.setEllipsize(TextUtils.TruncateAt.MIDDLE);
        panel.addView(browserPathText, margin(matchWidth(), 0, 0, 0, sdp(10)));

        ScrollView listScroll = new ScrollView(this);
        listScroll.setBackground(rounded(COLOR_SURFACE_ALT, COLOR_BORDER, dp(1), dp(10)));
        listScroll.setFillViewport(true);
        listScroll.setClipChildren(false);
        listScroll.setClipToPadding(false);
        listScroll.setFocusable(false);
        listScroll.setFocusableInTouchMode(false);
        panel.addView(listScroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, sdp(330)));
        browserListContainer = new LinearLayout(this);
        browserListContainer.setOrientation(LinearLayout.VERTICAL);
        browserListContainer.setClipChildren(false);
        browserListContainer.setClipToPadding(false);
        listScroll.addView(browserListContainer, matchWidth());

        browserMessage = widgets.caption(kind.equals("local") ? "" : "正在读取目录...", COLOR_SUBTLE);
        panel.addView(browserMessage, margin(matchWidth(), 0, sdp(8), 0, sdp(4)));

        browserMountButton = button("选择此目录");
        browserMountButton.setOnClickListener(view -> confirmMediaSource());
        browserBackButton = button("返回上级");
        browserBackButton.setOnClickListener(view -> browserNavigateUp());
        browserCancelButton = button("取消");
        browserCancelButton.setOnClickListener(view -> closeFolderBrowser());
        overlays.footer(panel, browserMountButton, browserBackButton, browserCancelButton);

        loadBrowserEntries(browserCurrent);
        browserOverlay.focus(browserFocusRows(), browserMountButton);
    }

    /**
     * The action row first, then one row per folder; rebuilt whenever the list changes.
     * <p>
     * The action row sits under the list on screen but leads the D-pad order: a video folder
     * routinely holds dozens of subfolders, and the remote has to reach 「选择此目录」 in one key
     * press from the top rather than by walking the whole list. DOWN enters the folders, UP
     * returns to the buttons.
     */
    private List<List<View>> browserFocusRows() {
        List<List<View>> rows = new ArrayList<>();
        if (browserMountButton != null) {
            rows.add(Arrays.asList(browserMountButton, browserBackButton, browserCancelButton));
        }
        for (Button item : browserItemButtons) {
            rows.add(Collections.singletonList(item));
        }
        return rows;
    }

    private void closeFolderBrowser() {
        if (browserOverlay == null) {
            return;
        }
        Overlays.Overlay closing = browserOverlay;
        browserOverlay = null;
        isFolderBrowserOpen = false;
        browserKind = null;
        browserRoot = null;
        browserCurrent = null;
        browserUser = "";
        browserPass = "";
        browserLabel = "";
        browserItems.clear();
        browserItemButtons.clear();
        browserListContainer = null;
        browserPathText = null;
        browserMessage = null;
        browserMountButton = null;
        browserBackButton = null;
        browserCancelButton = null;
        closing.close();
    }

    private void loadBrowserEntries(String url) {
        if (!isFolderBrowserOpen || !url.equals(browserCurrent)) {
            return;
        }
        String kind = browserKind;
        String username = browserUser;
        String password = browserPass;
        runAsync(
                () -> {
                    List<BrowseEntry> entries = new ArrayList<>();
                    if (kind.equals("local")) {
                        File dir = new File(url);
                        File[] children = dir.listFiles(File::isDirectory);
                        if (children == null) {
                            throw new IOException("该目录无法读取。");
                        }
                        for (File child : children) {
                            String name = child.getName();
                            if (name.startsWith(".") || name.startsWith("_")) {
                                continue;
                            }
                            entries.add(new BrowseEntry(name, child.getAbsolutePath(), true));
                        }
                    } else if (kind.equals("webdav")) {
                        WebDavClient client = new WebDavClient(url, new WebDavClient.Credential(username, password));
                        for (WebDavClient.Entry entry : client.list(url)) {
                            // The first PROPFIND response is the listed directory itself.
                            if (!entry.isCollection || entry.name.isEmpty()
                                    || sameDirectoryUrl(entry.href, url)) {
                                continue;
                            }
                            entries.add(new BrowseEntry(entry.name, entry.href, true));
                        }
                    } else {
                        SmbClient client = new SmbClient(url, username, password);
                        List<BrowseEntry> children = isSmbShareLevel(url)
                                ? client.listShares(url) : client.list(url);
                        for (BrowseEntry entry : children) {
                            if (entry.directory) {
                                entries.add(entry);
                            }
                        }
                    }
                    Collections.sort(entries);
                    return entries;
                },
                entries -> {
                    if (!isFolderBrowserOpen || !url.equals(browserCurrent)) {
                        return;
                    }
                    browserItems.clear();
                    browserItems.addAll(entries);
                    renderBrowserItems();
                },
                error -> {
                    if (!isFolderBrowserOpen || !url.equals(browserCurrent)) {
                        return;
                    }
                    browserItems.clear();
                    renderBrowserItems();
                    browserMessage.setText("读取失败：" + friendlyLoginError(error));
                });
    }

    private void renderBrowserItems() {
        if (!isFolderBrowserOpen || browserListContainer == null) {
            return;
        }
        browserListContainer.removeAllViews();
        browserItemButtons.clear();
        if (browserPathText != null) {
            browserPathText.setText(displayPathForBrowser(browserCurrent));
        }
        boolean atRoot = browserIsAtRoot();
        if (!atRoot) {
            Button up = browserItemButton("..  返回上级目录");
            up.setOnClickListener(view -> browserNavigateUp());
            browserListContainer.addView(up, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, sdp(52)));
            browserItemButtons.add(up);
        }
        for (BrowseEntry item : browserItems) {
            Button button = browserItemButton(item.name);
            button.setOnClickListener(view -> browserNavigateInto(item));
            browserListContainer.addView(button, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, sdp(56)));
            browserItemButtons.add(button);
        }
        if (browserItems.isEmpty()) {
            String currentMessage = browserMessage.getText().toString();
            browserMessage.setText(currentMessage.isEmpty() ? "（该目录下没有子目录）" : currentMessage);
        } else if (browserMessage != null && !browserMessage.getText().toString().startsWith("读取失败")) {
            browserMessage.setText("");
        }
        if (browserMountButton != null) {
            browserMountButton.setEnabled(browserKind.equals("local") || !isSmbShareLevel(browserCurrent));
        }
        if (browserBackButton != null) {
            browserBackButton.setEnabled(!atRoot);
        }
        rebuildBrowserDpadGrid();
        View focused = getCurrentFocus();
        if (focused == null || !isDescendant(browserOverlay.body, focused)) {
            // The rows were just rebuilt, so the old focus is gone: land back on 「选择此目录」,
            // which is where the panel opens too. A disabled button cannot take focus (an SMB
            // share root still needs a subfolder), so fall back to the first folder / 取消.
            View target = browserMountButton != null && browserMountButton.isEnabled()
                    ? browserMountButton
                    : (browserItemButtons.isEmpty() ? browserCancelButton : browserItemButtons.get(0));
            target.requestFocus();
            FocusGrid.scrollToVisible(target);
        }
    }

    private Button browserItemButton(String label) {
        Button row = widgets.listRow(label, null);
        row.setMaxLines(1);
        row.setEllipsize(TextUtils.TruncateAt.END);
        return row;
    }

    private void rebuildBrowserDpadGrid() {
        if (browserOverlay != null) {
            browserOverlay.redeclare(browserFocusRows());
        }
    }

    /** Enter a listed folder; the entry already carries the exact URL to list next. */
    private void browserNavigateInto(BrowseEntry entry) {
        String next = browserKind.equals("local") ? entry.url : trimTrailingSlash(entry.url);
        if (next.isEmpty()) {
            return;
        }
        browserCurrent = next;
        loadBrowserEntries(next);
    }

    private void browserNavigateUp() {
        String parent = browserParentOf(browserCurrent);
        if (parent != null) {
            browserCurrent = parent;
            loadBrowserEntries(parent);
        }
    }

    private String browserParentOf(String url) {
        if (browserKind.equals("local")) {
            File parent = new File(url).getParentFile();
            return parent == null ? null : parent.getAbsolutePath();
        }
        String trimmed = trimTrailingSlash(url);
        if (browserKind.equals("webdav")) {
            String root = trimTrailingSlash(browserRoot);
            int schemeEnd = trimmed.indexOf("://");
            int lastSlash = trimmed.lastIndexOf('/');
            if (lastSlash <= schemeEnd + 2) {
                return null;
            }
            String parent = trimmed.substring(0, lastSlash);
            // The mounted root is the top of this picker; never walk above it.
            return parent.startsWith(root) ? parent : root;
        }
        String normalized = SmbClient.normalize(url);
        int schemeEnd = normalized.indexOf("://") + 3;
        String[] segments = normalized.substring(schemeEnd).split("/");
        if (segments.length <= 1) {
            return null;
        }
        if (segments.length == 2) {
            return "smb://" + segments[0];
        }
        return "smb://" + segments[0] + "/" + segments[1];
    }

    private boolean browserIsAtRoot() {
        if (browserKind.equals("local")) {
            return browserParentOf(browserCurrent) == null
                    || browserCurrent.equals("/");
        }
        if (browserKind.equals("webdav")) {
            return trimTrailingSlash(browserCurrent).equals(trimTrailingSlash(browserRoot));
        }
        return isSmbShareLevel(browserCurrent);
    }

    private static String trimTrailingSlash(String value) {
        String result = value == null ? "" : value;
        while (result.length() > 1 && result.endsWith("/")) {
            result = result.substring(0, result.length() - 1);
        }
        return result;
    }

    /** Directory urls can differ by trailing slash or percent-encoding; compare them anyway. */
    private static boolean sameDirectoryUrl(String left, String right) {
        String a = trimTrailingSlash(left);
        String b = trimTrailingSlash(right);
        if (a.equalsIgnoreCase(b)) {
            return true;
        }
        return decodeForDisplay(a).equalsIgnoreCase(decodeForDisplay(b));
    }

    private static String decodeForDisplay(String value) {
        return UrlLabels.displayPath(value);
    }

    /** 媒体源显示名：早先挂载的源存的是编码过的目录段，展示前统一还原成文件夹名。 */
    private static String displaySourceName(LocalEngine.Source source) {
        String name = source.name == null ? "" : source.name.trim();
        if (name.isEmpty()) {
            name = source.path == null ? "" : source.path;
        }
        return "webdav".equals(source.kind) ? UrlLabels.displayPath(name) : name;
    }

    /** 媒体源行上的路径：只有 WebDAV 存的是编码过的 URL，本地与 SMB 原样显示。 */
    private static String displaySourcePath(String kind, String path) {
        return "webdav".equals(kind) ? UrlLabels.displayPath(path) : path;
    }

    /** 目录浏览器顶部那行路径：WebDAV 的 href 是编码过的，本地路径原样显示。 */
    private static String displayPathForBrowser(String url) {
        return url != null && url.startsWith("http") ? UrlLabels.displayPath(url) : url;
    }

    private static boolean isSmbShareLevel(String url) {
        String normalized = SmbClient.normalize(url);
        int schemeEnd = normalized.indexOf("://");
        if (schemeEnd < 0) {
            return false;
        }
        return !normalized.substring(schemeEnd + 3).contains("/");
    }

    private void confirmMediaSource() {
        String kind = browserKind;
        String path = browserCurrent;
        String username = browserUser;
        String password = browserPass;
        String label = browserLabel;
        closeFolderBrowser();
        mountMediaSource(kind, path, username, password, label);
    }

    /** Save the source, remember it for next time, and start the scan. */
    private void mountMediaSource(String kind, String path, String username, String password, String label) {
        if (kind.equals("local") && !new File(path).isDirectory()) {
            showToast("本地目录不存在或不可读：" + path);
            return;
        }
        api.engine().saveSource(kind, "", path, username, password);
        if (savedConnections != null && !kind.equals("local")) {
            savedConnections.recordMount(kind, path, label, username, password);
        }
        if (sourcesOverlay != null) {
            // 弹窗里挂载：不跳页，新源立刻出现在列表里，扫描刮削就在弹窗里跑起来。
            renderSourcesOverlay();
            refreshHomeLibrary(false);
            startScanAndScrape(sourcesOverlay == null ? null : sourcesOverlay.statusLine());
            return;
        }
        // 全屏 loading 挡在海报墙前面：几百个文件的 WebDAV 能挂在那儿几分钟，一张海报都看不到。
        // 照 mac 版：立刻回海报墙，扫描刮削在后台跑，海报一张张上墙，失败只提示不挡路。
        showHome();
        startScanAndScrape(null);
    }

    private void showHome() {
        screen = Screen.HOME;
        applyImmersiveMode(false);
        boolean returningFromDetail = currentDetail != null;
        currentDetail = null;
        selectedSeasonId = "";
        homeScrollY = 0;
        destroyPlayer();
        boolean hasVisibleLibrary = !libraryItems.isEmpty();
        if (!hasVisibleLibrary) {
            List<Models.LibraryItem> cachedItems = api.getCachedLibraryItems();
            if (!cachedItems.isEmpty()) {
                libraryItems = new ArrayList<>(cachedItems);
                hasVisibleLibrary = true;
            }
        }

        if (hasVisibleLibrary) {
            renderHomeContent();
        } else {
            showLoading("正在加载媒体库");
        }

        if (!returningFromDetail || !hasVisibleLibrary) {
            refreshHomeLibrary(!hasVisibleLibrary);
        }
        // 刮削是在后台推进的：从详情页或播放器回到首页时，手上这份名单可能已经是刮削前那一版，
        // 不重读的话卡片就一直停在占位字母上（详情页每次现查数据库，所以它看起来是「已刮削」）。
        refreshLibraryInPlace();
        startStartupSync();
    }

    private void refreshHomeLibrary(boolean showErrorScreen) {
        runAsync(
                () -> api.getLibraryItems(),
                items -> {
                    if (screen != Screen.HOME) {
                        return;
                    }
                    libraryItems = new ArrayList<>(items);
                    renderHomeContent();
                },
                error -> {
                    if (showErrorScreen) {
                        showSetup(error.getMessage());
                    }
                });
    }

    /**
     * Draws the library screen. The toolbar, status line and scroll container are built once
     * and kept; sorting and searching only rebuild the rows below them, which is what
     * lets the page keep its scroll offset, its focus and its already-loaded posters.
     */
    /**
     * Tears every panel down before a screen takes the whole window, so no dialog can survive
     * the view tree it was added to (and keep eating the back key from the new screen).
     */
    private void closeAllOverlays() {
        settingsOverlay = null;
        sourcesOverlay = null;
        loginOverlay = null;
        browserOverlay = null;
        phoneInputOverlay = null;
        textInputOverlay = null;
        isSettingsOpen = false;
        isSourcesOpen = false;
        isNetworkLoginOpen = false;
        isFolderBrowserOpen = false;
        isPhoneInputOpen = false;
        if (phoneInput != null) {
            phoneInput.clearSession();
        }
        overlays.closeAll();
        focusGrid.clear();
    }

    private void renderHomeContent() {
        screen = Screen.HOME;
        if (homeScrollView == null || homeScrollView.getParent() != root) {
            buildHomeShell();
        }
        refreshHomeControls();
        refreshHomeBody();
    }

    private void buildHomeShell() {
        closeAllOverlays();
        root.removeAllViews();
        applyAppBackground();

        ScrollView scroll = new ScrollView(this);
        scroll.setFocusable(false);
        scroll.setFocusableInTouchMode(false);
        homeScrollView = scroll;
        scroll.setFillViewport(false);
        scroll.setClipChildren(false);
        scroll.setClipToPadding(false);
        FrameLayout.LayoutParams scrollParams = match();
        scrollParams.setMargins(0, homeTopPaddingPx(), 0, 0);
        root.addView(scroll, scrollParams);

        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(sdp(HOME_PAGE_HORIZONTAL_PADDING_DP), 0, sdp(HOME_PAGE_HORIZONTAL_PADDING_DP), sdp(42));
        page.setClipChildren(false);
        page.setClipToPadding(false);
        scroll.addView(page, matchWidth());
        int headerInset = homePosterVisualLeftInsetPx();

        LinearLayout toolbar = new LinearLayout(this);
        toolbar.setGravity(Gravity.CENTER_VERTICAL);
        toolbar.setClipChildren(false);
        toolbar.setClipToPadding(false);
        toolbar.setPadding(headerInset, 0, headerInset, 0);
        page.addView(toolbar, margin(matchWidth(), 0, sdp(4), 0, sdp(14)));
        TextView brand = widgets.text("觅影 OmniPlay", theme.ssp(Widgets.TEXT_TITLE), Typeface.BOLD, COLOR_TEXT);
        toolbar.addView(brand, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));

        homeSearchToggle = widgets.toolbarButton("搜索");
        toolbar.addView(homeSearchToggle, new LinearLayout.LayoutParams(sdp(112), sdp(Widgets.HEIGHT_COMPACT)));
        homeSearchToggle.setOnClickListener(view -> {
            isSearchOpen = !isSearchOpen;
            if (!isSearchOpen) {
                searchText = "";
            }
            refreshHomeControls();
            refreshHomeBody();
            if (isSearchOpen && homeSearchInput != null) {
                homeSearchInput.requestFocus();
            } else {
                homeSearchToggle.requestFocus();
            }
        });

        homeSortToggle = widgets.toolbarButton("排序");
        toolbar.addView(homeSortToggle, margin(new LinearLayout.LayoutParams(sdp(96), sdp(Widgets.HEIGHT_COMPACT)), sdp(9), 0, 0, 0));
        homeSortToggle.setOnClickListener(view -> {
            isSortOpen = !isSortOpen;
            refreshHomeControls();
            homeSortToggle.requestFocus();
        });

        homeDirectionToggle = widgets.toolbarButton(sortDescending ? "降序" : "升序");
        toolbar.addView(homeDirectionToggle, margin(new LinearLayout.LayoutParams(sdp(96), sdp(Widgets.HEIGHT_COMPACT)), sdp(9), 0, 0, 0));
        homeDirectionToggle.setOnClickListener(view -> {
            sortDescending = !sortDescending;
            homeDirectionToggle.setText(sortDescending ? "降序" : "升序");
            refreshHomeBody();
        });

        homeScanToggle = widgets.toolbarButton(scanButtonLabel());
        toolbar.addView(homeScanToggle, margin(new LinearLayout.LayoutParams(sdp(112), sdp(Widgets.HEIGHT_COMPACT)), sdp(9), 0, 0, 0));
        homeScanToggle.setOnClickListener(view -> startScanAndScrape(null));

        homeSourcesToggle = widgets.toolbarButton("媒体源");
        toolbar.addView(homeSourcesToggle, margin(new LinearLayout.LayoutParams(sdp(104), sdp(Widgets.HEIGHT_COMPACT)), sdp(9), 0, 0, 0));
        homeSourcesToggle.setOnClickListener(view -> openSourcesOverlay());

        homeSettingsToggle = widgets.toolbarButton("设置");
        toolbar.addView(homeSettingsToggle, margin(new LinearLayout.LayoutParams(sdp(96), sdp(Widgets.HEIGHT_COMPACT)), sdp(9), 0, 0, 0));
        homeSettingsToggle.setOnClickListener(view -> openSettingsOverlay());

        homeStatusView = widgets.caption("", COLOR_SUBTLE);
        homeStatusView.setMaxLines(2);
        homeStatusView.setEllipsize(TextUtils.TruncateAt.END);
        homeStatusView.setPadding(headerInset, 0, headerInset, 0);
        page.addView(homeStatusView, margin(matchWidth(), 0, 0, 0, sdp(8)));
        homeStatusView.setVisibility(View.GONE);
        refreshHomeStatusText();

        homeControls = new LinearLayout(this);
        homeControls.setOrientation(LinearLayout.VERTICAL);
        homeControls.setClipChildren(false);
        homeControls.setClipToPadding(false);
        page.addView(homeControls, matchWidth());

        homeBody = new LinearLayout(this);
        homeBody.setOrientation(LinearLayout.VERTICAL);
        homeBody.setClipChildren(false);
        homeBody.setClipToPadding(false);
        page.addView(homeBody, matchWidth());

        // 冷启动后画面里一个焦点都没有，遥控器第一下会被「建立焦点」吃掉；这里主动落一个。
        root.post(this::requestFirstHomeFocus);
    }

    /** Sort / search rows: the only part of the header that ever changes. */
    private void refreshHomeControls() {
        if (homeControls == null) {
            return;
        }
        homeSearchInput = null;
        homeControls.removeAllViews();
        int headerInset = homePosterVisualLeftInsetPx();

        if (isSearchOpen) {
            LinearLayout searchRow = new LinearLayout(this);
            searchRow.setGravity(Gravity.CENTER_VERTICAL);
            searchRow.setClipChildren(false);
            searchRow.setClipToPadding(false);
            searchRow.setPadding(headerInset, 0, headerInset, 0);
            homeControls.addView(searchRow, margin(matchWidth(), 0, 0, 0, sdp(14)));
            homeSearchInput = input("搜索", searchText);
            homeSearchInput.setSelection(homeSearchInput.getText().length());
            homeSearchInput.addTextChangedListener(new TextWatcher() {
                @Override
                public void beforeTextChanged(CharSequence value, int start, int count, int after) {
                }

                @Override
                public void onTextChanged(CharSequence value, int start, int before, int count) {
                }

                @Override
                public void afterTextChanged(Editable editable) {
                    searchText = editable.toString();
                    if (pendingSearchRender != null) {
                        main.removeCallbacks(pendingSearchRender);
                    }
                    // Typing only re-runs the query; the field itself must not be rebuilt.
                    pendingSearchRender = () -> {
                        pendingSearchRender = null;
                        if (screen == Screen.HOME && isSearchOpen) {
                            refreshHomeBody();
                        }
                    };
                    main.postDelayed(pendingSearchRender, 220);
                }
            });
            searchRow.addView(homeSearchInput, new LinearLayout.LayoutParams(0, sdp(Widgets.HEIGHT_INPUT), 1f));
            Button apply = widgets.toolbarButton("确定");
            searchRow.addView(apply, margin(new LinearLayout.LayoutParams(sdp(106), sdp(50)), sdp(14), sdp(4), 0, sdp(4)));
            apply.setOnClickListener(view -> {
                searchText = homeSearchInput.getText().toString();
                refreshHomeBody();
                homeSearchInput.requestFocus();
            });
        }

        if (isSortOpen) {
            LinearLayout sortRow = new LinearLayout(this);
            sortRow.setGravity(Gravity.CENTER_VERTICAL);
            sortRow.setClipChildren(false);
            sortRow.setClipToPadding(false);
            sortRow.setPadding(headerInset, 0, headerInset, 0);
            homeControls.addView(sortRow, margin(matchWidth(), 0, 0, 0, sdp(14)));
            addSortButton(sortRow, "名称", SortKey.TITLE);
            addSortButton(sortRow, "评分", SortKey.RATING);
            addSortButton(sortRow, "上映年份", SortKey.YEAR);
        }

    }

    /** Continue-watching rail and the poster wall: rebuilt whenever the list or the search term changes. */
    private void refreshHomeBody() {
        if (homeBody == null) {
            return;
        }
        int scrollY = homeScrollView == null ? 0 : homeScrollView.getScrollY();
        String focusedItemId = focusedPosterItemId();
        homeBody.removeAllViews();
        homePosterViews.clear();
        refreshHomeStatusText();
        int headerInset = homePosterVisualLeftInsetPx();
        List<Models.LibraryItem> continueItems = continueItems(libraryItems);

        LinearLayout continueHeader = new LinearLayout(this);
        continueHeader.setGravity(Gravity.CENTER_VERTICAL);
        continueHeader.setClipChildren(false);
        continueHeader.setClipToPadding(false);
        continueHeader.setPadding(headerInset, 0, headerInset, 0);
        homeBody.addView(continueHeader, margin(matchWidth(), 0, continueItems.isEmpty() ? sdp(12) : 0, 0, sdp(12)));
        TextView continueTitle = sectionTitle("继续播放");
        continueTitle.setPadding(0, 0, 0, 0);
        continueHeader.addView(continueTitle, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));

        if (continueItems.isEmpty()) {
            continueHeader.setVisibility(View.GONE);
        } else {
            HorizontalScrollView continueScroll = new HorizontalScrollView(this);
            continueScroll.setFocusable(false);
            continueScroll.setFocusableInTouchMode(false);
            continueScroll.setClipChildren(false);
            continueScroll.setClipToPadding(false);
            continueScroll.setHorizontalScrollBarEnabled(false);
            continueScroll.setPadding(headerInset, 0, headerInset, 0);
            homeBody.addView(continueScroll, margin(matchWidth(), 0, 0, 0, sdp(14)));
            LinearLayout continueRow = new LinearLayout(this);
            continueRow.setOrientation(LinearLayout.HORIZONTAL);
            continueRow.setClipChildren(false);
            continueRow.setClipToPadding(false);
            continueScroll.addView(continueRow, new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));
            int continueCardWidth = homePosterCardWidthPx();
            for (Models.LibraryItem item : continueItems) {
                View card = posterCard(item, true);
                LinearLayout.LayoutParams cardParams = new LinearLayout.LayoutParams(continueCardWidth,
                        ViewGroup.LayoutParams.WRAP_CONTENT);
                cardParams.setMargins(sdp(HOME_POSTER_CARD_MARGIN_DP), sdp(5),
                        sdp(HOME_POSTER_CARD_MARGIN_DP), 0);
                card.setLayoutParams(cardParams);
                continueRow.addView(card);
            }
        }

        LinearLayout libraryHeader = new LinearLayout(this);
        libraryHeader.setGravity(Gravity.CENTER_VERTICAL);
        libraryHeader.setClipChildren(false);
        libraryHeader.setClipToPadding(false);
        libraryHeader.setPadding(headerInset, 0, headerInset, 0);
        homeBody.addView(libraryHeader, margin(matchWidth(), 0, continueItems.isEmpty() ? sdp(12) : 0, 0, sdp(10)));
        TextView libraryTitle = sectionTitle("所有影视");
        libraryTitle.setPadding(0, 0, 0, 0);
        libraryHeader.addView(libraryTitle, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        libraryHeader.addView(widgets.caption(libraryItems.size() + " 部", COLOR_SUBTLE),
                margin(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT,
                        ViewGroup.LayoutParams.WRAP_CONTENT), sdp(12), sdp(4), 0, 0));

        List<Models.LibraryItem> items = displayedItems();
        GridLayout grid = posterGrid();
        homeBody.addView(grid);
        for (Models.LibraryItem item : items) {
            grid.addView(posterCard(item, false));
        }
        if (items.isEmpty()) {
            homeBody.addView(api.engine().sources().isEmpty()
                    ? emptySourceCallToAction()
                    : emptyText(libraryItems.isEmpty() ? "媒体库为空" : "没有匹配结果"));
        }

        if (homeScrollView != null) {
            homeScrollView.scrollTo(0, scrollY);
        }
        restorePosterFocus(focusedItemId);
    }

    /** 重建之后焦点从名单里补回来，别让刷新把用户丢回页面顶上。 */
    private void restorePosterFocus(String itemId) {
        View focused = getCurrentFocus();
        if (itemId == null || (focused != null && focused.isAttachedToWindow())) {
            return;
        }
        List<PosterViews> holders = homePosterViews.get(itemId);
        if (holders == null || holders.isEmpty()) {
            return;
        }
        FocusGrid.grab(holders.get(0).card);
    }

    private void addSortButton(LinearLayout row, String label, SortKey key) {
        Button option = button(label, sortKey == key);
        row.addView(option, margin(new LinearLayout.LayoutParams(sdp(146), sdp(48)), 0, 0, sdp(12), 0));
        option.setOnClickListener(view -> {
            sortKey = key;
            isSortOpen = false;
            refreshHomeControls();
            refreshHomeBody();
            homeSortToggle.requestFocus();
        });
    }

    /** Kept for call sites that used the old full-page rebuild; the page now keeps its own offset. */
    private void renderHomeContentKeepingPosition() {
        if (homeScrollView != null) {
            homeScrollY = homeScrollView.getScrollY();
        }
        refreshHomeControls();
        refreshHomeBody();
        if (homeScrollView != null) {
            homeScrollView.scrollTo(0, homeScrollY);
        }
    }

    /** Rows used when the page needs somewhere safe to put focus: the wall first, then the toolbar. */
    private List<List<View>> homeFocusRows() {
        List<List<View>> rows = new ArrayList<>();
        List<View> cards = new ArrayList<>();
        if (homeBody != null) {
            collectFocusables(homeBody, cards);
        }
        if (!cards.isEmpty()) {
            rows.add(cards);
        }
        if (homeSortToggle != null) {
            rows.add(Arrays.asList(homeSearchToggle, homeSortToggle, homeDirectionToggle,
                    homeScanToggle, homeSourcesToggle, homeSettingsToggle));
        }
        return rows;
    }

    private static void collectFocusables(View view, List<View> out) {
        if (view == null || view.getVisibility() == View.INVISIBLE || view.getVisibility() == View.GONE) {
            return;
        }
        if (view instanceof ViewGroup) {
            ViewGroup group = (ViewGroup) view;
            for (int index = 0; index < group.getChildCount(); index++) {
                collectFocusables(group.getChildAt(index), out);
            }
            // Scroll containers are traversal wrappers, not controls: without this they become the
            // view a panel opens on, which shows no focus ring at all.
            if (group.isFocusable() && !(group instanceof ScrollView)
                    && !(group instanceof HorizontalScrollView)) {
                out.add(group);
            }
            return;
        }
        if (view.isFocusable()) {
            out.add(view);
        }
    }

    private void openSettingsOverlay() {
        if (settingsOverlay != null) {
            return;
        }
        settingsRestoreFocus = getCurrentFocus();
        isSettingsOpen = true;
        renderSettingsOverlay();
    }

    private void closeSettingsOverlay() {
        if (settingsOverlay == null) {
            return;
        }
        Overlays.Overlay closing = settingsOverlay;
        settingsOverlay = null;
        isSettingsOpen = false;
        closing.close();
    }

    private void renderSettingsOverlay() {
        if (settingsOverlay != null) {
            return;
        }
        Overlays.Options options = new Overlays.Options();
        options.title = "设置";
        options.widthDp = 460;
        options.sideSheet = true;
        options.restoreFocus = settingsRestoreFocus;
        Overlays.Overlay overlay = overlays.open(options);
        overlay.onClose = () -> {
            if (settingsOverlay == overlay) {
                settingsOverlay = null;
                isSettingsOpen = false;
            }
        };
        settingsOverlay = overlay;
        isSettingsOpen = true;
        LinearLayout panel = settingsOverlay.body;
        TvSettings prefs = api.engine().settings();
        String versionName = "1.22";
        try {
            versionName = getPackageManager().getPackageInfo(getPackageName(), 0).versionName;
        } catch (Exception ignored) {
        }
        // 保存 / 验证的反馈共用一条只在有内容时占位的状态行，卡片高度不会因此跳动。
        TextView settingsStatus = new Overlays.StatusText(this, COLOR_MUTED,
                ssp(Widgets.TEXT_CAPTION), theme);
        settingsStatus.setMaxLines(3);
        settingsStatus.setEllipsize(TextUtils.TruncateAt.END);
        panel.addView(settingsStatus, margin(matchWidth(), 0, sdp(4), 0, 0));

        // ---------------------------------------------------------------- TMDB
        panel.addView(widgets.sectionCaption("TMDB 刮削"), margin(matchWidth(), 0, 0, 0, 0));
        LinearLayout tmdbCard = widgets.settingsCard();
        panel.addView(tmdbCard, margin(matchWidth(), 0, 0, 0, 0));
        EditText tmdbKeyInput = input("填写 API Key；留空则改用下面的反代地址",
                api.engine().tmdb().apiKey());
        widgets.addCardRow(tmdbCard, labelledRow("TMDB API Key", tmdbKeyInput));
        EditText tmdbProxyInput = input("https://tmdb.example.com/tmdb0512，留空直连官方 API",
                api.engine().tmdb().proxyUrl());
        widgets.addCardRow(tmdbCard, labelledRow("反向代理地址（可选）", tmdbProxyInput));
        Button saveTmdb = compactButton("保存配置");
        Button verifyTmdb = compactButton("验证连接");
        widgets.addCardRow(tmdbCard, widgets.cardButtonRow(saveTmdb, verifyTmdb));
        Button tmdbPhoneInput = compactButton("用手机输入 TMDB 配置");
        widgets.addCardRow(tmdbCard, tmdbPhoneInput);
        panel.addView(widgets.caption("API Key 与 v4 Read Access Token 都能直接粘进来，自动识别；"
                        + "反代只转发请求，钥匙仍要填。反代地址填根地址就行，/3 会自动补。",
                        COLOR_SUBTLE), margin(matchWidth(), sdp(4), sdp(4), 0, sdp(2)));
        saveTmdb.setOnClickListener(view -> {
            api.engine().tmdb().setApiKey(tmdbKeyInput.getText().toString().trim());
            api.engine().tmdb().setProxyUrl(tmdbProxyInput.getText().toString().trim());
            settingsStatus.setText(api.engine().tmdb().isConfigured()
                    ? api.engine().tmdbStatusMessage()
                    : "请填写 API Key 或反代地址。");
        });
        tmdbPhoneInput.setOnClickListener(view -> openPhoneInputOverlay("填写 TMDB 配置",
                "填入后请点“保存配置”。",
                Arrays.asList(
                        boundField("tmdb_key", "TMDB API Key / v4 Token", () -> tmdbKeyInput, false),
                        boundField("tmdb_proxy", "反向代理地址（留空直连官方 API）",
                                () -> tmdbProxyInput, false)),
                true));
        verifyTmdb.setOnClickListener(view -> {
            settingsStatus.setText("正在检测 TMDB…");
            runAsync(() -> api.engine().tmdb().checkConnectionDetailed().message,
                    settingsStatus::setText,
                    error -> settingsStatus.setText("检测失败：" + error.getMessage()));
        });

        // ---------------------------------------------------------------- 外观主题
        panel.addView(widgets.sectionCaption("外观主题"), margin(matchWidth(), 0, 0, 0, 0));
        LinearLayout themeCard = widgets.settingsCard();
        panel.addView(themeCard, margin(matchWidth(), 0, 0, 0, 0));
        List<Theme.Variant> variants = Theme.VARIANTS;
        Button[] themePills = new Button[Math.max(1, (variants.size() + 1) / 2)];
        for (int start = 0; start < variants.size(); start += 2) {
            Button left = themePill(variants.get(start), prefs);
            if (start == 0) {
                themePills[0] = left;
            }
            Button right = start + 1 < variants.size()
                    ? themePill(variants.get(start + 1), prefs) : spacerPill();
            widgets.addCardRow(themeCard, widgets.cardButtonRow(left, right));
        }
        panel.addView(widgets.caption("浅色主题与 macOS 版一致；深色主题适合关灯观影，切换后立即生效。",
                        COLOR_SUBTLE), margin(matchWidth(), sdp(4), sdp(4), 0, sdp(2)));

        // ---------------------------------------------------------------- 刮削与播放偏好
        panel.addView(widgets.sectionCaption("刮削与播放偏好"), margin(matchWidth(), 0, 0, 0, 0));
        LinearLayout preferenceCard = widgets.settingsCard();
        panel.addView(preferenceCard, margin(matchWidth(), 0, 0, 0, 0));
        Widgets.Segment scrapeLanguage = widgets.segment(
                Arrays.asList("简体中文", "English"),
                "en-US".equals(prefs.scrapeLanguage()) ? 1 : 0,
                index -> prefs.setScrapeLanguage(index == 1 ? "en-US" : "zh-CN"));
        widgets.addCardRow(preferenceCard, labelledRow("刮削语言", scrapeLanguage.view));
        String[] audioOptions = {"smart", "zh", "en", "ja"};
        Widgets.Segment audio = widgets.segment(
                Arrays.asList("智能", "中文", "英语", "日语"),
                indexOfValue(audioOptions, prefs.defaultAudioLanguage()),
                index -> prefs.setDefaultAudioLanguage(audioOptions[index]));
        widgets.addCardRow(preferenceCard, labelledRow("音频轨道", audio.view));
        String[] subtitleOptions = {"zh", "en", "no"};
        Widgets.Segment subtitles = widgets.segment(
                Arrays.asList("中文优先", "英文优先", "关闭字幕"),
                indexOfValue(subtitleOptions, prefs.defaultSubtitleLanguage()),
                index -> prefs.setDefaultSubtitleLanguage(subtitleOptions[index]));
        widgets.addCardRow(preferenceCard, labelledRow("字幕轨道", subtitles.view));
        String[] qualityOptions = {TvSettings.QUALITY_SMOOTH, TvSettings.QUALITY_BALANCED,
                TvSettings.QUALITY_HIGH};
        Widgets.Segment quality = widgets.segment(
                Arrays.asList("流畅优先", "平衡", "画质优先"),
                indexOfValue(qualityOptions, prefs.playbackQualityMode()),
                index -> prefs.setPlaybackQualityMode(qualityOptions[index]));
        widgets.addCardRow(preferenceCard, labelledRow("网络缓冲", quality.view));
        Widgets.SwitchRow episodeDetailsRow = widgets.switchRow("详情页显示分集简介与进度",
                prefs.showEpisodeDetails());
        episodeDetailsRow.view.setOnClickListener(view -> {
            boolean next = !prefs.showEpisodeDetails();
            prefs.setShowEpisodeDetails(next);
            showEpisodeDetails = next;
            episodeDetailsRow.setOn(next);
        });
        widgets.addCardRow(preferenceCard, episodeDetailsRow.view);
        panel.addView(widgets.caption("智能 = 按制片国家语言选音轨；缓冲模式只调整 mpv 预读策略，"
                        + "画质优先高码率更稳。", COLOR_SUBTLE),
                margin(matchWidth(), sdp(4), sdp(4), 0, sdp(2)));

        // ---------------------------------------------------------------- 基础设置
        panel.addView(widgets.sectionCaption("基础设置"), margin(matchWidth(), 0, 0, 0, 0));
        LinearLayout basicCard = widgets.settingsCard();
        panel.addView(basicCard, margin(matchWidth(), 0, 0, 0, 0));
        Widgets.SwitchRow autoScan = widgets.switchRow("启动时自动扫描并同步",
                prefs.autoScanOnStartup());
        autoScan.view.setOnClickListener(view -> {
            boolean next = !prefs.autoScanOnStartup();
            prefs.setAutoScanOnStartup(next);
            autoScan.setOn(next);
        });
        widgets.addCardRow(basicCard, autoScan.view);
        Widgets.SwitchRow autoScrape = widgets.switchRow("扫描后自动刮削海报",
                prefs.autoScrapeAfterScan());
        autoScrape.view.setOnClickListener(view -> {
            boolean next = !prefs.autoScrapeAfterScan();
            prefs.setAutoScrapeAfterScan(next);
            autoScrape.setOn(next);
        });
        widgets.addCardRow(basicCard, autoScrape.view);
        Widgets.SwitchRow realPath = widgets.switchRow("媒体源显示真实路径",
                prefs.showRealSourcePath());
        realPath.view.setOnClickListener(view -> {
            boolean next = !prefs.showRealSourcePath();
            prefs.setShowRealSourcePath(next);
            realPath.setOn(next);
        });
        widgets.addCardRow(basicCard, realPath.view);

        // ---------------------------------------------------------------- 4K DV/HDR
        panel.addView(widgets.sectionCaption("4K DV/HDR 播放模式"),
                margin(matchWidth(), 0, 0, 0, 0));
        LinearLayout playbackCard = widgets.settingsCard();
        panel.addView(playbackCard, margin(matchWidth(), 0, 0, 0, 0));
        Widgets.Segment playbackMode = widgets.segment(
                Arrays.asList("兼容字幕播放", "高性能直出"),
                highPerformanceDirectPlayback ? 1 : 0,
                index -> update4kDvHdrPlaybackMode(index == 1));
        widgets.addCardRow(playbackCard, playbackMode.view);
        panel.addView(widgets.caption("兼容字幕播放的字幕兼容性更好但更吃性能，卡顿就换高性能直出；"
                        + "高性能直出资源要求低，但可能无法直接烧录字幕。", COLOR_SUBTLE),
                margin(matchWidth(), sdp(4), sdp(4), 0, sdp(2)));

        // ---------------------------------------------------------------- 关于与更新
        panel.addView(widgets.sectionCaption("关于与更新"), margin(matchWidth(), 0, 0, 0, 0));
        LinearLayout aboutCard = widgets.settingsCard();
        panel.addView(aboutCard, margin(matchWidth(), 0, 0, 0, 0));
        Button update = compactButton("检查软件更新");
        widgets.addCardRow(aboutCard, update);
        TextView updateStatus = new Overlays.StatusText(this, COLOR_MUTED,
                ssp(Widgets.TEXT_CAPTION), theme);
        widgets.addCardRow(aboutCard, updateStatus);
        Button disconnect = compactButton("退出连接");
        disconnect.setTextColor(COLOR_DANGER);
        widgets.addCardRow(aboutCard, disconnect);
        disconnect.setOnClickListener(view -> disconnectFromServer());
        update.setOnClickListener(view -> startUpdateInstall(update, updateStatus));
        TextView about = widgets.caption("版本 " + versionName
                        + " · 播放器内核 libmpv + FFmpeg；SMB 客户端 jcifs-ng + BouncyCastle；"
                        + "媒体库为本机 SQLite。以上组件按其开源许可证分发。",
                COLOR_SUBTLE);
        about.setPadding(sdp(12), sdp(6), sdp(12), 0);
        widgets.addCardRow(aboutCard, about);

        // 打开设置先停在 TMDB 卡片底部那颗按钮：面板从顶开始、焦点框可见，也不会顺手弹起软键盘。
        declarePanelRows(settingsOverlay, panel, saveTmdb);
    }

    /** 主题卡片里的一颗胶囊：色板 + 名称，当前主题描边高亮。 */
    private Button themePill(Theme.Variant variant, TvSettings prefs) {
        boolean selected = variant.id.equals(prefs.themeId());
        Button option = widgets.button(variant.name, selected);
        option.setTextSize(ssp(Widgets.TEXT_LABEL));
        option.setCompoundDrawables(themeSwatch(variant), null, null, null);
        option.setCompoundDrawablePadding(sdp(8));
        option.setOnClickListener(view -> {
            prefs.setThemeId(variant.id);
            Theme.apply(variant.id);
            syncThemeColors();
            applyThemeChange();
        });
        return option;
    }

    /** 奇数个主题时补齐半行，保证每颗胶囊一样宽。 */
    private Button spacerPill() {
        Button filler = new Button(this);
        filler.setText("");
        filler.setBackground(null);
        filler.setFocusable(false);
        filler.setClickable(false);
        return filler;
    }

    /** 卡片里的一小段带标题的控件：说明文字在上，控件在下。 */
    private LinearLayout labelledRow(String label, View control) {
        LinearLayout wrapper = new LinearLayout(this);
        wrapper.setOrientation(LinearLayout.VERTICAL);
        wrapper.setClipChildren(false);
        wrapper.setClipToPadding(false);
        TextView caption = widgets.caption(label, COLOR_MUTED);
        caption.setPadding(sdp(12), 0, sdp(12), sdp(5));
        wrapper.addView(caption, matchWidth());
        wrapper.addView(control, matchWidth());
        return wrapper;
    }

    private static int indexOfValue(String[] values, String value) {
        for (int index = 0; index < values.length; index++) {
            if (values[index].equals(value)) {
                return index;
            }
        }
        return 0;
    }

    /**
     * Declares the sheet's D-pad rows from the panel itself, so the arrow keys always walk the
     * sheet in the order the user sees: a hand-maintained row list drifts the moment a control is
     * inserted in the middle. Before layout there are no coordinates to group by, so the rows are
     * re-declared once the panel has really been measured.
     */
    private void declarePanelRows(Overlays.Overlay overlay, LinearLayout panel, View initial) {
        overlay.focus(FocusGrid.rowsFrom(panel), initial);
        FocusGrid.afterLayout(panel, () -> {
            if (overlay.isOpen()) {
                overlay.redeclare(FocusGrid.rowsFrom(panel));
            }
        });
    }

    /**
     * Rebuilds the settings panel in place after a preference changes, and puts focus back on
     * the same option: a TV user should not have to walk back down a long list after every
     * toggle, which is what the mac version's instant checkbox feels like by comparison.
     */
    /**
     * 换肤之后必须整屏重建：外壳里的标题、工具条和面板都是按旧调色板画出来的，
     * 只刷新列表会留下「一半新一半旧」的界面。
     */
    private void applyThemeChange() {
        int keepScroll = homeScrollView == null ? 0 : homeScrollView.getScrollY();
        boolean reopenSettings = settingsOverlay != null;
        boolean reopenSources = sourcesOverlay != null;
        closeSettingsOverlay();
        closeSourcesOverlay();
        homeScrollView = null;
        renderHomeContent();
        settingsRestoreFocus = homeSettingsToggle;
        sourcesRestoreFocus = homeSourcesToggle;
        if (reopenSettings) {
            renderSettingsOverlay();
        } else if (reopenSources) {
            renderSourcesOverlay();
        }
        if (homeScrollView != null) {
            homeScrollView.scrollTo(0, keepScroll);
        }
    }

    /** 分段控件自己会重画选中态，这里只需要记偏好，不用重建设置面板。 */
    private void update4kDvHdrPlaybackMode(boolean highPerformanceDirect) {
        highPerformanceDirectPlayback = highPerformanceDirect;
        uiPreferences.edit()
                .putString(
                        KEY_4K_DV_HDR_PLAYBACK_MODE,
                        highPerformanceDirect ? PLAYBACK_MODE_HIGH_PERFORMANCE_DIRECT : PLAYBACK_MODE_COMPATIBLE_SUBTITLE)
                .apply();
    }

    // ------------------------------------------------------------ 媒体源管理

    private void openSourcesOverlay() {
        if (sourcesOverlay != null) {
            return;
        }
        sourcesRestoreFocus = getCurrentFocus();
        isSourcesOpen = true;
        renderSourcesOverlay();
    }

    private void closeSourcesOverlay() {
        if (sourcesOverlay == null) {
            return;
        }
        Overlays.Overlay closing = sourcesOverlay;
        sourcesOverlay = null;
        isSourcesOpen = false;
        closing.close();
    }

    /**
     * 媒体源管理：抽屉形态与功能都照 mac 版的添加媒体源面板——已有源逐条停用 / 重命名 / 移除，
     * 局域网设备打开即扫、点一下就能登录，两种网络媒体源各有自己的添加入口。
     * 添加、挂载、扫描刮削全在弹窗里完成，不用退回首屏设置页。
     */
    private void renderSourcesOverlay() {
        // 全屏设置页退场后这里是唯一的补录时机：升级前就挂好的源也要能一键回填账号密码。
        rememberExistingSources();
        if (sourcesOverlay != null) {
            // Called again after enable / rename / remove: the list has to be redrawn in place.
            Overlays.Overlay closing = sourcesOverlay;
            sourcesOverlay = null;
            closing.close();
        }
        Overlays.Options options = new Overlays.Options();
        options.title = "媒体源管理";
        options.subtitle = "停用后该目录不再扫描入库；移除只清理索引与进度，不会改动共享文件。";
        options.widthDp = 470;
        options.sideSheet = true;
        options.restoreFocus = sourcesRestoreFocus;
        Overlays.Overlay overlay = overlays.open(options);
        overlay.onClose = () -> {
            if (sourcesOverlay == overlay) {
                sourcesOverlay = null;
                isSourcesOpen = false;
            }
        };
        sourcesOverlay = overlay;
        isSourcesOpen = true;
        LinearLayout panel = sourcesOverlay.body;
        TextView status = sourcesOverlay.statusLine();

        List<LocalEngine.Source> sources = api.engine().sources();
        if (sources.isEmpty()) {
            panel.addView(emptyText("还没有媒体源，用下面的按钮添加一个。"), margin(matchWidth(), 0, 0, 0, dp(12)));
        }
        boolean showPaths = api.engine().settings().showRealSourcePath();
        View initialFocus = null;
        for (LocalEngine.Source source : sources) {
            LinearLayout row = new LinearLayout(this);
            row.setOrientation(LinearLayout.VERTICAL);
            row.setClipChildren(false);
            row.setClipToPadding(false);
            row.setBackground(rounded(COLOR_SURFACE_ALT, COLOR_BORDER, dp(1), dp(10)));
            // 卡片整块至少一个列表行高：WebDAV 源名常常只有一行，光靠内容撑不出遥控器要的落点。
            row.setMinimumHeight(sdp(Widgets.HEIGHT_ROW));
            row.setPadding(dp(14), dp(14), dp(14), dp(14));
            panel.addView(row, margin(matchWidth(), 0, 0, 0, dp(10)));

            TextView title = text((source.enabled ? "" : "已停用 · ")
                            + kindLabel(source.kind) + "：" + displaySourceName(source),
                            14, Typeface.BOLD, source.enabled ? COLOR_TEXT : COLOR_SUBTLE);
            title.setMaxLines(1);
            title.setEllipsize(TextUtils.TruncateAt.END);
            row.addView(title, matchWidth());
            if (showPaths) {
                TextView path = text(displaySourcePath(source.kind, source.path),
                        11, Typeface.NORMAL, COLOR_SUBTLE);
                path.setMaxLines(2);
                path.setEllipsize(TextUtils.TruncateAt.END);
                row.addView(path, margin(matchWidth(), 0, dp(3), 0, dp(9)));
            }

            LinearLayout actions = new LinearLayout(this);
            actions.setClipChildren(false);
            actions.setClipToPadding(false);
            row.addView(actions, margin(matchWidth(), 0, dp(10), 0, 0));
            Button toggle = compactButton(source.enabled ? "停用" : "启用");
            Button rename = compactButton("重命名");
            Button remove = compactButton("移除");
            remove.setTextColor(COLOR_DANGER);
            actions.addView(toggle, margin(new LinearLayout.LayoutParams(0, sdp(Widgets.HEIGHT_BUTTON), 1f), 0, 0, dp(6), 0));
            actions.addView(rename, margin(new LinearLayout.LayoutParams(0, sdp(Widgets.HEIGHT_BUTTON), 1f), 0, 0, dp(6), 0));
            actions.addView(remove, new LinearLayout.LayoutParams(0, sdp(Widgets.HEIGHT_BUTTON), 1f));

            toggle.setOnClickListener(view -> {
                api.engine().setSourceEnabled(source.id, !source.enabled);
                renderSourcesOverlay();
            });
            rename.setOnClickListener(view -> openRenameSourceDialog(source, panel, status));
            remove.setOnClickListener(view -> {
                api.engine().removeSource(source.id);
                renderSourcesOverlay();
                refreshHomeLibrary(false);
            });
            if (initialFocus == null) {
                initialFocus = toggle;
            }
        }

        // 局域网设备：打开面板就自己扫，扫到的点一下就能登录。
        LinearLayout lanHeader = new LinearLayout(this);
        lanHeader.setGravity(Gravity.CENTER_VERTICAL);
        lanHeader.setClipChildren(false);
        lanHeader.setClipToPadding(false);
        panel.addView(lanHeader, margin(matchWidth(), 0, 0, 0, dp(12)));
        lanHeader.addView(text("局域网设备（自动扫描）", 14, Typeface.BOLD, COLOR_MUTED),
                new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        Button rescanLan = compactButton("重新扫描");
        rescanLan.setOnClickListener(view -> startLanScan());
        lanHeader.addView(rescanLan, new LinearLayout.LayoutParams(dp(118), dp(42)));
        lanStatus = widgets.caption("正在扫描局域网中的 WebDAV / SMB 设备...", COLOR_SUBTLE);
        panel.addView(lanStatus, margin(matchWidth(), 0, dp(2), 0, dp(6)));
        LinearLayout deviceList = new LinearLayout(this);
        deviceList.setOrientation(LinearLayout.VERTICAL);
        deviceList.setClipChildren(false);
        deviceList.setClipToPadding(false);
        panel.addView(deviceList, margin(matchWidth(), 0, 0, 0, dp(6)));

        // 两种网络媒体源各一颗按钮：小窗里填账号，登录后挑目录，挂完立刻出现在上面的列表里。
        Button addWebDav = compactButton("添加 WebDAV 媒体源");
        Button addSmb = compactButton("添加 SMB 媒体源");
        LinearLayout addRow = new LinearLayout(this);
        addRow.setClipChildren(false);
        addRow.setClipToPadding(false);
        panel.addView(addRow, margin(matchWidth(), 0, 0, 0, 0));
        addRow.addView(addWebDav, new LinearLayout.LayoutParams(0, dp(56), 1f));
        addRow.addView(addSmb, margin(new LinearLayout.LayoutParams(0, dp(56), 1f), dp(8), 0, 0, 0));
        addWebDav.setOnClickListener(view -> openLoginDialog("webdav", "", "", "", ""));
        addSmb.setOnClickListener(view -> openLoginDialog("smb", "", "", "", ""));

        Button wipe = compactButton("清空媒体库索引");
        wipe.setTextColor(COLOR_DANGER);

        // 本地目录也在这个抽屉里加：全屏设置页退场之后，这是唯一入口。
        Button addLocal = compactButton("添加本地目录");
        addLocal.setOnClickListener(view -> openLocalFolderPicker());
        LinearLayout.LayoutParams addLocalParams = margin(matchWidth(), 0, dp(8), 0, 0);
        // 与上面两颗添加按钮同高，别让整颗按钮被内容压成薄薄一条。
        addLocalParams.height = dp(Widgets.HEIGHT_BUTTON);
        panel.addView(addLocal, addLocalParams);
        panel.addView(wipe, margin(matchWidth(), 0, dp(6), 0, 0));
        ((LinearLayout.LayoutParams) wipe.getLayoutParams()).height = dp(52);

        declarePanelRows(sourcesOverlay, panel, initialFocus == null ? addWebDav : initialFocus);
        // 扫描会往面板里插按钮，必须等方向键顺序声明完再接管列表容器。
        attachLanListContainer(deviceList);
    }

    /**
     * 扫描全部媒体源并刮削：进度既写进给定状态行，也同步到首页顶部那行；
     * 首页工具条、媒体源弹窗、挂载新源后的自动扫描都走这一个入口。
     */
    private void startScanAndScrape(TextView status) {
        if (scanRunning) {
            // 启动那一轮可能还在跑（几百个文件 + 刮削要几分钟），再点必须给个回音。
            showToast("扫描刮削正在进行中，海报会陆续出现在首页。");
            homeStatusHiddenMessage = "";
            refreshHomeStatusText();
            return;
        }
        scanRunning = true;
        setScanButtonRunning(true);
        // 手动点了一次就得看到动静：这一轮的收尾文案常常和上一轮一模一样，
        // 不清掉上次的「已收起」记录，横幅会一直藏着，看着就像按钮坏了。
        homeStatusHiddenMessage = "";
        showHomeStatusLine("正在扫描媒体源...");
        if (status != null) {
            status.setText("正在扫描媒体源...");
        }
        runAsync(() -> api.engine().scanAndScrape(throttledProgress(status)),
                report -> {
                    scanRunning = false;
                    setScanButtonRunning(false);
                    if (status != null && status.isAttachedToWindow()) {
                        status.setText(report.message);
                    }
                    refreshHomeStatusText();
                    hideHomeStatusSoon(api.engine().scrapeStatus());
                    refreshHomeLibrary(false);
                },
                error -> {
                    scanRunning = false;
                    setScanButtonRunning(false);
                    if (status != null && status.isAttachedToWindow()) {
                        status.setText("扫描失败：" + error.getMessage());
                    }
                    showToast("扫描失败：" + error.getMessage());
                    refreshHomeStatusText();
                });
    }

    /** 工具条那颗按钮的字：扫描进行中写「扫描中」，有没有在跑一眼看得出来。 */
    private String scanButtonLabel() {
        return scanRunning ? "扫描中" : "扫描刮削";
    }

    private void setScanButtonRunning(boolean running) {
        if (homeScanToggle != null) {
            homeScanToggle.setText(running ? "扫描中" : "扫描刮削");
        }
    }

    /** 往首页横幅上直接写一行（不等引擎的进度回调），写完照常参与去重与自动收起。 */
    private void showHomeStatusLine(String text) {
        if (homeStatusView == null) {
            return;
        }
        homeStatusText = text;
        homeStatusView.setText(text);
        homeStatusView.setVisibility(View.VISIBLE);
    }

    /** 一个媒体源都没有时，墙上自带一个「添加媒体源」的入口，不再另开一屏设置页。 */
    private View emptySourceCallToAction() {
        LinearLayout block = new LinearLayout(this);
        block.setOrientation(LinearLayout.VERTICAL);
        block.setClipChildren(false);
        block.setClipToPadding(false);
        block.addView(emptyText("还没有媒体源。添加本地目录、WebDAV 或 SMB 后会自动扫描刮削。"),
                new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,
                        ViewGroup.LayoutParams.WRAP_CONTENT));
        Button open = button("添加媒体源");
        open.setAllCaps(false);
        open.setOnClickListener(view -> openSourcesOverlay());
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
                sdp(220), sdp(Widgets.HEIGHT_BUTTON));
        params.gravity = Gravity.CENTER_HORIZONTAL;
        params.topMargin = sdp(14);
        block.addView(open, params);
        return block;
    }

    /** Inline rename field for one source row. */
    private void openRenameSourceDialog(LocalEngine.Source source, LinearLayout panel, TextView status) {
        EditText field = input("媒体源名称", source.name);
        panel.addView(field, margin(matchWidth(), 0, dp(6), 0, dp(6)));
        Button save = compactButton("保存名称");
        panel.addView(save, margin(matchWidth(), 0, 0, 0, dp(6)));
        save.setOnClickListener(view -> {
            String value = field.getText().toString().trim();
            if (value.isEmpty()) {
                status.setText("名称不能为空。");
                return;
            }
            api.engine().renameSource(source.id, value);
            renderSourcesOverlay();
        });
        if (sourcesOverlay != null) {
            Overlays.Overlay opened = sourcesOverlay;
            FocusGrid.afterLayout(panel, () -> {
                if (sourcesOverlay == opened) {
                    opened.redeclare(FocusGrid.rowsFrom(panel));
                    FocusGrid.grab(field);
                    FocusGrid.scrollToVisible(field);
                }
            });
        }
    }

    // ------------------------------------------------------------ 首页状态条

    private void refreshHomeStatusText() {
        if (homeStatusView == null) {
            return;
        }
        String message = api.engine().scrapeStatus();
        String text = message == null ? "" : message.trim();
        if (text.isEmpty() && api.engine().scrapeQueueCount() > 0
                && !api.engine().tmdbConfigured()) {
            // mac 版这时候说的是「TMDB 连不上」，不是干挂一个待刮削数字。
            text = "检测到有待刮削的影视，但 TMDB 暂时无法连接。请在设置里检查 API Key 或反代地址。";
        }
        if (text.equals(homeStatusText)) {
            return;
        }
        homeStatusText = text;
        if (text.isEmpty() || text.equals(homeStatusHiddenMessage)) {
            homeStatusView.setVisibility(View.GONE);
            return;
        }
        homeStatusView.setText(text);
        homeStatusView.setVisibility(View.VISIBLE);
    }

    /** 跑完一轮就把那句收尾的话收起来：mac 版的横幅平时不占位置。 */
    private void hideHomeStatusSoon(String shown) {
        main.postDelayed(() -> {
            if (shown.equals(api.engine().scrapeStatus())) {
                homeStatusHiddenMessage = shown;
                refreshHomeStatusText();
            }
        }, 8000);
    }

    /**
     * 引擎一次扫描能发几百条进度，每条都刷横幅、查库、重绘海报，遥控器就是在这儿卡住的。
     * 先在后台线程按时间丢掉多余的，再往主线程投递。
     */
    private LocalEngine.ProgressListener throttledProgress(TextView status) {
        return message -> {
            long now = android.os.SystemClock.elapsedRealtime();
            if (now - lastProgressPaintAt < 250) {
                return;
            }
            lastProgressPaintAt = now;
            main.post(() -> {
                if (status != null && status.isAttachedToWindow()) {
                    status.setText(message);
                }
                refreshHomeStatusText();
                scheduleScrapePosterRefresh();
            });
        };
    }

    /** 启动时自动扫描并同步 + 刮削，与 mac 版一致；状态显示在首页顶部。 */
    private void startStartupSync() {
        if (startupSyncStarted) {
            refreshHomeStatusText();
            return;
        }
        startupSyncStarted = true;
        if (!api.engine().settings().autoScanOnStartup() || api.engine().sources().isEmpty()) {
            refreshHomeStatusText();
            return;
        }
        // 与手动那颗按钮共用一个入口：同一时间只跑一轮，横幅与按钮状态走同一套。
        startScanAndScrape(null);
    }

    private void disconnectFromServer() {
        closeSourcesOverlay();
        closeSettingsOverlay();
        showHome();
    }

    private void startUpdateInstall(Button updateButton, TextView updateStatus) {
        updateButton.setEnabled(false);
        updateStatus.setText("正在检查更新");
        runAsync(
                this::downloadLatestApk,
                apk -> {
                    updateButton.setEnabled(true);
                    updateStatus.setText("已下载，正在打开安装器");
                    installDownloadedApk(apk, updateStatus);
                },
                error -> {
                    updateButton.setEnabled(true);
                    updateStatus.setText("更新失败：" + error.getMessage());
                });
    }

    private File downloadLatestApk() throws IOException, JSONException {
        String downloadUrl = resolveLatestApkDownloadUrl();
        File apk = ApkUpdateProvider.apkFile(this);
        File directory = apk.getParentFile();
        if (directory != null && !directory.exists() && !directory.mkdirs()) {
            throw new IOException("无法创建更新缓存目录。");
        }

        HttpURLConnection connection = (HttpURLConnection) new URL(downloadUrl).openConnection();
        connection.setInstanceFollowRedirects(true);
        connection.setConnectTimeout(12000);
        connection.setReadTimeout(60000);
        connection.setRequestProperty("User-Agent", "OmniPlay-Android/1.5");
        int status = connection.getResponseCode();
        if (status < 200 || status >= 300) {
            connection.disconnect();
            throw new IOException("下载安装包失败：" + status);
        }

        try (InputStream input = connection.getInputStream();
             FileOutputStream output = new FileOutputStream(apk, false)) {
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = input.read(buffer)) >= 0) {
                output.write(buffer, 0, read);
            }
        } finally {
            connection.disconnect();
        }

        if (apk.length() <= 0) {
            throw new IOException("下载安装包为空。");
        }
        return apk;
    }

    private String resolveLatestApkDownloadUrl() throws IOException, JSONException {
        HttpURLConnection connection = (HttpURLConnection) new URL("https://api.github.com/repos/nandieling/OmniPlay/releases/latest").openConnection();
        connection.setConnectTimeout(12000);
        connection.setReadTimeout(30000);
        connection.setRequestProperty("Accept", "application/vnd.github+json");
        connection.setRequestProperty("User-Agent", "OmniPlay-Android/1.5");
        int status = connection.getResponseCode();
        String body;
        try (InputStream input = status >= 400 ? connection.getErrorStream() : connection.getInputStream()) {
            body = readRemoteText(input);
        } finally {
            connection.disconnect();
        }

        if (status < 200 || status >= 300) {
            throw new IOException("检查 GitHub Release 失败：" + status);
        }

        JSONArray assets = new JSONObject(body).optJSONArray("assets");
        String bestUrl = "";
        int bestScore = Integer.MIN_VALUE;
        if (assets != null) {
            for (int index = 0; index < assets.length(); index++) {
                JSONObject asset = assets.optJSONObject(index);
                if (asset == null) {
                    continue;
                }

                String name = asset.optString("name");
                String url = asset.optString("browser_download_url");
                if (name == null || url == null || !name.toLowerCase(Locale.ROOT).endsWith(".apk")) {
                    continue;
                }

                int score = apkAssetScore(name);
                if (score > bestScore) {
                    bestScore = score;
                    bestUrl = url;
                }
            }
        }

        if (bestUrl.isEmpty()) {
            throw new IOException("GitHub 最新 Release 中未找到 APK 安装包。");
        }
        return bestUrl;
    }

    private int apkAssetScore(String name) {
        String normalized = name == null ? "" : name.toLowerCase(Locale.ROOT);
        int score = 0;
        if (normalized.contains("omniplay")) {
            score += 20;
        }
        if (normalized.contains("android")) {
            score += 20;
        }
        if (normalized.contains("tv") || normalized.contains("leanback")) {
            score += 30;
        }
        if (normalized.contains("arm64") || normalized.contains("universal")) {
            score += 10;
        }
        return score;
    }

    private void installDownloadedApk(File apk, TextView updateStatus) {
        if (apk == null || !apk.isFile()) {
            updateStatus.setText("安装包不存在。");
            return;
        }

        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && !getPackageManager().canRequestPackageInstalls()) {
                updateStatus.setText("请允许 OmniPlay 安装未知应用后重试。");
                Intent settings = new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + getPackageName()));
                startActivity(settings);
                return;
            }

            Intent intent = new Intent(Intent.ACTION_VIEW);
            intent.setDataAndType(ApkUpdateProvider.apkUri(this), "application/vnd.android.package-archive");
            intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            startActivity(intent);
        } catch (Exception error) {
            updateStatus.setText("打开安装器失败：" + error.getMessage());
        }
    }

    private static String readRemoteText(InputStream input) throws IOException {
        if (input == null) {
            return "";
        }

        StringBuilder builder = new StringBuilder();
        try (BufferedReader reader = new BufferedReader(new InputStreamReader(input, StandardCharsets.UTF_8))) {
            String line;
            while ((line = reader.readLine()) != null) {
                builder.append(line);
            }
        }
        return builder.toString();
    }

    /**
     * 海报卡片。卡片的数据都收在 {@link PosterViews} 里，刮削过程中可以就地换图换字：
     * 整面墙重建会把焦点和滚动位置一起丢掉，用户看到的就是「闪一下又回到顶上」。
     */
    private View posterCard(Models.LibraryItem item, boolean showProgress) {
        int cardWidth = homePosterCardWidthPx();
        int cardInnerWidth = Math.max(dp(1), cardWidth - sdp(16));
        int posterHeight = Math.round(cardInnerWidth * 1.5f);
        final PosterViews views = new PosterViews(cardInnerWidth, posterHeight, showProgress);
        LinearLayout card = new LinearLayout(this);
        views.card = card;
        card.setOrientation(LinearLayout.VERTICAL);
        card.setFocusable(true);
        card.setClickable(true);
        card.setPadding(sdp(8), sdp(8), sdp(8), sdp(10));
        card.setBackground(rounded(Color.TRANSPARENT, Color.TRANSPARENT, 0, sdp(8)));
        card.setTag(item.id);
        GridLayout.LayoutParams params = new GridLayout.LayoutParams();
        params.width = cardWidth;
        params.height = ViewGroup.LayoutParams.WRAP_CONTENT;
        params.setMargins(sdp(HOME_POSTER_CARD_MARGIN_DP), sdp(5), sdp(HOME_POSTER_CARD_MARGIN_DP), sdp(HOME_POSTER_ROW_BOTTOM_DP));
        card.setLayoutParams(params);

        FrameLayout posterFrame = new FrameLayout(this);
        posterFrame.setPadding(dp(1), dp(1), dp(1), dp(1));
        posterFrame.setClipToOutline(true);
        posterFrame.setBackground(rounded(COLOR_SURFACE_ALT, COLOR_BORDER, dp(1), sdp(12)));
        card.addView(posterFrame, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, posterHeight));
        applyPosterCardFocus(card, posterFrame, () -> scheduleDetailPrefetch(views.item.id, card));

        ImageView poster = new ImageView(this);
        poster.setScaleType(ImageView.ScaleType.CENTER_CROP);
        poster.setClipToOutline(true);
        posterFrame.addView(poster, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        views.poster = poster;

        TextView artFallback = text("", ssp(40), Typeface.BOLD, Theme.withAlpha(COLOR_MUTED, 130));
        artFallback.setGravity(Gravity.CENTER);
        artFallback.setIncludeFontPadding(false);
        artFallback.setVisibility(View.GONE);
        posterFrame.addView(artFallback, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        views.artFallback = artFallback;

        LinearLayout badges = new LinearLayout(this);
        badges.setGravity(Gravity.CENTER_VERTICAL);
        badges.setOrientation(LinearLayout.HORIZONTAL);
        FrameLayout.LayoutParams badgeParams = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.LEFT | Gravity.BOTTOM);
        badgeParams.setMargins(sdp(10), 0, sdp(10), sdp(10));
        posterFrame.addView(badges, badgeParams);
        views.badges = badges;

        FrameLayout progressSlot = new FrameLayout(this);
        progressSlot.setClipChildren(false);
        progressSlot.setClipToPadding(false);
        card.addView(progressSlot, matchWidth());
        views.progress = progressSlot;

        TextView name = text("", ssp(16), Typeface.BOLD, COLOR_TEXT);
        name.setMaxLines(2);
        name.setEllipsize(TextUtils.TruncateAt.END);
        card.addView(name, margin(matchWidth(), 0, sdp(11), 0, 0));
        views.name = name;

        LinearLayout yearRow = new LinearLayout(this);
        yearRow.setGravity(Gravity.CENTER_VERTICAL);
        card.addView(yearRow, margin(matchWidth(), 0, sdp(2), 0, 0));
        TextView year = text("", ssp(13), Typeface.NORMAL, COLOR_SUBTLE);
        yearRow.addView(year, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        views.year = year;
        TextView watched = text("", ssp(13), Typeface.BOLD, COLOR_SUBTLE);
        watched.setGravity(Gravity.CENTER);
        watched.setPadding(sdp(8), 0, sdp(8), 0);
        watched.setMinHeight(sdp(28));
        watched.setFocusable(true);
        watched.setClickable(true);
        watched.setBackgroundColor(Color.TRANSPARENT);
        watched.setOnClickListener(view -> toggleLibraryItemWatched(views.item));
        applyWatchedIconFocus(watched, COLOR_SUBTLE);
        yearRow.addView(watched, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, sdp(30)));
        views.watched = watched;

        card.setOnClickListener(view -> showDetail(views.item.id));
        bindPosterCard(views, item);
        registerPosterViews(views);
        return card;
    }

    /** Refreshes a card that is already on screen with freshly scraped data. */
    private void bindPosterCard(PosterViews views, Models.LibraryItem item) {
        views.item = item;
        String prettyTitle = displayTitle(item.title);
        String year = releaseYearText(item.releaseDate);
        String rating = ratingText(item.voteAverage);
        String doubanRating = ratingText(item.doubanRating);
        int watchedPercent = item.progressPercent();
        // 刮削期间每两秒补一轮海报：内容没变的那张什么都不做。否则 48 张卡一起重设文字、
        // 重挂角标，整面墙重新排版，遥控器正好走在那一刻，手感就是「一卡一卡的」。
        String signature = item.posterAssetId + "\u0001" + prettyTitle + "\u0001" + year
                + "\u0001" + item.isWatched + "\u0001" + watchedPercent
                + "\u0001" + rating + "\u0001" + doubanRating;
        if (signature.equals(views.renderedSignature)) {
            return;
        }
        views.renderedSignature = signature;
        loadPoster(views.poster, item.posterAssetId, views.cardInnerWidth, views.posterHeight);
        boolean missingArt = item.posterAssetId == null || item.posterAssetId.isEmpty();
        views.artFallback.setVisibility(missingArt ? View.VISIBLE : View.GONE);
        views.artFallback.setText(missingArt ? artworkFallbackLetter(prettyTitle) : "");
        views.name.setText(prettyTitle);
        views.year.setText(year == null ? "" : year);
        boolean partlyWatched = !item.isWatched && watchedPercent > 0 && watchedPercent < 95;
        views.watched.setText(item.isWatched ? WATCHED_GLYPH_DONE
                : partlyWatched ? WATCHED_GLYPH_PART : WATCHED_GLYPH_NONE);
        views.watched.setTooltipText(item.isWatched ? "已看" : partlyWatched ? "未看完" : "未看");
        int watchedColor = item.isWatched ? COLOR_FOCUS : partlyWatched ? COLOR_ACCENT : COLOR_SUBTLE;
        views.watched.setTextColor(watchedColor);
        applyWatchedIconFocus(views.watched, watchedColor);
        views.badges.removeAllViews();
        if (rating != null) {
            views.badges.addView(badge(rating, Theme.RATING));
        }
        if (doubanRating != null) {
            views.badges.addView(badge(doubanRating, Theme.RATING_DOUBAN),
                    margin(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, sdp(30)), sdp(5), 0, 0, 0));
        }
        views.progress.removeAllViews();
        if (views.showProgress) {
            int progress = item.progressPercent();
            if (progress > 0 && progress < 95) {
                FrameLayout.LayoutParams barParams =
                        new FrameLayout.LayoutParams(views.cardInnerWidth, sdp(5));
                barParams.topMargin = sdp(9);
                views.progress.addView(progressBar(progress, views.cardInnerWidth, sdp(5)), barParams);
            }
        }
    }

    /** What a poster card needs in order to redraw itself in place. */
    private static final class PosterViews {
        TextView artFallback;
        final int cardInnerWidth;
        final int posterHeight;
        final boolean showProgress;
        LinearLayout card;
        ImageView poster;
        LinearLayout badges;
        TextView name;
        /** 这张卡上一次渲染的内容签名，用于跳过没变化的重绘。 */
        String renderedSignature;
        TextView year;
        TextView watched;
        FrameLayout progress;
        Models.LibraryItem item;

        PosterViews(int cardInnerWidth, int posterHeight, boolean showProgress) {
            this.cardInnerWidth = cardInnerWidth;
            this.posterHeight = posterHeight;
            this.showProgress = showProgress;
        }
    }

    /** 空海报上的那个字：取片名里第一个字母或汉字，跳过引号和括号。 */
    private static String artworkFallbackLetter(String title) {
        if (title == null) {
            return "";
        }
        for (int index = 0; index < title.length(); index++) {
            char c = title.charAt(index);
            if (Character.isLetterOrDigit(c)) {
                return String.valueOf(Character.toUpperCase(c));
            }
        }
        return "";
    }

    private void registerPosterViews(PosterViews views) {
        List<PosterViews> holders = homePosterViews.get(views.item.id);
        if (holders == null) {
            holders = new ArrayList<>();
            homePosterViews.put(views.item.id, holders);
        }
        holders.add(views);
    }

    private String focusedPosterItemId() {
        View focused = getCurrentFocus();
        while (focused != null) {
            Object tag = focused.getTag();
            if (tag instanceof String && homePosterViews.containsKey(tag)) {
                return (String) tag;
            }
            ViewParent parent = focused.getParent();
            focused = parent instanceof View ? (View) parent : null;
        }
        return null;
    }

    /**
     * 刮削进度回调很密，这里按秒限流刷新海报：条目没变就只补数据，条目增减了才重建，
     * 重建时把滚动位置和焦点都补回来。
     */
    /** 整面墙的重建合并成最多两秒一次：条目数一直在变，每出一张就重建一次太贵。 */
    private void scheduleWallRebuild() {
        if (pendingWallRebuild != null) {
            return;
        }
        pendingWallRebuild = () -> {
            pendingWallRebuild = null;
            if (screen == Screen.HOME) {
                refreshHomeBody();
            }
        };
        main.postDelayed(pendingWallRebuild, 2000);
    }

    private void scheduleScrapePosterRefresh() {
        if (android.os.SystemClock.elapsedRealtime() - lastLibraryPatchAt < 2000) {
            return;
        }
        refreshLibraryInPlace();
    }

    /** 重读媒体库并就地补卡片；条目增减了才重建整面墙。 */
    private void refreshLibraryInPlace() {
        if (libraryPatchInFlight) {
            return;
        }
        lastLibraryPatchAt = android.os.SystemClock.elapsedRealtime();
        libraryPatchInFlight = true;
        runAsync(() -> api.getLibraryItems(), items -> {
            libraryPatchInFlight = false;
            if (screen != Screen.HOME || items == null || items.isEmpty()) {
                return;
            }
            patchLibraryViews(new ArrayList<>(items));
        }, error -> libraryPatchInFlight = false);
    }

    private void patchLibraryViews(List<Models.LibraryItem> items) {
        Map<String, Models.LibraryItem> byId = new HashMap<>();
        for (Models.LibraryItem item : items) {
            byId.put(item.id, item);
        }
        // 只比数量的话，「换了一批同数量条目」（手动匹配跨类型改绑、清库重扫）会被当成
        // 能就地更新，结果一张卡都对不上号，墙上就永远留着旧名字。
        if (!byId.keySet().equals(distinctLibraryIds(libraryItems))) {
            libraryItems = items;
            scheduleWallRebuild();
            return;
        }
        for (Map.Entry<String, List<PosterViews>> entry : homePosterViews.entrySet()) {
            Models.LibraryItem fresh = byId.get(entry.getKey());
            if (fresh == null) {
                continue;
            }
            for (PosterViews views : entry.getValue()) {
                bindPosterCard(views, fresh);
            }
        }
        libraryItems = items;
        refreshHomeStatusText();
        android.util.Log.d("OmniPlayLibrary", "刮削中刷新：就地更新 " + homePosterViews.size() + " 张海报");
    }

    private static java.util.Set<String> distinctLibraryIds(List<Models.LibraryItem> items) {
        java.util.Set<String> ids = new java.util.HashSet<>();
        for (Models.LibraryItem item : items) {
            ids.add(item.id);
        }
        return ids;
    }

    private void showDetail(String itemId) {
        selectedSeasonId = "";
        loadDetail(itemId);
    }

    private void loadDetail(String itemId) {
        screen = Screen.DETAIL;
        applyImmersiveMode(false);
        requestedDetailItemId = itemId;
        Models.LibraryDetail cached = detailCache.get(itemId);
        if (cached != null) {
            renderDetail(cached);
            refreshDetail(itemId, false);
            return;
        }

        showLoading("正在进入详情");
        refreshDetail(itemId, true);
    }

    private void refreshDetail(String itemId, boolean showErrors) {
        runAsync(
                () -> api.getLibraryItemDetail(itemId),
                detail -> {
                    if (detail == null) {
                        return;
                    }
                    detailCache.put(itemId, detail);
                    refreshPlaybackSettingsForDetail(detail);
                    if (screen == Screen.DETAIL && itemId.equals(requestedDetailItemId)) {
                        renderDetail(detail);
                    }
                },
                error -> {
                    if (showErrors) {
                        renderError(error.getMessage(), this::showHome);
                    }
                });
    }

    private void refreshPlaybackSettingsForDetail(Models.LibraryDetail detail) {
        runAsync(
                () -> api.getSettings(),
                settings -> {
                    boolean nextShowEpisodeDetails = settings == null || settings.playback == null || settings.playback.showEpisodeDetails;
                    if (showEpisodeDetails != nextShowEpisodeDetails) {
                        showEpisodeDetails = nextShowEpisodeDetails;
                        if (screen == Screen.DETAIL && currentDetail != null && currentDetail.id.equals(detail.id)) {
                            renderDetail(currentDetail);
                        }
                    }
                },
                error -> {
                });
    }

    private void scheduleDetailPrefetch(String itemId, View focusedView) {
        if (itemId == null || itemId.isEmpty() || detailCache.containsKey(itemId)) {
            return;
        }
        main.postDelayed(() -> {
            if (focusedView.hasFocus()) {
                prefetchDetail(itemId);
            }
        }, 220);
    }

    private void prefetchDetail(String itemId) {
        if (itemId == null || itemId.isEmpty() || detailCache.containsKey(itemId) || pendingDetailPrefetches.size() >= 2) {
            return;
        }
        if (!pendingDetailPrefetches.add(itemId)) {
            return;
        }
        runAsync(
                () -> api.getLibraryItemDetail(itemId),
                detail -> {
                    pendingDetailPrefetches.remove(itemId);
                    if (detail != null) {
                        detailCache.put(itemId, detail);
                    }
                },
                error -> pendingDetailPrefetches.remove(itemId));
    }

    private void renderDetail(Models.LibraryDetail detail) {
        screen = Screen.DETAIL;
        applyImmersiveMode(false);
        currentDetail = detail;
        closeAllOverlays();
        root.removeAllViews();
        applyAppBackground();
        addDetailBackdrop(detail.posterAssetId);

        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(sdp(52), sdp(34), sdp(52), sdp(42));
        page.setClipChildren(false);
        page.setClipToPadding(false);
        root.addView(page, match());

        ScrollView scroll = new ScrollView(this);
        scroll.setFocusable(false);
        scroll.setFocusableInTouchMode(false);
        scroll.setClipChildren(false);
        scroll.setClipToPadding(false);
        page.addView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f));

        LinearLayout content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setClipChildren(false);
        content.setClipToPadding(false);
        scroll.addView(content, matchWidth());

        FrameLayout heroFrame = new FrameLayout(this);
        heroFrame.setClipChildren(false);
        heroFrame.setClipToPadding(false);
        content.addView(heroFrame, margin(matchWidth(), 0, 0, 0, sdp(40)));

        LinearLayout hero = new LinearLayout(this);
        hero.setGravity(Gravity.TOP);
        hero.setClipChildren(false);
        hero.setClipToPadding(false);
        heroFrame.addView(hero, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        FrameLayout posterFrame = new FrameLayout(this);
        posterFrame.setPadding(0, 0, 0, 0);
        posterFrame.setClipToOutline(true);
        posterFrame.setBackground(rounded(COLOR_SURFACE_ALT, COLOR_BORDER, dp(1), sdp(12)));
        int detailPosterWidth = sdp(DETAIL_POSTER_WIDTH_DP);
        int detailPosterHeight = sdp(DETAIL_POSTER_HEIGHT_DP);
        hero.addView(posterFrame, new LinearLayout.LayoutParams(detailPosterWidth, detailPosterHeight));

        ImageView poster = new ImageView(this);
        poster.setScaleType(ImageView.ScaleType.CENTER_CROP);
        poster.setClipToOutline(true);
        posterFrame.addView(poster, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        loadPoster(poster, detail.posterAssetId, detailPosterWidth, detailPosterHeight);

        LinearLayout info = new LinearLayout(this);
        info.setOrientation(LinearLayout.VERTICAL);
        info.setClipChildren(false);
        info.setClipToPadding(false);
        hero.addView(info, margin(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f), sdp(34), 0, 0, 0));

        TextView heading = text(displayTitle(detail.title), ssp(38), Typeface.BOLD, COLOR_TEXT);
        heading.setLineSpacing(sdp(2), 1f);
        info.addView(heading);

        // 元信息行：年份、TMDB / 豆瓣评分、类型与制片地区，顺序与 mac 版一致。
        LinearLayout facts = new LinearLayout(this);
        facts.setGravity(Gravity.CENTER_VERTICAL);
        info.addView(facts, margin(matchWidth(), 0, sdp(14), 0, 0));
        String year = preferredReleaseYear(detail);
        if (year != null) {
            facts.addView(metaPill(year));
        }
        String rating = ratingText(detail.voteAverage);
        if (rating != null) {
            facts.addView(ratingPill("TMDB \u2605 " + rating, Theme.RATING));
        }
        String doubanRating = ratingText(detail.doubanRating);
        if (doubanRating != null) {
            facts.addView(ratingPill("\u8c46\u74e3 \u2605 " + doubanRating, Theme.RATING_DOUBAN));
        }
        for (String part : detailMetadataParts(detail)) {
            facts.addView(metaPill(part), margin(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, sdp(34)), sdp(9), 0, 0, 0));
        }

        LinearLayout credits = new LinearLayout(this);
        credits.setOrientation(LinearLayout.VERTICAL);
        credits.setClipChildren(false);
        credits.setClipToPadding(false);
        info.addView(credits, margin(matchWidth(), 0, sdp(12), 0, 0));
        addCreditRow(credits, "\u5bfc\u6f14", joinNames(detail.directors));
        addCreditRow(credits, "\u4e3b\u6f14", joinNames(detail.casts.size() > 6
                ? detail.casts.subList(0, 6) : detail.casts));

        String overviewText = detailOverviewText(detail);
        TextView overview = text(overviewText.isEmpty() ? "\u6682\u65e0\u7b80\u4ecb" : overviewText,
                ssp(16), Typeface.NORMAL, overviewText.isEmpty() ? COLOR_SUBTLE : COLOR_TEXT);
        overview.setLineSpacing(sdp(4), 1f);
        overview.setMaxLines(5);
        overview.setEllipsize(TextUtils.TruncateAt.END);
        info.addView(overview, margin(matchWidth(), 0, sdp(16), 0, 0));
        if (overviewText.length() > 96) {
            Button toggleOverview = compactButton("\u5c55\u5f00\u7b80\u4ecb");
            toggleOverview.setOnClickListener(view -> {
                boolean expanded = overview.getMaxLines() == Integer.MAX_VALUE;
                overview.setMaxLines(expanded ? 5 : Integer.MAX_VALUE);
                toggleOverview.setText(expanded ? "\u5c55\u5f00\u7b80\u4ecb" : "\u6536\u8d77\u7b80\u4ecb");
            });
            info.addView(toggleOverview, margin(new LinearLayout.LayoutParams(sdp(160), sdp(48)), 0, sdp(10), 0, 0));
        }

        Models.VideoFile mainFile = resolveMainFile(detail);
        Button playButton = null;
        if (mainFile != null) {
            LinearLayout actions = new LinearLayout(this);
            actions.setGravity(Gravity.CENTER_VERTICAL);
            actions.setClipChildren(false);
            actions.setClipToPadding(false);
            info.addView(actions, margin(matchWidth(), 0, sdp(22), 0, 0));

            Button play = button(playButtonText(mainFile, "tv".equals(detail.itemKind)));
            playButton = play;
            play.setTag(mainFile);
            actions.addView(play, new LinearLayout.LayoutParams(sdp(252), sdp(58)));
            play.setOnClickListener(view -> {
                Object file = view.getTag();
                if (file instanceof Models.VideoFile) {
                    playFile((Models.VideoFile) file);
                }
            });
            play.requestFocus();

            Button watchStatus = button(watchedStatusLabel(mainFile), isEffectivelyWatched(mainFile));
            actions.addView(watchStatus, margin(new LinearLayout.LayoutParams(sdp(150), sdp(52)), sdp(12), 0, 0, 0));
            watchStatus.setOnClickListener(view -> toggleVideoWatched(mainFile));

            // 匹配与资料改在第二行：多一颗按钮就不进第一行了，遥控器左右走位也更短。
            LinearLayout metadataActions = new LinearLayout(this);
            metadataActions.setGravity(Gravity.CENTER_VERTICAL);
            metadataActions.setClipChildren(false);
            metadataActions.setClipToPadding(false);
            info.addView(metadataActions, margin(matchWidth(), 0, sdp(12), 0, 0));

            Button manualMatch = button("手动匹配");
            metadataActions.addView(manualMatch, new LinearLayout.LayoutParams(sdp(150), sdp(52)));
            manualMatch.setOnClickListener(view -> openManualMatchOverlay(detail, mainFile));

            Button edit = button("自定义编辑");
            metadataActions.addView(edit, margin(new LinearLayout.LayoutParams(sdp(150), sdp(52)), sdp(12), 0, 0, 0));
            edit.setOnClickListener(view -> openDetailCustomEditOverlay(detail));

            // \u5df2\u64ad\u65f6\u957f / \u603b\u65f6\u957f\uff1amac \u7248\u628a\u5b83\u653e\u5728\u6309\u94ae\u4e0b\u65b9\uff0c\u8fd9\u91cc\u540c\u4e00\u4f4d\u7f6e\u3002
            LinearLayout timelineColumn = new LinearLayout(this);
            timelineColumn.setOrientation(LinearLayout.VERTICAL);
            timelineColumn.setClipChildren(false);
            timelineColumn.setClipToPadding(false);
            info.addView(timelineColumn, margin(matchWidth(), 0, sdp(16), 0, 0));
            timelineColumn.addView(timeline(detail, mainFile),
                    new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, sdp(40)));

            refreshSubtitleCacheStatus(detail, mainFile);

            TextView subtitleStatus = subtitleCacheStatusView(mainFile);
            if (subtitleStatus != null) {
                subtitleStatus.setGravity(Gravity.CENTER);
                FrameLayout.LayoutParams subtitleParams = new FrameLayout.LayoutParams(
                        ViewGroup.LayoutParams.WRAP_CONTENT,
                        sdp(34),
                        Gravity.TOP | Gravity.RIGHT);
                subtitleParams.setMargins(0, sdp(2), sdp(4), 0);
                heroFrame.addView(subtitleStatus, subtitleParams);
            }
        }

        if ("tv".equals(detail.itemKind) && !detail.seasons.isEmpty()) {
            renderEpisodes(content, detail, mainFile, playButton);
        } else if ("movie".equals(detail.itemKind)) {
            renderMovieVersions(content, detail);
        }
    }

    private void renderMovieVersions(LinearLayout content, Models.LibraryDetail detail) {
        ArrayList<Models.VideoFile> files = moviePlaybackFiles(detail);
        if (files.isEmpty()) {
            return;
        }

        Models.VideoFile mainFile = resolveMainFile(detail);
        TextView heading = text("版本 / 文件（" + files.size() + "）", ssp(20), Typeface.BOLD, COLOR_TEXT);
        content.addView(heading, margin(matchWidth(), 0, sdp(24), 0, sdp(12)));
        for (int index = 0; index < files.size(); index++) {
            Models.VideoFile file = files.get(index);
            boolean selected = mainFile != null && mainFile.id.equals(file.id);
            Button version = button("版本 " + (index + 1) + " \u00b7 "
                    + (file.fileName == null || file.fileName.isEmpty() ? file.relativePath : file.fileName)
                    + (selected ? "（当前）" : ""));
            version.setAllCaps(false);
            version.setGravity(Gravity.LEFT | Gravity.CENTER_VERTICAL);
            version.setSingleLine(true);
            version.setEllipsize(TextUtils.TruncateAt.END);
            version.setTag(file);
            version.setOnClickListener(view -> {
                Object tag = view.getTag();
                if (tag instanceof Models.VideoFile) {
                    playFile((Models.VideoFile) tag);
                }
            });
            content.addView(version, margin(matchWidth(), 0, sdp(8), 0, 0));
            content.addView(text(versionPathLine(file), ssp(13), Typeface.NORMAL, COLOR_SUBTLE),
                    margin(matchWidth(), 0, sdp(16), 0, sdp(2)));
            content.addView(text(versionTimingLine(file), ssp(13), Typeface.NORMAL, COLOR_SUBTLE),
                    margin(matchWidth(), 0, sdp(16), 0, sdp(6)));
        }
    }

    private void renderEpisodes(LinearLayout content, Models.LibraryDetail detail, Models.VideoFile mainFile, Button playButton) {
        if (!hasSelectedSeason(detail)) {
            selectedSeasonId = defaultSeasonId(detail, mainFile);
        }

        Models.Season selectedSeason = selectedSeason(detail);
        if (selectedSeason == null) {
            return;
        }

        LinearLayout seasonHeader = new LinearLayout(this);
        seasonHeader.setGravity(Gravity.CENTER_VERTICAL);
        seasonHeader.setClipChildren(false);
        seasonHeader.setClipToPadding(false);
        content.addView(seasonHeader, margin(matchWidth(), 0, sdp(20), 0, sdp(20)));

        for (Models.Season season : detail.seasons) {
            Button seasonButton = button(seasonDisplayLabel(season), season.id.equals(selectedSeason.id));
            seasonHeader.addView(seasonButton, margin(new LinearLayout.LayoutParams(sdp(136), sdp(52)), 0, 0, sdp(12), 0));
            seasonButton.setOnClickListener(view -> {
                selectedSeasonId = season.id;
                renderDetail(detail);
            });
        }

        maybeScrapeEpisodeMetadata(detail, selectedSeason);

        GridLayout episodeGrid = new GridLayout(this);
        episodeGrid.setColumnCount(detailEpisodeColumnCount());
        episodeGrid.setClipChildren(false);
        episodeGrid.setClipToPadding(false);
        content.addView(episodeGrid);

        for (Models.Episode episode : selectedSeason.episodes) {
            episodeGrid.addView(episodeCard(episode, detail.posterAssetId, playButton));
        }
    }

    /** 评分胶囊：和 mac 版一样只用颜色区分 TMDB 与豆瓣。 */
    private TextView ratingPill(String value, int color) {
        TextView pill = widgets.metaPill(value, color);
        pill.setTypeface(Typeface.DEFAULT_BOLD);
        return pill;
    }

    /** 豆瓣年份优先，其次 TMDB 上映日，和 mac 版的 preferredYear 一致。 */
    private String preferredReleaseYear(Models.LibraryDetail detail) {
        if (detail == null) {
            return null;
        }
        if (detail.douban != null && detail.douban.year != null && !detail.douban.year.trim().isEmpty()) {
            return detail.douban.year.trim();
        }
        return releaseYearText(detail.releaseDate);
    }

    /** 类型 / 制片地区：优先 TMDB 的 genres，缺失时回落到豆瓣元数据。 */
    private List<String> detailMetadataParts(Models.LibraryDetail detail) {
        ArrayList<String> parts = new ArrayList<>();
        if (detail != null && detail.genres != null) {
            for (String genre : detail.genres) {
                if (parts.size() >= 3) {
                    break;
                }
                if (genre != null && !genre.trim().isEmpty()) {
                    parts.add(genre.trim());
                }
            }
        }
        for (String part : doubanMetadataParts(detail == null ? null : detail.douban)) {
            if (parts.size() >= 5 || parts.contains(part)) {
                continue;
            }
            parts.add(part);
        }
        return parts;
    }

    private String joinNames(List<String> values) {
        if (values == null || values.isEmpty()) {
            return "";
        }
        ArrayList<String> cleaned = new ArrayList<>();
        for (String value : values) {
            if (value != null && !value.trim().isEmpty()) {
                cleaned.add(value.trim());
            }
        }
        return join(cleaned, "、");
    }

    /** 导演 / 主演 一行：左侧固定标签列，右侧取值，与 mac 版同构。 */
    private void addCreditRow(LinearLayout parent, String label, String value) {
        if (value == null || value.isEmpty()) {
            return;
        }
        LinearLayout row = new LinearLayout(this);
        row.setGravity(Gravity.TOP);
        parent.addView(row, margin(matchWidth(), 0, sdp(6), 0, 0));
        row.addView(text(label, ssp(14), Typeface.BOLD, COLOR_SUBTLE),
                new LinearLayout.LayoutParams(sdp(46), ViewGroup.LayoutParams.WRAP_CONTENT));
        TextView valueView = text(value, ssp(15), Typeface.NORMAL, COLOR_TEXT);
        valueView.setMaxLines(2);
        valueView.setEllipsize(TextUtils.TruncateAt.END);
        valueView.setLineSpacing(sdp(2), 1f);
        row.addView(valueView, matchWidth());
    }

    /**
     * mac 版详情页的海报底图是毛玻璃。Android 12+ 有 RenderEffect，直接虚化海报；
     * 更早的系统没有便宜的实时虚化，退化成一层与主题同色的柔和底。
     */
    private void addDetailBackdrop(String posterAssetId) {
        if (Build.VERSION.SDK_INT >= 31 && posterAssetId != null) {
            ImageView backdrop = new ImageView(this);
            backdrop.setScaleType(ImageView.ScaleType.CENTER_CROP);
            root.addView(backdrop, match());
            loadPoster(backdrop, posterAssetId,
                    getResources().getDisplayMetrics().widthPixels / 2,
                    getResources().getDisplayMetrics().heightPixels / 2);
            backdrop.setRenderEffect(android.graphics.RenderEffect.createBlurEffect(
                    dp(48), dp(48), android.graphics.Shader.TileMode.CLAMP));
            backdrop.setAlpha(Theme.DARK ? 0.5f : 0.62f);
        }
        View wash = new View(this);
        wash.setBackgroundColor(Theme.withAlpha(Theme.BACKGROUND, Theme.DARK ? 170 : 148));
        root.addView(wash, match());
    }

    /** 版本行第二行：相对路径 + 封装 / 编码摘要。 */
    private String versionPathLine(Models.VideoFile file) {
        String path = file.relativePath == null || file.relativePath.isEmpty()
                ? "\u672a\u77e5\u8def\u5f84" : file.relativePath;
        String summary = file.mediaSummary();
        return summary.isEmpty() ? path : path + " \u00b7 " + summary;
    }

    /** 版本行第三行：已播时间 / 总时长，加一个播放状态词。 */
    private String versionTimingLine(Models.VideoFile file) {
        String total = file.durationSeconds > 0 ? formatPlaybackTime(file.durationSeconds) : "--:--";
        return formatPlaybackTime(Math.max(0, file.positionSeconds)) + " / " + total
                + " \u00b7 " + (isEffectivelyWatched(file) ? "\u5df2\u64ad\u5b8c"
                : file.positionSeconds > 5 ? "\u7ee7\u7eed\u64ad\u653e" : "\u672a\u64ad\u653e");
    }

    /** Loads TMDB episode entries for the opened season so stills and blurbs appear. */
    private void maybeScrapeEpisodeMetadata(Models.LibraryDetail detail, Models.Season season) {
        if (detail == null || season == null || !"tv".equals(detail.itemKind)) {
            return;
        }
        final String key = detail.id + "#" + season.seasonNumber;
        if (episodeMetaAttempts.contains(key) || !api.engine().needsEpisodeMetadata(detail.id, season.seasonNumber)) {
            return;
        }
        episodeMetaAttempts.add(key);
        runAsync(() -> api.engine().scrapeEpisodeMetadata(detail.id, season.seasonNumber, null),
                message -> {
                    if (screen == Screen.DETAIL && currentDetail != null && currentDetail.id.equals(detail.id)) {
                        detailCache.remove(detail.id);
                        refreshDetail(detail.id, false);
                    }
                },
                error -> episodeMetaAttempts.remove(key));
    }

    // ---- 手机输入：遥控器打字太慢时让手机浏览器代填 ----

    /**
     * Shows the pairing QR + URL and registers these fields as the current submit targets.
     * Calling it again while the panel is visible only swaps the registered fields, so any
     * overlay can offer the same entry point.
     */
    private void openPhoneInputOverlay(String title, String subtitle,
                                       List<PhoneInputServer.Field> fields, boolean allowNote) {
        if (phoneInput == null) {
            phoneInput = PhoneInputServer.shared(api.engine().settings());
        }
        phoneInput.start();
        if (!phoneInput.isRunning()) {
            showToast("本机输入服务启动失败，请重试。");
            return;
        }
        if (phoneInputOverlay == null) {
            isPhoneInputOpen = true;
            Overlays.Options options = new Overlays.Options();
            options.title = title;
            options.subtitle = subtitle == null ? "" : subtitle;
            options.widthDp = 470;
            options.scrollable = false;
            options.restoreFocus = getCurrentFocus();
            Overlays.Overlay overlay = overlays.open(options);
            overlay.onClose = () -> {
                if (phoneInputOverlay == overlay) {
                    phoneInputOverlay = null;
                    isPhoneInputOpen = false;
                }
            };
            phoneInputOverlay = overlay;
            LinearLayout panel = phoneInputOverlay.body;

            phoneInputQr = new ImageView(this);
            phoneInputQr.setScaleType(ImageView.ScaleType.FIT_CENTER);
            phoneInputQr.setBackgroundColor(Color.WHITE);
            phoneInputQr.setPadding(dp(10), dp(10), dp(10), dp(10));
            LinearLayout.LayoutParams qrParams = new LinearLayout.LayoutParams(dp(320), dp(320));
            qrParams.gravity = Gravity.CENTER_HORIZONTAL;
            panel.addView(phoneInputQr, qrParams);

            phoneInputUrl = text("", ssp(Widgets.TEXT_LABEL), Typeface.BOLD, COLOR_FOCUS);
            phoneInputUrl.setGravity(Gravity.CENTER);
            phoneInputUrl.setTypeface(Typeface.MONOSPACE);
            panel.addView(phoneInputUrl, margin(matchWidth(), 0, sdp(10), 0, 0));

            phoneInputStatus = widgets.caption("", COLOR_MUTED);
            phoneInputStatus.setGravity(Gravity.CENTER);
            panel.addView(phoneInputStatus, margin(matchWidth(), 0, sdp(6), 0, sdp(10)));

            phoneInputCloseButton = button("关闭");
            phoneInputCloseButton.setOnClickListener(view -> closePhoneInputOverlay());
            panel.addView(phoneInputCloseButton, margin(matchWidth(), 0, sdp(2), 0, 0));

            phoneInputOverlay.focus(phoneInputCloseButton);
        }
        phoneInput.setSession(new PhoneInputServer.Session(title, subtitle, fields, allowNote),
                (applied, from) -> {
                    TextView status = phoneInputStatus;
                    if (!isPhoneInputOpen || status == null) {
                        return;
                    }
                    status.setText(applied > 0
                            ? "已填入 " + applied + " 项（来自 " + from + "），可以回电视继续。"
                            : "手机提交了空内容，没有改动任何输入框。");
                });
        refreshPhoneInputTarget();
    }

    /** Refreshes the QR + URL and explains it when the box has no LAN address yet. */
    private void refreshPhoneInputTarget() {
        if (phoneInputQr == null || phoneInputUrl == null || phoneInputStatus == null) {
            return;
        }
        String url = phoneInput == null ? null : phoneInput.pageUrl();
        if (url == null) {
            phoneInputQr.setImageDrawable(null);
            phoneInputUrl.setText("电视当前没有局域网地址");
            phoneInputStatus.setText("请先让电视连上网络，手机与电视需在同一个局域网。");
            return;
        }
        phoneInputQr.setImageBitmap(QrCode.render(url, dp(300)));
        phoneInputUrl.setText(url);
        phoneInputStatus.setText("手机与电视需连同一个局域网：扫码或直接输入上面的地址，提交后电视上的输入框立即更新。");
    }

    private void closePhoneInputOverlay() {
        if (phoneInputOverlay == null) {
            return;
        }
        Overlays.Overlay closing = phoneInputOverlay;
        phoneInputOverlay = null;
        isPhoneInputOpen = false;
        if (phoneInput != null) {
            phoneInput.clearSession();
        }
        phoneInputQr = null;
        phoneInputUrl = null;
        phoneInputStatus = null;
        phoneInputCloseButton = null;
        closing.close();
    }

    /** Current text of an input, tolerating a dialog that was already torn down. */
    private static String currentValue(EditText target) {
        return target == null ? "" : target.getText().toString();
    }

    /**
     * Field for the phone page bound to a TV input by reference, so a panel rebuilt in the
     * meantime still receives the submitted text.
     */
    private static PhoneInputServer.Field boundField(String key, String label,
                                                    Supplier<EditText> getter, boolean secret) {
        return new PhoneInputServer.Field(key, label, currentValue(getter.get()), secret, value -> {
            EditText target = getter.get();
            if (target != null) {
                target.setText(value);
            }
        });
    }

    /** Small text editor overlay used by 编辑片名、分集副标题 and 缓存目录. */
    /** Small text editor overlay used by 编辑片名、分集副标题 and 缓存目录. */
    private void openTextInputOverlay(String heading, String initial, Success<String> onConfirm) {
        Overlays.Options options = new Overlays.Options();
        options.title = heading;
        options.widthDp = 460;
        options.scrollable = false;
        options.restoreFocus = getCurrentFocus();
        Overlays.Overlay opened = overlays.open(options);
        opened.onClose = () -> {
            if (textInputOverlay == opened) {
                textInputOverlay = null;
            }
        };
        textInputOverlay = opened;
        LinearLayout panel = textInputOverlay.body;

        EditText field = input(heading, initial == null ? "" : initial);
        panel.addView(field, margin(matchWidth(), 0, sdp(2), 0, 0));

        Button save = button("保存");
        Button phone = button("手机输入");
        phone.setOnClickListener(view -> openPhoneInputOverlay(heading,
                "长串内容可以粘贴在“补充内容”里整段发送。",
                Collections.singletonList(new PhoneInputServer.Field("value", heading,
                        currentValue(field), false, value -> field.setText(value))), true));
        Button cancel = button("取消");
        overlays.footer(panel, save, phone, cancel);
        Overlays.Overlay overlay = textInputOverlay;
        cancel.setOnClickListener(view -> {
            if (textInputOverlay == overlay) {
                textInputOverlay = null;
            }
            overlay.close();
        });
        save.setOnClickListener(view -> {
            String value = field.getText().toString();
            if (textInputOverlay == overlay) {
                textInputOverlay = null;
            }
            overlay.close();
            onConfirm.accept(value);
        });

        textInputOverlay.focus(Arrays.asList(Collections.singletonList(field),
                Arrays.asList(save, phone, cancel)), field);
    }

    // ------------------------------------------- 详情页：手动匹配 / 自定义编辑

    private Overlays.Overlay manualMatchOverlay;
    private EditText manualMatchQuery;
    private EditText manualMatchYear;
    private TextView manualMatchStatus;
    private LinearLayout manualMatchResults;
    private boolean manualMatchRunning;

    private Overlays.Overlay customEditOverlay;
    private EditText customEditTitle;
    private EditText customEditDate;
    private EditText customEditVote;
    private EditText customEditGenres;
    private EditText customEditDirectors;
    private EditText customEditCasts;
    private TextView customEditStatus;

    /**
     * 详情页「手动匹配」：mac 版那个手动匹配弹层的遥控器版——源文件路径 + 关键词 + 年份 +
     * 候选清单，点一行就关联。弹层一打开就按当前片名搜一次，遥控器上少一步打字。
     */
    private void openManualMatchOverlay(Models.LibraryDetail detail, Models.VideoFile mainFile) {
        if (!api.engine().tmdbConfigured()) {
            showToast("先在设置里填写 TMDB API Key 或反代地址，才能手动搜索。");
            return;
        }
        if (manualMatchOverlay != null) {
            return;
        }
        Overlays.Options options = new Overlays.Options();
        options.title = "手动匹配";
        options.subtitle = "点中正确的那条即可关联；关联后这条记录会被锁定，自动刮削不再覆盖它。";
        options.widthDp = 620;
        options.restoreFocus = getCurrentFocus();
        Overlays.Overlay overlay = overlays.open(options);
        overlay.onClose = () -> {
            if (manualMatchOverlay == overlay) {
                manualMatchOverlay = null;
            }
            manualMatchQuery = null;
            manualMatchYear = null;
            manualMatchStatus = null;
            manualMatchResults = null;
            manualMatchRunning = false;
        };
        manualMatchOverlay = overlay;
        LinearLayout panel = overlay.body;

        String source = mainFile == null ? "" : mainFile.relativePath == null || mainFile.relativePath.isEmpty()
                ? (mainFile.fileName == null ? "" : mainFile.fileName) : mainFile.relativePath;
        if (!source.isEmpty()) {
            panel.addView(widgets.caption("源文件：" + source, COLOR_SUBTLE),
                    margin(matchWidth(), 0, 0, 0, sdp(6)));
        }
        manualMatchQuery = input("影视名称关键词", detail.title == null ? "" : detail.title);
        panel.addView(manualMatchQuery, margin(matchWidth(), 0, 0, 0, sdp(4)));
        manualMatchYear = input("年份（可选）", fourDigitYear(detail.releaseDate));
        panel.addView(manualMatchYear, matchWidth());
        manualMatchStatus = overlay.statusLine();
        manualMatchResults = new LinearLayout(this);
        manualMatchResults.setOrientation(LinearLayout.VERTICAL);
        manualMatchResults.setClipChildren(false);
        manualMatchResults.setClipToPadding(false);
        panel.addView(manualMatchResults, margin(matchWidth(), 0, 0, 0, 0));

        Button close = button("关闭");
        close.setOnClickListener(view -> overlay.close());
        Button phone = button("手机输入");
        phone.setOnClickListener(view -> openPhoneInputOverlay("手动匹配",
                "只填需要改的项，留空的项不会被改动。",
                Arrays.asList(
                        boundField("query", "影视名称关键词", () -> manualMatchQuery, false),
                        boundField("year", "年份（可选）", () -> manualMatchYear, false)),
                true));
        Button search = button("搜索");
        search.setOnClickListener(view -> runManualMatchSearch(detail));
        overlays.footer(panel, close, phone, search);

        declarePanelRows(overlay, panel, manualMatchQuery);
        runManualMatchSearch(detail);
    }

    /** 手动匹配的搜索：候选清单按匹配分排序，与自动刮削同一套打分。 */
    private void runManualMatchSearch(Models.LibraryDetail detail) {
        if (manualMatchOverlay == null || manualMatchRunning || manualMatchQuery == null) {
            return;
        }
        String query = manualMatchQuery.getText().toString().trim();
        if (query.isEmpty()) {
            manualMatchStatus.setText("请先填写影视名称关键词。");
            return;
        }
        String year = manualMatchYear.getText().toString().trim();
        String mediaType = detail.itemKind;
        manualMatchRunning = true;
        manualMatchStatus.setText("正在向 TMDB 请求数据...");
        runAsync(() -> api.engine().searchCandidates(query, year, mediaType),
                results -> {
                    manualMatchRunning = false;
                    if (manualMatchOverlay == null || manualMatchResults == null) {
                        return;
                    }
                    manualMatchResults.removeAllViews();
                    if (results.isEmpty()) {
                        manualMatchStatus.setText("未找到与「" + query + "」相关的影视。");
                    } else {
                        manualMatchStatus.setText("找到 " + results.size() + " 条结果，点一行关联到这条影视。");
                        for (TmdbClient.Result result : results) {
                            manualMatchResults.addView(manualMatchRow(detail, result), listItemParams());
                        }
                    }
                    manualMatchOverlay.redeclare(FocusGrid.rowsFrom(manualMatchOverlay.body));
                },
                error -> {
                    manualMatchRunning = false;
                    if (manualMatchStatus != null) {
                        manualMatchStatus.setText("搜索失败：" + error.getMessage());
                    }
                });
    }

    private Button manualMatchRow(Models.LibraryDetail detail, TmdbClient.Result result) {
        String year = fourDigitYear(result.releaseDate);
        String kind = "tv".equals(result.mediaType) ? "剧集" : "电影";
        String blurb = result.overview == null ? "" : result.overview.trim().replace('\n', ' ');
        if (blurb.length() > 46) {
            blurb = blurb.substring(0, 46) + "...";
        }
        Button row = listRowButton(result.title,
                (year.isEmpty() ? kind : year + " · " + kind) + (blurb.isEmpty() ? "" : " · " + blurb));
        row.setOnClickListener(view -> bindManualMatch(detail, result));
        return row;
    }

    /** 关联：写进实体、补齐导演 / 主演、锁定，然后详情页与首页都按新数据重画。 */
    private void bindManualMatch(Models.LibraryDetail detail, TmdbClient.Result result) {
        if (manualMatchOverlay == null || manualMatchStatus == null) {
            return;
        }
        manualMatchStatus.setText("正在关联《" + result.title + "》...");
        String mediaType = detail.itemKind;
        long entityId = entityIdOf(detail.id);
        runAsync(() -> api.engine().bindToTmdbResult(mediaType, entityId, result),
                targetId -> {
                    Overlays.Overlay closing = manualMatchOverlay;
                    manualMatchOverlay = null;
                    if (closing != null) {
                        closing.close();
                    }
                    detailCache.clear();
                    showToast("已关联《" + result.title + "》");
                    // 跨类型改绑（电影 ↔ 剧集）会换一张表重建实体，条目 id 跟着变，只能整页重开。
                    showDetail(result.mediaType + ":" + targetId);
                    refreshHomeLibrary(false);
                },
                error -> {
                    if (manualMatchStatus != null) {
                        manualMatchStatus.setText("关联失败：" + error.getMessage());
                    }
                });
    }

    /**
     * 详情页「自定义编辑」：片名 / 上映日期 / 评分 / 类型 / 导演 / 主演都能手填，保存即锁定，
     * 对应 mac 版手动编辑资料里的「保存修改并锁定」。
     */
    private void openDetailCustomEditOverlay(Models.LibraryDetail detail) {
        if (customEditOverlay != null) {
            return;
        }
        Overlays.Options options = new Overlays.Options();
        options.title = "自定义编辑";
        options.subtitle = "手填的资料会锁定这条记录，之后的自动刮削不再覆盖它；名单用顿号或逗号分隔。";
        options.widthDp = 560;
        options.restoreFocus = getCurrentFocus();
        Overlays.Overlay overlay = overlays.open(options);
        overlay.onClose = () -> {
            if (customEditOverlay == overlay) {
                customEditOverlay = null;
            }
            customEditTitle = null;
            customEditDate = null;
            customEditVote = null;
            customEditGenres = null;
            customEditDirectors = null;
            customEditCasts = null;
            customEditStatus = null;
        };
        customEditOverlay = overlay;
        LinearLayout panel = overlay.body;
        customEditStatus = overlay.statusLine();

        customEditTitle = input("影视名称", detail.title == null ? "" : detail.title);
        panel.addView(customEditTitle, margin(matchWidth(), 0, 0, 0, sdp(4)));
        customEditDate = input("上映日期，例如 2001-05-09", detail.releaseDate == null ? "" : detail.releaseDate);
        panel.addView(customEditDate, margin(matchWidth(), 0, 0, 0, sdp(4)));
        customEditVote = input("TMDB 评分（0.0 - 10.0，留空不改）",
                detail.voteAverage == null || detail.voteAverage <= 0 ? "" : String.valueOf(detail.voteAverage));
        customEditVote.setInputType(InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_FLAG_DECIMAL);
        panel.addView(customEditVote, margin(matchWidth(), 0, 0, 0, sdp(4)));
        customEditGenres = input("类型", joinByComma(detail.genres));
        panel.addView(customEditGenres, margin(matchWidth(), 0, 0, 0, sdp(4)));
        customEditDirectors = input("导演", joinByComma(detail.directors));
        panel.addView(customEditDirectors, margin(matchWidth(), 0, 0, 0, sdp(4)));
        customEditCasts = input("主演", joinByComma(detail.casts));
        panel.addView(customEditCasts, matchWidth());

        Button cancel = button("取消");
        cancel.setOnClickListener(view -> overlay.close());
        Button phone = button("手机输入");
        phone.setOnClickListener(view -> openPhoneInputOverlay("自定义编辑",
                "名单用顿号或逗号分隔；留空的项不会被改动。",
                Arrays.asList(
                        boundField("title", "影视名称", () -> customEditTitle, false),
                        boundField("date", "上映日期", () -> customEditDate, false),
                        boundField("vote", "TMDB 评分", () -> customEditVote, false),
                        boundField("genres", "类型", () -> customEditGenres, false),
                        boundField("directors", "导演", () -> customEditDirectors, false),
                        boundField("casts", "主演", () -> customEditCasts, false)),
                true));
        Button save = button("保存修改并锁定");
        save.setOnClickListener(view -> saveCustomEdit(detail));
        overlays.footer(panel, cancel, phone, save);
        declarePanelRows(overlay, panel, customEditTitle);
    }

    private void saveCustomEdit(Models.LibraryDetail detail) {
        if (customEditOverlay == null) {
            return;
        }
        String typedVote = currentValue(customEditVote).trim();
        double parsedVote = 0;
        if (!typedVote.isEmpty()) {
            try {
                parsedVote = Math.max(0, Math.min(10, Double.parseDouble(typedVote)));
            } catch (NumberFormatException invalid) {
                if (customEditStatus != null) {
                    customEditStatus.setText("评分要填 0.0 到 10.0 之间的数字。");
                }
                return;
            }
        }
        final double vote = parsedVote;
        String title = currentValue(customEditTitle).trim();
        String date = currentValue(customEditDate).trim();
        String genres = toPipeList(currentValue(customEditGenres));
        String directors = toPipeList(currentValue(customEditDirectors));
        String casts = toPipeList(currentValue(customEditCasts));
        Overlays.Overlay closing = customEditOverlay;
        customEditOverlay = null;
        closing.close();
        runAsync(() -> {
            api.engine().library().applyCustomEdit(detail.itemKind, entityIdOf(detail.id),
                    title, date, vote, genres, directors, casts);
            return Boolean.TRUE;
        }, ignored -> {
            detailCache.clear();
            refreshDetail(detail.id, false);
            refreshHomeLibrary(false);
            showToast("资料已保存并锁定");
        }, error -> showToast("保存失败：" + error.getMessage()));
    }

    /** 日期或年份字符串里取那四位年份，取不到就留空。 */
    private static String fourDigitYear(String value) {
        String text = value == null ? "" : value.trim();
        if (text.length() < 4) {
            return "";
        }
        String head = text.substring(0, 4);
        for (int index = 0; index < 4; index++) {
            if (!Character.isDigit(head.charAt(index))) {
                return "";
            }
        }
        return head;
    }

    private static String joinByComma(List<String> values) {
        StringBuilder builder = new StringBuilder();
        for (String value : values) {
            if (builder.length() > 0) {
                builder.append('\u3001');
            }
            builder.append(value);
        }
        return builder.toString();
    }

    /** 用户在弹层里用顿号 / 逗号 / 分号分隔名单，入库前统一换成库里那套竖线分隔。 */
    private static String toPipeList(String value) {
        String text = value == null ? "" : value.trim();
        if (text.isEmpty()) {
            return "";
        }
        StringBuilder builder = new StringBuilder();
        for (String part : text.split("[|,，、;；\n]")) {
            String piece = part.trim();
            if (piece.isEmpty()) {
                continue;
            }
            if (builder.length() > 0) {
                builder.append('|');
            }
            builder.append(piece);
        }
        return builder.toString();
    }

    private static long entityIdOf(String itemId) {
        int colon = itemId.indexOf(':');
        try {
            return Long.parseLong(itemId.substring(colon + 1));
        } catch (RuntimeException ignored) {
            return 0;
        }
    }

    private View episodeCard(Models.Episode episode, String posterAssetId, Button playButton) {
        LinearLayout card = new LinearLayout(this);
        boolean showDetails = showEpisodeDetails;
        Models.VideoFile playbackFile = episodePlaybackFile(episode);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setFocusable(playbackFile != null);
        card.setClickable(playbackFile != null);
        card.setClipChildren(false);
        card.setClipToPadding(false);
        card.setPadding(0, 0, 0, dp(10));
        card.setBackground(rounded(Color.TRANSPARENT, Color.TRANSPARENT, 0, dp(8)));
        GridLayout.LayoutParams params = new GridLayout.LayoutParams();
        int episodeCardWidth = sdp(EPISODE_CARD_WIDTH_DP);
        int episodeStillHeight = sdp(EPISODE_STILL_HEIGHT_DP);
        params.width = episodeCardWidth;
        params.height = ViewGroup.LayoutParams.WRAP_CONTENT;
        params.setMargins(0, sdp(8), sdp(22), sdp(24));
        card.setLayoutParams(params);

        FrameLayout stillFrame = new FrameLayout(this);
        stillFrame.setPadding(0, 0, 0, 0);
        stillFrame.setClipToOutline(true);
        stillFrame.setBackground(rounded(COLOR_SURFACE_ALT, COLOR_BORDER, dp(1), sdp(12)));
        card.addView(stillFrame, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, episodeStillHeight));
        applyEpisodeStillFocus(card, stillFrame, showDetails, playButton, playbackFile);

        ImageView still = new ImageView(this);
        still.setScaleType(ImageView.ScaleType.CENTER_CROP);
        still.setClipToOutline(true);
        stillFrame.addView(still, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        if (episode.stillAssetId != null) {
            imageLoader.load(still, api.thumbnailUrl(episode.stillAssetId), api.cookieHeader(), episodeCardWidth, episodeStillHeight);
        } else {
            loadPoster(still, posterAssetId, episodeCardWidth, episodeStillHeight);
        }

        TextView title = text(episodeTitleDisplay(episode, showDetails), ssp(15), Typeface.BOLD, COLOR_TEXT);
        title.setMaxLines(2);
        title.setEllipsize(TextUtils.TruncateAt.END);
        title.setGravity(Gravity.CENTER);
        card.addView(title, margin(matchWidth(), 0, sdp(12), 0, 0));

        String facts = episodeFacts(episode,
                !episodeTitleDisplay(episode, showDetails).contains(
                        episodeDisplayLabel(episode.seasonNumber, episode.episodeNumber)));
        if (showDetails && !facts.isEmpty()) {
            TextView factView = text(facts, ssp(13), Typeface.NORMAL, COLOR_SUBTLE);
            factView.setSingleLine(true);
            factView.setEllipsize(TextUtils.TruncateAt.END);
            factView.setGravity(Gravity.CENTER);
            card.addView(factView, margin(matchWidth(), 0, sdp(4), 0, 0));
        }
        if (showDetails && episode.overview != null && !episode.overview.isEmpty()) {
            TextView overview = text(episode.overview, ssp(13), Typeface.NORMAL, COLOR_SUBTLE);
            overview.setMaxLines(2);
            overview.setEllipsize(TextUtils.TruncateAt.END);
            overview.setGravity(Gravity.CENTER);
            card.addView(overview, margin(matchWidth(), 0, sdp(7), 0, 0));
        }

        if (playbackFile != null) {
            int progress = fileProgressPercent(playbackFile);
            if (showDetails && progress > 0 && progress < 95) {
                int progressWidth = Math.max(dp(1), episodeCardWidth - sdp(28));
                card.addView(progressBar(progress, progressWidth, sdp(4)), margin(new LinearLayout.LayoutParams(progressWidth, sdp(4)), 0, sdp(12), 0, 0));
            }
            card.setOnClickListener(view -> playFile(playbackFile));
        }

        if (playbackFile != null) {
            LinearLayout episodeActions = new LinearLayout(this);
            episodeActions.setClipChildren(false);
            episodeActions.setClipToPadding(false);
            card.addView(episodeActions, margin(matchWidth(), 0, sdp(6), 0, 0));
            Button episodeSubtitle = compactButton("改副标题");
            episodeActions.addView(episodeSubtitle, new LinearLayout.LayoutParams(0, sdp(44), 1f));
            episodeSubtitle.setOnClickListener(view -> openTextInputOverlay("分集副标题（留空则显示 TMDB 集名）",
                    episode.subtitleOverride == null ? "" : episode.subtitleOverride, value -> {
                        runAsync(() -> {
                            api.engine().library().setEpisodeSubtitle(playbackFile.id, value);
                            return Boolean.TRUE;
                        }, ignored -> {
                            if (currentDetail != null && screen == Screen.DETAIL) {
                                detailCache.remove(currentDetail.id);
                                refreshDetail(currentDetail.id, false);
                            }
                        }, error -> {
                        });
                    }));
        }

        List<Models.VideoFile> versions = episodeVideoFiles(episode);
        if (versions.size() > 1) {
            TextView versionHeading = text("版本 / 文件（" + versions.size() + "）", ssp(12), Typeface.BOLD, COLOR_SUBTLE);
            card.addView(versionHeading, margin(matchWidth(), 0, sdp(4), 0, sdp(8)));
            for (int index = 0; index < versions.size(); index++) {
                Models.VideoFile versionFile = versions.get(index);
                Button versionButton = compactButton("版本 " + (index + 1) + " · " + versionFile.fileName);
                versionButton.setAllCaps(false);
                versionButton.setGravity(Gravity.LEFT | Gravity.CENTER_VERTICAL);
                versionButton.setTag(versionFile.withEpisodeLabel(episodeDisplayLabel(episode.seasonNumber, episode.episodeNumber)));
                versionButton.setOnClickListener(view -> {
                    Object tag = view.getTag();
                    if (tag instanceof Models.VideoFile) {
                        playFile((Models.VideoFile) tag);
                    }
                });
                card.addView(versionButton, margin(matchWidth(), 0, sdp(4), 0, 0));
            }
        }

        return card;
    }

    private void playFile(Models.VideoFile file) {
        List<Models.VideoFile> queue = playbackQueueFor(file);
        int index = indexOfVideoFile(queue, file.id);
        playFile(file, queue, Math.max(0, index));
    }

    private void playFile(Models.VideoFile file, List<Models.VideoFile> queue, int queueIndex) {
        screen = Screen.PLAYER;
        closeAllOverlays();
        root.removeAllViews();
        root.setBackgroundColor(Color.BLACK);
        root.setFocusable(true);
        root.setFocusableInTouchMode(false);
        root.setOnKeyListener((view, keyCode, event) ->
                event.getAction() == KeyEvent.ACTION_DOWN &&
                        screen == Screen.PLAYER &&
                        playerView != null &&
                        handlePlayerKeyDown(keyCode, event));
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

        activeDirectPlayback = shouldUseDirectPlayback(file);
        selectedAudioTrackOrdinal = -1;
        selectedEmbeddedSubtitleOrdinal = -1;
        selectedRuntimeAudioId = null;
        selectedRuntimeSubtitleId = null;
        selectedExternalSubtitleId = null;
        pendingEmbeddedSubtitleOrdinal = -1;
        pendingRuntimeSubtitleId = null;
        pendingExternalSubtitleId = null;
        activeRuntimeAudioTracks = Collections.emptyList();
        activeRuntimeSubtitleTracks = Collections.emptyList();
        subtitleLoadStartedAtMs = 0;
        lastKnownPlaybackPositionSeconds = 0;
        lastKnownPlaybackDurationSeconds = 0;
        playerView = new MpvVideoView(this, activeDirectPlayback);
        root.addView(playerView, match());
        playerView.setOnKeyListener((view, keyCode, event) ->
                event.getAction() == KeyEvent.ACTION_DOWN &&
                        screen == Screen.PLAYER &&
                        playerView != null &&
                        handlePlayerKeyDown(keyCode, event));
        playerPaused = false;

        LinearLayout overlay = new LinearLayout(this);
        overlay.setOrientation(LinearLayout.VERTICAL);
        overlay.setPadding(sdp(46), sdp(34), sdp(46), sdp(30));
        overlay.setBackgroundColor(Color.argb(108, 0, 0, 0));
        overlay.addView(text(file.label(), ssp(22), Typeface.BOLD, COLOR_PLAYER_TEXT));
        String summary = file.mediaSummary();
        if (!summary.isEmpty()) {
            overlay.addView(text(summary, ssp(14), Typeface.NORMAL, COLOR_PLAYER_MUTED));
        }
        TextView status = text(initialPlaybackStatus(file), ssp(14), Typeface.BOLD, COLOR_PLAYER_MUTED);
        playerStatusText = status;
        overlay.addView(status, margin(matchWidth(), 0, sdp(8), 0, 0));
        FrameLayout.LayoutParams overlayParams = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP);
        root.addView(overlay, overlayParams);
        overlay.setElevation(sdp(24));
        overlay.setTranslationZ(sdp(24));
        overlay.bringToFront();
        playerTopOverlay = overlay;

        TextView subtitles = text("", ssp(29), Typeface.BOLD, Color.WHITE);
        subtitles.setGravity(Gravity.CENTER);
        subtitles.setShadowLayer(sdp(4), 0, sdp(1), Color.BLACK);
        subtitles.setMaxLines(3);
        subtitles.setIncludeFontPadding(true);
        subtitles.setVisibility(View.GONE);
        FrameLayout.LayoutParams subtitleParams = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL);
        subtitleParams.setMargins(sdp(120), 0, sdp(120), sdp(154));
        root.addView(subtitles, subtitleParams);
        subtitles.setElevation(sdp(30));
        subtitles.setTranslationZ(sdp(30));
        subtitles.bringToFront();
        playerSubtitleOverlay = subtitles;

        PgsSubtitleView pgsSubtitles = new PgsSubtitleView(this);
        root.addView(pgsSubtitles, match());
        pgsSubtitles.setElevation(sdp(30));
        pgsSubtitles.setTranslationZ(sdp(30));
        pgsSubtitles.bringToFront();
        playerPgsSubtitleOverlay = pgsSubtitles;

        LinearLayout controls = playerControls(file);
        FrameLayout.LayoutParams controlParams = new FrameLayout.LayoutParams(sdp(1020), ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL);
        controlParams.setMargins(0, 0, 0, sdp(34));
        root.addView(controls, controlParams);
        controls.setElevation(sdp(28));
        controls.setTranslationZ(sdp(28));
        controls.bringToFront();
        playerControlsPanel = controls;

        activePlaybackFile = file;
        activePlaybackQueue = queue == null || queue.isEmpty() ? Collections.singletonList(file) : new ArrayList<>(queue);
        activePlaybackIndex = Math.max(0, Math.min(queueIndex, activePlaybackQueue.size() - 1));
        advancingPlaybackPart = false;
        if (file.playbackUrl == null || file.playbackUrl.isEmpty()) {
            renderPlayerError("播放地址未配置，请重新扫描媒体源。");
            return;
        }
        runAsync(
                () -> resolvePlayback(file),
                playback -> startResolvedPlayback(file, playback, status),
                error -> renderPlayerError(error.getMessage()));
        if (playerPlayPauseButton != null) {
            playerPlayPauseButton.requestFocus();
        }
    }

    private ResolvedPlayback resolvePlayback(Models.VideoFile file) throws Exception {
        String playbackUrl = file.playbackUrl;
        if (playbackUrl == null || playbackUrl.isEmpty()) {
            return null;
        }
        Models.AppSettings settings;
        try {
            settings = api.getSettings();
        } catch (Exception ignored) {
            settings = Models.AppSettings.defaults();
        }
        Models.PlaybackFileStreams streams = null;
        boolean iso = isIsoFile(file);
        String finalUrl = playbackUrl;
        if (playbackUrl.startsWith("smb://")) {
            finalUrl = SmbStreamServer.shared().prepare(api.engine().smbClientForUrl(playbackUrl), playbackUrl);
        }
        return new ResolvedPlayback(finalUrl, "", "", settings, streams, "", "", iso && playbackUrl.startsWith("file://"));
    }

    private String resolveIsoPlaybackUrl(String streamUrl, String cookieHeader) throws IOException {
        try {
            String proxyUrl = RemoteIsoStreamServer.shared().prepare(streamUrl, cookieHeader);
            if (proxyUrl != null && !proxyUrl.isEmpty()) {
                return proxyUrl;
            }
        } catch (Exception error) {
            throw new IOException("客户端 ISO Range 代理失败，未向 mpv 回退原始 ISO 字节流。", error);
        }
        throw new IOException("客户端 ISO Range 代理没有返回播放地址。");
    }

    private Models.PlaybackFileStreams resolvePlaybackStreams(String videoFileId) {
        try {
            return api.getPlaybackStreams(videoFileId);
        } catch (Exception ignored) {
            return null;
        }
    }

    private void startResolvedPlayback(Models.VideoFile file, ResolvedPlayback playback, TextView status) {
        if (screen != Screen.PLAYER || playerView == null || activePlaybackFile != file) {
            return;
        }

        if (playback == null || playback.url == null || playback.url.isEmpty()) {
            renderPlayerError("播放地址为空。");
            return;
        }

        Models.VideoFile playbackFile = fileWithResolvedStreams(file, playback.streams);
        activePlaybackFile = playbackFile;
        replaceActivePlaybackQueueFile(playbackFile);
        boolean resolvedDirectPlayback = shouldUseDirectPlayback(playbackFile);
        if (resolvedDirectPlayback != activeDirectPlayback) {
            replacePlayerViewForPlaybackMode(resolvedDirectPlayback);
        }
        status.setText("正在开始播放");
        activePlaybackTicket = playback.ticket;
        activeHlsSessionId = playback.hlsSessionId;
        playerLoadStartedAtMs = System.currentTimeMillis();
        final boolean[] playbackErrorShown = {false};
        playerView.setPlaybackListener(new MpvVideoView.PlaybackListener() {
            private boolean didStart;

            @Override
            public void onLoaded() {
                if (didStart || screen != Screen.PLAYER || playerView == null || activePlaybackFile != playbackFile) {
                    return;
                }

                didStart = true;
                refreshRuntimeTracks();
                scheduleResumeSeek(playbackFile);
                status.setText(playbackStatusText(playbackFile, playback));
                applyDefaultPlaybackTracks(playbackFile, playback.settings);
                scheduleRuntimeTrackRefresh(playbackFile, playback.settings);
                scheduleDefaultSubtitleRetries(playbackFile, playback.settings);
                startPlayerUiUpdates(playbackFile);
                startProgressUpdates(playbackFile);
                showPlayerChromeTemporarily();
            }

            @Override
            public void onError(String message) {
                if (screen == Screen.PLAYER && activePlaybackFile == playbackFile) {
                    playbackErrorShown[0] = true;
                    renderPlayerError(message);
                }
            }
        });
        boolean accepted = playerView.play(playback.url, playback.cookieHeader, playbackStartSeconds(playbackFile), playback.isoDevicePlayback);
        if (!accepted && !playbackErrorShown[0]) {
            renderPlayerError(playerView.lastError());
        }
    }

    private void replacePlayerViewForPlaybackMode(boolean directPlaybackMode) {
        if (playerView == null) {
            activeDirectPlayback = directPlaybackMode;
            playerView = new MpvVideoView(this, activeDirectPlayback);
            root.addView(playerView, 0, match());
        } else {
            root.removeView(playerView);
            playerView.destroyPlayer();
            activeDirectPlayback = directPlaybackMode;
            playerView = new MpvVideoView(this, activeDirectPlayback);
            root.addView(playerView, 0, match());
        }
        playerView.setOnKeyListener((view, keyCode, event) ->
                event.getAction() == KeyEvent.ACTION_DOWN &&
                        screen == Screen.PLAYER &&
                        playerView != null &&
                        handlePlayerKeyDown(keyCode, event));
    }

    private Models.VideoFile fileWithResolvedStreams(Models.VideoFile file, Models.PlaybackFileStreams streams) {
        if (streams == null) {
            return file;
        }

        boolean hasAudioTracks = !streams.audioTracks.isEmpty();
        boolean hasSubtitleStreams = !streams.subtitleStreams.isEmpty();
        if (!hasAudioTracks && !hasSubtitleStreams) {
            return file;
        }

        return file.withStreams(
                hasAudioTracks ? streams.audioTracks : file.audioTracks,
                hasSubtitleStreams ? streams.subtitleStreams : file.subtitleStreams);
    }

    private void replaceActivePlaybackQueueFile(Models.VideoFile playbackFile) {
        if (playbackFile == null || activePlaybackQueue == null || activePlaybackQueue.isEmpty()) {
            return;
        }

        ArrayList<Models.VideoFile> queue = new ArrayList<>(activePlaybackQueue);
        for (int index = 0; index < queue.size(); index++) {
            Models.VideoFile item = queue.get(index);
            if (item != null && playbackFile.id.equals(item.id)) {
                queue.set(index, playbackFile);
                if (index == activePlaybackIndex) {
                    activePlaybackIndex = index;
                }
                break;
            }
        }
        activePlaybackQueue = queue;
    }

    private void scheduleResumeSeek(Models.VideoFile file) {
        double targetSeconds = playbackStartSeconds(file);
        if (targetSeconds <= 5) {
            return;
        }

        main.postDelayed(() -> {
            if (screen == Screen.PLAYER && playerView != null && activePlaybackFile == file && playerView.currentTimeSeconds() < Math.max(0, targetSeconds - 3)) {
                playerView.seekTo(targetSeconds);
            }
        }, 350);
        main.postDelayed(() -> {
            if (screen == Screen.PLAYER && playerView != null && activePlaybackFile == file && playerView.currentTimeSeconds() < Math.max(0, targetSeconds - 3)) {
                playerView.seekTo(targetSeconds);
            }
        }, 1600);
    }

    private LinearLayout playerControls(Models.VideoFile file) {
        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(sdp(24), sdp(14), sdp(24), sdp(16));
        panel.setClipChildren(false);
        panel.setClipToPadding(false);
        panel.setBackground(rounded(Theme.withAlpha(theme.isDark() ? COLOR_SURFACE : Color.BLACK, 176),
                Color.argb(82, 255, 255, 255), dp(1), sdp(18)));

        FrameLayout buttonRow = new FrameLayout(this);
        buttonRow.setClipChildren(false);
        buttonRow.setClipToPadding(false);
        panel.addView(buttonRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, sdp(54)));

        ImageButton pause = playerIconButton(R.drawable.ic_player_pause);
        pause.setId(View.generateViewId());
        pause.setContentDescription("播放/暂停");
        playerPlayPauseButton = pause;
        FrameLayout.LayoutParams pauseParams = new FrameLayout.LayoutParams(sdp(66), sdp(50), Gravity.CENTER);
        buttonRow.addView(pause, pauseParams);
        pause.setOnClickListener(view -> {
            togglePlayerPaused();
            showPlayerChromeTemporarily();
        });

        LinearLayout tools = new LinearLayout(this);
        tools.setGravity(Gravity.RIGHT | Gravity.CENTER_VERTICAL);
        tools.setClipChildren(false);
        tools.setClipToPadding(false);
        FrameLayout.LayoutParams toolParams = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.MATCH_PARENT, Gravity.RIGHT | Gravity.CENTER_VERTICAL);
        buttonRow.addView(tools, toolParams);

        ImageButton audio = playerIconButton(R.drawable.ic_player_audio);
        audio.setId(View.generateViewId());
        audio.setContentDescription("音轨");
        playerAudioButton = audio;
        tools.addView(audio, new LinearLayout.LayoutParams(sdp(66), sdp(50)));
        audio.setOnClickListener(view -> showAudioMenu(activePlaybackFile == null ? file : activePlaybackFile, audio));

        ImageButton subtitle = playerIconButton(R.drawable.ic_player_subtitle);
        subtitle.setId(View.generateViewId());
        subtitle.setContentDescription("字幕");
        playerSubtitleButton = subtitle;
        tools.addView(subtitle, margin(new LinearLayout.LayoutParams(sdp(66), sdp(50)), sdp(9), 0, 0, 0));
        subtitle.setOnClickListener(view -> showSubtitleMenu(activePlaybackFile == null ? file : activePlaybackFile, subtitle));

        pause.setNextFocusRightId(audio.getId());
        audio.setNextFocusLeftId(pause.getId());
        audio.setNextFocusRightId(subtitle.getId());
        subtitle.setNextFocusLeftId(audio.getId());

        LinearLayout progressRow = new LinearLayout(this);
        progressRow.setGravity(Gravity.CENTER_VERTICAL);
        panel.addView(progressRow, margin(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, sdp(38)), 0, sdp(10), 0, 0));

        playerCurrentTime = text("0:00", ssp(13), Typeface.BOLD, COLOR_PLAYER_MUTED);
        playerCurrentTime.setGravity(Gravity.RIGHT | Gravity.CENTER_VERTICAL);
        progressRow.addView(playerCurrentTime, new LinearLayout.LayoutParams(sdp(78), ViewGroup.LayoutParams.MATCH_PARENT));

        playerProgressTrack = new FrameLayout(this);
        playerProgressTrack.setBackground(rounded(Color.argb(118, 255, 255, 255), Color.TRANSPARENT, 0, sdp(4)));
        playerProgressFill = new View(this);
        playerProgressFill.setBackground(rounded(COLOR_ACCENT, Color.TRANSPARENT, 0, sdp(4)));
        playerProgressTrack.addView(playerProgressFill, new FrameLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT));
        progressRow.addView(playerProgressTrack, margin(new LinearLayout.LayoutParams(0, sdp(7), 1f), sdp(14), 0, sdp(14), 0));

        playerTotalTime = text("--:--", ssp(13), Typeface.BOLD, COLOR_PLAYER_MUTED);
        playerTotalTime.setGravity(Gravity.LEFT | Gravity.CENTER_VERTICAL);
        progressRow.addView(playerTotalTime, new LinearLayout.LayoutParams(sdp(78), ViewGroup.LayoutParams.MATCH_PARENT));

        return panel;
    }

    private void startPlayerUiUpdates(Models.VideoFile file) {
        stopPlayerUiUpdates();
        playerUiUpdater = new Runnable() {
            @Override
            public void run() {
                if (screen != Screen.PLAYER || playerView == null || activePlaybackFile != file) {
                    return;
                }

                updatePlayerUi(file);
                main.postDelayed(this, 1000);
            }
        };
        playerUiUpdater.run();
    }

    private void stopPlayerUiUpdates() {
        if (playerUiUpdater != null) {
            main.removeCallbacks(playerUiUpdater);
            playerUiUpdater = null;
        }
    }

    private void updatePlayerUi(Models.VideoFile file) {
        double duration = playerView == null ? 0 : playerView.durationSeconds();
        if (duration <= 0) {
            duration = file.durationSeconds;
        }
        double position = resolvePlayerPosition(file, duration);
        PlaybackTimeline timeline = livePlaybackTimeline(file, position, duration);
        rememberPlaybackPosition(file, position, duration);
        syncKeepScreenOn();

        if (playerCurrentTime != null) {
            playerCurrentTime.setText(formatPlaybackTime(timeline.positionSeconds));
        }
        if (playerTotalTime != null) {
            playerTotalTime.setText(timeline.durationSeconds > 0 ? formatPlaybackTime(timeline.durationSeconds) : "--:--");
        }
        if (playerProgressTrack != null && playerProgressFill != null) {
            int width = playerProgressTrack.getWidth();
            int fillWidth = timeline.durationSeconds <= 0 || width <= 0 ? 0 : Math.max(0, Math.min(width, (int) Math.round(width * timeline.positionSeconds / timeline.durationSeconds)));
            ViewGroup.LayoutParams params = playerProgressFill.getLayoutParams();
            if (params.width != fillWidth) {
                params.width = fillWidth;
                playerProgressFill.setLayoutParams(params);
            }
            updatePlayerProgressSegments(timeline.durationSeconds, position, duration);
        }
        maybeAdvanceMoviePart(file, position, duration);
        if (playerStatusText != null) {
            if (position > 0 || duration > 0) {
                playerStatusText.setText(activePlaybackStatusText(file));
            } else {
                long elapsed = playerLoadStartedAtMs <= 0 ? 0 : System.currentTimeMillis() - playerLoadStartedAtMs;
                playerStatusText.setText(elapsed >= 8000 ? "正在等待视频输出" : "正在打开视频流");
            }
        }
    }

    private double resolvePlayerPosition(Models.VideoFile file, double duration) {
        if (playerSeekLongPressActive && playerSeekPreviewPosition >= 0) {
            return duration > 0 ? Math.min(duration, playerSeekPreviewPosition) : playerSeekPreviewPosition;
        }
        double position = playerView == null ? 0 : playerView.currentTimeSeconds();
        if (position > 0) {
            return position;
        }

        double percent = playerView == null ? -1 : playerView.percentPosition();
        if (percent > 0 && duration > 0) {
            return duration * percent / 100d;
        }

        double remaining = playerView == null ? -1 : playerView.remainingTimeSeconds();
        if (remaining >= 0 && duration > 0 && remaining <= duration) {
            double inferred = duration - remaining;
            if (inferred > 0) {
                return inferred;
            }
        }

        return Math.max(0, position);
    }

    private void updatePlayerProgressSegments(double totalDuration, double currentPosition, double currentDuration) {
        if (playerProgressTrack == null) {
            return;
        }

        for (View segmentView : playerProgressSegmentViews) {
            playerProgressTrack.removeView(segmentView);
        }
        playerProgressSegmentViews.clear();

        if (activePlaybackQueue == null || activePlaybackQueue.size() <= 1 || activePlaybackIndex < 0 || totalDuration <= 0) {
            return;
        }

        int trackWidth = playerProgressTrack.getWidth();
        if (trackWidth <= 0) {
            return;
        }

        double cursor = 0;
        for (int index = 0; index < activePlaybackQueue.size() - 1; index++) {
            cursor += playbackPartDurationSeconds(index, currentPosition, currentDuration);
            if (cursor <= 0 || cursor >= totalDuration) {
                continue;
            }

            View divider = new View(this);
            divider.setBackgroundColor(Color.argb(230, 255, 255, 255));
            int dividerWidth = dp(2);
            int left = Math.max(0, Math.min(trackWidth - dividerWidth, (int) Math.round(trackWidth * cursor / totalDuration) - dividerWidth / 2));
            FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(dividerWidth, ViewGroup.LayoutParams.MATCH_PARENT);
            params.leftMargin = left;
            playerProgressTrack.addView(divider, params);
            playerProgressSegmentViews.add(divider);
        }
    }

    private double playbackPartDurationSeconds(int index, double currentPosition, double currentDuration) {
        if (activePlaybackQueue == null || index < 0 || index >= activePlaybackQueue.size()) {
            return 0;
        }

        Models.VideoFile part = activePlaybackQueue.get(index);
        return index == activePlaybackIndex
                ? Math.max(0, Math.max(part.durationSeconds, Math.max(currentDuration, currentPosition)))
                : Math.max(0, part.durationSeconds);
    }

    private PlaybackTimeline livePlaybackTimeline(Models.VideoFile file, double currentPosition, double currentDuration) {
        if (activePlaybackQueue == null || activePlaybackQueue.size() <= 1 || activePlaybackIndex < 0) {
            return new PlaybackTimeline(Math.max(0, currentPosition), Math.max(0, currentDuration));
        }

        double position = 0;
        double duration = 0;
        for (int index = 0; index < activePlaybackQueue.size(); index++) {
            double partDuration = playbackPartDurationSeconds(index, currentPosition, currentDuration);
            if (index < activePlaybackIndex) {
                position += partDuration;
            }
            duration += partDuration;
        }
        position += Math.max(0, currentPosition);
        return new PlaybackTimeline(Math.min(position, Math.max(duration, position)), duration);
    }

    private void maybeAdvanceMoviePart(Models.VideoFile file, double position, double duration) {
        if (advancingPlaybackPart ||
                activePlaybackQueue == null ||
                activePlaybackQueue.size() <= 1 ||
                activePlaybackIndex + 1 >= activePlaybackQueue.size() ||
                duration <= 0 ||
                position < Math.max(duration - 1.5, duration * 0.995)) {
            return;
        }

        advancingPlaybackPart = true;
        Models.VideoFile nextFile = activePlaybackQueue.get(activePlaybackIndex + 1);
        ArrayList<Models.VideoFile> queue = new ArrayList<>(activePlaybackQueue);
        int nextIndex = activePlaybackIndex + 1;
        double finalDuration = Math.max(duration, position);
        runAsync(
                () -> {
                    api.updatePlaybackProgress(file.id, finalDuration, finalDuration);
                    return null;
                },
                ignored -> transitionToPlaybackPart(nextFile, queue, nextIndex),
                ignored -> transitionToPlaybackPart(nextFile, queue, nextIndex));
    }

    private void transitionToPlaybackPart(Models.VideoFile nextFile, List<Models.VideoFile> queue, int nextIndex) {
        stopPlayerUiUpdates();
        stopProgressUpdates();
        stopSubtitleOverlayUpdates();
        stopSmoothPlayerSeek();
        closePlayerMenu(null);
        stopActiveHlsSession();
        activePlaybackFile = null;
        if (playerView != null) {
            playerView.destroyPlayer();
            playerView = null;
        }
        playFile(nextFile, queue, nextIndex);
    }

    private void showAudioMenu(Models.VideoFile file, View returnFocus) {
        showPlayerChromeTemporarily();
        refreshRuntimeTracks();
        ArrayList<PlayerMenuOption> options = new ArrayList<>();
        if (isActiveHlsPlayback()) {
            options.add(new PlayerMenuOption("默认音轨 · HLS", true, () -> {
                if (playerView != null) {
                    playerView.setAudioTrack("auto");
                    selectedAudioTrackOrdinal = file.audioTracks.isEmpty() ? -1 : 0;
                }
            }));
            showPlayerMenu("音轨", options, returnFocus);
            return;
        }
        if (prefersRuntimeTracks(file) || shouldUseRuntimeAudioTracks(file)) {
            for (RuntimeTrack track : activeRuntimeAudioTracks) {
                boolean selected = isRuntimeAudioSelected(track);
                options.add(new PlayerMenuOption(formatRuntimeAudioTrack(track), selected, () -> selectRuntimeAudioTrack(track)));
            }
            if (options.isEmpty()) {
                options.add(new PlayerMenuOption("暂无可切换音轨", false, () -> {
                }));
            }
            showPlayerMenu("音轨", options, returnFocus);
            return;
        }
        for (int index = 0; index < file.audioTracks.size(); index++) {
            Models.VideoFileStream track = file.audioTracks.get(index);
            int trackOrdinal = index;
            boolean selected = selectedAudioTrackOrdinal == index;
            options.add(new PlayerMenuOption(formatAudioTrack(track, index), selected, () -> {
                if (playerView != null) {
                    playerView.setAudioTrack(String.valueOf(trackOrdinal + 1));
                    selectedAudioTrackOrdinal = trackOrdinal;
                    selectedRuntimeAudioId = null;
                }
            }));
        }
        if (options.isEmpty()) {
            options.add(new PlayerMenuOption("暂无可切换音轨", false, () -> {
            }));
        }
        showPlayerMenu("音轨", options, returnFocus);
    }

    private void showSubtitleMenu(Models.VideoFile file, View returnFocus) {
        showPlayerChromeTemporarily();
        refreshRuntimeTracks();
        ArrayList<PlayerMenuOption> options = new ArrayList<>();
        if (prefersRuntimeTracks(file) || shouldUseRuntimeSubtitleTracks(file)) {
            for (RuntimeTrack track : activeRuntimeSubtitleTracks) {
                boolean selected = isRuntimeSubtitleSelected(track);
                options.add(new PlayerMenuOption(formatRuntimeSubtitleTrack(track), selected, () -> selectRuntimeSubtitleTrack(file, track)));
            }
        } else {
            for (int index = 0; index < file.subtitleStreams.size(); index++) {
                Models.VideoFileStream stream = file.subtitleStreams.get(index);
                int ordinal = index;
                boolean selected = isEmbeddedSubtitleSelected(index);
                options.add(new PlayerMenuOption(formatSubtitleStream(stream, index), selected, () -> selectEmbeddedSubtitle(file, ordinal)));
            }
        }
        for (Models.SubtitleTrack track : activeExternalSubtitles) {
            boolean selected = isExternalSubtitleSelected(track);
            options.add(new PlayerMenuOption(formatExternalSubtitle(track), selected, () -> selectExternalSubtitle(track)));
        }
        if (options.isEmpty()) {
            options.add(new PlayerMenuOption("暂无可切换字幕", false, () -> {
            }));
        }
        showPlayerMenu("字幕", options, returnFocus);
    }

    private void showPlayerMenu(String title, List<PlayerMenuOption> options, View returnFocus) {
        showPlayerChromeTemporarily();
        closePlayerMenu(returnFocus);

        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(0, 0, 0, 0);
        panel.setClipChildren(false);
        panel.setClipToPadding(false);
        TextView menuTitle = text(title, ssp(18), Typeface.BOLD, COLOR_PLAYER_TEXT);
        menuTitle.setShadowLayer(sdp(2), 0, sdp(1), Color.BLACK);
        panel.addView(menuTitle, margin(matchWidth(), 0, 0, 0, sdp(12)));

        Button first = null;
        Button selectedButton = null;
        for (PlayerMenuOption option : options) {
            Button button = playerMenuButton(option.displayLabel(), option.selected);
            panel.addView(button, margin(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, sdp(48)), 0, sdp(7), 0, 0));
            button.setOnClickListener(view -> {
                option.action.run();
                closePlayerMenu(returnFocus);
                showPlayerChromeTemporarily();
            });
            if (first == null) {
                first = button;
            }
            if (selectedButton == null && option.selected) {
                selectedButton = button;
            }
        }

        FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(sdp(340), ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.RIGHT | Gravity.BOTTOM);
        params.setMargins(0, 0, sdp(58), sdp(248));
        root.addView(panel, params);
        playerMenuPanel = panel;
        panel.setElevation(sdp(34));
        panel.setTranslationZ(sdp(34));
        panel.bringToFront();
        if (selectedButton != null) {
            selectedButton.requestFocus();
        } else if (first != null) {
            first.requestFocus();
        }
    }

    private void closePlayerMenu(View returnFocus) {
        if (playerMenuPanel != null) {
            ViewParent parent = playerMenuPanel.getParent();
            if (parent instanceof ViewGroup) {
                ((ViewGroup) parent).removeView(playerMenuPanel);
            }
            playerMenuPanel = null;
        }
        if (returnFocus != null) {
            returnFocus.requestFocus();
        }
    }

    private void selectEmbeddedSubtitle(Models.VideoFile file, int ordinal) {
        selectEmbeddedSubtitle(file, ordinal, null);
    }

    private void selectEmbeddedSubtitle(Models.VideoFile file, int ordinal, Runnable onFailure) {
        if (playerView == null || ordinal < 0 || ordinal >= file.subtitleStreams.size()) {
            runSubtitleFallback(onFailure);
            return;
        }
        String ticket = activePlaybackTicket;
        Models.VideoFileStream stream = file.subtitleStreams.get(ordinal);
        boolean useAndroidSubtitleOverlay = usesAndroidSubtitleOverlay();
        clearSubtitleSelection();
        int loadGeneration = beginSubtitleLoad();
        markPendingEmbeddedSubtitle(ordinal);
        Runnable onLoaded = () -> markSelectedEmbeddedSubtitle(ordinal);
        Runnable onLoadFailed = () -> {
            if (!isSubtitleLoadCurrent(loadGeneration)) {
                return;
            }
            if (tryUseNativeEmbeddedSubtitle(ordinal)) {
                markSelectedEmbeddedSubtitle(ordinal);
                return;
            }
            clearSubtitleSelection();
            runSubtitleFallback(onFailure);
        };
        if (useAndroidSubtitleOverlay && isPgsSubtitle(stream)) {
            String subtitleUrl = api.embeddedPgsSubtitleUrl(file.id, ordinal, ticket);
            if (!subtitleUrl.isEmpty()) {
                loadPgsSubtitleOverlay(subtitleUrl, ticket == null || ticket.isEmpty() ? api.cookieHeader() : "", loadGeneration, onLoaded, onLoadFailed);
            } else {
                clearSubtitleOverlayIfCurrent(loadGeneration);
                onLoadFailed.run();
            }
            playerView.setSubtitleTrack("no");
            return;
        }
        if (useAndroidSubtitleOverlay && canUseEmbeddedSubtitleAsWebTrack(stream)) {
            String subtitleUrl = api.embeddedSubtitleUrl(file.id, ordinal, ticket);
            if (!subtitleUrl.isEmpty()) {
                loadSubtitleOverlay(subtitleUrl, ticket == null || ticket.isEmpty() ? api.cookieHeader() : "", loadGeneration, onLoaded, onLoadFailed);
            } else {
                clearSubtitleOverlayIfCurrent(loadGeneration);
                onLoadFailed.run();
            }
            playerView.setSubtitleTrack("no");
            return;
        }
        if (useAndroidSubtitleOverlay) {
            clearSubtitleOverlay();
            playerView.setSubtitleTrack("no");
            runSubtitleFallback(onFailure);
            return;
        }
        if (isActiveHlsPlayback()) {
            clearSubtitleOverlay();
            playerView.setSubtitleTrack("no");
            runSubtitleFallback(onFailure);
            return;
        }
        clearSubtitleOverlay();
        markSelectedEmbeddedSubtitle(ordinal);
        playerView.setSubtitleTrack(String.valueOf(ordinal + 1));
    }

    private void selectRuntimeAudioTrack(RuntimeTrack track) {
        if (playerView == null || track == null) {
            return;
        }
        playerView.setAudioTrack(track.mpvId);
        selectedAudioTrackOrdinal = track.ordinal;
        selectedRuntimeAudioId = runtimeTrackKey(track);
    }

    private void selectRuntimeSubtitleTrack(Models.VideoFile file, RuntimeTrack track) {
        selectRuntimeSubtitleTrack(file, track, null);
    }

    private void selectRuntimeSubtitleTrack(Models.VideoFile file, RuntimeTrack track, Runnable onFailure) {
        if (playerView == null || track == null) {
            runSubtitleFallback(onFailure);
            return;
        }
        boolean useAndroidSubtitleOverlay = usesAndroidSubtitleOverlay() && !isIsoFile(file);
        clearSubtitleSelection();
        int loadGeneration = beginSubtitleLoad();
        markPendingRuntimeSubtitle(track);
        Runnable onLoaded = () -> markSelectedRuntimeSubtitle(track);
        Runnable onLoadFailed = () -> {
            if (!isSubtitleLoadCurrent(loadGeneration)) {
                return;
            }
            if (tryUseNativeRuntimeSubtitle(track)) {
                markSelectedRuntimeSubtitle(track);
                return;
            }
            clearSubtitleSelection();
            runSubtitleFallback(onFailure);
        };
        if (useAndroidSubtitleOverlay && isPgsSubtitle(track)) {
            String subtitleUrl = api.embeddedPgsSubtitleUrl(file.id, track.ordinal, activePlaybackTicket);
            if (!subtitleUrl.isEmpty()) {
                loadPgsSubtitleOverlay(subtitleUrl, activePlaybackTicket == null || activePlaybackTicket.isEmpty() ? api.cookieHeader() : "", loadGeneration, onLoaded, onLoadFailed);
            } else {
                clearSubtitleOverlayIfCurrent(loadGeneration);
                onLoadFailed.run();
            }
            playerView.setSubtitleTrack("no");
            return;
        }
        if (useAndroidSubtitleOverlay && canUseEmbeddedSubtitleAsWebTrack(track)) {
            String subtitleUrl = api.embeddedSubtitleUrl(file.id, track.ordinal, activePlaybackTicket);
            if (!subtitleUrl.isEmpty()) {
                loadSubtitleOverlay(subtitleUrl, activePlaybackTicket == null || activePlaybackTicket.isEmpty() ? api.cookieHeader() : "", loadGeneration, onLoaded, onLoadFailed);
            } else {
                clearSubtitleOverlayIfCurrent(loadGeneration);
                onLoadFailed.run();
            }
            playerView.setSubtitleTrack("no");
            return;
        }
        if (useAndroidSubtitleOverlay) {
            clearSubtitleOverlay();
            playerView.setSubtitleTrack("no");
            runSubtitleFallback(onFailure);
            return;
        }
        if (isActiveHlsPlayback()) {
            clearSubtitleOverlay();
            playerView.setSubtitleTrack("no");
            runSubtitleFallback(onFailure);
            return;
        }
        clearSubtitleOverlay();
        markSelectedRuntimeSubtitle(track);
        playerView.setSubtitleTrack(track.mpvId);
    }

    private void selectExternalSubtitle(Models.SubtitleTrack track) {
        selectExternalSubtitle(track, null);
    }

    private void selectExternalSubtitle(Models.SubtitleTrack track, Runnable onFailure) {
        if (playerView == null) {
            runSubtitleFallback(onFailure);
            return;
        }
        String ticket = activePlaybackTicket;
        String cookieHeader = ticket == null || ticket.isEmpty() ? api.cookieHeader() : "";
        String subtitleUrl = api.subtitleUrl(track, ticket);
        if (!subtitleUrl.isEmpty()) {
            String trackKey = subtitleTrackKey(track);
            if (usesAndroidSubtitleOverlay()) {
                clearSubtitleSelection();
                int loadGeneration = beginSubtitleLoad();
                markPendingExternalSubtitle(trackKey);
                loadSubtitleOverlay(
                        subtitleUrl,
                        cookieHeader,
                        loadGeneration,
                        () -> markSelectedExternalSubtitle(trackKey),
                        () -> {
                            if (!isSubtitleLoadCurrent(loadGeneration)) {
                                return;
                            }
                            if (playerView != null && playerView.addSubtitle(subtitleUrl, cookieHeader)) {
                                markSelectedExternalSubtitle(trackKey);
                                clearSubtitleOverlayViews();
                                return;
                            }
                            clearSubtitleSelection();
                            runSubtitleFallback(onFailure);
                        });
                playerView.setSubtitleTrack("no");
                return;
            }
            clearSubtitleOverlay();
            markSelectedExternalSubtitle(trackKey);
            playerView.addSubtitle(subtitleUrl, cookieHeader);
        } else {
            runSubtitleFallback(onFailure);
        }
    }

    private void loadSubtitleOverlay(String url, String cookieHeader, int loadGeneration) {
        loadSubtitleOverlay(url, cookieHeader, loadGeneration, null, null);
    }

    private void loadSubtitleOverlay(String url, String cookieHeader, int loadGeneration, Runnable onLoaded, Runnable onFailed) {
        if (url == null || url.isEmpty()) {
            clearSubtitleOverlayIfCurrent(loadGeneration);
            runSubtitleFallback(onFailed);
            return;
        }
        runAsync(
                () -> parseSubtitleCues(fetchText(url, cookieHeader)),
                cues -> {
                    if (screen != Screen.PLAYER || playerView == null || !isSubtitleLoadCurrent(loadGeneration)) {
                        return;
                    }
                    if (cues.isEmpty()) {
                        clearSubtitleOverlayIfCurrent(loadGeneration);
                        runSubtitleFallback(onFailed);
                        return;
                    }
                    activePgsSubtitleCues = Collections.emptyList();
                    if (playerPgsSubtitleOverlay != null) {
                        playerPgsSubtitleOverlay.clear();
                    }
                    activeSubtitleCues = cues;
                    startSubtitleOverlayUpdates();
                    runSubtitleLoaded(onLoaded);
                },
                ignored -> {
                    clearSubtitleOverlayIfCurrent(loadGeneration);
                    runSubtitleFallback(onFailed);
                });
    }

    private void loadPgsSubtitleOverlay(String url, String cookieHeader, int loadGeneration) {
        loadPgsSubtitleOverlay(url, cookieHeader, loadGeneration, null, null);
    }

    private void loadPgsSubtitleOverlay(String url, String cookieHeader, int loadGeneration, Runnable onLoaded, Runnable onFailed) {
        if (url == null || url.isEmpty()) {
            clearSubtitleOverlayIfCurrent(loadGeneration);
            runSubtitleFallback(onFailed);
            return;
        }
        runAsync(
                () -> fetchPgsSubtitleCues(url, cookieHeader),
                cues -> {
                    if (screen != Screen.PLAYER || playerView == null || !isSubtitleLoadCurrent(loadGeneration)) {
                        return;
                    }
                    if (cues.isEmpty()) {
                        clearSubtitleOverlayIfCurrent(loadGeneration);
                        runSubtitleFallback(onFailed);
                        return;
                    }
                    activeSubtitleCues = Collections.emptyList();
                    if (playerSubtitleOverlay != null) {
                        playerSubtitleOverlay.setText("");
                        playerSubtitleOverlay.setVisibility(View.GONE);
                    }
                    activePgsSubtitleCues = cues;
                    if (playerPgsSubtitleOverlay != null) {
                        playerPgsSubtitleOverlay.setCues(cues);
                        playerPgsSubtitleOverlay.bringToFront();
                    }
                    startSubtitleOverlayUpdates();
                    runSubtitleLoaded(onLoaded);
                },
                ignored -> {
                    clearSubtitleOverlayIfCurrent(loadGeneration);
                    runSubtitleFallback(onFailed);
                });
    }

    private List<PgsSubtitleCue> fetchPgsSubtitleCues(String url, String cookieHeader) throws IOException {
        HttpURLConnection connection = (HttpURLConnection) new URL(url).openConnection();
        connection.setConnectTimeout(8000);
        connection.setReadTimeout(120000);
        connection.setRequestProperty("User-Agent", "OmniPlay-Android/0.1");
        if (cookieHeader != null && !cookieHeader.isEmpty()) {
            connection.setRequestProperty("Cookie", cookieHeader);
        }
        try (InputStream input = new BufferedInputStream(connection.getInputStream())) {
            return PgsSubtitleParser.parse(input);
        } finally {
            connection.disconnect();
        }
    }

    private String fetchText(String url, String cookieHeader) throws IOException {
        HttpURLConnection connection = (HttpURLConnection) new URL(url).openConnection();
        connection.setConnectTimeout(8000);
        connection.setReadTimeout(90000);
        connection.setRequestProperty("User-Agent", "OmniPlay-Android/0.1");
        if (cookieHeader != null && !cookieHeader.isEmpty()) {
            connection.setRequestProperty("Cookie", cookieHeader);
        }
        try (InputStream input = connection.getInputStream();
             BufferedReader reader = new BufferedReader(new InputStreamReader(input, StandardCharsets.UTF_8))) {
            StringBuilder builder = new StringBuilder();
            String line;
            while ((line = reader.readLine()) != null) {
                builder.append(line).append('\n');
            }
            return builder.toString();
        } finally {
            connection.disconnect();
        }
    }

    private List<SubtitleCue> parseSubtitleCues(String text) {
        ArrayList<SubtitleCue> cues = new ArrayList<>();
        if (text == null || text.isEmpty()) {
            return cues;
        }

        String[] lines = text
                .replace("\uFEFF", "")
                .replace("\r\n", "\n")
                .replace('\r', '\n')
                .split("\n", -1);
        for (int index = 0; index < lines.length; index++) {
            String line = lines[index].trim();
            if (line.isEmpty() || line.equalsIgnoreCase("WEBVTT")) {
                continue;
            }
            if (line.startsWith("NOTE") || line.equals("STYLE") || line.equals("REGION")) {
                while (index + 1 < lines.length && !lines[index + 1].trim().isEmpty()) {
                    index++;
                }
                continue;
            }
            if (!line.contains("-->") && index + 1 < lines.length && lines[index + 1].contains("-->")) {
                index++;
                line = lines[index].trim();
            }
            if (!line.contains("-->")) {
                continue;
            }

            Matcher matcher = SUBTITLE_TIME_LINE.matcher(line);
            if (!matcher.matches()) {
                continue;
            }
            double start = parseSubtitleTime(matcher.group(1));
            double end = parseSubtitleTime(matcher.group(2));
            StringBuilder body = new StringBuilder();
            while (index + 1 < lines.length && !lines[index + 1].trim().isEmpty()) {
                String bodyLine = lines[++index].trim();
                if (!bodyLine.isEmpty() && !bodyLine.contains("-->")) {
                    String cleanLine = cleanSubtitleTextLine(bodyLine);
                    if (cleanLine.isEmpty()) {
                        continue;
                    }
                    if (body.length() > 0) {
                        body.append('\n');
                    }
                    body.append(cleanLine);
                }
            }
            if (end > start && body.length() > 0) {
                cues.add(new SubtitleCue(start, end, body.toString()));
            }
        }
        return cues;
    }

    private String cleanSubtitleTextLine(String line) {
        if (line == null) {
            return "";
        }
        String withoutTags = line
                .replaceAll("</?c(?:\\.[^>]+)?>", "")
                .replaceAll("</?v(?:\\s+[^>]+)?>", "")
                .replaceAll("</?lang(?:\\s+[^>]+)?>", "")
                .replaceAll("<\\d{1,2}:\\d{2}:\\d{2}[,.]\\d{1,3}>", "")
                .replaceAll("<[^>]+>", "");
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            return Html.fromHtml(withoutTags, Html.FROM_HTML_MODE_LEGACY).toString().trim();
        }
        return Html.fromHtml(withoutTags).toString().trim();
    }

    private double parseSubtitleTime(String value) {
        String clean = value == null ? "" : value.trim().replace(',', '.');
        String[] parts = clean.split(":");
        try {
            if (parts.length == 3) {
                return Integer.parseInt(parts[0]) * 3600d + Integer.parseInt(parts[1]) * 60d + Double.parseDouble(parts[2]);
            }
            if (parts.length == 2) {
                return Integer.parseInt(parts[0]) * 60d + Double.parseDouble(parts[1]);
            }
        } catch (NumberFormatException ignored) {
        }
        return 0;
    }

    private void startSubtitleOverlayUpdates() {
        stopSubtitleOverlayUpdates();
        subtitleOverlayUpdater = new Runnable() {
            @Override
            public void run() {
                updateSubtitleOverlay();
                if (screen == Screen.PLAYER && playerView != null) {
                    main.postDelayed(this, 250);
                }
            }
        };
        subtitleOverlayUpdater.run();
    }

    private void updateSubtitleOverlay() {
        if (playerView == null) {
            return;
        }
        double position = playerView.currentTimeSeconds();
        if (playerSubtitleOverlay != null) {
            String subtitle = "";
            for (SubtitleCue cue : activeSubtitleCues) {
                if (position >= cue.startSeconds && position <= cue.endSeconds) {
                    subtitle = cue.text;
                    break;
                }
            }
            playerSubtitleOverlay.setText(subtitle);
            playerSubtitleOverlay.setVisibility(subtitle.isEmpty() ? View.GONE : View.VISIBLE);
            if (!subtitle.isEmpty()) {
                playerSubtitleOverlay.bringToFront();
            }
        }
        if (playerPgsSubtitleOverlay != null && !activePgsSubtitleCues.isEmpty()) {
            playerPgsSubtitleOverlay.setPlaybackPosition(position);
            if (playerPgsSubtitleOverlay.getVisibility() == View.VISIBLE) {
                playerPgsSubtitleOverlay.bringToFront();
            }
        }
    }

    private void clearSubtitleOverlay() {
        subtitleLoadGeneration++;
        clearSubtitleSelection();
        clearSubtitleOverlayViews();
    }

    private int beginSubtitleLoad() {
        subtitleLoadGeneration++;
        subtitleLoadStartedAtMs = System.currentTimeMillis();
        clearSubtitleOverlayViews();
        return subtitleLoadGeneration;
    }

    private boolean isSubtitleLoadCurrent(int loadGeneration) {
        return loadGeneration == subtitleLoadGeneration;
    }

    private void clearSubtitleOverlayIfCurrent(int loadGeneration) {
        if (isSubtitleLoadCurrent(loadGeneration)) {
            clearSubtitleOverlayViews();
        }
    }

    private void clearSubtitleSelection() {
        selectedEmbeddedSubtitleOrdinal = -1;
        selectedRuntimeSubtitleId = null;
        selectedExternalSubtitleId = null;
        pendingEmbeddedSubtitleOrdinal = -1;
        pendingRuntimeSubtitleId = null;
        pendingExternalSubtitleId = null;
    }

    private void markPendingEmbeddedSubtitle(int ordinal) {
        pendingEmbeddedSubtitleOrdinal = ordinal;
        pendingRuntimeSubtitleId = null;
        pendingExternalSubtitleId = null;
    }

    private void markSelectedEmbeddedSubtitle(int ordinal) {
        selectedEmbeddedSubtitleOrdinal = ordinal;
        selectedRuntimeSubtitleId = null;
        selectedExternalSubtitleId = null;
        pendingEmbeddedSubtitleOrdinal = -1;
        pendingRuntimeSubtitleId = null;
        pendingExternalSubtitleId = null;
    }

    private void markPendingRuntimeSubtitle(RuntimeTrack track) {
        pendingEmbeddedSubtitleOrdinal = -1;
        pendingRuntimeSubtitleId = runtimeTrackKey(track);
        pendingExternalSubtitleId = null;
    }

    private void markSelectedRuntimeSubtitle(RuntimeTrack track) {
        selectedEmbeddedSubtitleOrdinal = -1;
        selectedRuntimeSubtitleId = runtimeTrackKey(track);
        selectedExternalSubtitleId = null;
        pendingEmbeddedSubtitleOrdinal = -1;
        pendingRuntimeSubtitleId = null;
        pendingExternalSubtitleId = null;
    }

    private void markPendingExternalSubtitle(String trackKey) {
        pendingEmbeddedSubtitleOrdinal = -1;
        pendingRuntimeSubtitleId = null;
        pendingExternalSubtitleId = trackKey;
    }

    private void markSelectedExternalSubtitle(String trackKey) {
        selectedEmbeddedSubtitleOrdinal = -1;
        selectedRuntimeSubtitleId = null;
        selectedExternalSubtitleId = trackKey;
        pendingEmbeddedSubtitleOrdinal = -1;
        pendingRuntimeSubtitleId = null;
        pendingExternalSubtitleId = null;
    }

    private boolean isEmbeddedSubtitleSelected(int ordinal) {
        return selectedExternalSubtitleId == null &&
                pendingExternalSubtitleId == null &&
                selectedRuntimeSubtitleId == null &&
                pendingRuntimeSubtitleId == null &&
                (selectedEmbeddedSubtitleOrdinal == ordinal || pendingEmbeddedSubtitleOrdinal == ordinal);
    }

    private boolean isRuntimeAudioSelected(RuntimeTrack track) {
        if (track == null) {
            return false;
        }
        String key = runtimeTrackKey(track);
        return key.equals(selectedRuntimeAudioId) ||
                (selectedRuntimeAudioId == null && selectedAudioTrackOrdinal == track.ordinal && track.selected);
    }

    private boolean isRuntimeSubtitleSelected(RuntimeTrack track) {
        String key = runtimeTrackKey(track);
        return key.equals(selectedRuntimeSubtitleId) || key.equals(pendingRuntimeSubtitleId);
    }

    private boolean isExternalSubtitleSelected(Models.SubtitleTrack track) {
        String key = subtitleTrackKey(track);
        return key.equals(selectedExternalSubtitleId) || key.equals(pendingExternalSubtitleId);
    }

    private boolean hasSubtitleSelection() {
        return selectedEmbeddedSubtitleOrdinal >= 0 ||
                selectedRuntimeSubtitleId != null ||
                selectedExternalSubtitleId != null ||
                pendingEmbeddedSubtitleOrdinal >= 0 ||
                pendingRuntimeSubtitleId != null ||
                pendingExternalSubtitleId != null;
    }

    private boolean hasPendingSubtitleSelection() {
        return pendingEmbeddedSubtitleOrdinal >= 0 ||
                pendingRuntimeSubtitleId != null ||
                pendingExternalSubtitleId != null;
    }

    private boolean hasRuntimeSubtitleSelection() {
        return selectedRuntimeSubtitleId != null || pendingRuntimeSubtitleId != null;
    }

    private boolean isSubtitleLoadStale() {
        return subtitleLoadStartedAtMs > 0 && System.currentTimeMillis() - subtitleLoadStartedAtMs > 8000;
    }

    private boolean hasRenderableSubtitleSelection(Models.VideoFile file) {
        if (!hasSubtitleSelection()) {
            return false;
        }
        if (usesAndroidSubtitleOverlay() && !isIsoFile(file)) {
            return !activeSubtitleCues.isEmpty() || !activePgsSubtitleCues.isEmpty();
        }
        return !hasPendingSubtitleSelection();
    }

    private boolean tryUseNativeEmbeddedSubtitle(int ordinal) {
        if (playerView == null || isActiveHlsPlayback() || ordinal < 0) {
            return false;
        }
        if (activeDirectPlayback && !isIsoFile(activePlaybackFile)) {
            return false;
        }
        playerView.setSubtitleTrack(String.valueOf(ordinal + 1));
        return true;
    }

    private boolean tryUseNativeRuntimeSubtitle(RuntimeTrack track) {
        if (playerView == null || isActiveHlsPlayback() || track == null) {
            return false;
        }
        if (activeDirectPlayback && !isIsoFile(activePlaybackFile)) {
            return false;
        }
        playerView.setSubtitleTrack(track.mpvId);
        return true;
    }

    private void runSubtitleLoaded(Runnable onLoaded) {
        if (onLoaded != null) {
            onLoaded.run();
        }
    }

    private void runSubtitleFallback(Runnable onFailure) {
        if (onFailure != null) {
            onFailure.run();
        }
    }

    private void runSubtitleFallbackIfCurrent(int loadGeneration, Runnable onFailure) {
        if (isSubtitleLoadCurrent(loadGeneration)) {
            runSubtitleFallback(onFailure);
        }
    }

    private void clearSubtitleOverlayViews() {
        activeSubtitleCues = Collections.emptyList();
        activePgsSubtitleCues = Collections.emptyList();
        stopSubtitleOverlayUpdates();
        if (playerSubtitleOverlay != null) {
            playerSubtitleOverlay.setText("");
            playerSubtitleOverlay.setVisibility(View.GONE);
        }
        if (playerPgsSubtitleOverlay != null) {
            playerPgsSubtitleOverlay.clear();
        }
    }

    private void stopSubtitleOverlayUpdates() {
        if (subtitleOverlayUpdater != null) {
            main.removeCallbacks(subtitleOverlayUpdater);
            subtitleOverlayUpdater = null;
        }
    }

    private void applyDefaultPlaybackTracks(Models.VideoFile file, Models.AppSettings settings) {
        Models.PlaybackSettings playback = settings == null ? Models.PlaybackSettings.defaults() : settings.playback;
        if (playerView != null) {
            playerView.setQualityProfile(api.engine().settings().playbackQualityMode());
        }
        if (isActiveHlsPlayback()) {
            selectedAudioTrackOrdinal = file.audioTracks.isEmpty() ? -1 : 0;
            selectedRuntimeAudioId = null;
            if (playerView != null) {
                playerView.setAudioTrack("auto");
            }
            loadDefaultSubtitle(file, playback.defaultSubtitleLanguage);
            return;
        }
        refreshRuntimeTracks();
        if (shouldUseRuntimeAudioTracks(file)) {
            RuntimeTrack audioTrack = resolvePreferredRuntimeAudioTrack(file, playback.defaultAudioLanguage);
            selectedAudioTrackOrdinal = audioTrack == null ? -1 : audioTrack.ordinal;
            selectedRuntimeAudioId = audioTrack == null ? null : runtimeTrackKey(audioTrack);
            if (playerView != null) {
                playerView.setAudioTrack(audioTrack == null ? "auto" : audioTrack.mpvId);
            }
            loadDefaultSubtitle(file, playback.defaultSubtitleLanguage);
            return;
        }
        if (prefersRuntimeTracks(file)) {
            selectedAudioTrackOrdinal = -1;
            selectedRuntimeAudioId = null;
            if (playerView != null) {
                playerView.setAudioTrack("auto");
            }
            loadDefaultSubtitle(file, playback.defaultSubtitleLanguage);
            return;
        }
        Models.VideoFileStream audioTrack = resolvePreferredAudioTrack(file, playback.defaultAudioLanguage);
        selectedAudioTrackOrdinal = audioTrack == null ? -1 : audioTrackOrdinal(file, audioTrack);
        selectedRuntimeAudioId = null;
        if (playerView != null) {
            playerView.setAudioTrack(audioTrack == null ? "auto" : mpvAudioTrackId(file, audioTrack));
        }
        loadDefaultSubtitle(file, playback.defaultSubtitleLanguage);
    }

    private void scheduleRuntimeTrackRefresh(Models.VideoFile file, Models.AppSettings settings) {
        scheduleRuntimeTrackRefresh(file, settings, 350);
        scheduleRuntimeTrackRefresh(file, settings, 1200);
        scheduleRuntimeTrackRefresh(file, settings, 2600);
    }

    private void scheduleRuntimeTrackRefresh(Models.VideoFile file, Models.AppSettings settings, long delayMs) {
        main.postDelayed(() -> {
            if (screen != Screen.PLAYER || playerView == null || activePlaybackFile != file) {
                return;
            }
            int previousAudioCount = activeRuntimeAudioTracks.size();
            int previousSubtitleCount = activeRuntimeSubtitleTracks.size();
            refreshRuntimeTracks();
            if (activeRuntimeAudioTracks.size() != previousAudioCount && shouldUseRuntimeAudioTracks(file) && selectedAudioTrackOrdinal < 0) {
                Models.PlaybackSettings playback = settings == null ? Models.PlaybackSettings.defaults() : settings.playback;
                RuntimeTrack audioTrack = resolvePreferredRuntimeAudioTrack(file, playback.defaultAudioLanguage);
                selectedAudioTrackOrdinal = audioTrack == null ? -1 : audioTrack.ordinal;
                selectedRuntimeAudioId = audioTrack == null ? null : runtimeTrackKey(audioTrack);
                if (playerView != null) {
                    playerView.setAudioTrack(audioTrack == null ? "auto" : audioTrack.mpvId);
                }
            }
            if (activeRuntimeSubtitleTracks.size() != previousSubtitleCount && !hasSubtitleSelection()) {
                Models.PlaybackSettings playback = settings == null ? Models.PlaybackSettings.defaults() : settings.playback;
                loadDefaultSubtitle(file, playback.defaultSubtitleLanguage);
            } else if (activeRuntimeSubtitleTracks.size() != previousSubtitleCount &&
                    shouldUseRuntimeSubtitleTracks(file) &&
                    !hasRuntimeSubtitleSelection() &&
                    selectedExternalSubtitleId == null &&
                    pendingExternalSubtitleId == null) {
                clearSubtitleSelection();
                Models.PlaybackSettings playback = settings == null ? Models.PlaybackSettings.defaults() : settings.playback;
                loadDefaultSubtitle(file, playback.defaultSubtitleLanguage);
            }
        }, delayMs);
    }

    private void scheduleDefaultSubtitleRetries(Models.VideoFile file, Models.AppSettings settings) {
        scheduleDefaultSubtitleRetry(file, settings, 700);
        scheduleDefaultSubtitleRetry(file, settings, 1800);
        scheduleDefaultSubtitleRetry(file, settings, 4200);
        scheduleDefaultSubtitleRetry(file, settings, 8500);
    }

    private void scheduleDefaultSubtitleRetry(Models.VideoFile file, Models.AppSettings settings, long delayMs) {
        main.postDelayed(() -> {
            if (screen != Screen.PLAYER || playerView == null || activePlaybackFile != file) {
                return;
            }
            if (hasRenderableSubtitleSelection(file)) {
                return;
            }
            if (hasPendingSubtitleSelection() && !isSubtitleLoadStale()) {
                return;
            }
            clearSubtitleSelection();
            Models.PlaybackSettings playback = settings == null ? Models.PlaybackSettings.defaults() : settings.playback;
            refreshRuntimeTracks();
            loadDefaultSubtitle(file, playback.defaultSubtitleLanguage);
        }, delayMs);
    }

    private int audioTrackOrdinal(Models.VideoFile file, Models.VideoFileStream audioTrack) {
        for (int index = 0; index < file.audioTracks.size(); index++) {
            if (file.audioTracks.get(index) == audioTrack) {
                return index;
            }
        }
        return -1;
    }

    private String mpvAudioTrackId(Models.VideoFile file, Models.VideoFileStream audioTrack) {
        for (int index = 0; index < file.audioTracks.size(); index++) {
            if (file.audioTracks.get(index) == audioTrack) {
                return String.valueOf(index + 1);
            }
        }
        return String.valueOf(audioTrack.index);
    }

    private void refreshRuntimeTracks() {
        if (playerView == null) {
            activeRuntimeAudioTracks = Collections.emptyList();
            activeRuntimeSubtitleTracks = Collections.emptyList();
            return;
        }

        int count = (int) Math.round(playerView.getDoubleProperty("track-list/count", 0));
        if (count <= 0) {
            return;
        }

        ArrayList<RuntimeTrack> audioTracks = new ArrayList<>();
        ArrayList<RuntimeTrack> subtitleTracks = new ArrayList<>();
        int audioOrdinal = 0;
        int subtitleOrdinal = 0;
        for (int index = 0; index < Math.min(count, 128); index++) {
            String prefix = "track-list/" + index + "/";
            String type = runtimePropertyString(prefix + "type", "");
            if ("audio".equals(type)) {
                audioTracks.add(readRuntimeTrack(prefix, audioOrdinal++, type));
            } else if ("sub".equals(type) || "subtitle".equals(type)) {
                subtitleTracks.add(readRuntimeTrack(prefix, subtitleOrdinal++, "sub"));
            }
        }
        activeRuntimeAudioTracks = audioTracks;
        activeRuntimeSubtitleTracks = subtitleTracks;
    }

    private RuntimeTrack readRuntimeTrack(String prefix, int ordinal, String type) {
        String mpvId = runtimePropertyString(prefix + "id", "");
        if (mpvId.isEmpty()) {
            double idValue = playerView == null ? -1 : playerView.getDoubleProperty(prefix + "id", -1);
            if (Double.isFinite(idValue) && idValue > 0) {
                mpvId = formatRuntimeTrackId(idValue);
            }
        }
        if (mpvId.isEmpty()) {
            mpvId = String.valueOf(ordinal + 1);
        }
        String codec = runtimePropertyString(prefix + "codec", "");
        String language = runtimePropertyString(prefix + "lang", "");
        String title = runtimePropertyString(prefix + "title", "");
        Integer channels = runtimePropertyInteger(prefix + "demux-channel-count");
        String channelLayout = runtimePropertyString(prefix + "demux-channels", "");
        boolean selected = runtimePropertyBoolean(prefix + "selected");
        boolean isDefault = runtimePropertyBoolean(prefix + "default");
        boolean isForced = runtimePropertyBoolean(prefix + "forced");
        return new RuntimeTrack(ordinal, mpvId, type, codec, language, title, channels, channelLayout, selected, isDefault, isForced);
    }

    private String runtimePropertyString(String property, String fallback) {
        if (playerView == null) {
            return fallback == null ? "" : fallback;
        }
        String value = playerView.getStringProperty(property, fallback == null ? "" : fallback);
        return value == null ? "" : value.trim();
    }

    private Integer runtimePropertyInteger(String property) {
        if (playerView == null) {
            return null;
        }
        double value = playerView.getDoubleProperty(property, -1);
        return Double.isFinite(value) && value > 0 ? (int) Math.round(value) : null;
    }

    private boolean runtimePropertyBoolean(String property) {
        if (playerView == null) {
            return false;
        }
        String value = playerView.getStringProperty(property, "").trim().toLowerCase(Locale.ROOT);
        if (value.equals("yes") || value.equals("true") || value.equals("1")) {
            return true;
        }
        if (value.equals("no") || value.equals("false") || value.equals("0")) {
            return false;
        }
        double numeric = playerView.getDoubleProperty(property, 0);
        return Double.isFinite(numeric) && numeric > 0.5d;
    }

    private String formatRuntimeTrackId(double value) {
        if (Math.abs(value - Math.rint(value)) < 0.001d) {
            return String.valueOf((int) Math.round(value));
        }
        return String.format(Locale.US, "%.3f", value);
    }

    private boolean shouldUseRuntimeAudioTracks(Models.VideoFile file) {
        return !isActiveHlsPlayback() && !activeRuntimeAudioTracks.isEmpty();
    }

    private boolean shouldUseRuntimeSubtitleTracks(Models.VideoFile file) {
        return !isActiveHlsPlayback() && !activeRuntimeSubtitleTracks.isEmpty();
    }

    private boolean prefersRuntimeTracks(Models.VideoFile file) {
        return shouldUseRuntimeSubtitleTracks(file);
    }

    private String runtimeTrackKey(RuntimeTrack track) {
        if (track == null) {
            return "";
        }
        return track.type + "|" + track.mpvId + "|" + track.ordinal;
    }

    private void loadDefaultSubtitle(Models.VideoFile file, String languagePreference) {
        if ("no".equals(languagePreference) || "off".equals(languagePreference)) {
            // 关闭字幕: leave the overlay empty until the user picks a track.
            clearSubtitleOverlay();
            return;
        }
        if (!hasSubtitleSelection()) {
            List<SubtitleCandidate> embeddedCandidates = defaultSubtitleCandidates(file, Collections.emptyList(), languagePreference);
            selectDefaultSubtitleCandidate(file, embeddedCandidates, 0);
        }
        int requestGeneration = subtitleLoadGeneration;
        runAsync(
                () -> api.getSubtitles(file.id),
                tracks -> {
                    if (screen != Screen.PLAYER || playerView == null || activePlaybackFile != file || requestGeneration != subtitleLoadGeneration) {
                        return;
                    }
                    activeExternalSubtitles = new ArrayList<>(tracks);
                    selectDefaultSubtitleCandidate(file, defaultSubtitleCandidates(file, tracks, languagePreference), 0);
                },
                ignored -> {
                    if (screen == Screen.PLAYER && playerView != null && activePlaybackFile == file && requestGeneration == subtitleLoadGeneration) {
                        selectDefaultSubtitleCandidate(file, defaultSubtitleCandidates(file, Collections.emptyList(), languagePreference), 0);
                    }
                });
    }

    private List<SubtitleCandidate> defaultSubtitleCandidates(Models.VideoFile file, List<Models.SubtitleTrack> tracks, String languagePreference) {
        ArrayList<SubtitleCandidate> candidates = new ArrayList<>();
        Models.SubtitleTrack preferredExternal = matchingExternalSubtitle(tracks, languagePreference);
        if (preferredExternal != null) {
            addSubtitleCandidate(candidates, SubtitleCandidate.external(preferredExternal));
        }

        RuntimeTrack preferredRuntime = matchingRuntimeSubtitleTrack(file, languagePreference);
        if (preferredRuntime != null) {
            addSubtitleCandidate(candidates, SubtitleCandidate.runtime(preferredRuntime));
        }

        if (!prefersRuntimeTracks(file)) {
            int preferredEmbeddedOrdinal = matchingEmbeddedSubtitleOrdinal(file, languagePreference);
            if (preferredEmbeddedOrdinal >= 0) {
                addSubtitleCandidate(candidates, SubtitleCandidate.embedded(preferredEmbeddedOrdinal));
            }
        }

        Models.SubtitleTrack fallbackExternal = lastPlayableExternalSubtitle(tracks);
        if (fallbackExternal != null) {
            addSubtitleCandidate(candidates, SubtitleCandidate.external(fallbackExternal));
        }

        RuntimeTrack fallbackRuntime = lastPlayableRuntimeSubtitleTrack(file);
        if (fallbackRuntime != null) {
            addSubtitleCandidate(candidates, SubtitleCandidate.runtime(fallbackRuntime));
        }

        if (!prefersRuntimeTracks(file)) {
            int fallbackEmbeddedOrdinal = lastPlayableEmbeddedSubtitleOrdinal(file);
            if (fallbackEmbeddedOrdinal >= 0) {
                addSubtitleCandidate(candidates, SubtitleCandidate.embedded(fallbackEmbeddedOrdinal));
            }
        }

        if (shouldUseRuntimeSubtitleTracks(file)) {
            for (int index = activeRuntimeSubtitleTracks.size() - 1; index >= 0; index--) {
                RuntimeTrack track = activeRuntimeSubtitleTracks.get(index);
                if (isPlayableRuntimeSubtitleForDefault(file, track)) {
                    addSubtitleCandidate(candidates, SubtitleCandidate.runtime(track));
                }
            }
        }

        if (!prefersRuntimeTracks(file)) {
            for (int index = file.subtitleStreams.size() - 1; index >= 0; index--) {
                Models.VideoFileStream stream = file.subtitleStreams.get(index);
                if (!isPlayableEmbeddedSubtitleForDefault(stream)) {
                    continue;
                }
                addSubtitleCandidate(candidates, SubtitleCandidate.embedded(index));
            }
        }

        for (int index = tracks.size() - 1; index >= 0; index--) {
            Models.SubtitleTrack track = tracks.get(index);
            if (!api.subtitleUrl(track, activePlaybackTicket).isEmpty()) {
                addSubtitleCandidate(candidates, SubtitleCandidate.external(track));
            }
        }
        return candidates;
    }

    private void addSubtitleCandidate(List<SubtitleCandidate> candidates, SubtitleCandidate candidate) {
        for (SubtitleCandidate existing : candidates) {
            if (existing.sameAs(candidate)) {
                return;
            }
        }
        candidates.add(candidate);
    }

    private void selectDefaultSubtitleCandidate(Models.VideoFile file, List<SubtitleCandidate> candidates, int index) {
        if (screen != Screen.PLAYER || playerView == null || activePlaybackFile != file || index < 0 || index >= candidates.size()) {
            return;
        }

        SubtitleCandidate candidate = candidates.get(index);
        if (candidate.externalTrack != null && isExternalSubtitleSelected(candidate.externalTrack)) {
            return;
        }
        if (candidate.runtimeTrack != null && isRuntimeSubtitleSelected(candidate.runtimeTrack)) {
            return;
        }
        if (candidate.externalTrack == null && candidate.runtimeTrack == null && isEmbeddedSubtitleSelected(candidate.embeddedOrdinal)) {
            return;
        }
        Runnable fallback = () -> selectDefaultSubtitleCandidate(file, candidates, index + 1);
        if (candidate.externalTrack != null) {
            selectExternalSubtitle(candidate.externalTrack, fallback);
        } else if (candidate.runtimeTrack != null) {
            selectRuntimeSubtitleTrack(file, candidate.runtimeTrack, fallback);
        } else {
            selectEmbeddedSubtitle(file, candidate.embeddedOrdinal, fallback);
        }
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
        activePlaybackTicket = null;
        activeHlsSessionId = null;
        playerLoadStartedAtMs = 0;
    }

    private void postPlaybackProgress(Models.VideoFile file) {
        double duration = playerView == null ? 0 : playerView.durationSeconds();
        if (duration <= 0) {
            duration = file.durationSeconds;
        }
        double position = resolvePlayerPosition(file, duration);
        if (duration <= 0 && lastKnownPlaybackDurationSeconds > 0) {
            duration = lastKnownPlaybackDurationSeconds;
        }
        if (position <= 0 && lastKnownPlaybackPositionSeconds > 0) {
            position = lastKnownPlaybackPositionSeconds;
        }
        if (position <= 0 || duration <= 0) {
            return;
        }

        double finalDuration = duration;
        double finalPosition = position;
        rememberPlaybackPosition(file, finalPosition, finalDuration);
        runAsync(
                () -> {
                    api.updatePlaybackProgress(file.id, finalPosition, finalDuration);
                    return null;
                },
                ignored -> {
                },
                ignored -> {
                });
    }

    private void rememberPlaybackPosition(Models.VideoFile file, double position, double duration) {
        if (file == null || position <= 0 || !Double.isFinite(position)) {
            return;
        }
        double safeDuration = duration > 0 && Double.isFinite(duration) ? duration : file.durationSeconds;
        lastKnownPlaybackPositionSeconds = Math.max(0, position);
        lastKnownPlaybackDurationSeconds = Math.max(0, safeDuration);
        localPlaybackProgress.put(file.id, new PlaybackTimeline(lastKnownPlaybackPositionSeconds, lastKnownPlaybackDurationSeconds));
    }

    private void toggleLibraryItemWatched(Models.LibraryItem item) {
        runAsync(
                () -> {
                    api.setLibraryItemWatchedStatus(item.id, !item.isWatched);
                    return null;
                },
                ignored -> showHome(),
                error -> renderError(error.getMessage(), this::showHome));
    }

    private void toggleVideoWatched(Models.VideoFile file) {
        boolean next = !isEffectivelyWatched(file);
        runAsync(
                () -> {
                    api.setWatchedStatus(file.id, next, file.durationSeconds);
                    return null;
                },
                ignored -> {
                    if (currentDetail != null) {
                        loadDetail(currentDetail.id);
                    }
                },
                error -> renderError(error.getMessage(), () -> {
                    if (currentDetail != null) {
                        renderDetail(currentDetail);
                    } else {
                        showHome();
                    }
                }));
    }

    private void renderPlayerError(String message) {
        TextView error = text(message == null ? "播放器初始化失败。" : message, 22, Typeface.BOLD, COLOR_DANGER);
        error.setGravity(Gravity.CENTER);
        error.setBackgroundColor(Color.argb(196, 0, 0, 0));
        root.addView(error, match());
    }

    private void showLoading(String message) {
        closeAllOverlays();
        root.removeAllViews();
        applyAppBackground();
        TextView view = text(message, 24, Typeface.BOLD, COLOR_TEXT);
        view.setGravity(Gravity.CENTER);
        root.addView(view, match());
    }

    /** Short-lived message for actions taken from the detail page (cache queue errors). */
    private void showToast(String message) {
        if (message == null || message.isEmpty()) {
            return;
        }
        main.post(() -> android.widget.Toast.makeText(this, message, android.widget.Toast.LENGTH_LONG).show());
    }

    private void renderError(String message, Runnable backAction) {
        closeAllOverlays();
        root.removeAllViews();
        applyAppBackground();
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setGravity(Gravity.CENTER);
        root.addView(page, match());
        TextView error = text(message == null ? "加载失败" : message, 22, Typeface.BOLD, COLOR_DANGER);
        page.addView(error, margin(matchWidth(), 0, 0, 0, dp(18)));
        Button back = button("返回");
        page.addView(back, new LinearLayout.LayoutParams(dp(160), dp(56)));
        back.setOnClickListener(view -> backAction.run());
        back.requestFocus();
    }

    @Override
    public boolean dispatchKeyEvent(KeyEvent event) {
        if (screen == Screen.PLAYER && playerView != null && handlePlayerKeyEvent(event)) {
            return true;
        }
        // Whatever screen or panel declared a grid owns the arrow keys, including EditTexts,
        // which would otherwise swallow them or hand them to the wrong neighbour.
        if (handleDpadChain(event)) {
            return true;
        }
        if (screen == Screen.HOME && !overlays.hasOpen() && handleHomeDpadKeyEvent(event)) {
            return true;
        }
        return super.dispatchKeyEvent(event);
    }

    @Override
    public boolean onKeyDown(int keyCode, KeyEvent event) {
        if ((keyCode == KeyEvent.KEYCODE_BACK || keyCode == KeyEvent.KEYCODE_ESCAPE)
                && closeTopmostLayer()) {
            return true;
        }
        if (isDpadNavigationKey(keyCode) && rescueFocusIntoTopPanel()) {
            return true;
        }
        return super.onKeyDown(keyCode, event);
    }

    @Override
    public void onBackPressed() {
        if (closeTopmostLayer()) {
            return;
        }
        super.onBackPressed();
    }

    /**
     * The one back-key path: leave the topmost thing the user can leave. Panels unwind through
     * {@link Overlays#closeTop()}, so a dialog opened from a dialog can never be skipped and a
     * panel cannot be closed twice by the two back entry points.
     */
    private boolean closeTopmostLayer() {
        if (screen == Screen.PLAYER && playerMenuPanel != null) {
            closePlayerMenu(playerPlayPauseButton);
            return true;
        }
        if (overlays.closeTop()) {
            return true;
        }
        if (screen == Screen.PLAYER) {
            closePlayer();
            return true;
        }
        if (screen == Screen.HOME && (isSearchOpen || isSortOpen)) {
            isSearchOpen = false;
            isSortOpen = false;
            renderHomeContentKeepingPosition();
            // The rows that held focus (the search field, the option buttons) are gone.
            if (homeSearchToggle != null) {
                homeSearchToggle.requestFocus();
            }
            return true;
        }
        if (screen == Screen.DETAIL) {
            showHome();
            return true;
        }
        if (screen == Screen.SETUP && api.hasServerUrl()) {
            showHome();
            return true;
        }
        return false;
    }

    /**
     * Focus can land on a container after a list rebuilds itself; with a panel open the arrow
     * keys must not start driving the screen underneath it, so pull focus back in.
     */
    private boolean rescueFocusIntoTopPanel() {
        Overlays.Overlay top = overlays.top();
        if (top == null) {
            return false;
        }
        View focused = getCurrentFocus();
        if (focused != null && isDescendant(top.body, focused)) {
            return false;
        }
        focusGrid.focusFirst();
        return true;
    }

    private boolean isDpadNavigationKey(int keyCode) {
        return keyCode == KeyEvent.KEYCODE_DPAD_UP
                || keyCode == KeyEvent.KEYCODE_DPAD_DOWN
                || keyCode == KeyEvent.KEYCODE_DPAD_LEFT
                || keyCode == KeyEvent.KEYCODE_DPAD_RIGHT
                || keyCode == KeyEvent.KEYCODE_DPAD_CENTER
                || keyCode == KeyEvent.KEYCODE_ENTER;
    }

    private boolean isDpadArrowKey(int keyCode) {
        return keyCode == KeyEvent.KEYCODE_DPAD_UP
                || keyCode == KeyEvent.KEYCODE_DPAD_DOWN
                || keyCode == KeyEvent.KEYCODE_DPAD_LEFT
                || keyCode == KeyEvent.KEYCODE_DPAD_RIGHT;
    }

    private boolean handleHomeDpadKeyEvent(KeyEvent event) {
        if (event == null || event.getAction() != KeyEvent.ACTION_DOWN
                || !isDpadArrowKey(event.getKeyCode())) {
            return false;
        }
        View current = getCurrentFocus();
        if (current instanceof EditText) {
            return false;
        }
        if (current == null || !isDescendant(root, current)) {
            requestFirstHomeFocus();
            return true;
        }
        View next = current.focusSearch(focusDirectionForKey(event.getKeyCode()));
        if (next == null || !isDescendant(root, next) || !next.isFocusable()) {
            return true;
        }
        next.requestFocus();
        FocusGrid.scrollToVisible(next);
        return true;
    }

    private void requestFirstHomeFocus() {
        View target = FocusGrid.firstButton(homeFocusRows());
        if (target != null) {
            FocusGrid.grab(target);
            return;
        }
        ArrayList<View> focusables = root == null ? new ArrayList<>() : root.getFocusables(View.FOCUS_FORWARD);
        for (View focusable : focusables) {
            if (focusable != null && focusable.isShown() && focusable.isFocusable()) {
                focusable.requestFocus();
                return;
            }
        }
        if (root != null) {
            root.requestFocus();
        }
    }

    private int focusDirectionForKey(int keyCode) {
        if (keyCode == KeyEvent.KEYCODE_DPAD_UP) {
            return View.FOCUS_UP;
        }
        if (keyCode == KeyEvent.KEYCODE_DPAD_DOWN) {
            return View.FOCUS_DOWN;
        }
        if (keyCode == KeyEvent.KEYCODE_DPAD_LEFT) {
            return View.FOCUS_LEFT;
        }
        return View.FOCUS_RIGHT;
    }

    private boolean isDescendant(View parent, View child) {
        View current = child;
        while (current != null) {
            if (current == parent) {
                return true;
            }
            ViewParent parentView = current.getParent();
            current = parentView instanceof View ? (View) parentView : null;
        }
        return false;
    }

    private boolean handlePlayerKeyEvent(KeyEvent event) {
        if (event == null) {
            return false;
        }
        int keyCode = event.getKeyCode();
        if (event.getAction() == KeyEvent.ACTION_UP && isPlayerHorizontalSeekKey(keyCode)) {
            return handlePlayerHorizontalKeyUp(keyCode);
        }
        if (event.getAction() != KeyEvent.ACTION_DOWN) {
            return false;
        }
        return handlePlayerKeyDown(keyCode, event);
    }

    private boolean handlePlayerKeyDown(int keyCode, KeyEvent event) {
        if (playerMenuPanel != null) {
            return false;
        }
        boolean wasChromeHidden = playerChromeHidden;
        showPlayerChromeTemporarily();
        if (isPlayerHorizontalSeekKey(keyCode)) {
            return handlePlayerHorizontalKeyDown(keyCode, event, wasChromeHidden);
        }
        if (isPlayerMediaSeekKey(keyCode)) {
            if (event.getRepeatCount() > 0) {
                playerView.seek(playerSeekSeconds(keyCode));
                return true;
            }
            playerView.seek(playerSeekSeconds(keyCode));
            return true;
        }
        if (keyCode == KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE || keyCode == KeyEvent.KEYCODE_SPACE) {
            togglePlayerPaused();
            return true;
        }
        if ((keyCode == KeyEvent.KEYCODE_DPAD_CENTER || keyCode == KeyEvent.KEYCODE_ENTER) &&
                (wasChromeHidden || getCurrentFocus() == playerView)) {
            togglePlayerPaused();
            if (wasChromeHidden && playerPlayPauseButton != null) {
                playerPlayPauseButton.requestFocus();
            }
            return true;
        }
        if (wasChromeHidden &&
                (keyCode == KeyEvent.KEYCODE_DPAD_UP || keyCode == KeyEvent.KEYCODE_DPAD_DOWN) &&
                playerPlayPauseButton != null) {
            playerPlayPauseButton.requestFocus();
            return true;
        }
        return false;
    }

    private boolean handlePlayerHorizontalKeyDown(int keyCode, KeyEvent event, boolean wasChromeHidden) {
        int direction = keyCode == KeyEvent.KEYCODE_DPAD_LEFT ? -1 : 1;
        if (!playerHorizontalKeyDown || playerHorizontalKeyCode != keyCode) {
            cancelPlayerSeekStarter();
            playerHorizontalKeyDown = true;
            playerHorizontalKeyCode = keyCode;
            playerHorizontalKeyWasChromeHidden = wasChromeHidden;
            playerSeekDirection = direction;
            playerSeekStarter = () -> beginSmoothPlayerSeek(direction);
            main.postDelayed(playerSeekStarter, ViewConfiguration.getLongPressTimeout());
        }
        if (event.getRepeatCount() > 0) {
            beginSmoothPlayerSeek(direction);
        }
        return true;
    }

    private boolean handlePlayerHorizontalKeyUp(int keyCode) {
        if (!playerHorizontalKeyDown || playerHorizontalKeyCode != keyCode) {
            return false;
        }

        boolean wasLongPress = playerSeekLongPressActive;
        boolean wasChromeHidden = playerHorizontalKeyWasChromeHidden;
        stopSmoothPlayerSeek();
        playerHorizontalKeyDown = false;
        playerHorizontalKeyCode = 0;
        playerSeekDirection = 0;
        if (!wasLongPress) {
            if (wasChromeHidden || !isDescendant(playerControlsPanel, getCurrentFocus())) {
                if (playerPlayPauseButton != null) {
                    playerPlayPauseButton.requestFocus();
                }
            } else {
                movePlayerControlFocus(keyCode == KeyEvent.KEYCODE_DPAD_LEFT ? View.FOCUS_LEFT : View.FOCUS_RIGHT);
            }
        }
        return true;
    }

    private void beginSmoothPlayerSeek(int direction) {
        cancelPlayerSeekStarter();
        if (playerView == null || activePlaybackFile == null) {
            return;
        }
        if (playerSeekLongPressActive) {
            playerSeekDirection = direction < 0 ? -1 : 1;
            return;
        }
        playerSeekLongPressActive = true;
        playerSeekDirection = direction < 0 ? -1 : 1;
        if (playerSeekPreviewPosition < 0) {
            double duration = playerView.durationSeconds();
            if (duration <= 0) {
                duration = activePlaybackFile.durationSeconds;
            }
            playerSeekPreviewPosition = resolvePlayerPosition(activePlaybackFile, duration);
        }
        runSmoothPlayerSeekStep();
    }

    private void runSmoothPlayerSeekStep() {
        if (!playerSeekLongPressActive || playerView == null || activePlaybackFile == null || playerSeekDirection == 0) {
            return;
        }
        double duration = playerView.durationSeconds();
        if (duration <= 0) {
            duration = activePlaybackFile.durationSeconds;
        }
        double upperBound = duration > 0 ? duration : Math.max(0, playerSeekPreviewPosition + PLAYER_SEEK_STEP_SECONDS);
        playerSeekPreviewPosition = Math.max(0, Math.min(upperBound, playerSeekPreviewPosition + playerSeekDirection * PLAYER_SEEK_STEP_SECONDS));
        playerView.seekTo(playerSeekPreviewPosition);
        updatePlayerUi(activePlaybackFile);
        if (playerSeekRepeater != null) {
            main.removeCallbacks(playerSeekRepeater);
        }
        playerSeekRepeater = this::runSmoothPlayerSeekStep;
        main.postDelayed(playerSeekRepeater, PLAYER_SEEK_REPEAT_MS);
    }

    private void stopSmoothPlayerSeek() {
        cancelPlayerSeekStarter();
        if (playerSeekRepeater != null) {
            main.removeCallbacks(playerSeekRepeater);
            playerSeekRepeater = null;
        }
        playerSeekLongPressActive = false;
        playerSeekPreviewPosition = -1;
    }

    private void cancelPlayerSeekStarter() {
        if (playerSeekStarter != null) {
            main.removeCallbacks(playerSeekStarter);
            playerSeekStarter = null;
        }
    }

    private void movePlayerControlFocus(int direction) {
        View current = getCurrentFocus();
        View next = null;
        if (current == playerPlayPauseButton && direction == View.FOCUS_RIGHT) {
            next = playerAudioButton;
        } else if (current == playerAudioButton) {
            next = direction == View.FOCUS_LEFT ? playerPlayPauseButton : playerSubtitleButton;
        } else if (current == playerSubtitleButton && direction == View.FOCUS_LEFT) {
            next = playerAudioButton;
        }
        if (next == null && current != null) {
            next = current.focusSearch(direction);
            if (!isDescendant(playerControlsPanel, next)) {
                next = null;
            }
        }
        if (next != null) {
            next.requestFocus();
        } else if (playerPlayPauseButton != null) {
            playerPlayPauseButton.requestFocus();
        }
    }

    private boolean isPlayerHorizontalSeekKey(int keyCode) {
        return keyCode == KeyEvent.KEYCODE_DPAD_LEFT ||
                keyCode == KeyEvent.KEYCODE_DPAD_RIGHT;
    }

    private boolean isPlayerMediaSeekKey(int keyCode) {
        return keyCode == KeyEvent.KEYCODE_MEDIA_REWIND ||
                keyCode == KeyEvent.KEYCODE_MEDIA_FAST_FORWARD;
    }

    private double playerSeekSeconds(int keyCode) {
        return keyCode == KeyEvent.KEYCODE_DPAD_LEFT || keyCode == KeyEvent.KEYCODE_MEDIA_REWIND ? -10 : 10;
    }

    private boolean isLikely4K(Models.VideoFile file) {
        if (file == null) {
            return false;
        }
        if (file.videoHeight >= 2000 || file.videoWidth >= 3500) {
            return true;
        }
        String name = joinNonEmptyValues(" ", file.fileName, file.relativePath, file.label()).toLowerCase(Locale.ROOT);
        return name.contains("2160p") ||
                name.contains("4k") ||
                name.contains("uhd") ||
                name.contains("ultra hd");
    }

    private boolean shouldUseDirectPlayback(Models.VideoFile file) {
        if (isIsoFile(file)) {
            return false;
        }
        return highPerformanceDirectPlayback && (needsManagedColorPlayback(file) || isLikely4K(file));
    }

    private String initialPlaybackStatus(Models.VideoFile file) {
        if (isIsoFile(file)) {
            return "正在准备 ISO TV端解码";
        }
        if (shouldUseDirectPlayback(file)) {
            return "正在准备高性能直出";
        }
        if (needsManagedColorPlayback(file) || isLikely4K(file)) {
            return "正在准备兼容字幕播放";
        }
        return "正在准备安卓端色彩管理播放";
    }

    private String playbackStatusText(Models.VideoFile file, ResolvedPlayback playback) {
        if (playback != null && playback.hlsSessionId != null && !playback.hlsSessionId.isEmpty()) {
            return isIsoFile(file) ? "ISO TV端解码" : "服务端 HLS 兼容播放";
        }
        return activePlaybackStatusText(file);
    }

    private String activePlaybackStatusText(Models.VideoFile file) {
        if (isActiveHlsPlayback()) {
            return isIsoFile(file) ? "ISO TV端解码" : "服务端 HLS 兼容播放";
        }
        if (isIsoFile(file)) {
            return "ISO TV端解码";
        }
        if (shouldUseDirectPlayback(file)) {
            return "高性能直出";
        }
        if (needsManagedColorPlayback(file) || isLikely4K(file)) {
            return "兼容字幕播放";
        }
        return "安卓端色彩管理播放";
    }

    private boolean needsManagedColorPlayback(Models.VideoFile file) {
        return hasDolbyVisionHint(file) || hasHdrHint(file);
    }

    private boolean needsServerToneMappedPlayback(Models.VideoFile file) {
        return false;
    }

    private boolean isActiveHlsPlayback() {
        return activeHlsSessionId != null && !activeHlsSessionId.isEmpty();
    }

    private boolean usesAndroidSubtitleOverlay() {
        return isActiveHlsPlayback() || (activeDirectPlayback && !isIsoFile(activePlaybackFile));
    }

    private boolean isIsoFile(Models.VideoFile file) {
        if (file == null) {
            return false;
        }
        return hasFileExtension(file.fileName, ".iso") ||
                hasFileExtension(file.relativePath, ".iso") ||
                "iso".equalsIgnoreCase(file.container == null ? "" : file.container.trim());
    }

    private boolean hasFileExtension(String value, String extension) {
        return value != null && value.trim().toLowerCase(Locale.ROOT).endsWith(extension);
    }

    private boolean hasDolbyVisionHint(Models.VideoFile file) {
        String value = playbackHintText(file);
        Set<String> tokens = tokenizeSearchText(value);
        String compact = value.replaceAll("[^a-z0-9]+", "");
        return value.contains("dolby vision") ||
                compact.contains("dolbyvision") ||
                tokens.contains("dovi") ||
                tokens.contains("dv") ||
                tokens.contains("dvhe");
    }

    private boolean hasHdrHint(Models.VideoFile file) {
        String value = playbackHintText(file);
        Set<String> tokens = tokenizeSearchText(value);
        return tokens.contains("hdr") ||
                tokens.contains("hdr10") ||
                tokens.contains("hdr10plus") ||
                tokens.contains("hdr10+") ||
                tokens.contains("hlg") ||
                tokens.contains("pq") ||
                tokens.contains("bt2020") ||
                value.contains("bt.2020");
    }

    private String playbackHintText(Models.VideoFile file) {
        if (file == null) {
            return "";
        }
        return normalizeSearchText(Collections.singletonList(joinNonEmptyValues(
                " ",
                file.fileName,
                file.relativePath,
                file.label(),
                file.videoCodec,
                file.container)));
    }

    private void togglePlayerPaused() {
        if (playerView == null) {
            return;
        }
        playerView.togglePaused();
        playerPaused = !playerPaused;
        updatePlayerPlayPauseIcon();
        syncKeepScreenOn();
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

    private void updatePlayerPlayPauseIcon() {
        if (playerPlayPauseButton != null) {
            playerPlayPauseButton.setImageResource(playerPaused ? R.drawable.ic_player_play : R.drawable.ic_player_pause);
        }
    }

    private void showPlayerChromeTemporarily() {
        if (screen != Screen.PLAYER) {
            return;
        }
        showPlayerChromeView(playerTopOverlay);
        showPlayerChromeView(playerControlsPanel);
        if (playerTopOverlay != null) {
            playerTopOverlay.bringToFront();
        }
        if (playerControlsPanel != null) {
            playerControlsPanel.bringToFront();
        }
        if (playerMenuPanel != null) {
            playerMenuPanel.bringToFront();
        }
        playerChromeHidden = false;
        schedulePlayerChromeHide();
    }

    private void showPlayerChromeView(View view) {
        if (view == null) {
            return;
        }
        view.setVisibility(View.VISIBLE);
        view.setAlpha(1f);
    }

    private void schedulePlayerChromeHide() {
        if (playerChromeHider != null) {
            main.removeCallbacks(playerChromeHider);
        }
        playerChromeHider = () -> {
            if (screen != Screen.PLAYER) {
                return;
            }
            if (playerMenuPanel != null) {
                schedulePlayerChromeHide();
                return;
            }
            hidePlayerChrome();
        };
        main.postDelayed(playerChromeHider, 3000);
    }

    private void hidePlayerChrome() {
        if (screen != Screen.PLAYER || playerMenuPanel != null) {
            return;
        }
        if (playerView != null) {
            root.requestFocus();
        }
        hidePlayerChromeView(playerTopOverlay);
        hidePlayerChromeView(playerControlsPanel);
        playerChromeHidden = true;
    }

    private void hidePlayerChromeView(View view) {
        if (view == null) {
            return;
        }
        view.setAlpha(0f);
        view.setVisibility(View.INVISIBLE);
    }

    private void stopPlayerChromeHide() {
        if (playerChromeHider != null) {
            main.removeCallbacks(playerChromeHider);
            playerChromeHider = null;
        }
    }

    private void closePlayer() {
        destroyPlayer();
        getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        if (currentDetail != null) {
            renderDetail(currentDetail);
        } else {
            showHome();
        }
    }

    private void destroyPlayer() {
        stopPlayerChromeHide();
        stopSubtitleOverlayUpdates();
        stopSmoothPlayerSeek();
        stopActiveHlsSession();
        RemoteIsoStreamServer.shared().clear();
        SmbStreamServer.shared().clear();
        if (playerView != null) {
            closePlayerMenu(null);
            stopPlayerUiUpdates();
            stopProgressUpdates();
            flushPlaybackProgress();
            playerView.destroyPlayer();
            playerView = null;
        }
        playerCurrentTime = null;
        playerTotalTime = null;
        playerStatusText = null;
        playerSubtitleOverlay = null;
        playerPgsSubtitleOverlay = null;
        playerPlayPauseButton = null;
        playerAudioButton = null;
        playerSubtitleButton = null;
        playerTopOverlay = null;
        playerControlsPanel = null;
        playerProgressFill = null;
        playerProgressTrack = null;
        playerProgressSegmentViews.clear();
        playerChromeHidden = false;
        playerPaused = false;
        playerHorizontalKeyDown = false;
        playerHorizontalKeyWasChromeHidden = false;
        playerSeekLongPressActive = false;
        playerHorizontalKeyCode = 0;
        playerSeekDirection = 0;
        playerSeekPreviewPosition = -1;
        activeDirectPlayback = false;
        activePlaybackQueue = Collections.emptyList();
        activePlaybackIndex = 0;
        advancingPlaybackPart = false;
        activeExternalSubtitles = Collections.emptyList();
        activeRuntimeAudioTracks = Collections.emptyList();
        activeRuntimeSubtitleTracks = Collections.emptyList();
        activeSubtitleCues = Collections.emptyList();
        activePgsSubtitleCues = Collections.emptyList();
        selectedAudioTrackOrdinal = -1;
        selectedEmbeddedSubtitleOrdinal = -1;
        selectedRuntimeAudioId = null;
        selectedRuntimeSubtitleId = null;
        selectedExternalSubtitleId = null;
        pendingEmbeddedSubtitleOrdinal = -1;
        pendingRuntimeSubtitleId = null;
        pendingExternalSubtitleId = null;
        subtitleLoadStartedAtMs = 0;
        lastKnownPlaybackPositionSeconds = 0;
        lastKnownPlaybackDurationSeconds = 0;
        root.setOnKeyListener(null);
        root.setFocusable(false);
        root.setFocusableInTouchMode(false);
    }

    private void stopActiveHlsSession() {
        String sessionId = activeHlsSessionId;
        activeHlsSessionId = null;
        if (sessionId == null || sessionId.isEmpty()) {
            return;
        }

        io.execute(() -> {
            try {
                api.stopHlsSession(sessionId);
            } catch (Exception ignored) {
            }
        });
    }

    @Override
    protected void onDestroy() {
        destroyPlayer();
        io.shutdownNow();
        if (phoneInput != null) {
            phoneInput.stop();
        }
        super.onDestroy();
    }

    private List<Models.LibraryItem> continueItems(List<Models.LibraryItem> items) {
        ArrayList<Models.LibraryItem> result = new ArrayList<>();
        for (Models.LibraryItem item : items) {
            if (!item.isWatched && item.maxProgressSeconds > 0 && item.maxDurationSeconds > 0) {
                result.add(item);
                if (result.size() >= 12) {
                    break;
                }
            }
        }
        return result;
    }

    /** 首页名单：只按搜索词过滤再排序，不再有类型 / 观看状态筛选。 */
    private List<Models.LibraryItem> displayedItems() {
        String normalizedSearch = searchText == null ? "" : searchText.trim().toLowerCase(Locale.ROOT);
        ArrayList<Models.LibraryItem> result = new ArrayList<>(libraryItems);
        if (!normalizedSearch.isEmpty()) {
            result.removeIf(item -> item.title == null
                    || !item.title.toLowerCase(Locale.ROOT).contains(normalizedSearch));
        }
        result.sort(libraryComparator());
        return result;
    }

    private Comparator<Models.LibraryItem> libraryComparator() {
        return (a, b) -> {
            int direction = sortDescending ? -1 : 1;
            int result;
            if (sortKey == SortKey.TITLE) {
                result = titleCollator.compare(a.title, b.title) * direction;
            } else if (sortKey == SortKey.RATING) {
                result = compareNullableNumber(ratingValue(a.voteAverage), ratingValue(b.voteAverage), direction);
            } else {
                result = compareNullableNumber(yearValue(a.releaseDate), yearValue(b.releaseDate), direction);
            }

            if (result == 0) {
                result = titleCollator.compare(a.title, b.title);
            }
            return result;
        };
    }

    private int compareNullableNumber(Double a, Double b, int direction) {
        if (a == null && b == null) {
            return 0;
        }
        if (a == null) {
            return 1;
        }
        if (b == null) {
            return -1;
        }
        return Double.compare(a, b) * direction;
    }

    private Double ratingValue(Double value) {
        return value != null && value > 0 && Double.isFinite(value) ? value : null;
    }

    private Double yearValue(String releaseDate) {
        if (releaseDate == null || releaseDate.length() < 4) {
            return null;
        }
        try {
            return (double) Integer.parseInt(releaseDate.substring(0, 4));
        } catch (NumberFormatException ignored) {
            return null;
        }
    }

    private Models.VideoFile resolveMainFile(Models.LibraryDetail detail) {
        if ("tv".equals(detail.itemKind)) {
            ArrayList<Models.VideoFile> files = episodeFiles(detail);
            for (Models.VideoFile file : files) {
                if (hasUnfinishedProgress(file)) {
                    return file;
                }
            }
            for (Models.VideoFile file : files) {
                if (!isEffectivelyWatched(file)) {
                    return file;
                }
            }
            if (!files.isEmpty()) {
                return files.get(0);
            }
        }

        ArrayList<Models.VideoFile> movieFiles = moviePlaybackFiles(detail);
        for (Models.VideoFile file : movieFiles) {
            if (hasUnfinishedProgress(file)) {
                return file;
            }
        }
        for (Models.VideoFile file : movieFiles) {
            if (!isEffectivelyWatched(file)) {
                return file;
            }
        }
        if (!movieFiles.isEmpty()) {
            return movieFiles.get(0);
        }

        if (!detail.videoFiles.isEmpty()) {
            return detail.videoFiles.get(0);
        }
        List<Models.VideoFile> playable = detail.playableFiles();
        return playable.isEmpty() ? null : playable.get(0);
    }

    private ArrayList<Models.VideoFile> moviePlaybackFiles(Models.LibraryDetail detail) {
        ArrayList<Models.VideoFile> files = new ArrayList<>();
        if (detail == null || !"movie".equals(detail.itemKind)) {
            return files;
        }
        files.addAll(detail.videoFiles);
        files.sort((left, right) -> {
            int leftPart = moviePartSortIndex(left);
            int rightPart = moviePartSortIndex(right);
            if (leftPart != rightPart) {
                return Integer.compare(leftPart, rightPart);
            }

            int path = compareNaturalText(left.relativePath, right.relativePath);
            if (path != 0) {
                return path;
            }
            int name = compareNaturalText(left.fileName, right.fileName);
            return name != 0 ? name : left.id.compareTo(right.id);
        });
        return files;
    }

    private int moviePartSortIndex(Models.VideoFile file) {
        String raw = (file.relativePath == null ? "" : file.relativePath) + "/" + (file.fileName == null ? "" : file.fileName);
        String[] tokens = raw.split("[\\\\/\\s._\\-\\[\\]\\(\\)\\{\\}【】（）,:：]+");
        for (int index = 0; index < tokens.length; index++) {
            String rawToken = tokens[index];
            if (rawToken == null || rawToken.isEmpty()) {
                continue;
            }
            Integer chinesePart = parseChineseMoviePartToken(rawToken);
            if (chinesePart != null) {
                return chinesePart;
            }

            String token = rawToken.toLowerCase(Locale.ROOT);
            for (String prefix : MOVIE_PART_PREFIXES) {
                if (token.equals(prefix)) {
                    if (index + 1 < tokens.length) {
                        Integer separatedPart = parseMoviePartToken(tokens[index + 1]);
                        if (separatedPart != null) {
                            return separatedPart;
                        }
                    }
                } else if (token.startsWith(prefix)) {
                    Integer joinedPart = parseMoviePartToken(token.substring(prefix.length()));
                    if (joinedPart != null) {
                        return joinedPart;
                    }
                }
            }
        }
        return UNKNOWN_MOVIE_PART_INDEX;
    }

    private Integer parseMoviePartToken(String token) {
        if (token == null) {
            return null;
        }
        String trimmed = token.trim();
        if (trimmed.isEmpty()) {
            return null;
        }
        try {
            int numeric = Integer.parseInt(trimmed);
            return numeric > 0 ? numeric : null;
        } catch (NumberFormatException ignored) {
            // Continue with non-decimal part markers.
        }

        Integer chinesePart = parseChineseMoviePartToken(trimmed);
        if (chinesePart != null) {
            return chinesePart;
        }
        return parseRomanNumber(trimmed);
    }

    private Integer parseChineseMoviePartToken(String token) {
        switch (token) {
            case "上":
            case "上半":
            case "上篇":
            case "前篇":
                return 1;
            case "下":
            case "下半":
            case "下篇":
            case "后篇":
            case "後篇":
                return 2;
            default:
                return null;
        }
    }

    private Integer parseRomanNumber(String token) {
        String roman = token.trim().toUpperCase(Locale.ROOT);
        if (roman.isEmpty()) {
            return null;
        }

        int total = 0;
        for (int index = 0; index < roman.length(); index++) {
            int current = romanDigitValue(roman.charAt(index));
            if (current <= 0) {
                return null;
            }
            int next = index + 1 < roman.length() ? romanDigitValue(roman.charAt(index + 1)) : 0;
            total += current < next ? -current : current;
        }
        return total > 0 ? total : null;
    }

    private int romanDigitValue(char ch) {
        switch (ch) {
            case 'I':
                return 1;
            case 'V':
                return 5;
            case 'X':
                return 10;
            case 'L':
                return 50;
            case 'C':
                return 100;
            case 'D':
                return 500;
            case 'M':
                return 1000;
            default:
                return 0;
        }
    }

    private List<Models.VideoFile> playbackQueueFor(Models.VideoFile file) {
        if (currentDetail == null || file == null || !"movie".equals(currentDetail.itemKind)) {
            return Collections.singletonList(file);
        }

        ArrayList<Models.VideoFile> files = moviePlaybackFiles(currentDetail);
        return files.size() > 1 && indexOfVideoFile(files, file.id) >= 0 ? files : Collections.singletonList(file);
    }

    private int indexOfVideoFile(List<Models.VideoFile> files, String videoFileId) {
        if (files == null || videoFileId == null) {
            return -1;
        }
        for (int index = 0; index < files.size(); index++) {
            if (videoFileId.equals(files.get(index).id)) {
                return index;
            }
        }
        return -1;
    }

    private int compareNaturalText(String left, String right) {
        return Collator.getInstance(Locale.CHINA).compare(left == null ? "" : left, right == null ? "" : right);
    }

    private ArrayList<Models.VideoFile> episodeFiles(Models.LibraryDetail detail) {
        ArrayList<Models.VideoFile> files = new ArrayList<>();
        for (Models.Season season : detail.seasons) {
            for (Models.Episode episode : season.episodes) {
                files.addAll(episodeVideoFiles(episode));
            }
        }
        return files;
    }

    private Models.VideoFile episodePlaybackFile(Models.Episode episode) {
        if (episode.videoFile == null && episode.videoFiles.isEmpty()) {
            return null;
        }
        Models.VideoFile file = episode.videoFile == null ? episode.videoFiles.get(0) : episode.videoFile;
        return file.withEpisodeLabel(episodeDisplayLabel(episode.seasonNumber, episode.episodeNumber));
    }

    private List<Models.VideoFile> episodeVideoFiles(Models.Episode episode) {
        ArrayList<Models.VideoFile> files = new ArrayList<>();
        for (Models.VideoFile file : episode.videoFiles) {
            files.add(file.withEpisodeLabel(episodeDisplayLabel(episode.seasonNumber, episode.episodeNumber)));
        }
        if (files.isEmpty() && episode.videoFile != null) {
            files.add(episode.videoFile.withEpisodeLabel(episodeDisplayLabel(episode.seasonNumber, episode.episodeNumber)));
        }
        return files;
    }

    private boolean hasUnfinishedProgress(Models.VideoFile file) {
        double position = rememberedPositionSeconds(file);
        if (file == null || position <= 5) {
            return false;
        }
        return rememberedDurationSeconds(file) <= 0 || fileProgressPercent(file) < 95;
    }

    private double playbackStartSeconds(Models.VideoFile file) {
        double position = rememberedPositionSeconds(file);
        double duration = rememberedDurationSeconds(file);
        if (file == null || position <= 5) {
            return 0;
        }
        if (isEffectivelyWatched(file)) {
            return 0;
        }
        if (duration > 0 && position >= Math.max(0, duration - 10)) {
            return 0;
        }
        return Math.max(0, position);
    }

    private boolean isEffectivelyWatched(Models.VideoFile file) {
        return file != null && (file.isWatched || fileProgressPercent(file) >= 95);
    }

    private int fileProgressPercent(Models.VideoFile file) {
        double duration = rememberedDurationSeconds(file);
        double position = rememberedPositionSeconds(file);
        if (file == null || duration <= 0 || position <= 0) {
            return 0;
        }
        return Math.max(0, Math.min(100, (int) Math.round(Math.min(position, duration) / duration * 100)));
    }

    private double rememberedPositionSeconds(Models.VideoFile file) {
        if (file == null) {
            return 0;
        }
        PlaybackTimeline remembered = localPlaybackProgress.get(file.id);
        return remembered == null ? Math.max(0, file.positionSeconds) : Math.max(file.positionSeconds, remembered.positionSeconds);
    }

    private double rememberedDurationSeconds(Models.VideoFile file) {
        if (file == null) {
            return 0;
        }
        PlaybackTimeline remembered = localPlaybackProgress.get(file.id);
        return remembered == null ? Math.max(0, file.durationSeconds) : Math.max(file.durationSeconds, remembered.durationSeconds);
    }

    /** 三态文案，与 mac 版 PlaybackWatchState.statusLabel 一一对应。 */
    private String watchedStatusLabel(Models.VideoFile file) {
        if (file == null) {
            return "未观看";
        }
        if (isEffectivelyWatched(file)) {
            return "已观看";
        }
        return file.positionSeconds > 5 ? "观看中" : "未观看";
    }

    private String playButtonText(Models.VideoFile file) {
        return playButtonText(file, true);
    }

    /** 电影页不带集号；剧集页标出点下去会播的那一集。 */
    private String playButtonText(Models.VideoFile file, boolean includeEpisodeLabel) {
        String action = isEffectivelyWatched(file) ? "重新播放" : (file.positionSeconds > 5 ? "继续播放" : "开始播放");
        if (includeEpisodeLabel && file.seasonNumber != null && file.episodeNumber != null) {
            return action + " " + episodeDisplayLabel(file.seasonNumber, file.episodeNumber);
        }
        return action;
    }

    private boolean hasSelectedSeason(Models.LibraryDetail detail) {
        for (Models.Season season : detail.seasons) {
            if (season.id.equals(selectedSeasonId)) {
                return true;
            }
        }
        return false;
    }

    private String defaultSeasonId(Models.LibraryDetail detail, Models.VideoFile mainFile) {
        if (mainFile != null && mainFile.seasonNumber != null) {
            for (Models.Season season : detail.seasons) {
                if (season.seasonNumber == mainFile.seasonNumber) {
                    return season.id;
                }
            }
        }
        return detail.seasons.isEmpty() ? "" : detail.seasons.get(0).id;
    }

    private Models.Season selectedSeason(Models.LibraryDetail detail) {
        for (Models.Season season : detail.seasons) {
            if (season.id.equals(selectedSeasonId)) {
                return season;
            }
        }
        return detail.seasons.isEmpty() ? null : detail.seasons.get(0);
    }

    private String seasonDisplayLabel(Models.Season season) {
        if (season.title != null && !season.title.isEmpty()) {
            return season.title;
        }
        return season.seasonNumber == 0 ? "特别篇" : "第 " + season.seasonNumber + " 季";
    }

    private String episodeTitleDisplay(Models.Episode episode, boolean showDetails) {
        String label = episodeDisplayLabel(episode.seasonNumber, episode.episodeNumber);
        if (showDetails) {
            return episode.title == null || episode.title.isEmpty() ? label : episode.title;
        }

        String subtitle = extractEpisodeSubtitle(episode.title);
        return subtitle.isEmpty() ? label : label + "·" + subtitle;
    }

    /** 集号 + 首播日期；集号已经出现在标题里时只留日期，免得同一行读两遍。 */
    private String episodeFacts(Models.Episode episode, boolean includeLabel) {
        ArrayList<String> facts = new ArrayList<>();
        if (includeLabel) {
            facts.add(episodeDisplayLabel(episode.seasonNumber, episode.episodeNumber));
        }
        if (episode.airDate != null && !episode.airDate.isEmpty()) {
            facts.add(episode.airDate);
        }
        return join(facts, " · ");
    }

    private String episodeDisplayLabel(int seasonNumber, int episodeNumber) {
        return seasonNumber == 0 ? "特别篇第 " + episodeNumber + " 集" : "第 " + seasonNumber + " 季第 " + episodeNumber + " 集";
    }

    private String extractEpisodeSubtitle(String title) {
        if (title == null || title.isEmpty()) {
            return "";
        }

        int separatorIndex = title.indexOf("·");
        return separatorIndex < 0 ? "" : title.substring(separatorIndex + 1).trim();
    }

    private String formatAudioTrack(Models.VideoFileStream track, int ordinal) {
        ArrayList<String> parts = new ArrayList<>();
        parts.add("音轨" + (ordinal + 1));
        parts.add(displayLanguage(track.language, track.title));
        String audioFormat = displayAudioFormat(track);
        if (!audioFormat.isEmpty()) {
            parts.add(audioFormat);
        }
        return joinNonEmpty(parts, " · ");
    }

    private String formatRuntimeAudioTrack(RuntimeTrack track) {
        ArrayList<String> parts = new ArrayList<>();
        parts.add("音轨" + (track.ordinal + 1));
        parts.add(displayLanguage(track.language, track.title));
        String audioFormat = displayRuntimeAudioFormat(track);
        if (!audioFormat.isEmpty()) {
            parts.add(audioFormat);
        }
        return joinNonEmpty(parts, " · ");
    }

    private String formatSubtitleStream(Models.VideoFileStream stream, int ordinal) {
        ArrayList<String> parts = new ArrayList<>();
        parts.add("字幕" + (ordinal + 1));
        parts.add(displaySubtitleLanguage(stream));
        String subtitleFormat = displaySubtitleFormat(stream.codec);
        if (!subtitleFormat.isEmpty()) {
            parts.add(subtitleFormat);
        }
        return joinNonEmpty(parts, " · ");
    }

    private String formatRuntimeSubtitleTrack(RuntimeTrack track) {
        ArrayList<String> parts = new ArrayList<>();
        parts.add("字幕" + (track.ordinal + 1));
        parts.add(displayRuntimeSubtitleLanguage(track));
        String subtitleFormat = displaySubtitleFormat(track.codec);
        if (!subtitleFormat.isEmpty()) {
            parts.add(subtitleFormat);
        }
        return joinNonEmpty(parts, " · ");
    }

    private String formatExternalSubtitle(Models.SubtitleTrack track) {
        ArrayList<String> parts = new ArrayList<>();
        parts.add("外挂字幕");
        parts.add(displayLanguage(track.language, track.fileName));
        String format = displaySubtitleFormat(track.format);
        if (!format.isEmpty()) {
            parts.add(format);
        }
        if (track.fileName != null && !track.fileName.isEmpty()) {
            parts.add(track.fileName);
        }
        return joinNonEmpty(parts, " · ");
    }

    private Models.VideoFileStream resolvePreferredAudioTrack(Models.VideoFile file, String preference) {
        if (file.audioTracks.isEmpty()) {
            return null;
        }

        Models.VideoFileStream defaultTrack = null;
        for (Models.VideoFileStream track : file.audioTracks) {
            if (defaultTrack == null && track.isDefault) {
                defaultTrack = track;
            }
        }

        String requestedLanguage = "smart".equalsIgnoreCase(preference)
                ? resolveSmartAudioLanguage(file)
                : preference;
        if (requestedLanguage != null && !requestedLanguage.isEmpty()) {
            for (Models.VideoFileStream track : file.audioTracks) {
                ArrayList<String> values = new ArrayList<>();
                values.add(track.language);
                values.add(track.title);
                values.add(track.codec);
                values.add(track.channelLayout);
                values.add(track.channels == null ? null : track.channels + "ch");
                if (matchesLanguagePreference(values, requestedLanguage)) {
                    return track;
                }
            }
        }

        return defaultTrack == null ? file.audioTracks.get(0) : defaultTrack;
    }

    private RuntimeTrack resolvePreferredRuntimeAudioTrack(Models.VideoFile file, String preference) {
        if (activeRuntimeAudioTracks.isEmpty()) {
            return null;
        }

        RuntimeTrack defaultTrack = null;
        for (RuntimeTrack track : activeRuntimeAudioTracks) {
            if (defaultTrack == null && track.isDefault) {
                defaultTrack = track;
            }
        }

        String requestedLanguage = "smart".equalsIgnoreCase(preference)
                ? resolveSmartAudioLanguage(file)
                : preference;
        if (requestedLanguage != null && !requestedLanguage.isEmpty()) {
            for (RuntimeTrack track : activeRuntimeAudioTracks) {
                ArrayList<String> values = new ArrayList<>();
                values.add(track.language);
                values.add(track.title);
                values.add(track.codec);
                values.add(track.channelLayout);
                values.add(track.channels == null ? null : track.channels + "ch");
                if (matchesLanguagePreference(values, requestedLanguage)) {
                    return track;
                }
            }
        }

        return defaultTrack == null ? activeRuntimeAudioTracks.get(0) : defaultTrack;
    }

    private Models.SubtitleTrack matchingExternalSubtitle(List<Models.SubtitleTrack> tracks, String preference) {
        for (Models.SubtitleTrack track : tracks) {
            if (api.subtitleUrl(track, activePlaybackTicket).isEmpty()) {
                continue;
            }
            ArrayList<String> values = new ArrayList<>();
            values.add(track.language);
            values.add(track.fileName);
            values.add(track.format);
            if (matchesLanguagePreference(values, preference)) {
                return track;
            }
        }
        return null;
    }

    private Models.SubtitleTrack lastPlayableExternalSubtitle(List<Models.SubtitleTrack> tracks) {
        for (int index = tracks.size() - 1; index >= 0; index--) {
            Models.SubtitleTrack track = tracks.get(index);
            if (!api.subtitleUrl(track, activePlaybackTicket).isEmpty()) {
                return track;
            }
        }
        return null;
    }

    private RuntimeTrack matchingRuntimeSubtitleTrack(Models.VideoFile file, String preference) {
        if (!shouldUseRuntimeSubtitleTracks(file)) {
            return null;
        }
        for (RuntimeTrack track : activeRuntimeSubtitleTracks) {
            if (!isPlayableRuntimeSubtitleForDefault(file, track)) {
                continue;
            }
            ArrayList<String> values = new ArrayList<>();
            values.add(track.language);
            values.add(track.title);
            values.add(track.codec);
            if (matchesLanguagePreference(values, preference)) {
                return track;
            }
        }
        return null;
    }

    private RuntimeTrack lastPlayableRuntimeSubtitleTrack(Models.VideoFile file) {
        if (!shouldUseRuntimeSubtitleTracks(file)) {
            return null;
        }
        for (int index = activeRuntimeSubtitleTracks.size() - 1; index >= 0; index--) {
            RuntimeTrack track = activeRuntimeSubtitleTracks.get(index);
            if (isPlayableRuntimeSubtitleForDefault(file, track)) {
                return track;
            }
        }
        return null;
    }

    private String subtitleTrackKey(Models.SubtitleTrack track) {
        if (track == null) {
            return "";
        }
        String id = track.id == null ? "" : track.id;
        if (!id.isEmpty()) {
            return id;
        }
        return joinNonEmptyValues("|", track.fileName, track.webVttUrl, track.language, track.format);
    }

    private int matchingEmbeddedSubtitleOrdinal(Models.VideoFile file, String preference) {
        for (int index = 0; index < file.subtitleStreams.size(); index++) {
            Models.VideoFileStream stream = file.subtitleStreams.get(index);
            if (!isPlayableEmbeddedSubtitleForDefault(stream)) {
                continue;
            }
            ArrayList<String> values = new ArrayList<>();
            values.add(stream.language);
            values.add(stream.title);
            values.add(stream.codec);
            if (matchesLanguagePreference(values, preference)) {
                return index;
            }
        }
        return -1;
    }

    private int lastPlayableEmbeddedSubtitleOrdinal(Models.VideoFile file) {
        for (int index = file.subtitleStreams.size() - 1; index >= 0; index--) {
            if (isPlayableEmbeddedSubtitleForDefault(file.subtitleStreams.get(index))) {
                return index;
            }
        }
        return -1;
    }

    private boolean isPlayableEmbeddedSubtitleForDefault(Models.VideoFileStream stream) {
        if (activeDirectPlayback && !isIsoFile(activePlaybackFile)) {
            return canUseEmbeddedSubtitleAsWebTrack(stream) || isPgsSubtitle(stream);
        }
        return !(isActiveHlsPlayback() && !canUseEmbeddedSubtitleAsWebTrack(stream) && !isPgsSubtitle(stream));
    }

    private boolean isPlayableRuntimeSubtitleForDefault(Models.VideoFile file, RuntimeTrack track) {
        if (track == null || isActiveHlsPlayback()) {
            return false;
        }
        if (activeDirectPlayback && !isIsoFile(file)) {
            return canUseEmbeddedSubtitleAsWebTrack(track) || isPgsSubtitle(track);
        }
        return true;
    }

    private String resolveSmartAudioLanguage(Models.VideoFile file) {
        ArrayList<String> values = new ArrayList<>();
        if (currentDetail != null) {
            values.add(currentDetail.title);
        }
        values.add(file.fileName);
        values.add(file.relativePath);
        values.add(file.episodeTitle);
        String text = normalizeSearchText(values);
        Set<String> tokens = tokenizeSearchText(text);
        if (tokens.contains("japan") ||
                tokens.contains("japanese") ||
                tokens.contains("jpn") ||
                tokens.contains("jp") ||
                tokens.contains("anime") ||
                containsJapaneseKana(text)) {
            return "ja";
        }
        if (tokens.contains("china") ||
                tokens.contains("chinese") ||
                tokens.contains("mandarin") ||
                tokens.contains("cantonese") ||
                tokens.contains("chn") ||
                tokens.contains("cn") ||
                tokens.contains("hk") ||
                tokens.contains("tw")) {
            return "zh";
        }
        if (tokens.contains("usa") ||
                tokens.contains("us") ||
                tokens.contains("uk") ||
                tokens.contains("gb") ||
                tokens.contains("english") ||
                tokens.contains("eng") ||
                tokens.contains("america") ||
                tokens.contains("britain")) {
            return "en";
        }
        return null;
    }

    private boolean matchesLanguagePreference(List<String> values, String preference) {
        String language = normalizeLanguagePreference(preference);
        String text = normalizeSearchText(values);
        Set<String> tokens = tokenizeSearchText(text);
        if ("en".equals(language)) {
            return tokens.contains("en") ||
                    tokens.contains("eng") ||
                    tokens.contains("english") ||
                    text.contains("英语") ||
                    text.contains("英文") ||
                    text.contains("英語");
        }
        if ("ja".equals(language)) {
            return tokens.contains("ja") ||
                    tokens.contains("jp") ||
                    tokens.contains("jpn") ||
                    tokens.contains("japanese") ||
                    text.contains("日本語") ||
                    text.contains("日语") ||
                    text.contains("日語") ||
                    text.contains("日文");
        }
        return tokens.contains("zh") ||
                tokens.contains("zho") ||
                tokens.contains("chi") ||
                tokens.contains("chs") ||
                tokens.contains("cht") ||
                tokens.contains("cmn") ||
                tokens.contains("yue") ||
                tokens.contains("cn") ||
                tokens.contains("chinese") ||
                text.contains("chinese") ||
                text.contains("mandarin") ||
                text.contains("cantonese") ||
                text.contains("中文") ||
                text.contains("国语") ||
                text.contains("國語") ||
                text.contains("普通话") ||
                text.contains("普通話") ||
                text.contains("粤语") ||
                text.contains("粵語") ||
                text.contains("简体") ||
                text.contains("簡體") ||
                text.contains("繁体") ||
                text.contains("繁體");
    }

    private String normalizeLanguagePreference(String preference) {
        String value = preference == null ? "" : preference.trim().toLowerCase(Locale.ROOT);
        if (value.equals("en") || value.equals("eng") || value.equals("english")) {
            return "en";
        }
        if (value.equals("ja") || value.equals("jp") || value.equals("jpn") || value.equals("japanese")) {
            return "ja";
        }
        return "zh";
    }

    private String normalizeSearchText(List<String> values) {
        StringBuilder builder = new StringBuilder();
        for (String value : values) {
            if (value == null || value.isEmpty()) {
                continue;
            }
            if (builder.length() > 0) {
                builder.append(' ');
            }
            builder.append(value);
        }
        return Normalizer.normalize(builder.toString(), Normalizer.Form.NFKC).toLowerCase(Locale.ROOT);
    }

    private Set<String> tokenizeSearchText(String value) {
        HashSet<String> tokens = new HashSet<>();
        if (value == null || value.isEmpty()) {
            return tokens;
        }
        String[] parts = value.split("[^a-z0-9]+");
        for (String part : parts) {
            if (!part.isEmpty()) {
                tokens.add(part);
            }
        }
        return tokens;
    }

    private boolean containsJapaneseKana(String value) {
        if (value == null) {
            return false;
        }
        for (int index = 0; index < value.length(); index++) {
            char character = value.charAt(index);
            if ((character >= '\u3040' && character <= '\u30ff')) {
                return true;
            }
        }
        return false;
    }

    private String displayLanguage(String language, String fallbackText) {
        String value = language == null ? "" : language.trim().toLowerCase(Locale.ROOT);
        String text = ((fallbackText == null ? "" : fallbackText) + " " + value).toLowerCase(Locale.ROOT);
        if (value.equals("zh") || value.equals("zho") || value.equals("chi") || value.equals("chs") || value.equals("cht") ||
                value.equals("cmn") || value.equals("yue") || text.contains("中文") || text.contains("chinese") ||
                text.contains("mandarin") || text.contains("cantonese") || text.contains("国语") || text.contains("國語") ||
                text.contains("普通话") || text.contains("普通話") || text.contains("粤语") || text.contains("粵語") ||
                text.contains("简体") || text.contains("簡體") || text.contains("繁体") || text.contains("繁體")) {
            return "中文";
        }
        if (value.equals("en") || value.equals("eng") || text.contains("english") || text.contains("英语") || text.contains("英文")) {
            return "英语";
        }
        if (value.equals("ja") || value.equals("jpn") || value.equals("jp") || text.contains("japanese") || text.contains("日语") || text.contains("日文")) {
            return "日语";
        }
        if (value.equals("ko") || value.equals("kor") || value.equals("kr") || text.contains("korean") || text.contains("韩语") || text.contains("韓語")) {
            return "韩语";
        }
        if (value.equals("und") || value.isEmpty()) {
            return "";
        }
        return language;
    }

    private String displaySubtitleLanguage(Models.VideoFileStream stream) {
        ArrayList<String> values = new ArrayList<>();
        values.add(stream.language);
        values.add(stream.title);
        String text = normalizeSearchText(values);
        Set<String> tokens = tokenizeSearchText(text);
        boolean chinese = hasChineseLanguageHint(text, tokens);
        boolean japanese = hasJapaneseLanguageHint(text, tokens);
        if (chinese && japanese) {
            return "中日";
        }
        if (chinese) {
            return "中文";
        }
        if (japanese) {
            return "日语";
        }
        return displayLanguage(stream.language, stream.title);
    }

    private String displayRuntimeSubtitleLanguage(RuntimeTrack track) {
        ArrayList<String> values = new ArrayList<>();
        values.add(track.language);
        values.add(track.title);
        String text = normalizeSearchText(values);
        Set<String> tokens = tokenizeSearchText(text);
        boolean chinese = hasChineseLanguageHint(text, tokens);
        boolean japanese = hasJapaneseLanguageHint(text, tokens);
        if (chinese && japanese) {
            return "中日";
        }
        if (chinese) {
            return "中文";
        }
        if (japanese) {
            return "日语";
        }
        return displayLanguage(track.language, track.title);
    }

    private boolean hasChineseLanguageHint(String text, Set<String> tokens) {
        return tokens.contains("zh") ||
                tokens.contains("zho") ||
                tokens.contains("chi") ||
                tokens.contains("chs") ||
                tokens.contains("cht") ||
                tokens.contains("cmn") ||
                tokens.contains("yue") ||
                tokens.contains("cn") ||
                tokens.contains("chinese") ||
                text.contains("中文") ||
                text.contains("国语") ||
                text.contains("國語") ||
                text.contains("普通话") ||
                text.contains("普通話") ||
                text.contains("粤语") ||
                text.contains("粵語") ||
                text.contains("简体") ||
                text.contains("簡體") ||
                text.contains("繁体") ||
                text.contains("繁體");
    }

    private boolean hasJapaneseLanguageHint(String text, Set<String> tokens) {
        return tokens.contains("ja") ||
                tokens.contains("jp") ||
                tokens.contains("jpn") ||
                tokens.contains("japanese") ||
                text.contains("日本語") ||
                text.contains("日语") ||
                text.contains("日語") ||
                text.contains("日文") ||
                containsJapaneseKana(text);
    }

    private String displayAudioFormat(Models.VideoFileStream track) {
        ArrayList<String> parts = new ArrayList<>();
        String codec = displayAudioCodec(track.codec);
        if (!codec.isEmpty()) {
            parts.add(codec);
        }
        String channels = displayChannels(track);
        if (!channels.isEmpty()) {
            parts.add(channels);
        }
        return joinNonEmpty(parts, " ");
    }

    private String displayRuntimeAudioFormat(RuntimeTrack track) {
        ArrayList<String> parts = new ArrayList<>();
        String codec = displayAudioCodec(track.codec);
        if (!codec.isEmpty()) {
            parts.add(codec);
        }
        String channels = displayChannels(track);
        if (!channels.isEmpty()) {
            parts.add(channels);
        }
        return joinNonEmpty(parts, " ");
    }

    private String displayAudioCodec(String codec) {
        String value = codec == null ? "" : codec.trim().toLowerCase(Locale.ROOT).replace('_', '-');
        if (value.isEmpty()) {
            return "";
        }
        if (value.equals("truehd")) {
            return "TrueHD";
        }
        if (value.equals("eac3") || value.equals("eac-3")) {
            return "DD+";
        }
        if (value.equals("ac3") || value.equals("ac-3")) {
            return "AC-3";
        }
        if (value.equals("dts")) {
            return "DTS";
        }
        if (value.equals("aac")) {
            return "AAC";
        }
        if (value.equals("flac")) {
            return "FLAC";
        }
        if (value.equals("opus")) {
            return "Opus";
        }
        return codec.toUpperCase(Locale.ROOT);
    }

    private String displayChannels(Models.VideoFileStream track) {
        if (track.channelLayout != null && !track.channelLayout.isEmpty()) {
            String layout = track.channelLayout.trim().toLowerCase(Locale.ROOT);
            if (layout.equals("7.1") || layout.contains("7.1")) {
                return "7.1";
            }
            if (layout.equals("5.1") || layout.contains("5.1")) {
                return "5.1";
            }
            if (layout.equals("stereo")) {
                return "2.0";
            }
            if (layout.equals("mono")) {
                return "1.0";
            }
            if (layout.contains("side") && track.channels != null && track.channels == 8) {
                return "7.1";
            }
            if (layout.contains("5.1")) {
                return "5.1";
            }
        }
        if (track.channels == null || track.channels <= 0) {
            return "";
        }
        if (track.channels == 8) {
            return "7.1";
        }
        if (track.channels == 6) {
            return "5.1";
        }
        if (track.channels == 2) {
            return "2.0";
        }
        if (track.channels == 1) {
            return "1.0";
        }
        return track.channels + "ch";
    }

    private String displayChannels(RuntimeTrack track) {
        if (track.channelLayout != null && !track.channelLayout.isEmpty()) {
            String layout = track.channelLayout.trim().toLowerCase(Locale.ROOT);
            if (layout.equals("7.1") || layout.contains("7.1")) {
                return "7.1";
            }
            if (layout.equals("5.1") || layout.contains("5.1")) {
                return "5.1";
            }
            if (layout.equals("stereo")) {
                return "2.0";
            }
            if (layout.equals("mono")) {
                return "1.0";
            }
        }
        if (track.channels == null || track.channels <= 0) {
            return "";
        }
        if (track.channels == 8) {
            return "7.1";
        }
        if (track.channels == 6) {
            return "5.1";
        }
        if (track.channels == 2) {
            return "2.0";
        }
        if (track.channels == 1) {
            return "1.0";
        }
        return track.channels + "ch";
    }

    private String displaySubtitleFormat(String format) {
        String value = format == null ? "" : format.trim().toLowerCase(Locale.ROOT).replace('_', '-');
        if (value.isEmpty()) {
            return "";
        }
        if (value.equals("hdmv-pgs-subtitle") || value.equals("pgssub") || value.equals("sup") || value.equals("pgs")) {
            return "PGS";
        }
        if (value.equals("subrip") || value.equals("srt")) {
            return "SRT";
        }
        if (value.equals("webvtt") || value.equals("vtt")) {
            return "VTT";
        }
        if (value.equals("ass")) {
            return "ASS";
        }
        if (value.equals("ssa")) {
            return "SSA";
        }
        if (value.equals("mov-text")) {
            return "MOV_TEXT";
        }
        return format.toUpperCase(Locale.ROOT);
    }

    private boolean canUseEmbeddedSubtitleAsWebTrack(Models.VideoFileStream stream) {
        String codec = stream.codec == null ? "" : stream.codec.trim().toLowerCase(Locale.ROOT);
        return codec.equals("ass") ||
                codec.equals("ssa") ||
                codec.equals("subrip") ||
                codec.equals("srt") ||
                codec.equals("webvtt") ||
                codec.equals("mov_text") ||
                codec.equals("text");
    }

    private boolean canUseEmbeddedSubtitleAsWebTrack(RuntimeTrack track) {
        String codec = track.codec == null ? "" : track.codec.trim().toLowerCase(Locale.ROOT);
        return codec.equals("ass") ||
                codec.equals("ssa") ||
                codec.equals("subrip") ||
                codec.equals("srt") ||
                codec.equals("webvtt") ||
                codec.equals("mov_text") ||
                codec.equals("mov-text") ||
                codec.equals("text");
    }

    private boolean isPgsSubtitle(Models.VideoFileStream stream) {
        String codec = stream.codec == null ? "" : stream.codec.trim().toLowerCase(Locale.ROOT).replace('_', '-');
        return codec.equals("hdmv-pgs-subtitle") ||
                codec.equals("pgssub") ||
                codec.equals("pgs") ||
                codec.equals("sup");
    }

    private boolean isPgsSubtitle(RuntimeTrack track) {
        String codec = track.codec == null ? "" : track.codec.trim().toLowerCase(Locale.ROOT).replace('_', '-');
        return codec.equals("hdmv-pgs-subtitle") ||
                codec.equals("pgssub") ||
                codec.equals("pgs") ||
                codec.equals("sup");
    }

    private boolean isChineseSubtitle(Models.VideoFileStream stream) {
        String value = ((stream.language == null ? "" : stream.language) + " " + (stream.title == null ? "" : stream.title)).toLowerCase(Locale.ROOT);
        return value.contains("zh") ||
                value.contains("zho") ||
                value.contains("chi") ||
                value.contains("chs") ||
                value.contains("cht") ||
                value.contains("cmn") ||
                value.contains("yue") ||
                value.contains("cn") ||
                value.contains("chinese") ||
                value.contains("中文") ||
                value.contains("简体") ||
                value.contains("繁体");
    }

    private String releaseYearText(String releaseDate) {
        return releaseDate == null || releaseDate.length() < 4 ? null : releaseDate.substring(0, 4);
    }

    /**
     * 未刮削条目的展示名：「The.Last.of.Us.2023.1080p」这类无空格的场景命名改成可读标题；
     * 已带空格的正式片名原样保留。
     */
    /**
     * 卡片与详情页上的片名：先把 WebDAV 带来的百分号编码解回来，再按 mac 版规则整理成看得懂的片名。
     * 老版本入库的条目存的就是编码串，这一步顺便把它们救回来。
     */
    static String displayTitle(String raw) {
        return prettifyMediaTitle(UrlLabels.displayPath(raw));
    }

    static String prettifyMediaTitle(String raw) {
        if (raw == null) {
            return "";
        }
        String value = raw.trim();
        long dots = 0;
        for (int index = 0; index < value.length(); index++) {
            if (value.charAt(index) == '.') {
                dots++;
            }
        }
        if (value.isEmpty() || value.indexOf(' ') >= 0 || (dots < 2 && value.indexOf('_') < 0)) {
            return value;
        }
        String cleaned = value.replace('[', ' ').replace(']', ' ')
                .replace('.', ' ').replace('_', ' ').replaceAll(" {2,}", " ").trim();
        return cleaned.isEmpty() ? value : cleaned;
    }

    private String ratingText(Double voteAverage) {
        return voteAverage != null && voteAverage > 0 && Double.isFinite(voteAverage) ? String.format(Locale.CHINA, "%.1f", voteAverage) : null;
    }

    private String detailOverviewText(Models.LibraryDetail detail) {
        if (detail == null) {
            return "";
        }
        if (detail.overview != null && !detail.overview.trim().isEmpty()) {
            return detail.overview.trim();
        }
        if (detail.douban != null && detail.douban.summary != null && !detail.douban.summary.trim().isEmpty()) {
            return detail.douban.summary.trim();
        }
        return "";
    }

    private List<String> doubanMetadataParts(Models.DoubanMetadata douban) {
        if (douban == null) {
            return Collections.emptyList();
        }
        ArrayList<String> parts = new ArrayList<>();
        for (String item : parseDoubanMetadataList(douban.genres)) {
            if (parts.size() >= 3) {
                break;
            }
            parts.add(item);
        }
        for (String item : parseDoubanMetadataList(douban.countries)) {
            if (parts.size() >= 5) {
                break;
            }
            parts.add(item);
        }
        return parts;
    }

    private List<String> parseDoubanMetadataList(String value) {
        if (value == null || value.trim().isEmpty()) {
            return Collections.emptyList();
        }
        String normalized = value.trim();
        if (normalized.startsWith("[") && normalized.endsWith("]")) {
            normalized = normalized.substring(1, normalized.length() - 1);
        }
        normalized = normalized
                .replace("\"", "")
                .replace("'", "")
                .replace("，", ",")
                .replace("/", ",")
                .replace("、", ",");
        ArrayList<String> result = new ArrayList<>();
        for (String raw : normalized.split(",")) {
            String part = raw.trim();
            if (!part.isEmpty() && !result.contains(part)) {
                result.add(part);
            }
        }
        return result;
    }

    private TextView subtitleCacheStatusView(Models.VideoFile file) {
        Models.SubtitleCacheStatus status = file == null ? null : subtitleCacheStatusCache.get(file.id);
        String label;
        int color;
        if (status == null) {
            if (!hasPrewarmableSubtitle(file)) {
                return null;
            }
            label = "字幕缓存 检测中";
            color = COLOR_SUBTLE;
        } else if (status.subtitleTotal <= 0) {
            return null;
        } else if (status.subtitleCached >= status.subtitleTotal) {
            label = "字幕已缓存 " + status.subtitleCached + "/" + status.subtitleTotal;
            color = Color.rgb(0, 166, 41);
        } else {
            label = "字幕未全缓存 " + status.subtitleCached + "/" + status.subtitleTotal;
            color = Color.rgb(217, 119, 6);
        }
        return metaPill(label, color);
    }

    private void refreshSubtitleCacheStatus(Models.LibraryDetail detail, Models.VideoFile file) {
        if (detail == null || file == null || file.id == null || file.id.isEmpty()) {
            return;
        }
        if (subtitleCacheStatusCache.containsKey(file.id)) {
            return;
        }
        runAsync(
                () -> api.getSubtitleCacheStatus(file.id),
                status -> {
                    // 本地目录没有字幕记录，接口会返回 null；ConcurrentHashMap 不允许 null 值。
                    subtitleCacheStatusCache.put(file.id, status == null
                            ? Models.SubtitleCacheStatus.fromJson(null) : status);
                    if (screen == Screen.DETAIL && currentDetail != null && currentDetail.id.equals(detail.id)) {
                        renderDetail(currentDetail);
                    }
                },
                error -> {
                });
    }

    private boolean hasPrewarmableSubtitle(Models.VideoFile file) {
        if (file == null) {
            return false;
        }
        for (Models.VideoFileStream stream : file.subtitleStreams) {
            if (isPrewarmableSubtitleCodec(stream.codec)) {
                return true;
            }
        }
        return false;
    }

    private boolean isPrewarmableSubtitleCodec(String codec) {
        if (codec == null) {
            return false;
        }
        String normalized = codec.trim().toLowerCase(Locale.ROOT).replace('_', '-');
        return isPgsSubtitleCodec(codec) ||
                normalized.equals("subrip") ||
                normalized.equals("srt") ||
                normalized.equals("webvtt") ||
                normalized.equals("vtt") ||
                normalized.equals("ass") ||
                normalized.equals("ssa");
    }

    private boolean isPgsSubtitleCodec(String codec) {
        if (codec == null) {
            return false;
        }
        String normalized = codec.trim().toLowerCase(Locale.ROOT).replace('_', '-');
        return normalized.equals("hdmv-pgs-subtitle") || normalized.equals("pgs") || normalized.equals("pgssub") || normalized.equals("sup");
    }

    private String formatPlaybackTime(double seconds) {
        int total = Math.max(0, (int) Math.round(seconds));
        int hours = total / 3600;
        int minutes = (total % 3600) / 60;
        int remainingSeconds = total % 60;
        if (hours > 0) {
            return String.format(Locale.CHINA, "%d:%02d:%02d", hours, minutes, remainingSeconds);
        }
        return String.format(Locale.CHINA, "%d:%02d", minutes, remainingSeconds);
    }

    private LinearLayout timeline(Models.LibraryDetail detail, Models.VideoFile file) {
        PlaybackTimeline summary = "movie".equals(detail.itemKind)
                ? savedMovieTimeline(moviePlaybackFiles(detail))
                : new PlaybackTimeline(file == null ? 0 : Math.max(0, file.positionSeconds), file == null ? 0 : Math.max(0, file.durationSeconds));
        LinearLayout timeline = new LinearLayout(this);
        timeline.setGravity(Gravity.CENTER_VERTICAL);
        TextView elapsed = text(formatPlaybackTime(summary.positionSeconds), ssp(12), Typeface.BOLD, COLOR_MUTED);
        elapsed.setGravity(Gravity.RIGHT | Gravity.CENTER_VERTICAL);
        timeline.addView(elapsed, new LinearLayout.LayoutParams(sdp(64), ViewGroup.LayoutParams.MATCH_PARENT));
        int percent = summary.durationSeconds > 0
                ? Math.max(0, Math.min(100, (int) Math.round(Math.min(summary.positionSeconds, summary.durationSeconds) / summary.durationSeconds * 100)))
                : fileProgressPercent(file);
        timeline.addView(progressBar(percent, sdp(308), sdp(6)), margin(new LinearLayout.LayoutParams(sdp(308), sdp(6)), sdp(10), 0, sdp(10), 0));
        TextView total = text(summary.durationSeconds > 0 ? formatPlaybackTime(summary.durationSeconds) : "--:--", ssp(12), Typeface.BOLD, COLOR_MUTED);
        total.setGravity(Gravity.LEFT | Gravity.CENTER_VERTICAL);
        timeline.addView(total, new LinearLayout.LayoutParams(sdp(78), ViewGroup.LayoutParams.MATCH_PARENT));
        return timeline;
    }

    private PlaybackTimeline savedMovieTimeline(List<Models.VideoFile> files) {
        double position = 0;
        double duration = 0;
        for (Models.VideoFile file : files) {
            if (file.durationSeconds <= 0) {
                continue;
            }
            duration += file.durationSeconds;
            position += isEffectivelyWatched(file)
                    ? file.durationSeconds
                    : Math.min(Math.max(0, file.positionSeconds), file.durationSeconds);
        }
        return new PlaybackTimeline(position, duration);
    }

    private FrameLayout progressBar(int percent, int width, int height) {
        return widgets.progressBar(percent, width, height);
    }

    private GridLayout posterGrid() {
        GridLayout grid = new GridLayout(this);
        grid.setColumnCount(HOME_POSTER_COLUMNS);
        grid.setClipChildren(false);
        grid.setClipToPadding(false);
        int sideInset = homePosterGridSideInsetPx();
        int contentWidth = Math.max(1, getResources().getDisplayMetrics().widthPixels - sdp(HOME_PAGE_HORIZONTAL_PADDING_DP) * 2);
        int rowWidth = HOME_POSTER_COLUMNS * (homePosterCardWidthPx() + sdp(HOME_POSTER_CARD_MARGIN_DP) * 2);
        grid.setPadding(sideInset, 0, Math.max(0, contentWidth - rowWidth - sideInset), 0);
        grid.setLayoutParams(matchWidth());
        return grid;
    }

    private int homePosterCardWidthPx() {
        int screenWidth = getResources().getDisplayMetrics().widthPixels;
        int contentWidth = Math.max(1, screenWidth - sdp(HOME_PAGE_HORIZONTAL_PADDING_DP) * 2);
        int totalHorizontalMargins = HOME_POSTER_COLUMNS * sdp(HOME_POSTER_CARD_MARGIN_DP) * 2;
        return Math.max(1, (contentWidth - totalHorizontalMargins) / HOME_POSTER_COLUMNS);
    }

    private int homePosterGridSideInsetPx() {
        int screenWidth = getResources().getDisplayMetrics().widthPixels;
        int contentWidth = Math.max(1, screenWidth - sdp(HOME_PAGE_HORIZONTAL_PADDING_DP) * 2);
        int rowWidth = HOME_POSTER_COLUMNS * (homePosterCardWidthPx() + sdp(HOME_POSTER_CARD_MARGIN_DP) * 2);
        return Math.max(0, (contentWidth - rowWidth) / 2);
    }

    private int homePosterVisualLeftInsetPx() {
        return homePosterGridSideInsetPx() + sdp(HOME_POSTER_CARD_MARGIN_DP + HOME_POSTER_CARD_PADDING_DP);
    }

    private int homeTopPaddingPx() {
        int height = getResources().getDisplayMetrics().heightPixels;
        return Math.max(sdp(38), Math.round(height * 0.032f));
    }

    private int detailEpisodeColumnCount() {
        int availableWidth = Math.max(1, getResources().getDisplayMetrics().widthPixels - sdp(104));
        int itemWidth = sdp(EPISODE_CARD_WIDTH_DP) + sdp(22);
        return Math.max(4, Math.min(6, availableWidth / Math.max(1, itemWidth)));
    }

    private void loadPoster(ImageView image, String posterAssetId) {
        loadPoster(image, posterAssetId, 0, 0);
    }

    private void loadPoster(ImageView image, String posterAssetId, int targetWidth, int targetHeight) {
        if (posterAssetId != null) {
            imageLoader.load(image, api.posterUrl(posterAssetId), api.cookieHeader(), targetWidth, targetHeight);
        } else {
            imageLoader.load(image, null, api.cookieHeader(), targetWidth, targetHeight);
        }
    }

    private <T> void runAsync(Task<T> task, Success<T> success, Failure failure) {
        io.execute(() -> {
            T result = null;
            Throwable raised = null;
            try {
                result = task.run();
            } catch (Throwable error) {
                // Errors (missing class, linkage problems, OOM) used to escape here and take the
                // whole app down, which on a TV looks like a random restart.
                CrashLog.record(this, "后台任务", error);
                raised = error;
            }
            final T value = result;
            final Throwable raisedError = raised;
            main.post(() -> {
                try {
                    if (raisedError == null) {
                        success.accept(value);
                    } else {
                        failure.accept(raisedError);
                    }
                } catch (Throwable callbackBroke) {
                    // A broken callback on the main thread is what a "闪退" usually is.
                    CrashLog.record(this, "界面回调", callbackBroke);
                    showToast(CrashLog.summarize(callbackBroke));
                }
            });
        });
    }

    private EditText input(String hint, String value) {
        return widgets.input(hint, value);
    }

    /** The two-tone chip in front of a theme name: panel colour on the right, accent on the left. */
    private Drawable themeSwatch(Theme.Variant variant) {
        GradientDrawable base = new GradientDrawable();
        base.setColor(variant.surface);
        base.setCornerRadius(dp(3));
        base.setStroke(dp(1), Theme.blend(variant.surface,
                variant.dark ? Color.WHITE : Color.BLACK, 0.24f));
        GradientDrawable accent = new GradientDrawable();
        accent.setColor(variant.accent);
        accent.setCornerRadius(dp(2));
        LayerDrawable layer = new LayerDrawable(new Drawable[]{base, accent});
        layer.setLayerInset(1, dp(3), dp(3), Math.round(dp(30) * 0.55f), dp(3));
        layer.setBounds(0, 0, dp(30), dp(18));
        return layer;
    }

    private Button compactButton(String label) {
        return widgets.toolbarButton(label);
    }

    private Button playerButton(String label) {
        Button button = new Button(this);
        button.setText(label);
        button.setTextSize(ssp(14));
        button.setAllCaps(false);
        button.setTextColor(Color.WHITE);
        button.setGravity(Gravity.CENTER);
        button.setPadding(sdp(12), 0, sdp(12), 0);
        button.setMinWidth(0);
        button.setMinHeight(0);
        button.setMinimumWidth(0);
        button.setMinimumHeight(0);
        button.setIncludeFontPadding(false);
        button.setBackgroundTintList(null);
        button.setStateListAnimator(null);
        button.setClipToOutline(true);
        button.setBackground(rounded(Color.argb(70, 255, 255, 255), Color.argb(86, 255, 255, 255), FOCUS_STROKE_PX, sdp(24)));
        button.setFocusable(true);
        button.setFocusableInTouchMode(false);
        theme.stylePlayerButtonFocus(button);
        return button;
    }

    private ImageButton playerIconButton(int iconResId) {
        ImageButton button = new ImageButton(this);
        button.setImageResource(iconResId);
        button.setColorFilter(Color.WHITE);
        button.setScaleType(ImageView.ScaleType.CENTER);
        button.setPadding(sdp(13), sdp(11), sdp(13), sdp(11));
        button.setBackgroundTintList(null);
        button.setStateListAnimator(null);
        button.setClipToOutline(true);
        button.setBackground(rounded(Color.argb(26, 255, 255, 255), Color.argb(136, 255, 255, 255), FOCUS_STROKE_PX, sdp(24)));
        button.setFocusable(true);
        button.setFocusableInTouchMode(false);
        theme.stylePlayerIconFocus(button);
        return button;
    }

    private Button playerMenuButton(String label, boolean selected) {
        Button button = new Button(this);
        button.setText(label);
        button.setTextSize(ssp(14));
        button.setAllCaps(false);
        button.setTextColor(selected ? COLOR_ACCENT : Color.WHITE);
        button.setGravity(Gravity.CENTER_VERTICAL);
        button.setPadding(sdp(16), 0, sdp(16), 0);
        button.setMinWidth(0);
        button.setMinHeight(0);
        button.setMinimumWidth(0);
        button.setMinimumHeight(0);
        button.setIncludeFontPadding(false);
        button.setSingleLine(true);
        button.setEllipsize(TextUtils.TruncateAt.END);
        button.setBackgroundTintList(null);
        button.setStateListAnimator(null);
        button.setClipToOutline(true);
        button.setBackground(rounded(Color.argb(selected ? 44 : 18, 255, 255, 255), Color.argb(112, 255, 255, 255), FOCUS_STROKE_PX, sdp(10)));
        button.setFocusable(true);
        button.setFocusableInTouchMode(false);
        theme.stylePlayerMenuFocus(button, selected);
        return button;
    }

    // ---- shared controls: every button/label in this app comes from ui/Widgets ----

    private Button button(String label) {
        return widgets.button(label);
    }

    private Button button(String label, boolean active) {
        return widgets.button(label, active);
    }

    private TextView title(String value) {
        return widgets.display(value);
    }

    private TextView sectionTitle(String value) {
        return widgets.sectionTitle(value);
    }

    private TextView body(String value) {
        return widgets.body(value);
    }

    private TextView emptyText(String value) {
        return widgets.empty(value);
    }

    private TextView metaPill(String value) {
        return widgets.metaPill(value);
    }

    private TextView metaPill(String value, int textColor) {
        return widgets.metaPill(value, textColor);
    }

    private TextView badge(String value) {
        return widgets.badge(value, COLOR_TEXT);
    }

    private TextView badge(String value, int textColor) {
        return widgets.badge(value, textColor);
    }

    private TextView text(String value, int sp, int style, int color) {
        return widgets.text(value, sp, style, color);
    }

    // ---- focus styles: ui/Theme owns them so a card cannot look different per screen ----

    private void applyPosterCardFocus(View card, FrameLayout posterFrame, Runnable onFocused) {
        theme.stylePosterFocus(card, posterFrame, onFocused);
    }

    /** 分集缩略图与海报卡片用同一套焦点样式：一张阴影，背景预先画好，换焦点时只换引用。 */
    private void applyEpisodeStillFocus(View card, FrameLayout stillFrame, boolean showDetails, Button playButton, Models.VideoFile playbackFile) {
        applyPosterCardFocus(card, stillFrame, () -> {
            if (playButton != null && playbackFile != null) {
                playButton.setText(playButtonText(playbackFile));
                playButton.setTag(playbackFile);
            }
        });
    }

    /** 观看状态那颗图标：静止时用自己的颜色，拿到焦点只抬高并染主题色。 */
    private void applyWatchedIconFocus(TextView icon, int restColor) {
        icon.setBackgroundColor(Color.TRANSPARENT);
        icon.setTextColor(restColor);
        icon.setOnFocusChangeListener((focusedView, hasFocus) -> {
            animateFocusScale(focusedView, hasFocus);
            icon.setTextColor(hasFocus ? COLOR_FOCUS : restColor);
        });
    }

    private void animateFocusScale(View view, boolean hasFocus) {
        theme.animateFocusScale(view, hasFocus);
    }

    private void applyAppBackground() {
        root.setBackground(theme.appBackground());
    }

    private GradientDrawable rounded(int fillColor, int strokeColor, int strokeWidth, int radius) {
        return theme.panel(fillColor, strokeColor, strokeWidth, radius);
    }

    private FrameLayout.LayoutParams match() {
        return Widgets.fill();
    }

    private LinearLayout.LayoutParams matchWidth() {
        return Widgets.column();
    }

    private LinearLayout.LayoutParams margin(LinearLayout.LayoutParams params, int left, int top, int right, int bottom) {
        params.setMargins(left, top, right, bottom);
        return params;
    }

    private int dp(float value) {
        return theme.dp(value);
    }

    private int sdp(float value) {
        return theme.sdp(value);
    }

    private int ssp(int value) {
        return theme.ssp(value);
    }

    private String join(List<String> values, String separator) {
        StringBuilder builder = new StringBuilder();
        for (String value : values) {
            if (builder.length() > 0) {
                builder.append(separator);
            }
            builder.append(value);
        }
        return builder.toString();
    }

    private String joinNonEmpty(List<String> values, String separator) {
        StringBuilder builder = new StringBuilder();
        for (String value : values) {
            if (value == null || value.isEmpty()) {
                continue;
            }
            if (builder.length() > 0) {
                builder.append(separator);
            }
            builder.append(value);
        }
        return builder.toString();
    }

    private String joinNonEmptyValues(String separator, String... values) {
        ArrayList<String> list = new ArrayList<>();
        if (values != null) {
            Collections.addAll(list, values);
        }
        return joinNonEmpty(list, separator);
    }

    private enum Screen {
        SETUP,
        HOME,
        DETAIL,
        PLAYER
    }

    private enum SortKey {
        TITLE,
        RATING,
        YEAR
    }

    private static final class ResolvedPlayback {
        final String url;
        final String ticket;
        final String cookieHeader;
        final Models.AppSettings settings;
        final Models.PlaybackFileStreams streams;
        final String hlsSessionId;
        final String reason;
        final boolean isoDevicePlayback;

        ResolvedPlayback(String url, String ticket, String cookieHeader, Models.AppSettings settings, Models.PlaybackFileStreams streams, String hlsSessionId, String reason) {
            this(url, ticket, cookieHeader, settings, streams, hlsSessionId, reason, false);
        }

        ResolvedPlayback(String url, String ticket, String cookieHeader, Models.AppSettings settings, Models.PlaybackFileStreams streams, String hlsSessionId, String reason, boolean isoDevicePlayback) {
            this.url = url;
            this.ticket = ticket == null ? "" : ticket;
            this.cookieHeader = cookieHeader == null ? "" : cookieHeader;
            this.settings = settings == null ? Models.AppSettings.defaults() : settings;
            this.streams = streams;
            this.hlsSessionId = hlsSessionId == null ? "" : hlsSessionId;
            this.reason = reason == null ? "" : reason;
            this.isoDevicePlayback = isoDevicePlayback;
        }
    }

    private static final class SubtitleCandidate {
        final Models.SubtitleTrack externalTrack;
        final RuntimeTrack runtimeTrack;
        final int embeddedOrdinal;
        final String externalKey;
        final String runtimeKey;

        private SubtitleCandidate(Models.SubtitleTrack externalTrack, RuntimeTrack runtimeTrack, int embeddedOrdinal) {
            this.externalTrack = externalTrack;
            this.runtimeTrack = runtimeTrack;
            this.embeddedOrdinal = embeddedOrdinal;
            this.externalKey = externalTrack == null ? "" : joinStatic("|", externalTrack.id, externalTrack.fileName, externalTrack.webVttUrl, externalTrack.language, externalTrack.format);
            this.runtimeKey = runtimeTrack == null ? "" : joinStatic("|", runtimeTrack.type, runtimeTrack.mpvId, String.valueOf(runtimeTrack.ordinal));
        }

        static SubtitleCandidate external(Models.SubtitleTrack track) {
            return new SubtitleCandidate(track, null, -1);
        }

        static SubtitleCandidate runtime(RuntimeTrack track) {
            return new SubtitleCandidate(null, track, -1);
        }

        static SubtitleCandidate embedded(int ordinal) {
            return new SubtitleCandidate(null, null, ordinal);
        }

        boolean sameAs(SubtitleCandidate other) {
            if (other == null) {
                return false;
            }
            if (externalTrack != null || other.externalTrack != null) {
                return externalTrack != null && other.externalTrack != null && externalKey.equals(other.externalKey);
            }
            if (runtimeTrack != null || other.runtimeTrack != null) {
                return runtimeTrack != null && other.runtimeTrack != null && runtimeKey.equals(other.runtimeKey);
            }
            return embeddedOrdinal == other.embeddedOrdinal;
        }

        private static String joinStatic(String separator, String... values) {
            StringBuilder builder = new StringBuilder();
            for (String value : values) {
                if (value == null || value.isEmpty()) {
                    continue;
                }
                if (builder.length() > 0) {
                    builder.append(separator);
                }
                builder.append(value);
            }
            return builder.toString();
        }
    }

    private static final class RuntimeTrack {
        final int ordinal;
        final String mpvId;
        final String type;
        final String codec;
        final String language;
        final String title;
        final Integer channels;
        final String channelLayout;
        final boolean selected;
        final boolean isDefault;
        final boolean isForced;

        RuntimeTrack(
                int ordinal,
                String mpvId,
                String type,
                String codec,
                String language,
                String title,
                Integer channels,
                String channelLayout,
                boolean selected,
                boolean isDefault,
                boolean isForced) {
            this.ordinal = Math.max(0, ordinal);
            this.mpvId = mpvId == null || mpvId.isEmpty() ? String.valueOf(this.ordinal + 1) : mpvId;
            this.type = type == null ? "" : type;
            this.codec = codec == null ? "" : codec;
            this.language = language == null ? "" : language;
            this.title = title == null ? "" : title;
            this.channels = channels;
            this.channelLayout = channelLayout == null ? "" : channelLayout;
            this.selected = selected;
            this.isDefault = isDefault;
            this.isForced = isForced;
        }
    }

    private static final class PlayerMenuOption {
        final String label;
        final boolean selected;
        final Runnable action;

        PlayerMenuOption(String label, Runnable action) {
            this(label, false, action);
        }

        PlayerMenuOption(String label, boolean selected, Runnable action) {
            this.label = label;
            this.selected = selected;
            this.action = action;
        }

        String displayLabel() {
            // 选中的那一行本身就被染成主题色，不需要再加对号。
            return label;
        }
    }

    private static final class PlaybackTimeline {
        final double positionSeconds;
        final double durationSeconds;

        PlaybackTimeline(double positionSeconds, double durationSeconds) {
            this.positionSeconds = Math.max(0, positionSeconds);
            this.durationSeconds = Math.max(0, durationSeconds);
        }
    }

    private static final class SubtitleCue {
        final double startSeconds;
        final double endSeconds;
        final String text;

        SubtitleCue(double startSeconds, double endSeconds, String text) {
            this.startSeconds = startSeconds;
            this.endSeconds = endSeconds;
            this.text = text == null ? "" : text;
        }
    }

    private interface Task<T> {
        T run() throws Exception;
    }

    private interface Success<T> {
        void accept(T result);
    }

    private interface Failure {
        void accept(Throwable error);
    }
}
