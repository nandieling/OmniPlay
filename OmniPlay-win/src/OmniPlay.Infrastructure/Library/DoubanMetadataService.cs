using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models.Library;
using OmniPlay.Infrastructure.Data;

namespace OmniPlay.Infrastructure.Library;

public sealed class DoubanMetadataService : IDoubanMetadataService
{
    private static readonly TimeSpan RiskControlPause = TimeSpan.FromHours(12);
    private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromSeconds(12);
    private const string MobileUserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";
    private const string RefererUrl = "https://m.douban.com/movie/";

    private readonly SqliteDatabase database;
    private readonly HttpClient httpClient;
    private readonly HttpClient pageClient;
    private readonly IStoragePaths storagePaths;
    private readonly DoubanRequestGate requestGate;

    public DoubanMetadataService(SqliteDatabase database, HttpClient httpClient, IStoragePaths storagePaths)
    {
        this.database = database;
        this.httpClient = httpClient;
        this.storagePaths = storagePaths;
        this.requestGate = new DoubanRequestGate(storagePaths);

        // 页面请求单独使用带 cookie 的客户端：安全验证通过后会写入 cookie，后续请求可避免再次被挑战。
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };
        pageClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<DoubanMetadata?> BindAsync(
        string itemKind,
        long entityId,
        string subject,
        string fallbackTitle,
        string? fallbackYear,
        CancellationToken cancellationToken = default)
    {
        var normalizedKind = NormalizeKind(itemKind);
        var subjectId = ExtractSubjectId(subject);
        if (string.IsNullOrWhiteSpace(subjectId))
        {
            throw new ArgumentException("请填写豆瓣 subject ID 或豆瓣电影链接。", nameof(subject));
        }

        var metadata = await FetchSubjectAsync(normalizedKind, entityId, subjectId, fallbackTitle, fallbackYear, cancellationToken)
                        ?? CreatePlaceholder(normalizedKind, entityId, subjectId, fallbackTitle, fallbackYear);
        await SaveAsync(metadata, cancellationToken);
        await ApplyPeopleToEntityAsync(metadata, cancellationToken);
        return metadata;
    }

    public async Task<DoubanMetadata?> GetAsync(
        string itemKind,
        long entityId,
        CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        return await connection.QueryFirstOrDefaultAsync<DoubanRow>(
            new CommandDefinition(
                """
                SELECT itemKind AS ItemKind,
                       entityId AS EntityId,
                       subjectId AS SubjectId,
                       subjectUrl AS SubjectUrl,
                       title AS Title,
                       originalTitle AS OriginalTitle,
                       year AS Year,
                       rating AS Rating,
                       ratingCount AS RatingCount,
                       summary AS Summary,
                       posterUrl AS PosterUrl,
                       fetchedAt AS FetchedAt,
                       directors AS Directors,
                       casts AS Casts
                FROM doubanMetadata
                WHERE itemKind = @ItemKind AND entityId = @EntityId
                """,
                new { ItemKind = NormalizeKind(itemKind), EntityId = entityId },
                cancellationToken: cancellationToken)) is { } row
            ? ToMetadata(row)
            : null;
    }

    public async Task UnbindAsync(
        string itemKind,
        long entityId,
        CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM doubanMetadata WHERE itemKind = @ItemKind AND entityId = @EntityId",
                new { ItemKind = NormalizeKind(itemKind), EntityId = entityId },
                cancellationToken: cancellationToken));
    }

    private async Task<DoubanMetadata?> FetchSubjectAsync(
        string itemKind,
        long entityId,
        string subjectId,
        string fallbackTitle,
        string? fallbackYear,
        CancellationToken cancellationToken)
    {
        try
        {
            await requestGate.AcquireAsync(cancellationToken);
        }
        catch (DoubanRateLimitException)
        {
            throw;
        }

        string? html;
        try
        {
            html = await DownloadAsync($"https://m.douban.com/movie/subject/{subjectId}/", RefererUrl, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }

        if (html is null)
        {
            return null;
        }

        if (IsChallengePage(html))
        {
            string? solved = null;
            try
            {
                solved = await SolveChallengeAsync(html, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                solved = null;
            }

            if (solved is null || IsChallengePage(solved))
            {
                requestGate.Pause(RiskControlPause);
                throw new DoubanRateLimitException("豆瓣返回了安全验证页面，自动验证未通过，已暂停豆瓣请求 12 小时。");
            }

            html = solved;
        }

        if (html.Contains("sec.douban.com", StringComparison.OrdinalIgnoreCase)
            || html.Contains("检测到有异常请求", StringComparison.Ordinal)
            || html.Contains("captcha", StringComparison.OrdinalIgnoreCase))
        {
            requestGate.Pause(RiskControlPause);
            throw new DoubanRateLimitException("豆瓣返回了验证或异常请求页面，已暂停豆瓣请求 12 小时。");
        }

        var parsed = ParseSubjectHtml(html);
        if (parsed is null || (parsed.Rating is null && IsInvalidPlaceholderTitle(parsed.Title)))
        {
            return null;
        }

        var posterLocalPath = await TryDownloadPosterAsync(subjectId, parsed.PosterUrl, cancellationToken);

        return new DoubanMetadata(
            itemKind,
            entityId,
            subjectId,
            $"https://movie.douban.com/subject/{subjectId}/",
            parsed.Title,
            parsed.OriginalTitle,
            parsed.Year ?? NormalizeYear(fallbackYear),
            parsed.Rating,
            parsed.RatingCount,
            parsed.Summary,
            parsed.PosterUrl,
            DateTimeOffset.UtcNow,
            posterLocalPath)
        {
            Directors = parsed.Directors,
            Casts = parsed.Casts,
        };
    }

    private async Task<string?> DownloadAsync(string url, string referer, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(MobileUserAgent);
        request.Headers.Referrer = new Uri(referer);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh-Hans;q=0.9,en;q=0.8");
        using var response = await pageClient.SendAsync(request, cancellationToken);

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            requestGate.Pause(RiskControlPause);
            throw new DoubanRateLimitException($"豆瓣返回 {(int)response.StatusCode}，已暂停豆瓣请求 12 小时以避免继续触发风控。");
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>
    /// 豆瓣"安全验证"页：表单 form#sec POST 到 /c，需先解出 sha512(cha + nonce) 前缀为 difficulty 个 0 的 nonce。
    /// </summary>
    private async Task<string?> SolveChallengeAsync(string challengeHtml, CancellationToken cancellationToken)
    {
        if (!TrySolveChallenge(challengeHtml, out var fields))
        {
            return null;
        }

        var form = new Dictionary<string, string>
        {
            ["tok"] = fields.Tok,
            ["cha"] = fields.Cha,
            ["sol"] = fields.Sol,
        };
        if (!string.IsNullOrWhiteSpace(fields.Red))
        {
            form["red"] = fields.Red;
        }

        using var content = new FormUrlEncodedContent(form);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://m.douban.com/c") { Content = content };
        request.Headers.UserAgent.ParseAdd(MobileUserAgent);
        request.Headers.Referrer = new Uri(RefererUrl);
        using var response = await pageClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public static bool IsChallengePage(string html)
    {
        return html.Contains("安全验证", StringComparison.Ordinal)
            || Regex.IsMatch(html, "<form[^>]+id=[\"']sec[\"'][^>]+action=[\"']/c[\"']", RegexOptions.IgnoreCase);
    }

    public static bool TrySolveChallenge(string html, out (string Tok, string Cha, string? Red, string Sol) result)
    {
        var tok = FirstMatch(html, "<input[^>]*id=[\"']tok[\"'][^>]*value=[\"']([^\"']+)[\"']");
        var cha = FirstMatch(html, "<input[^>]*id=[\"']cha[\"'][^>]*value=[\"']([^\"']+)[\"']");
        var red = FirstMatch(html, "<input[^>]*id=[\"']red[\"'][^>]*value=[\"']([^\"']+)[\"']");
        var difficultyMatch = Regex.Match(html, "difficulty\\s*=\\s*(\\d+)");
        var difficulty = difficultyMatch.Success ? int.Parse(difficultyMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 4;
        difficulty = Math.Clamp(difficulty, 1, 8);
        if (string.IsNullOrEmpty(tok) || string.IsNullOrEmpty(cha))
        {
            result = default;
            return false;
        }

        var target = new string('0', difficulty);
        for (var nonce = 1; nonce <= 5_000_000; nonce++)
        {
            var hash = Sha512Hex(cha + nonce.ToString(CultureInfo.InvariantCulture));
            if (hash.StartsWith(target, StringComparison.Ordinal))
            {
                result = (tok, cha, red, nonce.ToString(CultureInfo.InvariantCulture));
                return true;
            }
        }

        result = default;
        return false;
    }

    private static string Sha512Hex(string input)
    {
        return Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    public sealed record ParsedSubject(
        string Title,
        string? OriginalTitle,
        string? Year,
        double? Rating,
        int? RatingCount,
        string? Summary,
        string? PosterUrl)
    {
        public IReadOnlyList<string> Directors { get; init; } = [];

        public IReadOnlyList<string> Casts { get; init; } = [];
    }

    /// <summary>
    /// 对齐 mac 版 DoubanService.parseSubject：JSON-LD 优先，其次 og/meta/标题等多种兜底。
    /// </summary>
    public static ParsedSubject? ParseSubjectHtml(string html)
    {
        var jsonLd = ExtractJsonLd(html);
        var title = CleanTitle(FirstNonEmpty(
            JsonGetString(jsonLd, "name"),
            MetaContent(html, "property", "og:title"),
            MetaContent(html, "property", "og:title", contentFirst: true),
            StrippedMatch(html, "<h1[^>]*>(.*?)</h1>"),
            StrippedMatch(html, "<span[^>]+property=[\"']v:itemreviewed[\"'][^>]*>(.*?)</span>"),
            TitleFromTag(html)));
        if (title is null)
        {
            return null;
        }

        var aggregateRating = JsonGetObject(jsonLd, "aggregateRating");
        var rating = ParseRating(html, aggregateRating);
        var ratingCount = ParseRatingCount(html, aggregateRating);
        var summary = FirstNonEmpty(
            JsonGetString(jsonLd, "description"),
            StrippedMatch(html, "<section[^>]+class=[\"'][^\"']*subject-intro[^\"']*\"[^>]*>.*?<p[^>]*>(.*?)</p>"),
            StrippedMatch(html, "<span[^>]+property=[\"']v:summary[\"'][^>]*>(.*?)</span>"),
            MetaContent(html, "name", "description"),
            MetaContent(html, "itemprop", "description"),
            MetaContent(html, "property", "og:description"));
        var originalTitle = StrippedMatch(html, "<div[^>]+class=[\"']sub-original-title[\"'][^>]*>(.*?)</div>");
        var directors = JsonNameList(jsonLd, "director");
        var casts = JsonNameList(jsonLd, "actor");
        var year = JsonYear(jsonLd)
            ?? YearFrom(originalTitle)
            ?? YearFrom(FirstMatch(html, "(19|20)\\d{2}-[0-9]{2}-[0-9]{2}[^<]*上映"))
            ?? YearFrom(FirstMatch(html, "<span[^>]+class=[\"']year[\"'][^>]*>[(（](19|20)\\d{2}[)）]</span>"));

        return new ParsedSubject(title, originalTitle, year, rating, ratingCount, summary, BestPosterUrl(html, jsonLd))
        {
            Directors = directors,
            Casts = casts,
        };
    }

    private static double? ParseRating(string html, JsonElement? aggregateRating)
    {
        var value = JsonGetDouble(aggregateRating, "ratingValue") ?? JsonGetDouble(aggregateRating, "rating");
        if (value is not null)
        {
            return ValidRating(value.Value);
        }

        string[] patterns =
        {
            "<strong[^>]+property=[\"']v:average[\"'][^>]*>(.*?)</strong>",
            "<strong[^>]+class=[\"'][^\"']*rating_num[^\"']*[\"'][^>]*>(.*?)</strong>",
            "<span[^>]+class=[\"'][^\"']*rating_num[^\"']*[\"'][^>]*>(.*?)</span>",
            "<meta[^>]+itemprop=[\"']ratingValue[\"'][^>]+content=[\"']([0-9]+(?:\\.[0-9]+)?)[\"']",
            "<meta[^>]+content=[\"']([0-9]+(?:\\.[0-9]+)?)[\"'][^>]+itemprop=[\"']ratingValue[\"']",
            "\"ratingValue\"\\s*:\\s*\"?([0-9]+(?:\\.[0-9]+)?)",
            "\"rating_num\"\\s*:\\s*\"?([0-9]+(?:\\.[0-9]+)?)",
            "\"value\"\\s*:\\s*\"?([0-9]+(?:\\.[0-9]+)?)[\"']?\\s*,\\s*\"max\"",
            "\"average\"\\s*:\\s*\"?([0-9]+(?:\\.[0-9]+)?)[\"']",
            "<span[^>]+class=[\"'][^\"']*rating[^\"']*[\"'][^>]*>\\s*([0-9]+(?:\\.[0-9]+)?)\\s*</span>",
        };
        foreach (var pattern in patterns)
        {
            var raw = FirstMatch(html, pattern);
            if (raw is null)
            {
                continue;
            }
            if (double.TryParse(CleanText(raw), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return ValidRating(parsed);
            }
        }

        return null;
    }

    private static double? ValidRating(double value)
    {
        return value > 0 && value <= 10 ? value : null;
    }

    private static int? ParseRatingCount(string html, JsonElement? aggregateRating)
    {
        var value = JsonGetInt(aggregateRating, "ratingCount") ?? JsonGetInt(aggregateRating, "reviewCount");
        if (value is > 0)
        {
            return value;
        }

        string[] patterns =
        {
            "<span[^>]+property=[\"']v:votes[\"'][^>]*>(.*?)</span>",
            "<meta[^>]+itemprop=[\"']reviewCount[\"'][^>]+content=[\"']([0-9][0-9,]*)[\"']",
            "<meta[^>]+content=[\"']([0-9][0-9,]*)[\"'][^>]+itemprop=[\"']reviewCount[\"']",
            "([0-9][0-9,]*)\\s*人评价",
            "([0-9][0-9,]*)\\s*人评分",
            "\"ratingCount\"\\s*:\\s*\"?([0-9][0-9,]*)",
            "\"count\"\\s*:\\s*\"?([0-9][0-9,]*)",
        };
        foreach (var pattern in patterns)
        {
            var raw = FirstMatch(html, pattern);
            if (raw is null)
            {
                continue;
            }
            var digits = new string(raw.Where(char.IsDigit).ToArray());
            if (long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0)
            {
                return (int)Math.Min(count, int.MaxValue);
            }
        }

        return null;
    }

    private static string? BestPosterUrl(string html, JsonElement? jsonLd)
    {
        string?[] candidates =
        {
            DecodeEntity(FirstMatch(html, "<a[^>]+class=[\"']sub-cover[\"'][^>]*>.*?<img[^>]+src=[\"']([^\"']+)[\"']")),
            DecodeEntity(FirstMatch(html, "<img[^>]+rel=[\"']v:image[\"'][^>]+src=[\"']([^\"']+)[\"']")),
            JsonGetString(jsonLd, "image"),
            DecodeEntity(MetaContent(html, "itemprop", "image")),
            DecodeEntity(MetaContent(html, "property", "og:image")),
        };
        foreach (var candidate in candidates)
        {
            var normalized = DoubanPosterUrls.Normalize(candidate);
            if (DoubanPosterUrls.IsUsable(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    private static JsonElement? ExtractJsonLd(string html)
    {
        var matches = Regex.Matches(html, "<script[^>]+type=[\"']application/ld\\+json[\"'][^>]*>(.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match match in matches)
        {
            var raw = match.Groups[1].Value.Trim();
            if (raw.Length == 0)
            {
                continue;
            }
            try
            {
                using var document = JsonDocument.Parse(raw);
                var root = document.RootElement;
                var element = root.ValueKind == JsonValueKind.Array
                    ? root.EnumerateArray().FirstOrDefault(IsJsonLdSubject)
                    : IsJsonLdSubject(root) ? root : default;
                if (element.ValueKind != JsonValueKind.Undefined)
                {
                    return element.Clone();
                }
            }
            catch (JsonException)
            {
                // 单个 JSON-LD 块解析失败时跳过，继续尝试下一块。
            }
        }

        return null;
    }

    private static bool IsJsonLdSubject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (element.TryGetProperty("@type", out var type) && type.ValueKind == JsonValueKind.String
            && type.GetString() is { } typeValue
            && typeValue.Contains("movie", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return element.TryGetProperty("name", out _);
    }

    private static string? JsonGetString(JsonElement? element, string name)
    {
        if (element is { } e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        return null;
    }

    private static JsonElement? JsonGetObject(JsonElement? element, string name)
    {
        if (element is { } e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }
        return null;
    }

    private static double? JsonGetDouble(JsonElement? element, string name)
    {
        if (element is { } e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            {
                return number;
            }
            if (value.ValueKind == JsonValueKind.String
                && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }
        return null;
    }

    private static int? JsonGetInt(JsonElement? element, string name)
    {
        if (element is { } e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value))
        {
            string? text = value.ValueKind == JsonValueKind.Number ? value.ToString() : value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (text is null)
            {
                return null;
            }
            var digits = new string(text.Where(char.IsDigit).ToArray());
            if (long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
            {
                return (int)Math.Min(count, int.MaxValue);
            }
        }
        return null;
    }

    private static string? JsonYear(JsonElement? element)
    {
        return YearFrom(JsonGetString(element, "datePublished"));
    }

    private static string? YearFrom(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var match = Regex.Match(value, "(19|20)\\d{2}");
        return match.Success ? match.Value : null;
    }

    private static string? MetaContent(string html, string attribute, string value, bool contentFirst = false)
    {
        var pattern = contentFirst
            ? $@"<meta[^>]+content=[""']([^""']+)[.""'][^>]+{attribute}=[""']{value}[""']"
            : $@"<meta[^>]+{attribute}=[""']{value}[""'][^>]+content=[""']([^""']+)[.""']";
        return DecodeEntity(FirstMatch(html, pattern));
    }

    private static string? StrippedMatch(string html, string pattern)
    {
        return CleanText(FirstMatch(html, pattern));
    }

    private static string? TitleFromTag(string html)
    {
        var text = CleanText(FirstMatch(html, "<title>(.*?)</title>"));
        if (text is null)
        {
            return null;
        }
        text = text.Replace("(豆瓣)", string.Empty, StringComparison.Ordinal).Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? CleanTitle(string? value)
    {
        var text = CleanText(value);
        if (text is null)
        {
            return null;
        }
        text = text.Replace("(豆瓣)", string.Empty, StringComparison.Ordinal).Trim();
        text = Regex.Replace(text, "\\s+[-–—]\\s*(电影|电视剧|剧集|短片)\\s*$", string.Empty).Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var text = Regex.Replace(value, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, "\\s+", " ").Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? DecodeEntity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var text = System.Net.WebUtility.HtmlDecode(value).Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
    }

    private static bool IsInvalidPlaceholderTitle(string title)
    {
        var text = title.Trim();
        return text is "豆瓣" or "豆瓣电影" or "安全验证";
    }

    private async Task<string?> TryDownloadPosterAsync(string subjectId, string? posterUrl, CancellationToken cancellationToken)
    {
        if (!DoubanPosterUrls.IsUsable(posterUrl))
        {
            return null;
        }

        try
        {
            storagePaths.EnsureCreated();
            var extension = Path.GetExtension(posterUrl);
            if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8)
            {
                extension = ".jpg";
            }
            var destinationPath = Path.Combine(storagePaths.PostersDirectory, $"douban-{subjectId}{extension.ToLowerInvariant()}");
            if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
            {
                return destinationPath;
            }

            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadCts.CancelAfter(TimeSpan.FromSeconds(20));
            using var posterRequest = new HttpRequestMessage(HttpMethod.Get, posterUrl);
            posterRequest.Headers.UserAgent.ParseAdd(MobileUserAgent);
            posterRequest.Headers.Referrer = new Uri("https://movie.douban.com/");
            using var response = await pageClient.SendAsync(posterRequest, HttpCompletionOption.ResponseHeadersRead, downloadCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var tempPath = destinationPath + ".tmp";
            await using (var responseStream = await response.Content.ReadAsStreamAsync(downloadCts.Token))
            await using (var fileStream = File.Create(tempPath))
            {
                await responseStream.CopyToAsync(fileStream, downloadCts.Token);
            }
            if (new FileInfo(tempPath).Length == 0)
            {
                File.Delete(tempPath);
                return null;
            }
            File.Move(tempPath, destinationPath, overwrite: true);
            return destinationPath;
        }
        catch
        {
            return null;
        }
    }

    private static DoubanMetadata CreatePlaceholder(string itemKind, long entityId, string subjectId, string title, string? year)
    {
        return new DoubanMetadata(
            itemKind,
            entityId,
            subjectId,
            $"https://movie.douban.com/subject/{subjectId}/",
            title,
            null,
            NormalizeYear(year),
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            null);
    }

    private async Task SaveAsync(DoubanMetadata metadata, CancellationToken cancellationToken)
    {
        using var connection = database.OpenConnection();
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO doubanMetadata (
                    itemKind, entityId, subjectId, subjectUrl, title, originalTitle, year,
                    rating, ratingCount, summary, posterUrl, fetchedAt, directors, casts)
                VALUES (
                    @ItemKind, @EntityId, @SubjectId, @SubjectUrl, @Title, @OriginalTitle, @Year,
                    @Rating, @RatingCount, @Summary, @PosterUrl, @FetchedAt, @Directors, @Casts)
                ON CONFLICT(itemKind, entityId) DO UPDATE SET
                    subjectId = excluded.subjectId,
                    subjectUrl = excluded.subjectUrl,
                    title = excluded.title,
                    originalTitle = excluded.originalTitle,
                    year = excluded.year,
                    rating = excluded.rating,
                    ratingCount = excluded.ratingCount,
                    summary = excluded.summary,
                    posterUrl = excluded.posterUrl,
                    fetchedAt = excluded.fetchedAt,
                    directors = COALESCE(excluded.directors, doubanMetadata.directors),
                    casts = COALESCE(excluded.casts, doubanMetadata.casts)
                """,
                new
                {
                    metadata.ItemKind,
                    metadata.EntityId,
                    metadata.SubjectId,
                    metadata.SubjectUrl,
                    metadata.Title,
                    metadata.OriginalTitle,
                    metadata.Year,
                    metadata.Rating,
                    metadata.RatingCount,
                    metadata.Summary,
                    metadata.PosterUrl,
                    FetchedAt = metadata.FetchedAt.ToString("O"),
                    Directors = OmniPlay.Core.Models.PersonNames.Encode(metadata.Directors),
                    Casts = OmniPlay.Core.Models.PersonNames.Encode(metadata.Casts)
                },
                cancellationToken: cancellationToken));
    }

    private static DoubanMetadata ToMetadata(DoubanRow row)
    {
        var fetchedAt = DateTimeOffset.TryParse(row.FetchedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : DateTimeOffset.UtcNow;
        return new DoubanMetadata(
            row.ItemKind,
            row.EntityId,
            row.SubjectId,
            row.SubjectUrl,
            row.Title,
            row.OriginalTitle,
            row.Year,
            row.Rating > 0 ? (double?)row.Rating : null,
            row.RatingCount > 0 ? (int?)row.RatingCount : null,
            row.Summary,
            row.PosterUrl,
            fetchedAt,
            null)
        {
            Directors = OmniPlay.Core.Models.PersonNames.Decode(row.Directors),
            Casts = OmniPlay.Core.Models.PersonNames.Decode(row.Casts),
        };
    }

    private static string NormalizeKind(string itemKind)
    {
        return string.Equals(itemKind, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
    }

    public static string? ExtractSubjectId(string value)
    {
        var trimmed = value.Trim();
        if (Regex.IsMatch(trimmed, "^\\d+$"))
        {
            return trimmed;
        }

        return FirstMatch(trimmed, "(?:movie\\.douban\\.com/subject|m\\.douban\\.com/movie/subject)/(\\d+)");
    }

    /// <summary>豆瓣 JSON-LD 里 director/actor 可能是对象、对象数组或字符串数组，统一取 name。</summary>
    private static IReadOnlyList<string> JsonNameList(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty(name, out var node))
        {
            return [];
        }

        List<string> names = [];
        CollectNames(node, names);
        return OmniPlay.Core.Models.PersonNames.Normalize(names);
    }

    private static void CollectNames(JsonElement node, List<string> names)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.String:
                names.Add(node.GetString() ?? string.Empty);
                return;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    CollectNames(item, names);
                }

                return;
            case JsonValueKind.Object:
                if (node.TryGetProperty("name", out var inner))
                {
                    CollectNames(inner, names);
                }

                return;
        }
    }

    /// <summary>豆瓣自带中文人名：只在库里还没有导演/主演时补写，避免盖掉 TMDB 的名单。</summary>
    private async Task ApplyPeopleToEntityAsync(DoubanMetadata metadata, CancellationToken cancellationToken)
    {
        if (metadata.Directors.Count == 0 && metadata.Casts.Count == 0)
        {
            return;
        }

        var table = string.Equals(metadata.ItemKind, "tv", StringComparison.OrdinalIgnoreCase) ? "tvShow" : "movie";
        var dateColumn = string.Equals(table, "tvShow", StringComparison.Ordinal) ? "firstAirDate" : "releaseDate";
        using var connection = database.OpenConnection();
        await connection.ExecuteAsync(
            new CommandDefinition(
                $"""
                UPDATE {table}
                SET director = CASE WHEN director IS NULL OR TRIM(director) = '' THEN @Director ELSE director END,
                    mainCast = CASE WHEN mainCast IS NULL OR TRIM(mainCast) = '' THEN @MainCast ELSE mainCast END,
                    directors = CASE WHEN directors IS NULL THEN @Directors ELSE directors END,
                    casts = CASE WHEN casts IS NULL THEN @Casts ELSE casts END,
                    {dateColumn} = COALESCE({dateColumn}, @Year)
                WHERE id = @EntityId
                """,
                new
                {
                    metadata.EntityId,
                    Director = metadata.DirectorText,
                    MainCast = metadata.CastText,
                    Directors = OmniPlay.Core.Models.PersonNames.Encode(metadata.Directors),
                    Casts = OmniPlay.Core.Models.PersonNames.Encode(metadata.Casts),
                    Year = metadata.Year,
                },
                cancellationToken: cancellationToken));
    }

    private static string? FirstMatch(string value, string pattern)
    {
        var match = Regex.Match(value, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? NormalizeYear(string? value)
    {
        var match = Regex.Match(value ?? string.Empty, "(19|20)\\d{2}");
        return match.Success ? match.Value : null;
    }

    public sealed record DoubanRow(
        string ItemKind,
        long EntityId,
        string SubjectId,
        string SubjectUrl,
        string Title,
        string? OriginalTitle,
        string? Year,
        double Rating,
        long RatingCount,
        string? Summary,
        string? PosterUrl,
        string FetchedAt,
        string? Directors = null,
        string? Casts = null);

    /// <summary>
    /// 对齐 mac 版 DoubanRequestGate：请求间最小 12 秒 + 随机抖动，风控冷却 12 小时并持久化。
    /// </summary>
    private sealed class DoubanRequestGate
    {
        private static readonly object Sync = new();
        private static DateTimeOffset nextAllowedUtc = DateTimeOffset.MinValue;

        private readonly string cooldownFilePath;

        public DoubanRequestGate(IStoragePaths storagePaths)
        {
            cooldownFilePath = Path.Combine(storagePaths.DataDirectory, "doubanCooldown.json");
        }

        public async Task AcquireAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            if (now < LoadCooldownUntil())
            {
                throw new DoubanRateLimitException("豆瓣请求已暂停，请稍后再试（风控冷却中）。");
            }

            TimeSpan delay;
            lock (Sync)
            {
                delay = nextAllowedUtc - now;
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            lock (Sync)
            {
                nextAllowedUtc = DateTimeOffset.UtcNow
                    .Add(DoubanMetadataService.MinimumRequestInterval)
                    .AddMilliseconds(Random.Shared.Next(3000, 8000));
            }
        }

        public void Pause(TimeSpan duration)
        {
            try
            {
                var directory = Path.GetDirectoryName(cooldownFilePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(
                    cooldownFilePath,
                    DateTimeOffset.UtcNow.Add(duration).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            }
            catch
            {
                // 冷却持久化失败不影响主流程（仍受进程内最小间隔限制）。
            }
        }

        private DateTimeOffset LoadCooldownUntil()
        {
            try
            {
                if (!File.Exists(cooldownFilePath))
                {
                    return DateTimeOffset.MinValue;
                }
                if (long.TryParse(File.ReadAllText(cooldownFilePath).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
                {
                    return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
                }
            }
            catch
            {
                return DateTimeOffset.MinValue;
            }
            return DateTimeOffset.MinValue;
        }
    }
}
