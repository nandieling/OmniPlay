using System.Net;
using System.Net.Http;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Core.Settings;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.FileSystem;
using OmniPlay.Infrastructure.Library;
using OmniPlay.Infrastructure.Tmdb;

namespace OmniPlay.Tests;

public sealed class LibraryMetadataEnricherTests : IDisposable
{
    private readonly string rootPath;
    private readonly string libraryRootPath;
    private readonly TestStoragePaths storagePaths;
    private readonly SqliteDatabase database;
    private readonly MediaSourceRepository mediaSourceRepository;

    public LibraryMetadataEnricherTests()
    {
        rootPath = Path.Combine(
            AppContext.BaseDirectory,
            "test-data",
            nameof(LibraryMetadataEnricherTests),
            Guid.NewGuid().ToString("N"));
        libraryRootPath = Path.Combine(rootPath, "library");

        Directory.CreateDirectory(libraryRootPath);

        storagePaths = new TestStoragePaths(rootPath);
        database = new SqliteDatabase(storagePaths);
        database.EnsureInitialized();
        mediaSourceRepository = new MediaSourceRepository(database);
    }

    [Fact]
    public async Task EnrichMissingMetadataAsync_UpdatesMovieMetadataAndDownloadsPoster()
    {
        CreateMediaFile("movies/Inception.2010.mkv", 128);

        var sourceId = await mediaSourceRepository.AddAsync(new MediaSource
        {
            Name = "Movies",
            ProtocolType = "local",
            BaseUrl = libraryRootPath
        });

        using (var connection = database.OpenConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO movie (id, title, releaseDate, overview, posterPath, voteAverage, isLocked)
                VALUES (-101, 'Inception', '2010', NULL, NULL, NULL, 0);

                INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                VALUES ('movie-file', @SourceId, 'movies/Inception.2010.mkv', 'Inception.2010.mkv', 'movie', -101, NULL, 0, 0);
                """,
                new { SourceId = sourceId });
        }

        using var httpClient = new HttpClient(new FakeTmdbHttpMessageHandler())
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);
        var enricher = new LibraryMetadataEnricher(database, client);

        var summary = await enricher.EnrichMissingMetadataAsync();

        using var verification = database.OpenConnection();
        var movie = await verification.QuerySingleAsync<MovieRecord>(
            """
            SELECT title, releaseDate, overview, posterPath, voteAverage
            FROM movie
            WHERE id = -101
            """);

        Assert.Equal("Inception", movie.Title);
        Assert.Equal("2010-07-16", movie.ReleaseDate);
        Assert.Equal("Dream invasion.", movie.Overview);
        Assert.Equal(8.4, movie.VoteAverage);
        Assert.False(string.IsNullOrWhiteSpace(movie.PosterPath));
        Assert.Contains("movie-27205-", movie.PosterPath!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(movie.PosterPath));
        Assert.Equal(1, summary.UpdatedMovieCount);
        Assert.Equal(1, summary.DownloadedPosterCount);
    }

    [Fact]
    public async Task EnrichMissingMetadataAsync_UpdatesTvShowMetadataAndDownloadsPoster()
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
                VALUES (-202, 'Dark', NULL, NULL, NULL, NULL, 0);

                INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                VALUES ('tv-file', @SourceId, 'shows/Dark/Dark.S01E01.mkv', 'Dark.S01E01.mkv', 'tv', NULL, -202, 0, 0);
                """,
                new { SourceId = sourceId });
        }

        using var httpClient = new HttpClient(new FakeTmdbHttpMessageHandler())
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);
        var enricher = new LibraryMetadataEnricher(database, client);

        var summary = await enricher.EnrichMissingMetadataAsync();

        using var verification = database.OpenConnection();
        var show = await verification.QuerySingleAsync<TvShowRecord>(
            """
            SELECT title, firstAirDate, overview, posterPath, voteAverage
            FROM tvShow
            WHERE id = -202
            """);

        Assert.Equal("Dark", show.Title);
        Assert.Equal("2017-12-01", show.FirstAirDate);
        Assert.Equal("Time and fate intersect.", show.Overview);
        Assert.Equal(8.7, show.VoteAverage);
        Assert.False(string.IsNullOrWhiteSpace(show.PosterPath));
        Assert.Contains("tv-70523-", show.PosterPath!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(show.PosterPath));
        Assert.Equal(1, summary.UpdatedTvShowCount);
        Assert.Equal(1, summary.DownloadedPosterCount);
    }

    [Fact]
    public async Task EnrichMissingMetadataAsync_WhenMetadataEnrichmentDisabled_OnlyDownloadsPoster()
    {
        CreateMediaFile("movies/Inception.2010.mkv", 128);

        var sourceId = await mediaSourceRepository.AddAsync(new MediaSource
        {
            Name = "Movies",
            ProtocolType = "local",
            BaseUrl = libraryRootPath
        });

        using (var connection = database.OpenConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO movie (id, title, releaseDate, overview, posterPath, voteAverage, isLocked)
                VALUES (-301, 'Keep Title', '2010', NULL, NULL, NULL, 0);

                INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                VALUES ('movie-file-metadata-off', @SourceId, 'movies/Inception.2010.mkv', 'Inception.2010.mkv', 'movie', -301, NULL, 0, 0);
                """,
                new { SourceId = sourceId });
        }

        using var httpClient = new HttpClient(new FakeTmdbHttpMessageHandler())
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);
        var enricher = new LibraryMetadataEnricher(database, client);

        var summary = await enricher.EnrichMissingMetadataAsync(new TmdbSettings
        {
            EnableMetadataEnrichment = false,
            EnablePosterDownloads = true
        });

        using var verification = database.OpenConnection();
        var movie = await verification.QuerySingleAsync<MovieOptionalRecord>(
            """
            SELECT title, releaseDate, overview, posterPath, voteAverage
            FROM movie
            WHERE id = -301
            """);

        Assert.Equal("Keep Title", movie.Title);
        Assert.Equal("2010", movie.ReleaseDate);
        Assert.Null(movie.Overview);
        Assert.Null(movie.VoteAverage);
        Assert.False(string.IsNullOrWhiteSpace(movie.PosterPath));
        Assert.True(File.Exists(movie.PosterPath));
        Assert.Equal(0, summary.UpdatedMovieCount);
        Assert.Equal(1, summary.DownloadedPosterCount);
    }

    [Fact]
    public async Task EnrichMissingMetadataAsync_WhenPosterDownloadsDisabled_OnlyUpdatesMetadata()
    {
        CreateMediaFile("movies/Inception.2010.mkv", 128);

        var sourceId = await mediaSourceRepository.AddAsync(new MediaSource
        {
            Name = "Movies",
            ProtocolType = "local",
            BaseUrl = libraryRootPath
        });

        using (var connection = database.OpenConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO movie (id, title, releaseDate, overview, posterPath, voteAverage, isLocked)
                VALUES (-302, 'Inception', '2010', NULL, NULL, NULL, 0);

                INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                VALUES ('movie-file-poster-off', @SourceId, 'movies/Inception.2010.mkv', 'Inception.2010.mkv', 'movie', -302, NULL, 0, 0);
                """,
                new { SourceId = sourceId });
        }

        using var httpClient = new HttpClient(new FakeTmdbHttpMessageHandler())
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);
        var enricher = new LibraryMetadataEnricher(database, client);

        var summary = await enricher.EnrichMissingMetadataAsync(new TmdbSettings
        {
            EnableMetadataEnrichment = true,
            EnablePosterDownloads = false
        });

        using var verification = database.OpenConnection();
        var movie = await verification.QuerySingleAsync<MovieOptionalRecord>(
            """
            SELECT title, releaseDate, overview, posterPath, voteAverage
            FROM movie
            WHERE id = -302
            """);

        Assert.Equal("Inception", movie.Title);
        Assert.Equal("2010-07-16", movie.ReleaseDate);
        Assert.Equal("Dream invasion.", movie.Overview);
        Assert.Equal(8.4, movie.VoteAverage);
        Assert.Null(movie.PosterPath);
        Assert.Equal(1, summary.UpdatedMovieCount);
        Assert.Equal(0, summary.DownloadedPosterCount);
    }

    [Fact]
    public async Task EnrichMissingMetadataAsync_SkipsImplausibleMovieYearMatch()
    {
        CreateMediaFile("movies/Inception.2010.mkv", 128);

        var sourceId = await mediaSourceRepository.AddAsync(new MediaSource
        {
            Name = "Movies",
            ProtocolType = "local",
            BaseUrl = libraryRootPath
        });

        using (var connection = database.OpenConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO movie (id, title, releaseDate, overview, posterPath, voteAverage, isLocked)
                VALUES (-401, 'Keep Me', '2010', NULL, NULL, NULL, 0);

                INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                VALUES ('movie-file-year-mismatch', @SourceId, 'movies/Inception.2010.mkv', 'Inception.2010.mkv', 'movie', -401, NULL, 0, 0);
                """,
                new { SourceId = sourceId });
        }

        using var httpClient = new HttpClient(new YearMismatchTmdbHttpMessageHandler())
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);
        var enricher = new LibraryMetadataEnricher(database, client);

        var summary = await enricher.EnrichMissingMetadataAsync();

        using var verification = database.OpenConnection();
        var movie = await verification.QuerySingleAsync<MovieOptionalRecord>(
            """
            SELECT title, releaseDate, overview, posterPath, voteAverage
            FROM movie
            WHERE id = -401
            """);

        Assert.Equal("Keep Me", movie.Title);
        Assert.Equal("2010", movie.ReleaseDate);
        Assert.Null(movie.Overview);
        Assert.Null(movie.PosterPath);
        Assert.Null(movie.VoteAverage);
        Assert.Equal(0, summary.UpdatedMovieCount);
        Assert.Equal(0, summary.DownloadedPosterCount);
    }

    [Fact]
    public async Task EnrichMissingMetadataAsync_UsesSourcePathYearWhenStoredYearIsBlank()
    {
        CreateMediaFile("movies/Inception.2010.mkv", 128);

        var sourceId = await mediaSourceRepository.AddAsync(new MediaSource
        {
            Name = "Movies",
            ProtocolType = "local",
            BaseUrl = libraryRootPath
        });

        using (var connection = database.OpenConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO movie (id, title, releaseDate, overview, posterPath, voteAverage, isLocked)
                VALUES (-403, 'Inception', NULL, NULL, NULL, NULL, 0);

                INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                VALUES ('movie-file-path-year', @SourceId, 'movies/Inception.2010.mkv', 'Inception.2010.mkv', 'movie', -403, NULL, 0, 0);
                """,
                new { SourceId = sourceId });
        }

        using var httpClient = new HttpClient(new YearMismatchTmdbHttpMessageHandler())
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);
        var enricher = new LibraryMetadataEnricher(database, client);

        var summary = await enricher.EnrichMissingMetadataAsync();

        using var verification = database.OpenConnection();
        var movie = await verification.QuerySingleAsync<MovieOptionalRecord>(
            """
            SELECT title, releaseDate, overview, posterPath, voteAverage
            FROM movie
            WHERE id = -403
            """);

        Assert.Equal("Inception", movie.Title);
        Assert.Null(movie.ReleaseDate);
        Assert.Null(movie.Overview);
        Assert.Null(movie.PosterPath);
        Assert.Null(movie.VoteAverage);
        Assert.Equal(0, summary.UpdatedMovieCount);
        Assert.Equal(0, summary.DownloadedPosterCount);
    }

    [Fact]
    public async Task EnrichMissingMetadataAsync_PrefersChineseFallbackTitleWhenTmdbReturnsEnglishOnly()
    {
        CreateMediaFile("movies/寄生虫/Parasite.2019.mkv", 128);

        var sourceId = await mediaSourceRepository.AddAsync(new MediaSource
        {
            Name = "Movies",
            ProtocolType = "local",
            BaseUrl = libraryRootPath
        });

        using (var connection = database.OpenConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO movie (id, title, releaseDate, overview, posterPath, voteAverage, isLocked)
                VALUES (-402, 'Parasite', '2019', NULL, NULL, NULL, 0);

                INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                VALUES ('movie-file-chinese-fallback', @SourceId, 'movies/寄生虫/Parasite.2019.mkv', 'Parasite.2019.mkv', 'movie', -402, NULL, 0, 0);
                """,
                new { SourceId = sourceId });
        }

        using var httpClient = new HttpClient(new EnglishOnlyTmdbHttpMessageHandler())
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);
        var enricher = new LibraryMetadataEnricher(database, client);

        var summary = await enricher.EnrichMissingMetadataAsync();

        using var verification = database.OpenConnection();
        var movie = await verification.QuerySingleAsync<MovieRecord>(
            """
            SELECT title, releaseDate, overview, posterPath, voteAverage
            FROM movie
            WHERE id = -402
            """);

        Assert.Equal("寄生虫", movie.Title);
        Assert.Equal("2019-05-30", movie.ReleaseDate);
        Assert.Equal("Greed and class discrimination.", movie.Overview);
        Assert.Equal(8.5, movie.VoteAverage);
        Assert.Equal(1, summary.UpdatedMovieCount);
    }

    [Fact]
    public async Task EnrichMissingMetadataAsync_UsesSourcePathYearForTvShowReboot()
    {
        const string relativePath = "shows/万物生灵.All.Creatures.Great.and.Small.2020年/万物生灵.All.Creatures.Great.and.Small.S01E01.2020年.mkv";
        CreateMediaFile(relativePath, 128);

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
                VALUES (-501, '万物生灵', NULL, NULL, NULL, NULL, 0);

                INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                VALUES ('tv-reboot-year', @SourceId, @RelativePath, '万物生灵.All.Creatures.Great.and.Small.S01E01.2020年.mkv', 'tv', NULL, -501, 0, 0);
                """,
                new
                {
                    SourceId = sourceId,
                    RelativePath = relativePath
                });
        }

        using var httpClient = new HttpClient(new TvRebootTmdbHttpMessageHandler())
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);
        var enricher = new LibraryMetadataEnricher(database, client);

        var summary = await enricher.EnrichMissingMetadataAsync(new TmdbSettings
        {
            EnableMetadataEnrichment = true,
            EnablePosterDownloads = false,
            Language = "zh-CN"
        });

        using var verification = database.OpenConnection();
        var show = await verification.QuerySingleAsync<TvShowLanguageRecord>(
            """
            SELECT title, firstAirDate, overview, metadataLanguage
            FROM tvShow
            WHERE id = -501
            """);

        Assert.Equal("万物生灵", show.Title);
        Assert.Equal("2020-09-01", show.FirstAirDate);
        Assert.Equal("The modern remake.", show.Overview);
        Assert.Equal("zh-CN", show.MetadataLanguage);
        Assert.Equal(1, summary.UpdatedTvShowCount);
    }

    [Fact]
    public async Task EnrichMissingMetadataAsync_RefreshesCompleteEnglishMetadataWhenLanguageChangesToChinese()
    {
        CreateMediaFile("movies/Inception.2010.mkv", 128);

        var sourceId = await mediaSourceRepository.AddAsync(new MediaSource
        {
            Name = "Movies",
            ProtocolType = "local",
            BaseUrl = libraryRootPath
        });

        using (var connection = database.OpenConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO movie (
                    id,
                    title,
                    releaseDate,
                    overview,
                    posterPath,
                    voteAverage,
                    isLocked,
                    productionCountryCodes,
                    originalLanguage,
                    metadataLanguage)
                VALUES (
                    -502,
                    'Inception',
                    '2010-07-16',
                    'English metadata.',
                    NULL,
                    8.4,
                    0,
                    'US',
                    'en',
                    'en-US');

                INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                VALUES ('movie-language-refresh', @SourceId, 'movies/Inception.2010.mkv', 'Inception.2010.mkv', 'movie', -502, NULL, 0, 0);
                """,
                new { SourceId = sourceId });
        }

        var settingsService = new JsonSettingsService(storagePaths);
        await settingsService.SaveAsync(new AppSettings
        {
            Tmdb = new TmdbSettings
            {
                EnableBuiltInPublicSource = true,
                Language = "en-US"
            }
        });
        var handler = new ChineseLanguageOverrideTmdbHttpMessageHandler();
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);
        var enricher = new LibraryMetadataEnricher(database, client);

        var summary = await enricher.EnrichMissingMetadataAsync(new TmdbSettings
        {
            EnableMetadataEnrichment = true,
            EnablePosterDownloads = false,
            EnableBuiltInPublicSource = true,
            Language = "zh-CN"
        });

        using var verification = database.OpenConnection();
        var movie = await verification.QuerySingleAsync<MovieLanguageRecord>(
            """
            SELECT title, overview, metadataLanguage
            FROM movie
            WHERE id = -502
            """);

        Assert.Contains("zh-CN", handler.SearchLanguages);
        Assert.Equal("盗梦空间", movie.Title);
        Assert.Equal("中文简介。", movie.Overview);
        Assert.Equal("zh-CN", movie.MetadataLanguage);
        Assert.Equal(1, summary.UpdatedMovieCount);
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

    private sealed record MovieRecord(
        string Title,
        string ReleaseDate,
        string Overview,
        string? PosterPath,
        double VoteAverage);

    private sealed record TvShowRecord(
        string Title,
        string FirstAirDate,
        string Overview,
        string? PosterPath,
        double VoteAverage);

    private sealed record MovieLanguageRecord(
        string Title,
        string Overview,
        string? MetadataLanguage);

    private sealed record TvShowLanguageRecord(
        string Title,
        string FirstAirDate,
        string Overview,
        string? MetadataLanguage);

    private sealed class MovieOptionalRecord
    {
        public string Title { get; init; } = string.Empty;

        public string? ReleaseDate { get; init; }

        public string? Overview { get; init; }

        public string? PosterPath { get; init; }

        public double? VoteAverage { get; init; }
    }

    private sealed class FakeTmdbHttpMessageHandler : HttpMessageHandler
    {
        private static readonly byte[] TinyPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4////fwAJ+wP9KobjigAAAABJRU5ErkJggg==");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri?.ToString() ?? string.Empty;

            if (requestUri.Contains("/search/movie", StringComparison.Ordinal))
            {
                return Task.FromResult(CreateJsonResponse(
                    """
                    {
                      "results": [
                        {
                          "id": 27205,
                          "title": "Inception",
                          "original_title": "Inception",
                          "overview": "Dream invasion.",
                          "poster_path": "/movie-poster.jpg",
                          "release_date": "2010-07-16",
                          "vote_average": 8.4,
                          "popularity": 90
                        },
                        {
                          "id": 99999,
                          "title": "Inception: The Cobol Job",
                          "original_title": "Inception: The Cobol Job",
                          "overview": "Animated prequel.",
                          "poster_path": "/wrong-movie-poster.jpg",
                          "release_date": "2010-12-07",
                          "vote_average": 6.9,
                          "popularity": 999
                        }
                      ]
                    }
                    """));
            }

            if (requestUri.Contains("/search/tv", StringComparison.Ordinal))
            {
                return Task.FromResult(CreateJsonResponse(
                    """
                    {
                      "results": [
                        {
                          "id": 70523,
                          "name": "Dark",
                          "original_name": "Dark",
                          "overview": "Time and fate intersect.",
                          "poster_path": "/tv-poster.jpg",
                          "first_air_date": "2017-12-01",
                          "vote_average": 8.7,
                          "popularity": 150
                        },
                        {
                          "id": 88888,
                          "name": "Dark Shadows",
                          "original_name": "Dark Shadows",
                          "overview": "Wrong candidate.",
                          "poster_path": "/wrong-tv-poster.jpg",
                          "first_air_date": "2016-01-01",
                          "vote_average": 7.1,
                          "popularity": 999
                        }
                      ]
                    }
                    """));
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

    private sealed class YearMismatchTmdbHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri?.ToString() ?? string.Empty;

            if (requestUri.Contains("/search/movie", StringComparison.Ordinal))
            {
                return Task.FromResult(CreateJsonResponse(
                    """
                    {
                      "results": [
                        {
                          "id": 1101,
                          "title": "Inception",
                          "original_title": "Inception",
                          "overview": "Wrong year match.",
                          "poster_path": "/wrong-year.jpg",
                          "release_date": "1999-01-01",
                          "vote_average": 8.1,
                          "popularity": 999
                        }
                      ]
                    }
                    """));
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

    private sealed class EnglishOnlyTmdbHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri?.ToString() ?? string.Empty;

            if (requestUri.Contains("/search/movie", StringComparison.Ordinal))
            {
                return Task.FromResult(CreateJsonResponse(
                    """
                    {
                      "results": [
                        {
                          "id": 496243,
                          "title": "Parasite",
                          "original_title": "Gisaengchung",
                          "overview": "Greed and class discrimination.",
                          "poster_path": "/parasite.jpg",
                          "release_date": "2019-05-30",
                          "vote_average": 8.5,
                          "popularity": 350
                        }
                      ]
                    }
                    """));
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

        private static readonly byte[] TinyPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4////fwAJ+wP9KobjigAAAABJRU5ErkJggg==");

        private static HttpResponseMessage CreateJsonResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class TvRebootTmdbHttpMessageHandler : HttpMessageHandler
    {
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
                          "id": 100,
                          "name": "万物生灵",
                          "original_name": "All Creatures Great and Small",
                          "overview": "The older series.",
                          "first_air_date": "1978-01-08",
                          "vote_average": 8.0,
                          "popularity": 900
                        },
                        {
                          "id": 200,
                          "name": "万物生灵",
                          "original_name": "All Creatures Great and Small",
                          "overview": "The modern remake.",
                          "first_air_date": "2020-09-01",
                          "vote_average": 8.5,
                          "popularity": 100
                        }
                      ]
                    }
                    """));
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

    private sealed class ChineseLanguageOverrideTmdbHttpMessageHandler : HttpMessageHandler
    {
        public List<string> SearchLanguages { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri?.ToString() ?? string.Empty;

            if (requestUri.Contains("/search/movie", StringComparison.Ordinal))
            {
                var language = ReadQueryValue(request.RequestUri, "language");
                SearchLanguages.Add(language);

                return Task.FromResult(CreateJsonResponse(string.Equals(language, "zh-CN", StringComparison.OrdinalIgnoreCase)
                    ? """
                      {
                        "results": [
                          {
                            "id": 27205,
                            "title": "盗梦空间",
                            "original_title": "Inception",
                            "overview": "中文简介。",
                            "release_date": "2010-07-16",
                            "vote_average": 8.4,
                            "popularity": 90
                          }
                        ]
                      }
                      """
                    : """
                      {
                        "results": [
                          {
                            "id": 27205,
                            "title": "Inception",
                            "original_title": "Inception",
                            "overview": "English metadata.",
                            "release_date": "2010-07-16",
                            "vote_average": 8.4,
                            "popularity": 90
                          }
                        ]
                      }
                      """));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(requestUri, Encoding.UTF8, "text/plain")
            });
        }

        private static string ReadQueryValue(Uri? requestUri, string key)
        {
            if (requestUri is null)
            {
                return string.Empty;
            }

            foreach (var part in requestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pieces = part.Split('=', 2);
                if (pieces.Length == 2 && string.Equals(pieces[0], key, StringComparison.Ordinal))
                {
                    return Uri.UnescapeDataString(pieces[1]);
                }
            }

            return string.Empty;
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
