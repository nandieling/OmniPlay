package com.omniplay.tv.data;

import android.content.Context;

import com.omniplay.tv.local.LocalEngine;

import java.util.Collections;
import java.util.List;

/**
 * Standalone replacement for {@link OmniPlayApi}: everything runs on-device
 * (local folder / WebDAV sources, SQLite library, TMDB scraping).
 * Method signatures mirror OmniPlayApi so the activities can switch over
 * without touching their call sites.
 */
public final class LocalPlayApi {
    private final LocalEngine engine;

    public LocalPlayApi(Context context) {
        engine = LocalEngine.shared(context);
    }

    public LocalEngine engine() {
        return engine;
    }

    public boolean hasServerUrl() {
        return !engine.sources().isEmpty();
    }

    public String serverUrl() {
        return "";
    }

    public String cookieHeader() {
        return "";
    }

    public String savedUsername() {
        return "";
    }

    public String savedPassword() {
        return "";
    }

    public String luckyStunManagementUrl() {
        return "";
    }

    public String luckyStunUsername() {
        return "";
    }

    public String luckyStunPassword() {
        return "";
    }

    public String luckyStunRuleId() {
        return "";
    }

    public boolean luckyStunAutoUpdate() {
        return false;
    }

    public int luckyStunUpdateIntervalMinutes() {
        return 30;
    }

    public void saveLuckyStunSettings(String managementUrl, String username, String password,
                                      String ruleId, String ruleName, boolean autoUpdate, int intervalMinutes) {
        // Standalone build: sources are added directly, no Lucky STUN punch-through.
    }

    public void startLuckyStunAutoUpdate() {
    }

    public void disconnect() {
        // Standalone build: dropping back to the setup screen keeps the scraped library.
    }

    /** Explicit "清空媒体库索引" from the source manager. */
    public void wipeLibrary() {
        engine.clearLibrary();
    }

    public Models.AuthStatus authStatus() {
        return Models.AuthStatus.of(false, !engine.sources().isEmpty(), "local", "admin");
    }

    public Models.AuthStatus login(String baseUrl, String username, String password) {
        return Models.AuthStatus.of(false, true, "local", "admin");
    }

    public Models.AppSettings getSettings() {
        return engine.settings().toAppSettings();
    }

    public List<Models.LibraryItem> getLibraryItems() {
        return engine.libraryItems();
    }

    public List<Models.LibraryItem> getCachedLibraryItems() {
        return engine.libraryItems();
    }

    public Models.LibraryDetail getLibraryItemDetail(String id) {
        return engine.detail(id);
    }

    public List<Models.SubtitleTrack> getSubtitles(String videoFileId) {
        return Collections.emptyList();
    }

    public Models.PlaybackFileStreams getPlaybackStreams(String videoFileId) {
        return null;
    }

    public Models.SubtitleCacheStatus getSubtitleCacheStatus(String videoFileId) {
        return null;
    }

    public void updatePlaybackProgress(String videoFileId, double positionSeconds, double durationSeconds) {
        engine.updateProgress(videoFileId, positionSeconds, durationSeconds);
    }

    public void setWatchedStatus(String videoFileId, boolean isWatched, double durationSeconds) {
        engine.setWatched(videoFileId, isWatched, durationSeconds);
    }

    public Models.LibraryDetail setLibraryItemWatchedStatus(String libraryItemId, boolean isWatched) {
        return null;
    }

    public String posterUrl(String posterAssetId) {
        return posterAssetId == null || posterAssetId.isEmpty() ? "" : posterAssetId;
    }

    public String thumbnailUrl(String thumbnailAssetId) {
        return thumbnailAssetId == null || thumbnailAssetId.isEmpty() ? "" : thumbnailAssetId;
    }

    public String streamUrl(String videoFileId) {
        return "";
    }

    public String streamUrl(String videoFileId, String ticket) {
        return "";
    }

    public String createPlaybackTicket(String videoFileId) {
        return "";
    }

    public void stopHlsSession(String sessionId) {
    }

    public String subtitleUrl(Models.SubtitleTrack track) {
        return track == null ? "" : track.webVttUrl;
    }

    public String subtitleUrl(Models.SubtitleTrack track, String ticket) {
        return track == null ? "" : track.webVttUrl;
    }

    public String embeddedSubtitleUrl(String videoFileId, int subtitleOrdinal, String ticket) {
        return "";
    }

    public String embeddedPgsSubtitleUrl(String videoFileId, int subtitleOrdinal, String ticket) {
        return "";
    }

    public LuckyStunClient.Result refreshLuckyStun(String managementUrl, String username, String password,
                                                   String ruleId, String ruleName) {
        return new LuckyStunClient.Result(Collections.emptyList(), null, "独立版使用本地媒体源，无需 Lucky 穿透。");
    }
}
