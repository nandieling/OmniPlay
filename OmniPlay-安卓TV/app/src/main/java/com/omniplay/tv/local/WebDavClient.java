package com.omniplay.tv.local;

import android.util.Base64;

import org.xmlpull.v1.XmlPullParser;
import org.xmlpull.v1.XmlPullParserFactory;

import java.io.BufferedReader;
import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * WebDAV directory walker + authenticated stream URL builder,
 * ported from the OmniPlay (mac) WebDAV scanner.
 */
public final class WebDavClient {
    public static final class Entry {
        public final String name;
        public final String href;
        public final boolean isCollection;
        /** getcontentlength；服务器没给就是 0，蓝光主片判断会退回「不知道体积」。 */
        public final long sizeBytes;

        Entry(String name, String href, boolean isCollection, long sizeBytes) {
            this.name = name;
            this.href = href;
            this.isCollection = isCollection;
            this.sizeBytes = sizeBytes;
        }
    }

    public static final class Credential {
        public final String username;
        public final String password;
        public final String cookieHeader;

        public Credential(String username, String password) {
            this.username = username == null ? "" : username;
            this.password = password == null ? "" : password;
            if (this.username.isEmpty() && this.password.isEmpty()) {
                cookieHeader = "";
            } else {
                String raw = this.username + ":" + this.password;
                cookieHeader = "Basic " + Base64.encodeToString(
                        raw.getBytes(StandardCharsets.UTF_8), Base64.NO_WRAP);
            }
        }
    }

    private static final Pattern YEAR_DIR = Pattern.compile("(?i)^season[ ._-]*\\d{1,2}$");
    /** 只用来决定「哪个目录算作品名」：分盘目录不能当标题来源。 */
    private static final Pattern GENERIC_DIR = Pattern.compile(
            "(?i)^(bdmv|stream|bd|cd|dvd|disc\\s*\\d+|volume\\s*\\d+|vol\\s*\\d+|cd\\s*\\d+|dvd\\s*\\d+)$");
    private static final int REQUEST_TIMEOUT_MS = 15000;
    private static final byte[] PROPFIND_BODY =
            ("<propfind xmlns='DAV:'><prop><resourcetype/><displayname/><getcontentlength/></prop></propfind>")
                    .getBytes(StandardCharsets.UTF_8);

    private final String rootUrl;
    private final Credential credential;
    private final java.util.Map<String, List<Entry>> directoryCache = new ConcurrentHashMap<>();
    // 光盘的 BDMV / STREAM / DISC1 也要占三层，深度太小会让整盘看不见；
    // 本地目录走 12 层，这里对齐。这条只当兜底：真正的「不重走」由 visited 集合保证。
    private int maxDepth = 12;
    /** 一轮遍历最多列多少个目录：畸形响应把兄弟目录也回成子条目时，宁可少扫一片，也别把电视跑死。 */
    private static final int MAX_DIRECTORIES = 20000;
    /** 清单缓存只为遍历期间的自引用条目和目录选择器服务，涨过头就整份重来，别吃掉电视的内存。 */
    private static final int MAX_CACHED_DIRECTORIES = 4096;
    /** 待走的目录（广度优先，与 mac 版的 pendingDirectories 同一走法）。 */
    private static final class PendingDirectory {
        final String url;
        final int depth;

        PendingDirectory(String url, int depth) {
            this.url = url;
            this.depth = depth;
        }
    }

    public WebDavClient(String rootUrl, Credential credential) {
        this.rootUrl = normalizeRoot(rootUrl);
        this.credential = credential == null ? new Credential("", "") : credential;
    }

    public String rootUrl() {
        return rootUrl;
    }

    public static String normalizeRoot(String value) {
        if (value == null) {
            return "";
        }
        String url = value.trim();
        if (!url.contains("://")) {
            url = "https://" + url;
        }
        while (url.endsWith("/")) {
            url = url.substring(0, url.length() - 1);
        }
        return url;
    }

    /**
     * Lists one directory with a Depth:1 PROPFIND. The request goes through {@link Http}
     * rather than HttpURLConnection because Android's HttpURLConnection rejects any method
     * outside its fixed list, and PROPFIND is not on it.
     */
    public List<Entry> list(String url) throws IOException {
        List<Entry> cached = directoryCache.get(url);
        if (cached != null) {
            return cached;
        }
        Map<String, String> headers = new LinkedHashMap<>();
        headers.put("Depth", "1");
        headers.put("Accept", "application/xml");
        headers.put("Content-Type", "text/xml; charset=utf-8");
        if (!credential.cookieHeader.isEmpty()) {
            headers.put("Authorization", credential.cookieHeader);
        }
        Http.Response response = Http.request(ensureTrailingSlash(url), "PROPFIND",
                headers, PROPFIND_BODY, REQUEST_TIMEOUT_MS);
        if (response.status != 207 && response.status != 200) {
            throw new IOException("WebDAV PROPFIND 返回 " + response.status);
        }
        List<Entry> entries = parseMultiStatus(new ByteArrayInputStream(response.body), url);
        synchronized (entries) {
            if (directoryCache.size() > MAX_CACHED_DIRECTORIES) {
                directoryCache.clear();
            }
            directoryCache.put(url, entries);
        }
        return entries;
    }

    /**
     * Walks the tree and yields every video file, including the streams inside a Blu-ray
     * folder: mac 版就是整盘走完之后才在每个分组里挑主片， walker 自己不做判断，
     * 否则 BDMV 一旦剪掉，原盘电影就一个条目都进不来。
     */
    public void walk(VideoSink sink) throws IOException {
        java.util.Deque<PendingDirectory> pending = new java.util.ArrayDeque<>();
        java.util.Set<String> visited = new java.util.HashSet<>();
        pending.add(new PendingDirectory(rootUrl, 0));
        int listed = 0;
        while (!pending.isEmpty()) {
            PendingDirectory directory = pending.pollFirst();
            String key = directoryKey(directory.url);
            // 同一个目录只列一次：服务器常把 /影视 规范化成 /影视/，也可能回一条别名路径，
            // 两者算同一个目录；跳过已经走过的，才不会有「一个文件夹走出几万条记录」。
            if (!visited.add(key)) {
                continue;
            }
            if (++listed > MAX_DIRECTORIES) {
                throw new IOException("这个媒体源的目录超过 " + MAX_DIRECTORIES
                        + " 个，这一轮先停在这里。");
            }
            for (Entry entry : list(directory.url)) {
                if (!entry.isCollection) {
                    if (LocalMediaNameParser.isVideoFile(entry.name)) {
                        sink.onVideo(entry.name, relativePath(entry.href), entry.sizeBytes);
                    }
                    continue;
                }
                if (isSkippableCollection(entry.name)) {
                    continue;
                }
                // RFC 4918 要求 Depth:1 的响应里带上「被请求的目录自己」。上一版直接照着
                // 递归，等于让每个目录原地自套十二层，文件数按指数量往上翻，扫描永远走不完，
                // 刮削也一直排不上队。mac 版靠 visitedDirectories + 相对路径必须落在本目录
                // 之下这两道判断挡掉它，这里同一套。
                if (directoryKey(entry.href).equals(key)) {
                    continue;
                }
                if (directory.depth + 1 > maxDepth) {
                    continue;
                }
                pending.add(new PendingDirectory(entry.href, directory.depth + 1));
            }
        }
    }

    /**
     * 目录的身份：只看主机之后的那段路径，先解一层百分号转义、再去掉结尾斜杠。
     * 编码写法不同、结尾斜杠有无不同，都算同一个目录（与 mac 版 normalizePath 同一口径）。
     */
    private static String directoryKey(String url) {
        String decoded = UrlLabels.displayPath(url);
        int schemeEnd = decoded.indexOf("://");
        String rest = schemeEnd >= 0 ? decoded.substring(schemeEnd + 3) : decoded;
        int slash = rest.indexOf('/');
        String path = slash >= 0 ? rest.substring(slash) : "/";
        int query = path.indexOf('?');
        if (query >= 0) {
            path = path.substring(0, query);
        }
        int fragment = path.indexOf('#');
        if (fragment >= 0) {
            path = path.substring(0, fragment);
        }
        while (path.length() > 1 && path.endsWith("/")) {
            path = path.substring(0, path.length() - 1);
        }
        return path;
    }

    /** 只有系统垃圾目录才剪掉；BDMV / STREAM / DISC1 都得走进去。 */
    private static boolean isSkippableCollection(String name) {
        if (name == null || name.isEmpty()) {
            return true;
        }
        String lower = name.toLowerCase(java.util.Locale.ROOT);
        return lower.equals("lost+found") || lower.startsWith(".$") || lower.equals("@eadir")
                || lower.equals("lost & found") || lower.endsWith(".nomedia")
                || lower.startsWith("_") || lower.startsWith(".");
    }

    /**
     * Group folder for a file's relative path (first meaningful directory). 分盘和光盘结构
     * 目录要跳过，否则原盘剧集的组名会变成 DISC1 这种没有意义的词。
     */
    public static String groupFolderFor(String relativePath) {
        String[] parts = relativePath.split("/");
        for (String part : parts) {
            if (part.isEmpty() || isSkippableCollection(part)
                    || GENERIC_DIR.matcher(part).matches()) {
                continue;
            }
            return part;
        }
        return "";
    }

    /**
     * Relative path of a PROPFIND href, always percent-decoded and always keeping the folder
     * structure.
     * <p>
     * 服务器只编码它自己回的路径：挂载点里带中文时，用户填的 {@code rootUrl} 是原文、href 是
     * 编码过的，直接比原文会让前缀匹配失败，相对路径退化成最后一段编码过的文件名。跟着坏掉的就
     * 有三部：刮削查询词变成一串百分号、剧集分组名取不到目录名、播放地址被二次编码成 404。
     * 两边都先解一层再比，两条路径就都对上了。
     */
    private String relativePath(String href) {
        String decodedHref = UrlLabels.displayPath(href);
        String decodedRoot = UrlLabels.displayPath(rootUrl);
        if (decodedHref.startsWith(decodedRoot)) {
            String relative = decodedHref.substring(decodedRoot.length());
            while (relative.startsWith("/")) {
                relative = relative.substring(1);
            }
            return relative;
        }
        int slash = decodedHref.lastIndexOf('/');
        return slash >= 0 ? decodedHref.substring(slash + 1) : decodedHref;
    }

    /** Stream URL with embedded credentials (for mpv to fetch directly). */
    public String streamUrl(String relativePath) {
        String decoded = relativePath;
        String path = "";
        for (String segment : decoded.split("/")) {
            if (!segment.isEmpty()) {
                path += "/" + urlEncode(segment).replace("+", "%20");
            }
        }
        if (credential.username.isEmpty() && credential.password.isEmpty()) {
            return rootUrl + path;
        }
        return rootUrl
                .replaceFirst("^(https?://)", "$1"
                        + urlEncode(credential.username).replace("+", "%20")
                        + ":"
                        + urlEncode(credential.password).replace("+", "%20")
                        + "@")
                + path;
    }

    /**
     * {@code URLEncoder.encode(String, Charset)} 是 Android 13（API 33）才加的重载，
     * 本应用 minSdk 26，在旧机器上一点海报就抛 {@code NoSuchMethodError}；只能用字符串重载。
     */
    private static String urlEncode(String value) {
        try {
            return URLEncoder.encode(value, "UTF-8");
        } catch (java.io.UnsupportedEncodingException impossible) {
            return value;
        }
    }

    /**
     * Resolves a stored relative path (possibly a /group/... form) back to a full
     * WebDAV URL. For local-folder files the path is already absolute.
     */
    public String resolvePlaybackUrl(String relativePath) {
        if (relativePath.startsWith(rootUrl) || relativePath.startsWith("/")) {
            return relativePath;
        }
        return streamUrl(relativePath);
    }

    private static String ensureTrailingSlash(String url) {
        return url.endsWith("/") ? url : url + "/";
    }

    /** 见 {@link UrlLabels#displayPath}：显示前解一层百分号转义。 */
    public static String displayPath(String value) {
        return UrlLabels.displayPath(value);
    }

    private static long parseLong(String value) {
        try {
            return Long.parseLong(value.trim());
        } catch (RuntimeException ignored) {
            return 0;
        }
    }

    private List<Entry> parseMultiStatus(InputStream input, String baseHref) throws IOException {
        List<Entry> entries = new ArrayList<>();
        try {
            XmlPullParserFactory factory = XmlPullParserFactory.newInstance();
            factory.setNamespaceAware(false);
            XmlPullParser parser = factory.newPullParser();
            parser.setInput(new BufferedReader(new InputStreamReader(input, StandardCharsets.UTF_8)));
            int event = parser.getEventType();
            String currentHref = null;
            String currentName = null;
            long currentSize = 0;
            boolean inCollection = false;
            boolean insideResponse = false;
            while (event != XmlPullParser.END_DOCUMENT) {
                if (event == XmlPullParser.START_TAG) {
                    String tag = parser.getName().toLowerCase(java.util.Locale.ROOT);
                    if (tag.equals("d:response") || tag.equals("response")) {
                        insideResponse = true;
                        currentHref = null;
                        currentName = null;
                        currentSize = 0;
                        inCollection = false;
                    } else if (insideResponse && (tag.equals("d:href") || tag.equals("href"))) {
                        if (currentHref == null) {
                            currentHref = parser.nextText().trim();
                        }
                    } else if (insideResponse && (tag.equals("d:displayname") || tag.equals("displayname"))) {
                        currentName = parser.nextText().trim();
                    } else if (insideResponse && (tag.equals("d:collection") || tag.equals("collection"))) {
                        inCollection = true;
                    } else if (insideResponse
                            && (tag.equals("d:getcontentlength") || tag.equals("getcontentlength"))) {
                        currentSize = parseLong(parser.nextText());
                    }
                } else if (event == XmlPullParser.END_TAG) {
                    String tag = parser.getName().toLowerCase(java.util.Locale.ROOT);
                    if (tag.equals("d:response") || tag.equals("response")) {
                        if (currentHref != null) {
                            Entry entry = new Entry(
                                    // 有的服务器连 displayname 都是转义过的，显示前统一解一层。
                                    displayPath(currentName == null || currentName.isEmpty()
                                            ? displayNameOf(currentHref) : currentName),
                                    absoluteHref(currentHref, baseHref),
                                    inCollection,
                                    currentSize);
                            entries.add(entry);
                        }
                        insideResponse = false;
                    }
                }
                event = parser.next();
            }
        } catch (org.xmlpull.v1.XmlPullParserException e) {
            throw new IOException("解析 WebDAV 响应失败：" + e.getMessage(), e);
        }
        return entries;
    }

    /**
     * Label for a response that carries no displayname. Collection hrefs end with "/", and the
     * trailing slash has to go before the last segment is taken: leaving it in produced an empty
     * name, and the folder picker skips unnamed entries, so plain folders simply never appeared.
     */
    private static String displayNameOf(String href) {
        String path = href;
        while (path.endsWith("/")) {
            path = path.substring(0, path.length() - 1);
        }
        int slash = path.lastIndexOf('/');
        String name = slash >= 0 ? path.substring(slash + 1) : path;
        if (name.isEmpty()) {
            return path.isEmpty() ? href : path;
        }
        try {
            return java.net.URLDecoder.decode(name, "UTF-8");
        } catch (IOException ignored) {
            return name;
        }
    }

    private static String absoluteHref(String href, String baseHref) {
        if (href.startsWith("http://") || href.startsWith("https://")) {
            return href;
        }
        if (href.startsWith("/")) {
            String base = baseHref;
            int schemeEnd = base.indexOf("://");
            String scheme = base.substring(0, schemeEnd);
            String rest = base.substring(schemeEnd + 3);
            int slash = rest.indexOf('/');
            String authority = slash >= 0 ? rest.substring(0, slash) : rest;
            return scheme + "://" + authority + href;
        }
        return baseHref.endsWith("/") ? baseHref + href : baseHref + "/" + href;
    }

    public interface VideoSink {
        void onVideo(String fileName, String relativePath, long sizeBytes);
    }
}
