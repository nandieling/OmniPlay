package com.omniplay.tv.data;

import android.content.Context;
import android.content.SharedPreferences;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.io.UnsupportedEncodingException;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.util.List;
import java.util.Map;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;

public final class OmniPlayApi {
    private static final String PREFS = "omniplay_tv";
    private static final String KEY_SERVER_URL = "server_url";
    private static final String KEY_COOKIE = "session_cookie";
    private static final String KEY_LIBRARY_ITEMS_CACHE = "library_items_cache";
    private static final String KEY_LOGIN_USERNAME = "login_username";
    private static final String KEY_LOGIN_PASSWORD = "login_password";
    private static final String KEY_LUCKY_URL = "lucky_stun_url";
    private static final String KEY_LUCKY_USERNAME = "lucky_stun_username";
    private static final String KEY_LUCKY_PASSWORD = "lucky_stun_password";
    private static final String KEY_LUCKY_RULE_ID = "lucky_stun_rule_id";
    private static final String KEY_LUCKY_RULE_NAME = "lucky_stun_rule_name";
    private static final String KEY_LUCKY_AUTO_UPDATE = "lucky_stun_auto_update";
    private static final String KEY_LUCKY_INTERVAL_MINUTES = "lucky_stun_interval_minutes";

    private final SharedPreferences preferences;
    private String serverUrl;
    private String sessionCookie;
    private final ScheduledExecutorService luckyScheduler = Executors.newSingleThreadScheduledExecutor();
    private ScheduledFuture<?> luckyScheduledTask;

    public OmniPlayApi(Context context) {
        preferences = context.getApplicationContext().getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        serverUrl = normalizeServerUrl(preferences.getString(KEY_SERVER_URL, ""));
        sessionCookie = preferences.getString(KEY_COOKIE, "");
        startLuckyStunAutoUpdate();
    }

    public boolean hasServerUrl() {
        return serverUrl != null && !serverUrl.isEmpty();
    }

    public String serverUrl() {
        return serverUrl == null ? "" : serverUrl;
    }

    public String cookieHeader() {
        return sessionCookie == null ? "" : sessionCookie;
    }

    public String savedUsername() {
        return preferences.getString(KEY_LOGIN_USERNAME, "");
    }

    public String savedPassword() {
        return preferences.getString(KEY_LOGIN_PASSWORD, "");
    }

    public void setServerUrl(String value) {
        serverUrl = normalizeServerUrl(value);
        preferences.edit().putString(KEY_SERVER_URL, serverUrl).apply();
    }

    public String luckyStunManagementUrl() {
        return preferences.getString(KEY_LUCKY_URL, "");
    }

    public String luckyStunUsername() {
        return preferences.getString(KEY_LUCKY_USERNAME, "");
    }

    public String luckyStunPassword() {
        return preferences.getString(KEY_LUCKY_PASSWORD, "");
    }

    public String luckyStunRuleId() {
        return preferences.getString(KEY_LUCKY_RULE_ID, "");
    }

    public boolean luckyStunAutoUpdate() {
        return preferences.getBoolean(KEY_LUCKY_AUTO_UPDATE, false);
    }

    public int luckyStunUpdateIntervalMinutes() {
        return Math.max(5, Math.min(1440, preferences.getInt(KEY_LUCKY_INTERVAL_MINUTES, 30)));
    }

    public void saveLuckyStunSettings(String managementUrl, String username, String password, String ruleId, String ruleName, boolean autoUpdate, int intervalMinutes) {
        preferences.edit()
                .putString(KEY_LUCKY_URL, trimOuterWhitespace(managementUrl))
                .putString(KEY_LUCKY_USERNAME, trimOuterWhitespace(username))
                .putString(KEY_LUCKY_PASSWORD, password == null ? "" : password)
                .putString(KEY_LUCKY_RULE_ID, trimOuterWhitespace(ruleId))
                .putString(KEY_LUCKY_RULE_NAME, trimOuterWhitespace(ruleName))
                .putBoolean(KEY_LUCKY_AUTO_UPDATE, autoUpdate)
                .putInt(KEY_LUCKY_INTERVAL_MINUTES, Math.max(5, Math.min(1440, intervalMinutes)))
                .apply();
        startLuckyStunAutoUpdate();
    }

    public LuckyStunClient.Result refreshLuckyStun(String managementUrl, String username, String password, String ruleId, String ruleName) throws IOException, JSONException {
        saveLuckyStunSettings(managementUrl, username, password, ruleId, ruleName, luckyStunAutoUpdate(), luckyStunUpdateIntervalMinutes());
        LuckyStunClient.Result result = LuckyStunClient.fetch(
                luckyStunManagementUrl(),
                luckyStunUsername(),
                luckyStunPassword(),
                luckyStunRuleId(),
                preferences.getString(KEY_LUCKY_RULE_NAME, ""));
        if (result.selectedRule != null) {
            preferences.edit()
                    .putString(KEY_LUCKY_RULE_ID, result.selectedRule.id)
                    .putString(KEY_LUCKY_RULE_NAME, result.selectedRule.name)
                    .apply();
            String address = LuckyStunClient.normalizeAddress(result.selectedRule.address);
            if (address != null && !address.isEmpty()) {
                setServerUrl(address);
            }
        }
        return result;
    }

    public void startLuckyStunAutoUpdate() {
        if (luckyScheduledTask != null) {
            luckyScheduledTask.cancel(false);
            luckyScheduledTask = null;
        }
        if (!luckyStunAutoUpdate() || luckyStunManagementUrl().trim().isEmpty()) {
            return;
        }
        long interval = luckyStunUpdateIntervalMinutes();
        luckyScheduledTask = luckyScheduler.scheduleWithFixedDelay(() -> {
            try {
                LuckyStunClient.Result result = LuckyStunClient.fetch(
                        luckyStunManagementUrl(),
                        luckyStunUsername(),
                        luckyStunPassword(),
                        luckyStunRuleId(),
                        preferences.getString(KEY_LUCKY_RULE_NAME, ""));
                if (result.selectedRule != null) {
                    preferences.edit()
                            .putString(KEY_LUCKY_RULE_ID, result.selectedRule.id)
                            .putString(KEY_LUCKY_RULE_NAME, result.selectedRule.name)
                            .apply();
                    String address = LuckyStunClient.normalizeAddress(result.selectedRule.address);
                    if (address != null && !address.isEmpty()) {
                        setServerUrl(address);
                    }
                }
            } catch (Exception ignored) {
            }
        }, interval, interval, TimeUnit.MINUTES);
    }

    public Models.AuthStatus authStatus() throws IOException, JSONException {
        return Models.AuthStatus.fromJson(new JSONObject(request("GET", "/api/auth/status", null)));
    }

    public Models.AuthStatus login(String baseUrl, String username, String password) throws IOException, JSONException {
        setServerUrl(baseUrl);
        sessionCookie = "";
        preferences.edit().remove(KEY_COOKIE).apply();
        JSONObject body = new JSONObject();
        body.put("username", trimOuterWhitespace(username));
        body.put("password", trimOuterWhitespace(password));
        Models.AuthStatus status = Models.AuthStatus.fromJson(new JSONObject(request("POST", "/api/auth/login", body.toString())));
        preferences.edit()
                .putString(KEY_COOKIE, sessionCookie)
                .putString(KEY_LOGIN_USERNAME, trimOuterWhitespace(username))
                .putString(KEY_LOGIN_PASSWORD, trimOuterWhitespace(password))
                .apply();
        return status;
    }

    public void disconnect() {
        if (luckyScheduledTask != null) {
            luckyScheduledTask.cancel(false);
            luckyScheduledTask = null;
        }
        if (hasServerUrl()) {
            try {
                request("POST", "/api/auth/logout", "{}");
            } catch (Exception ignored) {
            }
        }

        serverUrl = "";
        sessionCookie = "";
        preferences.edit()
                .remove(KEY_SERVER_URL)
                .remove(KEY_COOKIE)
                .remove(KEY_LIBRARY_ITEMS_CACHE)
                .apply();
    }

    public List<Models.LibraryItem> getLibraryItems() throws IOException, JSONException {
        String response = request("GET", "/api/library/items", null);
        preferences.edit().putString(KEY_LIBRARY_ITEMS_CACHE, response).apply();
        return Models.libraryItemsFromJson(new JSONArray(response));
    }

    public List<Models.LibraryItem> getCachedLibraryItems() {
        String cached = preferences.getString(KEY_LIBRARY_ITEMS_CACHE, "");
        if (cached == null || cached.isEmpty()) {
            return java.util.Collections.emptyList();
        }

        try {
            return Models.libraryItemsFromJson(new JSONArray(cached));
        } catch (JSONException ignored) {
            return java.util.Collections.emptyList();
        }
    }

    public Models.AppSettings getSettings() throws IOException, JSONException {
        return Models.AppSettings.fromJson(new JSONObject(request("GET", "/api/settings", null)));
    }

    public Models.LibraryDetail getLibraryItemDetail(String id) throws IOException, JSONException {
        return Models.LibraryDetail.fromJson(new JSONObject(request("GET", "/api/library/items/" + encode(id), null)));
    }

    public List<Models.SubtitleTrack> getSubtitles(String videoFileId) throws IOException, JSONException {
        return Models.subtitleTracksFromJson(new JSONArray(request("GET", "/api/playback/files/" + encode(videoFileId) + "/subtitles", null)));
    }

    public Models.PlaybackFileStreams getPlaybackStreams(String videoFileId) throws IOException, JSONException {
        return Models.PlaybackFileStreams.fromJson(new JSONObject(request("GET", "/api/playback/files/" + encode(videoFileId) + "/streams", null)));
    }

    public Models.SubtitleCacheStatus getSubtitleCacheStatus(String videoFileId) throws IOException, JSONException {
        return Models.SubtitleCacheStatus.fromJson(new JSONObject(request("GET", "/api/playback/files/" + encode(videoFileId) + "/subtitle-cache/status", null)));
    }

    public Models.PlaybackDecision getPlaybackDecision(String videoFileId) throws IOException, JSONException {
        return getPlaybackDecision(videoFileId, true);
    }

    public Models.PlaybackDecision getPlaybackDecision(String videoFileId, boolean hardware) throws IOException, JSONException {
        Models.PlaybackDecision decision = Models.PlaybackDecision.fromJson(new JSONObject(request(
                "GET",
                "/api/playback/decision/" + encode(videoFileId) + "?quality=auto&subtitleMode=off&hardware=" + (hardware ? "true" : "false"),
                null)));
        return decision == null ? null : new Models.PlaybackDecision(
                decision.fileId,
                decision.mode,
                absolutePlaybackUrl(decision.streamUrl),
                absolutePlaybackUrl(decision.manifestUrl),
                decision.sessionId,
                decision.isReady,
                decision.reason,
                decision.durationSeconds);
    }

    public void updatePlaybackProgress(String videoFileId, double positionSeconds, double durationSeconds) throws IOException, JSONException {
        JSONObject body = new JSONObject();
        body.put("videoFileId", videoFileId);
        body.put("positionSeconds", Math.max(0, positionSeconds));
        body.put("durationSeconds", Math.max(0, durationSeconds));
        request("POST", "/api/playback/progress", body.toString());
    }

    public void setWatchedStatus(String videoFileId, boolean isWatched, double durationSeconds) throws IOException, JSONException {
        JSONObject body = new JSONObject();
        body.put("videoFileId", videoFileId);
        body.put("isWatched", isWatched);
        body.put("durationSeconds", Math.max(0, durationSeconds));
        request("POST", "/api/playback/watched", body.toString());
    }

    public Models.LibraryDetail setLibraryItemWatchedStatus(String libraryItemId, boolean isWatched) throws IOException, JSONException {
        JSONObject body = new JSONObject();
        body.put("libraryItemId", libraryItemId);
        body.put("isWatched", isWatched);
        return Models.LibraryDetail.fromJson(new JSONObject(request("POST", "/api/library/items/" + encode(libraryItemId) + "/watched", body.toString())));
    }

    public String posterUrl(String posterAssetId) {
        return absoluteUrl("/api/assets/posters/" + encode(posterAssetId));
    }

    public String thumbnailUrl(String thumbnailAssetId) {
        return absoluteUrl("/api/assets/thumbnails/" + encode(thumbnailAssetId));
    }

    public String streamUrl(String videoFileId) {
        return absoluteUrl("/api/playback/files/" + encode(videoFileId) + "/stream");
    }

    public String streamUrl(String videoFileId, String ticket) {
        return withTicket(streamUrl(videoFileId), ticket);
    }

    public String createPlaybackTicket(String videoFileId) throws IOException, JSONException {
        String response = request("POST", "/api/playback/files/" + encode(videoFileId) + "/ticket", "{}");
        JSONObject json = new JSONObject(response);
        String token = json.optString("token");
        return token == null || token.isEmpty() ? json.optString("ticket") : token;
    }

    public void stopHlsSession(String sessionId) throws IOException {
        if (sessionId == null || sessionId.isEmpty()) {
            return;
        }

        request("POST", "/api/playback/hls/" + encode(sessionId) + "/stop", "{}");
    }

    public String subtitleUrl(Models.SubtitleTrack track) {
        if (track == null || track.webVttUrl == null || track.webVttUrl.isEmpty()) {
            return "";
        }

        if (track.webVttUrl.startsWith("http://") || track.webVttUrl.startsWith("https://")) {
            return track.webVttUrl;
        }

        return absoluteUrl(track.webVttUrl);
    }

    public String subtitleUrl(Models.SubtitleTrack track, String ticket) {
        return withTicket(subtitleUrl(track), ticket);
    }

    public String embeddedSubtitleUrl(String videoFileId, int subtitleOrdinal, String ticket) {
        return withTicket(absoluteUrl("/api/playback/files/" + encode(videoFileId) + "/embedded-subtitles/" + subtitleOrdinal + ".vtt"), ticket);
    }

    public String embeddedPgsSubtitleUrl(String videoFileId, int subtitleOrdinal, String ticket) {
        return withTicket(absoluteUrl("/api/playback/files/" + encode(videoFileId) + "/embedded-subtitles/" + subtitleOrdinal + ".sup"), ticket);
    }

    private String request(String method, String path, String body) throws IOException {
        ensureServerUrl();
        HttpURLConnection connection = (HttpURLConnection) new URL(absoluteUrl(path)).openConnection();
        connection.setConnectTimeout(8000);
        connection.setReadTimeout(30000);
        connection.setRequestMethod(method);
        connection.setRequestProperty("Accept", "application/json");
        connection.setRequestProperty("User-Agent", "OmniPlay-Android/0.1");
        if (sessionCookie != null && !sessionCookie.isEmpty()) {
            connection.setRequestProperty("Cookie", sessionCookie);
        }

        if (body != null) {
            byte[] bytes = body.getBytes(StandardCharsets.UTF_8);
            connection.setDoOutput(true);
            connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
            connection.setFixedLengthStreamingMode(bytes.length);
            try (OutputStream output = connection.getOutputStream()) {
                output.write(bytes);
            }
        }

        int status = connection.getResponseCode();
        captureSessionCookie(connection.getHeaderFields());
        InputStream stream = status >= 400 ? connection.getErrorStream() : connection.getInputStream();
        String text = readText(stream);
        connection.disconnect();
        if (status < 200 || status >= 300) {
            throw new IOException(errorMessage(status, text));
        }
        return text;
    }

    private void captureSessionCookie(Map<String, List<String>> headers) {
        for (Map.Entry<String, List<String>> entry : headers.entrySet()) {
            if (entry.getKey() == null || !"Set-Cookie".equalsIgnoreCase(entry.getKey())) {
                continue;
            }

            for (String value : entry.getValue()) {
                if (value == null || !value.startsWith("omniplay_session=")) {
                    continue;
                }

                int end = value.indexOf(';');
                sessionCookie = end > 0 ? value.substring(0, end) : value;
                preferences.edit().putString(KEY_COOKIE, sessionCookie).apply();
                return;
            }
        }
    }

    private String absoluteUrl(String path) {
        String normalizedPath = path.startsWith("/") ? path : "/" + path;
        return serverUrl() + normalizedPath;
    }

    private String absolutePlaybackUrl(String value) {
        if (value == null || value.isEmpty()) {
            return value;
        }
        if (value.startsWith("http://") || value.startsWith("https://")) {
            return value;
        }
        return absoluteUrl(value);
    }

    private static String withTicket(String url, String ticket) {
        if (url == null || url.isEmpty() || ticket == null || ticket.isEmpty()) {
            return url;
        }

        return url + (url.contains("?") ? "&" : "?") + "ticket=" + encode(ticket);
    }

    private void ensureServerUrl() {
        if (!hasServerUrl()) {
            throw new IllegalStateException("请先设置 OmniPlay 服务端地址。");
        }
    }

    private static String normalizeServerUrl(String value) {
        String trimmed = value == null ? "" : value.trim();
        if (!trimmed.isEmpty() && !trimmed.contains("://")) {
            trimmed = "http://" + trimmed;
        }
        while (trimmed.endsWith("/")) {
            trimmed = trimmed.substring(0, trimmed.length() - 1);
        }
        return trimmed;
    }

    private static String trimOuterWhitespace(String value) {
        if (value == null || value.isEmpty()) {
            return "";
        }

        int start = 0;
        int end = value.length();
        while (start < end && Character.isWhitespace(value.charAt(start))) {
            start++;
        }
        while (end > start && Character.isWhitespace(value.charAt(end - 1))) {
            end--;
        }
        return value.substring(start, end);
    }

    private static String encode(String value) {
        try {
            return URLEncoder.encode(value, "UTF-8").replace("+", "%20");
        } catch (UnsupportedEncodingException error) {
            throw new IllegalStateException(error);
        }
    }

    private static String readText(InputStream stream) throws IOException {
        if (stream == null) {
            return "";
        }

        StringBuilder builder = new StringBuilder();
        try (BufferedReader reader = new BufferedReader(new InputStreamReader(stream, StandardCharsets.UTF_8))) {
            String line;
            while ((line = reader.readLine()) != null) {
                builder.append(line);
            }
        }
        return builder.toString();
    }

    private static String errorMessage(int status, String body) {
        try {
            JSONObject json = new JSONObject(body);
            String error = json.optString("error");
            if (error != null && !error.isEmpty()) {
                return error;
            }
        } catch (JSONException ignored) {
        }

        return "请求失败：" + status;
    }
}
