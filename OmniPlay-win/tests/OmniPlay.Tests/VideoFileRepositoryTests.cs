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

        await repository.UpdatePlaybackStateAsync("movie-in-progress", playProgress: 1930, durationSeconds: 2000);
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
