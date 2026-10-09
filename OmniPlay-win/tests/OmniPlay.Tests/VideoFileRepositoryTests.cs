using Dapper;
using Microsoft.Data.Sqlite;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Core.Models.Playback;
using OmniPlay.Infrastructure.Data;

namespace OmniPlay.Tests;

public sealed class VideoFileRepositoryTests : IDisposable
{
    private readonly string rootPath;
    private readonly SqliteDatabase database;
    private readonly VideoFileRepository repository;

    public VideoFileRepositoryTests()
    {
        rootPath = Path.Combine(
            AppContext.BaseDirectory,
            "test-data",
            nameof(VideoFileRepositoryTests),
            Guid.NewGuid().ToString("N"));

        var storagePaths = new TestStoragePaths(rootPath);
        database = new SqliteDatabase(storagePaths);
        database.EnsureInitialized();
        repository = new VideoFileRepository(database, storagePaths);
    }

    [Fact]
    public async Task UpdatePlaybackStateAsync_PersistsDurationForMovieFiles()
    {
        await SeedMovieFileAsync(movieId: -1001, videoFileId: "movie-file-1");

        await repository.UpdatePlaybackStateAsync("movie-file-1", playProgress: 1500, durationSeconds: 2000);

        var item = Assert.Single(await repository.GetByMovieAsync(-1001));
        Assert.Equal(1500, item.PlayProgress);
        Assert.Equal(2000, item.Duration);
        Assert.InRange(item.ProgressRatio, 0.749, 0.751);
        Assert.False(item.IsWatched);
    }

    [Fact]
    public async Task GetContinueWatchingAsync_UsesPersistedDurationForProgressPercent()
    {
        await SeedMovieFileAsync(movieId: -1002, videoFileId: "movie-file-2");

        await repository.UpdatePlaybackStateAsync("movie-file-2", playProgress: 1500, durationSeconds: 2000);

        var item = Assert.Single(await repository.GetContinueWatchingAsync());
        Assert.InRange(item.ContinueWatchingProgress, 0.749, 0.751);
        Assert.NotNull(item.ContinueWatchingLabel);
        Assert.Contains("75%", item.ContinueWatchingLabel);
        Assert.NotNull(item.LastPlayedAt);
    }

    [Fact]
    public async Task GetContinueWatchingAsync_UsesMostRecentlyUnfinishedEpisodeProgress()
    {
        await SeedTvShowFileAsync(tvShowId: -1006, videoFileId: "tv-old-progress");
        await SeedTvShowFileAsync(tvShowId: -1006, videoFileId: "tv-recent-progress");

        using (var connection = database.OpenConnection())
        {
            await connection.ExecuteAsync(
                """
                UPDATE videoFile
                SET playProgress = 1800, duration = 2000, lastPlayedAt = 100
                WHERE id = 'tv-old-progress';

                UPDATE videoFile
                SET playProgress = 200, duration = 2000, lastPlayedAt = 200
                WHERE id = 'tv-recent-progress';
                """);
        }

        var item = Assert.Single(await repository.GetContinueWatchingAsync());
        Assert.InRange(item.ContinueWatchingProgress, 0.099, 0.101);
        Assert.Contains("10%", item.ContinueWatchingLabel);
    }

    [Fact]
    public async Task GetContinueWatchingAsync_ExcludesWatchedItems()
    {
        await SeedMovieFileAsync(movieId: -1003, videoFileId: "movie-file-3");

        await repository.UpdatePlaybackStateAsync("movie-file-3", playProgress: 1950, durationSeconds: 2000);

        Assert.Empty(await repository.GetContinueWatchingAsync());
    }

    [Fact]
    public async Task GetContinueWatchingAsync_UsesTvShowYearAsSubtitle()
    {
        await SeedTvShowFileAsync(tvShowId: -1004, videoFileId: "tv-file-year");

        await repository.UpdatePlaybackStateAsync("tv-file-year", playProgress: 100, durationSeconds: 2000);

        var item = Assert.Single(await repository.GetContinueWatchingAsync());
        Assert.Equal("2024", item.Subtitle);
    }

    [Fact]
    public async Task GetContinueWatchingAsync_ExcludesItemsWithoutProgress()
    {
        await SeedMovieFileAsync(movieId: -1005, videoFileId: "movie-no-progress");

        Assert.Empty(await repository.GetContinueWatchingAsync());
    }

    [Fact]
    public async Task GetLibraryPlaybackStatesAsync_UsesThreeStateThresholds()
    {
        await SeedMovieFileAsync(movieId: -1010, videoFileId: "movie-unwatched");
        await SeedMovieFileAsync(movieId: -1011, videoFileId: "movie-in-progress");
        await SeedMovieFileAsync(movieId: -1012, videoFileId: "movie-watched");

        await repository.UpdatePlaybackStateAsync("movie-in-progress", playProgress: 1890, durationSeconds: 2000);
        await repository.UpdatePlaybackStateAsync("movie-watched", playProgress: 1940, durationSeconds: 2000);

        var states = await repository.GetLibraryPlaybackStatesAsync();

        Assert.Equal(PlaybackWatchState.Unwatched, states["movie--1010"]);
        Assert.Equal(PlaybackWatchState.InProgress, states["movie--1011"]);
        Assert.Equal(PlaybackWatchState.Watched, states["movie--1012"]);
    }

    [Fact]
    public async Task GetLibraryPlaybackStatesAsync_RequiresAllTvEpisodesWatched()
    {
        await SeedTvShowFileAsync(tvShowId: -1020, videoFileId: "tv-ep-1");
        await SeedTvShowFileAsync(tvShowId: -1020, videoFileId: "tv-ep-2");

        await repository.UpdatePlaybackStateAsync("tv-ep-1", playProgress: 1940, durationSeconds: 2000);

        var states = await repository.GetLibraryPlaybackStatesAsync();
        Assert.Equal(PlaybackWatchState.InProgress, states["tv--1020"]);

        await repository.UpdatePlaybackStateAsync("tv-ep-2", playProgress: 1940, durationSeconds: 2000);

        states = await repository.GetLibraryPlaybackStatesAsync();
        Assert.Equal(PlaybackWatchState.Watched, states["tv--1020"]);
    }

    [Fact]
    public async Task GetByMovieAsync_MapsWebDavSourceToSafeAndAuthenticatedPlayableUrls()
    {
        using var connection = database.OpenConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO mediaSource (id, name, protocolType, baseUrl, authConfig)
            VALUES (2, 'Remote', 'webdav', 'https://demo.example/library', @AuthConfig);

            INSERT INTO movie (id, title, releaseDate, overview, posterPath, voteAverage, isLocked)
            VALUES (-1004, '流浪地球 2', '2023', NULL, NULL, NULL, 0);

            INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
            VALUES ('movie-file-4', 2, 'movies/流浪地球 2.mkv', '流浪地球 2.mkv', 'movie', -1004, NULL, 0, 0);
            """,
            new
            {
                AuthConfig = MediaSourceAuthConfigSerializer.SerializeWebDav(new WebDavAuthConfig("demo", "secret"))
            });

        var item = Assert.Single(await repository.GetByMovieAsync(-1004));
        Assert.Equal(
            "https://demo.example/library/movies/%E6%B5%81%E6%B5%AA%E5%9C%B0%E7%90%83%202.mkv",
            item.AbsolutePath);
        Assert.Equal(
            "https://demo:secret@demo.example/library/movies/%E6%B5%81%E6%B5%AA%E5%9C%B0%E7%90%83%202.mkv",
            item.PlaybackPath);
    }

    [Fact]
    public async Task GetByMovieAsync_UsesServerHlsForEmbyIsoEvenWhenMetadataPathIsLocal()
    {
        var localIsoPath = Path.Combine(rootPath, "mirror", "Reservoir.Dogs.1992.iso");
        Directory.CreateDirectory(Path.GetDirectoryName(localIsoPath)!);
        File.WriteAllText(localIsoPath, "iso");

        using var connection = database.OpenConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO mediaSource (id, name, protocolType, baseUrl, authConfig)
            VALUES (3, 'Emby', 'emby', 'http://127.0.0.1:8096', @AuthConfig);

            INSERT INTO movie (id, title, releaseDate, overview, posterPath, voteAverage, isLocked)
            VALUES (-1005, '落水狗', '1992', NULL, NULL, NULL, 0);

            INSERT INTO videoFile (id, sourceId, metadataPath, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
            VALUES ('movie-file-5', 3, @MetadataPath, 'Items/abc123/Download', 'Reservoir.Dogs.1992.iso', 'movie', -1005, NULL, 0, 0);
            """,
            new
            {
                MetadataPath = localIsoPath,
                AuthConfig = MediaSourceAuthConfigSerializer.SerializeMediaServer(new MediaServerAuthConfig("token"))
            });

        var item = Assert.Single(await repository.GetByMovieAsync(-1005));
        Assert.Null(item.LocalIsoPlaybackPath);
        Assert.Equal(item.PlaybackPath, item.EffectivePlaybackPath);
        Assert.Equal(
            "http://127.0.0.1:8096/Videos/abc123/master.m3u8?MediaSourceId=abc123&PlaySessionId=omniplayabc123&DeviceId=omniplay-windows&EnableAutoStreamCopy=true&AllowVideoStreamCopy=true&AllowAudioStreamCopy=true&EnableAdaptiveBitrateStreaming=false&VideoCodec=h264%2Chevc&AudioCodec=aac%2Cac3%2Ceac3%2Cdts%2Cflac%2Ctruehd%2Cmp3%2Copus%2Cvorbis&SegmentContainer=ts&SegmentLength=6&MinSegments=1&VideoBitRate=35000000&MaxStreamingBitrate=45000000&AudioBitRate=640000&MaxWidth=1920&MaxHeight=1080&Profile=high&Level=51&RequireAvc=false&TranscodingMaxAudioChannels=6&BreakOnNonKeyFrames=false&CopyTimestamps=true&Context=Streaming&api_key=token",
            item.PlaybackPath);
    }

    [Fact]
    public async Task GetByMovieAsync_UpgradesExistingEmbyDiscHlsPathToHighQuality()
    {
        using var connection = database.OpenConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO mediaSource (id, name, protocolType, baseUrl, authConfig)
            VALUES (4, 'Jellyfin', 'jellyfin', 'http://127.0.0.1:8096', @AuthConfig);

            INSERT INTO movie (id, title, releaseDate, overview, posterPath, voteAverage, isLocked)
            VALUES (-1006, '年少日记', '2023', NULL, NULL, NULL, 0);

            INSERT INTO videoFile (id, sourceId, metadataPath, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
            VALUES ('movie-file-6', 4, NULL, 'Videos/item123/master.m3u8?mediaSourceId=source456&videoCodec=h264', '年少日记 Time Still Turns the Pages 2023 1080p Blu-ray AVC DTS-HD MA 5.1-Thor@HDSky', 'movie', -1006, NULL, 0, 0);
            """,
            new
            {
                AuthConfig = MediaSourceAuthConfigSerializer.SerializeMediaServer(new MediaServerAuthConfig("token"))
            });

        var item = Assert.Single(await repository.GetByMovieAsync(-1006));

        Assert.Contains("/Videos/item123/master.m3u8?", item.PlaybackPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MediaSourceId=source456", item.PlaybackPath);
        Assert.Contains("VideoBitRate=35000000", item.PlaybackPath);
        Assert.Contains("MaxStreamingBitrate=45000000", item.PlaybackPath);
        Assert.Contains("EnableAdaptiveBitrateStreaming=false", item.PlaybackPath);
        Assert.Contains("api_key=token", item.PlaybackPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private async Task SeedMovieFileAsync(long movieId, string videoFileId)
    {
        using var connection = database.OpenConnection();

        await connection.ExecuteAsync(
            """
            INSERT OR IGNORE INTO mediaSource (id, name, protocolType, baseUrl, authConfig)
            VALUES (1, 'Movies', 'local', 'D:\Movies', NULL);

            INSERT INTO movie (id, title, releaseDate, overview, posterPath, voteAverage, isLocked)
            VALUES (@MovieId, 'Inception', '2010', NULL, NULL, NULL, 0);

            INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
            VALUES (@VideoFileId, 1, 'Inception.2010.mkv', 'Inception.2010.mkv', 'movie', @MovieId, NULL, 0, 0);
            """,
            new
            {
                MovieId = movieId,
                VideoFileId = videoFileId
            });
    }

    private async Task SeedTvShowFileAsync(long tvShowId, string videoFileId)
    {
        using var connection = database.OpenConnection();

        await connection.ExecuteAsync(
            """
            INSERT OR IGNORE INTO mediaSource (id, name, protocolType, baseUrl, authConfig)
            VALUES (1, 'Movies', 'local', 'D:\Movies', NULL);

            INSERT OR IGNORE INTO tvShow (id, title, firstAirDate, overview, posterPath, voteAverage, isLocked)
            VALUES (@TvShowId, 'Demo Show', '2024', NULL, NULL, NULL, 0);

            INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
            VALUES (@VideoFileId, 1, @FileName, @FileName, 'tv', NULL, @TvShowId, 0, 0);
            """,
            new
            {
                TvShowId = tvShowId,
                VideoFileId = videoFileId,
                FileName = $"{videoFileId}.mkv"
            });
    }
}
