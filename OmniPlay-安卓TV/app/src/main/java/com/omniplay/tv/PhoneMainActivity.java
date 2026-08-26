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
import com.omniplay.tv.data.OmniPlayApi;
import com.omniplay.tv.iso.RemoteIsoStreamServer;
import com.omniplay.tv.player.MpvVideoView;
import com.omniplay.tv.ui.ImageLoader;

import java.io.IOException;
import java.util.ArrayList;
import java.util.List;
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
    private OmniPlayApi api;
    private ImageLoader imageLoader;
    private Models.LibraryDetail currentDetail;
    private MpvVideoView playerView;
    private Models.VideoFile activePlaybackFile;
    private Runnable progressReporter;
    private Screen screen = Screen.SETUP;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        getWindow().setStatusBarColor(COLOR_BACKGROUND);
        getWindow().setNavigationBarColor(COLOR_BACKGROUND);

        api = new OmniPlayApi(this);
        imageLoader = new ImageLoader(this);
        root = new FrameLayout(this);
        root.setBackgroundColor(COLOR_BACKGROUND);
        setContentView(root);

        if (api.hasServerUrl()) {
            checkSession();
        } else {
            showSetup(null);
        }
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
        setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED);
        root.removeAllViews();
        root.setBackgroundColor(COLOR_BACKGROUND);

        ScrollView scroll = new ScrollView(this);
        root.addView(scroll, match());
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(22), dp(34), dp(22), dp(34));
        scroll.addView(page, matchWidth());

        page.addView(title("OmniPlay"));
        page.addView(body("连接docker服务端，安卓端只播放，不扫描刮削。"));

        EditText serverInput = input("Docker 服务端地址，例如 http://192.168.1.10:45722", api.serverUrl());
        page.addView(serverInput, margin(matchWidth(), 0, dp(18), 0, 0));

        EditText usernameInput = input("用户名", api.savedUsername());
        page.addView(usernameInput, margin(matchWidth(), 0, dp(12), 0, 0));

        EditText passwordInput = input("密码", api.savedPassword());
        passwordInput.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        page.addView(passwordInput, margin(matchWidth(), 0, dp(12), 0, 0));

        page.addView(body("Lucky STUN 穿透地址"), margin(matchWidth(), 0, dp(6), 0, dp(2)));
        page.addView(body("管理地址"), margin(matchWidth(), 0, 0, 0, dp(3)));
        EditText luckyUrlInput = input("例如 http://192.168.1.1:16601", api.luckyStunManagementUrl());
        page.addView(luckyUrlInput, margin(matchWidth(), 0, dp(10), 0, 0));
        page.addView(body("账号"), margin(matchWidth(), 0, 0, 0, dp(3)));
        EditText luckyUsernameInput = input("Lucky 管理账号", api.luckyStunUsername());
        page.addView(luckyUsernameInput, margin(matchWidth(), 0, dp(10), 0, 0));
        page.addView(body("密码"), margin(matchWidth(), 0, 0, 0, dp(3)));
        EditText luckyPasswordInput = input("Lucky 管理密码", api.luckyStunPassword());
        luckyPasswordInput.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        page.addView(luckyPasswordInput, margin(matchWidth(), 0, dp(10), 0, 0));
        ArrayList<String> luckyRuleIds = new ArrayList<>();
        ArrayList<String> luckyRuleNames = new ArrayList<>();
        ArrayList<String> luckyRuleLabels = new ArrayList<>();
        Spinner luckyRuleSpinner = new Spinner(this);
        ArrayAdapter<String> luckyRuleAdapter = new ArrayAdapter<>(this, android.R.layout.simple_spinner_item, luckyRuleLabels);
        luckyRuleAdapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        luckyRuleSpinner.setAdapter(luckyRuleAdapter);
        page.addView(luckyRuleSpinner, margin(matchWidth(), 0, dp(8), 0, 0));
        TextView luckyRuleList = body("登录后将在此显示规则列表。");
        luckyRuleList.setMaxLines(8);
        luckyRuleList.setEllipsize(TextUtils.TruncateAt.END);
        page.addView(luckyRuleList, margin(matchWidth(), 0, dp(8), 0, 0));
        CheckBox luckyAutoUpdate = new CheckBox(this);
        luckyAutoUpdate.setText("定期自动更新 Lucky STUN 地址");
        luckyAutoUpdate.setTextColor(COLOR_TEXT);
        luckyAutoUpdate.setChecked(api.luckyStunAutoUpdate());
        page.addView(luckyAutoUpdate, margin(matchWidth(), 0, dp(4), 0, 0));
        EditText luckyIntervalInput = input("更新周期（分钟，至少 5）", String.valueOf(api.luckyStunUpdateIntervalMinutes()));
        luckyIntervalInput.setInputType(InputType.TYPE_CLASS_NUMBER);
        page.addView(luckyIntervalInput, margin(matchWidth(), 0, dp(8), 0, 0));
        Button luckyUpdate = button("登录检测并更新");
        page.addView(luckyUpdate, margin(matchWidth(), 0, dp(4), 0, 0));
        TextView luckyStatus = body("选择规则后点击按钮同步 Docker 地址。");
        page.addView(luckyStatus, margin(matchWidth(), 0, dp(12), 0, 0));
        luckyUpdate.setOnClickListener(view -> {
            int selectedIndex = luckyRuleSpinner.getSelectedItemPosition();
            String selectedId = selectedIndex >= 0 && selectedIndex < luckyRuleIds.size() ? luckyRuleIds.get(selectedIndex) : api.luckyStunRuleId();
            String selectedName = selectedIndex >= 0 && selectedIndex < luckyRuleNames.size() ? luckyRuleNames.get(selectedIndex) : "";
            int interval;
            try {
                interval = Math.max(5, Math.min(1440, Integer.parseInt(luckyIntervalInput.getText().toString().trim())));
            } catch (NumberFormatException error) {
                interval = 30;
            }
            luckyUpdate.setEnabled(false);
            luckyStatus.setText("正在登录并读取 Lucky STUN 规则...");
            api.saveLuckyStunSettings(luckyUrlInput.getText().toString(), luckyUsernameInput.getText().toString(), luckyPasswordInput.getText().toString(), selectedId, selectedName, luckyAutoUpdate.isChecked(), interval);
            runAsync(
                    () -> api.refreshLuckyStun(luckyUrlInput.getText().toString(), luckyUsernameInput.getText().toString(), luckyPasswordInput.getText().toString(), selectedId, selectedName),
                    result -> {
                        luckyRuleIds.clear();
                        luckyRuleNames.clear();
                        luckyRuleLabels.clear();
                        for (com.omniplay.tv.data.LuckyStunClient.Rule rule : result.rules) {
                            luckyRuleIds.add(rule.id);
                            luckyRuleNames.add(rule.name);
                            luckyRuleLabels.add(rule.name + " · " + rule.address);
                        }
                        luckyRuleAdapter.notifyDataSetChanged();
                        StringBuilder ruleSummary = new StringBuilder("已登录规则：\n");
                        for (com.omniplay.tv.data.LuckyStunClient.Rule rule : result.rules) {
                            ruleSummary.append("• ").append(rule.name).append("：").append(rule.address).append("\n");
                        }
                        luckyRuleList.setText(ruleSummary.toString().trim());
                        if (result.selectedRule != null) {
                            int index = luckyRuleIds.indexOf(result.selectedRule.id);
                            if (index >= 0) {
                                luckyRuleSpinner.setSelection(index);
                            }
                        }
                        luckyStatus.setText(result.message + "\n当前服务端：" + api.serverUrl());
                        luckyUpdate.setEnabled(true);
                    },
                    error -> {
                        luckyStatus.setText("Lucky STUN 更新失败：" + error.getMessage());
                        luckyUpdate.setEnabled(true);
                    });
        });

        if (message != null && !message.isEmpty()) {
            TextView error = body(message);
            error.setTextColor(COLOR_DANGER);
            page.addView(error);
        }

        Button login = button("登录");
        page.addView(login, margin(matchWidth(), 0, dp(18), 0, 0));
        login.setOnClickListener(view -> {
            showLoading("正在登录");
            runAsync(
                    () -> api.login(
                            serverInput.getText().toString(),
                            usernameInput.getText().toString(),
                            passwordInput.getText().toString()),
                    status -> showHome(),
                    error -> showSetup(error.getMessage()));
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
        TextView server = text(api.serverUrl(), 13, Typeface.NORMAL, COLOR_MUTED);
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
            page.addView(body("媒体库为空，请先在服务端添加媒体源并扫描。"));
        }
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
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN | WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        root.removeAllViews();
        root.setBackgroundColor(Color.BLACK);

        playerView = new MpvVideoView(this);
        root.addView(playerView, match());

        LinearLayout top = playerOverlay(file);
        root.addView(top, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP));

        LinearLayout controls = playerControls();
        root.addView(controls, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(64), Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL));

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
                    }
                },
                error -> renderPlayerError(error.getMessage()));
    }

    private PlaybackTarget resolvePlaybackTarget(Models.VideoFile file) throws Exception {
        String streamUrl = api.streamUrl(file.id);
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
        if (screen == Screen.DETAIL) {
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

    private void destroyPlayer() {
        RemoteIsoStreamServer.shared().clear();
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
