package com.omniplay.tv.local;

import android.content.ContentValues;
import android.content.Context;
import android.database.Cursor;
import android.database.sqlite.SQLiteDatabase;
import android.database.sqlite.SQLiteOpenHelper;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.Set;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;

/**
 * SQLite-backed local media library (movies / shows / video files / progress).
 * Mirrors the OmniPlay (mac) GRDB schema for the Android standalone build.
 */
public final class LocalMediaLibrary {
    public static final class ScannedFile {
        public final String fileId;
        public final String relativePath;
        public final String fileName;
        public final boolean isTvLike;
        public final int season;
        public final int episode;
        public final int fallbackIndex;

        ScannedFile(String fileId, String relativePath, String fileName, boolean isTvLike,
                    int season, int episode, int fallbackIndex) {
            this.fileId = fileId;
            this.relativePath = relativePath;
            this.fileName = fileName;
            this.isTvLike = isTvLike;
            this.season = season;
            this.episode = episode;
            this.fallbackIndex = fallbackIndex;
        }
    }

    public static final class StoredFile {
        public final String id;
        public final long movieId;
        public final long showId;
        public final String relativePath;
        public final String fileName;
        public final String mediaKind;
        public final int season;
        public final int episode;
        public final String episodeTitle;
        public final String episodeSubtitle;
        public final double positionSeconds;
        public final double durationSeconds;
        public final boolean isWatched;

        StoredFile(String id, long movieId, long showId, String relativePath, String fileName,
                   String mediaKind, int season, int episode, String episodeTitle,
                   String episodeSubtitle,
                   double positionSeconds, double durationSeconds, boolean isWatched) {
            this.id = id;
            this.movieId = movieId;
            this.showId = showId;
            this.relativePath = relativePath;
            this.fileName = fileName;
            this.mediaKind = mediaKind;
            this.season = season;
            this.episode = episode;
            this.episodeTitle = episodeTitle;
            this.episodeSubtitle = nullToEmpty(episodeSubtitle);
            this.positionSeconds = positionSeconds;
            this.durationSeconds = durationSeconds;
            this.isWatched = isWatched;
        }
    }

    public static final class StoredEntity {
        public final long id;
        public final String title;
        public final String releaseDate;
        public final String overview;
        public final String posterUrl;
        public final double voteAverage;
        public final boolean isTv;
        public final boolean isScraped;
        public final long tmdbId;
        public final String genres;
        public final String directors;
        public final String casts;

        StoredEntity(long id, String title, String releaseDate, String overview,
                     String posterUrl, double voteAverage, boolean isTv, boolean isScraped,
                     long tmdbId, String genres, String directors, String casts) {
            this.id = id;
            this.title = title;
            this.releaseDate = releaseDate;
            this.overview = overview;
            this.posterUrl = posterUrl;
            this.voteAverage = voteAverage;
            this.isTv = isTv;
            this.isScraped = isScraped;
            this.tmdbId = tmdbId;
            this.genres = nullToEmpty(genres);
            this.directors = nullToEmpty(directors);
            this.casts = nullToEmpty(casts);
        }
    }

    /** Cached episode metadata scraped from TMDB (title / overview / air date / still). */
    public static final class EpisodeMetadata {
        public final int season;
        public final int episode;
        public final String title;
        public final String overview;
        public final String airDate;
        public final String stillUrl;

        EpisodeMetadata(int season, int episode, String title, String overview,
                        String airDate, String stillUrl) {
            this.season = season;
            this.episode = episode;
            this.title = nullToEmpty(title);
            this.overview = nullToEmpty(overview);
            this.airDate = nullToEmpty(airDate);
            this.stillUrl = nullToEmpty(stillUrl);
        }
    }

    /** A finished offline-cache download for one video file. */
    public static final class CacheRecord {
        public final String fileId;
        public final String localPath;
        public final String sourceUrl;
        public final long bytes;

        CacheRecord(String fileId, String localPath, String sourceUrl, long bytes) {
            this.fileId = fileId;
            this.localPath = localPath;
            this.sourceUrl = nullToEmpty(sourceUrl);
            this.bytes = bytes;
        }
    }

    private static final class OpenHelper extends SQLiteOpenHelper {
        OpenHelper(Context context) {
            // v3 是中文译名缓存表，v4 是豆瓣条目表。老库只有版本号升上去才会走
            // onUpgrade → onCreate，表建不出来时每次查缓存都报错，刮削就被记成「请求失败」。
            super(context, "omniplay_local.db", null, 6);
        }

        /**
         * 开 WAL。默认的回滚日志模式下，扫描一边写、首页一边读，读会被写挡到几百毫秒，
         * 「已发现 N」在涨而海报不动，有一半就是挡在这儿。WAL 让读写并行。
         */
        @Override
        public void onConfigure(SQLiteDatabase db) {
            db.enableWriteAheadLogging();
        }

        @Override
        public void onCreate(SQLiteDatabase db) {
            db.execSQL("CREATE TABLE IF NOT EXISTS movie ("
                    + "id INTEGER PRIMARY KEY AUTOINCREMENT, "
                    + "title TEXT NOT NULL, "
                    + "releaseDate TEXT DEFAULT '', "
                    + "overview TEXT DEFAULT '', "
                    + "posterUrl TEXT DEFAULT '', "
                    + "voteAverage REAL DEFAULT 0, "
                    + "isLocked INTEGER DEFAULT 0, "
                    + "scraped INTEGER DEFAULT 0, "
                    + "tmdbId INTEGER DEFAULT 0, "
                    + "genres TEXT DEFAULT '', "
                    + "directors TEXT DEFAULT '', "
                    + "casts TEXT DEFAULT '')");
            db.execSQL("CREATE TABLE IF NOT EXISTS tvShow ("
                    + "id INTEGER PRIMARY KEY AUTOINCREMENT, "
                    + "title TEXT NOT NULL, "
                    + "firstAirDate TEXT DEFAULT '', "
                    + "overview TEXT DEFAULT '', "
                    + "posterUrl TEXT DEFAULT '', "
                    + "voteAverage REAL DEFAULT 0, "
                    + "isLocked INTEGER DEFAULT 0, "
                    + "scraped INTEGER DEFAULT 0, "
                    + "tmdbId INTEGER DEFAULT 0, "
                    + "genres TEXT DEFAULT '', "
                    + "directors TEXT DEFAULT '', "
                    + "casts TEXT DEFAULT '')");
            db.execSQL("CREATE TABLE IF NOT EXISTS videoFile ("
                    + "id TEXT PRIMARY KEY, "
                    + "movieId INTEGER, "
                    + "showId INTEGER, "
                    + "season INTEGER DEFAULT 0, "
                    + "episode INTEGER DEFAULT 0, "
                    + "episodeTitle TEXT DEFAULT '', "
                    + "episodeSubtitle TEXT DEFAULT '', "
                    + "relativePath TEXT NOT NULL, "
                    + "fileName TEXT NOT NULL, "
                    + "mediaKind TEXT DEFAULT 'movie', "
                    + "playProgress REAL DEFAULT 0, "
                    + "duration REAL DEFAULT 0, "
                    + "isWatched INTEGER DEFAULT 0, "
                    + "lastPlayedAt REAL DEFAULT 0)");
            db.execSQL("CREATE INDEX IF NOT EXISTS idx_videoFile_movie ON videoFile(movieId)");
            db.execSQL("CREATE INDEX IF NOT EXISTS idx_videoFile_show ON videoFile(showId)");
            db.execSQL("CREATE TABLE IF NOT EXISTS scrapeQueue ("
                    + "id INTEGER PRIMARY KEY AUTOINCREMENT, "
                    + "mediaType TEXT NOT NULL, "
                    + "entityId INTEGER NOT NULL, "
                    + "query TEXT NOT NULL, "
                    + "year TEXT DEFAULT '', "
                    + "secondaryQuery TEXT DEFAULT '', "
                    + "preferredSeason INTEGER DEFAULT 0, "
                    + "sourceFileId TEXT DEFAULT '')");
            db.execSQL("CREATE TABLE IF NOT EXISTS tvEpisode ("
                    + "showId INTEGER NOT NULL, "
                    + "season INTEGER NOT NULL, "
                    + "episode INTEGER NOT NULL, "
                    + "title TEXT DEFAULT '', "
                    + "overview TEXT DEFAULT '', "
                    + "airDate TEXT DEFAULT '', "
                    + "stillUrl TEXT DEFAULT '', "
                    + "updatedAt REAL DEFAULT 0, "
                    + "PRIMARY KEY (showId, season, episode))");
            db.execSQL("CREATE TABLE IF NOT EXISTS offlineCache ("
                    + "fileId TEXT PRIMARY KEY, "
                    + "localPath TEXT NOT NULL, "
                    + "sourceUrl TEXT DEFAULT '', "
                    + "bytes INTEGER DEFAULT 0, "
                    + "updatedAt REAL DEFAULT 0)");
            // 中文译名缓存（对应 mac 版 ChineseNameStore）：key = work#<movie|tv>#<tmdbId>，
            // title 为空是「已确认 TMDB 没有中文条目」的负缓存，过一阵子才允许再问。
            db.execSQL("CREATE TABLE IF NOT EXISTS chineseNameCache ("
                    + "key TEXT PRIMARY KEY, "
                    + "title TEXT NOT NULL DEFAULT '', "
                    + "updatedAt INTEGER DEFAULT 0)");
            // 手动绑定的豆瓣条目（对应 mac 版 doubanMetadata 表）：key = <movie|tv>#<实体 id>，
            // 一条实体最多绑一条豆瓣。列表字段用「 / 」拼成一整串，界面上自己拆。
            db.execSQL("CREATE TABLE IF NOT EXISTS doubanMetadata ("
                    + "key TEXT PRIMARY KEY, "
                    + "subjectId TEXT NOT NULL DEFAULT '', "
                    + "subjectUrl TEXT DEFAULT '', "
                    + "title TEXT DEFAULT '', "
                    + "originalTitle TEXT DEFAULT '', "
                    + "year TEXT DEFAULT '', "
                    + "rating REAL DEFAULT 0, "
                    + "ratingCount INTEGER DEFAULT 0, "
                    + "summary TEXT DEFAULT '', "
                    + "genres TEXT DEFAULT '', "
                    + "countries TEXT DEFAULT '', "
                    + "directors TEXT DEFAULT '', "
                    + "casts TEXT DEFAULT '', "
                    + "posterUrl TEXT DEFAULT '', "
                    + "updatedAt INTEGER DEFAULT 0)");
            // 转档出来的字幕 sidecar（1.34）：一条视频文件的一条字幕轨一行，记下产物路径、
            // 写到哪了（byteOffset 续跑）与状态。表名与列名都留着给以后的多轨转档用。
            db.execSQL("CREATE TABLE IF NOT EXISTS subtitleSidecar ("
                    + "videoFileId TEXT NOT NULL, "
                    + "trackNumber INTEGER NOT NULL, "
                    + "relativePath TEXT NOT NULL DEFAULT '', "
                    + "language TEXT NOT NULL DEFAULT '', "
                    + "format TEXT NOT NULL DEFAULT '', "
                    + "status TEXT NOT NULL DEFAULT 'pending', "
                    + "cueCount INTEGER DEFAULT 0, "
                    + "byteOffset INTEGER DEFAULT 0, "
                    + "coveredSeconds REAL DEFAULT 0, "
                    + "error TEXT DEFAULT '', "
                    + "updatedAt INTEGER DEFAULT 0, "
                    + "PRIMARY KEY (videoFileId, trackNumber))");
        }

        @Override
        public void onUpgrade(SQLiteDatabase db, int oldVersion, int newVersion) {
            // v6 只多了一张 subtitleSidecar 表，交给末尾统一的 onCreate 建出来，不用搬数据；
            // 整表重解析在 v5 那次已经跑过，这里不再重复。
            if (oldVersion < 5) {
                // 扫描是增量的（大小与修改时间没变就跳过重解析），光改解析器要等一次整库重扫才生效。
                // 这里按当前规则把每条文件的季 / 集重算一遍：老库里 `S00E01` 那批被抹成第 1 季的
                // 特别篇，等的就是这一次。
                reparseEpisodeColumns(db);
            }
            if (oldVersion < 4) {
                // v4 只多了一张豆瓣表，同样交给下面的 onCreate 建出来。
                addColumnIfMissing(db, "videoFile", "episodeSubtitle", "TEXT DEFAULT ''");
            }
            if (oldVersion < 3) {
                // v3 只多了一张缓存表，交给下面统一的 onCreate 建出来。
                addColumnIfMissing(db, "videoFile", "episodeSubtitle", "TEXT DEFAULT ''");
            }
            if (oldVersion < 2) {
                addColumnIfMissing(db, "movie", "tmdbId", "INTEGER DEFAULT 0");
                addColumnIfMissing(db, "movie", "genres", "TEXT DEFAULT ''");
                addColumnIfMissing(db, "movie", "directors", "TEXT DEFAULT ''");
                addColumnIfMissing(db, "movie", "casts", "TEXT DEFAULT ''");
                addColumnIfMissing(db, "tvShow", "tmdbId", "INTEGER DEFAULT 0");
                addColumnIfMissing(db, "tvShow", "genres", "TEXT DEFAULT ''");
                addColumnIfMissing(db, "tvShow", "directors", "TEXT DEFAULT ''");
                addColumnIfMissing(db, "tvShow", "casts", "TEXT DEFAULT ''");
                addColumnIfMissing(db, "videoFile", "episodeSubtitle", "TEXT DEFAULT ''");
            }
            // New v2 tables land here for both fresh installs and upgraded databases.
            onCreate(db);
        }

        /** 按 {@link LocalMediaNameParser} 现在的规则重算每一条文件的季 / 集。 */
        private static void reparseEpisodeColumns(SQLiteDatabase db) {
            try (Cursor cursor = db.rawQuery(
                    "SELECT id, relativePath, episode FROM videoFile WHERE showId IS NOT NULL", null)) {
                while (cursor.moveToNext()) {
                    String fileId = cursor.getString(0);
                    String path = cursor.getString(1);
                    int storedEpisode = cursor.getInt(2);
                    if (fileId == null || path == null || path.isEmpty()) {
                        continue;
                    }
                    LocalMediaNameParser.EpisodeInfo info =
                            LocalMediaNameParser.parseEpisode(path, Math.max(1, storedEpisode));
                    try (android.database.sqlite.SQLiteStatement update = db.compileStatement(
                            "UPDATE videoFile SET season = ?, episode = ? WHERE id = ?")) {
                        update.bindLong(1, info.season);
                        update.bindLong(2, info.episode);
                        update.bindString(3, fileId);
                        update.executeUpdateDelete();
                    }
                }
            }
        }

        private static void addColumnIfMissing(SQLiteDatabase db, String table, String column, String definition) {
            try (Cursor cursor = db.rawQuery("PRAGMA table_info(" + table + ")", null)) {
                while (cursor.moveToNext()) {
                    if (column.equalsIgnoreCase(cursor.getString(1))) {
                        return;
                    }
                }
            }
            db.execSQL("ALTER TABLE " + table + " ADD COLUMN " + column + " " + definition);
        }
    }

    private final OpenHelper helper;
    private final Map<String, double[]> progressOverrides = new ConcurrentHashMap<>();

    public LocalMediaLibrary(Context context) {
        helper = new OpenHelper(context.getApplicationContext());
    }

    // ---------------------------------------------------------------- entities

    public long upsertUnscrapedMovie(String title) {
        return upsertUnscrapedEntity("movie", title);
    }

    public long upsertUnscrapedShow(String title) {
        return upsertUnscrapedEntity("tv", title);
    }

    /**
     * 文件与条目的绑定关系。已登记过的文件保持它原来的条目（还没刮削时顺手把名字换成新算出来的），
     * 只有真正的新文件、或者条目已经不见了，才新建条目。
     * <p>
     * 以前扫描是无脑 upsert 新条目，可 {@code videoFile} 走的是 INSERT OR IGNORE——文件行还挂在旧条目上，
     * 新条目建出来就是个空壳，下一句剪枝就又把它删了。后果有两处：海报墙上的名字永远停在第一次入库
     * 那版（改不动），而刮削队列里留下一堆指向空壳条目的行，删不掉也刮不完，首页就一直挂着「待刮削 N」。
     */
    public long bindEntityForFile(String mediaType, String fileId, String title) {
        String table = "tv".equals(mediaType) ? "tvShow" : "movie";
        String column = "tv".equals(mediaType) ? "showId" : "movieId";
        SQLiteDatabase db = helper.getWritableDatabase();
        long existing = 0;
        boolean fileKnown = false;
        try (Cursor cursor = db.rawQuery("SELECT " + column + " FROM videoFile WHERE id = ?",
                new String[]{fileId})) {
            if (cursor.moveToFirst()) {
                fileKnown = true;
                existing = cursor.isNull(0) ? 0 : cursor.getLong(0);
            }
        }
        if (existing > 0) {
            boolean alive = false;
            try (Cursor cursor = db.rawQuery("SELECT scraped FROM " + table + " WHERE id = ?",
                    new String[]{String.valueOf(existing)})) {
                if (cursor.moveToFirst()) {
                    alive = true;
                    if (cursor.getInt(0) == 0 && title != null && !title.isEmpty()) {
                        // 没刮削的条目改名是安全的：名字只来自文件名，用户还没看过它。
                        db.execSQL("UPDATE " + table + " SET title = ? WHERE id = ? AND scraped = 0",
                                new Object[]{title, existing});
                    }
                }
            }
            if (alive) {
                return existing;
            }
        }
        long id = upsertUnscrapedEntity(mediaType, title);
        if (fileKnown && id != existing) {
            db.execSQL("UPDATE videoFile SET " + column + " = ? WHERE id = ?",
                    new Object[]{id, fileId});
        }
        return id;
    }

    private long upsertUnscrapedEntity(String mediaType, String title) {
        String table = mediaType.equals("tv") ? "tvShow" : "movie";
        String dateColumn = mediaType.equals("tv") ? "firstAirDate" : "releaseDate";
        SQLiteDatabase db = helper.getWritableDatabase();
        try (Cursor cursor = db.rawQuery("SELECT id FROM " + table
                + " WHERE title = ? AND scraped = 0", new String[]{title})) {
            if (cursor.moveToFirst()) {
                return cursor.getLong(0);
            }
        }
        try (android.database.sqlite.SQLiteStatement insert = db.compileStatement(
                "INSERT INTO " + table + " (title, " + dateColumn + ") VALUES (?, '')")) {
            insert.bindString(1, title);
            return insert.executeInsert();
        }
    }

    public void applyScrapeResult(String mediaType, long entityId, TmdbClient.Result result) {
        String table = mediaType.equals("tv") ? "tvShow" : "movie";
        String dateColumn = mediaType.equals("tv") ? "firstAirDate" : "releaseDate";
        SQLiteDatabase db = helper.getWritableDatabase();
        try (android.database.sqlite.SQLiteStatement statement = db.compileStatement(
                "UPDATE " + table + " SET title = ?, " + dateColumn + " = ?, overview = ?, "
                        + "posterUrl = ?, voteAverage = ?, scraped = 1, tmdbId = ? WHERE id = ?")) {
            statement.bindString(1, result.title == null || result.title.isEmpty()
                    ? String.valueOf(entityId) : result.title);
            statement.bindString(2, result.releaseDate == null ? "" : result.releaseDate);
            statement.bindString(3, result.overview == null ? "" : result.overview);
            statement.bindString(4, result.basePosterUrl() == null ? "" : result.basePosterUrl());
            statement.bindDouble(5, result.voteAverage);
            statement.bindLong(6, result.id);
            statement.bindLong(7, entityId);
            statement.executeUpdateDelete();
        }
    }

    /** Stores genres / directors / casts for the detail page (pipe-separated). */
    public void setEntityCredits(String mediaType, long entityId, String genres,
                                 String directors, String casts) {
        String table = mediaType.equals("tv") ? "tvShow" : "movie";
        SQLiteDatabase db = helper.getWritableDatabase();
        try (android.database.sqlite.SQLiteStatement statement = db.compileStatement(
                "UPDATE " + table + " SET genres = ?, directors = ?, casts = ? WHERE id = ?")) {
            statement.bindString(1, genres == null ? "" : genres);
            statement.bindString(2, directors == null ? "" : directors);
            statement.bindString(3, casts == null ? "" : casts);
            statement.bindLong(4, entityId);
            statement.executeUpdateDelete();
        }
    }

    /**
     * Manual rename from the detail page. The new title is locked so a later rescan
     * of the same folder keeps it, and the scrape queue gets a fresh lookup query.
     */
    public void renameEntity(String mediaType, long entityId, String newTitle) {
        String table = mediaType.equals("tv") ? "tvShow" : "movie";
        String trimmed = newTitle == null ? "" : newTitle.trim();
        if (trimmed.isEmpty()) {
            return;
        }
        SQLiteDatabase db = helper.getWritableDatabase();
        try (android.database.sqlite.SQLiteStatement statement = db.compileStatement(
                "UPDATE " + table + " SET title = ?, isLocked = 1 WHERE id = ?")) {
            statement.bindString(1, trimmed);
            statement.bindLong(2, entityId);
            statement.executeUpdateDelete();
        }
    }

    /** 负缓存有效期：30 天，与 mac 版 ChineseNameStore.negativeCacheTTL 同一条规则。 */
    public static final long CHINESE_TITLE_RETRY_SECONDS = 30L * 24 * 60 * 60;

    /** 中文译名缓存的键，与 mac 版 workKey 同一个形状。 */
    public static String chineseTitleKey(String mediaType, long tmdbId) {
        return "work#" + ("tv".equals(mediaType) ? "tv" : "movie") + "#" + tmdbId;
    }

    /**
     * 这部作品的中文译名是否已经问过 TMDB 且答案还能用：查到过就一直能用，
     * 「确实没有中文条目」这条只挡 30 天。返回 false 表示该再去问一次。
     */
    public boolean chineseTitleCached(String mediaType, long tmdbId) {
        try (Cursor cursor = helper.getReadableDatabase().rawQuery(
                "SELECT title, updatedAt FROM chineseNameCache WHERE key = ?",
                new String[]{chineseTitleKey(mediaType, tmdbId)})) {
            if (!cursor.moveToFirst()) {
                return false;
            }
            if (!cursor.getString(0).isEmpty()) {
                return true;
            }
            return cursor.getLong(1)
                    > System.currentTimeMillis() / 1000L - CHINESE_TITLE_RETRY_SECONDS;
        }
    }

    /** 缓存里的中文译名；空串表示已确认 TMDB 没有中文条目。 */
    public String cachedChineseTitle(String mediaType, long tmdbId) {
        try (Cursor cursor = helper.getReadableDatabase().rawQuery(
                "SELECT title FROM chineseNameCache WHERE key = ?",
                new String[]{chineseTitleKey(mediaType, tmdbId)})) {
            return cursor.moveToFirst() ? cursor.getString(0) : "";
        }
    }

    /** 记下查询结果；传空串就是写负缓存，省掉下一轮又一次注定没结果的请求。 */
    public void rememberChineseTitle(String mediaType, long tmdbId, String title) {
        ContentValues values = new ContentValues();
        values.put("key", chineseTitleKey(mediaType, tmdbId));
        values.put("title", title == null ? "" : title.trim());
        values.put("updatedAt", System.currentTimeMillis() / 1000L);
        helper.getWritableDatabase().replace("chineseNameCache", null, values);
    }

    /**
     * 换成中文译名：只改标题，不碰 isLocked。手动匹配过的条目照样该显示中文片名，
     * 「锁定」挡的是自动刮削覆盖，不该把官方中文名也一起挡在门外。
     */
    public void applyLocalizedTitle(String mediaType, long entityId, String newTitle) {
        String trimmed = newTitle == null ? "" : newTitle.trim();
        if (trimmed.isEmpty()) {
            return;
        }
        helper.getWritableDatabase().execSQL(
                "UPDATE " + ("tv".equals(mediaType) ? "tvShow" : "movie")
                        + " SET title = ? WHERE id = ?",
                new Object[]{trimmed, entityId});
    }

    /**
     * 拿文件名里那个中文片名补名，但只在条目现在还挂着外文名的时候动手：
     * 官方中文别名（localizeTitle 那条链路）优先级更高，不能反过来把它盖掉。
     */
    public boolean applyChineseTitleUnlessLocalized(String mediaType, long entityId,
                                                    String newTitle) {
        String trimmed = newTitle == null ? "" : newTitle.trim();
        if (trimmed.isEmpty() || !containsHan(trimmed)) {
            return false;
        }
        String table = "tv".equals(mediaType) ? "tvShow" : "movie";
        try (Cursor cursor = helper.getReadableDatabase().rawQuery(
                "SELECT title FROM " + table + " WHERE id = ?", new String[]{String.valueOf(entityId)})) {
            if (!cursor.moveToFirst() || containsHan(cursor.getString(0))) {
                return false;
            }
        }
        applyLocalizedTitle(mediaType, entityId, trimmed);
        return true;
    }

    private static boolean containsHan(String value) {
        if (value == null) {
            return false;
        }
        for (int index = 0; index < value.length(); index++) {
            char c = value.charAt(index);
            if (c >= '\u4E00' && c <= '\u9FFF') {
                return true;
            }
        }
        return false;
    }

    /**
     * 名称 / 年份规则换代后要重新判定的条目：已刮削、没被手动锁住，还能找回代表文件。
     * 每个实体只取一条文件行做代表（GROUP BY 取到的那一条）。
     */
    public List<Map<String, Object>> recheckCandidates(int limit) {
        List<Map<String, Object>> result = new ArrayList<>();
        SQLiteDatabase db = helper.getReadableDatabase();
        for (String table : new String[]{"movie", "tvShow"}) {
            String kind = "tvShow".equals(table) ? "tv" : "movie";
            String dateColumn = "tv".equals(kind) ? "firstAirDate" : "releaseDate";
            String owner = "tv".equals(kind) ? "showId" : "movieId";
            try (Cursor cursor = db.rawQuery(
                    "SELECT e.id, e.title, e." + dateColumn + ", f.relativePath, f.fileName,"
                            + " f.season, f.id FROM " + table + " e JOIN videoFile f ON f." + owner
                            + " = e.id WHERE e.isLocked = 0 AND e.scraped = 1"
                            + " GROUP BY e.id LIMIT ?",
                    new String[]{String.valueOf(limit)})) {
                while (cursor.moveToNext()) {
                    Map<String, Object> row = new HashMap<>();
                    row.put("mediaType", kind);
                    row.put("entityId", cursor.getLong(0));
                    row.put("title", nullToEmpty(cursor.getString(1)));
                    row.put("releaseDate", nullToEmpty(cursor.getString(2)));
                    row.put("relativePath", nullToEmpty(cursor.getString(3)));
                    row.put("fileName", nullToEmpty(cursor.getString(4)));
                    row.put("season", cursor.getInt(5));
                    row.put("fileId", nullToEmpty(cursor.getString(6)));
                    result.add(row);
                }
            }
        }
        return result;
    }

    /**
     * 已刮削、片名里还没有汉字、别名又没查过（或负缓存已过期）的条目：刮削收尾时
     * 拿它们补一批中文译名，老库里的外文片名才有条路子被换掉。
     */
    public List<Map<String, Object>> localizationCandidates(int limit) {
        List<Map<String, Object>> result = new ArrayList<>();
        String expiry = String.valueOf(System.currentTimeMillis() / 1000L
                - CHINESE_TITLE_RETRY_SECONDS);
        SQLiteDatabase db = helper.getReadableDatabase();
        for (String table : new String[]{"movie", "tvShow"}) {
            String kind = "tvShow".equals(table) ? "tv" : "movie";
            try (Cursor cursor = db.rawQuery(
                    "SELECT e.id, e.tmdbId, e.title FROM " + table + " e WHERE e.scraped = 1"
                            + " AND e.tmdbId > 0 AND e.title NOT GLOB '*[\u4e00-\u9fa5]*'"
                            + " AND NOT EXISTS (SELECT 1 FROM chineseNameCache c WHERE c.key ="
                            + " 'work#" + kind + "#' || e.tmdbId"
                            + " AND (c.title <> '' OR c.updatedAt > ?)) LIMIT ?",
                    new String[]{expiry, String.valueOf(limit)})) {
                while (cursor.moveToNext()) {
                    Map<String, Object> row = new HashMap<>();
                    row.put("mediaType", kind);
                    row.put("entityId", cursor.getLong(0));
                    row.put("tmdbId", cursor.getLong(1));
                    row.put("title", cursor.getString(2));
                    result.add(row);
                }
            }
        }
        return result;
    }

    /** 手动匹配或自定义编辑过的条目：自动刮削不再覆盖它（与 mac 版 isLocked 同一含义）。 */
    public boolean isEntityLocked(String mediaType, long entityId) {
        String table = "tv".equals(mediaType) ? "tvShow" : "movie";
        try (Cursor cursor = helper.getReadableDatabase().rawQuery(
                "SELECT isLocked FROM " + table + " WHERE id = ?", new String[]{String.valueOf(entityId)})) {
            return cursor.moveToFirst() && cursor.getInt(0) == 1;
        }
    }

    /**
     * 手动匹配：把用户挑中那条 TMDB 结果写进实体并锁住，之后整库扫描不再覆盖。
     * 类型对不上时（文件名被当成电影扫出来、其实是一部剧集）换一张表重建实体，文件行跟着搬走，
     * 返回值是之后该用的实体 id——同类型改绑时 id 不变。
     */
    public long applyManualMatch(String mediaType, long entityId, TmdbClient.Result result,
                                 String genres, String directors, String casts) {
        String targetKind = "tv".equals(result.mediaType) ? "tv" : "movie";
        SQLiteDatabase db = helper.getWritableDatabase();
        long targetId;
        if (targetKind.equals(mediaType)) {
            targetId = entityId;
            applyScrapeResult(targetKind, targetId, result);
        } else {
            targetId = insertScrapedEntity(db, targetKind, result);
            String fromTable = "tv".equals(mediaType) ? "tvShow" : "movie";
            String toColumn = "tv".equals(targetKind) ? "showId" : "movieId";
            String fromColumn = "tv".equals(mediaType) ? "showId" : "movieId";
            db.execSQL("UPDATE videoFile SET " + toColumn + " = ?, " + fromColumn + " = 0"
                            + ", mediaKind = ? WHERE " + fromColumn + " = ?",
                    new Object[]{targetId, targetKind, entityId});
            db.execSQL("DELETE FROM scrapeQueue WHERE mediaType = ? AND entityId = ?",
                    new Object[]{mediaType, String.valueOf(entityId)});
            db.execSQL("DELETE FROM tvEpisode WHERE showId = ?", new Object[]{entityId});
            db.execSQL("DELETE FROM " + fromTable + " WHERE id = ?", new Object[]{entityId});
        }
        String table = "tv".equals(targetKind) ? "tvShow" : "movie";
        String[] current = readCredits(db, table, targetId);
        try (android.database.sqlite.SQLiteStatement statement = db.compileStatement(
                "UPDATE " + table + " SET genres = ?, directors = ?, casts = ?, isLocked = 1 WHERE id = ?")) {
            statement.bindString(1, keepExisting(current[0], genres));
            statement.bindString(2, keepExisting(current[1], directors));
            statement.bindString(3, keepExisting(current[2], casts));
            statement.bindLong(4, targetId);
            statement.executeUpdateDelete();
        }
        // 换绑到的那部可能已经有另一条实体（同一部剧的两个季目录各自建了一条），顺手合掉。
        collapseDuplicateWorks(targetKind, targetId, result.id);
        return targetId;
    }

    /**
     * 详情页「自定义编辑」：手填资料并锁定，对应 mac 版的「保存修改并锁定」。
     * 片名留空表示不改名，只更新其余字段。
     */
    public void applyCustomEdit(String mediaType, long entityId, String title, String releaseDate,
                               double voteAverage, String genres, String directors, String casts) {
        String table = "tv".equals(mediaType) ? "tvShow" : "movie";
        String dateColumn = "tv".equals(mediaType) ? "firstAirDate" : "releaseDate";
        String trimmed = title == null ? "" : title.trim();
        boolean rename = !trimmed.isEmpty();
        SQLiteDatabase db = helper.getWritableDatabase();
        try (android.database.sqlite.SQLiteStatement statement = db.compileStatement(
                "UPDATE " + table + " SET " + (rename ? "title = ?, " : "") + dateColumn
                        + " = ?, voteAverage = ?, genres = ?, directors = ?, casts = ?, isLocked = 1"
                        + " WHERE id = ?")) {
            int index = 1;
            if (rename) {
                statement.bindString(index++, trimmed);
            }
            statement.bindString(index++, releaseDate == null ? "" : releaseDate.trim());
            statement.bindDouble(index++, voteAverage);
            statement.bindString(index++, genres == null ? "" : genres.trim());
            statement.bindString(index++, directors == null ? "" : directors.trim());
            statement.bindString(index++, casts == null ? "" : casts.trim());
            statement.bindLong(index, entityId);
            statement.executeUpdateDelete();
        }
    }

    /**
     * 自定义编辑里的「换海报」：直接改这条实体的海报地址并锁定，之后自动刮削不会再把它覆盖回去。
     * 传空串等于撤掉自定义海报，海报退回占位字母，等下一次刮削重新填。
     */
    public void setPosterOverride(String mediaType, long entityId, String posterUrl) {
        String table = "tv".equals(mediaType) ? "tvShow" : "movie";
        helper.getWritableDatabase().execSQL("UPDATE " + table
                + " SET posterUrl = ?, isLocked = ? WHERE id = ?",
                new Object[]{posterUrl == null ? "" : posterUrl.trim(),
                        posterUrl == null || posterUrl.trim().isEmpty() ? 0 : 1, entityId});
    }

    private long insertScrapedEntity(SQLiteDatabase db, String mediaType, TmdbClient.Result result) {
        String table = "tv".equals(mediaType) ? "tvShow" : "movie";
        String dateColumn = "tv".equals(mediaType) ? "firstAirDate" : "releaseDate";
        try (android.database.sqlite.SQLiteStatement insert = db.compileStatement(
                "INSERT INTO " + table + " (title, " + dateColumn + ", overview, posterUrl, voteAverage,"
                        + " isLocked, scraped, tmdbId) VALUES (?,?,?,?,?,1,1,?)")) {
            insert.bindString(1, result.title == null || result.title.isEmpty()
                    ? String.valueOf(result.id) : result.title);
            insert.bindString(2, result.releaseDate == null ? "" : result.releaseDate);
            insert.bindString(3, result.overview == null ? "" : result.overview);
            insert.bindString(4, result.basePosterUrl() == null ? "" : result.basePosterUrl());
            insert.bindDouble(5, result.voteAverage);
            insert.bindLong(6, result.id);
            return insert.executeInsert();
        }
    }

    private static String[] readCredits(SQLiteDatabase db, String table, long entityId) {
        try (Cursor cursor = db.rawQuery("SELECT genres, directors, casts FROM " + table + " WHERE id = ?",
                new String[]{String.valueOf(entityId)})) {
            if (cursor.moveToFirst()) {
                return new String[]{nullToEmpty(cursor.getString(0)), nullToEmpty(cursor.getString(1)),
                        nullToEmpty(cursor.getString(2))};
            }
        }
        return new String[]{"", "", ""};
    }

    /** 名单已有内容就保留（可能是用户手填的），只在新值非空时补上。 */
    private static String keepExisting(String existing, String fresh) {
        if (existing != null && !existing.isEmpty()) {
            return existing;
        }
        return fresh == null ? "" : fresh;
    }

    /** 实体是否已经刮到元数据（movie / tvShow 的 scraped 标记）。 */
    private boolean isScraped(SQLiteDatabase db, String mediaType, long entityId) {
        String table = "tv".equals(mediaType) ? "tvShow" : "movie";
        try (Cursor cursor = db.rawQuery("SELECT scraped FROM " + table + " WHERE id = ?",
                new String[]{String.valueOf(entityId)})) {
            return cursor.moveToFirst() && cursor.getInt(0) == 1;
        }
    }

    /** Re-queues a scrape for one entity, optionally with a user-supplied search query. */
    public void requeueScrape(String mediaType, long entityId, String query, String year,
                              String secondaryQuery, int preferredSeason) {
        SQLiteDatabase db = helper.getWritableDatabase();
        db.delete("scrapeQueue", "mediaType = ? AND entityId = ?", new String[]{mediaType, String.valueOf(entityId)});
        // 用户主动要求重刮，说明他要的就是新结果：解锁，否则这一刮会被锁标记挡住。
        db.execSQL("UPDATE " + ("tv".equals(mediaType) ? "tvShow" : "movie")
                + " SET isLocked = 0 WHERE id = ?", new Object[]{entityId});
        // 手动重新匹配：即使已经刮过也要强制再刮一次。
        enqueueScrape(mediaType, entityId, query, year, secondaryQuery, preferredSeason, "", true);
    }

    public StoredEntity entity(String mediaType, long entityId) {
        String table = mediaType.equals("tv") ? "tvShow" : "movie";
        String dateColumn = mediaType.equals("tv") ? "firstAirDate" : "releaseDate";
        SQLiteDatabase db = helper.getReadableDatabase();
        try (Cursor cursor = db.rawQuery("SELECT id, title, " + dateColumn + ", overview, posterUrl, "
                + "voteAverage, scraped, tmdbId, genres, directors, casts FROM " + table + " WHERE id = ?",
                new String[]{String.valueOf(entityId)})) {
            if (cursor.moveToFirst()) {
                return readEntity(cursor, mediaType.equals("tv"));
            }
        }
        return null;
    }

    public List<StoredEntity> allEntities() {
        List<StoredEntity> result = new ArrayList<>();
        SQLiteDatabase db = helper.getReadableDatabase();
        try (Cursor cursor = db.rawQuery(
                "SELECT id, title, releaseDate, overview, posterUrl, voteAverage, scraped, tmdbId, "
                        + "genres, directors, casts FROM movie "
                        + "ORDER BY title COLLATE NOCASE", null)) {
            while (cursor.moveToNext()) {
                result.add(readEntity(cursor, false));
            }
        }
        try (Cursor cursor = db.rawQuery(
                "SELECT id, title, firstAirDate, overview, posterUrl, voteAverage, scraped, tmdbId, "
                        + "genres, directors, casts FROM tvShow "
                        + "ORDER BY title COLLATE NOCASE", null)) {
            while (cursor.moveToNext()) {
                result.add(readEntity(cursor, true));
            }
        }
        return result;
    }

    private static StoredEntity readEntity(Cursor cursor, boolean isTv) {
        return new StoredEntity(
                cursor.getLong(0),
                cursor.getString(1),
                nullToEmpty(cursor.getString(2)),
                nullToEmpty(cursor.getString(3)),
                nullToEmpty(cursor.getString(4)),
                cursor.getDouble(5),
                isTv,
                cursor.getInt(6) != 0,
                cursor.getLong(7),
                nullToEmpty(cursor.getString(8)),
                nullToEmpty(cursor.getString(9)),
                nullToEmpty(cursor.getString(10)));
    }

    // ---------------------------------------------------------------- 豆瓣条目

    /** 库里存的那份豆瓣资料（列表字段已经是「 / 」拼好的一整串）。 */
    public static final class DoubanRow {
        public final String subjectId;
        public final String subjectUrl;
        public final String title;
        public final String originalTitle;
        public final String year;
        public final double rating;
        public final int ratingCount;
        public final String summary;
        public final String genres;
        public final String countries;
        public final String directors;
        public final String casts;
        public final String posterUrl;

        DoubanRow(String subjectId, String subjectUrl, String title, String originalTitle,
                  String year, double rating, int ratingCount, String summary, String genres,
                  String countries, String directors, String casts, String posterUrl) {
            this.subjectId = subjectId;
            this.subjectUrl = subjectUrl;
            this.title = title;
            this.originalTitle = originalTitle;
            this.year = year;
            this.rating = rating;
            this.ratingCount = ratingCount;
            this.summary = summary;
            this.genres = genres;
            this.countries = countries;
            this.directors = directors;
            this.casts = casts;
            this.posterUrl = posterUrl;
        }
    }

    private static String doubanKey(String mediaType, long entityId) {
        return ("tv".equals(mediaType) ? "tv" : "movie") + "#" + entityId;
    }

    /** 存一份豆瓣资料；条目还什么都没刮到时，用豆瓣那份把片名 / 年份 / 简介顶上。 */
    public void saveDouban(String mediaType, long entityId, DoubanClient.Subject subject) {
        SQLiteDatabase db = helper.getWritableDatabase();
        String table = "tv".equals(mediaType) ? "tvShow" : "movie";
        String dateColumn = "tv".equals(mediaType) ? "firstAirDate" : "releaseDate";
        try (android.database.sqlite.SQLiteStatement statement = db.compileStatement(
                "INSERT OR REPLACE INTO doubanMetadata (key, subjectId, subjectUrl, title,"
                        + " originalTitle, year, rating, ratingCount, summary, genres, countries,"
                        + " directors, casts, posterUrl, updatedAt) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)")) {
            statement.bindString(1, doubanKey(mediaType, entityId));
            statement.bindString(2, subject.subjectId);
            statement.bindString(3, subject.subjectUrl);
            statement.bindString(4, subject.title);
            statement.bindString(5, subject.originalTitle);
            statement.bindString(6, subject.year);
            statement.bindDouble(7, subject.rating);
            statement.bindLong(8, subject.ratingCount);
            statement.bindString(9, subject.summary);
            statement.bindString(10, joinWithSlash(subject.genres));
            statement.bindString(11, joinWithSlash(subject.countries));
            statement.bindString(12, joinWithPipe(subject.directors));
            statement.bindString(13, joinWithPipe(subject.casts));
            statement.bindString(14, subject.posterUrl);
            statement.bindLong(15, System.currentTimeMillis() / 1000L);
            statement.executeUpdateDelete();
        }
        // 海报：只在条目本来没有海报时才换（mac 版 applyPosterToMovie 同一取舍）。
        try (Cursor cursor = db.rawQuery("SELECT posterUrl, directors, casts, scraped, tmdbId"
                + " FROM " + table + " WHERE id = ?", new String[]{String.valueOf(entityId)})) {
            if (cursor.moveToFirst()) {
                String poster = nullToEmpty(cursor.getString(0));
                if (poster.isEmpty() && !subject.posterUrl.isEmpty()) {
                    db.execSQL("UPDATE " + table + " SET posterUrl = ? WHERE id = ?",
                            new Object[]{subject.posterUrl, entityId});
                }
                if (nullToEmpty(cursor.getString(1)).isEmpty() && !subject.directors.isEmpty()) {
                    db.execSQL("UPDATE " + table + " SET directors = ? WHERE id = ?",
                            new Object[]{joinWithPipe(subject.directors), entityId});
                }
                if (nullToEmpty(cursor.getString(2)).isEmpty() && !subject.casts.isEmpty()) {
                    db.execSQL("UPDATE " + table + " SET casts = ? WHERE id = ?",
                            new Object[]{joinWithPipe(subject.casts), entityId});
                }
                // 条目连 TMDB 都没匹配上（scraped=0 或 tmdbId=0）：豆瓣这份就是它的资料，
                // 顶上并锁住，首页那条「待刮削」才不会一直挂着。
                if (cursor.getInt(3) == 0 || cursor.getLong(4) == 0) {
                    db.execSQL("UPDATE " + table + " SET title = ?, " + dateColumn + " = ?,"
                                    + " overview = ?, scraped = 1, isLocked = 1 WHERE id = ?",
                            new Object[]{subject.title, subject.year, subject.summary, entityId});
                }
            }
        }
    }

    public DoubanRow doubanRow(String mediaType, long entityId) {
        try (Cursor cursor = helper.getReadableDatabase().rawQuery(
                "SELECT subjectId, subjectUrl, title, originalTitle, year, rating, ratingCount,"
                        + " summary, genres, countries, directors, casts, posterUrl"
                        + " FROM doubanMetadata WHERE key = ?",
                new String[]{doubanKey(mediaType, entityId)})) {
            if (!cursor.moveToFirst()) {
                return null;
            }
            return new DoubanRow(nullToEmpty(cursor.getString(0)), nullToEmpty(cursor.getString(1)),
                    nullToEmpty(cursor.getString(2)), nullToEmpty(cursor.getString(3)),
                    nullToEmpty(cursor.getString(4)), cursor.getDouble(5), cursor.getInt(6),
                    nullToEmpty(cursor.getString(7)), nullToEmpty(cursor.getString(8)),
                    nullToEmpty(cursor.getString(9)), nullToEmpty(cursor.getString(10)),
                    nullToEmpty(cursor.getString(11)), nullToEmpty(cursor.getString(12)));
        }
    }

    public void deleteDouban(String mediaType, long entityId) {
        helper.getWritableDatabase().delete("doubanMetadata", "key = ?",
                new String[]{doubanKey(mediaType, entityId)});
    }

    /** 整库豆瓣评分：一次查完，首页那份名单按「movie#3」这样的键取。 */
    public Map<String, Double> doubanRatings() {
        Map<String, Double> result = new HashMap<>();
        try (Cursor cursor = helper.getReadableDatabase().rawQuery(
                "SELECT key, rating FROM doubanMetadata WHERE rating > 0", null)) {
            while (cursor.moveToNext()) {
                result.put(nullToEmpty(cursor.getString(0)), cursor.getDouble(1));
            }
        }
        return result;
    }

    private static String joinWithSlash(List<String> values) {
        return values == null || values.isEmpty() ? "" : String.join(" / ", values);
    }

    private static String joinWithPipe(List<String> values) {
        return values == null || values.isEmpty() ? "" : String.join("|", values);
    }

    // ---------------------------------------------------------------- files

    public void insertVideoFile(ScannedFile file, long movieId, long showId, String mediaKind) {
        SQLiteDatabase db = helper.getWritableDatabase();
        try (android.database.sqlite.SQLiteStatement statement = db.compileStatement(
                "INSERT OR IGNORE INTO videoFile (id, movieId, showId, season, episode, episodeTitle, "
                        + "episodeSubtitle, relativePath, fileName, mediaKind) VALUES (?,?,?,?,?,?,?,?,?,?)")) {
            statement.bindString(1, file.fileId);
            statement.bindLong(2, movieId);
            statement.bindLong(3, showId);
            statement.bindLong(4, file.season);
            statement.bindLong(5, file.episode);
            statement.bindString(6, "");
            statement.bindString(7, "");
            statement.bindString(8, file.relativePath);
            statement.bindString(9, file.fileName);
            statement.bindString(10, mediaKind);
            statement.executeUpdateDelete();
        }
        // 行已经存在时 INSERT OR IGNORE 什么也不做：解析规则升级后（比如 S00 现在算特别篇），
        // 那条文件会永远挂在第一次入库时的那一季上。文件还在，就按这次的解析改判一次。
        try (android.database.sqlite.SQLiteStatement refresh = db.compileStatement(
                "UPDATE videoFile SET season = ?, episode = ? WHERE id = ?"
                        + " AND (season != ? OR episode != ?)")) {
            refresh.bindLong(1, file.season);
            refresh.bindLong(2, file.episode);
            refresh.bindString(3, file.fileId);
            refresh.bindLong(4, file.season);
            refresh.bindLong(5, file.episode);
            refresh.executeUpdateDelete();
        }
    }

    public int removeMissingFiles(List<String> currentFileIds) {
        return removeMissingFiles(currentFileIds, null);
    }

    /**
     * 清掉「这一轮扫描没再见到」的文件行。传 {@code scannedSourceIds} 就只在这些源的范围里清：
     * 停用的源这一轮根本没去扫，它的文件行不该被当成「盘上没了」。
     */
    public int removeMissingFiles(List<String> currentFileIds, Set<Integer> scannedSourceIds) {
        Set<String> keep = new HashSet<>(currentFileIds);
        List<String> stale = new ArrayList<>();
        try (Cursor cursor = helper.getReadableDatabase().rawQuery("SELECT id FROM videoFile", null)) {
            while (cursor.moveToNext()) {
                String id = cursor.getString(0);
                if (keep.contains(id)) {
                    continue;
                }
                if (scannedSourceIds != null && !scannedSourceIds.contains(sourceIdOf(id))) {
                    continue;
                }
                stale.add(id);
            }
        }
        if (stale.isEmpty()) {
            return 0;
        }
        SQLiteDatabase db = helper.getWritableDatabase();
        db.beginTransaction();
        try {
            for (String fileId : stale) {
                db.execSQL("DELETE FROM videoFile WHERE id = ?", new Object[]{fileId});
                db.execSQL("DELETE FROM offlineCache WHERE fileId = ?", new Object[]{fileId});
                progressOverrides.remove(fileId);
            }
            // Files deleted on disk take their card with them, exactly as on the mac.
            pruneOrphanEntities(db);
            db.setTransactionSuccessful();
        } finally {
            db.endTransaction();
        }
        return stale.size();
    }

    /**
     * Forgets everything one media source left in the library: its files, the movies and shows no
     * file points at any more, their episodes, the scrape-queue entries waiting for them, the
     * cached copies and the in-memory playback overrides. Mirrors the mac app, which deletes a
     * removed source's rows by source id and prunes the entities left without a file; without this
     * the wall keeps showing posters of a share that is already gone.
     *
     * @return how many video files were dropped
     */
    public int purgeSource(int sourceId) {
        // File ids are built by LocalEngine.fileId: "local-<sourceId>:<relative path>".
        String prefix = "local-" + sourceId + ":";
        List<String> owned = new ArrayList<>();
        try (Cursor cursor = helper.getReadableDatabase().rawQuery("SELECT id FROM videoFile", null)) {
            while (cursor.moveToNext()) {
                String id = cursor.getString(0);
                if (id != null && id.startsWith(prefix)) {
                    owned.add(id);
                }
            }
        }
        return deleteFileRows(owned);
    }

    /**
     * 删掉所有「媒体源已经不在了」的文件行。扫描进行中移除媒体源时，那一轮扫描仍会把它
     * 扫到的文件登记回来，只靠 {@link #purgeSource(int)} 清不干净，海报会一直活到下一次扫描。
     */
    public int purgeUnknownSources(java.util.Collection<Integer> liveSourceIds) {
        List<String> stale = new ArrayList<>();
        try (Cursor cursor = helper.getReadableDatabase().rawQuery("SELECT id FROM videoFile", null)) {
            while (cursor.moveToNext()) {
                String id = cursor.getString(0);
                int sourceId = sourceIdOf(id);
                if (sourceId >= 0 && (liveSourceIds == null || !liveSourceIds.contains(sourceId))) {
                    stale.add(id);
                }
            }
        }
        return deleteFileRows(stale);
    }

    /** fileId 里的媒体源 id；不是 {@code local-<id>:<路径>} 形态的返回 -1。 */
    private static int sourceIdOf(String fileId) {
        if (fileId == null || !fileId.startsWith("local-")) {
            return -1;
        }
        int colon = fileId.indexOf(':');
        if (colon <= 6) {
            return -1;
        }
        try {
            return Integer.parseInt(fileId.substring("local-".length(), colon));
        } catch (NumberFormatException ignored) {
            return -1;
        }
    }

    /** 一批文件行连同进度覆盖与离线记录一起删掉，再裁掉因此变空的实体。 */
    private int deleteFileRows(List<String> fileIds) {
        if (fileIds.isEmpty()) {
            return 0;
        }
        SQLiteDatabase db = helper.getWritableDatabase();
        db.beginTransaction();
        try {
            for (String fileId : fileIds) {
                db.delete("videoFile", "id = ?", new String[]{fileId});
                db.delete("offlineCache", "fileId = ?", new String[]{fileId});
                progressOverrides.remove(fileId);
            }
            pruneOrphanEntities(db);
            db.setTransactionSuccessful();
        } finally {
            db.endTransaction();
        }
        return fileIds.size();
    }

    /**
     * Drops movies, shows and their episodes that no video file references any more, plus the
     * scrape-queue rows left pointing at a file that no longer exists. Entities are what the wall
     * is built from, so this is what makes a card disappear once its last file is gone.
     */
    private void pruneOrphanEntities(SQLiteDatabase db) {
        db.execSQL("DELETE FROM movie WHERE id NOT IN "
                + "(SELECT movieId FROM videoFile WHERE movieId IS NOT NULL)");
        db.execSQL("DELETE FROM tvShow WHERE id NOT IN "
                + "(SELECT showId FROM videoFile WHERE showId IS NOT NULL)");
        db.execSQL("DELETE FROM tvEpisode WHERE showId NOT IN (SELECT id FROM tvShow)");
        db.execSQL("DELETE FROM scrapeQueue WHERE sourceFileId <> '' "
                + "AND sourceFileId NOT IN (SELECT id FROM videoFile)");
        // 指向已经不存在的条目的队列行：刮不到、也永远删不掉，「待刮削 N」就是这么卡住的。
        db.execSQL("DELETE FROM scrapeQueue WHERE "
                + "(mediaType = 'movie' AND entityId NOT IN (SELECT id FROM movie)) "
                + "OR (mediaType = 'tv' AND entityId NOT IN (SELECT id FROM tvShow))");
    }

    /**
     * 首页「所有影视 N 部」用这一条 COUNT 就能刷新。整表重读要几千次查询，扫描期间
     * 根本追不上登记速度，那个数字就会看着停住；计数是索引上的两条 COUNT，随时可查。
     */
    public int entityCount() {
        return entityCount(null);
    }

    /**
     * 首页那个「N 部」。传 {@code enabledSourceIds} 就只数这些源还挂着文件的条目——
     * 与 mac 版一致，媒体库查询一律带 {@code mediaSource.isEnabled = 1}，停用的源
     * 在首页就该消失，而不是等下一轮扫描把它连同文件一起删掉。
     */
    public int entityCount(Set<Integer> enabledSourceIds) {
        SQLiteDatabase db = helper.getReadableDatabase();
        String clause = sourceClause(enabledSourceIds);
        String[] args = sourceArgs(enabledSourceIds);
        int total = 0;
        try (Cursor cursor = db.rawQuery("SELECT COUNT(DISTINCT movieId) FROM videoFile "
                + "WHERE movieId IS NOT NULL" + clause, args)) {
            if (cursor.moveToFirst()) {
                total += cursor.getInt(0);
            }
        }
        try (Cursor cursor = db.rawQuery("SELECT COUNT(DISTINCT showId) FROM videoFile "
                + "WHERE showId IS NOT NULL" + clause, args)) {
            if (cursor.moveToFirst()) {
                total += cursor.getInt(0);
            }
        }
        return total;
    }

    /**
     * 某个源已经入库的相对路径。扫描拿它跳过「上一轮就扫到过的文件」：文件行不用重插、
     * 实体不用重绑、刮削队列也不用重填。
     */
    public Set<String> knownRelativePaths(int sourceId) {
        Set<String> result = new HashSet<>();
        try (Cursor cursor = helper.getReadableDatabase().rawQuery(
                "SELECT relativePath FROM videoFile WHERE id LIKE ?",
                new String[]{"local-" + sourceId + ":%"})) {
            while (cursor.moveToNext()) {
                result.add(cursor.getString(0));
            }
        }
        return result;
    }

    /**
     * {@code sources} 为 null 表示不加这个条件；空集合表示「一个源都不算」——全部媒体源
     * 都停用时就是这个局面，这时候不能退化成不过滤，否则首页照样摆满停用源的海报。
     * 文件行主键形如 {@code local-<sourceId>:<路径>}。
     */
    private static String sourceClause(Set<Integer> sources) {
        if (sources == null) {
            return "";
        }
        if (sources.isEmpty()) {
            return " AND 0";
        }
        StringBuilder builder = new StringBuilder(" AND (");
        int index = 0;
        for (Integer id : sources) {
            if (index++ > 0) {
                builder.append(" OR ");
            }
            builder.append("id LIKE ?");
        }
        return builder.append(')').toString();
    }

    private static String[] sourceArgs(Set<Integer> sources) {
        if (sources == null || sources.isEmpty()) {
            return new String[0];
        }
        List<String> args = new ArrayList<>();
        for (Integer id : sources) {
            args.add("local-" + id + ":%");
        }
        return args.toArray(new String[0]);
    }

    public List<StoredFile> filesForMovie(long movieId) {
        return queryFiles("movieId", movieId);
    }

    public List<StoredFile> filesForShow(long showId) {
        return queryFiles("showId", showId);
    }

    /**
     * 一次查询取回全部文件行并按实体分组，替掉「每个条目再查一次」那种 N+1 读法。
     * 媒体库上千条时，海报墙每 0.7 秒重读一次，N+1 就是几千次查询，重读本身比扫描还慢，
     * 首页看着就是「数字不动了」。
     *
     * @return key 为实体 id，value 为该实体名下的文件行；电影与剧集各查一遍。
     */
    public Map<Long, List<StoredFile>> allFilesGrouped(boolean isTv) {
        return allFilesGrouped(isTv, null);
    }

    /** {@code enabledSourceIds} 非空时，停用源的文件不进这份分组（首页因此不再显示它们）。 */
    public Map<Long, List<StoredFile>> allFilesGrouped(boolean isTv, Set<Integer> enabledSourceIds) {
        Map<Long, List<StoredFile>> grouped = new LinkedHashMap<>();
        String column = isTv ? "showId" : "movieId";
        SQLiteDatabase db = helper.getReadableDatabase();
        try (Cursor cursor = db.rawQuery(
                "SELECT id, movieId, showId, relativePath, fileName, mediaKind, season, episode, "
                        + "episodeTitle, episodeSubtitle, playProgress, duration, isWatched FROM videoFile "
                        + "WHERE " + column + " IS NOT NULL" + sourceClause(enabledSourceIds)
                        + " ORDER BY " + column + ", season ASC, episode ASC, fileName COLLATE NOCASE",
                sourceArgs(enabledSourceIds))) {
            while (cursor.moveToNext()) {
                StoredFile file = readFile(cursor);
                long entityId = isTv ? file.showId : file.movieId;
                List<StoredFile> bucket = grouped.get(entityId);
                if (bucket == null) {
                    bucket = new ArrayList<>();
                    grouped.put(entityId, bucket);
                }
                bucket.add(file);
            }
        }
        return grouped;
    }

    private StoredFile readFile(Cursor cursor) {
        String fileId = cursor.getString(0);
        double progress = cursor.getDouble(10);
        double duration = cursor.getDouble(11);
        double[] override = progressOverrides.get(fileId);
        if (override != null) {
            progress = Math.max(progress, override[0]);
            duration = Math.max(duration, override[1]);
        }
        return new StoredFile(
                fileId,
                cursor.getLong(1),
                cursor.getLong(2),
                nullToEmpty(cursor.getString(3)),
                nullToEmpty(cursor.getString(4)),
                nullToEmpty(cursor.getString(5)),
                cursor.getInt(6),
                cursor.getInt(7),
                nullToEmpty(cursor.getString(8)),
                nullToEmpty(cursor.getString(9)),
                progress,
                duration,
                cursor.getInt(12) != 0);
    }

    private List<StoredFile> queryFiles(String column, long entityId) {
        List<StoredFile> result = new ArrayList<>();
        SQLiteDatabase db = helper.getReadableDatabase();
        try (Cursor cursor = db.rawQuery(
                "SELECT id, movieId, showId, relativePath, fileName, mediaKind, season, episode, "
                        + "episodeTitle, episodeSubtitle, playProgress, duration, isWatched FROM videoFile "
                        + "WHERE " + column + " = ? "
                        + "ORDER BY season ASC, episode ASC, fileName COLLATE NOCASE",
                new String[]{String.valueOf(entityId)})) {
            while (cursor.moveToNext()) {
                result.add(readFile(cursor));
            }
        }
        return result;
    }

    public void updatePlaybackState(String fileId, double positionSeconds, double durationSeconds) {
        double safePosition = Math.max(0, positionSeconds);
        double safeDuration = Math.max(0, durationSeconds);
        boolean watched = safeDuration > 0 && safePosition / safeDuration >= 0.95;
        double now = System.currentTimeMillis() / 1000.0;
        SQLiteDatabase db = helper.getWritableDatabase();
        try (android.database.sqlite.SQLiteStatement statement = db.compileStatement(
                "UPDATE videoFile SET playProgress = ?, duration = ?, isWatched = ?, lastPlayedAt = ? "
                        + "WHERE id = ? AND (duration = 0 OR playProgress <= ?)")) {
            statement.bindDouble(1, watched ? safeDuration : safePosition);
            statement.bindDouble(2, safeDuration);
            statement.bindLong(3, watched ? 1 : 0);
            statement.bindDouble(4, now);
            statement.bindString(5, fileId);
            statement.bindDouble(6, safePosition);
            statement.executeUpdateDelete();
        }
        progressOverrides.put(fileId, new double[]{safePosition, safeDuration});
    }

    public void setWatched(String fileId, boolean watched, double durationSeconds) {
        double now = System.currentTimeMillis() / 1000.0;
        SQLiteDatabase db = helper.getWritableDatabase();
        try (android.database.sqlite.SQLiteStatement statement = db.compileStatement(
                "UPDATE videoFile SET isWatched = ?, playProgress = CASE WHEN ? THEN ? ELSE playProgress END, "
                        + "lastPlayedAt = ? WHERE id = ?")) {
            statement.bindLong(1, watched ? 1 : 0);
            statement.bindLong(2, watched ? 1 : 0);
            statement.bindDouble(3, Math.max(0, durationSeconds));
            statement.bindDouble(4, now);
            statement.bindString(5, fileId);
            statement.executeUpdateDelete();
        }
        progressOverrides.put(fileId, new double[]{
                watched ? Math.max(0, durationSeconds) : 0, Math.max(0, durationSeconds)});
    }

    // ---------------------------------------------------------------- scrape queue

    public void enqueueScrape(String mediaType, long entityId, String query, String year,
                              String secondaryQuery, int preferredSeason, String sourceFileId) {
        enqueueScrape(mediaType, entityId, query, year, secondaryQuery, preferredSeason, sourceFileId, false);
    }

    /**
     * 扫描入队只管「还没刮到元数据」的实体，和 mac 版一样只刮需要刮削的新文件。
     * 否则每次冷启都要把整库重刮一遍，白白发一堆 TMDB 请求，左上角提示也反复刷屏。
     */
    public void enqueueScrape(String mediaType, long entityId, String query, String year,
                              String secondaryQuery, int preferredSeason, String sourceFileId,
                              boolean force) {
        SQLiteDatabase db = helper.getWritableDatabase();
        db.delete("scrapeQueue", "mediaType = ? AND entityId = ?",
                new String[]{mediaType, String.valueOf(entityId)});
        // 一个实体在队列里只留一条；已经刮到元数据的直接跳过，除非调用方强制重刮。
        if (!force && isScraped(db, mediaType, entityId)) {
            return;
        }
        try (android.database.sqlite.SQLiteStatement statement = db.compileStatement(
                "INSERT INTO scrapeQueue (mediaType, entityId, query, year, secondaryQuery, preferredSeason, sourceFileId) "
                        + "VALUES (?,?,?,?,?,?,?)")) {
            statement.bindString(1, mediaType);
            statement.bindLong(2, entityId);
            statement.bindString(3, query);
            statement.bindString(4, year == null ? "" : year);
            statement.bindString(5, secondaryQuery == null ? "" : secondaryQuery);
            statement.bindLong(6, preferredSeason);
            statement.bindString(7, sourceFileId == null ? "" : sourceFileId);
            statement.executeUpdateDelete();
        }
    }

    /**
     * Takes the next batch of pending scrapes and removes exactly those rows, so a queue longer
     * than one batch keeps the rest instead of losing it (the old code cleared the whole table).
     */
    public List<Map<String, Object>> nextScrapeBatches(int limit) {
        List<Map<String, Object>> batches = new ArrayList<>();
        List<Object> args = new ArrayList<>();
        SQLiteDatabase db = helper.getReadableDatabase();
        String sql = "SELECT id, mediaType, entityId, query, year, secondaryQuery, "
                + "preferredSeason, sourceFileId "
                + "FROM scrapeQueue GROUP BY mediaType, entityId ORDER BY id ASC LIMIT "
                + Math.max(1, limit);
        try (Cursor cursor = db.rawQuery(sql, null)) {
            while (cursor.moveToNext()) {
                args.add(cursor.getLong(0));
                Map<String, Object> batch = new HashMap<>();
                batch.put("mediaType", cursor.getString(1));
                batch.put("entityId", cursor.getLong(2));
                batch.put("query", cursor.getString(3));
                batch.put("year", cursor.getString(4));
                batch.put("secondaryQuery", cursor.getString(5));
                batch.put("preferredSeason", cursor.getInt(6));
                batch.put("sourceFileId", cursor.getString(7));
                batches.add(batch);
            }
        }
        if (!args.isEmpty()) {
            StringBuilder placeholders = new StringBuilder();
            for (int index = 0; index < args.size(); index++) {
                placeholders.append(index == 0 ? "?" : ",?");
            }
            db.execSQL("DELETE FROM scrapeQueue WHERE id IN (" + placeholders + ")", args.toArray());
        }
        return batches;
    }

    /** How many entities are still waiting for metadata, counting each entity once. */
    /**
     * 把「还没刮到资料、又没被手动锁定」的实体重新排进队列，返回新增的条目数。
     *
     * <p>mac 版没有「队列行」这回事：每轮刮削都从库里现算未刮削清单（
     * {@code processUnmatchedFiles(sourceID:)}），所以任何一次网络失败都会在下一轮自愈。
     * TV 版的队列是表，取批次时按行删掉，一次请求失败就等于这条目被踢出队列，
     * 之后再扫也不会有人再替它发一次请求——海报就永远停在占位字母上。这一句把两边对齐。</p>
     */
    public int requeueUnscraped() {
        SQLiteDatabase db = helper.getWritableDatabase();
        int before = queueRowCount();
        db.execSQL("INSERT INTO scrapeQueue (mediaType, entityId, query, year, secondaryQuery, "
                + "preferredSeason, sourceFileId) "
                + "SELECT 'movie', m.id, m.title, COALESCE(substr(m.releaseDate, 1, 4), ''), '', 0, "
                + "COALESCE((SELECT f.id FROM videoFile f WHERE f.movieId = m.id LIMIT 1), '') "
                + "FROM movie m WHERE m.scraped = 0 AND m.isLocked = 0 "
                + "AND m.title IS NOT NULL AND m.title <> '' "
                + "AND NOT EXISTS (SELECT 1 FROM scrapeQueue q "
                + "WHERE q.mediaType = 'movie' AND q.entityId = m.id)");
        db.execSQL("INSERT INTO scrapeQueue (mediaType, entityId, query, year, secondaryQuery, "
                + "preferredSeason, sourceFileId) "
                + "SELECT 'tv', s.id, s.title, COALESCE(substr(s.firstAirDate, 1, 4), ''), '', "
                + "COALESCE((SELECT MIN(f.season) FROM videoFile f WHERE f.showId = s.id), 0), "
                + "COALESCE((SELECT f.id FROM videoFile f WHERE f.showId = s.id LIMIT 1), '') "
                + "FROM tvShow s WHERE s.scraped = 0 AND s.isLocked = 0 "
                + "AND s.title IS NOT NULL AND s.title <> '' "
                + "AND NOT EXISTS (SELECT 1 FROM scrapeQueue q "
                + "WHERE q.mediaType = 'tv' AND q.entityId = s.id)");
        return queueRowCount() - before;
    }

    /**
     * 代表文件：刮前阶段按它现算关键词与年份（mac 版每轮都从 {@code file.relativePath}
     * 重取，TV 版以前只用扫描时存下的那份快照）。优先用队列行记下的那个文件，取不到
     * 再随便拿该实体名下的一个。
     *
     * @return {@code {relativePath, fileName}}，没有文件行时返回 {@code null}
     */
    public String[] representativeFile(String mediaType, long entityId, String preferredFileId) {
        SQLiteDatabase db = helper.getReadableDatabase();
        if (preferredFileId != null && !preferredFileId.isEmpty()) {
            try (Cursor cursor = db.rawQuery(
                    "SELECT relativePath, fileName FROM videoFile WHERE id = ?",
                    new String[]{preferredFileId})) {
                if (cursor.moveToFirst()) {
                    return new String[]{cursor.getString(0), cursor.getString(1)};
                }
            }
        }
        String column = "tv".equals(mediaType) ? "showId" : "movieId";
        try (Cursor cursor = db.rawQuery("SELECT relativePath, fileName FROM videoFile WHERE "
                + column + " = ? ORDER BY id LIMIT 1", new String[]{String.valueOf(entityId)})) {
            if (cursor.moveToFirst()) {
                return new String[]{cursor.getString(0), cursor.getString(1)};
            }
        }
        return null;
    }

    /** 合并指定实体所在的重复工作条目，{@code preferredId} 那条留下。 */
    public int collapseDuplicateWorks(String mediaType, long preferredId) {
        return collapseDuplicateWorks(mediaType, preferredId, 0);
    }

    /**
     * 同一个 TMDB 作品被建成了两条实体（同一部剧的两季分别放在两个目录、或者中英文
     * 目录名各自算出了一个标题），首页就是两张一模一样的海报。mac 版的作品表直接以
     * TMDB id 当主键，结构上不可能重复；TV 版的实体 id 是自增的，只能在写入后收拢一次。
     *
     * <p>留哪条：{@code preferredId} 在场就留它（刚刮到资料、带演职人员），否则留挂着文件
     * 多的那条，播放进度不会因为合并丢失；剧集分集行一并搬过去。</p>
     */
    public int collapseDuplicateWorks(String mediaType, long preferredId, long onlyTmdbId) {
        String table = "tv".equals(mediaType) ? "tvShow" : "movie";
        String fileColumn = "tv".equals(mediaType) ? "showId" : "movieId";
        SQLiteDatabase db = helper.getWritableDatabase();
        List<Long> works = new ArrayList<>();
        try (Cursor cursor = db.rawQuery("SELECT tmdbId FROM " + table + " WHERE tmdbId > 0"
                + (onlyTmdbId > 0 ? " AND tmdbId = " + onlyTmdbId : "")
                + " GROUP BY tmdbId HAVING COUNT(*) > 1", null)) {
            while (cursor.moveToNext()) {
                works.add(cursor.getLong(0));
            }
        }
        int merged = 0;
        for (Long tmdbId : works) {
            List<Long> ids = new ArrayList<>();
            try (Cursor cursor = db.rawQuery("SELECT id FROM " + table + " WHERE tmdbId = ?"
                            + " ORDER BY (SELECT COUNT(*) FROM videoFile f WHERE f." + fileColumn
                            + " = " + table + ".id) DESC, id ASC", new String[]{String.valueOf(tmdbId)})) {
                while (cursor.moveToNext()) {
                    ids.add(cursor.getLong(0));
                }
            }
            long survivor = ids.contains(preferredId) ? preferredId : ids.get(0);
            for (Long loser : ids) {
                if (loser == survivor) {
                    continue;
                }
                db.execSQL("UPDATE videoFile SET " + fileColumn + " = ? WHERE " + fileColumn + " = ?",
                        new Object[]{survivor, loser});
                if ("tv".equals(mediaType)) {
                    db.execSQL("INSERT OR IGNORE INTO tvEpisode (showId, season, episode, title,"
                                    + " overview, airDate, stillUrl, updatedAt) SELECT ?, season,"
                                    + " episode, title, overview, airDate, stillUrl, updatedAt"
                                    + " FROM tvEpisode WHERE showId = ?",
                            new Object[]{survivor, loser});
                    db.execSQL("DELETE FROM tvEpisode WHERE showId = ?", new Object[]{loser});
                }
                db.execSQL("DELETE FROM scrapeQueue WHERE mediaType = ? AND entityId = ?",
                        new Object[]{mediaType, String.valueOf(loser)});
                db.execSQL("DELETE FROM " + table + " WHERE id = ?", new Object[]{loser});
                merged++;
            }
        }
        return merged;
    }

    /**
     * 还差资料的条目数：队列行 ∪ 库里未刮削的实体。
     *
     * <p>只看 scrapeQueue 那张表不够：取批次时队列行按行删掉，预算用完会剩下半批
     * 没刮的条目，行数归零，首页那句「待刮削 N」跟着一起消失，看起来就像刮完了。
     * mac 版每轮都从库里现算未刮削清单，这一句把「还剩多少」的口径对齐到同一件事上。</p>
     */
    public int scrapeQueueCount() {
        SQLiteDatabase db = helper.getReadableDatabase();
        try (Cursor cursor = db.rawQuery(
                "SELECT COUNT(*) FROM (SELECT mediaType, entityId FROM scrapeQueue "
                        + "UNION SELECT 'movie', id FROM movie WHERE scraped = 0 AND isLocked = 0 "
                        + "AND title IS NOT NULL AND title <> '' "
                        + "UNION SELECT 'tv', id FROM tvShow WHERE scraped = 0 AND isLocked = 0 "
                        + "AND title IS NOT NULL AND title <> '')", null)) {
            return cursor.moveToFirst() ? cursor.getInt(0) : 0;
        }
    }

    /** 队列表现存的行数，只用来统计补了多少行，不代表「还剩多少活」。 */
    private int queueRowCount() {
        SQLiteDatabase db = helper.getReadableDatabase();
        try (Cursor cursor = db.rawQuery("SELECT COUNT(*) FROM scrapeQueue", null)) {
            return cursor.moveToFirst() ? cursor.getInt(0) : 0;
        }
    }

    // ------------------------------------------------------- episode metadata

    /** Stores one TMDB episode lookup (title / overview / air date / still URL). */
    public void upsertEpisodeMetadata(long showId, int season, int episode, String title,
                                      String overview, String airDate, String stillUrl) {
        SQLiteDatabase db = helper.getWritableDatabase();
        try (android.database.sqlite.SQLiteStatement statement = db.compileStatement(
                "INSERT OR REPLACE INTO tvEpisode (showId, season, episode, title, overview, "
                        + "airDate, stillUrl, updatedAt) VALUES (?,?,?,?,?,?,?,?)")) {
            statement.bindLong(1, showId);
            statement.bindLong(2, season);
            statement.bindLong(3, episode);
            statement.bindString(4, title == null ? "" : title);
            statement.bindString(5, overview == null ? "" : overview);
            statement.bindString(6, airDate == null ? "" : airDate);
            statement.bindString(7, stillUrl == null ? "" : stillUrl);
            statement.bindDouble(8, System.currentTimeMillis() / 1000.0);
            statement.executeUpdateDelete();
        }
    }

    /** Key is {@code season * 10000 + episode}. */
    public Map<Integer, EpisodeMetadata> episodeMetadata(long showId) {
        Map<Integer, EpisodeMetadata> result = new HashMap<>();
        SQLiteDatabase db = helper.getReadableDatabase();
        try (Cursor cursor = db.rawQuery("SELECT season, episode, title, overview, airDate, stillUrl "
                + "FROM tvEpisode WHERE showId = ?", new String[]{String.valueOf(showId)})) {
            while (cursor.moveToNext()) {
                EpisodeMetadata metadata = new EpisodeMetadata(cursor.getInt(0), cursor.getInt(1),
                        cursor.getString(2), cursor.getString(3), cursor.getString(4), cursor.getString(5));
                result.put(episodeKey(metadata.season, metadata.episode), metadata);
            }
        }
        return result;
    }

    /** True when every episode of the season already has a cached TMDB lookup. */
    public boolean hasEpisodeMetadata(long showId, int season, int episodeCount) {
        SQLiteDatabase db = helper.getReadableDatabase();
        try (Cursor cursor = db.rawQuery("SELECT COUNT(*) FROM tvEpisode WHERE showId = ? AND season = ?",
                new String[]{String.valueOf(showId), String.valueOf(season)})) {
            return cursor.moveToFirst() && cursor.getInt(0) >= Math.max(1, episodeCount);
        }
    }

    public static int episodeKey(int season, int episode) {
        return season * 10000 + episode;
    }

    /** Manual episode subtitle override (分集自定义副标题). */
    public void setEpisodeSubtitle(String fileId, String subtitle) {
        SQLiteDatabase db = helper.getWritableDatabase();
        try (android.database.sqlite.SQLiteStatement statement = db.compileStatement(
                "UPDATE videoFile SET episodeSubtitle = ? WHERE id = ?")) {
            statement.bindString(1, subtitle == null ? "" : subtitle.trim());
            statement.bindString(2, fileId);
            statement.executeUpdateDelete();
        }
    }

    // ------------------------------------------------------- offline cache

    public CacheRecord cacheRecord(String fileId) {
        if (fileId == null || fileId.isEmpty()) {
            return null;
        }
        SQLiteDatabase db = helper.getReadableDatabase();
        try (Cursor cursor = db.rawQuery("SELECT fileId, localPath, sourceUrl, bytes FROM offlineCache "
                + "WHERE fileId = ?", new String[]{fileId})) {
            if (cursor.moveToFirst()) {
                return new CacheRecord(cursor.getString(0), cursor.getString(1),
                        cursor.getString(2), cursor.getLong(3));
            }
        }
        return null;
    }

    public Map<String, CacheRecord> allCacheRecords() {
        Map<String, CacheRecord> result = new HashMap<>();
        SQLiteDatabase db = helper.getReadableDatabase();
        try (Cursor cursor = db.rawQuery("SELECT fileId, localPath, sourceUrl, bytes FROM offlineCache", null)) {
            while (cursor.moveToNext()) {
                CacheRecord record = new CacheRecord(cursor.getString(0), cursor.getString(1),
                        cursor.getString(2), cursor.getLong(3));
                result.put(record.fileId, record);
            }
        }
        return result;
    }

    public void saveCacheRecord(String fileId, String localPath, String sourceUrl, long bytes) {
        SQLiteDatabase db = helper.getWritableDatabase();
        try (android.database.sqlite.SQLiteStatement statement = db.compileStatement(
                "INSERT OR REPLACE INTO offlineCache (fileId, localPath, sourceUrl, bytes, updatedAt) "
                        + "VALUES (?,?,?,?,?)")) {
            statement.bindString(1, fileId);
            statement.bindString(2, localPath);
            statement.bindString(3, sourceUrl == null ? "" : sourceUrl);
            statement.bindLong(4, bytes);
            statement.bindDouble(5, System.currentTimeMillis() / 1000.0);
            statement.executeUpdateDelete();
        }
    }

    public void removeCacheRecord(String fileId) {
        SQLiteDatabase db = helper.getWritableDatabase();
        db.delete("offlineCache", "fileId = ?", new String[]{fileId});
    }

    public void clearLibrary() {
        SQLiteDatabase db = helper.getWritableDatabase();
        db.delete("videoFile", null, null);
        db.delete("movie", null, null);
        db.delete("tvShow", null, null);
        db.delete("scrapeQueue", null, null);
        db.delete("tvEpisode", null, null);
        db.delete("offlineCache", null, null);
    }

    private static String nullToEmpty(String value) {
        return value == null ? "" : value;
    }

    // ---- 字幕 sidecar（1.34）----------------------------------------------------------

    /** 一条转档记录。 */
    public static final class SidecarRow {
        public String videoFileId = "";
        public int trackNumber;
        public String relativePath = "";
        public String language = "";
        public String format = "";
        public String status = "pending";
        public int cueCount;
        public long byteOffset;
        public double coveredSeconds;
        public String error = "";
    }

    public void recordSidecar(SidecarRow row) {
        helper.getWritableDatabase().execSQL("INSERT OR REPLACE INTO subtitleSidecar "
                        + "(videoFileId, trackNumber, relativePath, language, format, status, "
                        + "cueCount, byteOffset, coveredSeconds, error, updatedAt) VALUES (?,?,?,?,?,?,?,?,?,?,?)",
                new Object[]{row.videoFileId, row.trackNumber, row.relativePath, row.language,
                        row.format, row.status, row.cueCount, row.byteOffset, row.coveredSeconds,
                        row.error, System.currentTimeMillis()});
    }

    public java.util.List<SidecarRow> sidecarRows(String videoFileId) {
        java.util.ArrayList<SidecarRow> rows = new java.util.ArrayList<>();
        try (Cursor cursor = helper.getReadableDatabase().rawQuery(
                "SELECT videoFileId, trackNumber, relativePath, language, format, status, "
                        + "cueCount, byteOffset, coveredSeconds, error FROM subtitleSidecar "
                        + "WHERE videoFileId = ? ORDER BY trackNumber",
                new String[]{videoFileId})) {
            while (cursor.moveToNext()) {
                SidecarRow row = new SidecarRow();
                row.videoFileId = cursor.getString(0);
                row.trackNumber = cursor.getInt(1);
                row.relativePath = cursor.getString(2);
                row.language = cursor.getString(3);
                row.format = cursor.getString(4);
                row.status = cursor.getString(5);
                row.cueCount = cursor.getInt(6);
                row.byteOffset = cursor.getLong(7);
                row.coveredSeconds = cursor.getDouble(8);
                row.error = cursor.getString(9);
                rows.add(row);
            }
        }
        return rows;
    }

    /** 还没转档的视频文件：没看过的优先，转档不抢已看过那批的带宽。 */
    public java.util.List<String[]> sidecarCandidates(int limit, boolean onlyUnwatched) {
        java.util.ArrayList<String[]> rows = new java.util.ArrayList<>();
        String sql = "SELECT f.id, f.relativePath FROM videoFile f "
                + "LEFT JOIN subtitleSidecar s ON s.videoFileId = f.id "
                + "WHERE s.videoFileId IS NULL "
                + (onlyUnwatched ? "AND f.isWatched = 0 " : "")
                + "ORDER BY f.isWatched ASC, f.lastPlayedAt ASC LIMIT ?";
        try (Cursor cursor = helper.getReadableDatabase().rawQuery(sql,
                new String[]{String.valueOf(Math.max(1, limit))})) {
            while (cursor.moveToNext()) {
                rows.add(new String[]{cursor.getString(0), cursor.getString(1)});
            }
        }
        return rows;
    }

    public void forgetSidecars(String videoFileId) {
        helper.getWritableDatabase().execSQL("DELETE FROM subtitleSidecar WHERE videoFileId = ?",
                new Object[]{videoFileId});
    }
}

