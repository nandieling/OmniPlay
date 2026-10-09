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

    /** Re-queues a scrape for one entity, optionally with a user-supplied search query. */
    public void requeueScrape(String mediaType, long entityId, String query, String year,
                              String secondaryQuery, int preferredSeason) {
        SQLiteDatabase db = helper.getWritableDatabase();
        db.delete("scrapeQueue", "mediaType = ? AND entityId = ?", new String[]{mediaType, String.valueOf(entityId)});
        enqueueScrape(mediaType, entityId, query, year, secondaryQuery, preferredSeason, "");
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
        if (owned.isEmpty()) {
            return 0;
        }
        SQLiteDatabase db = helper.getWritableDatabase();
        db.beginTransaction();
        try {
            for (String fileId : owned) {
                db.delete("videoFile", "id = ?", new String[]{fileId});
                db.delete("offlineCache", "fileId = ?", new String[]{fileId});
                progressOverrides.remove(fileId);
            }
            pruneOrphanEntities(db);
            db.setTransactionSuccessful();
        } finally {
            db.endTransaction();
        }
        return owned.size();
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
        SQLiteDatabase db = helper.getWritableDatabase();
        // One pending entry per entity: rescanning refreshes the query rather than stacking a
        // duplicate, which would otherwise inflate the count and repeat the same TMDB lookup.
        db.delete("scrapeQueue", "mediaType = ? AND entityId = ?",
                new String[]{mediaType, String.valueOf(entityId)});
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

    public List<Map<String, Object>> nextScrapeBatches(int limit) {
        List<Map<String, Object>> batches = new ArrayList<>();
        SQLiteDatabase db = helper.getReadableDatabase();
        String sql = "SELECT mediaType, entityId, query, year, secondaryQuery, preferredSeason FROM scrapeQueue "
                + "GROUP BY mediaType, entityId ORDER BY id ASC LIMIT " + Math.max(1, limit);
        try (Cursor cursor = db.rawQuery(sql, null)) {
            while (cursor.moveToNext()) {
                Map<String, Object> batch = new HashMap<>();
                batch.put("mediaType", cursor.getString(0));
                batch.put("entityId", cursor.getLong(1));
                batch.put("query", cursor.getString(2));
                batch.put("year", cursor.getString(3));
                batch.put("secondaryQuery", cursor.getString(4));
                batch.put("preferredSeason", cursor.getInt(5));
                batches.add(batch);
            }
        }
        if (!batches.isEmpty()) {
            db.delete("scrapeQueue", null, null);
        }
        return batches;
    }

    public int scrapeQueueCount() {
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
}
