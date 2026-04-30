using System.Net;
using System.Net.Http;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Core.Settings;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.Library;

namespace OmniPlay.Tests;

public sealed class LibraryScannerTests : IDisposable
{
    private readonly string rootPath;
    private readonly string libraryRootPath;
    private readonly SqliteDatabase database;
    private readonly MediaSourceRepository mediaSourceRepository;
    private readonly LibraryScanner scanner;

    public LibraryScannerTests()
    {
        rootPath = Path.Combine(
            AppContext.BaseDirectory,
            "test-data",
            nameof(LibraryScannerTests),
            Guid.NewGuid().ToString("N"));
        libraryRootPath = Path.Combine(rootPath, "library");

        Directory.CreateDirectory(libraryRootPath);

        var storagePaths = new TestStoragePaths(rootPath);
        database = new SqliteDatabase(storagePaths);
        database.EnsureInitialized();
        mediaSourceRepository = new MediaSourceRepository(database);
        scanner = new LibraryScanner(database, mediaSourceRepository);
    }

    [Fact]
    public async Task ScanAllAsync_SplitsMoviesAndTvShowsIntoSeparateCollections()
    {
        CreateMediaFile("movies/Inception.2010.mkv", 128);
        CreateMediaFile("shows/Dark/Dark.S01E01.mkv", 96);

        await mediaSourceRepository.AddAsync(new MediaSource
        {
            Name = "Library",
            ProtocolType = "local",
            BaseUrl = libraryRootPath
        });

        var summary = await scanner.ScanAllAsync();

        Assert.Equal(1, summary.NewMovieCount);
        Assert.Equal(1, summary.NewTvShowCount);
        Assert.Equal(2, summary.NewVideoFileCount);

        using var connection = database.OpenConnection();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM movie"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tvShow"));
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM videoFile"));
    }

    [Fact]
    public async Task ScanAllAsync_UsesExtractedMovieTitleAndLeavesYearBlankBeforeScrape()
    {
        CreateMediaFile(
            "movies/American Beauty 1999 Paramount Blu-ray 1080p AVC DTS-HD MA 5.1/American.Beauty.1999.Paramount.Blu-ray.1080p.AVC.DTS-HD.MA.5.1.iso",
            128);

        await mediaSourceRepository.AddAsync(new MediaSource
        {
            Name = "Library",
            ProtocolType = "local",
            BaseUrl = libraryRootPath
        });

        await scanner.ScanAllAsync();

        using var connection = database.OpenConnection();
        var movie = await connection.QuerySingleAsync<MovieScanRecord>(
            "SELECT title, releaseDate FROM movie");

        Assert.Equal("American Beauty", movie.Title);
        Assert.Null(movie.ReleaseDate);
    }

    [Fact]
    public async Task ScanAllAsync_RefreshesOldUnenrichedMoviePlaceholder()
    {
        CreateMediaFile("movies/Inception.2010.1080p.BluRay.x264.mkv", 128);

        var sourceId = await mediaSourceRepository.AddAsync(new MediaSource
        {
            Name = "Library",
            ProtocolType = "local",
            BaseUrl = libraryRootPath
        });

        using (var connection = database.OpenConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO movie (id, title, releaseDate, overview, posterPath, voteAverage, isLocked)
                VALUES (-1001, 'Inception.2010.1080p.BluRay.x264', '2010', NULL, NULL, NULL, 0);

                INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                VALUES ('old-placeholder', @SourceId, 'movies/Inception.2010.1080p.BluRay.x264.mkv', 'Inception.2010.1080p.BluRay.x264.mkv', 'movie', -1001, NULL, 0, 0);
                """,
                new { SourceId = sourceId });
        }

        await scanner.ScanAllAsync();

        using var verification = database.OpenConnection();
        var movie = await verification.QuerySingleAsync<MovieScanRecord>(
            "SELECT title, releaseDate FROM movie WHERE id = -1001");

        Assert.Equal("Inception", movie.Title);
        Assert.Null(movie.ReleaseDate);
    }

    [Fact]
    public async Task ScanAllAsync_GroupsDifferentSeasonFoldersIntoSingleTvShow()
    {
        CreateMediaFile("shows/Dark/Season 1/Dark.S01E01.mkv", 96);
        CreateMediaFile("shows/Dark/S02/Dark.S02E01.mkv", 96);
        CreateMediaFile("shows/Dark/特别季/Dark.S00E01.mkv", 96);

        await mediaSourceRepository.AddAsync(new MediaSource
        {
            Name = "Library",
            ProtocolType = "local",
            BaseUrl = libraryRootPath
        });

        var summary = await scanner.ScanAllAsync();

        Assert.Equal(1, summary.NewTvShowCount);
        Assert.Equal(3, summary.NewVideoFileCount);

        using var connection = database.OpenConnection();
        var showIds = (await connection.QueryAsync<long>(
            "SELECT DISTINCT episodeId FROM videoFile WHERE mediaType = 'tv' ORDER BY episodeId")).ToList();
        var showId = Assert.Single(showIds);

        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tvShow"));
        Assert.Equal("Dark", await connection.ExecuteScalarAsync<string>(
            "SELECT title FROM tvShow WHERE id = @Id",
            new { Id = showId }));
    }

    [Fact]
    public async Task ScanAllAsync_GroupsSeriesSeasonSubtitleFoldersIntoSingleTvShow()
    {
        CreateMediaFile("shows/南家三姐妹 再来一碗.Minami-ke Okawari.2008.S02/南家三姐妹 再来一碗.Minami-ke Okawari.S02E01.mkv", 96);
        CreateMediaFile("shows/南家三姐妹 我回来了.Minami-ke Tadaima.2013.S04/南家三姐妹 我回来了.Minami-ke Tadaima.S04E01.mkv", 96);

        await mediaSourceRepository.AddAsync(new MediaSource
        {
            Name = "Library",
            ProtocolType = "local",
            BaseUrl = libraryRootPath
        });

        var summary = await scanner.ScanAllAsync();

        Assert.Equal(1, summary.NewTvShowCount);
        Assert.Equal(2, summary.NewVideoFileCount);

        using var connection = database.OpenConnection();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tvShow"));
        Assert.Equal("南家三姐妹", await connection.ExecuteScalarAsync<string>("SELECT title FROM tvShow"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(DISTINCT episodeId) FROM videoFile WHERE mediaType = 'tv'"));
    }

    [Fact]
    public async Task ScanAllAsync_MergesExistingSeasonFolderTvShowsIntoSingleTvShow()
    {
        CreateMediaFile("shows/Dark/Season 1/Dark.S01E01.mkv", 96);
        CreateMediaFile("shows/Dark/Season 2/Dark.S02E01.mkv", 96);

        var sourceId = await mediaSourceRepository.AddAsync(new MediaSource
        {
            Name = "Library",
            ProtocolType = "local",
            BaseUrl = libraryRootPath
        });

        using (var connection = database.OpenConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO tvShow (id, title, firstAirDate, overview, posterPath, voteAverage, isLocked)
                VALUES (-9101, 'Season 1', '2017', 'Old season metadata', '/poster-s1.jpg', 8.5, 0),
                       (-9102, 'Season 2', '2019', 'Old season metadata', '/poster-s2.jpg', 8.6, 0);

                INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                VALUES ('dark-s1', @SourceId, 'shows/Dark/Season 1/Dark.S01E01.mkv', 'Dark.S01E01.mkv', 'tv', NULL, -9101, 120, 1800),
                       ('dark-s2', @SourceId, 'shows/Dark/Season 2/Dark.S02E01.mkv', 'Dark.S02E01.mkv', 'tv', NULL, -9102, 0, 1800);
                """,
                new { SourceId = sourceId });
        }

        var summary = await scanner.ScanAllAsync();

        Assert.Equal(1, summary.NewTvShowCount);
        Assert.Equal(0, summary.NewVideoFileCount);

        using var verification = database.OpenConnection();
        var show = await verification.QuerySingleAsync<TvShowRecord>(
            "SELECT id, title, posterPath FROM tvShow");
        var fileShowIds = (await verification.QueryAsync<long>(
            "SELECT DISTINCT episodeId FROM videoFile WHERE mediaType = 'tv' ORDER BY episodeId")).ToList();
        var fileProgress = await verification.ExecuteScalarAsync<double>(
            "SELECT playProgress FROM videoFile WHERE id = 'dark-s1'");

        Assert.Equal("Dark", show.Title);
        Assert.Equal("/poster-s1.jpg", show.PosterPath);
        Assert.Equal(show.Id, Assert.Single(fileShowIds));
        Assert.Equal(120, fileProgress);
    }

    [Fact]
    public async Task ScanAllAsync_RemovesDuplicateAndStaleRowsForExistingSource()
    {
        CreateMediaFile("movies/MovieA.mkv", 256);

        var sourceId = await mediaSourceRepository.AddAsync(new MediaSource
        {
            Name = "Library",
            ProtocolType = "local",
            BaseUrl = libraryRootPath
        });

        using (var connection = database.OpenConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO movie (id, title, releaseDate, overview, posterPath, voteAverage, isLocked)
                VALUES (-2001, 'Movie A', '2024', NULL, NULL, NULL, 0),
                       (-2002, 'Old Movie', '2020', NULL, NULL, NULL, 0);

                INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                VALUES ('drop-me', @SourceId, 'movies/moviea.mkv', 'MovieA.mkv', 'movie', -2001, NULL, 10, 20),
                       ('keep-me', @SourceId, 'movies\MovieA.mkv', 'MovieA.mkv', 'movie', -2001, NULL, 120, 150),
                       ('stale-file', @SourceId, 'movies/OldMovie.mkv', 'OldMovie.mkv', 'movie', -2002, NULL, 0, 0);
                """,
                new { SourceId = sourceId });
        }

        var summary = await scanner.ScanAllAsync();

        Assert.Equal(0, summary.NewMovieCount);
        Assert.Equal(0, summary.NewTvShowCount);
        Assert.Equal(0, summary.NewVideoFileCount);
        Assert.Equal(2, summary.RemovedVideoFileCount);

        using var verification = database.OpenConnection();
        var files = (await verification.QueryAsync<VideoFileRecord>(
            "SELECT relativePath, playProgress, duration FROM videoFile")).ToList();
        var kept = Assert.Single(files);

        Assert.Equal("movies/MovieA.mkv", kept.RelativePath);
        Assert.Equal(120, kept.PlayProgress);
        Assert.Equal(150, kept.Duration);
        Assert.Equal(1, await verification.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM movie"));
        Assert.Equal(0, await verification.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM videoFile WHERE relativePath = 'movies/OldMovie.mkv'"));
    }

    [Fact]
    public async Task ScanAllAsync_RootLevelBdmvSource_KeepsOnlyPrimaryStreamFiles()
    {
        var discRoot = Path.Combine(libraryRootPath, "InterstellarDisc");
        CreateMediaFile(Path.Combine("InterstellarDisc", "BDMV", "STREAM", "00001.m2ts"), 1000);
        CreateMediaFile(Path.Combine("InterstellarDisc", "BDMV", "STREAM", "00002.m2ts"), 120);

        await mediaSourceRepository.AddAsync(new MediaSource
        {
            Name = "InterstellarDisc",
            ProtocolType = "local",
            BaseUrl = discRoot
        });

        var summary = await scanner.ScanAllAsync();

        Assert.Equal(1, summary.NewMovieCount);
        Assert.Equal(1, summary.NewVideoFileCount);

        using var connection = database.OpenConnection();
        var relativePaths = (await connection.QueryAsync<string>(
            "SELECT relativePath FROM videoFile ORDER BY relativePath")).ToList();
        Assert.Equal(["BDMV/STREAM/00001.m2ts"], relativePaths);
    }

    [Fact]
    public async Task ScanAllAsync_ImportsWebDavDirectoryIntoLibrary()
    {
        using var httpClient = new HttpClient(new FakeWebDavHttpMessageHandler())
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var webDavClient = new WebDavDiscoveryClient(httpClient);
        var webDavScanner = new LibraryScanner(
            database,
            mediaSourceRepository,
            webDavDiscoveryClient: webDavClient);

        await mediaSourceRepository.AddAsync(new MediaSource
        {
            Name = "Remote",
            ProtocolType = "webdav",
            BaseUrl = "https://demo.example/library"
        });

        var summary = await webDavScanner.ScanAllAsync();

        Assert.Equal(1, summary.NewMovieCount);
        Assert.Equal(1, summary.NewTvShowCount);
        Assert.Equal(2, summary.NewVideoFileCount);

        using var connection = database.OpenConnection();
        var relativePaths = (await connection.QueryAsync<string>(
            "SELECT relativePath FROM videoFile ORDER BY relativePath")).ToList();
        Assert.Equal(
            [
                "movies/Inception.2010.mkv",
                "shows/Dark/Dark.S01E01.mkv"
            ],
            relativePaths);
    }

    [Fact]
    public async Task ScanAllAsync_MissingLocalSource_ReturnsDiagnosticInsteadOfThrowing()
    {
        await mediaSourceRepository.AddAsync(new MediaSource
        {
            Name = "MissingLibrary",
            ProtocolType = "local",
            BaseUrl = Path.Combine(rootPath, "missing-library")
        });

        var summary = await scanner.ScanAllAsync();

        Assert.True(summary.HasDiagnostics);
        Assert.Contains(summary.Diagnostics, message => message.Contains("目录不存在", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScanAllAsync_WebDavTimeout_ReturnsDiagnosticInsteadOfThrowing()
    {
        using var httpClient = new HttpClient(new TimeoutWebDavHttpMessageHandler())
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var webDavClient = new WebDavDiscoveryClient(httpClient);
        var webDavScanner = new LibraryScanner(
            database,
            mediaSourceRepository,
            webDavDiscoveryClient: webDavClient);

        await mediaSourceRepository.AddAsync(new MediaSource
        {
            Name = "RemoteTimeout",
            ProtocolType = "webdav",
            BaseUrl = "https://demo.example/library"
        });

        var summary = await webDavScanner.ScanAllAsync();

        Assert.True(summary.HasDiagnostics);
        Assert.Contains(summary.Diagnostics, message => message.Contains("连接超时", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScanAllAsync_DoesNotRunMetadataEnricher()
    {
        var metadataEnricher = new CapturingMetadataEnricher();
        var settingsService = new FakeSettingsService(new AppSettings
        {
            Tmdb = new TmdbSettings
            {
                EnableMetadataEnrichment = false,
                EnablePosterDownloads = true,
                EnableEpisodeThumbnailDownloads = false,
                Language = "en-US"
            }
        });
        var configuredScanner = new LibraryScanner(
            database,
            mediaSourceRepository,
            metadataEnricher,
            settingsService);

        _ = await configuredScanner.ScanAllAsync();

        Assert.Equal(0, metadataEnricher.CallCount);
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

    private sealed record VideoFileRecord(
        string RelativePath,
        double PlayProgress,
        double Duration);

    private sealed record MovieScanRecord(
        string Title,
        string? ReleaseDate);

    private sealed record TvShowRecord(
        long Id,
        string Title,
        string? PosterPath);

    private sealed class FakeWebDavHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!string.Equals(request.Method.Method, "PROPFIND", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
            }

            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var content = path switch
            {
                "/library/" => BuildDirectoryResponse(
                    "/library/",
                    ("/library/movies/", true, 0L),
                    ("/library/shows/", true, 0L)),
                "/library/movies/" => BuildDirectoryResponse(
                    "/library/movies/",
                    ("/library/movies/Inception.2010.mkv", false, 128L)),
                "/library/shows/" => BuildDirectoryResponse(
                    "/library/shows/",
                    ("/library/shows/Dark/", true, 0L)),
                "/library/shows/Dark/" => BuildDirectoryResponse(
                    "/library/shows/Dark/",
                    ("/library/shows/Dark/Dark.S01E01.mkv", false, 96L)),
                _ => null
            };

            return Task.FromResult(content is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage((HttpStatusCode)207)
                {
                    Content = new StringContent(content, Encoding.UTF8, "application/xml")
                });
        }

        private static string BuildDirectoryResponse(
            string currentDirectory,
            params (string Href, bool IsDirectory, long Size)[] children)
        {
            var builder = new StringBuilder();
            builder.Append("""<?xml version="1.0" encoding="utf-8"?><d:multistatus xmlns:d="DAV:">""");
            builder.Append(BuildResponse(currentDirectory, isDirectory: true, size: 0));

            foreach (var child in children)
            {
                builder.Append(BuildResponse(child.Href, child.IsDirectory, child.Size));
            }

            builder.Append("</d:multistatus>");
            return builder.ToString();
        }

        private static string BuildResponse(string href, bool isDirectory, long size)
        {
            var resourceType = isDirectory ? "<d:collection />" : string.Empty;
            var contentLength = isDirectory ? string.Empty : $"<d:getcontentlength>{size}</d:getcontentlength>";

            return $"""
                    <d:response>
                      <d:href>{href}</d:href>
                      <d:propstat>
                        <d:prop>
                          <d:resourcetype>{resourceType}</d:resourcetype>
                          {contentLength}
                        </d:prop>
                        <d:status>HTTP/1.1 200 OK</d:status>
                      </d:propstat>
                    </d:response>
                    """;
        }
    }

    private sealed class TimeoutWebDavHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new TaskCanceledException("timeout");
        }
    }

    private sealed class CapturingMetadataEnricher : ILibraryMetadataEnricher
    {
        public int CallCount { get; private set; }

        public TmdbSettings? LastSettings { get; private set; }

        public Task<LibraryMetadataEnrichmentSummary> EnrichMissingMetadataAsync(TmdbSettings? settings = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastSettings = settings;
            return Task.FromResult(new LibraryMetadataEnrichmentSummary());
        }

        public Task<LibraryMetadataEnrichmentSummary> EnrichMissingMovieMetadataAsync(long movieId, TmdbSettings? settings = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastSettings = settings;
            return Task.FromResult(new LibraryMetadataEnrichmentSummary());
        }

        public Task<LibraryMetadataEnrichmentSummary> EnrichMissingTvShowMetadataAsync(long tvShowId, TmdbSettings? settings = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastSettings = settings;
            return Task.FromResult(new LibraryMetadataEnrichmentSummary());
        }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        private readonly AppSettings settings;

        public FakeSettingsService(AppSettings settings)
        {
            this.settings = settings;
        }

        public string SettingsDirectory => Path.GetTempPath();

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(settings);
        }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
