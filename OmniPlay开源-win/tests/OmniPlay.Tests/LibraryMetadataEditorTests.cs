using System.Net;
using System.Net.Http;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using OmniPlay.Core.Settings;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.FileSystem;
using OmniPlay.Infrastructure.Library;
using OmniPlay.Infrastructure.Tmdb;

namespace OmniPlay.Tests;

public sealed class LibraryMetadataEditorTests : IDisposable
{
    private readonly string rootPath;
    private readonly string libraryRootPath;
    private readonly TestStoragePaths storagePaths;
    private readonly SqliteDatabase database;
    private readonly MediaSourceRepository mediaSourceRepository;

    public LibraryMetadataEditorTests()
    {
        rootPath = Path.Combine(
            AppContext.BaseDirectory,
            "test-data",
            nameof(LibraryMetadataEditorTests),
            Guid.NewGuid().ToString("N"));
        libraryRootPath = Path.Combine(rootPath, "library");

        Directory.CreateDirectory(libraryRootPath);

        storagePaths = new TestStoragePaths(rootPath);
        database = new SqliteDatabase(storagePaths);
        database.EnsureInitialized();
        mediaSourceRepository = new MediaSourceRepository(database);
    }

    [Fact]
    public async Task RefreshMovieAsync_UpdatesMovieMetadataAndDownloadsPoster()
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
                VALUES (-101, 'Wrong Title', '2010', NULL, NULL, NULL, 0);

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
        var editor = new LibraryMetadataEditor(database, client);

        var result = await editor.RefreshMovieAsync(-101, "Inception");

        Assert.True(result.FoundMatch);
        Assert.True(result.Updated);
        Assert.True(result.DownloadedPoster);

        using var verification = database.OpenConnection();
        var movie = await verification.QuerySingleAsync<MovieRecord>(
            """
            SELECT title, releaseDate, overview, posterPath, voteAverage, isLocked
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
        Assert.False(movie.IsLocked);
    }

    [Fact]
    public async Task RefreshTvShowAsync_UpdatesTvShowMetadataAndDownloadsPoster()
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
                VALUES (-202, 'Wrong Show', NULL, NULL, NULL, NULL, 0);

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
        var editor = new LibraryMetadataEditor(database, client);

        var result = await editor.RefreshTvShowAsync(-202, "Dark");

        Assert.True(result.FoundMatch);
        Assert.True(result.Updated);
        Assert.True(result.DownloadedPoster);

        using var verification = database.OpenConnection();
        var show = await verification.QuerySingleAsync<TvShowRecord>(
            """
            SELECT title, firstAirDate, overview, posterPath, voteAverage, isLocked
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
        Assert.False(show.IsLocked);
    }

    [Fact]
    public async Task SearchMovieMatchesAsync_AndApplyMovieMatchAsync_UsesSelectedCandidate()
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
                VALUES (-203, 'Original Movie', '2010', NULL, NULL, NULL, 0);

                INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                VALUES ('movie-file-2', @SourceId, 'movies/Inception.2010.mkv', 'Inception.2010.mkv', 'movie', -203, NULL, 0, 0);
                """,
                new { SourceId = sourceId });
        }

        using var httpClient = new HttpClient(new FakeTmdbHttpMessageHandler())
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);
        var editor = new LibraryMetadataEditor(database, client);

        var candidates = await editor.SearchMovieMatchesAsync(-203, "Inception");

        Assert.True(candidates.Count >= 2);
        Assert.All(candidates, candidate => Assert.False(string.IsNullOrWhiteSpace(candidate.PreviewImagePath)));
        var selected = Assert.Single(candidates, candidate => candidate.Title == "Inception: The Cobol Job");
        Assert.True(File.Exists(selected.PreviewImagePath));

        var result = await editor.ApplyMovieMatchAsync(-203, selected);

        Assert.True(result.FoundMatch);
        Assert.True(result.Updated);
        Assert.Equal("Inception: The Cobol Job", result.MatchedTitle);

        using var verification = database.OpenConnection();
        var movie = await verification.QuerySingleAsync<MovieRecord>(
            """
            SELECT title, releaseDate, overview, posterPath, voteAverage, isLocked
            FROM movie
            WHERE id = -203
            """);

        Assert.Equal("Inception: The Cobol Job", movie.Title);
        Assert.Equal("2010-12-07", movie.ReleaseDate);
        Assert.Equal("Animated prequel.", movie.Overview);
        Assert.Equal(6.9, movie.VoteAverage);
        Assert.Contains("movie-99999-", movie.PosterPath!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchMovieMatchesAsync_WhenPrimaryQueryMisses_UsesParentChineseFallbackAndAnnotatesCandidate()
    {
        const string relativePath = "movies/\u5BC4\u751F\u866B/Parasite.2019.mkv";
        CreateMediaFile(relativePath, 128);

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
                VALUES (-204, 'Parasite', '2019', NULL, NULL, NULL, 0);

                INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                VALUES ('movie-fallback-search', @SourceId, @RelativePath, 'Parasite.2019.mkv', 'movie', -204, NULL, 0, 0);
                """,
                new
                {
                    SourceId = sourceId,
                    RelativePath = relativePath
                });
        }

        var settingsService = await CreateEnglishTmdbSettingsServiceAsync();
        var handler = new ParentFolderFallbackMovieTmdbHttpMessageHandler();
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);
        var editor = new LibraryMetadataEditor(database, client, settingsService);

        var candidates = await editor.SearchMovieMatchesAsync(-204, "Parasite");

        var candidate = Assert.Single(candidates);
        Assert.Equal("Parasite", candidate.Title);
        Assert.Equal("\u5BC4\u751F\u866B", candidate.MatchedQuery);
        Assert.Equal("\u7236\u76EE\u5F55\u4E2D\u6587\u540D", candidate.MatchedQueryLabel);
        Assert.Equal("\u7236\u76EE\u5F55\u4E2D\u6587\u540D\uFF1A\u5BC4\u751F\u866B", candidate.MatchedQueryText);
        Assert.False(string.IsNullOrWhiteSpace(candidate.PreviewImagePath));
        Assert.True(File.Exists(candidate.PreviewImagePath));
        Assert.Contains("Parasite", handler.SearchQueries);
        Assert.Contains("\u5BC4\u751F\u866B", handler.SearchQueries);
    }

    [Fact]
    public async Task SetMovieLockedAsync_PreventsAutomaticEnrichment()
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
                VALUES (-303, 'Locked Movie', '2010', NULL, NULL, NULL, 0);

                INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                VALUES ('movie-locked', @SourceId, 'movies/Inception.2010.mkv', 'Inception.2010.mkv', 'movie', -303, NULL, 0, 0);
                """,
                new { SourceId = sourceId });
        }

        using var httpClient = new HttpClient(new FakeTmdbHttpMessageHandler())
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);
        var editor = new LibraryMetadataEditor(database, client);
        var enricher = new LibraryMetadataEnricher(database, client);

        await editor.SetMovieLockedAsync(-303, true);
        await enricher.EnrichMissingMetadataAsync();

        using var verification = database.OpenConnection();
        var movie = await verification.QuerySingleAsync<MovieRecord>(
            """
            SELECT title, releaseDate, overview, posterPath, voteAverage, isLocked
            FROM movie
            WHERE id = -303
            """);

        Assert.Equal("Locked Movie", movie.Title);
        Assert.Equal("2010", movie.ReleaseDate);
        Assert.Null(movie.Overview);
        Assert.Null(movie.PosterPath);
        Assert.Null(movie.VoteAverage);
        Assert.True(movie.IsLocked);
    }

    [Fact]
    public async Task RefreshMovieAsync_WhenOnlyYearMismatchResultExists_DoesNotUpdateMovie()
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
                VALUES (-404, 'Original Movie', '2010', NULL, NULL, NULL, 0);

                INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                VALUES ('movie-year-mismatch', @SourceId, 'movies/Inception.2010.mkv', 'Inception.2010.mkv', 'movie', -404, NULL, 0, 0);
                """,
                new { SourceId = sourceId });
        }

        using var httpClient = new HttpClient(new YearMismatchTmdbHttpMessageHandler())
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);
        var editor = new LibraryMetadataEditor(database, client);

        var result = await editor.RefreshMovieAsync(-404, "Inception");

        Assert.False(result.FoundMatch);
        Assert.False(result.Updated);

        using var verification = database.OpenConnection();
        var movie = await verification.QuerySingleAsync<MovieRecord>(
            """
            SELECT title, releaseDate, overview, posterPath, voteAverage, isLocked
            FROM movie
            WHERE id = -404
            """);

        Assert.Equal("Original Movie", movie.Title);
        Assert.Equal("2010", movie.ReleaseDate);
        Assert.Null(movie.Overview);
        Assert.Null(movie.PosterPath);
        Assert.Null(movie.VoteAverage);
        Assert.False(movie.IsLocked);
    }

    [Fact]
    public async Task RefreshMovieAsync_WhenPrimaryQueryMisses_UsesParentChineseFallbackAndReportsMatchedQuery()
    {
        const string relativePath = "movies/\u5BC4\u751F\u866B/Parasite.2019.mkv";
        CreateMediaFile(relativePath, 128);

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
                VALUES (-406, 'Wrong Title', '2019', NULL, NULL, NULL, 0);

                INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                VALUES ('movie-fallback-refresh', @SourceId, @RelativePath, 'Parasite.2019.mkv', 'movie', -406, NULL, 0, 0);
                """,
                new
                {
                    SourceId = sourceId,
                    RelativePath = relativePath
                });
        }

        var settingsService = await CreateEnglishTmdbSettingsServiceAsync();
        var handler = new ParentFolderFallbackMovieTmdbHttpMessageHandler();
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);
        var editor = new LibraryMetadataEditor(database, client, settingsService);

        var result = await editor.RefreshMovieAsync(-406, "Parasite");

        Assert.True(result.FoundMatch);
        Assert.True(result.Updated);
        Assert.Equal("Parasite", result.MatchedTitle);
        Assert.Contains("\u5BC4\u751F\u866B", result.Message);
        Assert.Contains("Parasite", result.Message);
        Assert.Contains("Parasite", handler.SearchQueries);
        Assert.Contains("\u5BC4\u751F\u866B", handler.SearchQueries);

        using var verification = database.OpenConnection();
        var movie = await verification.QuerySingleAsync<MovieRecord>(
            """
            SELECT title, releaseDate, overview, posterPath, voteAverage, isLocked
            FROM movie
            WHERE id = -406
            """);

        Assert.Equal("Parasite", movie.Title);
        Assert.Equal("2019-05-30", movie.ReleaseDate);
        Assert.Equal("Greed and class discrimination.", movie.Overview);
        Assert.Equal(8.5, movie.VoteAverage);
        Assert.Contains("movie-496243-", movie.PosterPath!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(movie.PosterPath));
    }

    [Fact]
    public async Task RefreshMovieAsync_PrefersChineseFallbackTitleWhenTmdbReturnsEnglishOnly()
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
                VALUES (-405, 'Parasite', '2019', NULL, NULL, NULL, 0);

                INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                VALUES ('movie-chinese-fallback', @SourceId, 'movies/寄生虫/Parasite.2019.mkv', 'Parasite.2019.mkv', 'movie', -405, NULL, 0, 0);
                """,
                new { SourceId = sourceId });
        }

        using var httpClient = new HttpClient(new EnglishOnlyTmdbHttpMessageHandler())
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);
        var editor = new LibraryMetadataEditor(database, client, settingsService);

        var result = await editor.RefreshMovieAsync(-405, "Parasite");

        Assert.True(result.FoundMatch);
        Assert.True(result.Updated);

        using var verification = database.OpenConnection();
        var movie = await verification.QuerySingleAsync<MovieRecord>(
            """
            SELECT title, releaseDate, overview, posterPath, voteAverage, isLocked
            FROM movie
            WHERE id = -405
            """);

        Assert.Equal("寄生虫", movie.Title);
        Assert.Equal("2019-05-30", movie.ReleaseDate);
        Assert.Equal("Greed and class discrimination.", movie.Overview);
        Assert.Equal(8.5, movie.VoteAverage);
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

    private async Task<JsonSettingsService> CreateEnglishTmdbSettingsServiceAsync()
    {
        var settingsService = new JsonSettingsService(storagePaths);
        await settingsService.SaveAsync(new AppSettings
        {
            Tmdb = new TmdbSettings
            {
                EnableBuiltInPublicSource = true,
                Language = "en-US"
            }
        });

        return settingsService;
    }

    private sealed class MovieRecord
    {
        public string Title { get; init; } = string.Empty;

        public string? ReleaseDate { get; init; }

        public string? Overview { get; init; }

        public string? PosterPath { get; init; }

        public double? VoteAverage { get; init; }

        public bool IsLocked { get; init; }
    }

    private sealed class TvShowRecord
    {
        public string Title { get; init; } = string.Empty;

        public string? FirstAirDate { get; init; }

        public string? Overview { get; init; }

        public string? PosterPath { get; init; }

        public double? VoteAverage { get; init; }

        public bool IsLocked { get; init; }
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
                          "poster_path": "/movie-prequel-poster.jpg",
                          "release_date": "2010-12-07",
                          "vote_average": 6.9,
                          "popularity": 40
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

    private sealed class ParentFolderFallbackMovieTmdbHttpMessageHandler : HttpMessageHandler
    {
        private static readonly byte[] TinyPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4////fwAJ+wP9KobjigAAAABJRU5ErkJggg==");

        private readonly List<string> searchQueries = [];

        public IReadOnlyList<string> SearchQueries => searchQueries;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri?.ToString() ?? string.Empty;

            if (requestUri.Contains("/search/movie", StringComparison.Ordinal))
            {
                var query = GetQueryParameter(request.RequestUri, "query");
                if (!string.IsNullOrWhiteSpace(query))
                {
                    searchQueries.Add(query);
                }

                return Task.FromResult(string.Equals(query, "\u5BC4\u751F\u866B", StringComparison.Ordinal)
                    ? CreateJsonResponse(
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
                        """)
                    : CreateJsonResponse(
                        """
                        {
                          "results": []
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

        private static string GetQueryParameter(Uri? requestUri, string key)
        {
            if (requestUri is null)
            {
                return string.Empty;
            }

            var query = requestUri.Query;
            if (query.StartsWith("?", StringComparison.Ordinal))
            {
                query = query[1..];
            }

            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var pieces = part.Split('=', 2);
                if (pieces.Length == 0)
                {
                    continue;
                }

                if (!string.Equals(Uri.UnescapeDataString(pieces[0]), key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return pieces.Length == 1
                    ? string.Empty
                    : Uri.UnescapeDataString(pieces[1]);
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
