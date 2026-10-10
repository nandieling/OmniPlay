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
    private final SharedPreferences preferences;
    private final TvSettings settings;
    private final OfflineCacheManager cache;
    private final Map<String, WebDavClient> webDavClients = new ConcurrentHashMap<>();
    private final Map<String, SmbClient> smbClients = new ConcurrentHashMap<>();
    private volatile boolean scraping;
    /** Guards {@link #scraping} so only one scrape pass can be in flight. */
    private final Object scrapeLock = new Object();
    /** 一次刮削 200 个条目；队列更长时 {@link #scrapePending} 会继续取下一批。 */
    private static final int SCRAPE_BATCH = 200;
    private volatile String scrapeStatus = "";
    private final AtomicInteger activeScrapes = new AtomicInteger();
    private final Set<String> episodeScrapeInFlight = ConcurrentHashMap.newKeySet();

    public interface ProgressListener {
        void onScrapeProgress(String message);
    }

    private LocalEngine(Context context) {
        this.context = context.getApplicationContext();
        this.library = new LocalMediaLibrary(context);
        this.tmdb = new TmdbClient(context);
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

    /** 当前媒体源 id 集合，供扫描收尾清理「源已经不存在了」的文件行。 */
    public java.util.Set<Integer> liveSourceIds() {
        return liveSourceIds;
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
            int sourceIndex = 0;
            for (Source source : enabled) {
                sourceIndex++;
                // 逐文件回调会在主线程上刷几百次横幅，是遥控器发卡的头一个来源；这里按源报一次。
                if (listener != null) {
                    listener.onScrapeProgress("扫描中 (" + sourceIndex + "/" + enabled.size()
                            + ")：" + source.name);
                }
                if (source.kind.equals("local")) {
                    scanLocalSource(source, currentFileIds, report, listener);
                } else if (source.kind.equals("smb")) {
                    scanSmbSource(source, currentFileIds, report, listener);
                } else {
                    scanWebDavSource(source, currentFileIds, report, listener);
                }
            }
            int removed = library.removeMissingFiles(new ArrayList<>(currentFileIds));
            // 扫描进行中移除的媒体源，这一轮仍会把它扫到的文件登记回来；收尾按还活着的源再清一次。
            removed += library.purgeUnknownSources(liveSourceIds);
            report.removedFiles = removed;
            report.success = true;
            // 收尾文案与 mac 版同一句话：新增 / 移除 / 共发现。
            report.message = "扫描完成：新增 " + report.newFiles
                    + "，移除 " + removed
                    + "，共发现 " + report.scannedFiles + " 个文件。"
                    + (report.skippedStreams > 0
                            ? "（蓝光非正片分卷跳过 " + report.skippedStreams + " 条）" : "");
        } catch (Exception error) {
            report.success = false;
            report.message = "扫描失败：" + error.getMessage();
        }
        return report;
    }

    private void scanLocalSource(Source source, Set<String> fileIds,
                                 ScanReport report, ProgressListener listener) {
        File root = new File(source.path);
        if (!root.isDirectory()) {
            return;
        }
        ScanCollector collector = new ScanCollector(source, fileIds, report, listener);
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
                                  ScanReport report, ProgressListener listener) throws IOException {
        WebDavClient client = webDavClient(source);
        ScanCollector collector = new ScanCollector(source, fileIds, report, listener);
        client.walk(collector::offer);
        collector.finish();
    }

    private void scanSmbSource(Source source, Set<String> fileIds,
                               ScanReport report, ProgressListener listener) throws IOException {
        SmbClient client = smbClient(source);
        ScanCollector collector = new ScanCollector(source, fileIds, report, listener);
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
        private final Map<String, List<BluRayStreams.Candidate>> discs = new LinkedHashMap<>();

        ScanCollector(Source source, Set<String> fileIds,
                      ScanReport report, ProgressListener listener) {
            this.source = source;
            this.fileIds = fileIds;
            this.report = report;
            this.listener = listener;
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
            fileIds.add(fileId(source.id, relativePath));
            report.scannedFiles++;
            registerFile(source, relativePath, fileName,
                    LocalMediaNameParser.parseEpisode(relativePath, 1), fileIds, report, listener);
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
            if (metadata.chineseTitle != null || metadata.foreignTitle != null) {
                library.enqueueScrape("tv", entityId,
                        metadata.chineseTitle != null ? metadata.chineseTitle
                                : (metadata.foreignTitle != null ? metadata.foreignTitle : entityTitle),
                        metadata.year,
                        metadata.chineseTitle != null ? metadata.foreignTitle : metadata.chineseTitle,
                        episode.season, fileId);
            }
        } else {
            // 文件名算不出能看的名字时，退回「清洗过的文件名」；条目已存在的话由库里那条改名逻辑负责。
            entityTitle = prettyName != null ? prettyName : LocalMediaNameParser.stripDisplay(fileName);
            entityId = library.bindEntityForFile("movie", fileId, entityTitle);
            report.newEntities++;
            library.insertVideoFile(
                    new LocalMediaLibrary.ScannedFile(fileId, relativePath, fileName,
                            isTv, 0, 1, 1),
                    entityId, 0, "movie");
            if (metadata.chineseTitle != null || metadata.foreignTitle != null) {
                library.enqueueScrape("movie", entityId,
                        metadata.chineseTitle != null ? metadata.chineseTitle
                                : (metadata.foreignTitle != null ? metadata.foreignTitle : entityTitle),
                        metadata.year,
                        metadata.chineseTitle != null ? metadata.foreignTitle : metadata.chineseTitle,
                        0, fileId);
            }
        }
        report.newFiles++;
    }

    private static String fileId(int sourceId, String relativePath) {
        return "local-" + sourceId + ":" + relativePath.replace('/', '-');
    }

    // ------------------------------------------------------------- library models

    public List<Models.LibraryItem> libraryItems() {
        List<Models.LibraryItem> result = new ArrayList<>();
        List<LocalMediaLibrary.StoredEntity> entities = library.allEntities();
        for (LocalMediaLibrary.StoredEntity entity : entities) {
            List<LocalMediaLibrary.StoredFile> files = entity.isTv
                    ? library.filesForShow(entity.id)
                    : library.filesForMovie(entity.id);
            if (files.isEmpty()) {
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
            result.add(new Models.LibraryItem(
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
                    maxDuration));
        }
        result.sort((a, b) -> a.title == null || b.title == null
                ? 0 : a.title.compareToIgnoreCase(b.title));
        return result;
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
                            LocalMediaLibrary.episodeKey(Math.max(1, file.season), file.episode),
                            file.episodeSubtitle);
                }
                bySeason.computeIfAbsent(Math.max(1, file.season), key -> new ArrayList<>()).add(file);
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
            Collections.sort(seasonNumbers);
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
                        "第 " + seasonNumber + " 季",
                        poster,
                        episodes));
            }
            return Models.LibraryDetail.local(itemId, "tv", entity.title, entity.releaseDate,
                    entity.overview, poster, vote, allWatched, stored.size(), maxProgress, maxDuration,
                    new ArrayList<>(), seasons, genres, directors, casts);
        }

        movieFiles.sort(Comparator.comparing(f -> f.fileName == null ? "" : f.fileName, String.CASE_INSENSITIVE_ORDER));
        return Models.LibraryDetail.local(itemId, "movie", entity.title, entity.releaseDate,
                entity.overview, poster, vote, allWatched, stored.size(), maxProgress, maxDuration,
                movieFiles, new ArrayList<>(), genres, directors, casts);
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
        ScanReport report = scanAll(listener);
        if (!report.success || !settings.autoScrapeAfterScan()) {
            return report;
        }
        if (!tmdb.isConfigured()) {
            // 扫完就把「等 TMDB」这件事说出来，别让它变成一条看不懂的待刮削计数。
            scrapeStatus = library.scrapeQueueCount() > 0 ? "等待 TMDB API 连通后刮削..." : "";
            if (listener != null && !scrapeStatus.isEmpty()) {
                listener.onScrapeProgress(scrapeStatus);
            }
            return report;
        }
        // 扫描摘要先说一声，随后才轮到刮削进度盖上去（与 mac 版同一顺序）。
        if (listener != null) {
            listener.onScrapeProgress(report.message);
        }
        if (library.scrapeQueueCount() > 0) {
            scrapePending(listener);
        }
        return report;
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
        if (!tmdb.isConfigured()) {
            if (library.scrapeQueueCount() > 0) {
                // mac 版这时候说的是「等待 TMDB API 连通后刮削」，而不是干挂一个待刮削数字。
                scrapeStatus = "等待 TMDB API 连通后刮削...";
                if (listener != null) {
                    listener.onScrapeProgress(scrapeStatus);
                }
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
            // 一次取一批，取完接着取，直到队列真的见底：中途扫描还在往队列里补条目，
            // 只要「取到不满一批」就退出，剩下那截就会一直挂在首页上不动。
            int done = 0;
            int failed = 0;
            int skipped = 0;
            int totalQueued = library.scrapeQueueCount();
            for (int round = 0; round < 100; round++) {
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
                    index++;
                    String mediaType = (String) batch.get("mediaType");
                    long entityId = (Long) batch.get("entityId");
                    String query = (String) batch.get("query");
                    String year = (String) batch.get("year");
                    String secondary = (String) batch.get("secondaryQuery");
                    int season = (Integer) batch.get("preferredSeason");
                    if (library.isEntityLocked(mediaType, entityId)) {
                        // 手动匹配过或自定义编辑过的条目不再自动覆盖（mac 版同一条规则）；
                        // 队列行在取批次时已经删掉，跳过它不会让「待刮削」卡住。
                        skipped++;
                        continue;
                    }
                    scrapeStatus = "刮削元数据和海报 (" + (done + index) + "/"
                            + Math.max(totalQueued, done + total) + ")：" + query;
                    if (listener != null) {
                        listener.onScrapeProgress(scrapeStatus);
                    }
                    activeScrapes.incrementAndGet();
                    try {
                        TmdbClient.Result result =
                                tmdb.multiSearch(query, year, mediaType, season, secondary);
                        if (result != null && isYearPlausible(result, year, mediaType)) {
                            library.applyScrapeResult(mediaType, entityId, result);
                            applyTitleDetails(mediaType, entityId, result.id);
                        }
                    } catch (Exception error) {
                        failed++;
                    } finally {
                        activeScrapes.decrementAndGet();
                    }
                }
                done += total;
            }
            if (done > 0) {
                String tail = skipped > 0 ? "，跳过 " + skipped + " 个手动匹配的条目" : "";
                scrapeStatus = failed == 0
                        ? "刮削完成，共处理 " + done + " 个条目" + tail + "。"
                        : "刮削完成，处理 " + done + " 个条目，" + failed + " 个没匹配上（可重新扫描重试）" + tail + "。";
                if (listener != null) {
                    listener.onScrapeProgress(scrapeStatus);
                }
            }
        } finally {
            synchronized (scrapeLock) {
                scraping = false;
            }
        }
    }

    private static boolean isYearPlausible(TmdbClient.Result result, String year, String mediaType) {
        if (year == null || year.isEmpty()) {
            return true;
        }
        if (result.releaseDate == null || result.releaseDate.length() < 4) {
            return true;
        }
        try {
            int target = Integer.parseInt(year);
            int actual = Integer.parseInt(result.releaseDate.substring(0, 4));
            return Math.abs(actual - target) <= 2;
        } catch (NumberFormatException ignored) {
            return true;
        }
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
        return library.applyManualMatch(mediaType, entityId, result, genres, directors, casts);
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
            if (Math.max(1, file.season) == season && file.episode > 0) {
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
                if (Math.max(1, file.season) == season && file.episode > 0 && !episodeNumbers.contains(file.episode)) {
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
                    listener.onScrapeProgress("获取分集剧照 S" + pad2(season) + "E" + pad2(episodeNumber));
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
}
