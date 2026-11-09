package com.omniplay.tv.phone;

import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageInfo;
import android.graphics.Typeface;
import android.text.TextUtils;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;

import com.omniplay.tv.LauncherActivity;
import com.omniplay.tv.local.TvSettings;
import com.omniplay.tv.ui.Theme;

import java.util.ArrayList;
import java.util.List;

/**
 * 设置：主题（与电视端同一套 {@link Theme} 变体，含深色）、TMDB 与反代、刮削与扫描开关、
 * 播放偏好、清库。选项一律用「点一行选一档」的形态，手机上比遥控器那种分段控件好按。
 */
public final class PhoneSettingsSheet {
    private PhoneSettingsSheet() {
    }

    public static void show(final PhoneHost host) {
        final Activity activity = host.activity();
        final PhoneUi.Sheet sheet = PhoneUi.sheet(activity, "设置", null);
        final LinearLayout body = sheet.body;
        final TvSettings settings = host.engine().settings();

        body.addView(PhoneUi.section(activity, "外观"), PhoneUi.block(activity));
        optionRow(host, sheet, body, "主题", settings.themeId(), themeLabel(settings.themeId()),
                Theme.VARIANTS.size() == 0 ? null : themeOptions(),
                id -> {
                    settings.setThemeId(id);
                    Theme.apply(id);
                    host.refreshLibrary();
                });

        String shell = LauncherActivity.shellMode(activity);
        optionRow(host, sheet, body, "打开时进的界面", shell,
                LauncherActivity.shellLabel(shell), shellOptions(),
                mode -> {
                    LauncherActivity.setShellMode(activity, mode);
                    host.toast("已记住，下次打开进" + LauncherActivity.shellLabel(mode) + "界面");
                    offerShellRestart(host, mode);
                });

        body.addView(PhoneUi.section(activity, "刮削"), PhoneUi.block(activity));
        credentialFields(host, body);
        optionRow(host, sheet, body, "刮削语言", settings.scrapeLanguage(),
                languageLabel(settings.scrapeLanguage()), languageOptions(), settings::setScrapeLanguage);

        body.addView(PhoneUi.section(activity, "扫描"), PhoneUi.block(activity));
        toggleRow(host, body, "打开软件就扫描", settings.autoScanOnStartup(),
                settings::setAutoScanOnStartup);
        toggleRow(host, body, "扫描完自动刮削", settings.autoScrapeAfterScan(),
                settings::setAutoScrapeAfterScan);

        body.addView(PhoneUi.section(activity, "详情页"), PhoneUi.block(activity));
        toggleRow(host, body, "显示分集简介与进度", settings.showEpisodeDetails(),
                settings::setShowEpisodeDetails);
        toggleRow(host, body, "显示真实源文件路径", settings.showRealSourcePath(),
                settings::setShowRealSourcePath);

        body.addView(PhoneUi.section(activity, "播放"), PhoneUi.block(activity));
        optionRow(host, sheet, body, "默认音轨语言", settings.defaultAudioLanguage(),
                languageLabel(settings.defaultAudioLanguage()), languageOptions(),
                settings::setDefaultAudioLanguage);
        optionRow(host, sheet, body, "默认字幕语言", settings.defaultSubtitleLanguage(),
                languageLabel(settings.defaultSubtitleLanguage()), languageOptions(),
                settings::setDefaultSubtitleLanguage);
        optionRow(host, sheet, body, "网络缓冲", settings.playbackQualityMode(),
                qualityLabel(settings.playbackQualityMode()), qualityOptions(),
                settings::setPlaybackQualityMode);

        body.addView(PhoneUi.section(activity, "数据"), PhoneUi.block(activity));
        Button clear = PhoneUi.ghost(activity, "清空媒体库（重新扫描刮削）");
        body.addView(clear, PhoneUi.block(activity, PhoneUi.dp(activity, 6), 0));
        clear.setOnClickListener(view -> confirmClear(host, sheet));

        body.addView(PhoneUi.section(activity, "关于"), PhoneUi.block(activity));
        // 与 mac 版对齐：UsageGuideView / OpenSourceInfoView 在安卓这边是两行入口
        LinearLayout helpRow = PhoneUi.buttonRow(activity);
        body.addView(helpRow, PhoneUi.block(activity));
        Button guide = PhoneUi.ghost(activity, "使用指南");
        helpRow.addView(guide, PhoneUi.width(0, PhoneUi.dp(activity, 44), 1f));
        Button about = PhoneUi.ghost(activity, "关于与开源");
        helpRow.addView(about, PhoneUi.margin(PhoneUi.width(0, PhoneUi.dp(activity, 44), 1f),
                PhoneUi.dp(activity, 8), 0, 0, 0));
        guide.setOnClickListener(view -> PhoneGuideSheet.showGuide(host));
        about.setOnClickListener(view -> PhoneGuideSheet.showAbout(host));
        body.addView(PhoneUi.body(activity, "觅影 OmniPlay 手机版 · " + version(host)),
                PhoneUi.block(activity));
    }

    // ---- TMDB ---------------------------------------------------------------

    private static void credentialFields(final PhoneHost host, LinearLayout body) {
        Activity activity = host.activity();
        final EditText key = PhoneUi.field(activity, "TMDB API Key（留空用反代）",
                host.engine().tmdb().apiKey(), false);
        body.addView(key, PhoneUi.block(activity, PhoneUi.dp(activity, 6), 0));
        final EditText proxy = PhoneUi.field(activity, "反向代理地址（留空直连官方 API）",
                host.engine().tmdb().proxyUrl(), false);
        body.addView(proxy, PhoneUi.margin(PhoneUi.block(activity), 0, PhoneUi.dp(activity, 8), 0, 0));
        final EditText proxyKey = PhoneUi.field(activity, "反代 Key（反代需要时才填）",
                host.engine().tmdb().proxyApiKey(), false);
        body.addView(proxyKey, PhoneUi.margin(PhoneUi.block(activity), 0, PhoneUi.dp(activity, 8), 0, 0));

        final TextView status = PhoneUi.statusLine(activity,
                host.engine().tmdbConfigured() ? "已配置，自动刮削可用。" : "还没配 Key：自动刮削不跑，手动匹配可以只用豆瓣。");
        body.addView(status, PhoneUi.block(activity));

        LinearLayout row = PhoneUi.buttonRow(activity);
        body.addView(row, PhoneUi.block(activity, PhoneUi.dp(activity, 8), 0));
        Button save = PhoneUi.primary(activity, "保存");
        row.addView(save, PhoneUi.width(0, PhoneUi.dp(activity, 48), 1f));
        Button check = PhoneUi.ghost(activity, "检测连接");
        row.addView(check, PhoneUi.margin(PhoneUi.width(0, PhoneUi.dp(activity, 48), 1f),
                PhoneUi.dp(activity, 8), 0, 0, 0));
        save.setOnClickListener(view -> {
            host.engine().tmdb().setApiKey(key.getText().toString().trim());
            host.engine().tmdb().setProxyUrl(proxy.getText().toString().trim());
            host.engine().tmdb().setProxyApiKey(proxyKey.getText().toString().trim());
            status.setText(host.engine().tmdbConfigured()
                    ? "已保存，自动刮削可用。" : "已保存，但还没有可用的 Key。");
        });
        check.setOnClickListener(view -> {
            host.engine().tmdb().setApiKey(key.getText().toString().trim());
            host.engine().tmdb().setProxyUrl(proxy.getText().toString().trim());
            host.engine().tmdb().setProxyApiKey(proxyKey.getText().toString().trim());
            status.setText("正在检测…");
            host.io().execute(() -> {
                try {
                    boolean ok = host.engine().tmdb().checkConnection();
                    host.main().post(() -> status.setText(ok
                            ? "TMDB 能用。" : "TMDB 连不上：" + host.engine().tmdbStatusMessage()));
                } catch (Exception error) {
                    host.main().post(() -> status.setText("检测失败：" + error.getMessage()));
                }
            });
        });
    }

    // ---- 通用行 ---------------------------------------------------------------

    private static void toggleRow(PhoneHost host, LinearLayout body, String label,
                                  boolean value, java.util.function.Consumer<Boolean> setter) {
        Activity activity = host.activity();
        LinearLayout row = PhoneUi.buttonRow(activity);
        body.addView(row, PhoneUi.block(activity, PhoneUi.dp(activity, 4), 0));
        TextView text = PhoneUi.text(activity, label, 16, Typeface.NORMAL, Theme.TEXT);
        row.addView(text, PhoneUi.weight(activity));
        final Button toggle = PhoneUi.ghost(activity, value ? "已开启" : "已关闭");
        toggle.setTextColor(value ? Theme.ACCENT : Theme.SUBTLE);
        row.addView(toggle, PhoneUi.width(PhoneUi.dp(activity, 104), PhoneUi.dp(activity, 44)));
        toggle.setOnClickListener(view -> {
            boolean next = !value;
            setter.accept(next);
            toggle.setText(next ? "已开启" : "已关闭");
            toggle.setTextColor(next ? Theme.ACCENT : Theme.SUBTLE);
            host.toast(label + (next ? "：已开启" : "：已关闭"));
        });
    }

    /** 一行当前值，点开是一列候选；选中即写回。 */
    private static void optionRow(PhoneHost host, PhoneUi.Sheet parent, LinearLayout body,
                                  String label, String current, String currentLabel,
                                  List<String[]> options,
                                  java.util.function.Consumer<String> onSelect) {
        Activity activity = host.activity();
        if (options == null || options.isEmpty()) {
            return;
        }
        LinearLayout row = PhoneUi.buttonRow(activity);
        body.addView(row, PhoneUi.block(activity, PhoneUi.dp(activity, 4), 0));
        TextView text = PhoneUi.text(activity, label, 16, Typeface.NORMAL, Theme.TEXT);
        row.addView(text, PhoneUi.weight(activity));
        Button value = PhoneUi.ghost(activity, currentLabel);
        value.setSingleLine(true);
        value.setEllipsize(TextUtils.TruncateAt.END);
        row.addView(value, PhoneUi.width(PhoneUi.dp(activity, 160), PhoneUi.dp(activity, 44)));
        value.setOnClickListener(view -> {
            final PhoneUi.Sheet picker = PhoneUi.sheet(activity, label, "选一档");
            for (final String[] option : options) {
                Button item = PhoneUi.ghost(activity,
                        option[0].equals(current) ? "✓ " + option[1] : option[1]);
                picker.body.addView(item, PhoneUi.block(activity, PhoneUi.dp(activity, 6), 0));
                item.setOnClickListener(ignored -> {
                    picker.close();
                    onSelect.accept(option[0]);
                    value.setText(option[1]);
                });
            }
        });
    }

    /** 界面选择是下次打开才生效的，这里顺手递一步：要现在就把另一套界面拉起来也可以。 */
    private static void offerShellRestart(final PhoneHost host, final String mode) {
        Activity activity = host.activity();
        final PhoneUi.Sheet confirm = PhoneUi.sheet(activity,
                "换到" + LauncherActivity.shellLabel(mode) + "界面",
                "选择已经记住了，下次打开软件就进这一套。也可以现在就直接过去。");
        Button now = PhoneUi.primary(activity, "现在就去");
        confirm.body.addView(now, PhoneUi.block(activity, PhoneUi.dp(activity, 10), 0));
        now.setOnClickListener(view -> {
            confirm.close();
            Intent intent = new Intent(activity, LauncherActivity.class);
            intent.setFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TASK);
            activity.startActivity(intent);
            activity.finish();
        });
        Button later = PhoneUi.ghost(activity, "下次再说");
        confirm.body.addView(later, PhoneUi.block(activity, PhoneUi.dp(activity, 6), 0));
        later.setOnClickListener(view -> confirm.close());
    }

    private static void confirmClear(final PhoneHost host, final PhoneUi.Sheet sheet) {
        Activity activity = host.activity();
        final PhoneUi.Sheet confirm = PhoneUi.sheet(activity, "清空媒体库",
                "只清刮削结果与观看记录，媒体源保留，之后重新扫描即可。");
        Button go = PhoneUi.primary(activity, "确认清空");
        confirm.body.addView(go, PhoneUi.block(activity, PhoneUi.dp(activity, 10), 0));
        go.setOnClickListener(view -> {
            confirm.close();
            host.io().execute(() -> {
                host.engine().clearLibrary();
                host.main().post(() -> {
                    sheet.close();
                    host.refreshLibrary();
                    host.toast("媒体库已清空");
                });
            });
        });
    }

    // ---- 候选项 --------------------------------------------------------------

    private static List<String[]> themeOptions() {
        List<String[]> options = new ArrayList<>();
        for (Theme.Variant variant : Theme.VARIANTS) {
            options.add(new String[]{variant.id, variant.name});
        }
        return options;
    }

    private static String themeLabel(String id) {
        Theme.Variant variant = Theme.variant(id);
        return variant == null ? id : variant.name;
    }

    private static List<String[]> shellOptions() {
        List<String[]> options = new ArrayList<>();
        options.add(new String[]{LauncherActivity.SHELL_AUTO, "自动（按设备身份判）"});
        options.add(new String[]{LauncherActivity.SHELL_TV, "电视版（遥控器）"});
        options.add(new String[]{LauncherActivity.SHELL_PHONE, "手机版（触摸）"});
        return options;
    }

    private static List<String[]> languageOptions() {
        List<String[]> options = new ArrayList<>();
        options.add(new String[]{"", "自动"});
        options.add(new String[]{"zh", "中文"});
        options.add(new String[]{"en", "英语"});
        options.add(new String[]{"ja", "日语"});
        options.add(new String[]{"ko", "韩语"});
        return options;
    }

    private static String languageLabel(String value) {
        if (value == null || value.isEmpty()) {
            return "自动";
        }
        switch (value) {
            case "zh":
                return "中文";
            case "en":
                return "英语";
            case "ja":
                return "日语";
            case "ko":
                return "韩语";
            case "off":
                return "关闭";
            default:
                return value;
        }
    }

    private static List<String[]> qualityOptions() {
        List<String[]> options = new ArrayList<>();
        options.add(new String[]{TvSettings.QUALITY_SMOOTH, "流畅优先"});
        options.add(new String[]{TvSettings.QUALITY_BALANCED, "均衡"});
        options.add(new String[]{TvSettings.QUALITY_HIGH, "画质优先"});
        return options;
    }

    private static String qualityLabel(String value) {
        if (TvSettings.QUALITY_HIGH.equals(value)) {
            return "画质优先";
        }
        return TvSettings.QUALITY_SMOOTH.equals(value) ? "流畅优先" : "均衡";
    }

    private static String version(PhoneHost host) {
        try {
            PackageInfo info = host.activity().getPackageManager()
                    .getPackageInfo(host.activity().getPackageName(), 0);
            return info.versionName + " (" + info.versionCode + ")";
        } catch (Exception ignored) {
            return "未知版本";
        }
    }
}
