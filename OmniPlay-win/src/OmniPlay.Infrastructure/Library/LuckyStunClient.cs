using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Settings;

namespace OmniPlay.Infrastructure.Library;

public sealed class LuckyStunClient : ILuckyStunClient
{
    private static readonly string[] LoginPaths =
    [
        "/api/login",
        "/api/auth/login",
        "/api/user/login",
        "/api/auth/signin",
        "/login"
    ];

    private static readonly string[] RulePaths =
    [
        "/api/stun",
        "/api/stun/rules",
        "/api/stun/list",
        "/api/penetration",
        "/api/penetration/rules",
        "/api/nat/stun",
        "/api/config/stun",
        "/api/rules",
        "/api/getstunlist"
    ];

    private static readonly string[] IdKeys = ["id", "ruleId", "uuid", "key", "index"];
    private static readonly string[] NameKeys = ["name", "title", "ruleName", "remark", "description"];
    private static readonly string[] AddressKeys =
    [
        "address",
        "url",
        "remoteAddress",
        "publicAddress",
        "externalAddress",
        "penetrationAddress",
        "forwardAddress",
        "stunAddress",
        "remoteUrl",
        "domain",
        "host"
    ];

    private readonly HttpClient httpClient;

    public LuckyStunClient(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public async Task<LuckyStunSnapshot> FetchAsync(
        LuckyStunSettings settings,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = NormalizeManagementUrl(settings.ManagementUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("请填写 Lucky STUN 管理地址。");
        }

        var cookies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? token = null;
        if (!string.IsNullOrWhiteSpace(settings.Username) || !string.IsNullOrWhiteSpace(settings.Password))
        {
            (token, cookies) = await TryLoginAsync(
                baseUrl,
                settings.Username.Trim(),
                settings.Password,
                cookies,
                cancellationToken);
        }

        var lastFailure = "Lucky STUN 没有返回规则列表。";
        foreach (var path in RulePaths)
        {
            try
            {
                var response = await SendAsync(
                    HttpMethod.Get,
                    CombineUrl(baseUrl, path),
                    body: null,
                    token,
                    cookies,
                    cancellationToken);
                if ((int)response.StatusCode is < 200 or >= 300)
                {
                    lastFailure = $"HTTP {(int)response.StatusCode}";
                    continue;
                }

                var rules = ParseRules(response.Body);
                if (rules.Count == 0)
                {
                    lastFailure = "响应中没有识别到穿透规则。";
                    continue;
                }

                var selected = SelectRule(rules, settings);
                return new LuckyStunSnapshot(
                    rules,
                    selected,
                    selected is null
                        ? "已登录，但没有可用的穿透地址。"
                        : $"已读取 {rules.Count} 条规则，当前地址：{selected.Address}");
            }
            catch (HttpRequestException ex)
            {
                lastFailure = ex.Message;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastFailure = "Lucky STUN 请求超时。";
            }
            catch (JsonException ex)
            {
                lastFailure = $"规则响应解析失败：{ex.Message}";
            }
        }

        throw new InvalidOperationException($"无法读取 Lucky STUN 规则：{lastFailure}");
    }

    public static string NormalizeAddress(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return $"http:{trimmed}";
        }

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.TrimEnd('/');
        }

        return trimmed.Contains(':', StringComparison.Ordinal) || trimmed.Contains('.', StringComparison.Ordinal)
            ? $"http://{trimmed.TrimEnd('/') }"
            : trimmed;
    }

    private async Task<(string? Token, HashSet<string> Cookies)> TryLoginAsync(
        string baseUrl,
        string username,
        string password,
        HashSet<string> cookies,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new { username, password });
        var lastStatus = HttpStatusCode.BadRequest;
        foreach (var path in LoginPaths)
        {
            try
            {
                var response = await SendAsync(
                    HttpMethod.Post,
                    CombineUrl(baseUrl, path),
                    body,
                    token: null,
                    cookies,
                    cancellationToken);
                lastStatus = response.StatusCode;
                if ((int)response.StatusCode is < 200 or >= 300)
                {
                    continue;
                }

                var token = TryFindString(response.Body, ["token", "accessToken", "access_token", "jwt", "authorization"]);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    token = token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? token
                        : $"Bearer {token}";
                }

                return (token, cookies);
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        if (lastStatus is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException("Lucky STUN 登录失败，请检查账号和密码。");
        }

        return (null, cookies);
    }

    private async Task<LuckyHttpResponse> SendAsync(
        HttpMethod method,
        string url,
        string? body,
        string? token,
        HashSet<string> cookies,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("OmniPlay-LuckySTUN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(token);
        }

        if (cookies.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        foreach (var cookie in response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [])
        {
            var value = cookie.Split(';', 2)[0].Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                cookies.RemoveWhere(existing => existing.StartsWith(value.Split('=', 2)[0] + "=", StringComparison.OrdinalIgnoreCase));
                cookies.Add(value);
            }
        }

        return new LuckyHttpResponse(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static IReadOnlyList<LuckyStunRule> ParseRules(string body)
    {
        using var document = JsonDocument.Parse(body);
        var rules = new List<LuckyStunRule>();
        CollectRules(document.RootElement, rules, "rule");
        return rules
            .GroupBy(static rule => $"{rule.Id}\n{rule.Name}\n{rule.Address}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
    }

    private static void CollectRules(JsonElement element, ICollection<LuckyStunRule> rules, string fallbackName)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                CollectRules(child, rules, fallbackName);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var id = FindString(element, IdKeys) ?? string.Empty;
        var name = FindString(element, NameKeys) ?? fallbackName;
        var address = FindAddress(element);
        if (!string.IsNullOrWhiteSpace(address) && LooksLikeAddress(address))
        {
            rules.Add(new LuckyStunRule(
                string.IsNullOrWhiteSpace(id) ? $"{name}:{address}" : id,
                string.IsNullOrWhiteSpace(name) ? "Lucky STUN 规则" : name,
                NormalizeAddress(address)));
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                CollectRules(property.Value, rules, name);
            }
        }
    }

    private static string? FindAddress(JsonElement element)
    {
        foreach (var key in AddressKeys)
        {
            var value = FindString(element, [key]);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        var host = FindString(element, ["remoteHost", "publicHost", "externalHost"]);
        var port = FindString(element, ["remotePort", "publicPort", "externalPort", "port"]);
        return string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(port)
            ? null
            : $"{host}:{port}";
    }

    private static string? FindString(JsonElement element, IReadOnlyList<string> keys)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!keys.Any(key => string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        return null;
    }

    private static bool LooksLikeAddress(string value)
    {
        var trimmed = value.Trim();
        return (trimmed.Contains('.', StringComparison.Ordinal) || trimmed.Contains(':', StringComparison.Ordinal)) &&
               !trimmed.Contains("/api/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryFindString(string body, IReadOnlyList<string> keys)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return FindStringRecursive(document.RootElement, keys);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FindStringRecursive(JsonElement element, IReadOnlyList<string> keys)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var direct = FindString(element, keys);
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }

            foreach (var property in element.EnumerateObject())
            {
                var nested = FindStringRecursive(property.Value, keys);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                var nested = FindStringRecursive(child, keys);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static LuckyStunRule? SelectRule(IReadOnlyList<LuckyStunRule> rules, LuckyStunSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.SelectedRuleId))
        {
            var byId = rules.FirstOrDefault(rule => string.Equals(rule.Id, settings.SelectedRuleId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                return byId;
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.SelectedRuleName))
        {
            var byName = rules.FirstOrDefault(rule => string.Equals(rule.Name, settings.SelectedRuleName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                return byName;
            }
        }

        return rules.FirstOrDefault();
    }

    private static string NormalizeManagementUrl(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = $"http://{trimmed}";
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return string.Empty;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4].TrimEnd('/');
        }

        var builder = new UriBuilder(uri) { Path = path, Query = string.Empty, Fragment = string.Empty };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static string CombineUrl(string baseUrl, string path)
    {
        return $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
    }

    private sealed record LuckyHttpResponse(HttpStatusCode StatusCode, string Body);
}
