package com.omniplay.tv.local;

import android.content.Context;
import android.content.SharedPreferences;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.IOException;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.atomic.AtomicLong;

/**
 * TMDB client ported from the OmniPlay (mac) TMDBService:
 * multi-search with scoring (title/year/media-type), proxy gateway support,
 * zh-CN localization merge and rate limiting.
 */
public final class TmdbClient {
    public static final String PREFS = "omniplay_tv_local";
    public static final String KEY_API_KEY = "tmdb_api_key";
    public static final String KEY_PROXY_URL = "tmdb_proxy_url";

    public static final class Result {
        public final int id;
        public final String title;
        public final String originalTitle;
        public final String overview;
        public final String posterPath;
        public final String releaseDate;
        public final double voteAverage;
        public final double popularity;
        public final String mediaType;

        Result(int id, String title, String originalTitle, String overview,
               String posterPath, String releaseDate, double voteAverage,
               double popularity, String mediaType) {
            this.id = id;
            this.title = title;
            this.originalTitle = originalTitle;
            this.overview = overview;
            this.posterPath = posterPath;
            this.releaseDate = releaseDate;
            this.voteAverage = voteAverage;
            this.popularity = popularity;
            this.mediaType = mediaType;
        }

        public String basePosterUrl() {
            if (posterPath == null || posterPath.isEmpty()) {
                return null;
            }
            if (posterPath.startsWith("http://") || posterPath.startsWith("https://")) {
                return posterPath;
            }
            return "https://image.tmdb.org/t/p/w500" + posterPath;
        }
    }

    private final SharedPreferences preferences;
    private final Object rateLock = new Object();
    private long nextAllowedMillis;

    public TmdbClient(Context context) {
        preferences = context.getApplicationContext().getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    public String apiKey() {
        return preferences.getString(KEY_API_KEY, "").trim();
    }

    public void setApiKey(String value) {
        preferences.edit().putString(KEY_API_KEY, value == null ? "" : value.trim()).apply();
    }

    public String proxyUrl() {
        return preferences.getString(KEY_PROXY_URL, "").trim();
    }

    public void setProxyUrl(String value) {
        preferences.edit().putString(KEY_PROXY_URL, value == null ? "" : value.trim()).apply();
    }

    public String effectiveApiKey() {
        String key = apiKey();
        String proxy = proxyUrl();
        if (!proxy.isEmpty()) {
            String proxyKey = preferences.getString("tmdb_proxy_api_key", "").trim();
            if (!proxyKey.isEmpty()) {
                key = proxyKey;
            }
        }
        return key;
    }

    public void setProxyApiKey(String value) {
        preferences.edit().putString("tmdb_proxy_api_key", value == null ? "" : value.trim()).apply();
    }

    public String proxyApiKey() {
        return preferences.getString("tmdb_proxy_api_key", "").trim();
    }

    public boolean isConfigured() {
        return !effectiveApiKey().isEmpty();
    }

    /**
     * TMDB 有两把钥匙：32 位的 v3 API Key 走 {@code ?api_key=}，v4 Read Access Token
     * 是一段很长的 JWT，只能走 {@code Authorization: Bearer}。mac 版按长度区分（>50 个字符
     * 就是 v4），这里沿用同一条判据，用户只需要往同一个框里粘一次。
     */
    public boolean usesBearerToken() {
        return usesBearerToken(effectiveApiKey());
    }

    public static boolean usesBearerToken(String key) {
        return key != null && key.trim().length() > 50;
    }

    /** v3 才需要往查询串里塞 api_key；v4 走请求头，查询串保持干净。 */
    public String apiKeyQuery() {
        return apiKeyQuery(false);
    }

    private String apiKeyQuery(boolean first) {
        String key = effectiveApiKey();
        if (key.isEmpty() || usesBearerToken(key)) {
            return "";
        }
        String encoded;
        try {
            encoded = URLEncoder.encode(key, "UTF-8");
        } catch (java.io.UnsupportedEncodingException impossible) {
            encoded = key;
        }
        return (first ? "?api_key=" : "&api_key=") + encoded;
    }

    /** Scrape language, shared with {@link TvSettings}. */
    public String language() {
        return "en-US".equals(preferences.getString(TvSettings.KEY_SCRAPE_LANGUAGE, "zh-CN"))
                ? "en-US" : "zh-CN";
    }

    private String imageLanguage() {
        return "en-US".equals(language()) ? "en,null" : "zh,null,en";
    }

    /** Resolves the API base: proxy gateway when configured, else official. */
    public String apiBase() {
        String proxy = proxyUrl();
        if (!proxy.isEmpty()) {
            return normalizeProxyBase(proxy);
        }
        return "https://api.themoviedb.org/3";
    }

    /**
     * 反代地址要补上 {@code /3}：TMDB 的接口全挂在 /3 下面，反代只是把这层前缀转发过去，
     * 少这一段就是 404（实测 https://…/tmdb0512/configuration 返回 nginx 404，
     * 而 …/tmdb0512/3/configuration 返回 TMDB 自己的 JSON）。与 mac 版的
     * proxyAPIBaseURL 同一条规则：路径为空就补 /3，已经以 /3 结尾就不动。
     */
    public static String normalizeProxyBase(String input) {
        String proxy = input == null ? "" : input.trim();
        if (proxy.isEmpty()) {
            return "https://api.themoviedb.org/3";
        }
        if (!proxy.contains("://")) {
            proxy = "https://" + proxy;
        }
        while (proxy.endsWith("/")) {
            proxy = proxy.substring(0, proxy.length() - 1);
        }
        int schemeEnd = proxy.indexOf("://");
        int pathStart = proxy.indexOf('/', schemeEnd + 3);
        String path = pathStart < 0 ? "" : proxy.substring(pathStart);
        if (!path.isEmpty() && (path.equals("/3") || path.endsWith("/3"))) {
            return proxy;
        }
        return proxy + "/3";
    }

    public boolean checkConnection() {
        return checkConnectionDetailed().connected;
    }

    /** 检测结果连原因一起给：反代没补 /3、Key 填成 v4、网络不通，症状都一样，原因不同。 */
    public static final class CheckResult {
        public final boolean connected;
        public final String message;

        CheckResult(boolean connected, String message) {
            this.connected = connected;
            this.message = message;
        }
    }

    public CheckResult checkConnectionDetailed() {
        String key = effectiveApiKey();
        if (key.isEmpty()) {
            return new CheckResult(false, "还没有填写 TMDB API Key 或 v4 Read Access Token。");
        }
        String url = apiBase() + "/configuration" + apiKeyQuery(true);
        int status = probeStatus(url);
        if (status >= 200 && status < 300) {
            return new CheckResult(true, isProxy()
                    ? "TMDB 连接正常（经反代 " + apiBase() + "）。"
                    : "TMDB 连接正常。");
        }
        if (status == 401 || status == 403) {
            return new CheckResult(false, "TMDB 拒绝了这把钥匙（HTTP " + status
                    + "）。反代只负责转发，API Key 仍要自己填；v4 Token 会以 Bearer 头发出。");
        }
        if (status == 404) {
            return new CheckResult(false, "反代地址下没有 /3/configuration（HTTP 404）。"
                    + "请只填反代根地址，例如 https://tmdb.example.com/tmdb0512。");
        }
        if (status <= 0) {
            return new CheckResult(false, "连不上 " + apiBase()
                    + "，请检查网络、地址拼写或证书。");
        }
        return new CheckResult(false, "TMDB 返回 HTTP " + status + "。");
    }

    /** GET /configuration 一次，只取状态码；-1 表示根本没走到 HTTP 层。 */
    private int probeStatus(String url) {
        int[] status = new int[1];
        try {
            getOrNull(url, status);
        } catch (Exception ignored) {
            return -1;
        }
        return status[0] == 0 ? -1 : status[0];
    }

    /**
     * Multi-search with the OmniPlay scoring: exact title, secondary query,
     * media-type preference, season awareness and year tolerance.
     */
    public Result multiSearch(String query, String year, String preferredMediaType, int preferredSeason,
                              String secondaryQuery) throws Exception {
        if (query == null || query.trim().isEmpty()) {
            return null;
        }
        List<Result> all = new ArrayList<>();
        for (int page = 1; page <= 2; page++) {
            String encoded = URLEncoder.encode(query, "UTF-8");
            String url = apiBase() + "/search/multi?query=" + encoded
                    + "&language=" + language() + "&page=" + page
                    + apiKeyQuery();
            JSONObject root = get(url);
            if (root == null) {
                break;
            }
            JSONArray results = root.optJSONArray("results");
            if (results == null) {
                break;
            }
            for (int i = 0; i < results.length(); i++) {
                JSONObject item = results.optJSONObject(i);
                if (item == null) {
                    continue;
                }
                String mediaType = item.optString("media_type", "").toLowerCase(Locale.ROOT);
                if (!mediaType.equals("movie") && !mediaType.equals("tv")) {
                    continue;
                }
                Result parsed = new Result(
                        item.optInt("id"),
                        item.optString("title", item.optString("name", "")),
                        item.optString("original_title", item.optString("original_name", "")),
                        item.optString("overview", ""),
                        item.optString("poster_path", null),
                        item.optString("release_date", item.optString("first_air_date", "")),
                        item.optDouble("vote_average", 0),
                        item.optDouble("popularity", 0),
                        mediaType);
                if (parsed.id <= 0) {
                    continue;
                }
                all.add(parsed);
            }
            int total = root.optInt("total_pages", 1);
            if (page >= total) {
                break;
            }
        }

        Result best = null;
        double bestScore = Double.NEGATIVE_INFINITY;
        for (Result item : all) {
            double score = score(item, query, year, preferredMediaType, preferredSeason, secondaryQuery);
            if (score > bestScore) {
                bestScore = score;
                best = item;
            }
        }
        return best;
    }

    private boolean isProxy() {
        return !proxyUrl().isEmpty();
    }

    private double score(Result item, String query, String year, String preferredMediaType,
                         int preferredSeason, String secondaryQuery) {
        double score = item.popularity;
        String queryNorm = normalizeTitle(query);
        List<String> candidates = matchableTitles(item);
        double bestTitleBonus = 0;
        for (String candidate : candidates) {
            String c = normalizeTitle(candidate);
            if (c.isEmpty()) {
                continue;
            }
            if (c.equals(queryNorm)) {
                bestTitleBonus = Math.max(bestTitleBonus, 12000.0);
            } else if (c.contains(queryNorm) && !queryNorm.isEmpty()) {
                bestTitleBonus = Math.max(bestTitleBonus, 4000.0);
            } else if (!queryNorm.isEmpty() && queryNorm.contains(c)) {
                bestTitleBonus = Math.max(bestTitleBonus, 2000.0);
            }
        }
        score += bestTitleBonus;

        if (secondaryQuery != null && !secondaryQuery.trim().isEmpty()) {
            String secondaryNorm = normalizeTitle(secondaryQuery.trim());
            if (!secondaryNorm.isEmpty() && !secondaryNorm.equals(queryNorm)) {
                double bestSecondary = 0;
                for (String candidate : candidates) {
                    String c = normalizeTitle(candidate);
                    if (c.equals(secondaryNorm)) {
                        bestSecondary = Math.max(bestSecondary, 8000.0);
                    } else if (c.contains(secondaryNorm)) {
                        bestSecondary = Math.max(bestSecondary, 2600.0);
                    } else if (secondaryNorm.contains(c)) {
                        bestSecondary = Math.max(bestSecondary, 1200.0);
                    }
                }
                score += bestSecondary;
            }
        }

        boolean querySequel = containsSequelSemantics(query) || containsSequelSemantics(queryNorm);
        boolean titleSequel = false;
        for (String candidate : candidates) {
            if (containsSequelSemantics(candidate) || containsSequelSemantics(normalizeTitle(candidate))) {
                titleSequel = true;
                break;
            }
        }
        if (!querySequel && titleSequel) {
            score -= 6000.0;
        }

        if (preferredMediaType != null && !preferredMediaType.isEmpty()) {
            if (item.mediaType.equals(preferredMediaType.toLowerCase(Locale.ROOT))) {
                score += 9000.0;
            } else {
                score -= 3500.0;
            }
        }

        String itemYear = item.releaseDate == null || item.releaseDate.length() < 4
                ? "" : item.releaseDate.substring(0, 4);
        if (item.mediaType.equals("tv") && preferredSeason > 0) {
            Integer seasonCount = tvSeasonCount(item.id);
            if (seasonCount != null) {
                score += seasonCount >= preferredSeason ? 4500.0 : -800.0;
            }
        }

        int queryTokens = tokenCount(query);
        if (queryTokens > 0 && queryTokens <= 2) {
            int longest = 0;
            boolean hasExact = false;
            for (String candidate : candidates) {
                String c = normalizeTitle(candidate);
                longest = Math.max(longest, tokenCount(candidate));
                if (c.equals(queryNorm)) {
                    hasExact = true;
                }
            }
            if (!hasExact && longest >= 4) {
                score -= 2200.0;
            }
        }

        if (year != null && !year.isEmpty() && itemYear.length() == 4) {
            try {
                int target = Integer.parseInt(year);
                int itemValue = Integer.parseInt(itemYear);
                int diff = Math.abs(itemValue - target);
                if (diff == 0) {
                    score += 10000.0;
                } else if (diff == 1) {
                    score += 5000.0;
                } else if (diff >= 10) {
                    score -= 20000.0;
                } else if (diff >= 3) {
                    score -= 12000.0;
                    if (item.mediaType.equals("tv") && preferredSeason > 0) {
                        score -= 2500.0;
                    }
                }
            } catch (NumberFormatException ignored) {
            }
        }
        return score;
    }

    private Integer tvSeasonCount(int tvId) {
        Integer cached = seasonCountCache.get(tvId);
        if (cached != null) {
            return cached;
        }
        try {
            String url = apiBase() + "/tv/" + tvId + "?language=en-US"
                    + apiKeyQuery();
            JSONObject root = get(url);
            Integer count = root == null ? null : root.optInt("number_of_seasons", 0);
            if (count != null) {
                seasonCountCache.put(tvId, count);
            }
            return count;
        } catch (Exception ignored) {
            return null;
        }
    }

    private final java.util.Map<Integer, Integer> seasonCountCache = new java.util.concurrent.ConcurrentHashMap<>();

    // ------------------------------------------------- detail / credits / episodes

    /** Title detail with genres and credits, from {@code append_to_response=credits}. */
    public static final class Details {
        public final List<String> genres = new ArrayList<>();
        public final List<String> directors = new ArrayList<>();
        public final List<String> casts = new ArrayList<>();
        public String overview;
        public String releaseDate;
    }

    /** TMDB episode lookup: name, overview, air date and still URL (w500). */
    public static final class EpisodeDetail {
        public final boolean tmdbAnswered;
        public final String title;
        public final String overview;
        public final String airDate;
        public final String stillUrl;

        EpisodeDetail(boolean tmdbAnswered, String title, String overview, String airDate, String stillUrl) {
            this.tmdbAnswered = tmdbAnswered;
            this.title = title;
            this.overview = overview;
            this.airDate = airDate;
            this.stillUrl = stillUrl;
        }

        static EpisodeDetail miss(boolean tmdbAnswered) {
            return new EpisodeDetail(tmdbAnswered, null, null, null, null);
        }
    }

    /** One season summary used for the still-image season mapping. */
    public static final class SeasonSummary {
        public final int seasonNumber;
        public final int episodeCount;

        SeasonSummary(int seasonNumber, int episodeCount) {
            this.seasonNumber = seasonNumber;
            this.episodeCount = episodeCount;
        }
    }

    /** Fetches genres / directors / casts for a scraped title. */
    public Details details(String mediaType, int tmdbId) {
        String kind = "tv".equals(mediaType) ? "tv" : "movie";
        try {
            JSONObject root = get(apiBase() + "/" + kind + "/" + tmdbId
                    + "?language=" + language() + "&append_to_response=credits"
                    + apiKeyQuery());
            if (root == null) {
                return null;
            }
            Details details = new Details();
            details.overview = root.optString("overview", "");
            details.releaseDate = root.optString("release_date", root.optString("first_air_date", ""));
            JSONArray genres = root.optJSONArray("genres");
            if (genres != null) {
                for (int i = 0; i < genres.length(); i++) {
                    String name = genres.optJSONObject(i) == null
                            ? null : genres.optJSONObject(i).optString("name", "");
                    if (!name.isEmpty()) {
                        details.genres.add(name);
                    }
                }
            }
            JSONObject credits = root.optJSONObject("credits");
            if (credits != null) {
                JSONArray cast = credits.optJSONArray("cast");
                if (cast != null) {
                    for (int i = 0; i < cast.length() && details.casts.size() < 12; i++) {
                        String name = cast.optJSONObject(i) == null
                                ? null : cast.optJSONObject(i).optString("name", "");
                        if (!name.isEmpty()) {
                            details.casts.add(name);
                        }
                    }
                }
                JSONArray crew = credits.optJSONArray("crew");
                if (crew != null) {
                    for (int i = 0; i < crew.length() && details.directors.size() < 6; i++) {
                        JSONObject member = crew.optJSONObject(i);
                        if (member == null) {
                            continue;
                        }
                        String job = member.optString("job", "");
                        String department = member.optString("department", "");
                        // mac parity: movie directors from crew job "Director", series from "Creator".
                        boolean isDirector = "Director".equalsIgnoreCase(job)
                                || "Creator".equalsIgnoreCase(job)
                                || "Series Director".equalsIgnoreCase(job);
                        String name = member.optString("name", "");
                        if (isDirector && !name.isEmpty() && !details.directors.contains(name)) {
                            details.directors.add(name);
                        }
                    }
                }
            }
            return details;
        } catch (Exception ignored) {
            return null;
        }
    }

    /** Season list of a series (season number + episode count), used for still mapping. */
    public List<SeasonSummary> seasonSummaries(int tvId) {
        List<SeasonSummary> summaries = new ArrayList<>();
        try {
            JSONObject root = get(apiBase() + "/tv/" + tvId + "?language=en-US"
                    + apiKeyQuery());
            JSONArray seasons = root == null ? null : root.optJSONArray("seasons");
            if (seasons == null) {
                return summaries;
            }
            for (int i = 0; i < seasons.length(); i++) {
                JSONObject item = seasons.optJSONObject(i);
                if (item == null) {
                    continue;
                }
                int number = item.optInt("season_number", -1);
                int count = item.optInt("episode_count", 0);
                if (number >= 0 && count > 0) {
                    summaries.add(new SeasonSummary(number, count));
                }
            }
        } catch (Exception ignored) {
        }
        return summaries;
    }

    /**
     * Episode lookup ported from the mac ThumbnailManager: local season/episode numbers stay
     * as they are, but the TMDB request falls back to another season that can hold the episode
     * (anime releases often split one long TMDB season into several local seasons).
     */
    public EpisodeDetail episodeDetail(int tvId, int season, int episode) {
        List<int[]> candidates = episodeStillCandidates(tvId, season, episode);
        boolean sawResponse = false;
        for (int[] candidate : candidates) {
            EpisodeDetail detail = fetchEpisodeDetail(tvId, candidate[0], candidate[1]);
            if (detail != null) {
                return detail;
            }
            sawResponse = true;
        }
        return EpisodeDetail.miss(sawResponse);
    }

    private List<int[]> episodeStillCandidates(int tvId, int season, int episode) {
        List<int[]> candidates = new ArrayList<>();
        candidates.add(new int[]{season, episode});
        if (episode <= 0) {
            return candidates;
        }
        List<SeasonSummary> regular = new ArrayList<>();
        for (SeasonSummary summary : seasonSummaries(tvId)) {
            if (summary.seasonNumber > 0 && summary.episodeCount > 0) {
                regular.add(summary);
            }
        }
        regular.sort((a, b) -> a.seasonNumber != b.seasonNumber
                ? Integer.compare(a.seasonNumber, b.seasonNumber)
                : Integer.compare(b.episodeCount, a.episodeCount));

        // A series that only keeps one regular season: map onto it when it can hold the episode.
        if (regular.size() == 1 && regular.get(0).seasonNumber != season
                && regular.get(0).episodeCount >= episode) {
            addCandidate(candidates, regular.get(0).seasonNumber, episode);
        }
        for (SeasonSummary summary : regular) {
            if (summary.seasonNumber == 1 && summary.episodeCount >= episode) {
                addCandidate(candidates, 1, episode);
                break;
            }
        }
        List<SeasonSummary> bySize = new ArrayList<>(regular);
        bySize.sort((a, b) -> a.episodeCount != b.episodeCount
                ? Integer.compare(b.episodeCount, a.episodeCount)
                : Integer.compare(a.seasonNumber, b.seasonNumber));
        for (SeasonSummary summary : bySize) {
            if (summary.episodeCount >= episode) {
                addCandidate(candidates, summary.seasonNumber, episode);
            }
        }
        return candidates;
    }

    private static void addCandidate(List<int[]> candidates, int season, int episode) {
        for (int[] candidate : candidates) {
            if (candidate[0] == season && candidate[1] == episode) {
                return;
            }
        }
        candidates.add(new int[]{season, episode});
    }

    /** Returns null when TMDB did not answer (network/5xx); a miss when it answered without a still. */
    private EpisodeDetail fetchEpisodeDetail(int tvId, int season, int episode) {
        int[] status = new int[1];
        JSONObject root;
        try {
            root = getOrNull(apiBase() + "/tv/" + tvId + "/season/" + season
                    + "/episode/" + episode + "?language=" + language()
                    + apiKeyQuery(), status);
        } catch (IOException error) {
            return null;
        }
        if (root == null) {
            // 404: TMDB has no such episode, retrying other candidates is still useful.
            return status[0] == 404 ? EpisodeDetail.miss(true) : null;
        }
        String stillPath = root.optString("still_path", "");
        String stillUrl = stillPath.isEmpty()
                ? fallbackStillPath(tvId, season, episode)
                : stillImage(stillPath);
        return new EpisodeDetail(true,
                root.optString("name", ""),
                root.optString("overview", ""),
                root.optString("air_date", ""),
                stillUrl == null || stillUrl.isEmpty() ? null : stillUrl);
    }

    private static String stillImage(String stillPath) {
        if (stillPath.startsWith("http://") || stillPath.startsWith("https://")) {
            return stillPath;
        }
        return "https://image.tmdb.org/t/p/w500" + stillPath;
    }

    /** /images fallback: highest scored still, weighted by vote count (mac behaviour). */
    private String fallbackStillPath(int tvId, int season, int episode) {
        int[] status = new int[1];
        JSONObject root;
        try {
            root = getOrNull(apiBase() + "/tv/" + tvId + "/season/" + season
                    + "/episode/" + episode + "/images?include_image_language=" + imageLanguage()
                    + apiKeyQuery(), status);
        } catch (IOException error) {
            return null;
        }
        JSONArray stills = root == null ? null : root.optJSONArray("stills");
        if (stills == null || stills.length() == 0) {
            return null;
        }
        String best = null;
        double bestScore = Double.NEGATIVE_INFINITY;
        for (int i = 0; i < stills.length(); i++) {
            JSONObject item = stills.optJSONObject(i);
            if (item == null) {
                continue;
            }
            double score = item.optDouble("vote_average", 0) + item.optInt("vote_count", 0) * 0.1;
            String path = item.optString("file_path", "");
            if (!path.isEmpty() && score > bestScore) {
                bestScore = score;
                best = path;
            }
        }
        return best == null ? null : stillImage(best);
    }

    private List<String> matchableTitles(Result item) {
        List<String> titles = new ArrayList<>();
        addTitle(titles, item.title);
        addTitle(titles, item.originalTitle);
        return titles;
    }

    private static void addTitle(List<String> titles, String value) {
        if (value != null && !value.trim().isEmpty()) {
            titles.add(value.trim());
        }
    }

    private static String normalizeTitle(String input) {
        if (input == null) {
            return "";
        }
        return input.toLowerCase(Locale.ROOT).replaceAll("[\\s._\\-]+", " ").trim();
    }

    private static int tokenCount(String input) {
        if (input == null) {
            return 0;
        }
        String trimmed = input.trim();
        return trimmed.isEmpty() ? 0 : trimmed.split("\\s+").length;
    }

    private static boolean containsSequelSemantics(String text) {
        if (text == null) {
            return false;
        }
        String lower = text.toLowerCase(Locale.ROOT);
        return lower.contains("续集") || lower.contains("续") || lower.contains("sequel")
                || lower.contains(" part ") || lower.contains("2: ") || lower.contains(" reloaded");
    }

    private void waitForRateLimit() throws InterruptedException {
        long intervalMillis = 313;
        synchronized (rateLock) {
            long now = System.currentTimeMillis();
            if (now < nextAllowedMillis) {
                Thread.sleep(nextAllowedMillis - now);
            }
            nextAllowedMillis = System.currentTimeMillis() + intervalMillis;
        }
    }

    private JSONObject get(String url) throws IOException {
        int[] status = new int[1];
        JSONObject root = getOrNull(url, status);
        if (root == null) {
            throw new IOException("TMDB 请求失败：" + status[0]);
        }
        return root;
    }

    /** GET that reports the HTTP status instead of throwing, for 404-aware episode lookups. */
    private JSONObject getOrNull(String url, int[] statusOut) throws IOException {
        for (int attempt = 0; attempt < 3; attempt++) {
            waitForRateLimitQuietly();
            HttpURLConnection connection = null;
            try {
                connection = (HttpURLConnection) new URL(url).openConnection();
                connection.setConnectTimeout(12000);
                connection.setReadTimeout(20000);
                connection.setRequestProperty("Accept", "application/json");
                connection.setRequestProperty("User-Agent", "OmniPlay-Android/1.16");
                if (usesBearerToken()) {
                    // v4 Read Access Token 只认请求头，查询里塞 api_key 会被当成无效钥匙。
                    connection.setRequestProperty("Authorization", "Bearer " + effectiveApiKey());
                }
                int status = connection.getResponseCode();
                if (statusOut != null) {
                    statusOut[0] = status;
                }
                if (status == 429 || status == 503) {
                    if (attempt < 2) {
                        sleepQuiet(1200L * (attempt + 1));
                        continue;
                    }
                    return null;
                }
                if (status < 200 || status >= 300) {
                    return null;
                }
                try {
                    return new JSONObject(readFully(connection.getInputStream()));
                } catch (org.json.JSONException jsonError) {
                    if (statusOut != null) {
                        statusOut[0] = -1;
                    }
                    return null;
                }
            } finally {
                if (connection != null) {
                    connection.disconnect();
                }
            }
        }
        return null;
    }

    private void waitForRateLimitQuietly() {
        try {
            waitForRateLimit();
        } catch (InterruptedException interrupted) {
            Thread.currentThread().interrupt();
        }
    }

    private static void sleepQuiet(long millis) {
        try {
            Thread.sleep(millis);
        } catch (InterruptedException interrupted) {
            Thread.currentThread().interrupt();
        }
    }

    private static String readFully(InputStream input) throws IOException {
        java.io.ByteArrayOutputStream buffer = new java.io.ByteArrayOutputStream();
        byte[] chunk = new byte[16384];
        int read;
        while ((read = input.read(chunk)) >= 0) {
            buffer.write(chunk, 0, read);
        }
        return new String(buffer.toByteArray(), StandardCharsets.UTF_8);
    }

    private final AtomicLong unusedCounter = new AtomicLong();
}
