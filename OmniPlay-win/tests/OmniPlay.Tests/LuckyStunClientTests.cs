using System.Net;
using System.Net.Http.Headers;
using OmniPlay.Core.Settings;
using OmniPlay.Infrastructure.Library;

namespace OmniPlay.Tests;

public sealed class LuckyStunClientTests
{
    [Fact]
    public async Task FetchAsync_LogsInReadsRealStunRuleListAndUsesSelectedRule()
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
        Assert.Equal(2, handler.Requests.Count);

        var loginRequest = handler.Requests[0];
        Assert.Equal("/api/login", loginRequest.Path);
        Assert.Equal("POST", loginRequest.Method);
        Assert.Contains("\"Account\":\"admin\"", loginRequest.Body);
        Assert.Contains("\"Password\":\"secret\"", loginRequest.Body);
        Assert.Contains("\"TwoFA\":\"\"", loginRequest.Body);

        var rulesRequest = handler.Requests[1];
        Assert.Equal("/api/stunrulelist", rulesRequest.Path);
        Assert.Equal("GET", rulesRequest.Method);
        Assert.Equal("lucky-token", rulesRequest.LuckyAdminToken);
        Assert.NotEmpty(rulesRequest.Nonce);
    }

    [Fact]
    public async Task FetchAsync_MapsLoginFailureMessageFromRetEnvelope()
    {
        var handler = new LuckyHttpMessageHandler
        {
            LoginJson = "{\"ret\":1,\"msg\":\"IncorrectAccountOrPassword\"}"
        };
        using var httpClient = new HttpClient(handler);
        var client = new LuckyStunClient(httpClient);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchAsync(new LuckyStunSettings
        {
            ManagementUrl = "192.168.1.2:16601",
            Username = "admin",
            Password = "wrong"
        }));

        Assert.Contains("账号或密码错误", error.Message);
    }

    [Fact]
    public async Task FetchAsync_ReportsRulesFailureFromRetEnvelope()
    {
        var handler = new LuckyHttpMessageHandler
        {
            RulesJson = "{\"ret\":401,\"msg\":\"TokenExpired\"}"
        };
        using var httpClient = new HttpClient(handler);
        var client = new LuckyStunClient(httpClient);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchAsync(new LuckyStunSettings
        {
            ManagementUrl = "192.168.1.2:16601",
            Username = "admin",
            Password = "secret"
        }));

        Assert.Contains("登录凭证已失效", error.Message);
    }

    [Fact]
    public async Task FetchAsync_ReportsMissingTokenInSuccessfulLogin()
    {
        var handler = new LuckyHttpMessageHandler
        {
            LoginJson = "{\"ret\":0,\"msg\":\"OK\"}"
        };
        using var httpClient = new HttpClient(handler);
        var client = new LuckyStunClient(httpClient);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchAsync(new LuckyStunSettings
        {
            ManagementUrl = "192.168.1.2:16601",
            Username = "admin",
            Password = "secret"
        }));

        Assert.Contains("响应中没有管理令牌", error.Message);
    }

    [Fact]
    public async Task FetchAsync_RequiresManagementCredentials()
    {
        var handler = new LuckyHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var client = new LuckyStunClient(httpClient);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchAsync(new LuckyStunSettings
        {
            ManagementUrl = "192.168.1.2:16601",
            Username = "  ",
            Password = ""
        }));

        Assert.Contains("请填写 Lucky 管理账号和密码", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task FetchAsync_ReportsHttpFailureFromLoginEndpoint()
    {
        var handler = new LuckyHttpMessageHandler
        {
            LoginStatus = HttpStatusCode.BadRequest
        };
        using var httpClient = new HttpClient(handler);
        var client = new LuckyStunClient(httpClient);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchAsync(new LuckyStunSettings
        {
            ManagementUrl = "192.168.1.2:16601",
            Username = "admin",
            Password = "secret"
        }));

        Assert.Contains("HTTP 400", error.Message);
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

        public string LoginJson { get; set; } =
            "{\"ret\":0,\"msg\":\"OK\",\"token\":\"lucky-token\"}";

        public HttpStatusCode LoginStatus { get; set; } = HttpStatusCode.OK;

        public string RulesJson { get; set; } =
            "{\"ret\":0,\"msg\":\"OK\",\"data\":{\"rules\":[{\"id\":\"rule-1\",\"name\":\"NAS远程\",\"remoteHost\":\"nas.example.com\",\"remotePort\":\"2443\"},{\"id\":\"rule-2\",\"name\":\"手机远程\",\"url\":\"remote.example.com:2443\"}]}}";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string? luckyToken = null;
            if (request.Headers.TryGetValues("Lucky-Admin-Token", out var tokenValues))
            {
                luckyToken = tokenValues.FirstOrDefault();
            }

            var query = request.RequestUri!.Query;
            Requests.Add(new RequestSnapshot(
                request.RequestUri.AbsolutePath,
                request.Method.Method,
                luckyToken,
                query.Length > 3 ? query[3..] : string.Empty,
                request.Content is null
                    ? string.Empty
                    : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult()));

            return Task.FromResult(request.RequestUri.AbsolutePath switch
            {
                "/api/login" => new HttpResponseMessage(LoginStatus)
                {
                    Content = new StringContent(LoginJson)
                },
                "/api/stunrulelist" => CreateJsonResponse(RulesJson),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            });
        }

        private static HttpResponseMessage CreateJsonResponse(string json)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return response;
        }
    }

    private sealed record RequestSnapshot(
        string Path,
        string Method,
        string? LuckyAdminToken,
        string Nonce,
        string Body);
}
