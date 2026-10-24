package com.omniplay.tv.local;

import android.content.Context;
import android.content.SharedPreferences;
import android.os.Environment;

import com.omniplay.tv.data.Models;

import java.io.File;
import java.io.IOException;

import jcifs.smb.SmbFile;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.HashMap;
import java.util.Set;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * Standalone media engine for the Android TV build:
 * local folder / WebDAV sources, scan into SQLite, TMDB scraping with proxy
 * gateway, playback progress (0.95 watched / precise resume).
 * Produces the same Models.* types the UI already renders.
 */
public final class LocalEngine {
    public static final class Source {
        public final int id;
        public final String kind;        // "local" | "webdav"
        public final String name;
        public final String path;        // local absolute dir or webdav root
        public final String username;
        public final String password;
        public final boolean enabled;

        public Source(int id, String kind, String name, String path,
                      String username, String password, boolean enabled) {
            this.id = id;
            this.kind = kind;
            this.name = name;
            this.path = path;
            this.username = username == null ? "" : username;
            this.password = password == null ? "" : password;
            this.enabled = enabled;
        }
    }

    public static final class ScanReport {
        public int scannedFiles;
        public int newEntities;
        public int newFiles;
        public int removedFiles;
        /** 蓝光光盘里被跳过、没有入库的分卷数（花絮、宣传片、多余分段）。 */
        public int skippedStreams;
        public boolean success;
        public String message;
    }

    private static LocalEngine instance;

    private final Context context;
    private final LocalMediaLibrary library;
    private final TmdbClient tmdb;
    private final DoubanClient douban;
    private final SharedPreferences preferences;
    private final TvSettings settings;
    private final OfflineCacheManager cache;
    private final Map<String, WebDavClient> webDavClients = new ConcurrentHashMap<>();
    private final Map<String, SmbClient> smbClients = new ConcurrentHashMap<>();
    private volatile boolean scraping;
    /**
     * 一次「扫描刮削」的账本。扫描途中挤的那一小段和扫完尾那一轮共用一份，同一个条目
     * 只记一次：分开记会把同一批数两遍，收尾那句就变成「共处理 6 个：3 个没搜到、3
     * 个年份对不上」，像是一个都没刷新，实际上海报墙刚刷了五张。
     */
    private final Set<String> tallySeen = new HashSet<>();
    private int tallyUpdated;
    private int tallyFailed;
    private int tallyUnmatched;
    private int tallyYearMismatch;
    private int tallySkipped;
    private int tallyMerged;
    /** Guards {@link #scraping} so only one scrape pass can be in flight. */
    private final Object scrapeLock = new Object();
    /** 一次刮削 200 个条目；队列更长时 {@link #scrapePending} 会继续取下一批。 */
    private static final int SCRAPE_BATCH = 200;
    /** 扫描途中每次挤多久去刮削：够刮掉两三个条目，又不至于把目录遍历按停。 */
    private static final long SCRAPE_PUMP_BUDGET_MS = 4000;
    /** 每登记这么多条新文件，就在扫描途中挤一次刮削。 */
    private static final int SCRAPE_PUMP_AFTER_FILES = 20;
    private volatile String scrapeStatus = "";
    private final AtomicInteger activeScrapes = new AtomicInteger();
    private final Set<String> episodeScrapeInFlight = ConcurrentHashMap.newKeySet();

    public interface ProgressListener {
        void onScrapeProgress(String message);
    }

    /** 扫描进行中该媒体源被用户移除，用它把当前这一轮的遍历立刻停住。 */
    public static final class ScanAborted extends RuntimeException {
        ScanAborted(String sourceName) {
            super(sourceName);
        }
    }

    /**
     * 进度的唯一出口：先存进 {@link #scrapeStatus} 再回调。首页左上角读的是这个字段，
     * 扫描阶段以前只回调不存，于是整段扫描期间那一行一直停在开场第一句上，
     * 大目录扫几分钟界面上一个字都不动，看着就像卡死了。
     */
    private void progress(ProgressListener listener, String message) {
        scrapeStatus = message;
        if (listener != null) {
            listener.onScrapeProgress(message);
        }
    }

    private LocalEngine(Context context) {
        this.context = context.getApplicationContext();
        this.library = new LocalMediaLibrary(context);
        this.tmdb = new TmdbClient(context);
        this.douban = new DoubanClient(context);
        this.settings = new TvSettings(context);
        this.preferences = context.getSharedPreferences("omniplay_tv_sources", Context.MODE_PRIVATE);
        this.cache = OfflineCacheManager.shared(context, this, library, settings);
    }

    public static synchronized LocalEngine shared(Context context) {
        if (instance == null) {
            instance = new LocalEngine(context);
        }
        return instance;
    }

    // ------------------------------------------------------------- sources

    public TmdbClient tmdb() {
        return tmdb;
    }

    public TvSettings settings() {
        return settings;
    }

    public OfflineCacheManager cache() {
        return cache;
    }

    public LocalMediaLibrary library() {
        return library;
    }

    public List<Source> sources() {
        List<Source> result = new ArrayList<>();
        java.util.Set<Integer> ids = new java.util.HashSet<>();
        String raw = preferences.getString("sources", "");
        if (!raw.isEmpty()) {
            for (String line : raw.split("\n")) {
                if (line.isEmpty()) {
                    continue;
                }
                String[] parts = line.split("\u0001", -1);
                if (parts.length < 4) {
                    continue;
                }
                try {
                    int id = Integer.parseInt(parts[0]);
                    ids.add(id);
                    result.add(new Source(
                            id,
                        parts[1],
                        parts[2],
                        parts[3],
                        parts.length > 4 ? parts[4] : "",
                        parts.length > 5 ? parts[5] : "",
                        parts.length <= 6 || Boolean.parseBoolean(parts[6])));
                } catch (NumberFormatException ignored) {
                }
            }
        }
        liveSourceIds = ids;
        return result;
    }

    public void saveSource(String kind, String name, String path, String username, String password) {
        List<Source> current = sources();
        int nextId = 1;
        for (Source source : current) {
            nextId = Math.max(nextId, source.id + 1);
        }
        for (int i = 0; i < current.size(); i++) {
            Source source = current.get(i);
            boolean sameTarget = source.kind.equals(kind)
                    && source.path.equalsIgnoreCase(path)
                    && source.username.equals(username)
                    && source.password.equals(password);
            if (sameTarget) {
                String trimmedName = name == null || name.trim().isEmpty()
                        ? defaultSourceName(kind, path) : name.trim();
                current.set(i, new Source(source.id, kind, trimmedName, path,
                        username, password, true));
                persistSources(current);
                webDavClients.remove(path);
                return;
            }
        }
        String trimmedName = name == null || name.trim().isEmpty()
                ? defaultSourceName(kind, path) : name.trim();
        current.add(new Source(nextId, kind, trimmedName, path, username, password, true));
        persistSources(current);
        webDavClients.remove(path);
        smbClients.remove(path);
    }

    public void removeSource(int sourceId) {
        // 移除之后左上角还在「正在扫描」，是因为这一轮还在往下走：先叫停，再删源。
        abortRequested = true;
        List<Source> current = sources();
        current.removeIf(source -> source.id == sourceId);
        persistSources(current);
        // Same order as the mac app: forget the source, then forget what it indexed, so the wall
        // cannot keep posters of a share the user just deleted.
        library.purgeSource(sourceId);
    }

    public void setSourceEnabled(int sourceId, boolean enabled) {
        List<Source> current = sources();
        List<Source> updated = new ArrayList<>();
        for (Source source : current) {
            updated.add(source.id == sourceId
                    ? new Source(source.id, source.kind, source.name, source.path,
                    source.username, source.password, enabled)
                    : source);
        }
        persistSources(updated);
    }

    public void renameSource(int sourceId, String name) {
        String trimmed = name == null ? "" : name.trim();
        if (trimmed.isEmpty()) {
            return;
        }
        List<Source> current = sources();
        List<Source> updated = new ArrayList<>();
        for (Source source : current) {
            updated.add(source.id == sourceId
                    ? new Source(source.id, source.kind, trimmed, source.path,
                    source.username, source.password, source.enabled)
                    : source);
        }
        persistSources(updated);
    }

    /**
     * 还活着的媒体源 id 快照。扫描线程每个文件都要问一句「这个源还在不在」，不能每条
     * 都回去读一遍偏好，所以在解析源列表和落盘的时候顺手带出来。
     */
    private volatile java.util.Set<Integer> liveSourceIds = java.util.Collections.emptySet();

    /**
     * 用户中途移除媒体源时立起来的牌子：扫描的每个文件、每一批刮削都会看它一眼，
     * 看到就把这一轮收住——移除之后左上角还挂着「正在扫描」比什么都烦人。
     */
    private volatile boolean abortRequested;

    /** 主动叫停这一轮扫描刮削（下一次点「扫描刮削」会清掉标记从头再来）。 */
    public void cancelScanAndScrape() {
        abortRequested = true;
    }

    /** 当前媒体源 id 集合，供扫描收尾清理「源已经不存在了」的文件行。 */
    public java.util.Set<Integer> liveSourceIds() {
        return liveSourceIds;
    }

    /**
     * 启用的媒体源。首页列表与计数只认这些源（mac 版每条媒体库查询都带
     * {@code COALESCE(mediaSource.isEnabled, 1) = 1}），停用因此是「从首页收起来」，
     * 而不是删数据：重新启用不用重新扫描。
     */
    public java.util.Set<Integer> enabledSourceIds() {
        java.util.Set<Integer> ids = new java.util.HashSet<>();
        for (Source source : sources()) {
            if (source.enabled) {
                ids.add(source.id);
            }
        }
        return ids;
    }

    private void persistSources(List<Source> sources) {
        java.util.Set<Integer> ids = new java.util.HashSet<>();
        for (Source source : sources) {
            ids.add(source.id);
        }
        liveSourceIds = ids;
        StringBuilder builder = new StringBuilder();
        for (Source source : sources) {
            if (builder.length() > 0) {
                builder.append('\n');
            }
            builder.append(source.id).append('\u0001')
                    .append(source.kind).append('\u0001')
                    .append(source.name).append('\u0001')
                    .append(source.path).append('\u0001')
                    .append(source.username).append('\u0001')
                    .append(source.password).append('\u0001')
                    .append(source.enabled);
        }
        preferences.edit().putString("sources", builder.toString()).apply();
    }

    private static String defaultSourceName(String kind, String path) {
        if (path == null || path.isEmpty()) {
            return displayNameForKind(kind);
        }
        String cleaned = path.trim();
        while (cleaned.endsWith("/")) {
            cleaned = cleaned.substring(0, cleaned.length() - 1);
        }
        int slash = cleaned.lastIndexOf('/');
        String last = slash >= 0 ? cleaned.substring(slash + 1) : cleaned;
        if (last.isEmpty()) {
            return displayNameForKind(kind);
        }
        // WebDAV 的路径段是百分号编码过的，直接截出来就是「%E7%94%B5%E5%BD%B1」这种一串符号。
        return "webdav".equals(kind) ? UrlLabels.displayPath(last) : last;
    }

    private static String displayNameForKind(String kind) {
        if (kind.equals("webdav")) {
            return "WebDAV";
        }
        if (kind.equals("smb")) {
            return "SMB";
        }
        return "本地目录";
    }

    private WebDavClient webDavClient(Source source) {
        return webDavClients.computeIfAbsent(source.path,
                key -> new WebDavClient(key, new WebDavClient.Credential(source.username, source.password)));
    }

    private SmbClient smbClient(Source source) {
        return smbClients.computeIfAbsent(source.path,
                key -> new SmbClient(key, source.username, source.password));
    }

    /** Tests an SMB source's connectivity before it is saved. */
    public void testSmbConnection(String path, String username, String password) throws IOException {
        new SmbClient(path, username, password).testConnection();
    }

    /** Returns the client whose share root matches an absolute smb:// playback URL. */
    public SmbClient smbClientForUrl(String absoluteUrl) {
        for (Source source : sources()) {
            if (source.kind.equals("smb")) {
                SmbClient client = smbClient(source);
                if (absoluteUrl.startsWith(client.root())) {
                    return client;
                }
            }
        }
        return new SmbClient(absoluteUrl, "", "");
    }

    /** Opens an absolute smb:// URL with the credentials of the source that owns it. */
    public SmbFile smbFile(String absoluteUrl) throws IOException {
        return smbClientForUrl(absoluteUrl).open(absoluteUrl);
    }

    // ------------------------------------------------------------- scan

    public ScanReport scanAll(ProgressListener listener) {
        ScanReport report = new ScanReport();
        try {
            List<Source> enabled = new ArrayList<>();
            for (Source source : sources()) {
                if (source.enabled) {
                    enabled.add(source);
                }
            }
            if (enabled.isEmpty()) {
                report.message = "没有已启用的媒体源。";
                return report;
            }
            Set<String> currentFileIds = new HashSet<>();
            Set<Integer> scannedSourceIds = new HashSet<>();
            for (Source source : enabled) {
                scannedSourceIds.add(source.id);
            }
            int sourceIndex = 0;
            progress(listener, "扫描目录中...");
            for (Source source : enabled) {
                if (abortRequested) {
                    // 中途移除了媒体源：剩下的源这一轮不扫了，用户下次点再从头来。
                    report.success = true;
                    report.message = "已停止扫描刮削。";
                    return report;
                }
                sourceIndex++;
                int newBefore = report.newFiles;
                boolean removedMidScan = false;
                try {
                    if (source.kind.equals("local")) {
                        scanLocalSource(source, currentFileIds, report, listener, sourceIndex, enabled.size());
                    } else if (source.kind.equals("smb")) {
                        scanSmbSource(source, currentFileIds, report, listener, sourceIndex, enabled.size());
                    } else {
                        scanWebDavSource(source, currentFileIds, report, listener, sourceIndex, enabled.size());
                    }
                } catch (ScanAborted aborted) {
                    // 用户把这一源移掉了：别再往里走。剩下的清理交给收尾的 purgeUnknownSources，
                    // 它本来就按「还活着的源」清，海报不会留在墙上。
                    removedMidScan = true;
                    progress(listener, "已移除：" + aborted.getMessage() + "，本轮跳过");
                }
                if (!removedMidScan) {
                    // 与 mac 版同一句「完成：源名（新增 X 移除 Y）」，让用户看见进度在走。
                    progress(listener, "完成：" + source.name + "（新增 " + (report.newFiles - newBefore) + "）");
                    // 这个源已经整整齐齐在库里了，先把它那批刮掉：下一个源还要走上几分钟，
                    // 海报不必等到那时候才出现在首页。
                    pumpScrapeDuringScan(listener);
                }
            }
            progress(listener, "更新首页...");
            // 停用的源这一轮没去扫，它的文件行不能被当成「盘上已经没了」删掉。
            int removed = library.removeMissingFiles(new ArrayList<>(currentFileIds), scannedSourceIds);
            // 扫描进行中移除的媒体源，这一轮仍会把它扫到的文件登记回来；收尾按还活着的源再清一次。
            removed += library.purgeUnknownSources(liveSourceIds);
            report.removedFiles = removed;
            report.success = true;
            // 收尾文案与 mac 版同一句话：新增 / 移除 / 共发现。
            report.message = "扫描完成：新增 " + report.newFiles
                    + "，移除 " + removed
                    + "，共扫描 " + report.scannedFiles + " 个文件。"
                    + (report.skippedStreams > 0
                            ? "（蓝光非正片分卷跳过 " + report.skippedStreams + " 条）" : "");
        } catch (Exception error) {
            report.success = false;
            report.message = "扫描失败：" + error.getMessage();
        }
        return report;
    }

    private void scanLocalSource(Source source, Set<String> fileIds, ScanReport report,
                                 ProgressListener listener, int sourceIndex, int sourceTotal) {
        File root = new File(source.path);
        if (!root.isDirectory()) {
            return;
        }
        ScanCollector collector = new ScanCollector(source, fileIds, report, listener, sourceIndex, sourceTotal);
        walkLocal(root, root, 0, collector::offer);
        collector.finish();
    }

    private void walkLocal(File root, File dir, int depth, LocalFileVisitor visitor) {
        if (depth > 12) {
            return;
        }
        File[] children = dir.listFiles();
        if (children == null) {
            return;
        }
        for (File child : children) {
            String name = child.getName();
            if (name.startsWith(".") || name.startsWith("_")) {
                continue;
            }
            if (child.isDirectory()) {
                if (!isSkippableFolder(name)) {
                    walkLocal(root, child, depth + 1, visitor);
                }
                continue;
            }
            if (LocalMediaNameParser.isVideoFile(name)) {
                visitor.onVideo(name, relativeOf(root, child), child.length());
            }
        }
    }

    /**
     * 只剪系统垃圾目录。mac 版走的是整棵树，蓝光光盘的 BDMV/STREAM、分盘目录都要走进去，
     * 主片留哪一条由 BluRayStreams 在整盘走完之后再决定。
     */
    private static boolean isSkippableFolder(String name) {
        String lower = name.toLowerCase(Locale.ROOT);
        return lower.equals("lost+found") || lower.equals("lost & found")
                || lower.equals("@eadir") || lower.endsWith(".nomedia");
    }

    private static String relativeOf(File root, File file) {
        String rootPath = root.getAbsolutePath();
        String filePath = file.getAbsolutePath();
        if (filePath.startsWith(rootPath)) {
            String relative = filePath.substring(rootPath.length());
            while (relative.startsWith("/")) {
                relative = relative.substring(1);
            }
            return relative;
        }
        return file.getName();
    }

    private void scanWebDavSource(Source source, Set<String> fileIds,
                                  ScanReport report, ProgressListener listener,
                                  int sourceIndex, int sourceTotal) throws IOException {
        WebDavClient client = webDavClient(source);
        ScanCollector collector = new ScanCollector(source, fileIds, report, listener, sourceIndex, sourceTotal);
        client.walk(collector::offer);
        collector.finish();
    }

    private void scanSmbSource(Source source, Set<String> fileIds,
                               ScanReport report, ProgressListener listener,
                               int sourceIndex, int sourceTotal) throws IOException {
        SmbClient client = smbClient(source);
        ScanCollector collector = new ScanCollector(source, fileIds, report, listener, sourceIndex, sourceTotal);
        client.walk(collector::offer);
        collector.finish();
    }

    /**
     * 一次扫描的入库分流：普通视频文件直接登记，蓝光光盘的分卷先按盘攒起来，
     * 走完这个源再只登记主片。被跳过的分卷不会进 currentFileIds，所以上一次扫描
     * 留下的「每条 m2ts 一个条目」也会在同一个清理步骤里自动消失。
     */
    private final class ScanCollector {
        private final Source source;
        private final Set<String> fileIds;
        private final ScanReport report;
        private final ProgressListener listener;
        private final int sourceIndex;
        private final int sourceTotal;
        private long nextPaintAt;
        private boolean aborted;
        private final Map<String, List<BluRayStreams.Candidate>> discs = new LinkedHashMap<>();
        /** 距离上一次「扫描途中挤的刮削」又登记了多少条新文件。 */
        private int newSincePump;
        /** 这个源上一轮就入库的相对路径，本轮只用来确认「还在」，不再重走一遍入库。 */
        private final Set<String> knownPaths = new HashSet<>();
        /**
         * 本轮已经见过的路径。与上面那份分开记：knownPaths 管「要不要重新入库」，这一份管
         * 「同一个文件本轮只算一次，也不重复入库」。合成一份的话，上一轮就入库的文件会被
         * 当成重复直接挡掉，「已扫描 N 个文件」就会写成 0。
         */
        private final Set<String> seenThisRound = new HashSet<>();

        ScanCollector(Source source, Set<String> fileIds, ScanReport report,
                      ProgressListener listener, int sourceIndex, int sourceTotal) {
            this.source = source;
            this.fileIds = fileIds;
            this.report = report;
            this.listener = listener;
            this.sourceIndex = sourceIndex;
            this.sourceTotal = sourceTotal;
            knownPaths.addAll(library.knownRelativePaths(source.id));
        }

        void offer(String fileName, String relativePath, long sizeBytes) {
            if (BluRayStreams.isDiscPath(relativePath)) {
                if (!BluRayStreams.isStreamPath(relativePath)) {
                    // BDMV 里不在 STREAM 下的文件（播放列表、CLIPINF）不是可播放的影音文件。
                    return;
                }
                List<BluRayStreams.Candidate> group = discs.get(BluRayStreams.discFolderOf(relativePath));
                if (group == null) {
                    group = new ArrayList<>();
                    discs.put(BluRayStreams.discFolderOf(relativePath), group);
                }
                group.add(new BluRayStreams.Candidate(fileName, relativePath, sizeBytes));
                return;
            }
            register(relativePath, fileName);
        }

        void finish() {
            if (aborted) {
                // 中途被移除：这源攒下来的蓝光分卷就别再登记了。
                discs.clear();
                throw new ScanAborted(source.name);
            }
            for (List<BluRayStreams.Candidate> streams : discs.values()) {
                List<BluRayStreams.Candidate> keep = BluRayStreams.selectMainFeatures(streams);
                for (BluRayStreams.Candidate stream : keep) {
                    register(stream.relativePath, stream.fileName);
                }
                report.skippedStreams += Math.max(0, streams.size() - keep.size());
            }
            discs.clear();
        }

        private void register(String relativePath, String fileName) {
            if (abortRequested || !liveSourceIds.contains(source.id)) {
                // 遍历过程中用户把这一源移除了：一个文件都别再登记，并把整轮遍历停在这里。
                aborted = true;
                throw new ScanAborted(source.name);
            }
            // 同一个目录本轮被列到第二次（服务器把目录自己也回一条、共享里有环回链接）时，
            // 这一条出口把重复条目挡掉：「已扫描 N 个文件」只按盘上真有多少个文件往上走，
            // 不会空转成十几万，也不会把上一轮就入库的那批漏计成 0。
            if (!seenThisRound.add(relativePath)) {
                return;
            }
            // 「这个路径本轮还在」也要记账，后面每一条出口都要走到：收尾的 removeMissingFiles
            // 就靠这份名单判断谁真的从盘上消失了，漏记一次就等于把整面海报墙删掉。
            fileIds.add(fileId(source.id, relativePath));
            report.scannedFiles++;
            if (knownPaths.contains(relativePath)) {
                // 上一轮就入库的文件：路径没变就是同一个文件，实体不重绑、文件行不重插、
                // 刮削队列也不重填。一万条记录重复这一套要跑十几分钟，刮削永远排不上队。
                paintProgress();
                return;
            }
            knownPaths.add(relativePath);
            report.newFiles++;
            paintProgress();
            registerFile(source, relativePath, fileName,
                    LocalMediaNameParser.parseEpisode(relativePath, 1), fileIds, report, listener);
            // 边走路边刮：攒够一批新条目就挤一小段时间出去刮，海报墙不用等整轮遍历走完。
            if (++newSincePump >= SCRAPE_PUMP_AFTER_FILES) {
                newSincePump = 0;
                pumpScrapeDuringScan(listener);
            }
        }

        /**
         * 大目录要扫几分钟，光靠「扫描中 (1/2)：源名」一句不动，用户只能猜。这里每 400ms
         * 补一句已发现多少个文件；比那更密的进度只是白刷主线程。
         */
        private void paintProgress() {
            long now = android.os.SystemClock.elapsedRealtime();
            if (listener == null || now < nextPaintAt) {
                return;
            }
            nextPaintAt = now + 400;
            progress(listener, "扫描中 (" + sourceIndex + "/" + sourceTotal + ")：" + source.name
                    + " · 已扫描 " + report.scannedFiles + " 个文件，新增 " + report.newFiles);
        }
    }

    private void registerFile(Source source, String relativePath, String fileName,
                              LocalMediaNameParser.EpisodeInfo episode,
                              Set<String> fileIds, ScanReport report, ProgressListener listener) {
        if (!liveSourceIds.contains(source.id)) {
            // 扫描进行中该源被移除了：这一轮还在往里灌文件，先停住，收尾会把它留下的行一起清掉。
            return;
        }
        String fileId = fileId(source.id, relativePath);
        boolean isTv = episode.isTvShow;
        String group = isTv
                ? WebDavClient.groupFolderFor(relativePath)
                : "";
        LocalMediaNameParser.SearchMetadata metadata =
                LocalMediaNameParser.extractSearchMetadata(relativePath.isEmpty() ? fileName : relativePath);
        // 入库名照 mac 版取「看得懂的片名」，而不是整条场景命名；未刮削时海报下就是它。
        String prettyName = LocalMediaNameParser.displayTitle(relativePath, fileName);
        // 剧集按「剧」建条目，标题必须每一集都算出同一个值，所以只用剧集目录名推导。
        String showName = group.isEmpty() ? null : LocalMediaNameParser.displayTitle(group, group);

        long entityId;
        String entityTitle;
        if (isTv) {
            entityTitle = showName != null ? showName
                    : (!group.isEmpty() ? group : LocalMediaNameParser.stripDisplay(fileName));
            entityId = library.bindEntityForFile("tv", fileId, entityTitle);
            report.newEntities++;
            library.insertVideoFile(
                    new LocalMediaLibrary.ScannedFile(fileId, relativePath, fileName,
                            isTv, episode.season, episode.episode, 1),
                    0, entityId, "tv");
            queueScrape("tv", entityId, metadata, entityTitle, episode.season, fileId);
        } else {
            // 文件名算不出能看的名字时，退回「清洗过的文件名」；条目已存在的话由库里那条改名逻辑负责。
            entityTitle = prettyName != null ? prettyName : LocalMediaNameParser.stripDisplay(fileName);
            entityId = library.bindEntityForFile("movie", fileId, entityTitle);
            report.newEntities++;
            library.insertVideoFile(
                    new LocalMediaLibrary.ScannedFile(fileId, relativePath, fileName,
                            isTv, 0, 1, 1),
                    entityId, 0, "movie");
            queueScrape("movie", entityId, metadata, entityTitle, 0, fileId);
        }
    }

    /**
     * 扫描时给每个条目排一次刮削。以前只有从路径里解析出结构化片名（中 / 英文）才入队，
     * 可「电影/某部片.mkv」这类目录名解析不出中英文两个字段，于是这些条目从来没进过队列——
     * 扫描照跑、横幅照报「扫描完成」，海报却一张也不会多，只有详情页手动匹配能救回来。
     * 现在退一步用「入库那个看得懂的片名」当查询词，至少让每个条目都有被刮到的机会。
     */
    private void queueScrape(String mediaType, long entityId,
                             LocalMediaNameParser.SearchMetadata metadata,
                             String fallbackTitle, int season, String fileId) {
        String primary = metadata.chineseTitle != null ? metadata.chineseTitle : metadata.foreignTitle;
        String secondary = metadata.chineseTitle != null ? metadata.foreignTitle : metadata.chineseTitle;
        if (primary == null || primary.trim().isEmpty()) {
            primary = fallbackTitle == null ? "" : fallbackTitle.trim();
        }
        if (primary.trim().isEmpty()) {
            return;
        }
        library.enqueueScrape(mediaType, entityId, primary.trim(), metadata.year,
                secondary, season, fileId);
    }

    private static String fileId(int sourceId, String relativePath) {
        return "local-" + sourceId + ":" + relativePath.replace('/', '-');
    }

    // ------------------------------------------------------------- library models

    /** 首页那个「N 部」用这条轻查询，不必为了一个数字把整库重读一遍。 */
    public int libraryItemCount() {
        return library.entityCount(enabledSourceIds());
    }

    public List<Models.LibraryItem> libraryItems() {
        List<Models.LibraryItem> result = new ArrayList<>();
        List<LocalMediaLibrary.StoredEntity> entities = library.allEntities();
        Map<String, Double> doubanRatings = library.doubanRatings();
        java.util.Set<Integer> enabled = enabledSourceIds();
        Map<Long, List<LocalMediaLibrary.StoredFile>> movieFiles = library.allFilesGrouped(false, enabled);
        Map<Long, List<LocalMediaLibrary.StoredFile>> showFiles = library.allFilesGrouped(true, enabled);
        for (LocalMediaLibrary.StoredEntity entity : entities) {
            List<LocalMediaLibrary.StoredFile> files = entity.isTv
                    ? showFiles.get(entity.id) : movieFiles.get(entity.id);
            if (files == null || files.isEmpty()) {
                continue;
            }
            double maxProgress = 0;
            double maxDuration = 0;
            boolean allWatched = true;
            for (LocalMediaLibrary.StoredFile file : files) {
                maxDuration = Math.max(maxDuration, file.durationSeconds);
                if (!file.isWatched) {
                    allWatched = false;
                }
                double progress = Math.min(file.positionSeconds,
                        file.durationSeconds > 0 ? file.durationSeconds : file.positionSeconds);
                maxProgress = Math.max(maxProgress, progress);
            }
            Double vote = entity.voteAverage > 0 ? entity.voteAverage : null;
            Models.LibraryItem card = new Models.LibraryItem(
                    (entity.isTv ? "tv:" : "movie:") + entity.id,
                    entity.isTv ? "tv" : "movie",
                    entity.title,
                    entity.releaseDate,
                    entity.overview,
                    entity.posterUrl,
                    vote,
                    allWatched && !files.isEmpty(),
                    files.size(),
                    maxProgress,
                    maxDuration);
            // 豆瓣评分就挂在 TMDB 评分右边那一颗绿角标上，没绑过豆瓣的条目是 null。
            card.doubanRating = doubanRatings.get(
                    (entity.isTv ? "tv#" : "movie#") + entity.id);
            result.add(card);
        }
        result.sort((a, b) -> a.title == null || b.title == null
                ? 0 : a.title.compareToIgnoreCase(b.title));
        return result;
    }

    // ------------------------------------------------------------- 豆瓣

    /** 把用户贴的链接 / 编号换成一条豆瓣条目，存下来并补上条目缺的那部分资料。 */
    public Models.DoubanMetadata bindDouban(String mediaType, long entityId, String input)
            throws IOException {
        DoubanClient.Subject subject = douban.fetch(input);
        if (subject.isPlaceholder()) {
            throw new IOException("这个豆瓣页面看起来不是一部影视的条目，换一个链接再试。");
        }
        library.saveDouban(mediaType, entityId, subject);
        return doubanMetadata(mediaType, entityId);
    }

    /** 条目当前绑着的豆瓣资料；没绑过是 null。 */
    public Models.DoubanMetadata doubanMetadata(String mediaType, long entityId) {
        LocalMediaLibrary.DoubanRow row = library.doubanRow(mediaType, entityId);
        if (row == null) {
            return null;
        }
        return new Models.DoubanMetadata(row.subjectId, row.subjectUrl, row.title,
                row.originalTitle, row.year, row.rating > 0 ? row.rating : null,
                row.ratingCount > 0 ? row.ratingCount : null, row.summary, row.genres,
                row.countries, row.posterUrl);
    }

    public void unbindDouban(String mediaType, long entityId) {
        library.deleteDouban(mediaType, entityId);
    }

    /** 抓一条豆瓣条目要多久才能再试（0 = 现在就能抓）。 */
    public long doubanCooldownUntilMillis() {
        return douban.cooldownUntilMillis();
    }

    public Models.LibraryDetail detail(String itemId) {
        int colon = itemId.indexOf(':');
        if (colon < 0) {
            return null;
        }
        String kind = itemId.substring(0, colon);
        long entityId;
        try {
            entityId = Long.parseLong(itemId.substring(colon + 1));
        } catch (NumberFormatException ignored) {
            return null;
        }
        LocalMediaLibrary.StoredEntity entity = library.entity(kind, entityId);
        if (entity == null) {
            return null;
        }
        List<LocalMediaLibrary.StoredFile> stored = kind.equals("tv")
                ? library.filesForShow(entityId)
                : library.filesForMovie(entityId);
        if (stored.isEmpty()) {
            return null;
        }

        double maxProgress = 0;
        double maxDuration = 0;
        boolean allWatched = true;
        List<Models.VideoFile> movieFiles = new ArrayList<>();
        Map<Integer, List<LocalMediaLibrary.StoredFile>> bySeason = new LinkedHashMap<>();
        Map<Integer, String> manualEpisodeSubtitles = new HashMap<>();
        for (LocalMediaLibrary.StoredFile file : stored) {
            double progress = Math.min(file.positionSeconds,
                    file.durationSeconds > 0 ? file.durationSeconds : file.positionSeconds);
            maxDuration = Math.max(maxDuration, file.durationSeconds);
            maxProgress = Math.max(maxProgress, progress);
            if (!file.isWatched) {
                allWatched = false;
            }
            if (kind.equals("tv")) {
                if (!file.episodeSubtitle.isEmpty()) {
                    manualEpisodeSubtitles.put(
                            LocalMediaLibrary.episodeKey(file.season, file.episode),
                            file.episodeSubtitle);
                }
                // S00 就是特别篇，不再并进第 1 季：它是单独一节，排在所有正季后面。
                bySeason.computeIfAbsent(file.season, key -> new ArrayList<>()).add(file);
            } else {
                movieFiles.add(toVideoFile(file));
            }
        }

        Double vote = entity.voteAverage > 0 ? entity.voteAverage : null;
        String poster = entity.posterUrl.isEmpty() ? null : entity.posterUrl;
        List<String> genres = splitList(entity.genres);
        List<String> directors = splitList(entity.directors);
        List<String> casts = splitList(entity.casts);

        if (kind.equals("tv")) {
            Map<Integer, LocalMediaLibrary.EpisodeMetadata> episodeMeta = library.episodeMetadata(entityId);
            List<Models.Season> seasons = new ArrayList<>();
            List<Integer> seasonNumbers = new ArrayList<>(bySeason.keySet());
            seasonNumbers.sort(java.util.Comparator
                    .comparingInt((Integer number) -> number == 0 ? 1 : 0)
                    .thenComparingInt(number -> number));
            for (int seasonNumber : seasonNumbers) {
                List<LocalMediaLibrary.StoredFile> seasonFiles = bySeason.get(seasonNumber);
                seasonFiles.sort(Comparator
                        .<LocalMediaLibrary.StoredFile>comparingInt(f -> f.episode == 0 ? 1 : f.episode)
                        .thenComparing(f -> f.fileName == null ? "" : f.fileName, String.CASE_INSENSITIVE_ORDER));
                List<Models.Episode> episodes = new ArrayList<>();
                List<Models.VideoFile> flat = new ArrayList<>();
                int index = 0;
                for (LocalMediaLibrary.StoredFile file : seasonFiles) {
                    index++;
                    Models.VideoFile video = toVideoFile(file);
                    String label = "S" + pad2(seasonNumber) + "E" + pad2(file.episode == 0 ? index : file.episode);
                    flat.add(video.withEpisodeLabel(label));
                }
                // One episode card per (episode, version-group): keep every version in the card.
                Map<Integer, List<Models.VideoFile>> byEpisode = new LinkedHashMap<>();
                for (Models.VideoFile video : flat) {
                    int episodeNumber = video.episodeNumber == null ? 0 : video.episodeNumber;
                    byEpisode.computeIfAbsent(episodeNumber, key -> new ArrayList<>()).add(video);
                }
                int fallback = 0;
                for (Map.Entry<Integer, List<Models.VideoFile>> entry : byEpisode.entrySet()) {
                    fallback++;
                    List<Models.VideoFile> versions = entry.getValue();
                    int episodeNumber = entry.getKey() == 0 ? fallback : entry.getKey();
                    int metaKey = LocalMediaLibrary.episodeKey(seasonNumber, episodeNumber);
                    LocalMediaLibrary.EpisodeMetadata meta = episodeMeta.get(metaKey);
                    String override = manualEpisodeSubtitles.get(metaKey);
                    String episodeTitle = override != null && !override.isEmpty()
                            ? override
                            : (meta != null && !meta.title.isEmpty() ? meta.title : versions.get(0).episodeTitle);
                    String still = meta != null && !meta.stillUrl.isEmpty() ? meta.stillUrl : null;
                    episodes.add(new Models.Episode(
                            "ep-" + entityId + "-" + seasonNumber + "-" + episodeNumber,
                            seasonNumber,
                            episodeNumber,
                            episodeTitle,
                            meta == null || meta.overview.isEmpty() ? null : meta.overview,
                            meta == null || meta.airDate.isEmpty() ? null : meta.airDate,
                            still,
                            versions.get(0),
                            versions,
                            override == null || override.isEmpty() ? null : override));
                }
                seasons.add(new Models.Season(
                        "season-" + entityId + "-" + seasonNumber,
                        seasonNumber,
                        seasonNumber == 0 ? "特别篇" : "第 " + seasonNumber + " 季",
                        poster,
                        episodes));
            }
            return withDouban(Models.LibraryDetail.local(itemId, "tv", entity.title,
                    entity.releaseDate,
                    entity.overview, poster, vote, allWatched, stored.size(), maxProgress, maxDuration,
                    new ArrayList<>(), seasons, genres, directors, casts), kind, entityId);
        }

        movieFiles.sort(Comparator.comparing(f -> f.fileName == null ? "" : f.fileName, String.CASE_INSENSITIVE_ORDER));
        return withDouban(Models.LibraryDetail.local(itemId, "movie", entity.title,
                entity.releaseDate,
                entity.overview, poster, vote, allWatched, stored.size(), maxProgress, maxDuration,
                movieFiles, new ArrayList<>(), genres, directors, casts), kind, entityId);
    }

    /** 详情页那份豆瓣资料：绑过就带上，评分、年份、类型、简介都从它兜底。 */
    private Models.LibraryDetail withDouban(Models.LibraryDetail detail, String mediaType,
                                            long entityId) {
        LocalMediaLibrary.DoubanRow row = library.doubanRow(mediaType, entityId);
        if (row != null) {
            detail.douban = new Models.DoubanMetadata(row.subjectId, row.subjectUrl, row.title,
                    row.originalTitle, row.year, row.rating > 0 ? row.rating : null,
                    row.ratingCount > 0 ? row.ratingCount : null, row.summary, row.genres,
                    row.countries, row.posterUrl);
            detail.doubanRating = row.rating > 0 ? row.rating : null;
        }
        return detail;
    }

    private static List<String> splitList(String value) {
        if (value == null || value.isEmpty()) {
            return Collections.emptyList();
        }
        List<String> parts = new ArrayList<>();
        for (String part : value.split("\\|")) {
            if (!part.trim().isEmpty()) {
                parts.add(part.trim());
            }
        }
        return parts;
    }

    private Models.VideoFile toVideoFile(LocalMediaLibrary.StoredFile file) {
        String cached = cache.cachedPathFor(file.id);
        String url = cached != null ? "file://" + cached : playbackUrlFor(file);
        return Models.VideoFile.local(
                file.id,
                file.relativePath,
                file.fileName,
                file.mediaKind,
                file.season,
                file.episode,
                file.episodeTitle,
                file.positionSeconds,
                file.durationSeconds,
                file.isWatched,
                url);
    }

    private String playbackUrlFor(LocalMediaLibrary.StoredFile file) {
        String raw = file.relativePath;
        if (raw.startsWith("/")) {
            return "file://" + raw;
        }
        // Relative paths are relative to the source they were scanned from. Handing mpv one as-is
        // makes it resolve against "/", so local files never open.
        for (Source source : sources()) {
            if ("local".equals(source.kind) && !source.path.isEmpty()
                    && new File(source.path, raw).isFile()) {
                return "file://" + new File(source.path, raw).getAbsolutePath();
            }
        }
        for (Source source : sources()) {
            if (source.kind.equals("webdav")) {
                try {
                    return webDavClient(source).resolvePlaybackUrl(raw);
                } catch (Exception ignored) {
                }
            }
            if (source.kind.equals("smb")) {
                try {
                    return smbClient(source).absoluteUrl(raw);
                } catch (Exception ignored) {
                }
            }
        }
        return "file://" + raw;
    }

    // ------------------------------------------------------------- playback state

    public void clearLibrary() {
        library.clearLibrary();
    }

    /** Scan, then scrape, as one action: what the mac app does on startup and on 同步. */
    public ScanReport scanAndScrape(ProgressListener listener) {
        resetScrapeTally();
        // 上一轮留下的中断标记要清掉，否则这一轮刚起步就被自己叫停。
        abortRequested = false;
        ScanReport report = scanAll(listener);
        if (!report.success || !settings.autoScrapeAfterScan()) {
            return report;
        }
        if (!tmdb.isConfigured()) {
            // 扫完就把「等 TMDB」这件事说出来，别让它变成一条看不懂的待刮削计数。
            if (library.scrapeQueueCount() > 0) {
                progress(listener, "等待 TMDB API 连通后刮削...");
            }
            return report;
        }
        // 扫描摘要先说一声，随后才轮到刮削进度盖上去（与 mac 版同一顺序）。
        progress(listener, report.message);
        redecideStaleResults(listener);
        // 扫描途中那一次「挤刮削」早把队列行整批取走了，只数行数会把这一句判成
        // 「没活可干」：先按库里的未刮削实体补回队列，再决定要不要开跑。
        library.requeueUnscraped();
        if (library.scrapeQueueCount() > 0) {
            scrapePending(listener);
        }
        return report;
    }

    /** 名称 / 年份清洗规则换代一次就把版本号往前推这里一档。 */
    private static final int SCRAPE_RULE_EPOCH = 31;
    /** 一次重判最多看这么多条目，老库再大也只是一次性的开销。 */
    private static final int RECHECK_LIMIT = 5000;

    /**
     * 规则换代后重新判定旧结果（只跑一次）。已经刮完的条目 ordinarily 再也不会进队列，
     * 可老规则留下的错名字、错年份会一直挂在墙上：Signal 刮成 2017、
     * Millennium Actress 刮成「The Making of Millennium Actress」就是这么来的。
     * 拿现在的规则重算一遍，年份对不上、或者片名与关键词毫无关系的，重新排一次队。
     */
    private int redecideStaleResults(ProgressListener listener) {
        if (settings.scrapeRuleEpoch() >= SCRAPE_RULE_EPOCH) {
            return 0;
        }
        int requeued = 0;
        for (Map<String, Object> row : library.recheckCandidates(RECHECK_LIMIT)) {
            String mediaType = (String) row.get("mediaType");
            long entityId = (Long) row.get("entityId");
            String title = (String) row.get("title");
            String releaseDate = (String) row.get("releaseDate");
            LocalMediaNameParser.SearchMetadata meta =
                    LocalMediaNameParser.combinedSearchMetadata(
                            (String) row.get("relativePath"), (String) row.get("fileName"));
            String wanted = meta.chineseTitle != null ? meta.chineseTitle : meta.foreignTitle;
            String storedYear = releaseDate.length() >= 4 ? releaseDate.substring(0, 4) : "";
            boolean yearOff = meta.year != null && meta.year.length() == 4
                    && storedYear.length() == 4 && !meta.year.equals(storedYear);
            // 只挑「外文名和关键词毫不相干」的：已经换成中文译名的条目是正确答案，别去动它。
            boolean titleOff = wanted != null
                    && !LocalMediaNameParser.containsChinese(title)
                    && !sameLooseTitle(title, wanted);
            if (!yearOff && !titleOff) {
                continue;
            }
            library.enqueueScrape(mediaType, entityId, wanted == null ? title : wanted,
                    meta.year == null ? "" : meta.year,
                    meta.chineseTitle != null ? meta.foreignTitle : meta.chineseTitle,
                    (Integer) row.get("season"), (String) row.get("fileId"), true);
            requeued++;
        }
        settings.setScrapeRuleEpoch(SCRAPE_RULE_EPOCH);
        if (requeued > 0) {
            progress(listener, "按最新规则重新核对了 " + requeued + " 个条目，正在重新刮削...");
        }
        return requeued;
    }

    /** 忽略标点与大小写比较两个片名（与 TMDB 打分里那同一套）。 */
    private static boolean sameLooseTitle(String left, String right) {
        String a = looseTitleChars(left);
        String b = looseTitleChars(right);
        return !a.isEmpty() && a.equals(b);
    }

    private static String looseTitleChars(String value) {
        if (value == null) {
            return "";
        }
        StringBuilder out = new StringBuilder(value.length());
        for (int index = 0; index < value.length(); index++) {
            char c = value.charAt(index);
            if (Character.isLetterOrDigit(c)) {
                out.append(Character.toLowerCase(c));
            }
        }
        return out.toString();
    }

    public void updateProgress(String fileId, double positionSeconds, double durationSeconds) {
        library.updatePlaybackState(fileId, positionSeconds, durationSeconds);
    }

    public void setWatched(String fileId, boolean watched, double durationSeconds) {
        library.setWatched(fileId, watched, durationSeconds);
    }

    // ------------------------------------------------------------- scraping

    public boolean isScraping() {
        return scraping;
    }

    public String scrapeStatus() {
        return scrapeStatus;
    }

    public int scrapeQueueCount() {
        return library.scrapeQueueCount();
    }

    public boolean tmdbConfigured() {
        return tmdb.isConfigured();
    }

    public void scrapePending(ProgressListener listener) {
        scrape(listener, 0, true);
    }

    /** 清那本账：一次用户动作（点扫描刮削、挂载新源）从头算起。 */
    private void resetScrapeTally() {
        tallySeen.clear();
        tallyUpdated = 0;
        tallyFailed = 0;
        tallyUnmatched = 0;
        tallyYearMismatch = 0;
        tallySkipped = 0;
        tallyMerged = 0;
    }

    /**
     * 扫描途中挤一小段刮削：大目录走一趟要几分钟，海报不该等整轮遍历走完才出现。只花
     * {@link #SCRAPE_PUMP_BUDGET_MS} 那么多时间，用完就把剩下的留给后面几轮，遍历不会被按住。
     */
    private void pumpScrapeDuringScan(ProgressListener listener) {
        if (abortRequested || !settings.autoScrapeAfterScan() || !tmdb.isConfigured()) {
            return;
        }
        // 同上：先补队列再问有没有活，否则预算用完那半批会让后面每一轮都以为无事可做。
        library.requeueUnscraped();
        if (library.scrapeQueueCount() == 0) {
            return;
        }
        scrape(listener, SCRAPE_PUMP_BUDGET_MS, false);
    }

    /** {@code budgetMs} 为 0 是一路刮到队列见底；{@code summarize} 决定要不要报那句总结。 */
    private void scrape(ProgressListener listener, long budgetMs, boolean summarize) {
        if (!tmdb.isConfigured()) {
            if (library.scrapeQueueCount() > 0) {
                // mac 版这时候说的是「等待 TMDB API 连通后刮削」，而不是干挂一个待刮削数字。
                progress(listener, "等待 TMDB API 连通后刮削...");
            }
            return;
        }
        // 两个线程同时开跑会各自取一批又互相删行，状态行的「待刮削 N」就会来回跳。
        synchronized (scrapeLock) {
            if (scraping) {
                return;
            }
            scraping = true;
        }
        try {
            // 先补队列再动队列行：上一轮请求失败的条目，行已经被取走了，不补回来它会永远
            // 停在占位字母上（mac 版每轮现算未刮削清单，不存在这个问题）。
            library.requeueUnscraped();
            if (summarize) {
                progress(listener, "检测 TMDB 连通性...");
            }
            TmdbClient.CheckResult probe = tmdb.checkConnectionCached();
            if (!probe.connected) {
                // 队列行还整整齐齐在表里，「待刮削 N」继续挂着，原因写在这一行上。
                progress(listener, "TMDB 无法访问：" + probe.message);
                return;
            }
            // 一次取一批，取完接着取，直到队列真的见底：中途扫描还在往队列里补条目，
            // 只要「取到不满一批」就退出，剩下那截就会一直挂在首页上不动。
            long deadlineAt = budgetMs > 0
                    ? android.os.SystemClock.elapsedRealtime() + budgetMs : 0;
            boolean outOfBudget = false;
            int done = 0;
            int totalQueued = library.scrapeQueueCount();
            for (int round = 0; round < 100; round++) {
                if (abortRequested) {
                    // 移除了媒体源：没刮的条目仍是未刮削状态，下一轮 requeueUnscraped 会补回队列。
                    break;
                }
                List<Map<String, Object>> batches = library.nextScrapeBatches(SCRAPE_BATCH);
                if (batches.isEmpty()) {
                    break;
                }
                if (batches.size() > totalQueued) {
                    totalQueued = batches.size();
                }
                int total = batches.size();
                int index = 0;
                for (Map<String, Object> batch : batches) {
                    if (deadlineAt != 0 && android.os.SystemClock.elapsedRealtime() > deadlineAt) {
                        // 扫描途中借出来的这段时间用完了：这一批剩下的条目还是未刮削，
                        // 下一轮开头 requeueUnscraped 会把它们重新排进队列，一条都不会漏。
                        outOfBudget = true;
                        break;
                    }
                    index++;
                    String mediaType = (String) batch.get("mediaType");
                    long entityId = (Long) batch.get("entityId");
                    String query = (String) batch.get("query");
                    String year = (String) batch.get("year");
                    String secondary = (String) batch.get("secondaryQuery");
                    int season = (Integer) batch.get("preferredSeason");
                    String sourceFileId = (String) batch.get("sourceFileId");
                    // 同一个条目可能被抓两轮（扫途挤一次、收尾又来一次）：账只记第一次。
                    boolean firstTry = tallySeen.add(mediaType + "#" + entityId);
                    if (library.isEntityLocked(mediaType, entityId)) {
                        // 手动匹配过或自定义编辑过的条目不再自动覆盖（mac 版同一条规则）；
                        // 队列行在取批次时已经删掉，跳过它不会让「待刮削」卡住。
                        if (firstTry) {
                            tallySkipped++;
                        }
                        continue;
                    }
                    progress(listener, "刮削元数据和海报 (" + (done + index) + "/"
                            + Math.max(totalQueued, done + total) + ")：" + query);
                    activeScrapes.incrementAndGet();
                    try {
                        boolean[] mismatch = new boolean[1];
                        String[] fallback = new String[1];
                        TmdbClient.Result result = scrapeOne(mediaType, entityId, query, year,
                                secondary, season, sourceFileId, deadlineAt, mismatch, fallback);
                        if (result == null) {
                            // 「处理完了」和「刮到了」是两件事：不写清哪一种，用户只能看到海报没变。
                            if (firstTry) {
                                tallyUnmatched++;
                                if (mismatch[0]) {
                                    tallyYearMismatch++;
                                }
                            }
                        } else {
                            library.applyScrapeResult(mediaType, entityId, result);
                            applyTitleDetails(mediaType, entityId, result.id);
                            // 同一部剧的两个季目录各自建了一条实体，刮到同一个 TMDB 作品就地收拢，
                            // 不然首页会一直挂着两张一模一样的海报。
                            tallyMerged += library.collapseDuplicateWorks(
                                    mediaType, entityId, result.id);
                            boolean localized = localizeTitle(mediaType, entityId, result.id,
                                    result.title, deadlineAt);
                            if (!localized) {
                                // TMDB 那头没有中文条目（或别名页没登记），就退到文件名里那个中文片名：
                                // 「空战群英.Masters.of.the.Air…」刮完不该还挂着英文名。
                                library.applyChineseTitleUnlessLocalized(
                                        mediaType, entityId, fallback[0]);
                            }
                            if (firstTry) {
                                tallyUpdated++;
                            }
                        }
                    } catch (Exception error) {
                        if (firstTry) {
                            tallyFailed++;
                        }
                    } finally {
                        activeScrapes.decrementAndGet();
                    }
                }
                // index 就是这一批真正处理掉的条数：中途预算用完时它小于 total，别把
                // 没刮的也算进「共处理」里。
                done += index;
                if (outOfBudget) {
                    // 预算用完，这一批剩下的条目行已经删掉了：赶紧按库里的未刮削实体
                    // 补回队列，「待刮削 N」才不会在这一刻掉回 0。
                    library.requeueUnscraped();
                    break;
                }
            }
            if (summarize && abortRequested) {
                // 中途移除了媒体源：这一轮收尾只说「停了」，别再报一句「刮削完成」。
                progress(listener, "已停止扫描刮削。");
                resetScrapeTally();
            } else if (summarize) {
                // 库里早先已经建重的条目不等下次刮前：每轮收尾整体收拢一次。
                tallyMerged += library.collapseDuplicateWorks("movie", 0)
                        + library.collapseDuplicateWorks("tv", 0);
                int localized = backfillChineseTitles(listener);
                if (localized > 0) {
                    progress(listener, "补了 " + localized + " 个中文译名。");
                }
            }
            if (summarize && !abortRequested && !tallySeen.isEmpty()) {
                List<String> parts = new ArrayList<>();
                if (tallyFailed > 0) {
                    parts.add(tallyFailed + " 个请求失败");
                }
                if (tallyUnmatched > 0) {
                    parts.add(tallyUnmatched + " 个没搜到");
                }
                if (tallyYearMismatch > 0) {
                    parts.add(tallyYearMismatch + " 个年份对不上");
                }
                if (tallySkipped > 0) {
                    parts.add("跳过 " + tallySkipped + " 个手动匹配的条目");
                }
                if (tallyMerged > 0) {
                    parts.add("合并了 " + tallyMerged + " 个重复条目");
                }
                if (parts.isEmpty()) {
                    parts.add("没有匹配得上的条目");
                }
                progress(listener, tallyUpdated > 0
                        ? "刮削完成：更新了 " + tallyUpdated + " 个条目"
                                + (parts.size() == 1 && parts.get(0).equals("没有匹配得上的条目")
                                ? "" : "；" + String.join("，", parts)) + "。"
                        : "刮削完成：这一轮没有匹配到新的资料（"
                                + String.join("，", parts) + "）。");
                resetScrapeTally();
                // 后台批量抽字幕交给媒体源那台转档服务，电视端不再自己抢这份活：扫完只做一件
                // 便宜事——认一认媒体源里已经躺着的外挂字幕。要单独抽某一条，播放页字幕菜单里点。
                int recognised = sidecarRecogniseSweep(12, listener);
                if (recognised > 0) {
                    progress(listener, "字幕转档：" + recognised
                            + " 个文件在媒体源里找到了现成的外挂字幕。");
                }
            }
        } finally {
            synchronized (scrapeLock) {
                scraping = false;
            }
        }
    }

    /**
     * 年份校验（mac 版 isYearPlausibleMatch）：容差一年；剧集直接豁免——剧集文件名里的年份常是
     * 资源年份而不是首播年份，硬约会把正确答案挡掉。年份对不上不算失败，算「这个关键词先用错
     * 了」，由下一个关键词重试。
     */
    private static boolean isYearPlausible(TmdbClient.Result result, String year, String mediaType,
                                           int season) {
        if ("tv".equals(mediaType) || season > 0) {
            return true;
        }
        if (year == null || year.isEmpty()) {
            return true;
        }
        if (result.releaseDate == null || result.releaseDate.length() < 4) {
            return true;
        }
        try {
            int target = Integer.parseInt(year);
            int actual = Integer.parseInt(result.releaseDate.substring(0, 4));
            return Math.abs(actual - target) <= 1;
        } catch (NumberFormatException ignored) {
            return true;
        }
    }

    /**
     * 一条实体的刮前：关键词与年份从代表文件的路径现算，再按 mac 的降级顺序逐个试——
     * 中文名 → 父目录中文名 → 外文名（含 AKA 拆分）→ 完整清洗名 → 去空格去数字的纯名字，
     * 最后才是条目现名。年份对不上就换下一个关键词，全部落空才算没刷到。
     *
     * <p>以前这里只发一次请求，用的还是扫描时存下的那份快照；队列自愈重排时年份取的是
     * 条目自己的上映日期，未刮前条目就是空串——年份权重整段失效，热度最高的同名新作直接抢位。</p>
     */
    private TmdbClient.Result scrapeOne(String mediaType, long entityId, String storedQuery,
                                        String storedYear, String storedSecondary, int season,
                                        String sourceFileId, long deadlineAt,
                                        boolean[] yearMismatch,
                                        String[] chineseFallback) throws Exception {
        List<String[]> attempts = new ArrayList<>();
        String[] file = library.representativeFile(mediaType, entityId, sourceFileId);
        if (file != null) {
            LocalMediaNameParser.SearchMetadata meta =
                    LocalMediaNameParser.combinedSearchMetadata(file[0], file[1]);
            // mac 版写库时用的是 preferredTitle(chineseFallback:)：TMDB 没有中文条目就用
            // 文件名 / 目录名里那个中文片名。这一份要带出去给调用方用。
            if (chineseFallback != null) {
                chineseFallback[0] = meta.chineseTitle != null
                        ? meta.chineseTitle : meta.parentChineseTitle;
            }
            String year = meta.year == null ? "" : meta.year;
            String crossCheck = meta.foreignTitle != null ? meta.foreignTitle : meta.chineseTitle;
            addAttempt(attempts, meta.chineseTitle, year, meta.foreignTitle);
            addAttempt(attempts, meta.parentChineseTitle, year, meta.foreignTitle);
            for (String candidate : TmdbClient.foreignQueryCandidates(meta.foreignTitle)) {
                addAttempt(attempts, candidate, year, meta.chineseTitle);
            }
            addAttempt(attempts, meta.fullCleanTitle, year, crossCheck);
            addAttempt(attempts, meta.fullCleanTitle == null ? null
                    : meta.fullCleanTitle.replaceAll("[ \\d]", ""), "", crossCheck);
        }
        // 条目现名永远是最后一根救命稻：自定义编辑改过名、或者路径根本清洗不出名字的时候靠它。
        addAttempt(attempts, storedQuery, storedYear == null ? "" : storedYear, storedSecondary);
        java.util.Set<String> tried = new java.util.HashSet<>();
        for (String[] attempt : attempts) {
            String query = attempt[0] == null ? "" : attempt[0].trim();
            if (query.isEmpty() || !tried.add(query.toLowerCase(java.util.Locale.ROOT))) {
                continue;
            }
            if (deadlineAt != 0
                    && android.os.SystemClock.elapsedRealtime() > deadlineAt) {
                return null;
            }
            TmdbClient.Result result = tmdb.multiSearch(query, attempt[1], mediaType, season,
                    attempt[2]);
            if (result == null) {
                continue;
            }
            if (!isYearPlausible(result, attempt[1], mediaType, season)) {
                yearMismatch[0] = true;
                continue;
            }
            return result;
        }
        return null;
    }

    private static void addAttempt(List<String[]> attempts, String query, String year,
                                   String secondary) {
        if (query == null || query.trim().isEmpty()) {
            return;
        }
        attempts.add(new String[]{query.trim(), year == null ? "" : year,
                secondary == null ? "" : secondary});
    }

    public String tmdbStatusMessage() {
        if (!tmdb.isConfigured()) {
            return "未配置 TMDB：请在设置中填写 API Key，或使用 TMDB 反代地址免 Key 使用。";
        }
        return "TMDB 已配置" + (tmdb.proxyUrl().isEmpty() ? "（官方接口）" : "（反代）");
    }

    /** Genres / directors / casts for the detail page, fetched right after a match. */
    /** 详情页「手动匹配」的候选清单：与自动刮削同一套打分，按分数从高到低整份交出去。 */
    public List<TmdbClient.Result> searchCandidates(String query, String year, String mediaType)
            throws Exception {
        return tmdb.searchCandidates(query, year, mediaType, 0, "", 24);
    }

    /**
     * 手动匹配的豆瓣候选：不吃 API Key，TMDB 没配置的时候它就是另一条路。
     * 点中某一行之后仍然走 bindDouban，那里会去抓完整条目页补全资料。
     */
    public List<DoubanClient.Hit> searchDoubanCandidates(String query) throws IOException {
        return douban.search(query, 12);
    }

    /**
     * 用户在候选清单里挑了一条：写进实体、补齐导演 / 主演，并锁住这条记录，
     * 之后整库扫描不会再覆盖它。返回之后该用的实体 id（跨类型改绑时 id 会变）。
     */
    public long bindToTmdbResult(String mediaType, long entityId, TmdbClient.Result result) {
        String genres = "";
        String directors = "";
        String casts = "";
        if (result.id > 0) {
            TmdbClient.Details details = tmdb.details(result.mediaType, result.id);
            if (details != null) {
                genres = joinByPipe(details.genres);
                directors = joinByPipe(details.directors);
                casts = joinByPipe(details.casts);
            }
        }
        long targetId =
                library.applyManualMatch(mediaType, entityId, result, genres, directors, casts);
        // 手动挑中这条只说明「就是这部片」，不代表要留着外文名：照样换成官方中文译名。
        String kind = "tv".equals(result.mediaType) ? "tv" : "movie";
        if (!localizeTitle(kind, targetId, result.id, result.title, 0)) {
            String[] file = library.representativeFile(kind, targetId, null);
            if (file != null) {
                LocalMediaNameParser.SearchMetadata meta =
                        LocalMediaNameParser.combinedSearchMetadata(file[0], file[1]);
                library.applyChineseTitleUnlessLocalized(kind, targetId,
                        meta.chineseTitle != null ? meta.chineseTitle : meta.parentChineseTitle);
            }
        }
        return targetId;
    }

    /**
     * 外文片名换中文译名（mac 版 ChineseNameStore 那条链路）：查询结果连同「TMDB 确实
     * 没有中文条目」一起记进缓存，只有请求失败才留到下一轮重试。刮削预算用完时直接跳过，
     * 这一条下一轮还会排进队列。
     */
    private boolean localizeTitle(String mediaType, long entityId, long tmdbId,
                                  String currentTitle, long deadlineAt) {
        if (tmdbId <= 0 || !tmdb.isConfigured() || !"zh-CN".equals(tmdb.language())
                || LocalMediaNameParser.containsChinese(currentTitle)) {
            return false;
        }
        if (deadlineAt != 0 && android.os.SystemClock.elapsedRealtime() > deadlineAt) {
            return false;
        }
        if (library.chineseTitleCached(mediaType, tmdbId)) {
            return applyChineseTitle(mediaType, entityId,
                    library.cachedChineseTitle(mediaType, tmdbId), currentTitle);
        }
        TmdbClient.ChineseTitleResult remote = tmdb.fetchChineseTitle(mediaType, tmdbId);
        if (remote.requestFailed) {
            return false;
        }
        library.rememberChineseTitle(mediaType, tmdbId, remote.title);
        return applyChineseTitle(mediaType, entityId, remote.title, currentTitle);
    }

    private boolean applyChineseTitle(String mediaType, long entityId, String chineseTitle,
                                      String currentTitle) {
        if (chineseTitle.isEmpty() || chineseTitle.equals(currentTitle)) {
            return false;
        }
        library.applyLocalizedTitle(mediaType, entityId, chineseTitle);
        return true;
    }

    /** 老库里已经刮成外文库名的条目：一轮最多补这么多条，负缓存挡住反复无果的请求。 */
    private int backfillChineseTitles(ProgressListener listener) {
        if (!tmdb.isConfigured() || !"zh-CN".equals(tmdb.language())) {
            return 0;
        }
        int localized = 0;
        for (Map<String, Object> candidate : library.localizationCandidates(40)) {
            String mediaType = (String) candidate.get("mediaType");
            long entityId = (Long) candidate.get("entityId");
            long tmdbId = (Long) candidate.get("tmdbId");
            String title = (String) candidate.get("title");
            progress(listener, "补中文译名：" + title);
            if (localizeTitle(mediaType, entityId, tmdbId, title, 0)) {
                localized++;
            }
        }
        return localized;
    }

    private void applyTitleDetails(String mediaType, long entityId, int tmdbId) {
        if (tmdbId <= 0) {
            return;
        }
        TmdbClient.Details details = tmdb.details(mediaType, tmdbId);
        if (details == null) {
            return;
        }
        library.setEntityCredits(mediaType, entityId,
                joinByPipe(details.genres), joinByPipe(details.directors), joinByPipe(details.casts));
    }

    private static String joinByPipe(List<String> values) {
        StringBuilder builder = new StringBuilder();
        for (String value : values) {
            if (builder.length() > 0) {
                builder.append('|');
            }
            builder.append(value);
        }
        return builder.toString();
    }

    // ------------------------------------------------- episode metadata (分集剧照)

    /**
     * True when the season still needs its TMDB episode lookups. Callers run
     * {@link #scrapeEpisodeMetadata} off the main thread when a season is opened.
     */
    public boolean needsEpisodeMetadata(String itemId, int season) {
        long[] ids = entityIds(itemId);
        if (ids == null) {
            return false;
        }
        List<LocalMediaLibrary.StoredFile> files = library.filesForShow(ids[1]);
        int expected = 0;
        for (LocalMediaLibrary.StoredFile file : files) {
            if (file.season == season && file.episode > 0) {
                expected++;
            }
        }
        return expected > 0 && !library.hasEpisodeMetadata(ids[1], season, expected)
                && !episodeScrapeInFlight.contains(itemId + "#" + season);
    }

    /**
     * Fetches TMDB episode entries (name, overview, air date, still) for one season.
     * Misses are stored as empty rows so a season is only fetched once.
     */
    public String scrapeEpisodeMetadata(String itemId, int season, ProgressListener listener) {
        if (!tmdb.isConfigured()) {
            return apiDisabledMessage();
        }
        long[] ids = entityIds(itemId);
        if (ids == null) {
            return "未找到剧集条目。";
        }
        long showId = ids[1];
        String flightKey = itemId + "#" + season;
        if (!episodeScrapeInFlight.add(flightKey)) {
            return null;
        }
        try {
            LocalMediaLibrary.StoredEntity entity = library.entity("tv", showId);
            if (entity == null) {
                return "未找到剧集条目。";
            }
            long tmdbId = entity.tmdbId;
            if (tmdbId <= 0) {
                TmdbClient.Result candidate = tmdb.multiSearch(entity.title,
                        entity.releaseDate.length() >= 4 ? entity.releaseDate.substring(0, 4) : "",
                        "tv", season, null);
                if (candidate == null) {
                    return "TMDB 未匹配到该剧集，无法获取分集信息。";
                }
                library.applyScrapeResult("tv", showId, candidate);
                tmdbId = candidate.id;
            }
            List<Integer> episodeNumbers = new ArrayList<>();
            for (LocalMediaLibrary.StoredFile file : library.filesForShow(showId)) {
                if (file.season == season && file.episode > 0 && !episodeNumbers.contains(file.episode)) {
                    episodeNumbers.add(file.episode);
                }
            }
            if (episodeNumbers.isEmpty()) {
                return null;
            }
            Collections.sort(episodeNumbers);
            int stored = 0;
            for (int episodeNumber : episodeNumbers) {
                if (library.hasEpisodeMetadata(showId, season, episodeNumbers.size())) {
                    break;
                }
                if (listener != null) {
                    progress(listener, "获取分集剧照 S" + pad2(season) + "E" + pad2(episodeNumber));
                }
                TmdbClient.EpisodeDetail detail = tmdb.episodeDetail((int) tmdbId, season, episodeNumber);
                if (!detail.tmdbAnswered) {
                    continue;
                }
                library.upsertEpisodeMetadata(showId, season, episodeNumber,
                        detail.title, detail.overview, detail.airDate, detail.stillUrl);
                stored++;
            }
            return stored > 0 ? null : "TMDB 没有该剧季的分集信息。";
        } catch (Exception error) {
            return "分集信息获取失败：" + error.getMessage();
        } finally {
            episodeScrapeInFlight.remove(flightKey);
        }
    }

    /** {@code [0]} = 0 (movie) or show id marker, {@code [1]} = entity id; null when malformed. */
    private long[] entityIds(String itemId) {
        if (itemId == null) {
            return null;
        }
        int colon = itemId.indexOf(':');
        if (colon < 0) {
            return null;
        }
        try {
            return new long[]{0, Long.parseLong(itemId.substring(colon + 1))};
        } catch (NumberFormatException ignored) {
            return null;
        }
    }

    private String apiDisabledMessage() {
        return tmdbStatusMessage();
    }

    private static String pad2(int value) {
        return value < 10 ? "0" + value : String.valueOf(value);
    }

    public static File defaultLocalDirectory() {
        return new File(Environment.getExternalStorageDirectory(), "Movies");
    }

    private interface LocalFileVisitor {
        void onVideo(String fileName, String relativePath, long sizeBytes);
    }

    // ---- 字幕 sidecar 转档（1.34）------------------------------------------------------

    /** 一条可用的转档产物。 */
    public static final class Sidecar {
        public final String videoFileId;
        public final int trackNumber;
        public final String relativePath;
        public final String language;
        public final String format;
        public final String status;
        public final int cueCount;
        public final long byteOffset;
        public final double coveredSeconds;
        public final String error;
        /** 真的没转出来，是 NAS 那边写回媒体源的现成字幕（登记时占 900 号以后的轨位）。 */
        public final boolean external;

        Sidecar(LocalMediaLibrary.SidecarRow row) {
            external = row.trackNumber >= 900;
            videoFileId = row.videoFileId;
            trackNumber = row.trackNumber;
            relativePath = row.relativePath;
            language = row.language;
            format = row.format;
            status = row.status;
            cueCount = row.cueCount;
            byteOffset = row.byteOffset;
            coveredSeconds = row.coveredSeconds;
            error = row.error;
        }

        /** 跑完或跑到中途都算能用：覆盖到的区间里字幕照常显示。 */
        public boolean usable() {
            return "done".equals(status) || "partial".equals(status);
        }
    }

    /** 转档结果：给用户看的一句话 + 到底转成没转成。 */
    public static final class SidecarReport {
        public final boolean converted;
        public final String message;

        SidecarReport(boolean converted, String message) {
            this.converted = converted;
            this.message = message;
        }
    }

    private static final String[] SIDECAR_FORMATS = {"srt", "ass", "sup"};
    /** 文件里没有字幕轨可参照时，就按这几个语言码去找现成的 sidecar。 */
    private static final String[] SIDECAR_FALLBACK_LANGS = {"zh", "und", "en"};

    /**
     * 认一认媒体源里已经躺着的外挂字幕（NAS 那台转档服务写的就是同一套
     * ``片名.语言.格式``），登记进库就能直接在播放菜单里用。登记一条之后
     * {@code sidecarCandidates} 也不会再挑中这个文件，省下自己重抽一遍的带宽。
     *
     * @param streams 这条文件的内封字幕轨，用来推要探哪些语言码；传 null 走兜底那几个
     * @return 新登记了几条
     */
    public int discoverSidecars(String videoFileId, String relativePath,
                                java.util.List<Models.VideoFileStream> streams) {
        SubtitleSidecarService.Access access = sidecarAccessFor(relativePath);
        if (access == null) {
            return 0;
        }
        java.util.LinkedHashSet<String> languages = new java.util.LinkedHashSet<>();
        if (streams != null) {
            // 蓝光一张盘能有十几条字幕轨，探语言码探到十几轮就没必要了：挑前三条足够。
            for (Models.VideoFileStream stream : streams) {
                if (languages.size() >= 3) {
                    break;
                }
                String language = com.omniplay.tv.subtitle.MatroskaSubtitleExtractor
                        .normalizeLanguage(stream.language);
                if (language != null && !language.isEmpty()) {
                    languages.add(language);
                }
            }
        }
        // NAS 那台服务只会写「片名.偏好语言.格式」。文件里的字幕轨常常不标语言、或者标 und，
        // 只按文件推出来的语言探就会漏掉现成的那份，所以偏好语言那几个码总探一遍。
        java.util.Collections.addAll(languages, SIDECAR_FALLBACK_LANGS);
        String preferred = com.omniplay.tv.subtitle.MatroskaSubtitleExtractor
                .normalizeLanguage(settings.defaultSubtitleLanguage());
        if (preferred != null && !preferred.isEmpty()) {
            languages.add(preferred);
        }
        java.util.Set<String> recorded = new java.util.HashSet<>();
        for (Sidecar sidecar : sidecarsFor(videoFileId)) {
            recorded.add(sidecar.relativePath);
        }
        int found = 0;
        int slot = 0;
        for (String language : languages) {
            for (String format : SIDECAR_FORMATS) {
                String path = SubtitleSidecarService.sidecarPathFor(relativePath, language, format);
                slot++;
                if (recorded.contains(path) || !access.exists(path)) {
                    continue;
                }
                LocalMediaLibrary.SidecarRow row = new LocalMediaLibrary.SidecarRow();
                row.videoFileId = videoFileId;
                row.trackNumber = 900 + slot;
                row.relativePath = path;
                row.language = language;
                row.format = format;
                row.status = "done";
                library.recordSidecar(row);
                recorded.add(path);
                found++;
            }
        }
        return found;
    }

    public java.util.List<Sidecar> sidecarsFor(String videoFileId) {
        java.util.ArrayList<Sidecar> out = new java.util.ArrayList<>();
        for (LocalMediaLibrary.SidecarRow row : library.sidecarRows(videoFileId)) {
            out.add(new Sidecar(row));
        }
        return out;
    }

    /** 找一个能读写这条相对路径的媒体源。本地源看文件在不在，网络源按添加顺序取第一个。 */
    public SubtitleSidecarService.Access sidecarAccessFor(String relativePath) {
        SubtitleSidecarService.Access webDav = null;
        SubtitleSidecarService.Access smb = null;
        for (Source source : sources()) {
            if (!source.enabled) {
                continue;
            }
            if ("local".equals(source.kind)) {
                if (new File(source.path, relativePath).isFile()) {
                    return new SubtitleSidecarService.LocalAccess(new File(source.path));
                }
            } else if ("webdav".equals(source.kind)) {
                if (webDav == null) {
                    webDav = new SubtitleSidecarService.WebDavAccess(
                            source.path, source.username, source.password);
                }
            } else if ("smb".equals(source.kind) && smb == null) {
                smb = new SubtitleSidecarService.SmbAccess(smbClient(source));
            }
        }
        return webDav != null ? webDav : smb;
    }

    /**
     * 扫完一轮顺手认一遍现成的外挂字幕，只登记不抽取。候选就是库里还没有 sidecar 记录的文件，
     * 一次一小把，别把 WebDAV 的 HEAD 打爆。
     */
    public int sidecarRecogniseSweep(int limit, ProgressListener listener) {
        int recognised = 0;
        for (String[] candidate : library.sidecarCandidates(limit, false)) {
            if (discoverSidecars(candidate[0], candidate[1], null) > 0) {
                recognised++;
            }
        }
        return recognised;
    }

    /** 转一条视频文件里最该看的那条字幕轨。可以在任何后台线程上调。 */
    public SidecarReport transcodeSidecar(String videoFileId, String relativePath) {
        SubtitleSidecarService.Access access = sidecarAccessFor(relativePath);
        if (access == null) {
            return new SidecarReport(false, "这个媒体源现在读写不了。");
        }
        java.util.List<com.omniplay.tv.subtitle.MatroskaSubtitleExtractor.Track> tracks =
                SubtitleSidecarService.probe(access, relativePath);
        if (tracks.isEmpty()) {
            LocalMediaLibrary.SidecarRow row = new LocalMediaLibrary.SidecarRow();
            row.videoFileId = videoFileId;
            row.trackNumber = 0;
            row.relativePath = SubtitleSidecarService.sidecarPathFor(relativePath, "und", "srt");
            row.language = "und";
            row.format = "srt";
            row.status = "unsupported";
            row.error = "没有能转的字幕轨（目前支持 MKV 里的 SRT / ASS / PGS）。";
            library.recordSidecar(row);
            return new SidecarReport(false, row.error);
        }
        com.omniplay.tv.subtitle.MatroskaSubtitleExtractor.Track track =
                pickSubtitleTrack(tracks);
        LocalMediaLibrary.SidecarRow row = new LocalMediaLibrary.SidecarRow();
        row.videoFileId = videoFileId;
        row.trackNumber = track.trackNumber;
        row.language = track.language;
        row.format = track.format;
        try {
            SubtitleSidecarService.Outcome outcome = SubtitleSidecarService.convert(
                    access, relativePath, track, 0, 0, context.getCacheDir(),
                    (cueCount, progressOf) -> true);
            row.relativePath = outcome.sidecarPath;
            row.cueCount = outcome.cueCount;
            row.byteOffset = outcome.byteOffset;
            row.coveredSeconds = outcome.coveredSeconds;
            row.status = outcome.complete ? "done" : "partial";
            library.recordSidecar(row);
            return new SidecarReport(true, "已写好 " + outcome.sidecarPath + "（"
                    + outcome.cueCount + " 条）。");
        } catch (Exception error) {
            row.relativePath = SubtitleSidecarService.sidecarPathFor(
                    relativePath, track.language, track.format);
            row.status = "failed";
            row.error = error.getMessage() == null ? error.getClass().getSimpleName() : error.getMessage();
            library.recordSidecar(row);
            return new SidecarReport(false, "转档失败：" + row.error);
        }
    }

    /** 优先用户字幕偏好那条语言，其次容器标了默认轨的，再不行取第一条。 */
    private com.omniplay.tv.subtitle.MatroskaSubtitleExtractor.Track pickSubtitleTrack(
            java.util.List<com.omniplay.tv.subtitle.MatroskaSubtitleExtractor.Track> tracks) {
        String preferred = com.omniplay.tv.subtitle.MatroskaSubtitleExtractor
                .normalizeLanguage(settings.defaultSubtitleLanguage());
        for (com.omniplay.tv.subtitle.MatroskaSubtitleExtractor.Track track : tracks) {
            if (track.language.equals(preferred)) {
                return track;
            }
        }
        for (com.omniplay.tv.subtitle.MatroskaSubtitleExtractor.Track track : tracks) {
            if (track.isDefault) {
                return track;
            }
        }
        return tracks.get(0);
    }

    /** 播放侧读产物：文本轨几十 KB，位图轨能有几百 MB，都直接流式读完再交给覆盖层。 */
    public byte[] readSidecar(Sidecar sidecar) throws java.io.IOException {
        SubtitleSidecarService.Access access = sidecarAccessFor(sidecar.relativePath);
        if (access == null) {
            throw new java.io.IOException("媒体源现在读写不了。");
        }
        return access.read(sidecar.relativePath);
    }
}

