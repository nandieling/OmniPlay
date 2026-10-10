package com.omniplay.tv.local;

import android.content.Context;
import android.database.Cursor;
import android.database.sqlite.SQLiteDatabase;
import android.database.sqlite.SQLiteOpenHelper;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
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
            super(context, "omniplay_local.db", null, 2);
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
        }

        @Override
        public void onUpgrade(SQLiteDatabase db, int oldVersion, int newVersion) {
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
    }

    public int removeMissingFiles(List<String> currentFileIds) {
        Set<String> keep = new HashSet<>(currentFileIds);
        List<String> stale = new ArrayList<>();
        try (Cursor cursor = helper.getReadableDatabase().rawQuery("SELECT id FROM videoFile", null)) {
            while (cursor.moveToNext()) {
                String id = cursor.getString(0);
                if (!keep.contains(id)) {
                    stale.add(id);
                }
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

    public List<StoredFile> filesForMovie(long movieId) {
        return queryFiles("movieId", movieId);
    }

    public List<StoredFile> filesForShow(long showId) {
        return queryFiles("showId", showId);
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
                String fileId = cursor.getString(0);
                double progress = cursor.getDouble(10);
                double duration = cursor.getDouble(11);
                double[] override = progressOverrides.get(fileId);
                if (override != null) {
                    progress = Math.max(progress, override[0]);
                    duration = Math.max(duration, override[1]);
                }
                result.add(new StoredFile(
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
                        cursor.getInt(12) != 0));
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
        String sql = "SELECT id, mediaType, entityId, query, year, secondaryQuery, preferredSeason "
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
    public int scrapeQueueCount() {
        SQLiteDatabase db = helper.getReadableDatabase();
        try (Cursor cursor = db.rawQuery(
                "SELECT COUNT(*) FROM (SELECT 1 FROM scrapeQueue GROUP BY mediaType, entityId)", null)) {
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
}
