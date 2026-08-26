using System.Net;
using System.Net.Http.Headers;
using OmniPlay.Core.Settings;
using OmniPlay.Infrastructure.Library;

namespace OmniPlay.Tests;

public sealed class LuckyStunClientTests
{
    [Fact]
    public async Task FetchAsync_LogsInReadsNestedRulesAndUsesSelectedRule()
    {
        var handler = new LuckyHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var client = new LuckyStunClient(httpClient);

        var snapshot = await client.FetchAsync(new LuckyStunSettings
        {
            ManagementUrl = "192.168.1.2:16601/api",
            Username = "admin",
            Password = "secret",
            SelectedRuleId = "rule-2"
        });

        Assert.NotNull(snapshot.SelectedRule);
        var selected = snapshot.SelectedRule!;
        Assert.Equal("rule-2", selected.Id);
        Assert.Equal("手机远程", selected.Name);
        Assert.Equal("http://remote.example.com:2443", selected.Address);
        Assert.Equal(2, snapshot.Rules.Count);
        Assert.Equal("/api/login", handler.Requests[0].Path);
        Assert.Equal("/api/stun", handler.Requests[1].Path);
        Assert.Equal("/api/stun/rules", handler.Requests[2].Path);
        Assert.Equal("Bearer lucky-token", handler.Requests[2].Authorization);
        Assert.Equal("lucky_session=abc", handler.Requests[2].Cookie);
    }

    [Fact]
    public void NormalizeAddress_AddsHttpAndRemovesTrailingSlash()
    {
        Assert.Equal("http://example.com:16601", LuckyStunClient.NormalizeAddress("example.com:16601/"));
        Assert.Equal("https://example.com:16601", LuckyStunClient.NormalizeAddress("https://example.com:16601/"));
    }

    private sealed class LuckyHttpMessageHandler : HttpMessageHandler
    {
        public List<RequestSnapshot> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var cookie = request.Headers.TryGetValues("Cookie", out var cookies)
                ? string.Join("; ", cookies)
                : string.Empty;
            Requests.Add(new RequestSnapshot(
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString() ?? string.Empty,
                cookie));

            return Task.FromResult(request.RequestUri.AbsolutePath switch
            {
                "/api/login" => CreateJsonResponse(
                    "{\"data\":{\"accessToken\":\"lucky-token\"}}",
                    response => response.Headers.TryAddWithoutValidation("Set-Cookie", "lucky_session=abc; Path=/")),
                "/api/stun" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/stun/rules" => CreateJsonResponse(
                    "{\"data\":{\"rules\":[{\"id\":\"rule-1\",\"name\":\"NAS远程\",\"remoteHost\":\"nas.example.com\",\"remotePort\":\"2443\"},{\"id\":\"rule-2\",\"name\":\"手机远程\",\"url\":\"remote.example.com:2443\"}]}}"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            });
        }

        private static HttpResponseMessage CreateJsonResponse(
            string json,
            Action<HttpResponseMessage>? configure = null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            configure?.Invoke(response);
            return response;
        }
    }

    private sealed record RequestSnapshot(string Path, string Authorization, string Cookie);
}
