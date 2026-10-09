package com.omniplay.tv.local;

import android.content.Context;
import android.content.SharedPreferences;
import android.os.Environment;
import android.text.TextUtils;

import com.omniplay.tv.data.Models;

import java.io.File;

/**
 * Device-local preferences for the standalone TV build. Mirrors the mac app's
 * 偏好设置 surface: scan behaviour, track/subtitle defaults, playback quality,
 * scrape language and the offline cache directory.
 */
public final class TvSettings {
    public static final String PREFS = "omniplay_tv_local";

    public static final String KEY_AUTO_SCAN_ON_STARTUP = "auto_scan_on_startup";
    public static final String KEY_AUTO_SCRAPE_AFTER_SCAN = "auto_scrape_after_scan";
    public static final String KEY_SHOW_EPISODE_DETAILS = "show_episode_details";
    public static final String KEY_SHOW_REAL_SOURCE_PATH = "show_real_source_path";
    public static final String KEY_DEFAULT_AUDIO_LANGUAGE = "default_audio_language";
    public static final String KEY_DEFAULT_SUBTITLE_LANGUAGE = "default_subtitle_language";
    public static final String KEY_PLAYBACK_QUALITY_MODE = "playback_quality_mode";
    public static final String KEY_SCRAPE_LANGUAGE = "scrape_language";
    public static final String KEY_OFFLINE_CACHE_DIR = "offline_cache_dir";
    public static final String KEY_PHONE_INPUT_TOKEN = "phone_input_token";
    public static final String KEY_THEME = "theme_id";

    public static final String QUALITY_SMOOTH = "smooth";
    public static final String QUALITY_BALANCED = "balanced";
    public static final String QUALITY_HIGH = "quality";

    private final SharedPreferences preferences;

    public TvSettings(Context context) {
        preferences = context.getApplicationContext().getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    public boolean autoScanOnStartup() {
        return preferences.getBoolean(KEY_AUTO_SCAN_ON_STARTUP, true);
    }

    public void setAutoScanOnStartup(boolean enabled) {
        preferences.edit().putBoolean(KEY_AUTO_SCAN_ON_STARTUP, enabled).apply();
    }

    public boolean autoScrapeAfterScan() {
        return preferences.getBoolean(KEY_AUTO_SCRAPE_AFTER_SCAN, true);
    }

    public void setAutoScrapeAfterScan(boolean enabled) {
        preferences.edit().putBoolean(KEY_AUTO_SCRAPE_AFTER_SCAN, enabled).apply();
    }

    public boolean showEpisodeDetails() {
        return preferences.getBoolean(KEY_SHOW_EPISODE_DETAILS, true);
    }

    public void setShowEpisodeDetails(boolean enabled) {
        preferences.edit().putBoolean(KEY_SHOW_EPISODE_DETAILS, enabled).apply();
    }

    public boolean showRealSourcePath() {
        return preferences.getBoolean(KEY_SHOW_REAL_SOURCE_PATH, true);
    }

    public void setShowRealSourcePath(boolean enabled) {
        preferences.edit().putBoolean(KEY_SHOW_REAL_SOURCE_PATH, enabled).apply();
    }

    public String defaultAudioLanguage() {
        return preferences.getString(KEY_DEFAULT_AUDIO_LANGUAGE, "smart");
    }

    public void setDefaultAudioLanguage(String value) {
        preferences.edit().putString(KEY_DEFAULT_AUDIO_LANGUAGE, value).apply();
    }

    public String defaultSubtitleLanguage() {
        return preferences.getString(KEY_DEFAULT_SUBTITLE_LANGUAGE, "zh");
    }

    public void setDefaultSubtitleLanguage(String value) {
        preferences.edit().putString(KEY_DEFAULT_SUBTITLE_LANGUAGE, value).apply();
    }

    public String playbackQualityMode() {
        String value = preferences.getString(KEY_PLAYBACK_QUALITY_MODE, QUALITY_BALANCED);
        return QUALITY_SMOOTH.equals(value) || QUALITY_HIGH.equals(value) ? value : QUALITY_BALANCED;
    }

    public void setPlaybackQualityMode(String value) {
        preferences.edit().putString(KEY_PLAYBACK_QUALITY_MODE, value).apply();
    }

    /** TMDB scrape language: {@code zh-CN} or {@code en-US}. */
    public String scrapeLanguage() {
        String value = preferences.getString(KEY_SCRAPE_LANGUAGE, "zh-CN");
        return "en-US".equals(value) ? "en-US" : "zh-CN";
    }

    public void setScrapeLanguage(String value) {
        preferences.edit().putString(KEY_SCRAPE_LANGUAGE, "en-US".equals(value) ? "en-US" : "zh-CN").apply();
    }

    public String offlineCacheDir() {
        return preferences.getString(KEY_OFFLINE_CACHE_DIR, "");
    }

    public void setOfflineCacheDir(String value) {
        preferences.edit().putString(KEY_OFFLINE_CACHE_DIR, value == null ? "" : value.trim()).apply();
    }

    public File offlineCacheDirectory() {
        String configured = offlineCacheDir();
        if (TextUtils.isEmpty(configured)) {
            return new File(Environment.getExternalStorageDirectory(), "OmniPlayCache");
        }
        return new File(configured);
    }

    /**
     * Appearance id, one of the ids in {@code ui.Theme.VARIANTS}. Kept as a literal here so this
     * class stays free of the UI layer: the JVM harnesses compile local/ without it.
     */
    public static final String DEFAULT_THEME_ID = "crystal";

    public String themeId() {
        return preferences.getString(KEY_THEME, DEFAULT_THEME_ID);
    }

    public void setThemeId(String value) {
        preferences.edit().putString(KEY_THEME, value).apply();
    }

    /**
     * Token that guards the phone input page. Generated once and kept, so a bookmarked
     * page keeps working across launches; only the port changes between sessions.
     */

    public String phoneInputToken() {
        String existing = preferences.getString(KEY_PHONE_INPUT_TOKEN, "");
        if (!TextUtils.isEmpty(existing)) {
            return existing;
        }
        char[] alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789".toCharArray();
        java.security.SecureRandom random = new java.security.SecureRandom();
        StringBuilder generated = new StringBuilder(10);
        for (int index = 0; index < 10; index++) {
            generated.append(alphabet[random.nextInt(alphabet.length)]);
        }
        String token = generated.toString();
        preferences.edit().putString(KEY_PHONE_INPUT_TOKEN, token).apply();
        return token;
    }

    /** Feeds the playback path that already reads {@code api.getSettings()}. */
    public Models.AppSettings toAppSettings() {
        return Models.AppSettings.withPlayback(new Models.PlaybackSettings(
                showEpisodeDetails(), defaultAudioLanguage(), defaultSubtitleLanguage()));
    }

    public static String qualityLabel(String mode) {
        if (QUALITY_SMOOTH.equals(mode)) {
            return "流畅优先";
        }
        return QUALITY_HIGH.equals(mode) ? "画质优先" : "平衡 (推荐)";
    }

    public static String languageLabel(String value) {
        if (value == null || "smart".equals(value)) {
            return "智能匹配 (制片国家语言)";
        }
        switch (value) {
            case "zh":
                return "中文";
            case "en":
                return "英语";
            case "ja":
                return "日语";
            case "no":
                return "关闭字幕";
            default:
                return value;
        }
    }

    public static String scrapeLanguageLabel(String value) {
        return "en-US".equals(value) ? "English" : "简体中文";
    }
}
