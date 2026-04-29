using System.Net;
using System.Net.Http;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using OmniPlay.Core.Settings;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.FileSystem;
using OmniPlay.Infrastructure.Thumbnails;
using OmniPlay.Infrastructure.Tmdb;

namespace OmniPlay.Tests;

public sealed class LibraryThumbnailEnricherTests : IDisposable
{
    private readonly string rootPath;
    private readonly string libraryRootPath;
    private readonly TestStoragePaths storagePaths;
    private readonly SqliteDatabase database;
    private readonly MediaSourceRepository mediaSourceRepository;

    public LibraryThumbnailEnricherTests()
    {
        rootPath = Path.Combine(
            AppContext.BaseDirectory,
            "test-data",
            nameof(LibraryThumbnailEnricherTests),
            Guid.NewGuid().ToString("N"));
        libraryRootPath = Path.Combine(rootPath, "library");

        Directory.CreateDirectory(libraryRootPath);

        storagePaths = new TestStoragePaths(rootPath);
        database = new SqliteDatabase(storagePaths);
        database.EnsureInitialized();
        mediaSourceRepository = new MediaSourceRepository(database);
    }

    [Fact]
    public async Task EnrichMissingThumbnailsAsync_DownloadsEpisodeStill()
    {
        CreateMediaFile("shows/Dark/Dark.S01E01.mkv", 128);

        var sourceId = await mediaSourceRepository.AddAsync(new MediaSource
        {
            Name = "Shows",
            ProtocolType = "local",
            BaseUrl = libraryRootPath
        });

        using (var connection = database.OpenConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO tvShow (id, title, firstAirDate, overview, posterPath, voteAverage, isLocked)
                VALUES (-202, 'Dark', '2017-12-01', NULL, NULL, NULL, 0);

                INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                VALUES ('tv-file', @SourceId, 'shows/Dark/Dark.S01E01.mkv', 'Dark.S01E01.mkv', 'tv', NULL, -202, 0, 0);
                """,
                new { SourceId = sourceId });
        }

        using var httpClient = new HttpClient(new FakeThumbnailHttpMessageHandler())
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        await settingsService.SaveAsync(new AppSettings
        {
            Tmdb = new TmdbSettings
            {
                EnableBuiltInPublicSource = true,
                CustomApiKey = "custom-key"
            }
        });
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);
        var enricher = new LibraryThumbnailEnricher(database, storagePaths, client);

        var summary = await enricher.EnrichMissingThumbnailsAsync();

        Assert.Equal(1, summary.DownloadedThumbnailCount);
        Assert.True(File.Exists(Path.Combine(storagePaths.ThumbnailsDirectory, "tv-file.jpg")));
    }

    [Fact]
    public async Task EnrichMissingThumbnailsAsync_WhenDisabled_SkipsDownload()
    {
        CreateMediaFile("shows/Dark/Dark.S01E01.mkv", 128);

        var sourceId = await mediaSourceRepository.AddAsync(new MediaSource
        {
            Name = "Shows",
            ProtocolType = "local",
            BaseUrl = libraryRootPath
        });

        using (var connection = database.OpenConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO tvShow (id, title, firstAirDate, overview, posterPath, voteAverage, isLocked)
                VALUES (-203, 'Dark', '2017-12-01', NULL, NULL, NULL, 0);

                INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                VALUES ('tv-file-disabled', @SourceId, 'shows/Dark/Dark.S01E01.mkv', 'Dark.S01E01.mkv', 'tv', NULL, -203, 0, 0);
                """,
                new { SourceId = sourceId });
        }

        using var httpClient = new HttpClient(new FakeThumbnailHttpMessageHandler())
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);
        var enricher = new LibraryThumbnailEnricher(database, storagePaths, client);

        var summary = await enricher.EnrichMissingThumbnailsAsync(new TmdbSettings
        {
            EnableEpisodeThumbnailDownloads = false
        });

        Assert.Equal(0, summary.DownloadedThumbnailCount);
        Assert.False(File.Exists(Path.Combine(storagePaths.ThumbnailsDirectory, "tv-file-disabled.jpg")));
    }

    [Fact]
    public async Task EnrichMissingThumbnailsForTvShowAsync_DownloadsOnlySelectedShow()
    {
        CreateMediaFile("shows/Dark/Dark.S01E01.mkv", 128);
        CreateMediaFile("shows/Other/Other.S01E01.mkv", 128);

        var sourceId = await mediaSourceRepository.AddAsync(new MediaSource
        {
            Name = "Shows",
            ProtocolType = "local",
            BaseUrl = libraryRootPath
        });

        using (var connection = database.OpenConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO tvShow (id, title, firstAirDate, overview, posterPath, voteAverage, isLocked)
                VALUES (-204, 'Dark', '2017-12-01', NULL, NULL, NULL, 0),
                       (-205, 'Other', '2020-01-01', NULL, NULL, NULL, 0);

                INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                VALUES ('target-tv-file', @SourceId, 'shows/Dark/Dark.S01E01.mkv', 'Dark.S01E01.mkv', 'tv', NULL, -204, 0, 0),
                       ('other-tv-file', @SourceId, 'shows/Other/Other.S01E01.mkv', 'Other.S01E01.mkv', 'tv', NULL, -205, 0, 0);
                """,
                new { SourceId = sourceId });
        }

        using var httpClient = new HttpClient(new FakeThumbnailHttpMessageHandler())
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        await settingsService.SaveAsync(new AppSettings
        {
            Tmdb = new TmdbSettings
            {
                EnableBuiltInPublicSource = true,
                CustomApiKey = "custom-key"
            }
        });
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);
        var enricher = new LibraryThumbnailEnricher(database, storagePaths, client);

        var summary = await enricher.EnrichMissingThumbnailsForTvShowAsync(-204);

        Assert.Equal(1, summary.DownloadedThumbnailCount);
        Assert.True(File.Exists(Path.Combine(storagePaths.ThumbnailsDirectory, "target-tv-file.jpg")));
        Assert.False(File.Exists(Path.Combine(storagePaths.ThumbnailsDirectory, "other-tv-file.jpg")));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private void CreateMediaFile(string relativePath, int sizeBytes)
    {
        var fullPath = Path.Combine(
            libraryRootPath,
            relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        var directoryPath = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        File.WriteAllBytes(fullPath, new byte[sizeBytes]);
    }

    private sealed class FakeThumbnailHttpMessageHandler : HttpMessageHandler
    {
        private static readonly byte[] TinyPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4////fwAJ+wP9KobjigAAAABJRU5ErkJggg==");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri?.ToString() ?? string.Empty;

            if (requestUri.Contains("/search/tv", StringComparison.Ordinal))
            {
                return Task.FromResult(CreateJsonResponse(
                    """
                    {
                      "results": [
                        {
                          "id": 70523,
                          "name": "暗黑",
                          "original_name": "Dark",
                          "overview": "时间与命运交错。",
                          "poster_path": "/tv-poster.jpg",
                          "first_air_date": "2017-12-01",
                          "vote_average": 8.7,
                          "popularity": 150
                        }
                      ]
                    }
                    """));
            }

            if (requestUri.Contains("/tv/70523/season/1/episode/1", StringComparison.Ordinal))
            {
                return Task.FromResult(CreateJsonResponse("""{ "still_path": "/episode-still.jpg" }"""));
            }

            if (requestUri.Contains("image.tmdb.org", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(TinyPng)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(requestUri, Encoding.UTF8, "text/plain")
            });
        }

        private static HttpResponseMessage CreateJsonResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
