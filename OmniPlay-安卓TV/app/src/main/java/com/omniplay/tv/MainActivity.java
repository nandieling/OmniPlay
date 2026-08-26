package com.omniplay.tv;

import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.text.Html;
import android.text.Editable;
import android.text.InputType;
import android.text.TextUtils;
import android.text.TextWatcher;
import android.view.Display;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.View;
import android.view.ViewGroup;
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
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.TextView;

import com.omniplay.tv.data.Models;
import com.omniplay.tv.data.LuckyStunClient;
import com.omniplay.tv.data.OmniPlayApi;
import com.omniplay.tv.iso.RemoteIsoStreamServer;
import com.omniplay.tv.player.MpvVideoView;
import com.omniplay.tv.subtitle.PgsSubtitleCue;
import com.omniplay.tv.subtitle.PgsSubtitleParser;
import com.omniplay.tv.subtitle.PgsSubtitleView;
import com.omniplay.tv.ui.ImageLoader;

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
import java.util.Collections;
import java.util.Comparator;
import java.util.Enumeration;
import java.util.HashSet;
import java.util.LinkedHashSet;
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
import java.util.regex.Matcher;
import java.util.regex.Pattern;

public final class MainActivity extends Activity {
    private static final String UI_PREFS = "omniplay_tv_ui";
    private static final String KEY_4K_DV_HDR_PLAYBACK_MODE = "4k_dv_hdr_playback_mode";
    private static final int DOCKER_SERVER_PORT = 45722;
    private static final String PLAYBACK_MODE_COMPATIBLE_SUBTITLE = "compatible_subtitle";
    private static final String PLAYBACK_MODE_HIGH_PERFORMANCE_DIRECT = "high_performance_direct";
    private static final int COLOR_BACKGROUND = Color.rgb(245, 247, 250);
    private static final int COLOR_SURFACE = Color.rgb(255, 255, 255);
    private static final int COLOR_SURFACE_ALT = Color.rgb(243, 244, 246);
    private static final int COLOR_BORDER = Color.rgb(221, 226, 235);
    private static final int COLOR_TEXT = Color.rgb(17, 24, 39);
    private static final int COLOR_MUTED = Color.rgb(75, 85, 99);
    private static final int COLOR_SUBTLE = Color.rgb(107, 114, 128);
    private static final int COLOR_FOCUS = Color.rgb(37, 99, 235);
    private static final int COLOR_ACCENT = Color.rgb(20, 184, 166);
    private static final int COLOR_FOCUS_SOFT = Color.rgb(232, 240, 255);
    private static final int COLOR_DANGER = Color.rgb(185, 28, 28);
    private static final int COLOR_PLAYER_TEXT = Color.WHITE;
    private static final int COLOR_PLAYER_MUTED = Color.rgb(229, 231, 235);
    private static final int UNKNOWN_MOVIE_PART_INDEX = Integer.MAX_VALUE;
    private static final int PLAYER_SEEK_REPEAT_MS = 80;
    private static final double PLAYER_SEEK_STEP_SECONDS = 0.8d;
    private static final int FOCUS_STROKE_PX = 1;
    private static final float FOCUS_SCALE = 1.04f;
    private static final int FOCUS_ANIMATION_MS = 120;
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
    private OmniPlayApi api;
    private ImageLoader imageLoader;
    private SharedPreferences uiPreferences;
    private List<Models.LibraryItem> libraryItems = Collections.emptyList();
    private final Map<String, Models.LibraryDetail> detailCache = new ConcurrentHashMap<>();
    private final Map<String, Models.SubtitleCacheStatus> subtitleCacheStatusCache = new ConcurrentHashMap<>();
    private final Map<String, PlaybackTimeline> localPlaybackProgress = new ConcurrentHashMap<>();
    private final Set<String> pendingDetailPrefetches = Collections.newSetFromMap(new ConcurrentHashMap<>());
    private Models.LibraryDetail currentDetail;
    private MpvVideoView playerView;
    private ScrollView homeScrollView;
    private View settingsDim;
    private View settingsPanel;
    private View settingsInitialFocus;
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
    private int homeScrollY;
    private boolean restoreHomeScroll;
    private boolean advancingPlaybackPart;
    private Screen screen = Screen.SETUP;
    private boolean isSearchOpen;
    private boolean isSortOpen;
    private boolean isSettingsOpen;
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

        api = new OmniPlayApi(this);
        imageLoader = new ImageLoader(this);
        uiPreferences = getApplicationContext().getSharedPreferences(UI_PREFS, MODE_PRIVATE);
        highPerformanceDirectPlayback = PLAYBACK_MODE_HIGH_PERFORMANCE_DIRECT.equals(uiPreferences.getString(
                KEY_4K_DV_HDR_PLAYBACK_MODE,
                PLAYBACK_MODE_COMPATIBLE_SUBTITLE));
        root = new FrameLayout(this);
        root.setClipChildren(false);
        root.setClipToPadding(false);
        applyAppBackground();
        setContentView(root);

        if (api.hasServerUrl()) {
            checkSession();
        } else {
            showSetup(null);
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

    private void checkSession() {
        showLoading("正在连接 OmniPlay 服务端");
        runAsync(
                () -> api.authStatus(),
                status -> {
                    if (status.isAuthenticated) {
                        showHome();
                    } else {
                        showSetup(status.requiresSetup ? "请先在网页端完成管理员初始化。" : null);
                    }
                },
                error -> showSetup(error.getMessage()));
    }

    private void showSetup(String message) {
        screen = Screen.SETUP;
        currentDetail = null;
        destroyPlayer();
        root.removeAllViews();
        applyAppBackground();

        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setClipChildren(false);
        scroll.setClipToPadding(false);
        root.addView(scroll, match());

        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setGravity(Gravity.CENTER_HORIZONTAL);
        page.setPadding(dp(96), dp(28), dp(96), dp(28));
        page.setClipChildren(false);
        page.setClipToPadding(false);
        scroll.addView(page, new ScrollView.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(dp(40), dp(26), dp(40), dp(28));
        panel.setClipChildren(false);
        panel.setClipToPadding(false);
        panel.setBackground(rounded(COLOR_SURFACE, COLOR_BORDER, dp(2), dp(8)));
        panel.setElevation(dp(12));
        page.addView(panel, new LinearLayout.LayoutParams(dp(720), ViewGroup.LayoutParams.WRAP_CONTENT));

        panel.addView(title("OmniPlay TV"));
        panel.addView(body("连接docker服务端，安卓TV端只播放，不扫描刮削。"));

        EditText serverInput = input("Docker 服务端地址，例如 http://192.168.1.10:45722", api.serverUrl());
        panel.addView(serverInput, margin(matchWidth(), 0, dp(18), 0, 0));
        TextView discoveryStatus = text("", 13, Typeface.BOLD, COLOR_SUBTLE);
        panel.addView(discoveryStatus, margin(matchWidth(), 0, dp(6), 0, dp(8)));

        EditText usernameInput = input("用户名", api.savedUsername());
        panel.addView(usernameInput, margin(matchWidth(), 0, dp(10), 0, 0));

        EditText passwordInput = input("密码", api.savedPassword());
        passwordInput.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        panel.addView(passwordInput, margin(matchWidth(), 0, dp(10), 0, 0));

        if (message != null && !message.isEmpty()) {
            TextView error = body(message);
            error.setTextColor(COLOR_DANGER);
            panel.addView(error, margin(matchWidth(), 0, 0, 0, dp(16)));
        }

        LinearLayout actions = new LinearLayout(this);
        actions.setGravity(Gravity.CENTER_VERTICAL);
        actions.setClipChildren(false);
        actions.setClipToPadding(false);
        panel.addView(actions, margin(matchWidth(), 0, dp(14), 0, 0));

        Button login = button("登录");
        login.setId(View.generateViewId());
        actions.addView(login, new LinearLayout.LayoutParams(dp(180), dp(52)));
        Button scan = button("预扫描");
        scan.setId(View.generateViewId());
        actions.addView(scan, margin(new LinearLayout.LayoutParams(dp(180), dp(52)), dp(14), 0, 0, 0));
        login.setNextFocusRightId(scan.getId());
        scan.setNextFocusLeftId(login.getId());
        login.setOnClickListener(view -> {
            String server = serverInput.getText().toString();
            String username = usernameInput.getText().toString();
            String password = passwordInput.getText().toString();
            showLoading("正在登录");
            runAsync(
                    () -> api.login(server, username, password),
                    status -> showHome(),
                    error -> showSetup(error.getMessage()));
        });
        scan.setOnClickListener(view -> {
            discoveryStatus.setText("正在预扫描 Docker 服务端");
            scan.setEnabled(false);
            runAsync(
                    this::discoverServerUrl,
                    serverUrl -> {
                        scan.setEnabled(true);
                        if (serverUrl != null && !serverUrl.isEmpty()) {
                            serverInput.setText(serverUrl);
                            serverInput.setSelection(serverInput.getText().length());
                            discoveryStatus.setText("已发现 Docker 服务端");
                        } else {
                            discoveryStatus.setText("未发现 Docker 服务端，可手动输入");
                        }
                    },
                    error -> {
                        scan.setEnabled(true);
                        discoveryStatus.setText("未发现 Docker 服务端，可手动输入");
                    });
        });
        serverInput.requestFocus();
        startServerDiscovery(serverInput, discoveryStatus);
    }

    private void startServerDiscovery(EditText serverInput, TextView discoveryStatus) {
        if (serverInput.getText().toString().trim().length() > 0) {
            return;
        }

        discoveryStatus.setText("正在预扫描 Docker 服务端");
        runAsync(
                this::discoverServerUrl,
                serverUrl -> {
                    if (screen != Screen.SETUP) {
                        return;
                    }
                    if (serverUrl != null && !serverUrl.isEmpty() && serverInput.getText().toString().trim().isEmpty()) {
                        serverInput.setText(serverUrl);
                        serverInput.setSelection(serverInput.getText().length());
                        discoveryStatus.setText("已发现 Docker 服务端");
                    } else {
                        discoveryStatus.setText("未发现 Docker 服务端，可手动输入");
                    }
                },
                error -> {
                    if (screen == Screen.SETUP) {
                        discoveryStatus.setText("未发现 Docker 服务端，可手动输入");
                    }
                });
    }

    private String discoverServerUrl() throws Exception {
        Set<String> candidates = new LinkedHashSet<>();
        Enumeration<NetworkInterface> interfaces = NetworkInterface.getNetworkInterfaces();
        while (interfaces != null && interfaces.hasMoreElements()) {
            NetworkInterface networkInterface = interfaces.nextElement();
            if (!networkInterface.isUp() || networkInterface.isLoopback()) {
                continue;
            }

            for (InterfaceAddress interfaceAddress : networkInterface.getInterfaceAddresses()) {
                InetAddress address = interfaceAddress.getAddress();
                if (!(address instanceof Inet4Address) || address.isLoopbackAddress() || !isLanIPv4(address)) {
                    continue;
                }

                byte[] bytes = address.getAddress();
                int prefixLength = interfaceAddress.getNetworkPrefixLength();
                if (prefixLength < 0 || prefixLength > 32) {
                    prefixLength = 24;
                }
                long ipValue = ((long) (bytes[0] & 0xff) << 24)
                        | ((long) (bytes[1] & 0xff) << 16)
                        | ((long) (bytes[2] & 0xff) << 8)
                        | (long) (bytes[3] & 0xff);
                long mask = prefixLength == 0 ? 0 : (0xffffffffL << (32 - prefixLength)) & 0xffffffffL;
                long network = ipValue & mask;
                long broadcast = network | (~mask & 0xffffffffL);
                long hostCount = broadcast > network ? broadcast - network - 1 : 0;
                if (hostCount > 0 && hostCount <= 1022) {
                    for (long value = network + 1; value < broadcast; value++) {
                        candidates.add(formatIPv4(value));
                    }
                } else {
                    long local24 = ipValue & 0xffffff00L;
                    for (int host = 1; host <= 254; host++) {
                        candidates.add(formatIPv4(local24 | host));
                    }
                }
            }
        }

        if (candidates.isEmpty()) {
            return "";
        }

        ExecutorService scanner = Executors.newFixedThreadPool(Math.min(32, candidates.size()));
        ExecutorCompletionService<String> completion = new ExecutorCompletionService<>(scanner);
        ArrayList<Future<String>> futures = new ArrayList<>();
        for (String host : candidates) {
            futures.add(completion.submit(() -> probeServer(host)));
        }

        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(15);
        try {
            for (int remaining = futures.size(); remaining > 0; remaining--) {
                long waitNanos = deadline - System.nanoTime();
                if (waitNanos <= 0) {
                    break;
                }

                Future<String> future = completion.poll(waitNanos, TimeUnit.NANOSECONDS);
                if (future == null) {
                    break;
                }

                String result = future.get();
                if (result != null && !result.isEmpty()) {
                    return result;
                }
            }
        } finally {
            for (Future<String> future : futures) {
                future.cancel(true);
            }
            scanner.shutdownNow();
        }

        return "";
    }

    private String probeServer(String host) {
        String url = "http://" + host + ":" + DOCKER_SERVER_PORT;
        HttpURLConnection connection = null;
        try {
            connection = (HttpURLConnection) new URL(url + "/api/auth/status").openConnection();
            connection.setInstanceFollowRedirects(true);
            connection.setConnectTimeout(1000);
            connection.setReadTimeout(2500);
            connection.setRequestProperty("Accept", "application/json");
            connection.setRequestProperty("User-Agent", "OmniPlay-Android/1.5");
            int status = connection.getResponseCode();
            if (status >= 200 && status < 300) {
                String body = readRemoteText(connection.getInputStream()).toLowerCase(Locale.ROOT);
                if (body.contains("\"issetuprequired\"")
                        || body.contains("\"isauthenticated\"")
                        || body.contains("\"service\":\"omniplay.server\"")) {
                    return url;
                }
            }
        } catch (Exception ignored) {
        } finally {
            if (connection != null) {
                connection.disconnect();
            }
        }
        return "";
    }

    private static boolean isLanIPv4(InetAddress address) {
        if (!(address instanceof Inet4Address)) {
            return false;
        }
        byte[] bytes = address.getAddress();
        int first = bytes[0] & 0xff;
        int second = bytes[1] & 0xff;
        return address.isSiteLocalAddress()
                || first == 100 && second >= 64 && second <= 127
                || first == 169 && second == 254;
    }

    private static String formatIPv4(long value) {
        return ((value >> 24) & 0xff) + "."
                + ((value >> 16) & 0xff) + "."
                + ((value >> 8) & 0xff) + "."
                + (value & 0xff);
    }

    private void showHome() {
        screen = Screen.HOME;
        boolean returningFromDetail = currentDetail != null;
        currentDetail = null;
        selectedSeasonId = "";
        homeScrollY = 0;
        restoreHomeScroll = false;
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

    private void renderHomeContent() {
        screen = Screen.HOME;
        root.removeAllViews();
        applyAppBackground();

        ScrollView scroll = new ScrollView(this);
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

        View firstFocusable = null;
        List<Models.LibraryItem> continueItems = continueItems(libraryItems);
        int headerInset = homePosterVisualLeftInsetPx();

        LinearLayout continueHeader = new LinearLayout(this);
        continueHeader.setGravity(Gravity.CENTER_VERTICAL);
        continueHeader.setClipChildren(false);
        continueHeader.setClipToPadding(false);
        continueHeader.setPadding(headerInset, 0, headerInset, 0);
        page.addView(continueHeader, margin(matchWidth(), 0, continueItems.isEmpty() ? sdp(12) : 0, 0, sdp(12)));
        TextView continueTitle = sectionTitle("继续播放");
        continueTitle.setPadding(0, 0, 0, 0);
        continueHeader.addView(continueTitle, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        Button settings = compactButton("设置");
        continueHeader.addView(settings, new LinearLayout.LayoutParams(sdp(82), sdp(42)));
        settings.setOnClickListener(view -> {
            openSettingsOverlay();
        });

        if (continueItems.isEmpty()) {
            TextView emptyContinue = emptyText("暂无继续播放");
            emptyContinue.setPadding(headerInset, 0, 0, sdp(20));
            page.addView(emptyContinue);
        } else {
            GridLayout continueGrid = posterGrid();
            page.addView(continueGrid, margin(matchWidth(), 0, 0, 0, sdp(14)));
            for (Models.LibraryItem item : continueItems) {
                View card = posterCard(item, true);
                if (firstFocusable == null) {
                    firstFocusable = card;
                }
                continueGrid.addView(card);
            }
        }

        LinearLayout libraryHeader = new LinearLayout(this);
        libraryHeader.setGravity(Gravity.CENTER_VERTICAL);
        libraryHeader.setClipChildren(false);
        libraryHeader.setClipToPadding(false);
        libraryHeader.setPadding(headerInset, 0, headerInset, 0);
        page.addView(libraryHeader, margin(matchWidth(), 0, continueItems.isEmpty() ? sdp(12) : 0, 0, sdp(10)));

        TextView libraryTitle = sectionTitle("所有影视");
        libraryTitle.setPadding(0, 0, 0, 0);
        libraryHeader.addView(libraryTitle, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        Button search = compactButton("搜索");
        libraryHeader.addView(search, margin(new LinearLayout.LayoutParams(sdp(78), sdp(42)), sdp(18), 0, 0, 0));
        search.setOnClickListener(view -> {
            isSearchOpen = !isSearchOpen;
            renderHomeContentKeepingPosition();
        });

        Button sort = compactButton("排序");
        libraryHeader.addView(sort, margin(new LinearLayout.LayoutParams(sdp(78), sdp(42)), sdp(9), 0, 0, 0));
        sort.setOnClickListener(view -> {
            isSortOpen = !isSortOpen;
            renderHomeContentKeepingPosition();
        });

        Button direction = compactButton(sortDescending ? "降序" : "升序");
        libraryHeader.addView(direction, margin(new LinearLayout.LayoutParams(sdp(78), sdp(42)), sdp(9), 0, 0, 0));
        direction.setOnClickListener(view -> {
            sortDescending = !sortDescending;
            renderHomeContentKeepingPosition();
        });

        EditText searchInput = null;
        if (isSearchOpen) {
            LinearLayout searchRow = new LinearLayout(this);
            searchRow.setGravity(Gravity.CENTER_VERTICAL);
            searchRow.setClipChildren(false);
            searchRow.setClipToPadding(false);
            page.addView(searchRow, margin(matchWidth(), 0, 0, 0, sdp(16)));
            searchInput = input("搜索", searchText);
            searchInput.setSelection(searchInput.getText().length());
            searchInput.addTextChangedListener(new TextWatcher() {
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
                    pendingSearchRender = () -> {
                        pendingSearchRender = null;
                        if (screen == Screen.HOME && isSearchOpen) {
                            renderHomeContentKeepingPosition();
                        }
                    };
                    main.postDelayed(pendingSearchRender, 220);
                }
            });
            searchRow.addView(searchInput, new LinearLayout.LayoutParams(0, sdp(58), 1f));

            EditText finalSearchInput = searchInput;
            Button apply = compactButton("确定");
            searchRow.addView(apply, margin(new LinearLayout.LayoutParams(sdp(106), sdp(50)), sdp(14), sdp(4), 0, sdp(4)));
            apply.setOnClickListener(view -> {
                searchText = finalSearchInput.getText().toString();
                renderHomeContentKeepingPosition();
            });
        }

        if (isSortOpen) {
            LinearLayout sortRow = new LinearLayout(this);
            sortRow.setGravity(Gravity.CENTER_VERTICAL);
            sortRow.setClipChildren(false);
            sortRow.setClipToPadding(false);
            page.addView(sortRow, margin(matchWidth(), 0, 0, 0, sdp(18)));
            addSortButton(sortRow, "名称", SortKey.TITLE);
            addSortButton(sortRow, "评分", SortKey.RATING);
            addSortButton(sortRow, "上映年份", SortKey.YEAR);
        }

        List<Models.LibraryItem> displayedItems = displayedItems();
        GridLayout grid = posterGrid();
        page.addView(grid);
        for (Models.LibraryItem item : displayedItems) {
            View card = posterCard(item, false);
            if (firstFocusable == null) {
                firstFocusable = card;
            }
            grid.addView(card);
        }

        if (displayedItems.isEmpty()) {
            page.addView(emptyText(libraryItems.isEmpty() ? "媒体库为空" : "没有匹配结果"));
        }

        if (restoreHomeScroll) {
            int targetScrollY = homeScrollY;
            restoreHomeScroll = false;
            main.post(() -> scroll.scrollTo(0, targetScrollY));
        } else {
            if (searchInput != null && isSearchOpen) {
                searchInput.requestFocus();
            } else if (firstFocusable != null) {
                firstFocusable.requestFocus();
            } else {
                search.requestFocus();
            }
            main.post(() -> scroll.scrollTo(0, 0));
        }

        if (isSettingsOpen) {
            renderSettingsOverlay();
        }
    }

    private void addSortButton(LinearLayout row, String label, SortKey key) {
        Button option = button(label, sortKey == key);
        row.addView(option, margin(new LinearLayout.LayoutParams(sdp(146), sdp(48)), 0, 0, sdp(12), 0));
        option.setOnClickListener(view -> {
            sortKey = key;
            isSortOpen = false;
            renderHomeContentKeepingPosition();
        });
    }

    private void renderHomeContentKeepingPosition() {
        if (homeScrollView != null) {
            homeScrollY = homeScrollView.getScrollY();
            restoreHomeScroll = true;
        }
        renderHomeContent();
    }

    private void openSettingsOverlay() {
        if (isSettingsOpen) {
            return;
        }
        isSettingsOpen = true;
        renderSettingsOverlay();
    }

    private void closeSettingsOverlay() {
        isSettingsOpen = false;
        if (settingsPanel != null) {
            root.removeView(settingsPanel);
            settingsPanel = null;
        }
        settingsInitialFocus = null;
        if (settingsDim != null) {
            root.removeView(settingsDim);
            settingsDim = null;
        }
    }

    private void renderSettingsOverlay() {
        if (settingsPanel != null || settingsDim != null) {
            return;
        }
        FrameLayout dim = new FrameLayout(this);
        dim.setBackgroundColor(Color.argb(76, 15, 23, 42));
        dim.setFocusable(false);
        root.addView(dim, match());
        dim.setOnClickListener(view -> {
            closeSettingsOverlay();
        });
        settingsDim = dim;

        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(dp(18), dp(18), dp(18), dp(18));
        panel.setClipChildren(false);
        panel.setClipToPadding(false);
        panel.setBackground(rounded(COLOR_SURFACE, COLOR_BORDER, dp(2), dp(12)));
        panel.setElevation(dp(18));
        panel.setFocusable(true);
        FrameLayout.LayoutParams panelParams = new FrameLayout.LayoutParams(dp(340), ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP | Gravity.RIGHT);
        panelParams.setMargins(0, dp(84), dp(44), 0);
        root.addView(panel, panelParams);
        settingsPanel = panel;

        TextView title = text("设置", 24, Typeface.BOLD, COLOR_TEXT);
        panel.addView(title, margin(matchWidth(), 0, 0, 0, dp(18)));

        TextView version = text("版本 1.6", 14, Typeface.BOLD, COLOR_SUBTLE);
        panel.addView(version, margin(matchWidth(), 0, 0, 0, dp(14)));

        panel.addView(text("NAS 服务器", 14, Typeface.BOLD, COLOR_MUTED));
        TextView server = text(api.serverUrl(), 13, Typeface.NORMAL, COLOR_SUBTLE);
        server.setSingleLine(true);
        server.setEllipsize(TextUtils.TruncateAt.MIDDLE);
        panel.addView(server, margin(matchWidth(), 0, dp(6), 0, dp(16)));

        panel.addView(text("Lucky STUN 穿透地址", 14, Typeface.BOLD, COLOR_MUTED), margin(matchWidth(), 0, 0, 0, dp(8)));
        panel.addView(text("管理地址", 12, Typeface.BOLD, COLOR_MUTED), margin(matchWidth(), 0, 0, 0, dp(3)));
        EditText luckyUrl = input("例如 http://192.168.1.1:16601", api.luckyStunManagementUrl());
        panel.addView(luckyUrl, margin(matchWidth(), 0, dp(8), 0, 0));
        panel.addView(text("账号", 12, Typeface.BOLD, COLOR_MUTED), margin(matchWidth(), 0, 0, 0, dp(3)));
        EditText luckyUsername = input("Lucky 管理账号", api.luckyStunUsername());
        panel.addView(luckyUsername, margin(matchWidth(), 0, dp(8), 0, 0));
        panel.addView(text("密码", 12, Typeface.BOLD, COLOR_MUTED), margin(matchWidth(), 0, 0, 0, dp(3)));
        EditText luckyPassword = input("Lucky 管理密码", api.luckyStunPassword());
        luckyPassword.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        panel.addView(luckyPassword, margin(matchWidth(), 0, dp(8), 0, 0));
        ArrayList<String> luckyRuleIds = new ArrayList<>();
        ArrayList<String> luckyRuleNames = new ArrayList<>();
        ArrayList<String> luckyRuleLabels = new ArrayList<>();
        Spinner luckyRuleSpinner = new Spinner(this);
        ArrayAdapter<String> luckyRuleAdapter = new ArrayAdapter<>(this, android.R.layout.simple_spinner_item, luckyRuleLabels);
        luckyRuleAdapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        luckyRuleSpinner.setAdapter(luckyRuleAdapter);
        panel.addView(luckyRuleSpinner, margin(matchWidth(), 0, dp(8), 0, 0));
        TextView luckyRuleList = text("登录后将在此显示规则列表。", 11, Typeface.NORMAL, COLOR_SUBTLE);
        luckyRuleList.setMaxLines(6);
        luckyRuleList.setEllipsize(TextUtils.TruncateAt.END);
        panel.addView(luckyRuleList, margin(matchWidth(), 0, dp(8), 0, 0));
        CheckBox luckyAutoUpdate = new CheckBox(this);
        luckyAutoUpdate.setText("定期自动更新 Lucky STUN 地址");
        luckyAutoUpdate.setTextColor(COLOR_TEXT);
        luckyAutoUpdate.setChecked(api.luckyStunAutoUpdate());
        panel.addView(luckyAutoUpdate, margin(matchWidth(), 0, dp(4), 0, 0));
        EditText luckyInterval = input("更新周期（分钟，至少 5）", String.valueOf(api.luckyStunUpdateIntervalMinutes()));
        luckyInterval.setInputType(InputType.TYPE_CLASS_NUMBER);
        panel.addView(luckyInterval, margin(matchWidth(), 0, dp(8), 0, 0));
        Button luckyUpdate = compactButton("登录检测并更新");
        panel.addView(luckyUpdate, margin(matchWidth(), 0, dp(4), 0, 0));
        TextView luckyStatus = text("选择规则后点击按钮同步 Docker 地址。", 12, Typeface.NORMAL, COLOR_SUBTLE);
        luckyStatus.setMaxLines(3);
        luckyStatus.setEllipsize(TextUtils.TruncateAt.END);
        panel.addView(luckyStatus, margin(matchWidth(), 0, dp(14), 0, 0));
        luckyUpdate.setOnClickListener(view -> {
            int selectedIndex = luckyRuleSpinner.getSelectedItemPosition();
            String selectedId = selectedIndex >= 0 && selectedIndex < luckyRuleIds.size() ? luckyRuleIds.get(selectedIndex) : api.luckyStunRuleId();
            String selectedName = selectedIndex >= 0 && selectedIndex < luckyRuleNames.size() ? luckyRuleNames.get(selectedIndex) : "";
            int interval;
            try {
                interval = Math.max(5, Math.min(1440, Integer.parseInt(luckyInterval.getText().toString().trim())));
            } catch (NumberFormatException error) {
                interval = 30;
            }
            luckyUpdate.setEnabled(false);
            luckyStatus.setText("正在登录并读取 Lucky STUN 规则...");
            api.saveLuckyStunSettings(luckyUrl.getText().toString(), luckyUsername.getText().toString(), luckyPassword.getText().toString(), selectedId, selectedName, luckyAutoUpdate.isChecked(), interval);
            runAsync(
                    () -> api.refreshLuckyStun(luckyUrl.getText().toString(), luckyUsername.getText().toString(), luckyPassword.getText().toString(), selectedId, selectedName),
                    result -> {
                        luckyRuleIds.clear();
                        luckyRuleNames.clear();
                        luckyRuleLabels.clear();
                        for (LuckyStunClient.Rule rule : result.rules) {
                            luckyRuleIds.add(rule.id);
                            luckyRuleNames.add(rule.name);
                            luckyRuleLabels.add(rule.name + " · " + rule.address);
                        }
                        luckyRuleAdapter.notifyDataSetChanged();
                        StringBuilder ruleSummary = new StringBuilder("已登录规则：\n");
                        for (LuckyStunClient.Rule rule : result.rules) {
                            ruleSummary.append("• ").append(rule.name).append("：").append(rule.address).append("\n");
                        }
                        luckyRuleList.setText(ruleSummary.toString().trim());
                        if (result.selectedRule != null) {
                            int index = luckyRuleIds.indexOf(result.selectedRule.id);
                            if (index >= 0) {
                                luckyRuleSpinner.setSelection(index);
                            }
                        }
                        server.setText(api.serverUrl());
                        luckyStatus.setText(result.message + "\n当前服务端：" + api.serverUrl());
                        luckyUpdate.setEnabled(true);
                    },
                    error -> {
                        luckyStatus.setText("Lucky STUN 更新失败：" + error.getMessage());
                        luckyUpdate.setEnabled(true);
                    });
        });

        panel.addView(text("4K DV/HDR播放模式", 14, Typeface.BOLD, COLOR_MUTED), margin(matchWidth(), 0, 0, 0, dp(8)));
        LinearLayout playbackModeRow = new LinearLayout(this);
        playbackModeRow.setOrientation(LinearLayout.VERTICAL);
        playbackModeRow.setClipChildren(false);
        playbackModeRow.setClipToPadding(false);
        panel.addView(playbackModeRow, margin(matchWidth(), 0, 0, 0, dp(8)));
        Button compatibleSubtitle = settingsOptionButton("兼容字幕播放", !highPerformanceDirectPlayback);
        compatibleSubtitle.setId(View.generateViewId());
        playbackModeRow.addView(compatibleSubtitle, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(46)));
        compatibleSubtitle.setOnClickListener(view -> update4kDvHdrPlaybackMode(false));
        Button highPerformanceDirect = settingsOptionButton("高性能直出", highPerformanceDirectPlayback);
        highPerformanceDirect.setId(View.generateViewId());
        playbackModeRow.addView(highPerformanceDirect, margin(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(46)), 0, dp(8), 0, 0));
        highPerformanceDirect.setOnClickListener(view -> update4kDvHdrPlaybackMode(true));
        TextView playbackModeHint = text("兼容字幕播放模式字幕兼容性更好，但是资源要求更高，如果卡顿请切换至高性能直出模式。高性能直出模式资源要求低，但是可能无法直接烧录字幕，需要docker端开启字幕预缓存将字幕格式提前转为WebVTT格式并保存。", 12, Typeface.NORMAL, COLOR_SUBTLE);
        playbackModeHint.setLineSpacing(dp(2), 1f);
        panel.addView(playbackModeHint, margin(matchWidth(), 0, 0, 0, dp(18)));

        Button update = compactButton("软件更新");
        update.setId(View.generateViewId());
        panel.addView(update, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(44)));
        TextView updateStatus = text("", 13, Typeface.BOLD, COLOR_SUBTLE);
        panel.addView(updateStatus, margin(matchWidth(), 0, dp(10), 0, 0));
        update.setOnClickListener(view -> startUpdateInstall(update, updateStatus));

        Button disconnect = compactButton("退出连接");
        disconnect.setId(View.generateViewId());
        panel.addView(disconnect, margin(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(44)), 0, dp(12), 0, 0));
        disconnect.setTextColor(COLOR_DANGER);
        disconnect.setOnClickListener(view -> disconnectFromServer());

        trapSettingsFocus(compatibleSubtitle, highPerformanceDirect, luckyUpdate, update, disconnect);
        panel.bringToFront();
        settingsInitialFocus = compatibleSubtitle;
        compatibleSubtitle.requestFocus();
    }

    private void update4kDvHdrPlaybackMode(boolean highPerformanceDirect) {
        highPerformanceDirectPlayback = highPerformanceDirect;
        uiPreferences.edit()
                .putString(
                        KEY_4K_DV_HDR_PLAYBACK_MODE,
                        highPerformanceDirect ? PLAYBACK_MODE_HIGH_PERFORMANCE_DIRECT : PLAYBACK_MODE_COMPATIBLE_SUBTITLE)
                .apply();
        closeSettingsOverlay();
        renderHomeContentKeepingPosition();
        openSettingsOverlay();
    }

    private void trapSettingsFocus(Button compatibleSubtitle, Button highPerformanceDirect, Button luckyUpdate, Button update, Button disconnect) {
        compatibleSubtitle.setNextFocusLeftId(compatibleSubtitle.getId());
        compatibleSubtitle.setNextFocusRightId(compatibleSubtitle.getId());
        compatibleSubtitle.setNextFocusUpId(compatibleSubtitle.getId());
        compatibleSubtitle.setNextFocusDownId(highPerformanceDirect.getId());

        highPerformanceDirect.setNextFocusLeftId(highPerformanceDirect.getId());
        highPerformanceDirect.setNextFocusRightId(highPerformanceDirect.getId());
        highPerformanceDirect.setNextFocusUpId(compatibleSubtitle.getId());
        highPerformanceDirect.setNextFocusDownId(luckyUpdate.getId());

        luckyUpdate.setNextFocusLeftId(luckyUpdate.getId());
        luckyUpdate.setNextFocusRightId(luckyUpdate.getId());
        luckyUpdate.setNextFocusUpId(highPerformanceDirect.getId());
        luckyUpdate.setNextFocusDownId(update.getId());

        update.setNextFocusLeftId(update.getId());
        update.setNextFocusRightId(update.getId());
        update.setNextFocusUpId(luckyUpdate.getId());
        update.setNextFocusDownId(disconnect.getId());

        disconnect.setNextFocusLeftId(disconnect.getId());
        disconnect.setNextFocusRightId(disconnect.getId());
        disconnect.setNextFocusUpId(update.getId());
        disconnect.setNextFocusDownId(disconnect.getId());
    }

    private void disconnectFromServer() {
        closeSettingsOverlay();
        showLoading("正在退出连接");
        runAsync(
                () -> {
                    api.disconnect();
                    return null;
                },
                ignored -> showSetup(null),
                error -> showSetup(null));
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

    private View posterCard(Models.LibraryItem item, boolean showProgress) {
        int cardWidth = homePosterCardWidthPx();
        int cardInnerWidth = Math.max(dp(1), cardWidth - sdp(16));
        int posterHeight = Math.round(cardInnerWidth * 1.5f);
        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setFocusable(true);
        card.setClickable(true);
        card.setPadding(sdp(8), sdp(8), sdp(8), sdp(10));
        card.setBackground(rounded(Color.TRANSPARENT, Color.TRANSPARENT, 0, sdp(8)));
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
        applyPosterCardFocus(card, posterFrame, () -> scheduleDetailPrefetch(item.id, card));

        ImageView poster = new ImageView(this);
        poster.setScaleType(ImageView.ScaleType.CENTER_CROP);
        poster.setClipToOutline(true);
        posterFrame.addView(poster, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        loadPoster(poster, item.posterAssetId, cardInnerWidth, posterHeight);

        LinearLayout badges = new LinearLayout(this);
        badges.setGravity(Gravity.CENTER_VERTICAL);
        badges.setOrientation(LinearLayout.HORIZONTAL);
        FrameLayout.LayoutParams badgeParams = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.LEFT | Gravity.BOTTOM);
        badgeParams.setMargins(sdp(10), 0, sdp(10), sdp(10));
        posterFrame.addView(badges, badgeParams);

        String rating = ratingText(item.voteAverage);
        if (rating != null) {
            badges.addView(badge(rating, Color.rgb(217, 119, 6)));
        }
        String doubanRating = ratingText(item.doubanRating);
        if (doubanRating != null) {
            badges.addView(badge(doubanRating, Color.rgb(0, 166, 41)), margin(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, sdp(30)), sdp(5), 0, 0, 0));
        }

        if (showProgress) {
            int progress = item.progressPercent();
            if (progress > 0 && progress < 95) {
                card.addView(progressBar(progress, cardInnerWidth, sdp(5)), margin(new LinearLayout.LayoutParams(cardInnerWidth, sdp(5)), 0, sdp(9), 0, 0));
            }
        }

        TextView name = text(item.title, ssp(16), Typeface.BOLD, COLOR_TEXT);
        name.setMaxLines(2);
        name.setEllipsize(TextUtils.TruncateAt.END);
        card.addView(name, margin(matchWidth(), 0, sdp(11), 0, 0));

        LinearLayout yearRow = new LinearLayout(this);
        yearRow.setGravity(Gravity.CENTER_VERTICAL);
        card.addView(yearRow, margin(matchWidth(), 0, sdp(2), 0, 0));
        String year = releaseYearText(item.releaseDate);
        yearRow.addView(text(year == null ? "" : year, ssp(13), Typeface.NORMAL, COLOR_SUBTLE), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        TextView watched = text(item.isWatched ? "✓" : "○", ssp(17), Typeface.BOLD, item.isWatched ? COLOR_FOCUS : COLOR_SUBTLE);
        watched.setGravity(Gravity.CENTER);
        watched.setPadding(0, 0, 0, 0);
        watched.setMinHeight(sdp(28));
        watched.setFocusable(true);
        watched.setClickable(true);
        watched.setBackgroundColor(Color.TRANSPARENT);
        watched.setOnClickListener(view -> toggleLibraryItemWatched(item));
        applyWatchedIconFocus(watched, item.isWatched);
        yearRow.addView(watched, new LinearLayout.LayoutParams(sdp(30), sdp(28)));

        card.setOnClickListener(view -> showDetail(item.id));
        return card;
    }

    private void showDetail(String itemId) {
        selectedSeasonId = "";
        loadDetail(itemId);
    }

    private void loadDetail(String itemId) {
        screen = Screen.DETAIL;
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
                    detailCache.put(itemId, detail);
                },
                error -> pendingDetailPrefetches.remove(itemId));
    }

    private void renderDetail(Models.LibraryDetail detail) {
        screen = Screen.DETAIL;
        currentDetail = detail;
        root.removeAllViews();
        applyAppBackground();

        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(sdp(52), sdp(34), sdp(52), sdp(42));
        page.setClipChildren(false);
        page.setClipToPadding(false);
        root.addView(page, match());

        ScrollView scroll = new ScrollView(this);
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
        info.addView(text(detail.title, ssp(34), Typeface.BOLD, COLOR_TEXT));

        LinearLayout facts = new LinearLayout(this);
        facts.setGravity(Gravity.CENTER_VERTICAL);
        info.addView(facts, margin(matchWidth(), 0, sdp(10), 0, sdp(18)));
        String year = releaseYearText(detail.releaseDate);
        if (year != null) {
            facts.addView(metaPill(year));
        }
        String rating = ratingText(detail.voteAverage);
        if (rating != null) {
            facts.addView(metaPill("★ " + rating, Color.rgb(217, 119, 6)), margin(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, sdp(34)), sdp(9), 0, 0, 0));
        }
        String doubanRating = ratingText(detail.doubanRating);
        if (doubanRating != null) {
            facts.addView(metaPill("★ " + doubanRating, Color.rgb(0, 166, 41)), margin(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, sdp(34)), sdp(9), 0, 0, 0));
        }
        for (String part : doubanMetadataParts(detail.douban)) {
            facts.addView(metaPill(part), margin(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, sdp(34)), sdp(9), 0, 0, 0));
        }

        String overviewText = detailOverviewText(detail);
        if (!overviewText.isEmpty()) {
            TextView overview = text(overviewText, ssp(17), Typeface.NORMAL, COLOR_TEXT);
            overview.setLineSpacing(sdp(3), 1f);
            overview.setMaxLines(7);
            overview.setEllipsize(TextUtils.TruncateAt.END);
            info.addView(overview, margin(matchWidth(), 0, 0, 0, sdp(26)));
        }

        Models.VideoFile mainFile = resolveMainFile(detail);
        Button playButton = null;
        if (mainFile != null) {
            LinearLayout actions = new LinearLayout(this);
            actions.setGravity(Gravity.CENTER_VERTICAL);
            actions.setClipChildren(false);
            actions.setClipToPadding(false);
            info.addView(actions, margin(matchWidth(), 0, 0, 0, sdp(10)));

            LinearLayout playColumn = new LinearLayout(this);
            playColumn.setOrientation(LinearLayout.VERTICAL);
            playColumn.setGravity(Gravity.CENTER_HORIZONTAL);
            playColumn.setClipChildren(false);
            playColumn.setClipToPadding(false);
            actions.addView(playColumn, new LinearLayout.LayoutParams(sdp(236), ViewGroup.LayoutParams.WRAP_CONTENT));

            Button play = button(playButtonText(mainFile));
            playButton = play;
            play.setTag(mainFile);
            playColumn.addView(play, new LinearLayout.LayoutParams(sdp(236), sdp(58)));
            play.setOnClickListener(view -> {
                Object file = view.getTag();
                if (file instanceof Models.VideoFile) {
                    playFile((Models.VideoFile) file);
                }
            });
            play.requestFocus();

            Button watchStatus = button(watchedStatusLabel(mainFile), isEffectivelyWatched(mainFile));
            actions.addView(watchStatus, margin(new LinearLayout.LayoutParams(sdp(116), sdp(48)), sdp(16), 0, 0, 0));
            watchStatus.setOnClickListener(view -> toggleVideoWatched(mainFile));

            LinearLayout timelineColumn = new LinearLayout(this);
            timelineColumn.setOrientation(LinearLayout.VERTICAL);
            timelineColumn.setClipChildren(false);
            timelineColumn.setClipToPadding(false);
            actions.addView(timelineColumn, margin(new LinearLayout.LayoutParams(sdp(438), ViewGroup.LayoutParams.WRAP_CONTENT), sdp(18), 0, 0, 0));

            LinearLayout timeline = timeline(detail, mainFile);
            timelineColumn.addView(timeline, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, sdp(48)));

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

        TextView heading = text("版本 / 文件（" + files.size() + "）", ssp(20), Typeface.BOLD, COLOR_TEXT);
        content.addView(heading, margin(matchWidth(), 0, sdp(14), 0, sdp(18)));
        for (int index = 0; index < files.size(); index++) {
            Models.VideoFile file = files.get(index);
            Button version = button((files.size() > 1 ? "版本 " + (index + 1) + " · " : "正片 · ") + file.fileName);
            version.setAllCaps(false);
            version.setGravity(Gravity.LEFT | Gravity.CENTER_VERTICAL);
            version.setTag(file);
            version.setOnClickListener(view -> {
                Object tag = view.getTag();
                if (tag instanceof Models.VideoFile) {
                    playFile((Models.VideoFile) tag);
                }
            });
            content.addView(version, margin(matchWidth(), 0, sdp(8), 0, 0));

            TextView metadata = text((file.relativePath == null ? "" : file.relativePath) +
                    (file.mediaSummary().isEmpty() ? "" : " · " + file.mediaSummary()) +
                    " · " + fileProgressPercent(file) + "% 已播放", ssp(13), Typeface.NORMAL, COLOR_SUBTLE);
            metadata.setMaxLines(2);
            metadata.setEllipsize(TextUtils.TruncateAt.END);
            content.addView(metadata, margin(matchWidth(), 0, sdp(16), 0, sdp(2)));
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
            seasonHeader.addView(seasonButton, margin(new LinearLayout.LayoutParams(sdp(132), sdp(48)), 0, 0, sdp(12), 0));
            seasonButton.setOnClickListener(view -> {
                selectedSeasonId = season.id;
                renderDetail(detail);
            });
        }

        GridLayout episodeGrid = new GridLayout(this);
        episodeGrid.setColumnCount(detailEpisodeColumnCount());
        episodeGrid.setClipChildren(false);
        episodeGrid.setClipToPadding(false);
        content.addView(episodeGrid);

        for (Models.Episode episode : selectedSeason.episodes) {
            episodeGrid.addView(episodeCard(episode, detail.posterAssetId, playButton));
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

        String facts = episodeFacts(episode);
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
        runAsync(
                () -> resolvePlayback(file),
                playback -> startResolvedPlayback(file, playback, status),
                error -> renderPlayerError(error.getMessage()));
        if (playerPlayPauseButton != null) {
            playerPlayPauseButton.requestFocus();
        }
    }

    private ResolvedPlayback resolvePlayback(Models.VideoFile file) throws Exception {
        String videoFileId = file.id;
        Models.AppSettings settings;
        try {
            settings = api.getSettings();
        } catch (Exception ignored) {
            settings = Models.AppSettings.defaults();
        }

        Models.PlaybackFileStreams streams = resolvePlaybackStreams(videoFileId);

        try {
            String ticket = api.createPlaybackTicket(videoFileId);
            if (ticket != null && !ticket.isEmpty()) {
                String streamUrl = api.streamUrl(videoFileId, ticket);
                String playbackUrl = isIsoFile(file) ? resolveIsoPlaybackUrl(streamUrl, "") : streamUrl;
                return new ResolvedPlayback(playbackUrl, ticket, "", settings, streams, "", "", isIsoFile(file) && playbackUrl.equals(streamUrl));
            }
        } catch (IOException error) {
            if (error.getMessage() == null || !error.getMessage().contains("404")) {
                throw error;
            }
        }

        String streamUrl = api.streamUrl(videoFileId);
        String cookieHeader = api.cookieHeader();
        String playbackUrl = isIsoFile(file) ? resolveIsoPlaybackUrl(streamUrl, cookieHeader) : streamUrl;
        String playbackCookieHeader = playbackUrl.equals(streamUrl) ? cookieHeader : "";
        return new ResolvedPlayback(playbackUrl, "", playbackCookieHeader, settings, streams, "", "", isIsoFile(file) && playbackUrl.equals(streamUrl));
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

        if (playback.url == null || playback.url.isEmpty()) {
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
        panel.setBackground(rounded(Color.argb(176, 9, 13, 21), Color.argb(82, 255, 255, 255), dp(1), sdp(18)));

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
        root.removeAllViews();
        applyAppBackground();
        TextView view = text(message, 24, Typeface.BOLD, COLOR_TEXT);
        view.setGravity(Gravity.CENTER);
        root.addView(view, match());
    }

    private void renderError(String message, Runnable backAction) {
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
        if (screen == Screen.PLAYER && playerView != null) {
            if (handlePlayerKeyEvent(event)) {
                return true;
            }
        }
        if (screen == Screen.HOME && !isSettingsOpen && handleHomeDpadKeyEvent(event)) {
            return true;
        }
        return super.dispatchKeyEvent(event);
    }

    @Override
    public boolean onKeyDown(int keyCode, KeyEvent event) {
        if (keyCode == KeyEvent.KEYCODE_BACK || keyCode == KeyEvent.KEYCODE_ESCAPE) {
            if (isSettingsOpen) {
                closeSettingsOverlay();
                return true;
            }
            if (screen == Screen.HOME && (isSearchOpen || isSortOpen)) {
                isSearchOpen = false;
                isSortOpen = false;
                renderHomeContentKeepingPosition();
                return true;
            }
            if (screen == Screen.PLAYER) {
                if (playerMenuPanel != null) {
                    closePlayerMenu(playerPlayPauseButton);
                    return true;
                }
                closePlayer();
                return true;
            }
            if (screen == Screen.DETAIL) {
                showHome();
                return true;
            }
        }

        if (isSettingsOpen && isDpadNavigationKey(keyCode) && settingsPanel != null && !isDescendant(settingsPanel, getCurrentFocus())) {
            if (settingsInitialFocus != null) {
                settingsInitialFocus.requestFocus();
            } else {
                settingsPanel.requestFocus();
            }
            return true;
        }

        return super.onKeyDown(keyCode, event);
    }

    @Override
    public void onBackPressed() {
        if (isSettingsOpen) {
            closeSettingsOverlay();
            return;
        }
        if (screen == Screen.HOME && (isSearchOpen || isSortOpen)) {
            isSearchOpen = false;
            isSortOpen = false;
            renderHomeContentKeepingPosition();
            return;
        }
        if (screen == Screen.PLAYER) {
            if (playerMenuPanel != null) {
                closePlayerMenu(playerPlayPauseButton);
                return;
            }
            closePlayer();
            return;
        }
        if (screen == Screen.DETAIL) {
            showHome();
            return;
        }
        super.onBackPressed();
    }

    private boolean isDpadNavigationKey(int keyCode) {
        return keyCode == KeyEvent.KEYCODE_DPAD_UP ||
                keyCode == KeyEvent.KEYCODE_DPAD_DOWN ||
                keyCode == KeyEvent.KEYCODE_DPAD_LEFT ||
                keyCode == KeyEvent.KEYCODE_DPAD_RIGHT ||
                keyCode == KeyEvent.KEYCODE_DPAD_CENTER ||
                keyCode == KeyEvent.KEYCODE_ENTER;
    }

    private boolean isDpadArrowKey(int keyCode) {
        return keyCode == KeyEvent.KEYCODE_DPAD_UP ||
                keyCode == KeyEvent.KEYCODE_DPAD_DOWN ||
                keyCode == KeyEvent.KEYCODE_DPAD_LEFT ||
                keyCode == KeyEvent.KEYCODE_DPAD_RIGHT;
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

    private boolean handleHomeDpadKeyEvent(KeyEvent event) {
        if (event == null || event.getAction() != KeyEvent.ACTION_DOWN || !isDpadArrowKey(event.getKeyCode())) {
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
        return true;
    }

    private void requestFirstHomeFocus() {
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

    private List<Models.LibraryItem> displayedItems() {
        String normalizedSearch = searchText == null ? "" : searchText.trim().toLowerCase(Locale.ROOT);
        ArrayList<Models.LibraryItem> result = new ArrayList<>();
        for (Models.LibraryItem item : libraryItems) {
            if (normalizedSearch.isEmpty() || item.title.toLowerCase(Locale.ROOT).contains(normalizedSearch)) {
                result.add(item);
            }
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

    private String watchedStatusLabel(Models.VideoFile file) {
        if (file == null) {
            return "未播";
        }
        if (isEffectivelyWatched(file)) {
            return "已播";
        }
        return file.positionSeconds > 5 ? "未播完" : "未播";
    }

    private String playButtonText(Models.VideoFile file) {
        String action = isEffectivelyWatched(file) ? "重新播放" : (file.positionSeconds > 5 ? "继续播放" : "开始播放");
        if (file.seasonNumber != null && file.episodeNumber != null) {
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

    private String episodeFacts(Models.Episode episode) {
        ArrayList<String> facts = new ArrayList<>();
        facts.add(episodeDisplayLabel(episode.seasonNumber, episode.episodeNumber));
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
                    subtitleCacheStatusCache.put(file.id, status);
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
        FrameLayout track = new FrameLayout(this);
        track.setBackground(rounded(Color.rgb(229, 231, 235), Color.TRANSPARENT, 0, height / 2));
        View fill = new View(this);
        fill.setBackground(rounded(COLOR_FOCUS, Color.TRANSPARENT, 0, height / 2));
        track.addView(fill, new FrameLayout.LayoutParams(percent <= 0 ? 0 : Math.max(dp(2), Math.round(width * percent / 100f)), ViewGroup.LayoutParams.MATCH_PARENT));
        return track;
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
        editText.setTextSize(18);
        editText.setTextColor(COLOR_TEXT);
        editText.setHintTextColor(COLOR_SUBTLE);
        editText.setPadding(dp(18), 0, dp(18), 0);
        editText.setSelectAllOnFocus(false);
        editText.setBackground(rounded(COLOR_SURFACE, COLOR_BORDER, dp(2), dp(8)));
        editText.setMinHeight(dp(58));
        editText.setFocusable(true);
        editText.setFocusableInTouchMode(true);
        applyInputFocus(editText);
        return editText;
    }

    private Button compactButton(String label) {
        Button button = button(label);
        button.setTextSize(ssp(15));
        return button;
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
        button.setOnFocusChangeListener((focusedView, hasFocus) -> {
            animateFocusScale(focusedView, hasFocus);
            button.setTextColor(hasFocus ? COLOR_FOCUS : Color.WHITE);
            focusedView.setBackground(rounded(Color.argb(hasFocus ? 224 : 70, 255, 255, 255), Color.argb(hasFocus ? 224 : 86, 255, 255, 255), FOCUS_STROKE_PX, sdp(24)));
            focusedView.setElevation(0);
        });
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
        button.setOnFocusChangeListener((focusedView, hasFocus) -> {
            animateFocusScale(focusedView, hasFocus);
            button.setColorFilter(hasFocus ? COLOR_FOCUS : Color.WHITE);
            focusedView.setBackground(rounded(hasFocus ? Color.argb(222, 255, 255, 255) : Color.argb(26, 255, 255, 255), Color.argb(hasFocus ? 232 : 136, 255, 255, 255), FOCUS_STROKE_PX, sdp(24)));
            focusedView.setElevation(0);
        });
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
        button.setOnFocusChangeListener((focusedView, hasFocus) -> {
            animateFocusScale(focusedView, hasFocus);
            button.setTextColor(hasFocus ? COLOR_FOCUS : (selected ? COLOR_ACCENT : Color.WHITE));
            focusedView.setBackground(rounded(hasFocus ? Color.argb(226, 255, 255, 255) : Color.argb(selected ? 44 : 18, 255, 255, 255), Color.argb(hasFocus ? 226 : 112, 255, 255, 255), FOCUS_STROKE_PX, sdp(10)));
            focusedView.setElevation(0);
        });
        return button;
    }

    private Button button(String label) {
        return button(label, false);
    }

    private Button button(String label, boolean active) {
        Button button = new Button(this);
        button.setText(label);
        button.setTextSize(ssp(17));
        button.setAllCaps(false);
        button.setTextColor(active ? COLOR_FOCUS : COLOR_TEXT);
        button.setGravity(Gravity.CENTER);
        button.setPadding(sdp(14), 0, sdp(14), 0);
        button.setMinWidth(0);
        button.setMinHeight(0);
        button.setMinimumWidth(0);
        button.setMinimumHeight(0);
        button.setIncludeFontPadding(false);
        button.setBackgroundTintList(null);
        button.setStateListAnimator(null);
        button.setClipToOutline(true);
        button.setBackground(rounded(active ? COLOR_FOCUS_SOFT : Color.argb(238, 255, 255, 255), active ? COLOR_FOCUS : COLOR_BORDER, FOCUS_STROKE_PX, sdp(9)));
        button.setFocusable(true);
        button.setFocusableInTouchMode(false);
        applyButtonFocus(button, active);
        return button;
    }

    private Button settingsOptionButton(String label, boolean selected) {
        Button button = button((selected ? "✓ " : "  ") + label, false);
        button.setGravity(Gravity.CENTER_VERTICAL);
        button.setTextColor(COLOR_TEXT);
        applySettingsOptionFocus(button);
        return button;
    }

    private TextView title(String value) {
        TextView title = text(value, ssp(32), Typeface.BOLD, COLOR_TEXT);
        title.setLineSpacing(sdp(3), 1f);
        return title;
    }

    private TextView sectionTitle(String value) {
        TextView title = text(value, ssp(24), Typeface.BOLD, COLOR_TEXT);
        title.setPadding(0, sdp(12), 0, sdp(14));
        title.setLineSpacing(sdp(2), 1f);
        return title;
    }

    private TextView body(String value) {
        TextView text = text(value, ssp(16), Typeface.NORMAL, COLOR_MUTED);
        text.setLineSpacing(sdp(3), 1f);
        text.setPadding(0, sdp(8), 0, sdp(18));
        return text;
    }

    private TextView emptyText(String value) {
        TextView text = text(value, ssp(16), Typeface.NORMAL, COLOR_SUBTLE);
        text.setPadding(0, 0, 0, sdp(28));
        return text;
    }

    private TextView metaPill(String value) {
        return metaPill(value, COLOR_MUTED);
    }

    private TextView metaPill(String value, int textColor) {
        TextView pill = text(value, ssp(14), Typeface.BOLD, textColor);
        pill.setGravity(Gravity.CENTER);
        pill.setPadding(sdp(13), 0, sdp(13), 0);
        pill.setBackground(rounded(Color.argb(184, 255, 255, 255), COLOR_BORDER, dp(1), sdp(999)));
        pill.setMinHeight(sdp(32));
        return pill;
    }

    private TextView badge(String value) {
        return badge(value, COLOR_TEXT);
    }

    private TextView badge(String value, int textColor) {
        TextView badge = text(value, ssp(13), Typeface.BOLD, textColor);
        badge.setGravity(Gravity.CENTER);
        badge.setPadding(sdp(8), 0, sdp(8), 0);
        badge.setMinHeight(sdp(30));
        badge.setBackground(rounded(Color.argb(230, 255, 255, 255), Color.TRANSPARENT, 0, sdp(7)));
        return badge;
    }

    private TextView text(String value, int sp, int style, int color) {
        TextView textView = new TextView(this);
        textView.setText(value == null ? "" : value);
        textView.setTextSize(sp);
        textView.setTypeface(Typeface.DEFAULT, style);
        textView.setTextColor(color);
        textView.setIncludeFontPadding(false);
        textView.setFontFeatureSettings("kern");
        textView.setPaintFlags(textView.getPaintFlags() | Paint.ANTI_ALIAS_FLAG | Paint.SUBPIXEL_TEXT_FLAG | Paint.LINEAR_TEXT_FLAG);
        return textView;
    }

    private void applyButtonFocus(Button button, boolean active) {
        button.setOnFocusChangeListener((focusedView, hasFocus) -> {
            animateFocusScale(focusedView, hasFocus);
            button.setTextColor(hasFocus || active ? COLOR_FOCUS : COLOR_TEXT);
            focusedView.setBackground(rounded(hasFocus || active ? Color.argb(248, 255, 255, 255) : Color.argb(238, 255, 255, 255), COLOR_BORDER, FOCUS_STROKE_PX, sdp(9)));
            focusedView.setElevation(0);
        });
    }

    private void applySettingsOptionFocus(Button button) {
        button.setOnFocusChangeListener((focusedView, hasFocus) -> {
            animateFocusScale(focusedView, hasFocus);
            button.setTextColor(COLOR_TEXT);
            focusedView.setBackground(rounded(hasFocus ? Color.argb(248, 255, 255, 255) : Color.argb(238, 255, 255, 255), COLOR_BORDER, FOCUS_STROKE_PX, sdp(9)));
            focusedView.setElevation(0);
        });
    }

    private void applyInputFocus(EditText editText) {
        editText.setOnFocusChangeListener((focusedView, hasFocus) -> {
            focusedView.setBackground(rounded(COLOR_SURFACE, COLOR_BORDER, dp(2), sdp(8)));
            focusedView.setElevation(0);
        });
    }

    private void applyCardFocus(View view) {
        applyCardFocus(view, null);
    }

    private void applyCardFocus(View view, Runnable onFocused) {
        view.setOnFocusChangeListener((focusedView, hasFocus) -> {
            animateFocusScale(focusedView, hasFocus);
            focusedView.setBackground(rounded(Color.TRANSPARENT, Color.TRANSPARENT, 0, sdp(8)));
            focusedView.setElevation(hasFocus ? sdp(10) : 0);
            if (hasFocus && onFocused != null) {
                onFocused.run();
            }
        });
    }

    private void applyPosterCardFocus(View card, FrameLayout posterFrame, Runnable onFocused) {
        card.setOnFocusChangeListener((focusedView, hasFocus) -> {
            animateFocusScale(focusedView, hasFocus);
            focusedView.setBackground(rounded(Color.TRANSPARENT, Color.TRANSPARENT, 0, sdp(8)));
            focusedView.setElevation(hasFocus ? sdp(12) : 0);
            posterFrame.setBackground(rounded(COLOR_SURFACE_ALT, COLOR_BORDER, dp(1), sdp(12)));
            posterFrame.setElevation(hasFocus ? sdp(8) : 0);
            if (hasFocus && onFocused != null) {
                onFocused.run();
            }
        });
    }

    private void applyEpisodeStillFocus(View card, FrameLayout stillFrame, boolean showDetails, Button playButton, Models.VideoFile playbackFile) {
        card.setOnFocusChangeListener((focusedView, hasFocus) -> {
            focusedView.animate().scaleX(1f).scaleY(1f).setDuration(0).start();
            animateFocusScale(stillFrame, hasFocus);
            focusedView.setBackground(rounded(Color.TRANSPARENT, Color.TRANSPARENT, 0, sdp(8)));
            focusedView.setElevation(hasFocus ? sdp(10) : 0);
            stillFrame.setBackground(rounded(COLOR_SURFACE_ALT, COLOR_BORDER, FOCUS_STROKE_PX, sdp(12)));
            stillFrame.setElevation(hasFocus ? sdp(10) : 0);
            if (hasFocus && playButton != null && playbackFile != null) {
                playButton.setText(playButtonText(playbackFile));
                playButton.setTag(playbackFile);
            }
        });
    }

    private void applyBadgeFocus(TextView badge) {
        badge.setOnFocusChangeListener((focusedView, hasFocus) -> {
            animateFocusScale(focusedView, hasFocus);
            focusedView.setBackground(rounded(Color.argb(235, 255, 255, 255), Color.TRANSPARENT, 0, sdp(7)));
            focusedView.setElevation(hasFocus ? sdp(8) : 0);
        });
    }

    private void applyWatchedIconFocus(TextView icon, boolean watched) {
        icon.setOnFocusChangeListener((focusedView, hasFocus) -> {
            animateFocusScale(focusedView, hasFocus);
            icon.setTextColor(hasFocus || watched ? COLOR_FOCUS : COLOR_SUBTLE);
            focusedView.setBackgroundColor(Color.TRANSPARENT);
            focusedView.setElevation(0);
        });
    }

    private void animateFocusScale(View view, boolean hasFocus) {
        view.animate()
                .scaleX(hasFocus ? FOCUS_SCALE : 1f)
                .scaleY(hasFocus ? FOCUS_SCALE : 1f)
                .setDuration(FOCUS_ANIMATION_MS)
                .start();
    }

    private void applyAppBackground() {
        GradientDrawable background = new GradientDrawable(
                GradientDrawable.Orientation.TL_BR,
                new int[]{
                        Color.rgb(229, 236, 246),
                        Color.rgb(216, 230, 243),
                        blend(COLOR_ACCENT, Color.rgb(210, 224, 232), 0.78f)
                });
        root.setBackground(background);
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

    private int blend(int from, int to, float amount) {
        float clamped = Math.max(0f, Math.min(1f, amount));
        int red = Math.round(Color.red(from) + (Color.red(to) - Color.red(from)) * clamped);
        int green = Math.round(Color.green(from) + (Color.green(to) - Color.green(from)) * clamped);
        int blue = Math.round(Color.blue(from) + (Color.blue(to) - Color.blue(from)) * clamped);
        return Color.rgb(red, green, blue);
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

    private int sdp(float value) {
        return Math.round(dp(value) * tvScale());
    }

    private int ssp(int value) {
        return Math.round(value * textScale());
    }

    private float tvScale() {
        int width = getResources().getDisplayMetrics().widthPixels;
        if (width >= 3600) {
            return 1.18f;
        }
        if (width >= 2500) {
            return 1.10f;
        }
        return 1f;
    }

    private float textScale() {
        int width = getResources().getDisplayMetrics().widthPixels;
        if (width >= 3600) {
            return 1.12f;
        }
        if (width >= 2500) {
            return 1.06f;
        }
        return 1f;
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
            return selected ? "✓ " + label : "  " + label;
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
        void accept(Exception error);
    }
}
