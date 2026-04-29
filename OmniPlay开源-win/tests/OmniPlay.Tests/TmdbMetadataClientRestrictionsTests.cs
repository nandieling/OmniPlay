using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OmniPlay.Core.Settings;
using OmniPlay.Infrastructure.FileSystem;
using OmniPlay.Infrastructure.Tmdb;

namespace OmniPlay.Tests;

public sealed class TmdbMetadataClientRestrictionsTests : IDisposable
{
    private readonly string rootPath;
    private readonly TestStoragePaths storagePaths;

    public TmdbMetadataClientRestrictionsTests()
    {
        rootPath = Path.Combine(
            AppContext.BaseDirectory,
            "test-data",
            nameof(TmdbMetadataClientRestrictionsTests),
            Guid.NewGuid().ToString("N"));
        storagePaths = new TestStoragePaths(rootPath);
    }

    [Fact]
    public async Task SearchMovieCandidatesAsync_BuiltInPublicSourceLimitsFanOutAndResultCount()
    {
        var handler = new CountingTmdbHttpMessageHandler();
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);

        var results = await client.SearchMovieCandidatesAsync(["One", "Two", "Three", "Four", "Five"], null);

        Assert.Equal(new[] { "One", "Two" }, handler.SearchQueries.Distinct().ToArray());
        Assert.Equal(4, handler.SearchRequestCount);
        Assert.Equal([1, 1, 1, 1], handler.SearchPages);
        Assert.Equal(6, results.Count);
    }

    [Fact]
    public async Task SearchMovieCandidatesAsync_CustomApiKeyKeepsFullFanOutAndResultCount()
    {
        var handler = new CountingTmdbHttpMessageHandler();
        using var httpClient = new HttpClient(handler)
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

        var results = await client.SearchMovieCandidatesAsync(["One", "Two", "Three", "Four", "Five"], null);

        Assert.Equal(new[] { "One", "Two", "Three", "Four" }, handler.SearchQueries.Distinct().ToArray());
        Assert.Equal(16, handler.SearchRequestCount);
        Assert.Equal(8, handler.SearchPages.Count(page => page == 1));
        Assert.Equal(8, handler.SearchPages.Count(page => page == 2));
        Assert.Equal(12, results.Count);
    }

    [Fact]
    public async Task SearchMovieCandidatesAsync_UsesConfiguredTmdbLanguage()
    {
        var handler = new CountingTmdbHttpMessageHandler();
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        await settingsService.SaveAsync(new AppSettings
        {
            Tmdb = new TmdbSettings
            {
                EnableBuiltInPublicSource = true,
                CustomApiKey = "custom-key",
                Language = "en-US"
            }
        });
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);

        _ = await client.SearchMovieCandidatesAsync(["One"], null);

        Assert.Equal(["en-US", "en-US", "zh-CN", "zh-CN"], handler.SearchLanguages);
    }

    [Fact]
    public async Task SearchMovieCandidatesAsync_CustomApiKeyRejectedFallsBackToBuiltInPublicSourceLimits()
    {
        var handler = new CustomRejectingTmdbHttpMessageHandler();
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        await settingsService.SaveAsync(new AppSettings
        {
            Tmdb = new TmdbSettings
            {
                EnableBuiltInPublicSource = true,
                CustomApiKey = "bad-custom-key"
            }
        });
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);

        var results = await client.SearchMovieCandidatesAsync(["One", "Two", "Three", "Four", "Five"], null);

        Assert.Equal(1, handler.CustomRejectedRequestCount);
        Assert.Equal(new[] { "One", "Two" }, handler.BuiltInSearchQueries.Distinct().ToArray());
        Assert.Equal(4, handler.BuiltInSearchRequestCount);
        Assert.Equal([1, 1, 1, 1], handler.BuiltInSearchPages);
        Assert.Equal(6, results.Count);
    }

    [Fact]
    public async Task SearchMovieCandidatesAsync_CustomApiKeyRejectedWithoutBuiltInSourceReturnsNoResults()
    {
        var handler = new CustomRejectingTmdbHttpMessageHandler();
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        await settingsService.SaveAsync(new AppSettings
        {
            Tmdb = new TmdbSettings
            {
                EnableBuiltInPublicSource = false,
                CustomApiKey = "bad-custom-key"
            }
        });
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);

        var results = await client.SearchMovieCandidatesAsync(["One", "Two"], null);

        Assert.Empty(results);
        Assert.Equal(1, handler.CustomRejectedRequestCount);
        Assert.Equal(0, handler.BuiltInSearchRequestCount);
    }

    [Fact]
    public async Task DownloadEpisodeStillAsync_BuiltInPublicSourceSkipsEpisodeRequest()
    {
        var handler = new CountingTmdbHttpMessageHandler();
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);

        var result = await client.DownloadEpisodeStillAsync(70523, 1, 1, "tv-file");

        Assert.Null(result);
        Assert.Equal(0, handler.EpisodeDetailRequestCount);
        Assert.Equal(0, handler.ImageRequestCount);
    }

    [Fact]
    public async Task DownloadEpisodeStillAsync_CustomApiKeyCanStillDownloadEpisodeStill()
    {
        var handler = new CountingTmdbHttpMessageHandler();
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        await settingsService.SaveAsync(new AppSettings
        {
            Tmdb = new TmdbSettings
            {
                EnableBuiltInPublicSource = true,
                CustomApiKey = "custom-key",
                Language = "ja-JP"
            }
        });
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);

        var result = await client.DownloadEpisodeStillAsync(70523, 1, 1, "tv-file");

        var filePath = Assert.IsType<string>(result);
        Assert.True(File.Exists(filePath));
        Assert.Equal(1, handler.EpisodeDetailRequestCount);
        Assert.Equal(1, handler.ImageRequestCount);
        Assert.Equal(["ja-JP"], handler.EpisodeDetailLanguages);
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private sealed class CountingTmdbHttpMessageHandler : HttpMessageHandler
    {
        private static readonly byte[] TinyPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4////fwAJ+wP9KobjigAAAABJRU5ErkJggg==");

        public List<string> SearchQueries { get; } = [];

        public List<string> SearchLanguages { get; } = [];

        public List<int> SearchPages { get; } = [];

        public List<string> EpisodeDetailLanguages { get; } = [];

        public int EpisodeDetailRequestCount { get; private set; }

        public int ImageRequestCount { get; private set; }

        public int SearchRequestCount => SearchQueries.Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri?.ToString() ?? string.Empty;

            if (requestUri.Contains("/search/movie", StringComparison.Ordinal))
            {
                var query = ReadQueryValue(request.RequestUri, "query");
                var language = ReadQueryValue(request.RequestUri, "language");
                var page = int.TryParse(ReadQueryValue(request.RequestUri, "page"), out var parsedPage) ? parsedPage : 0;
                SearchQueries.Add(query);
                SearchLanguages.Add(language);
                SearchPages.Add(page);
                return Task.FromResult(CreateJsonResponse(BuildSearchPayload(query)));
            }

            if (requestUri.Contains("/tv/70523/season/1/episode/1", StringComparison.Ordinal))
            {
                EpisodeDetailRequestCount++;
                EpisodeDetailLanguages.Add(ReadQueryValue(request.RequestUri, "language"));
                return Task.FromResult(CreateJsonResponse("""{ "still_path": "/episode-still.jpg" }"""));
            }

            if (requestUri.Contains("image.tmdb.org", StringComparison.Ordinal))
            {
                ImageRequestCount++;
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

        private static string BuildSearchPayload(string query)
        {
            var baseId = query switch
            {
                "One" => 1000,
                "Two" => 2000,
                "Three" => 3000,
                "Four" => 4000,
                _ => 5000
            };

            var payload = new
            {
                results = Enumerable.Range(1, 4).Select(index => new
                {
                    id = baseId + index,
                    title = $"{query} Match {index}",
                    original_title = $"{query} Match {index}",
                    overview = $"{query} overview {index}",
                    poster_path = $"/{baseId + index}.jpg",
                    release_date = $"201{index}-01-01",
                    vote_average = 7.0 + index,
                    popularity = 100 - index
                })
            };

            return JsonSerializer.Serialize(payload);
        }

        private static string ReadQueryValue(Uri? requestUri, string parameterName)
        {
            if (requestUri is null)
            {
                return string.Empty;
            }

            foreach (var pair in requestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var segments = pair.Split('=', 2);
                if (segments.Length == 2 && string.Equals(segments[0], parameterName, StringComparison.Ordinal))
                {
                    return Uri.UnescapeDataString(segments[1]);
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

    private sealed class CustomRejectingTmdbHttpMessageHandler : HttpMessageHandler
    {
        public List<string> BuiltInSearchQueries { get; } = [];

        public List<int> BuiltInSearchPages { get; } = [];

        public int CustomRejectedRequestCount { get; private set; }

        public int BuiltInSearchRequestCount => BuiltInSearchQueries.Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri?.ToString() ?? string.Empty;
            var apiKey = ReadQueryValue(request.RequestUri, "api_key");
            if (string.Equals(apiKey, "bad-custom-key", StringComparison.Ordinal))
            {
                CustomRejectedRequestCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            if (requestUri.Contains("/search/movie", StringComparison.Ordinal))
            {
                var query = ReadQueryValue(request.RequestUri, "query");
                var page = int.TryParse(ReadQueryValue(request.RequestUri, "page"), out var parsedPage) ? parsedPage : 0;
                BuiltInSearchQueries.Add(query);
                BuiltInSearchPages.Add(page);
                return Task.FromResult(CreateJsonResponse(BuildSearchPayload(query)));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(requestUri, Encoding.UTF8, "text/plain")
            });
        }

        private static string BuildSearchPayload(string query)
        {
            var baseId = query switch
            {
                "One" => 1000,
                "Two" => 2000,
                "Three" => 3000,
                "Four" => 4000,
                _ => 5000
            };

            var payload = new
            {
                results = Enumerable.Range(1, 4).Select(index => new
                {
                    id = baseId + index,
                    title = $"{query} Match {index}",
                    original_title = $"{query} Match {index}",
                    overview = $"{query} overview {index}",
                    poster_path = $"/{baseId + index}.jpg",
                    release_date = $"201{index}-01-01",
                    vote_average = 7.0 + index,
                    popularity = 100 - index
                })
            };

            return JsonSerializer.Serialize(payload);
        }

        private static string ReadQueryValue(Uri? requestUri, string parameterName)
        {
            if (requestUri is null)
            {
                return string.Empty;
            }

            foreach (var pair in requestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var segments = pair.Split('=', 2);
                if (segments.Length == 2 && string.Equals(segments[0], parameterName, StringComparison.Ordinal))
                {
                    return Uri.UnescapeDataString(segments[1]);
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
