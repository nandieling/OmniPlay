package com.omniplay.tv.local;

import android.content.Context;
import android.content.SharedPreferences;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;
import java.nio.charset.Charset;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * 豆瓣条目抓取（mac 版 DoubanService 的对等实现）。
 *
 * <p>豆瓣没有给普通用户可用的开放接口，mac 版的做法是直接抓移动版条目页
 * {@code m.douban.com/movie/subject/NNN/}，优先读页面里那段 JSON-LD，读不到再退回一组
 * 正则。这里照同一套做法，并把「抓到验证页就暂停 12 小时」这条保护也一起搬过来：
 * 电视上重试没有意义，抓不到就是抓不到。</p>
 */
public final class DoubanClient {
    public static final String PREFS = "omniplay_tv_local";
    private static final String KEY_COOLDOWN_UNTIL = "douban_cooldown_until";

    /** 两次抓取之间的最小间隔（豆瓣的风控对连续请求很敏感）。 */
    private static final long MIN_INTERVAL_MS = 12_000L;
    /** 抓到验证页 / 403 / 429 之后暂停多久。 */
    private static final long COOLDOWN_MS = 12 * 60 * 60 * 1000L;

    private static final Pattern SUBJECT_URL = Pattern.compile(
            "https?://(?:movie\\.douban\\.com/subject|m\\.douban\\.com/movie/subject)/(\\d+)/?");
    private static final Pattern JSON_LD = Pattern.compile(
            "<script[^>]+type=[\"']application/ld\\+json[\"'][^>]*>(.*?)</script>",
            Pattern.CASE_INSENSITIVE | Pattern.DOTALL);
    private static final Pattern TAGS = Pattern.compile("<[^>]+>");
    private static final Pattern OG_TITLE = Pattern.compile(
            "<meta[^>]+property=[\"']og:title[\"'][^>]+content=[\"']([^\"']+)[\"']",
            Pattern.CASE_INSENSITIVE | Pattern.DOTALL);
    private static final Pattern OG_TITLE_FIRST = Pattern.compile(
            "<meta[^>]+content=[\"']([^\"']+)[\"'][^>]+property=[\"']og:title[\"']",
            Pattern.CASE_INSENSITIVE | Pattern.DOTALL);
    private static final Pattern ITEM_REVIEWED = Pattern.compile(
            "<span[^>]+property=[\"']v:itemreviewed[\"'][^>]*>(.*?)</span>",
            Pattern.CASE_INSENSITIVE | Pattern.DOTALL);
    private static final Pattern PAGE_TITLE = Pattern.compile(
            "<title>(.*?)</title>", Pattern.CASE_INSENSITIVE | Pattern.DOTALL);
    /** 移动版页面把片名单独放在这一行里，比 {@code <title>} 干净（后者会拖「 - 电影」）。 */
    private static final Pattern SUB_TITLE = Pattern.compile(
            "<div[^>]+class=[\"']sub-title[\"'][^>]*>(.*?)</div>",
            Pattern.CASE_INSENSITIVE | Pattern.DOTALL);
    /** 页面标题尾巴上那条栏目名：「未麻的部屋 - 电影」里的「- 电影」。 */
    private static final Pattern PAGE_SUFFIX = Pattern.compile(
            "\\s*[-–]\\s*(?:电影|电视剧|综艺|动漫|动画|纪录片|短片|真人秀|美剧|日剧|英剧|港剧|韩剧)$");
    private static final Pattern SUMMARY = Pattern.compile(
            "<span[^>]+property=[\"']v:summary[\"'][^>]*>(.*?)</span>",
            Pattern.CASE_INSENSITIVE | Pattern.DOTALL);
    private static final Pattern META_DESCRIPTION = Pattern.compile(
            "<meta[^>]+(?:name|property)=[\"'](?:description|og:description)[\"'][^>]+content=[\"']([^\"']+)[\"']",
            Pattern.CASE_INSENSITIVE | Pattern.DOTALL);
    private static final Pattern GENRE = Pattern.compile(
            "<span[^>]+property=[\"']v:genre[\"'][^>]*>(.*?)</span>",
            Pattern.CASE_INSENSITIVE | Pattern.DOTALL);
    private static final Pattern ORIGINAL_TITLE = Pattern.compile(
            "<div[^>]+class=[\"']sub-original-title[\"'][^>]*>(.*?)</div>",
            Pattern.CASE_INSENSITIVE | Pattern.DOTALL);
    private static final Pattern META_BLOCK = Pattern.compile(
            "<div[^>]+class=[\"']sub-meta[\"'][^>]*>(.*?)</div>",
            Pattern.CASE_INSENSITIVE | Pattern.DOTALL);
    private static final Pattern YEAR = Pattern.compile("(19|20)\\d{2}");
    private static final Pattern YEAR_IN_TITLE = Pattern.compile("\\(((?:19|20)\\d{2})\\)");
    // 移动版页面的「Perfect Blue（1997）」用的是全角括号，两种都得剥。
    private static final Pattern YEAR_SUFFIX = Pattern.compile(
            "\\s*[\\(\\[\\{（]\\s*(?:19|20)\\d{2}\\s*[\\)\\]\\}）]\\s*$");
    private static final Pattern YEAR_SPAN = Pattern.compile(
            "<span[^>]+class=[\"']year[\"'][^>]*>\\s*\\(((?:19|20)\\d{2})\\)\\s*</span>",
            Pattern.CASE_INSENSITIVE | Pattern.DOTALL);
    private static final Pattern ALT_NAME = Pattern.compile(
            "<span[^>]+property=[\"']v:alt-name[\"'][^>]*>(.*?)</span>",
            Pattern.CASE_INSENSITIVE | Pattern.DOTALL);
    private static final Pattern INFO_LABEL = Pattern.compile(
            "<span class=\"pl\">\\s*([^<]{1,24}):</span>(.*?)<br",
            Pattern.CASE_INSENSITIVE | Pattern.DOTALL);
    private static final Pattern LINK_NAME = Pattern.compile(
            "<a[^>]*>(.*?)</a>", Pattern.CASE_INSENSITIVE | Pattern.DOTALL);

    private static final String[] RATING_PATTERNS = {
            "<strong[^>]+property=[\"']v:average[\"'][^>]*>(.*?)</strong>",
            "<strong[^>]+class=[\"'][^\"']*rating_num[^\"']*[\"'][^>]*>(.*?)</strong>",
            "<span[^>]+class=[\"'][^\"']*rating_num[^\"']*[\"'][^>]*>(.*?)</span>",
            "<meta[^>]+itemprop=[\"']ratingValue[\"'][^>]+content=[\"']([0-9]+(?:\\.[0-9]+)?)[\"']",
            "\"ratingValue\"\\s*:\\s*\"?([0-9]+(?:\\.[0-9]+)?)",
            "\"rating\"\\s*:\\s*\\{[^}]*?\"value\"\\s*:\\s*\"?([0-9]+(?:\\.[0-9]+)?)",
            "<span[^>]+class=[\"'][^\"']*rating[^\"']*[\"'][^>]*>\\s*([0-9]+(?:\\.[0-9]+)?)\\s*</span>",
    };
    private static final String[] RATING_COUNT_PATTERNS = {
            "<span[^>]+property=[\"']v:votes[\"'][^>]*>(.*?)</span>",
            "<meta[^>]+itemprop=[\"']reviewCount[\"'][^>]+content=[\"']([0-9,]+)[\"']",
            "([0-9,]+)\\s*人评价",
            "([0-9,]+)\\s*人评分",
            "\"ratingCount\"\\s*:\\s*\"?([0-9,]+)",
    };
    private static final String[] POSTER_PATTERNS = {
            "<a[^>]+class=[\"']sub-cover[\"'][^>]*>.*?<img[^>]+src=[\"']([^\"']+)[\"']",
            "<img[^>]+rel=[\"']v:image[\"'][^>]+src=[\"']([^\"']+)[\"']",
            "<meta[^>]+itemprop=[\"']image[\"'][^>]+content=[\"']([^\"']+)[\"']",
            "<meta[^>]+property=[\"']og:image[\"'][^>]+content=[\"']([^\"']+)[\"']",
    };

    /** 抓移动版条目页用的 UA。 */
    private static final String MOBILE_AGENT = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like"
            + " Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0"
            + " Mobile/15E148 Safari/604.1";
    /** 搜索页只在桌面 UA 下吐数据，移动版 UA 会被推一份空列表。 */
    private static final String DESKTOP_AGENT = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)"
            + " AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Safari/605.1.15";

    private static final Set<String> KNOWN_COUNTRIES = new SetOf(
            "中国大陆", "中国香港", "中国台湾", "美国", "英国", "法国", "德国", "意大利",
            "日本", "韩国", "印度", "加拿大", "澳大利亚", "西班牙", "俄罗斯", "泰国");
    private static final Set<String> KNOWN_GENRES = new SetOf(
            "剧情", "喜剧", "动作", "爱情", "科幻", "动画", "悬疑", "惊悚", "恐怖", "犯罪",
            "同性", "音乐", "歌舞", "传记", "历史", "战争", "西部", "奇幻", "冒险", "灾难",
            "武侠", "古装", "运动", "家庭", "纪录片", "短片");

    private final SharedPreferences preferences;
    private long nextAllowedMillis;
    /** 从豆瓣首页领到的匿名 cookie，搜索页要用；领一次存着复用。 */
    private final Map<String, String> cookieStore = new LinkedHashMap<>();
    private volatile String searchCookie;
    private volatile boolean cookieTried;

    public DoubanClient(Context context) {
        preferences = context.getApplicationContext()
                .getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    /** 抓到的一个豆瓣条目。列表已经拼成「A / B」这种一行文本，界面直接摆。 */
    public static final class Subject {
        public final String subjectId;
        public final String subjectUrl;
        public final String title;
        public final String originalTitle;
        public final String year;
        public final double rating;
        public final int ratingCount;
        public final String summary;
        public final List<String> genres;
        public final List<String> countries;
        public final List<String> directors;
        public final List<String> casts;
        public final String posterUrl;

        Subject(String subjectId, String subjectUrl, String title, String originalTitle,
                String year, double rating, int ratingCount, String summary,
                List<String> genres, List<String> countries, List<String> directors,
                List<String> casts, String posterUrl) {
            this.subjectId = subjectId;
            this.subjectUrl = subjectUrl;
            this.title = title;
            this.originalTitle = originalTitle == null ? "" : originalTitle;
            this.year = year == null ? "" : year;
            this.rating = rating;
            this.ratingCount = ratingCount;
            this.summary = summary == null ? "" : summary;
            this.genres = genres;
            this.countries = countries;
            this.directors = directors;
            this.casts = casts;
            this.posterUrl = posterUrl == null ? "" : posterUrl;
        }

        /** 抓到的是豆瓣的壳页面（没有评分、标题就是「豆瓣」）：当成没抓到。 */
        public boolean isPlaceholder() {
            String normalized = title.trim();
            return rating <= 0 && ("豆瓣".equals(normalized) || "豆瓣电影".equals(normalized));
        }
    }

    /** 从用户输入里挑出条目 id：完整链接、移动版链接，或者光一串数字。 */
    public static String subjectIdFrom(String input) {
        String trimmed = input == null ? "" : input.trim();
        Matcher url = SUBJECT_URL.matcher(trimmed);
        if (url.find()) {
            return url.group(1);
        }
        StringBuilder digits = new StringBuilder();
        for (int index = 0; index < trimmed.length(); index++) {
            char c = trimmed.charAt(index);
            if (c >= '0' && c <= '9') {
                digits.append(c);
            } else if (!Character.isWhitespace(c)) {
                return null;
            }
        }
        return digits.length() >= 5 ? digits.toString() : null;
    }

    /** 暂停到什么时候（0 表示没在暂停）：界面上好把「过会儿再试」说清楚。 */
    public long cooldownUntilMillis() {
        return preferences.getLong(KEY_COOLDOWN_UNTIL, 0L);
    }

    public Subject fetch(String input) throws IOException {
        String subjectId = subjectIdFrom(input);
        if (subjectId == null) {
            throw new IOException("请输入豆瓣电影条目链接或条目编号。");
        }
        waitForTurn();
        String html = request("https://m.douban.com/movie/subject/" + subjectId + "/",
                "https://m.douban.com/movie/", MOBILE_AGENT, null, true);
        return parse(html, subjectId);
    }

    /**
     * 按关键词搜豆瓣条目。豆瓣的搜索页要先有一副匿名 cookie 才吐数据，所以这里先去首页
     * 领一份；页面里那段 window.__DATA__ 就是结果清单。
     *
     * <p>搜索被风控挡下时只报一次错，不会把整个豆瓣功能锁 12 小时——搜不到条目跟抓不到
     * 详情是两码事，用户换个链接还能绑。</p>
     */
    public List<Hit> search(String keyword, int limit) throws IOException {
        String needle = keyword == null ? "" : keyword.trim();
        if (needle.isEmpty()) {
            throw new IOException("请先填写影视名称关键词。");
        }
        waitForTurn();
        String html = request("https://search.douban.com/movie/subject_search?search_text="
                        + URLEncoder.encode(needle, "UTF-8") + "&cat=1002",
                "https://www.douban.com/", DESKTOP_AGENT, cookieHeader(), false);
        if (!html.contains("__DATA__")) {
            throw new IOException("豆瓣搜索被挡下了，换个关键词或直接贴条目链接再试。");
        }
        return parseSearch(html, Math.max(1, limit));
    }

    /** 排队：风控暂停期间直接报错，否则跟上一次豆瓣请求之间至少隔 MIN_INTERVAL_MS。 */
    private void waitForTurn() throws IOException {
        long now = System.currentTimeMillis();
        if (now < cooldownUntilMillis()) {
            throw new IOException("豆瓣请求已暂停（可能被风控），请稍后再试。");
        }
        synchronized (this) {
            if (now < nextAllowedMillis) {
                try {
                    Thread.sleep(nextAllowedMillis - now);
                } catch (InterruptedException interrupted) {
                    Thread.currentThread().interrupt();
                    throw new IOException("豆瓣抓取已取消。");
                }
            }
            nextAllowedMillis = System.currentTimeMillis() + MIN_INTERVAL_MS
                    + (long) (Math.random() * 5000L);
        }
    }

    /** 一副能用的匿名 cookie；领不到就返回空串，让请求裸奔一次也好过不做。 */
    private String cookieHeader() {
        if (cookieTried) {
            return searchCookie == null ? "" : searchCookie;
        }
        String header = "";
        try {
            request("https://www.douban.com/", "https://www.douban.com/", DESKTOP_AGENT, null, false);
            StringBuilder built = new StringBuilder();
            synchronized (cookieStore) {
                for (Map.Entry<String, String> entry : cookieStore.entrySet()) {
                    if (built.length() > 0) {
                        built.append("; ");
                    }
                    built.append(entry.getKey()).append('=').append(entry.getValue());
                }
            }
            header = built.toString();
        } catch (IOException ignored) {
            // 首页没领到 cookie：搜索大概率会空，交给上层那句「被挡下了」说明。
        }
        searchCookie = header;
        cookieTried = true;
        return header;
    }

    private String request(String location, String referer, String agent, String cookie,
            boolean checkGuards) throws IOException {
        HttpURLConnection connection = (HttpURLConnection) new URL(location).openConnection();
        connection.setConnectTimeout(12000);
        connection.setReadTimeout(15000);
        connection.setInstanceFollowRedirects(true);
        connection.setRequestProperty("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        connection.setRequestProperty("Accept-Language", "zh-CN,zh-Hans;q=0.9,en;q=0.8");
        connection.setRequestProperty("Referer", referer);
        connection.setRequestProperty("User-Agent", agent);
        if (cookie != null && !cookie.isEmpty()) {
            connection.setRequestProperty("Cookie", cookie);
        }
        int status;
        byte[] body;
        String contentType;
        try {
            status = connection.getResponseCode();
            contentType = connection.getContentType() == null ? "" : connection.getContentType();
            collectCookies(connection);
            InputStream input = status >= 400 ? connection.getErrorStream()
                    : connection.getInputStream();
            body = input == null ? new byte[0] : readAll(input);
        } finally {
            connection.disconnect();
        }
        if (status == 403 || status == 429) {
            pause();
            throw new IOException("豆瓣返回 " + status + "，已暂停豆瓣请求 12 小时以避免继续触发风控。");
        }
        if (status != 200) {
            throw new IOException("豆瓣返回 HTTP " + status + "。");
        }
        String html = decode(body, contentType);
        if (checkGuards && (html.contains("检测到有异常请求") || html.contains("sec.douban.com")
                || html.contains("请输入验证码"))) {
            pause();
            throw new IOException("豆瓣返回了验证页面，已暂停豆瓣请求 12 小时。");
        }
        return html;
    }

    private void collectCookies(HttpURLConnection connection) {
        Map<String, List<String>> fields = connection.getHeaderFields();
        if (fields == null) {
            return;
        }
        for (Map.Entry<String, List<String>> entry : fields.entrySet()) {
            if (entry.getKey() == null || !"set-cookie".equalsIgnoreCase(entry.getKey())) {
                continue;
            }
            for (String raw : entry.getValue()) {
                int semi = raw.indexOf(';');
                String pair = semi < 0 ? raw : raw.substring(0, semi);
                int equals = pair.indexOf('=');
                if (equals <= 0) {
                    continue;
                }
                String name = pair.substring(0, equals).trim();
                String value = pair.substring(equals + 1).trim();
                if (!name.isEmpty() && !value.isEmpty()) {
                    synchronized (cookieStore) {
                        cookieStore.put(name, value);
                    }
                }
            }
        }
    }

    private void pause() {
        preferences.edit()
                .putLong(KEY_COOLDOWN_UNTIL, System.currentTimeMillis() + COOLDOWN_MS).apply();
    }

    /** 关键词搜索出来的一条候选：够摆进候选清单，点中之后再用它去抓完整条目。 */
    public static final class Hit {
        public final String subjectId;
        public final String subjectUrl;
        public final String title;
        public final String year;
        public final String mediaType;
        public final double rating;
        public final int ratingCount;
        public final String summary;
        public final String posterUrl;

        Hit(String subjectId, String subjectUrl, String title, String year, String mediaType,
                double rating, int ratingCount, String summary, String posterUrl) {
            this.subjectId = subjectId;
            this.subjectUrl = subjectUrl;
            this.title = title;
            this.year = year == null ? "" : year;
            this.mediaType = mediaType;
            this.rating = rating;
            this.ratingCount = ratingCount;
            this.summary = summary == null ? "" : summary;
            this.posterUrl = posterUrl == null ? "" : posterUrl;
        }
    }

    static List<Hit> parseSearch(String html, int limit) {
        List<Hit> hits = new ArrayList<>();
        String block = searchDataBlock(html);
        if (block == null) {
            return hits;
        }
        Object parsed;
        try {
            parsed = MiniJson.read(block);
        } catch (RuntimeException broken) {
            return hits;
        }
        Object items = parsed instanceof Map ? ((Map<?, ?>) parsed).get("items") : null;
        if (!(items instanceof List)) {
            return hits;
        }
        for (Object entry : (List<?>) items) {
            if (hits.size() >= limit) {
                break;
            }
            if (!(entry instanceof Map)) {
                continue;
            }
            Map<?, ?> item = (Map<?, ?>) entry;
            String subjectId = text(item.get("id"));
            if (subjectId.isEmpty() || "0".equals(subjectId)) {
                continue;
            }
            // 标题里那个 \u200e 是豆瓣塞的方向控制符，留着会在界面上多出一个怪空格。
            String rawTitle = collapseSpace(text(item.get("title")))
                    .replace('\u200e', ' ').replace('\u200f', ' ').trim();
            String year = "";
            Matcher inTitle = YEAR_IN_TITLE.matcher(rawTitle);
            if (inTitle.find()) {
                year = inTitle.group(1);
            }
            String title = YEAR_SUFFIX.matcher(rawTitle).replaceAll("").trim();
            if (title.isEmpty()) {
                continue;
            }
            boolean series = false;
            if (item.get("labels") instanceof List) {
                for (Object label : (List<?>) item.get("labels")) {
                    if (label instanceof Map
                            && "剧集".equals(text(((Map<?, ?>) label).get("text")))) {
                        series = true;
                    }
                }
            }
            double rating = 0;
            int ratingCount = 0;
            if (item.get("rating") instanceof Map) {
                Map<?, ?> value = (Map<?, ?>) item.get("rating");
                rating = number(value.get("value"));
                ratingCount = (int) number(value.get("count"));
            }
            hits.add(new Hit(subjectId,
                    firstNonEmpty(text(item.get("url")),
                            "https://movie.douban.com/subject/" + subjectId + "/"),
                    title, year, series ? "tv" : "movie", rating, ratingCount,
                    stripTags(decodeEntities(text(item.get("abstract")))),
                    usablePoster(text(item.get("cover_url")))));
        }
        return hits;
    }

    private static String text(Object value) {
        return value == null ? "" : String.valueOf(value).trim();
    }

    private static double number(Object value) {
        if (value instanceof Number) {
            return ((Number) value).doubleValue();
        }
        return toDouble(String.valueOf(value));
    }

    /** 从页面里抠出 {@code window.__DATA__ = {...}} 那段 JSON：按大括号配对，字符串里的不算。 */
    static String searchDataBlock(String html) {
        for (int marker = html.indexOf("__DATA__"); marker >= 0;
                marker = html.indexOf("__DATA__", marker + 8)) {
            int open = html.indexOf('{', marker);
            if (open < 0) {
                return null;
            }
            int depth = 0;
            boolean inString = false;
            boolean escaped = false;
            for (int index = open; index < html.length(); index++) {
                char c = html.charAt(index);
                if (inString) {
                    if (escaped) {
                        escaped = false;
                    } else if (c == '\\') {
                        escaped = true;
                    } else if (c == '"') {
                        inString = false;
                    }
                    continue;
                }
                if (c == '"') {
                    inString = true;
                } else if (c == '{') {
                    depth++;
                } else if (c == '}' && --depth == 0) {
                    return html.substring(open, index + 1);
                }
            }
        }
        return null;
    }

    private static String usablePoster(String value) {
        String candidate = normalizePoster(value);
        return isUsablePosterUrl(candidate) ? candidate : "";
    }

    // ------------------------------------------------------------------ 解析

    static Subject parse(String html, String subjectId) throws IOException {
        JSONObject jsonLd = findMovieJsonLd(html);

        String title = firstNonEmpty(
                jsonLdText(jsonLd, "name"),
                // v:itemreviewed 是豆瓣自己标的片名，不带年份；og:title 会拖一个「(2006)」尾巴。
                strip(ITEM_REVIEWED, html),
                // 现在这版移动版页面这两样都没有，片名在 sub-title 这一行里。
                strip(SUB_TITLE, html),
                strip(OG_TITLE, html), strip(OG_TITLE_FIRST, html),
                strip(PAGE_TITLE, html).replace("(豆瓣)", "").replace("- 豆瓣", ""));
        // 页面标题里那个年份已经在 year 字段里单独存了一份，片名后面不留；栏目名同理。
        title = YEAR_SUFFIX.matcher(title).replaceAll("").trim();
        title = PAGE_SUFFIX.matcher(title).replaceAll("").trim();
        title = YEAR_SUFFIX.matcher(title).replaceAll("").trim();
        if (title.isEmpty()) {
            throw new IOException("未能从豆瓣页面解析到有效影视信息。");
        }

        double rating = 0;
        int ratingCount = 0;
        JSONObject aggregate = jsonLd == null ? null : jsonLd.optJSONObject("aggregateRating");
        if (aggregate != null) {
            rating = aggregate.optDouble("ratingValue", aggregate.optDouble("rating", 0));
            ratingCount = aggregate.optInt("ratingCount", aggregate.optInt("reviewCount", 0));
        }
        if (rating <= 0) {
            for (String pattern : RATING_PATTERNS) {
                double value = toDouble(strip(Pattern.compile(pattern, Pattern.CASE_INSENSITIVE
                        | Pattern.DOTALL), html));
                if (value > 0) {
                    rating = value;
                    break;
                }
            }
        }
        if (ratingCount <= 0) {
            for (String pattern : RATING_COUNT_PATTERNS) {
                int value = toInt(strip(Pattern.compile(pattern, Pattern.CASE_INSENSITIVE
                        | Pattern.DOTALL), html));
                if (value > 0) {
                    ratingCount = value;
                    break;
                }
            }
        }

        String summary = firstNonEmpty(
                jsonLdText(jsonLd, "description"),
                strip(SUMMARY, html),
                strip(META_DESCRIPTION, html));

        List<String> genres = jsonTextList(jsonLd == null ? null : jsonLd.opt("genre"));
        genres.addAll(all(GENRE, html));
        List<String> countries = new ArrayList<>();
        String metaBlock = strip(META_BLOCK, html);
        if (!metaBlock.isEmpty()) {
            for (String part : metaBlock.split("/")) {
                String value = part.trim();
                if (KNOWN_COUNTRIES.contains(value)) {
                    countries.add(value);
                } else if (KNOWN_GENRES.contains(value)) {
                    genres.add(value);
                }
            }
        }
        String infoCountries = infoValue(html, "制片国家/地区");
        for (String part : infoCountries.split("/")) {
            if (!part.trim().isEmpty()) {
                countries.add(part.trim());
            }
        }

        String year = yearOf(jsonLdText(jsonLd, "datePublished"));
        if (year.isEmpty()) {
            Matcher inTitle = YEAR_IN_TITLE.matcher(title);
            if (inTitle.find()) {
                year = inTitle.group(1);
            }
        }
        if (year.isEmpty()) {
            year = strip(YEAR_SPAN, html);
        }
        if (year.isEmpty()) {
            String original = strip(ORIGINAL_TITLE, html);
            Matcher matcher = YEAR.matcher(original);
            if (matcher.find()) {
                year = matcher.group();
            }
        }
        String originalTitle = firstNonEmpty(
                strip(ORIGINAL_TITLE, html).replaceFirst("^[^:]{0,6}:", "").trim(),
                strip(ALT_NAME, html),
                infoValue(html, "又名"));
        // 「又名」这一栏常常是好几个名字用 / 隔开，第一个才是常用原名。
        int slash = originalTitle.indexOf('/');
        if (slash > 0) {
            originalTitle = originalTitle.substring(0, slash).trim();
        }
        // 「Perfect Blue（1997）」：年份在 year 字段里单独存了一份，名字后面不留。
        originalTitle = YEAR_SUFFIX.matcher(originalTitle).replaceAll("").trim();
        if (originalTitle.equals(title)) {
            originalTitle = "";
        }

        List<String> directors = jsonNameList(jsonLd == null ? null : jsonLd.opt("director"));
        List<String> casts = jsonNameList(jsonLd == null ? null : jsonLd.opt("actor"));
        if (directors.isEmpty()) {
            directors.addAll(infoNames(html, "导演"));
        }
        if (casts.isEmpty()) {
            casts.addAll(infoNames(html, "主演"));
        }

        String poster = "";
        for (String pattern : POSTER_PATTERNS) {
            String candidate = normalizePoster(strip(Pattern.compile(pattern,
                    Pattern.CASE_INSENSITIVE | Pattern.DOTALL), html));
            if (isUsablePosterUrl(candidate)) {
                poster = candidate;
                break;
            }
        }
        if (poster.isEmpty()) {
            poster = normalizePoster(jsonLdText(jsonLd, "image"));
            if (!isUsablePosterUrl(poster)) {
                poster = "";
            }
        }

        return new Subject(subjectId, "https://movie.douban.com/subject/" + subjectId + "/",
                title, originalTitle, year, rating, ratingCount, summary,
                unique(genres), unique(countries), unique(directors), unique(casts), poster);
    }

    /** 页面里可能有好几段 JSON-LD，只要那条描述影视作品的。 */
    private static JSONObject findMovieJsonLd(String html) {
        Matcher matcher = JSON_LD.matcher(html);
        while (matcher.find()) {
            try {
                JSONObject object = new JSONObject(decodeEntities(matcher.group(1).trim()));
                String type = object.optString("@type", "").toLowerCase(java.util.Locale.ROOT);
                if (type.contains("movie") || object.has("name")) {
                    return object;
                }
            } catch (Exception ignored) {
                // 页面里那段不是合法 JSON：接着找下一段。
            }
        }
        return null;
    }

    private static String jsonLdText(JSONObject jsonLd, String key) {
        if (jsonLd == null) {
            return "";
        }
        Object value = jsonLd.opt(key);
        if (value instanceof String) {
            return ((String) value).trim();
        }
        if (value instanceof JSONArray && ((JSONArray) value).length() > 0) {
            Object first = ((JSONArray) value).opt(0);
            return first instanceof String ? ((String) first).trim() : String.valueOf(first);
        }
        return "";
    }

    private static List<String> jsonTextList(Object value) {
        List<String> result = new ArrayList<>();
        if (value instanceof String) {
            result.add((String) value);
        } else if (value instanceof JSONArray) {
            JSONArray array = (JSONArray) value;
            for (int index = 0; index < array.length(); index++) {
                Object item = array.opt(index);
                if (item instanceof String) {
                    result.add((String) item);
                }
            }
        }
        return result;
    }

    private static List<String> jsonNameList(Object value) {
        List<String> result = new ArrayList<>();
        if (value instanceof JSONObject) {
            String name = ((JSONObject) value).optString("name", "");
            if (!name.isEmpty()) {
                result.add(name);
            }
        } else if (value instanceof JSONArray) {
            JSONArray array = (JSONArray) value;
            for (int index = 0; index < array.length(); index++) {
                Object item = array.opt(index);
                if (item instanceof JSONObject) {
                    String name = ((JSONObject) item).optString("name", "");
                    if (!name.isEmpty()) {
                        result.add(name);
                    }
                }
            }
        }
        return result;
    }

    /** 桌面版页面那种「<span class="pl"> 导演:</span><span class="attrs"><a>…</a></span>」结构。 */
    private static List<String> infoNames(String html, String label) {
        List<String> result = new ArrayList<>();
        for (String block : infoBlocks(html, label)) {
            Matcher links = LINK_NAME.matcher(block);
            while (links.find()) {
                String name = stripTags(links.group(1));
                if (!name.isEmpty() && !result.contains(name)) {
                    result.add(name);
                }
            }
        }
        return result;
    }

    private static String infoValue(String html, String label) {
        for (String block : infoBlocks(html, label)) {
            String value = stripTags(block);
            if (!value.isEmpty()) {
                return value;
            }
        }
        return "";
    }

    private static List<String> infoBlocks(String html, String label) {
        List<String> result = new ArrayList<>();
        Matcher matcher = INFO_LABEL.matcher(html);
        while (matcher.find()) {
            if (label.equals(matcher.group(1).trim())) {
                result.add(matcher.group(2));
            }
        }
        return result;
    }

    private static String yearOf(String date) {
        if (date == null) {
            return "";
        }
        Matcher matcher = YEAR.matcher(date);
        return matcher.find() ? matcher.group() : "";
    }

    private static String normalizePoster(String value) {
        String result = value == null ? "" : value.trim();
        if (result.startsWith("//")) {
            result = "https:" + result;
        }
        // 页面 meta 里那张图现在给的是 qnmob3-sign 签名地址（带 sa_cv / sa_ct，会过期）。
        // 图片本体在正规 CDN 上是同一张，换个主机名就能长期用，不必存那串签名。
        if (result.startsWith("https://qnmob")) {
            int slash = result.indexOf('/', "https://".length());
            result = "https://img3.doubanio.com" + result.substring(slash);
        }
        result = result.replace("/view/photo/large/public/", "/view/photo/s_ratio_poster/public/")
                .replace("/view/photo/photo/public/", "/view/photo/s_ratio_poster/public/");
        int question = result.indexOf('?');
        return question > 0 ? result.substring(0, question) : result;
    }

    /** 只要那些能当海报用的大图：60 / 300 的小方图和 qnmob 那种缩略图都会糊成一片。 */
    public static boolean isUsablePosterUrl(String value) {
        String lower = value == null ? "" : value.toLowerCase(java.util.Locale.ROOT);
        if (!lower.startsWith("http://") && !lower.startsWith("https://")) {
            return false;
        }
        if (lower.contains("qnmob") || lower.contains("/q/60/")
                || lower.contains("/w/300/") || lower.contains("/h/300/")) {
            return false;
        }
        return lower.contains("doubanio.com/view/photo/s_ratio_poster/public/")
                || lower.contains("doubanio.com/view/photo/raw/public/")
                || lower.contains("doubanio.com/view/photo/l/public/");
    }

    // ------------------------------------------------------------------ 小工具

    private static String strip(Pattern pattern, String html) {
        Matcher matcher = pattern.matcher(html);
        return matcher.find() ? stripTags(decodeEntities(matcher.group(1))) : "";
    }

    private static List<String> all(Pattern pattern, String html) {
        List<String> result = new ArrayList<>();
        Matcher matcher = pattern.matcher(html);
        while (matcher.find()) {
            String value = stripTags(decodeEntities(matcher.group(1)));
            if (!value.isEmpty()) {
                result.add(value);
            }
        }
        return result;
    }

    private static String stripTags(String value) {
        if (value == null) {
            return "";
        }
        return collapseSpace(TAGS.matcher(value).replaceAll(" "));
    }

    private static String collapseSpace(String value) {
        return value.replaceAll("\\s+", " ").trim();
    }

    private static String decodeEntities(String value) {
        if (value == null) {
            return "";
        }
        return value.replace("&nbsp;", " ").replace("&amp;", "&")
                .replace("&lt;", "<").replace("&gt;", ">")
                .replace("&quot;", "\"").replace("&#39;", "'");
    }

    private static String firstNonEmpty(String... values) {
        for (String value : values) {
            if (value != null && !value.trim().isEmpty()) {
                return value.trim();
            }
        }
        return "";
    }

    private static double toDouble(String value) {
        try {
            return Double.parseDouble(value.trim());
        } catch (Exception ignored) {
            return 0;
        }
    }

    private static int toInt(String value) {
        try {
            return Integer.parseInt(value.trim().replace(",", ""));
        } catch (Exception ignored) {
            return 0;
        }
    }

    private static List<String> unique(List<String> values) {
        Set<String> seen = new LinkedHashSet<>();
        for (String value : values) {
            String trimmed = value == null ? "" : value.trim();
            if (!trimmed.isEmpty()) {
                seen.add(trimmed);
            }
        }
        return Collections.unmodifiableList(new ArrayList<>(seen));
    }

    private static byte[] readAll(InputStream input) throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream(64 * 1024);
        byte[] buffer = new byte[16 * 1024];
        int read;
        int total = 0;
        while ((read = input.read(buffer)) > 0 && total < 3 * 1024 * 1024) {
            out.write(buffer, 0, read);
            total += read;
        }
        input.close();
        return out.toByteArray();
    }

    /** 豆瓣大部分页面是 UTF-8，老页面仍有 GB18030：按 Content-Type 走，实在不对就换一张字表。 */
    private static String decode(byte[] body, String contentType) {
        String declared = charsetOf(contentType);
        if (declared != null) {
            try {
                return new String(body, Charset.forName(declared));
            } catch (Exception ignored) {
                // 服务器报了个 JVM 不认识的字符集，退回下面两条路。
            }
        }
        String utf8 = new String(body, StandardCharsets.UTF_8);
        return utf8.indexOf('\uFFFD') >= 0 ? new String(body, Charset.forName("GB18030")) : utf8;
    }

    private static String charsetOf(String contentType) {
        if (contentType == null) {
            return null;
        }
        String lower = contentType.toLowerCase(java.util.Locale.ROOT);
        int at = lower.indexOf("charset=");
        if (at < 0) {
            return null;
        }
        String value = contentType.substring(at + "charset=".length()).trim();
        int end = value.indexOf(';');
        if (end > 0) {
            value = value.substring(0, end);
        }
        return value.replace("\"", "").replace("'", "").trim();
    }

    /**
     * 一小段够用的 JSON 读取器，只给搜索页用。
     *
     * <p>条目页那一路用系统的 {@code org.json} 没问题；搜索页的解析要能在离线自检里跑，
     * 而 android.jar 里那份 JSONObject 是 {@code throw new RuntimeException("Stub!")} 的空壳，
     * 页面结构一漂移脚本根本发现不了。所以这一段自己读，返回值用 Map / List / String /
     * Number / Boolean 表示，整数的数字给 Long，免得条目号变成科学计数法。</p>
     */
    private static final class MiniJson {
        private final String text;
        private int cursor;

        private MiniJson(String source) {
            text = source;
        }

        /** 读第一个值；后面剩下的东西（页面里那个分号）不管。 */
        static Object read(String source) {
            return new MiniJson(source).readValue();
        }

        private Object readValue() {
            skipSpace();
            if (cursor >= text.length()) {
                throw new IllegalArgumentException("JSON 提前结束");
            }
            char c = text.charAt(cursor);
            if (c == '{') {
                return readObject();
            }
            if (c == '[') {
                return readArray();
            }
            if (c == '"') {
                return readString();
            }
            if (text.startsWith("true", cursor)) {
                cursor += 4;
                return Boolean.TRUE;
            }
            if (text.startsWith("false", cursor)) {
                cursor += 5;
                return Boolean.FALSE;
            }
            if (text.startsWith("null", cursor)) {
                cursor += 4;
                return null;
            }
            return readNumber();
        }

        private Map<String, Object> readObject() {
            Map<String, Object> result = new LinkedHashMap<>();
            cursor++;
            skipSpace();
            if (cursor < text.length() && text.charAt(cursor) == '}') {
                cursor++;
                return result;
            }
            while (true) {
                skipSpace();
                if (cursor >= text.length() || text.charAt(cursor) != '"') {
                    throw new IllegalArgumentException("JSON 键名不完整");
                }
                String key = readString();
                skipSpace();
                if (cursor >= text.length() || text.charAt(cursor) != ':') {
                    throw new IllegalArgumentException("JSON 少了冒号");
                }
                cursor++;
                result.put(key, readValue());
                skipSpace();
                if (cursor < text.length() && text.charAt(cursor) == ',') {
                    cursor++;
                    continue;
                }
                if (cursor < text.length() && text.charAt(cursor) == '}') {
                    cursor++;
                    return result;
                }
                throw new IllegalArgumentException("JSON 对象没收尾");
            }
        }

        private List<Object> readArray() {
            List<Object> result = new ArrayList<>();
            cursor++;
            skipSpace();
            if (cursor < text.length() && text.charAt(cursor) == ']') {
                cursor++;
                return result;
            }
            while (true) {
                result.add(readValue());
                skipSpace();
                if (cursor < text.length() && text.charAt(cursor) == ',') {
                    cursor++;
                    continue;
                }
                if (cursor < text.length() && text.charAt(cursor) == ']') {
                    cursor++;
                    return result;
                }
                throw new IllegalArgumentException("JSON 数组没收尾");
            }
        }

        private Object readNumber() {
            int start = cursor;
            while (cursor < text.length() && "+-.eE0123456789".indexOf(text.charAt(cursor)) >= 0) {
                cursor++;
            }
            String literal = text.substring(start, cursor);
            if (literal.isEmpty()) {
                throw new IllegalArgumentException("JSON 读到认不出的值");
            }
            try {
                return literal.indexOf('.') >= 0 || literal.indexOf('e') >= 0
                        || literal.indexOf('E') >= 0
                        ? (Object) Double.valueOf(literal) : (Object) Long.valueOf(literal);
            } catch (NumberFormatException broken) {
                throw new IllegalArgumentException("JSON 数字不完整");
            }
        }

        private String readString() {
            StringBuilder built = new StringBuilder();
            cursor++;
            while (cursor < text.length()) {
                char c = text.charAt(cursor++);
                if (c == '"') {
                    return built.toString();
                }
                if (c != '\\') {
                    built.append(c);
                    continue;
                }
                if (cursor >= text.length()) {
                    break;
                }
                char escape = text.charAt(cursor++);
                if (escape == 'n') {
                    built.append('\n');
                } else if (escape == 't') {
                    built.append('\t');
                } else if (escape == 'r') {
                    built.append('\r');
                } else if (escape == 'u') {
                    if (cursor + 4 > text.length()) {
                        throw new IllegalArgumentException("JSON 转义断了");
                    }
                    built.append((char) Integer.parseInt(text.substring(cursor, cursor + 4), 16));
                    cursor += 4;
                } else {
                    built.append(escape);
                }
            }
            throw new IllegalArgumentException("JSON 字符串没闭合");
        }

        private void skipSpace() {
            while (cursor < text.length() && Character.isWhitespace(text.charAt(cursor))) {
                cursor++;
            }
        }
    }

    /** 一个只读的小字符串集合：静态初始化里比写一堆 add() 干净。 */
    private static final class SetOf extends java.util.HashSet<String> {
        SetOf(String... values) {
            java.util.Collections.addAll(this, values);
        }
    }
}
