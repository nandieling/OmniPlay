using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OmniPlay.Core.Settings;
using OmniPlay.Infrastructure.FileSystem;
using OmniPlay.Infrastructure.Tmdb;

namespace OmniPlay.Tests;

public sealed class TmdbMetadataClientScoringTests : IDisposable
{
    private readonly string rootPath;
    private readonly TestStoragePaths storagePaths;

    public TmdbMetadataClientScoringTests()
    {
        rootPath = Path.Combine(
            AppContext.BaseDirectory,
            "test-data",
            nameof(TmdbMetadataClientScoringTests),
            Guid.NewGuid().ToString("N"));
        storagePaths = new TestStoragePaths(rootPath);
    }

    [Fact]
    public async Task SearchMovieAsync_PrefersExactTitleOverPopularSequel()
    {
        using var httpClient = new HttpClient(new DelegateHttpMessageHandler(request =>
        {
            if ((request.RequestUri?.ToString() ?? string.Empty).Contains("/search/movie", StringComparison.Ordinal))
            {
                return CreateJsonResponse(new
                {
                    results = new object[]
                    {
                        new
                        {
                            id = 9001,
                            title = "Inception 2",
                            original_title = "Inception 2",
                            release_date = "2010-07-16",
                            popularity = 999d,
                            vote_average = 7.1
                        },
                        new
                        {
                            id = 27205,
                            title = "Inception",
                            original_title = "Inception",
                            release_date = "2010-07-16",
                            popularity = 90d,
                            vote_average = 8.4
                        }
                    }
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        var settingsService = await CreateSettingsServiceAsync();
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);

        var result = await client.SearchMovieAsync(["Inception"], "2010");

        var match = Assert.IsType<TmdbMetadataMatch>(result);
        Assert.Equal(27205, match.Id);
        Assert.Equal("Inception", match.Title);
    }

    [Fact]
    public async Task SearchTvShowAsync_PreferredSeasonPrefersMatchingSeasonYear()
    {
        using var httpClient = new HttpClient(new DelegateHttpMessageHandler(request =>
        {
            var requestUri = request.RequestUri?.ToString() ?? string.Empty;
            if (requestUri.Contains("/search/tv", StringComparison.Ordinal))
            {
                return CreateJsonResponse(new
                {
                    results = new object[]
                    {
                        new
                        {
                            id = 101,
                            name = "Arcane",
                            original_name = "Arcane",
                            first_air_date = "2021-11-06",
                            popularity = 600d,
                            vote_average = 8.9
                        },
                        new
                        {
                            id = 202,
                            name = "Arcane",
                            original_name = "Arcane",
                            first_air_date = "2021-11-06",
                            popularity = 450d,
                            vote_average = 9.0
                        }
                    }
                });
            }

            if (requestUri.Contains("/tv/101/season/1", StringComparison.Ordinal))
            {
                return CreateJsonResponse(new { air_date = "2021-11-06" });
            }

            if (requestUri.Contains("/tv/202/season/2", StringComparison.Ordinal))
            {
                return CreateJsonResponse(new { air_date = "2024-11-09" });
            }

            if (requestUri.Contains("/tv/101?", StringComparison.Ordinal))
            {
                return CreateJsonResponse(new { number_of_seasons = 1 });
            }

            if (requestUri.Contains("/tv/202?", StringComparison.Ordinal))
            {
                return CreateJsonResponse(new { number_of_seasons = 2 });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        var settingsService = await CreateSettingsServiceAsync();
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);

        var result = await client.SearchTvShowAsync(
            ["Arcane"],
            "2024",
            CancellationToken.None,
            new TmdbSearchOptions
            {
                PreferredSeason = 2
            });

        var match = Assert.IsType<TmdbMetadataMatch>(result);
        Assert.Equal(202, match.Id);
    }

    [Fact]
    public async Task SearchMovieCandidatesAsync_MergesSearchLanguagesAndKeepsLocalizedTitle()
    {
        using var httpClient = new HttpClient(new DelegateHttpMessageHandler(request =>
        {
            var requestUri = request.RequestUri?.ToString() ?? string.Empty;
            if (requestUri.Contains("/search/movie", StringComparison.Ordinal))
            {
                var language = ReadQueryValue(request.RequestUri, "language");
                return CreateJsonResponse(new
                {
                    results = new object[]
                    {
                        string.Equals(language, "zh-CN", StringComparison.OrdinalIgnoreCase)
                            ? new
                            {
                                id = 27205,
                                title = "盗梦空间",
                                original_title = "Inception",
                                release_date = "2010-07-16",
                                popularity = 90d,
                                vote_average = 8.4
                            }
                            : new
                            {
                                id = 27205,
                                title = "Inception",
                                original_title = "Inception",
                                release_date = "2010-07-16",
                                popularity = 90d,
                                vote_average = 8.4
                            }
                    }
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        var settingsService = await CreateSettingsServiceAsync(language: "zh-CN");
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);

        var result = await client.SearchMovieCandidatesAsync(["Inception"], "2010");

        var match = Assert.Single(result);
        Assert.Equal("盗梦空间", match.Title);
    }

    [Fact]
    public async Task SearchMovieCandidatesAsync_UsesTranslationsFallbackForChineseTitle()
    {
        using var httpClient = new HttpClient(new DelegateHttpMessageHandler(request =>
        {
            var requestUri = request.RequestUri?.ToString() ?? string.Empty;
            if (requestUri.Contains("/search/movie", StringComparison.Ordinal))
            {
                return CreateJsonResponse(new
                {
                    results = new object[]
                    {
                        new
                        {
                            id = 500,
                            title = "The Ministry of Ungentlemanly Warfare",
                            original_title = "The Ministry of Ungentlemanly Warfare",
                            release_date = "2024-04-19",
                            popularity = 120d,
                            vote_average = 7.3
                        }
                    }
                });
            }

            if (requestUri.Contains("/movie/500/translations", StringComparison.Ordinal))
            {
                return CreateJsonResponse(
                    """
                    {
                      "translations": [
                        {
                          "iso_639_1": "zh",
                          "iso_3166_1": "CN",
                          "data": {
                            "title": "非绅士特攻队"
                          }
                        }
                      ]
                    }
                    """);
            }

            if (requestUri.Contains("/movie/500?", StringComparison.Ordinal))
            {
                return CreateJsonResponse(new
                {
                    id = 500,
                    title = "The Ministry of Ungentlemanly Warfare",
                    original_title = "The Ministry of Ungentlemanly Warfare",
                    release_date = "2024-04-19",
                    popularity = 120d,
                    vote_average = 7.3
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        var settingsService = await CreateSettingsServiceAsync(language: "zh-CN");
        var client = new TmdbMetadataClient(httpClient, storagePaths, settingsService);

        var result = await client.SearchMovieCandidatesAsync(["The Ministry of Ungentlemanly Warfare"], "2024");

        var match = Assert.Single(result);
        Assert.Equal("非绅士特攻队", match.Title);
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private async Task<JsonSettingsService> CreateSettingsServiceAsync(string language = "en-US")
    {
        var settingsService = new JsonSettingsService(storagePaths);
        await settingsService.SaveAsync(new AppSettings
        {
            Tmdb = new TmdbSettings
            {
                EnableBuiltInPublicSource = true,
                CustomApiKey = "custom-key",
                Language = language
            }
        });
        return settingsService;
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }

    private static HttpResponseMessage CreateJsonResponse(object payload)
    {
        var json = payload as string ?? JsonSerializer.Serialize(payload);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
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
}
