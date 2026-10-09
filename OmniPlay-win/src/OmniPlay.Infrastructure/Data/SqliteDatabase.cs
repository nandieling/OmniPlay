using OmniPlay.Core.Interfaces;
using System.Data;
using Microsoft.Data.Sqlite;

namespace OmniPlay.Infrastructure.Data;

public sealed class SqliteDatabase
{
    private readonly IStoragePaths storagePaths;

    public SqliteDatabase(IStoragePaths storagePaths)
    {
        this.storagePaths = storagePaths;
    }

    public string DatabasePath => Path.Combine(storagePaths.DataDirectory, "omniplay.sqlite");

    public void EnsureInitialized()
    {
        storagePaths.EnsureCreated();

        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS mediaSource (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                protocolType TEXT NOT NULL,
                baseUrl TEXT NOT NULL,
                addressConfig TEXT NULL,
                authConfig TEXT NULL,
                isEnabled INTEGER NOT NULL DEFAULT 1,
                disabledAt TEXT NULL,
                removedAt TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS movie (
                id INTEGER PRIMARY KEY,
                title TEXT NOT NULL,
                releaseDate TEXT NULL,
                overview TEXT NULL,
                posterPath TEXT NULL,
                voteAverage REAL NULL,
                isLocked INTEGER NOT NULL DEFAULT 0,
                productionCountryCodes TEXT NULL,
                originalLanguage TEXT NULL,
                metadataLanguage TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS tvShow (
                id INTEGER PRIMARY KEY,
                title TEXT NOT NULL,
                firstAirDate TEXT NULL,
                overview TEXT NULL,
                posterPath TEXT NULL,
                voteAverage REAL NULL,
                isLocked INTEGER NOT NULL DEFAULT 0,
                productionCountryCodes TEXT NULL,
                originalLanguage TEXT NULL,
                metadataLanguage TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS videoFile (
                id TEXT PRIMARY KEY,
                sourceId INTEGER NOT NULL,
                metadataPath TEXT NULL,
                relativePath TEXT NOT NULL,
                fileName TEXT NOT NULL,
                mediaType TEXT NOT NULL,
                movieId INTEGER NULL,
                episodeId INTEGER NULL,
                playProgress REAL NOT NULL DEFAULT 0,
                duration REAL NOT NULL DEFAULT 0,
                lastPlayedAt REAL NULL,
                FOREIGN KEY(sourceId) REFERENCES mediaSource(id) ON DELETE CASCADE,
                FOREIGN KEY(movieId) REFERENCES movie(id) ON DELETE SET NULL,
                FOREIGN KEY(episodeId) REFERENCES tvShow(id) ON DELETE SET NULL
            );

            CREATE INDEX IF NOT EXISTS ix_videoFile_sourceId ON videoFile(sourceId);
            CREATE INDEX IF NOT EXISTS ix_videoFile_movieId ON videoFile(movieId);
            CREATE INDEX IF NOT EXISTS ix_videoFile_episodeId ON videoFile(episodeId);

            CREATE TABLE IF NOT EXISTS doubanMetadata (
                itemKind TEXT NOT NULL,
                entityId INTEGER NOT NULL,
                subjectId TEXT NOT NULL,
                subjectUrl TEXT NOT NULL,
                title TEXT NOT NULL,
                originalTitle TEXT NULL,
                year TEXT NULL,
                rating REAL NULL,
                ratingCount INTEGER NULL,
                summary TEXT NULL,
                posterUrl TEXT NULL,
                directors TEXT NULL,
                casts TEXT NULL,
                fetchedAt TEXT NOT NULL,
                PRIMARY KEY (itemKind, entityId)
            );
            """;
        command.ExecuteNonQuery();

        EnsureColumn(connection, "movie", "releaseDate", "TEXT NULL");
        EnsureColumn(connection, "movie", "overview", "TEXT NULL");
        EnsureColumn(connection, "movie", "posterPath", "TEXT NULL");
        EnsureColumn(connection, "movie", "voteAverage", "REAL NULL");
        EnsureColumn(connection, "movie", "isLocked", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "movie", "productionCountryCodes", "TEXT NULL");
        EnsureColumn(connection, "movie", "originalLanguage", "TEXT NULL");
        EnsureColumn(connection, "movie", "metadataLanguage", "TEXT NULL");
        EnsureColumn(connection, "movie", "tmdbId", "INTEGER NULL");
        EnsureColumn(connection, "movie", "director", "TEXT NULL");
        EnsureColumn(connection, "movie", "mainCast", "TEXT NULL");

        EnsureColumn(connection, "tvShow", "posterPath", "TEXT NULL");
        EnsureColumn(connection, "tvShow", "firstAirDate", "TEXT NULL");
        EnsureColumn(connection, "tvShow", "overview", "TEXT NULL");
        EnsureColumn(connection, "tvShow", "voteAverage", "REAL NULL");
        EnsureColumn(connection, "tvShow", "isLocked", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "tvShow", "productionCountryCodes", "TEXT NULL");
        EnsureColumn(connection, "tvShow", "originalLanguage", "TEXT NULL");
        EnsureColumn(connection, "tvShow", "metadataLanguage", "TEXT NULL");
        EnsureColumn(connection, "tvShow", "director", "TEXT NULL");
        EnsureColumn(connection, "tvShow", "mainCast", "TEXT NULL");

        EnsureColumn(connection, "movie", "directors", "TEXT NULL");
        EnsureColumn(connection, "movie", "casts", "TEXT NULL");
        EnsureColumn(connection, "tvShow", "directors", "TEXT NULL");
        EnsureColumn(connection, "tvShow", "casts", "TEXT NULL");
        EnsureColumn(connection, "doubanMetadata", "directors", "TEXT NULL");
        EnsureColumn(connection, "doubanMetadata", "casts", "TEXT NULL");

        EnsureColumn(connection, "videoFile", "metadataPath", "TEXT NULL");
        EnsureColumn(connection, "videoFile", "playProgress", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(connection, "videoFile", "duration", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(connection, "videoFile", "lastPlayedAt", "REAL NULL");
        EnsureColumn(connection, "videoFile", "customEpisodeTitle", "TEXT NULL");
        EnsureColumn(connection, "videoFile", "customSeasonNumber", "INTEGER NULL");
        EnsureColumn(connection, "videoFile", "customEpisodeNumber", "INTEGER NULL");
        EnsureColumn(connection, "videoFile", "customEpisodeYear", "TEXT NULL");
        EnsureColumn(connection, "videoFile", "customEpisodeSubtitle", "TEXT NULL");
        EnsureColumn(connection, "videoFile", "customEpisodeThumbnailPath", "TEXT NULL");

        EnsureColumn(connection, "mediaSource", "isEnabled", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, "mediaSource", "addressConfig", "TEXT NULL");
        EnsureColumn(connection, "mediaSource", "disabledAt", "TEXT NULL");
        EnsureColumn(connection, "mediaSource", "removedAt", "TEXT NULL");

        // 一次性数据修复（schema v2）：早期版本写入的导演/主演存在错配数据
        // （误写语言名「普通话」或其他作品的人员），对所有未手动锁定的行清空导演/主演，
        // 下次刮削时从 TMDB 按正确语言重新抓取；用户手动编辑过的行（isLocked=1）保留。
        int schemaVersion;
        using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = "PRAGMA user_version;";
            schemaVersion = Convert.ToInt32(versionCommand.ExecuteScalar());
        }
        if (schemaVersion < 2)
        {
            using var peopleRepair = connection.CreateCommand();
            peopleRepair.CommandText = "UPDATE movie SET director = NULL, mainCast = NULL WHERE isLocked = 0; " +
                "UPDATE tvShow SET director = NULL, mainCast = NULL WHERE isLocked = 0;";
            peopleRepair.ExecuteNonQuery();

            using var versionBump = connection.CreateCommand();
            versionBump.CommandText = "PRAGMA user_version = 2;";
            versionBump.ExecuteNonQuery();
        }

        if (schemaVersion < 3)
        {
            // v3：分类文件夹、人物档案与中文译名缓存三张表（对齐 mac 版 v8/v9/v10）。
            using var categoryFolders = connection.CreateCommand();
            categoryFolders.CommandText = """
                CREATE TABLE IF NOT EXISTS categoryFolder (
                    id TEXT PRIMARY KEY,
                    kind TEXT NOT NULL,
                    name TEXT NOT NULL,
                    posterPath TEXT NULL,
                    updatedAt REAL NULL
                );
                CREATE TABLE IF NOT EXISTS personProfile (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    tmdbPersonId INTEGER NULL,
                    photoPath TEXT NULL,
                    filmographyJson TEXT NULL,
                    updatedAt REAL NULL
                );
                CREATE TABLE IF NOT EXISTS chineseNameCache (
                    key TEXT PRIMARY KEY,
                    value TEXT NULL,
                    updatedAt REAL NOT NULL
                );
                """;
            categoryFolders.ExecuteNonQuery();

            // 历史扁平名单（顿号/逗号分隔）升级为 JSON 全量名单。
            BackfillPersonListColumns(connection, "movie");
            BackfillPersonListColumns(connection, "tvShow");

            using var versionBump3 = connection.CreateCommand();
            versionBump3.CommandText = "PRAGMA user_version = 3;";
            versionBump3.ExecuteNonQuery();
        }

        using var languageRepair = connection.CreateCommand();
        languageRepair.CommandText = "UPDATE movie SET director = NULL WHERE director LIKE '%普通话%'; " +
            "UPDATE movie SET mainCast = NULL WHERE mainCast LIKE '%普通话%'; " +
            "UPDATE tvShow SET director = NULL WHERE director LIKE '%普通话%'; " +
            "UPDATE tvShow SET mainCast = NULL WHERE mainCast LIKE '%普通话%';";
        languageRepair.ExecuteNonQuery();
    }

    /// <summary>v3 迁移：把历史扁平导演/主演列拆成 JSON 名单列。</summary>
    private static void BackfillPersonListColumns(SqliteConnection connection, string tableName)
    {
        var rows = new List<(long Id, string? Director, string? MainCast)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = $"SELECT id, director, mainCast FROM {tableName} WHERE directors IS NULL AND casts IS NULL;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        foreach (var (id, director, mainCast) in rows)
        {
            var directors = OmniPlay.Core.Models.PersonNames.Encode(OmniPlay.Core.Models.PersonNames.Split(director));
            var casts = OmniPlay.Core.Models.PersonNames.Encode(OmniPlay.Core.Models.PersonNames.Split(mainCast));
            if (directors is null && casts is null)
            {
                continue;
            }

            using var update = connection.CreateCommand();
            update.CommandText = $"UPDATE {tableName} SET directors = @directors, casts = @casts WHERE id = @id;";
            update.Parameters.AddWithValue("@directors", (object?)directors ?? DBNull.Value);
            update.Parameters.AddWithValue("@casts", (object?)casts ?? DBNull.Value);
            update.Parameters.AddWithValue("@id", id);
            update.ExecuteNonQuery();
        }
    }

    private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string columnDefinition)
    {
        using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({tableName});";

        using var reader = pragma.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        alter.ExecuteNonQuery();
    }

    public IDbConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        return connection;
    }
}
