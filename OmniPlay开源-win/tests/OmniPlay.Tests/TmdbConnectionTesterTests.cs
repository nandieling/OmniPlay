using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using OmniPlay.Core.Settings;
using OmniPlay.Infrastructure.FileSystem;
using OmniPlay.Infrastructure.Tmdb;

namespace OmniPlay.Tests;

public sealed class TmdbConnectionTesterTests : IDisposable
{
    private readonly string rootPath;
    private readonly TestStoragePaths storagePaths;

    public TmdbConnectionTesterTests()
    {
        rootPath = Path.Combine(
            AppContext.BaseDirectory,
            "test-data",
            nameof(TmdbConnectionTesterTests),
            Guid.NewGuid().ToString("N"));
        storagePaths = new TestStoragePaths(rootPath);
    }

    [Fact]
    public async Task TestConnectionAsync_ReturnsSuccessForValidCustomApiKey()
    {
        var handler = new FakeConnectionHttpMessageHandler();
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        var tester = new TmdbMetadataClient(httpClient, storagePaths, settingsService);

        var result = await tester.TestConnectionAsync(new TmdbSettings
        {
            EnableBuiltInPublicSource = false,
            CustomApiKey = "custom-key"
        });

        Assert.True(result.Success);
        Assert.Contains("HTTP 200", result.Message, StringComparison.Ordinal);
        Assert.Contains("api_key=custom-key", handler.LastRequestUri, StringComparison.Ordinal);
        Assert.Contains("自定义 API Key", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnectionAsync_IdentifiesBuiltInPublicSourceAsRestricted()
    {
        using var httpClient = new HttpClient(new FakeConnectionHttpMessageHandler())
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        var tester = new TmdbMetadataClient(httpClient, storagePaths, settingsService);

        var result = await tester.TestConnectionAsync(new TmdbSettings
        {
            EnableBuiltInPublicSource = true,
            CustomApiKey = string.Empty
        });

        Assert.True(result.Success);
        Assert.Contains("内置公共受限源", result.Message, StringComparison.Ordinal);
        Assert.Contains("轻量刮削", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnectionAsync_UsesCustomAccessTokenAsBearerCredential()
    {
        var handler = new FakeConnectionHttpMessageHandler();
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        var tester = new TmdbMetadataClient(httpClient, storagePaths, settingsService);

        var result = await tester.TestConnectionAsync(new TmdbSettings
        {
            EnableBuiltInPublicSource = false,
            CustomApiKey = "custom-key",
            CustomAccessToken = "custom-token"
        });

        Assert.True(result.Success);
        Assert.Contains("自定义 Access Token", result.Message, StringComparison.Ordinal);
        Assert.Equal("Bearer custom-token", handler.LastAuthorizationHeader);
        Assert.DoesNotContain("api_key=", handler.LastRequestUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnectionAsync_ReturnsFriendlyMessageWhenNoCredentialIsConfigured()
    {
        using var httpClient = new HttpClient(new FakeConnectionHttpMessageHandler())
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        var tester = new TmdbMetadataClient(httpClient, storagePaths, settingsService);

        var result = await tester.TestConnectionAsync(new TmdbSettings
        {
            EnableBuiltInPublicSource = false,
            CustomApiKey = string.Empty
        });

        Assert.False(result.Success);
        Assert.Contains("未启用内置公共 TMDB 源", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnectionAsync_IncludesInnerSslFailureMessage()
    {
        using var httpClient = new HttpClient(new ThrowingConnectionHttpMessageHandler())
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        var tester = new TmdbMetadataClient(httpClient, storagePaths, settingsService);

        var result = await tester.TestConnectionAsync(new TmdbSettings
        {
            EnableBuiltInPublicSource = false,
            CustomApiKey = "custom-key"
        });

        Assert.False(result.Success);
        Assert.Contains("The SSL connection could not be established", result.Message, StringComparison.Ordinal);
        Assert.Contains("remote certificate is invalid", result.Message, StringComparison.Ordinal);
        Assert.Contains("TLS/证书握手失败", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnectionAsync_FallsBackToBuiltInSourceWhenCustomApiKeyIsRejected()
    {
        var handler = new FallbackConnectionHttpMessageHandler();
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var settingsService = new JsonSettingsService(storagePaths);
        var tester = new TmdbMetadataClient(httpClient, storagePaths, settingsService);

        var result = await tester.TestConnectionAsync(new TmdbSettings
        {
            EnableBuiltInPublicSource = true,
            CustomApiKey = "bad-custom-key"
        });

        Assert.True(result.Success);
        Assert.Contains("自定义 API Key 不可用", result.Message, StringComparison.Ordinal);
        Assert.Contains("已切换内置公共受限源", result.Message, StringComparison.Ordinal);
        Assert.Equal(["bad-custom-key", "built-in"], handler.ApiKeyAttempts);
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private sealed class FakeConnectionHttpMessageHandler : HttpMessageHandler
    {
        public string LastRequestUri { get; private set; } = string.Empty;

        public string? LastAuthorizationHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri?.ToString() ?? string.Empty;
            LastRequestUri = requestUri;
            LastAuthorizationHeader = request.Headers.Authorization?.ToString();
            if (requestUri.Contains("/configuration", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{ "images": { "base_url": "https://image.tmdb.org/t/p/" } }""", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class ThrowingConnectionHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException(
                "The SSL connection could not be established, see inner exception.",
                new AuthenticationException("The remote certificate is invalid because of errors in the certificate chain."));
        }
    }

    private sealed class FallbackConnectionHttpMessageHandler : HttpMessageHandler
    {
        public List<string> ApiKeyAttempts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var apiKey = ReadQueryValue(request.RequestUri, "api_key");
            ApiKeyAttempts.Add(string.Equals(apiKey, "bad-custom-key", StringComparison.Ordinal)
                ? "bad-custom-key"
                : "built-in");

            if (string.Equals(apiKey, "bad-custom-key", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "images": { "base_url": "https://image.tmdb.org/t/p/" } }""", Encoding.UTF8, "application/json")
            });
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
}
