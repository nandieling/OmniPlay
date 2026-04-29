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
                relativePath TEXT NOT NULL,
                fileName TEXT NOT NULL,
                mediaType TEXT NOT NULL,
                movieId INTEGER NULL,
                episodeId INTEGER NULL,
                playProgress REAL NOT NULL DEFAULT 0,
                duration REAL NOT NULL DEFAULT 0,
                FOREIGN KEY(sourceId) REFERENCES mediaSource(id) ON DELETE CASCADE,
                FOREIGN KEY(movieId) REFERENCES movie(id) ON DELETE SET NULL,
                FOREIGN KEY(episodeId) REFERENCES tvShow(id) ON DELETE SET NULL
            );

            CREATE INDEX IF NOT EXISTS ix_videoFile_sourceId ON videoFile(sourceId);
            CREATE INDEX IF NOT EXISTS ix_videoFile_movieId ON videoFile(movieId);
            CREATE INDEX IF NOT EXISTS ix_videoFile_episodeId ON videoFile(episodeId);
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

        EnsureColumn(connection, "tvShow", "posterPath", "TEXT NULL");
        EnsureColumn(connection, "tvShow", "firstAirDate", "TEXT NULL");
        EnsureColumn(connection, "tvShow", "overview", "TEXT NULL");
        EnsureColumn(connection, "tvShow", "voteAverage", "REAL NULL");
        EnsureColumn(connection, "tvShow", "isLocked", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "tvShow", "productionCountryCodes", "TEXT NULL");
        EnsureColumn(connection, "tvShow", "originalLanguage", "TEXT NULL");
        EnsureColumn(connection, "tvShow", "metadataLanguage", "TEXT NULL");

        EnsureColumn(connection, "videoFile", "playProgress", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(connection, "videoFile", "duration", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(connection, "videoFile", "customEpisodeTitle", "TEXT NULL");
        EnsureColumn(connection, "videoFile", "customSeasonNumber", "INTEGER NULL");
        EnsureColumn(connection, "videoFile", "customEpisodeNumber", "INTEGER NULL");
        EnsureColumn(connection, "videoFile", "customEpisodeYear", "TEXT NULL");
        EnsureColumn(connection, "videoFile", "customEpisodeSubtitle", "TEXT NULL");
        EnsureColumn(connection, "videoFile", "customEpisodeThumbnailPath", "TEXT NULL");

        EnsureColumn(connection, "mediaSource", "isEnabled", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, "mediaSource", "disabledAt", "TEXT NULL");
        EnsureColumn(connection, "mediaSource", "removedAt", "TEXT NULL");
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
