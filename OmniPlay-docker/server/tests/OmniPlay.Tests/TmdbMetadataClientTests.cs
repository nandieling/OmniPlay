using System.Net;
using System.Text;
using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.FileSystem;
using OmniPlay.Infrastructure.Tmdb;
using Xunit;

namespace OmniPlay.Tests;

public sealed class TmdbMetadataClientTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omniplay-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SearchCandidatesUsesChineseTranslationWhenLocalizedSearchReturnsEnglishTitle()
    {
        var client = CreateClient(request =>
        {
            var uri = request.RequestUri!;
            var query = ParseQuery(uri);
            if (uri.AbsolutePath == "/3/search/movie" && query.GetValueOrDefault("page") == "1")
            {
                return Json("""
                    {
                      "results": [
                        {
                          "id": 27205,
                          "title": "Inception",
                          "original_title": "Inception",
                          "overview": "A thief enters dreams.",
                          "release_date": "2010-07-15",
                          "poster_path": "/inception.jpg",
                          "vote_average": 8.4,
                          "popularity": 90
                        }
                      ]
                    }
                    """);
            }

            if (uri.AbsolutePath == "/3/search/movie")
            {
                return Json("""{ "results": [] }""");
            }

            if (uri.AbsolutePath == "/3/movie/27205")
            {
                return Json("""
                    {
                      "id": 27205,
                      "title": "Inception",
                      "original_title": "Inception",
                      "overview": "A thief enters dreams.",
                      "release_date": "2010-07-15",
                      "poster_path": "/inception.jpg",
                      "vote_average": 8.4,
                      "popularity": 90
                    }
                    """);
            }

            if (uri.AbsolutePath == "/3/movie/27205/translations")
            {
                return Json("""
                    {
                      "translations": [
                        { "iso_639_1": "zh", "iso_3166_1": "HK", "data": { "title": "潛行凶間" } },
                        { "iso_639_1": "zh", "iso_3166_1": "CN", "data": { "title": "盗梦空间" } }
                      ]
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var matches = await client.SearchCandidatesAsync("movie", "Inception", "2010", TestSettings());

        var match = Assert.Single(matches);
        Assert.Equal("盗梦空间", match.Title);
        Assert.Equal(27205, match.Id);
    }

    [Fact]
    public async Task TestConnectionUsesCustomApiKey()
    {
        string? firstApiKey = null;
        var client = CreateClient(request =>
        {
            firstApiKey ??= ParseQuery(request.RequestUri!).GetValueOrDefault("api_key");
            return Json("""{ "images": {} }""");
        });

        var result = await client.TestConnectionAsync(new TmdbSettings(CustomApiKey: "custom-key"));

        Assert.True(result.IsReachable);
        Assert.Equal("custom-key", firstApiKey);
        Assert.Equal("自定义 API Key", result.Source);
    }

    [Fact]
    public async Task GetDetailsUsesCommonChineseTranslationWhenDetailTitleIsEnglish()
    {
        var client = CreateClient(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/3/tv/1399")
            {
                return Json("""
                    {
                      "id": 1399,
                      "name": "Game of Thrones",
                      "original_name": "Game of Thrones",
                      "overview": "Nine noble families fight for control.",
                      "first_air_date": "2011-04-17",
                      "poster_path": "/got.jpg",
                      "vote_average": 8.5,
                      "popularity": 300
                    }
                    """);
            }

            if (uri.AbsolutePath == "/3/tv/1399/translations")
            {
                return Json("""
                    {
                      "translations": [
                        { "iso_639_1": "zh", "iso_3166_1": "TW", "data": { "name": "冰與火之歌：權力遊戲" } },
                        { "iso_639_1": "zh", "iso_3166_1": "CN", "data": { "name": "权力的游戏" } }
                      ]
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var match = await client.GetDetailsAsync("tv", 1399, TestSettings());

        Assert.NotNull(match);
        Assert.Equal("权力的游戏", match.Title);
        Assert.Equal("tv", match.MediaType);
    }

    [Fact]
    public async Task SearchCandidatesDoesNotFilterTvByFirstAirYear()
    {
        var client = CreateClient(request =>
        {
            var uri = request.RequestUri!;
            var query = ParseQuery(uri);
            if (uri.AbsolutePath == "/3/search/tv" && query.ContainsKey("first_air_date_year"))
            {
                return Json("""{ "results": [] }""");
            }

            if (uri.AbsolutePath == "/3/search/tv" && query.GetValueOrDefault("language") == "zh-CN" && query.GetValueOrDefault("page") == "1")
            {
                return Json("""
                    {
                      "results": [
                        {
                          "id": 106379,
                          "name": "辐射",
                          "original_name": "Fallout",
                          "overview": "末日废土故事。",
                          "first_air_date": "2024-04-10",
                          "poster_path": "/fallout.jpg",
                          "vote_average": 8.2,
                          "popularity": 120
                        }
                      ]
                    }
                    """);
            }

            if (uri.AbsolutePath == "/3/search/tv")
            {
                return Json("""{ "results": [] }""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var matches = await client.SearchCandidatesAsync("tv", "辐射", "2025", TestSettings());

        var match = Assert.Single(matches);
        Assert.Equal("辐射", match.Title);
        Assert.Equal("2024-04-10", match.ReleaseDate);
    }

    [Fact]
    public async Task SearchCandidatesPrefersOriginalMovieOverVariantByYear()
    {
        var client = CreateClient(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/3/search/movie")
            {
                return Json("""
                    {
                      "results": [
                        {
                          "id": 100,
                          "title": "偷自行车的人：Pumped Up",
                          "original_title": "Bicycle Thieves: Pumped Up",
                          "release_date": "2024-01-01",
                          "poster_path": "/variant.jpg",
                          "vote_average": 8.0,
                          "popularity": 200
                        },
                        {
                          "id": 101,
                          "title": "偷自行车的人",
                          "original_title": "Bicycle Thieves",
                          "release_date": "1948-07-21",
                          "poster_path": "/classic.jpg",
                          "vote_average": 8.3,
                          "popularity": 60
                        }
                      ]
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var matches = await client.SearchCandidatesAsync("movie", "Bicycle Thieves", "1948", TestSettings(), limit: 2);

        Assert.Equal(101, matches[0].Id);
        Assert.Equal("偷自行车的人", matches[0].Title);
    }

    [Fact]
    public async Task SearchCandidatesPrefersOriginalConcertOverTaylorVersion()
    {
        var client = CreateClient(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/3/search/movie")
            {
                return Json("""
                    {
                      "results": [
                        {
                          "id": 200,
                          "title": "泰勒·斯威夫特：时代巡回演唱会（泰勒版）",
                          "original_title": "Taylor Swift: The Eras Tour (Taylor's Version)",
                          "release_date": "2024-03-14",
                          "poster_path": "/taylors-version.jpg",
                          "vote_average": 8.4,
                          "popularity": 250
                        },
                        {
                          "id": 201,
                          "title": "泰勒·斯威夫特：时代巡回演唱会",
                          "original_title": "Taylor Swift: The Eras Tour",
                          "release_date": "2023-10-13",
                          "poster_path": "/eras.jpg",
                          "vote_average": 8.2,
                          "popularity": 120
                        }
                      ]
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var matches = await client.SearchCandidatesAsync("movie", "Taylor Swift the Eras Tour", "2023", TestSettings(), limit: 2);

        Assert.Equal(201, matches[0].Id);
    }

    [Fact]
    public async Task SearchCandidatesPrefersOriginalTvSeasonYearOverNewVariant()
    {
        var client = CreateClient(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/3/search/tv")
            {
                return Json("""
                    {
                      "results": [
                        {
                          "id": 300,
                          "name": "物语系列 外传季&怪物季",
                          "original_name": "Monogatari Series: Off & Monster Season",
                          "first_air_date": "2024-07-06",
                          "poster_path": "/off-monster.jpg",
                          "vote_average": 8.4,
                          "popularity": 180
                        },
                        {
                          "id": 301,
                          "name": "化物语",
                          "original_name": "Monogatari",
                          "first_air_date": "2009-07-03",
                          "poster_path": "/bakemonogatari.jpg",
                          "vote_average": 8.1,
                          "popularity": 70
                        }
                      ]
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var matches = await client.SearchCandidatesAsync("tv", "Monogatari Series", "2009", TestSettings(), limit: 2);

        Assert.Equal(301, matches[0].Id);
    }

    [Fact]
    public async Task SearchCandidatesConvertsTraditionalChineseTitleToSimplified()
    {
        var client = CreateClient(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/3/search/movie")
            {
                return Json("""
                    {
                      "results": [
                        {
                          "id": 496243,
                          "title": "寄生蟲",
                          "original_title": "Gisaengchung",
                          "release_date": "2019-05-30",
                          "poster_path": "/parasite.jpg",
                          "vote_average": 8.5,
                          "popularity": 120
                        }
                      ]
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var matches = await client.SearchCandidatesAsync("movie", "Parasite", "2019", TestSettings());

        var match = Assert.Single(matches);
        Assert.Equal("寄生虫", match.Title);
    }

    [Fact]
    public async Task SearchCandidatesUsesKnownChineseTitleForParasiteWhenTmdbReturnsEnglish()
    {
        var client = CreateClient(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/3/search/movie")
            {
                return Json("""
                    {
                      "results": [
                        {
                          "id": 496243,
                          "title": "Parasite",
                          "original_title": "Gisaengchung",
                          "release_date": "2019-05-30",
                          "poster_path": "/parasite.jpg",
                          "vote_average": 8.5,
                          "popularity": 120
                        }
                      ]
                    }
                    """);
            }

            if (uri.AbsolutePath == "/3/movie/496243")
            {
                return Json("""
                    {
                      "id": 496243,
                      "title": "Parasite",
                      "original_title": "Gisaengchung",
                      "release_date": "2019-05-30",
                      "poster_path": "/parasite.jpg",
                      "vote_average": 8.5,
                      "popularity": 120
                    }
                    """);
            }

            if (uri.AbsolutePath == "/3/movie/496243/translations")
            {
                return Json("""{ "translations": [] }""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var matches = await client.SearchCandidatesAsync("movie", "Parasite", "2019", TestSettings());

        var match = Assert.Single(matches);
        Assert.Equal("寄生虫", match.Title);
    }

    [Fact]
    public async Task SearchCandidatesPrefersYearMatchedParasiteOverUndatedExactTitle()
    {
        var client = CreateClient(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/3/search/movie")
            {
                return Json("""
                    {
                      "results": [
                        {
                          "id": 10,
                          "title": "Parasite",
                          "original_title": "Parasite",
                          "overview": "Unrelated title.",
                          "poster_path": "/wrong.jpg",
                          "vote_average": 10.0,
                          "popularity": 20
                        },
                        {
                          "id": 496243,
                          "title": "寄生虫",
                          "original_title": "기생충",
                          "overview": "Bong Joon-ho film.",
                          "release_date": "2019-05-30",
                          "poster_path": "/parasite.jpg",
                          "vote_average": 8.5,
                          "popularity": 120
                        }
                      ]
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var matches = await client.SearchCandidatesAsync("movie", "Parasite", "2019", TestSettings(), limit: 2);

        Assert.Equal(496243, matches[0].Id);
        Assert.Equal("寄生虫", matches[0].Title);
    }

    [Fact]
    public async Task SearchCandidatesUsesKnownAliasForTheGlory()
    {
        var client = CreateClient(request =>
        {
            var uri = request.RequestUri!;
            var query = ParseQuery(uri);
            if (uri.AbsolutePath == "/3/search/tv" && query.GetValueOrDefault("query") == "黑暗荣耀")
            {
                return Json("""
                    {
                      "results": [
                        {
                          "id": 136283,
                          "name": "黑暗荣耀",
                          "original_name": "The Glory",
                          "overview": "复仇故事。",
                          "first_air_date": "2022-12-30",
                          "poster_path": "/glory.jpg",
                          "vote_average": 8.5,
                          "popularity": 100
                        }
                      ]
                    }
                    """);
            }

            if (uri.AbsolutePath == "/3/search/tv")
            {
                return Json("""{ "results": [] }""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var matches = await client.SearchCandidatesAsync("tv", "The Glory", null, TestSettings());

        var match = Assert.Single(matches);
        Assert.Equal(136283, match.Id);
        Assert.Equal("黑暗荣耀", match.Title);
    }

    [Fact]
    public async Task SearchCandidatesUsesKnownAliasForHayate()
    {
        var client = CreateClient(request =>
        {
            var uri = request.RequestUri!;
            var query = ParseQuery(uri);
            if (uri.AbsolutePath == "/3/search/tv" && query.GetValueOrDefault("query") == "旋风管家")
            {
                return Json("""
                    {
                      "results": [
                        {
                          "id": 46078,
                          "name": "旋风管家",
                          "original_name": "Hayate no Gotoku!",
                          "overview": "管家喜剧。",
                          "first_air_date": "2007-04-01",
                          "poster_path": "/hayate.jpg",
                          "vote_average": 7.5,
                          "popularity": 50
                        }
                      ]
                    }
                    """);
            }

            if (uri.AbsolutePath == "/3/search/tv")
            {
                return Json("""{ "results": [] }""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var matches = await client.SearchCandidatesAsync("tv", "Hayate no Gotoku Cuties", null, TestSettings());

        var match = Assert.Single(matches);
        Assert.Equal(46078, match.Id);
        Assert.Equal("旋风管家", match.Title);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private TmdbMetadataClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> handle)
    {
        return new TmdbMetadataClient(
            new HttpClient(new StubHttpMessageHandler(handle)),
            new StoragePaths(Path.Combine(root, "app")));
    }

    private static TmdbSettings TestSettings()
    {
        return new TmdbSettings(CustomApiKey: "test-key");
    }

    private static HttpResponseMessage Json(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private static Dictionary<string, string> ParseQuery(Uri uri)
    {
        return uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => Uri.UnescapeDataString(parts[1]),
                StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> handle;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handle)
        {
            this.handle = handle;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handle(request));
        }
    }
}
