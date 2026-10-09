using System.Globalization;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models.Network;
using OmniPlay.Core.Settings;

namespace OmniPlay.Infrastructure.Library;

/// <summary>
/// Speaks the real Lucky STUN admin API (parity with ARM: LuckyStun.swift):
/// POST /api/login with {Account, Password, TwoFA}, then GET /api/stunrulelist
/// with the Lucky-Admin-Token header and a browser-like header set.
/// </summary>
public sealed class LuckyStunClient : ILuckyStunClient
{
    private const string UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X) AppleWebKit/605.1.15";
    private const string AcceptLanguage = "zh-CN,zh;q=0.9,en;q=0.8";
    private const string LoginPath = "/api/login";
    private const string RulesPath = "/api/stunrulelist";

    private static readonly string[] LegacyRulePaths =
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
        "publicAddr",
        "externalAddress",
        "penetrationAddress",
        "forwardAddress",
        "stunAddress",
        "remoteUrl",
        "domain",
        "host"
    ];
    private static readonly string[] HostKeys = ["remoteHost", "publicHost", "externalHost"];
    private static readonly string[] PortKeys = ["remotePort", "publicPort", "externalPort", "port"];
    private static readonly string[] TokenKeys = ["token", "accessToken", "access_token", "jwt", "authorization"];

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
            throw new InvalidOperationException("Lucky STUN 管理地址无效。");
        }

        var account = (settings.Username ?? string.Empty).Trim();
        var password = settings.Password ?? string.Empty;
        if (account.Length == 0 || password.Length == 0)
        {
            throw new InvalidOperationException("请填写 Lucky 管理账号和密码。");
        }

        var token = await LoginAsync(baseUrl, account, password, cancellationToken);
        var rules = await TryFetchRulesAsync(baseUrl, token, cancellationToken)
            ?? await TryFetchLegacyRulesAsync(baseUrl, token, cancellationToken);
        rules ??= [];

        var selected = SelectRule(rules, settings);
        return new LuckyStunSnapshot(
            rules,
            selected,
            selected is null
                ? "登录成功，但没有读取到穿透规则。"
                : $"登录成功，已读取 {rules.Count} 条穿透规则。");
    }

    public static string NormalizeAddress(string value)
    {
        return LuckyStunAddressHelper.NormalizeAddress(value);
    }

    private async Task<string> LoginAsync(
        string baseUrl,
        string account,
        string password,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl(baseUrl, LoginPath);
        var body = JsonSerializer.Serialize(new { Account = account, Password = password, TwoFA = string.Empty });
        (int Status, string Body) response;
        try
        {
            response = await SendAsync(HttpMethod.Post, url, baseUrl, body, token: null, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            throw new InvalidOperationException("Lucky STUN 请求超时。");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Lucky STUN 连接失败：{ExplainHttpError(ex)}");
        }

        var (status, responseBody) = response;
        if (status is < 200 or >= 300)
        {
            throw new InvalidOperationException($"Lucky STUN 登录失败：{LoginPath} 返回 HTTP {status}。");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(responseBody);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Lucky STUN 返回了无法解析的登录数据。");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object || !TryGetInt32(document.RootElement, "ret", out var result))
            {
                throw new InvalidOperationException("Lucky STUN 返回了无法解析的登录数据。");
            }

            if (result != 0)
            {
                throw new InvalidOperationException($"Lucky STUN 登录失败：{MapErrorMessage(document.RootElement)}");
            }

            var token = FindStringRecursive(document.RootElement, TokenKeys);
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("Lucky STUN 登录成功，但响应中没有管理令牌。");
            }

            var trimmed = token.Trim();
            if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed["Bearer ".Length..].Trim();
            }

            return trimmed;
        }
    }

    private async Task<IReadOnlyList<LuckyStunRule>?> TryFetchRulesAsync(
        string baseUrl,
        string token,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl(baseUrl, RulesPath);
        (int Status, string Body) response;
        try
        {
            response = await SendAsync(HttpMethod.Get, url, baseUrl, jsonBody: null, token, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            throw new InvalidOperationException("Lucky STUN 请求超时。");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"无法读取 Lucky STUN 规则：{ExplainHttpError(ex)}");
        }

        var (status, responseBody) = response;
        if (status is < 200 or >= 300)
        {
            if (status == 404)
            {
                return null;
            }

            throw new InvalidOperationException($"无法读取 Lucky STUN 规则：{RulesPath} 返回 HTTP {status}。");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(responseBody);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Lucky STUN 返回了无法解析的规则数据。");
        }

        using (document)
        {
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                TryGetInt32(document.RootElement, "ret", out var result) &&
                result != 0)
            {
                throw new InvalidOperationException($"无法读取 Lucky STUN 规则：{MapErrorMessage(document.RootElement)}");
            }
        }

        return ParseRules(responseBody);
    }

    private async Task<IReadOnlyList<LuckyStunRule>?> TryFetchLegacyRulesAsync(
        string baseUrl,
        string token,
        CancellationToken cancellationToken)
    {
        foreach (var path in LegacyRulePaths)
        {
            try
            {
                var (status, responseBody) = await SendAsync(HttpMethod.Get, BuildUrl(baseUrl, path), baseUrl, jsonBody: null, token, cancellationToken);
                if (status is < 200 or >= 300)
                {
                    continue;
                }

                var rules = ParseRules(responseBody);
                if (rules.Count > 0)
                {
                    return rules;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                throw new InvalidOperationException("Lucky STUN 请求超时。");
            }
            catch (HttpRequestException)
            {
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    private async Task<(int Status, string Body)> SendAsync(
        HttpMethod method,
        string url,
        string baseUrl,
        string? jsonBody,
        string? token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept-Language", AcceptLanguage);
        request.Headers.TryAddWithoutValidation("Origin", new Uri(baseUrl).GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation("Referer", baseUrl);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("Lucky-Admin-Token", token);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        return ((int)response.StatusCode, responseBody);
    }

    private static string ExplainHttpError(HttpRequestException ex)
    {
        if (ex.InnerException is SocketException socket)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "连接被拒绝，请确认 Lucky STUN 服务已启动且管理地址正确。",
                SocketError.TimedOut => "连接超时。",
                _ => socket.Message
            };
        }

        return ex.Message;
    }

    private static string BuildUrl(string baseUrl, string path)
    {
        return $"{baseUrl.TrimEnd('/')}{path}?_={BuildNonce()}";
    }

    private static string BuildNonce()
    {
        var milliseconds = ((long)(DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        if (milliseconds.Length <= 1)
        {
            return milliseconds;
        }

        var prefix = milliseconds.AsSpan()[..^1];
        var checksum = 0;
        foreach (var character in prefix)
        {
            checksum += character - 48;
        }

        return prefix.ToString() + (checksum % 8).ToString(CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<LuckyStunRule> ParseRules(string body)
    {
        using var document = JsonDocument.Parse(body);
        var rules = new List<LuckyStunRule>();
        CollectRules(document.RootElement, rules, "Lucky STUN 规则");
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

        var host = FindString(element, HostKeys);
        var port = FindString(element, PortKeys);
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

    private static bool LooksLikeAddress(string value)
    {
        var trimmed = value.Trim();
        return (trimmed.Contains('.', StringComparison.Ordinal) || trimmed.Contains(':', StringComparison.Ordinal)) &&
               !trimmed.Contains("/api/", StringComparison.OrdinalIgnoreCase);
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

    private static string MapErrorMessage(JsonElement element)
    {
        var raw = (FindString(element, ["msg", "message"]) ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return "未知错误";
        }

        return raw switch
        {
            "IncorrectAccountOrPassword" => "账号或密码错误",
            "LoginFailed" => "登录失败",
            "NoPermission" or "PermissionDenied" => "当前账号没有访问权限",
            "TokenExpired" or "InvalidToken" => "登录凭证已失效",
            _ => raw
        };
    }

    private static bool TryGetInt32(JsonElement element, string key, out int value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(property.GetString(), out value),
            _ => false
        };
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
}
