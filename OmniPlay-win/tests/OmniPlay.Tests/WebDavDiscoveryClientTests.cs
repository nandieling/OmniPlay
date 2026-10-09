using System.Net;
using System.Net.Http;
using System.Text;
using OmniPlay.Core.Models;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Infrastructure.Library;

namespace OmniPlay.Tests;

public sealed class WebDavDiscoveryClientTests
{
    [Fact]
    public async Task EnumerateFilesAsync_SendsBasicAuthAndFlattensNestedDirectories()
    {
        var handler = new RecordingWebDavHttpMessageHandler();
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var client = new WebDavDiscoveryClient(httpClient);

        var files = await client.EnumerateFilesAsync(new MediaSource
        {
            Name = "Remote",
            ProtocolType = "webdav",
            BaseUrl = "https://demo.example/library",
            AuthConfig = MediaSourceAuthConfigSerializer.SerializeWebDav(new WebDavAuthConfig("demo", "secret"))
        });

        Assert.Equal("Basic ZGVtbzpzZWNyZXQ=", handler.LastAuthorizationHeader);
        Assert.Equal(["movies/Inception.2010.mkv"], files.Select(static file => file.RelativePath).ToList());
    }

    [Fact]
    public async Task TestConnectionAsync_ReturnsFriendlyMessageWhenAuthenticationFails()
    {
        using var httpClient = new HttpClient(new UnauthorizedWebDavHttpMessageHandler())
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var client = new WebDavDiscoveryClient(httpClient);

        var result = await client.TestConnectionAsync(new MediaSource
        {
            Name = "Remote",
            ProtocolType = "webdav",
            BaseUrl = "https://demo.example/library",
            AuthConfig = MediaSourceAuthConfigSerializer.SerializeWebDav(new WebDavAuthConfig("demo", "wrong"))
        });

        Assert.False(result.Success);
        Assert.Contains("认证被拒绝", result.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingWebDavHttpMessageHandler : HttpMessageHandler
    {
        public string? LastAuthorizationHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorizationHeader = request.Headers.Authorization?.ToString();

            if (!string.Equals(request.Method.Method, "PROPFIND", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
            }

            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var content = path switch
            {
                "/library/" => BuildDirectoryResponse(
                    "/library/",
                    ("/library/movies/", true, 0L)),
                "/library/movies/" => BuildDirectoryResponse(
                    "/library/movies/",
                    ("/library/movies/Inception.2010.mkv", false, 128L)),
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

    private sealed class UnauthorizedWebDavHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }

    [Fact]
    public async Task TestConnectionAsync_RetriesServerErrorsUntilSuccess()
    {
        var handler = new SequenceWebDavHttpMessageHandler(
            [HttpStatusCode.ServiceUnavailable, (HttpStatusCode)207, (HttpStatusCode)207]);
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var client = new WebDavDiscoveryClient(httpClient);

        var result = await client.TestConnectionAsync(CreateWebDavSource());

        Assert.True(result.Success);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task EnumerateFilesAsync_RetriesNetworkErrorsUntilSuccess()
    {
        var handler = new SequenceWebDavHttpMessageHandler(
            [(HttpStatusCode)207],
            preSuccessThrow: new HttpRequestException("Connection reset by peer"));
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var client = new WebDavDiscoveryClient(httpClient);

        var files = await client.EnumerateFilesAsync(CreateWebDavSource());

        // 1 network failure on the root directory, then root and movies/ PROPFINDs succeed.
        Assert.Equal(3, handler.CallCount);
        Assert.Equal(["movies/Inception.2010.mkv"], files.Select(static file => file.RelativePath).ToList());
    }

    [Fact]
    public async Task TestConnectionAsync_DoesNotRetryAuthenticationFailure()
    {
        var handler = new SequenceWebDavHttpMessageHandler([HttpStatusCode.Unauthorized]);
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var client = new WebDavDiscoveryClient(httpClient);

        var result = await client.TestConnectionAsync(CreateWebDavSource());

        Assert.False(result.Success);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains("认证被拒绝", result.Message, StringComparison.Ordinal);
    }

    private static MediaSource CreateWebDavSource()
    {
        return new MediaSource
        {
            Name = "Remote",
            ProtocolType = "webdav",
            BaseUrl = "https://demo.example/library",
            AuthConfig = MediaSourceAuthConfigSerializer.SerializeWebDav(new WebDavAuthConfig("demo", "secret"))
        };
    }

    private sealed class SequenceWebDavHttpMessageHandler(
        IReadOnlyList<HttpStatusCode> statuses,
        Exception? preSuccessThrow = null) : HttpMessageHandler
    {
        private readonly IReadOnlyList<HttpStatusCode> statuses = statuses;
        private readonly Exception? preSuccessThrow = preSuccessThrow;
        private int callCount;

        public int CallCount => callCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            callCount++;
            if (preSuccessThrow is not null && callCount == 1)
            {
                throw preSuccessThrow;
            }

            var status = statuses[Math.Min(callCount - 1, statuses.Count - 1)];
            if (status == (HttpStatusCode)207)
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                var children = path switch
                {
                    "/library/" => """
                    <d:response>
                      <d:href>/library/movies/</d:href>
                      <d:propstat>
                        <d:prop>
                          <d:resourcetype><d:collection /></d:resourcetype>
                        </d:prop>
                        <d:status>HTTP/1.1 200 OK</d:status>
                      </d:propstat>
                    </d:response>
                    """,
                    "/library/movies/" => """
                    <d:response>
                      <d:href>/library/movies/Inception.2010.mkv</d:href>
                      <d:propstat>
                        <d:prop>
                          <d:resourcetype /><d:getcontentlength>128</d:getcontentlength>
                        </d:prop>
                        <d:status>HTTP/1.1 200 OK</d:status>
                      </d:propstat>
                    </d:response>
                    """,
                    _ => string.Empty
                };

                var content = """<?xml version="1.0" encoding="utf-8"?><d:multistatus xmlns:d="DAV:">""" +
                    $"""
                    <d:response>
                      <d:href>{path}</d:href>
                      <d:propstat>
                        <d:prop>
                          <d:resourcetype><d:collection /></d:resourcetype>
                        </d:prop>
                        <d:status>HTTP/1.1 200 OK</d:status>
                      </d:propstat>
                    </d:response>
                    """ + children + "</d:multistatus>";
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)207)
                {
                    Content = new StringContent(content, Encoding.UTF8, "application/xml")
                });
            }

            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
