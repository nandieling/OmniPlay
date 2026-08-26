using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.Tmdb;

public sealed class TmdbMetadataClient : ITmdbMetadataClient
{
    private const string ApiBaseUrl = "https://api.themoviedb.org/3";
    private const string ImageBaseUrl = "https://image.tmdb.org/t/p/w500";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient httpClient;
    private readonly IStoragePaths storagePaths;
    private readonly SemaphoreSlim apiRequestGate = new(1, 1);
    private DateTimeOffset lastApiRequestAt = DateTimeOffset.MinValue;

    public TmdbMetadataClient(HttpClient httpClient, IStoragePaths storagePaths)
    {
        this.httpClient = httpClient;
        this.storagePaths = storagePaths;
        if (this.httpClient.Timeout == Timeout.InfiniteTimeSpan)
        {
            this.httpClient.Timeout = TimeSpan.FromSeconds(15);
        }

        if (this.httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            this.httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("OmniPlay.NAS/0.1");
        }
    }

    public async Task<TmdbMetadataMatch?> SearchAsync(
        string mediaType,
        string title,
        string? year,
        TmdbSettings settings,
        string? secondaryQuery = null,
        CancellationToken cancellationToken = default)
    {
        var candidates = await SearchCandidatesAsync(
            mediaType,
            title,
            year,
            settings,
            secondaryQuery,
            limit: 1,
            cancellationToken);
        return candidates.FirstOrDefault();
    }

    public async Task<TmdbConnectionTestResult> TestConnectionAsync(
        TmdbSettings settings,
        CancellationToken cancellationToken = default)
    {
        var credentials = ResolveCredentialChain(settings).ToArray();
        if (credentials.Length == 0)
        {
            return new TmdbConnectionTestResult(false, "未配置", null, "未启用可用的 TMDB API 源。");
        }

        TmdbConnectionTestResult? lastResult = null;
        foreach (var credential in credentials)
        {
            try
            {
                using var response = await SendApiRequestAsync(
                    "/configuration",
                    new Dictionary<string, string?>(),
                    credential,
                    cancellationToken);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    await DelayForRetryAfterAsync(response, cancellationToken);
                    using var retryResponse = await SendApiRequestAsync(
                        "/configuration",
                        new Dictionary<string, string?>(),
                        credential,
                        cancellationToken);
                    if (retryResponse.IsSuccessStatusCode)
                    {
                        return new TmdbConnectionTestResult(true, credential.Source, (int)retryResponse.StatusCode, "TMDB 连接正常。");
                    }

                    lastResult = new TmdbConnectionTestResult(
                        false,
                        credential.Source,
                        (int)retryResponse.StatusCode,
                        $"TMDB 返回 HTTP {(int)retryResponse.StatusCode}。");
                    continue;
                }

                if (response.IsSuccessStatusCode)
                {
                    return new TmdbConnectionTestResult(true, credential.Source, (int)response.StatusCode, "TMDB 连接正常。");
                }

                lastResult = new TmdbConnectionTestResult(
                    false,
                    credential.Source,
                    (int)response.StatusCode,
                    $"TMDB 返回 HTTP {(int)response.StatusCode}。");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastResult = new TmdbConnectionTestResult(false, credential.Source, null, $"TMDB 连接失败：{ex.Message}");
            }
        }

        return lastResult ?? new TmdbConnectionTestResult(false, "未配置", null, "TMDB 连接失败。");
    }

    public async Task<IReadOnlyList<TmdbMetadataMatch>> SearchCandidatesAsync(
        string mediaType,
        string title,
        string? year,
        TmdbSettings settings,
        string? secondaryQuery = null,
        int limit = 8,
        CancellationToken cancellationToken = default)
    {
        if (!ResolveCredentialChain(settings).Any() || string.IsNullOrWhiteSpace(title))
        {
            return [];
        }

        var normalizedType = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
        var languageOrder = BuildSearchLanguageOrder(settings.Language);
        var mergedById = new Dictionary<int, (TmdbSearchItem Item, int LanguageRank)>();
        var queryTerms = BuildSearchQueryTerms(title, secondaryQuery);

        foreach (var queryTerm in queryTerms)
        {
            for (var languageIndex = 0; languageIndex < languageOrder.Count; languageIndex++)
            {
                var language = languageOrder[languageIndex];
                for (var page = 1; page <= 2; page++)
                {
                    var query = BuildSearchQuery(queryTerm, language, page);
                    var payload = await GetJsonWithFallbackAsync<TmdbSearchResponse>(
                        $"/search/{normalizedType}",
                        query,
                        settings,
                        cancellationToken);

                    if (payload?.Results is null)
                    {
                        continue;
                    }

                    foreach (var item in payload.Results.Where(static item => item.Id > 0))
                    {
                        if (mergedById.TryGetValue(item.Id, out var existing))
                        {
                            mergedById[item.Id] = languageIndex < existing.LanguageRank
                                ? (MergeLocalized(preferred: item, fallback: existing.Item), languageIndex)
                                : (MergeLocalized(preferred: existing.Item, fallback: item), existing.LanguageRank);
                        }
                        else
                        {
                            mergedById[item.Id] = (item, languageIndex);
                        }
                    }
                }
            }
        }

        if (mergedById.Count == 0)
        {
            return [];
        }

        var ordered = mergedById.Values
            .Select(static value => value.Item)
            .OrderByDescending(item => Score(item, title, secondaryQuery, year, normalizedType))
            .Take(Math.Clamp(limit, 1, 20))
            .ToArray();

        var localized = new List<TmdbMetadataMatch>(ordered.Length);
        foreach (var item in ordered)
        {
            var refined = HasChineseDisplayTitle(item, normalizedType)
                ? item
                : await FetchLocalizedDetailIfNeededAsync(item, normalizedType, settings, cancellationToken);
            localized.Add(ToMatch(refined, normalizedType));
        }

        return localized;
    }

    public async Task<string?> DownloadPosterAsync(
        string posterPath,
        string mediaType,
        int tmdbId,
        CancellationToken cancellationToken = default)
    {
        var safeType = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
        return await DownloadImageAsync(
            posterPath,
            storagePaths.PostersDirectory,
            $"{safeType}-{tmdbId}",
            cancellationToken);
    }

    public async Task<string?> DownloadStillAsync(
        string stillPath,
        int tvTmdbId,
        int seasonNumber,
        int episodeNumber,
        CancellationToken cancellationToken = default)
    {
        return await DownloadImageAsync(
            stillPath,
            storagePaths.ThumbnailsDirectory,
            $"tv-{tvTmdbId}-s{seasonNumber:00}e{episodeNumber:00}",
            cancellationToken);
    }

    private async Task<string?> DownloadImageAsync(
        string imagePath,
        string destinationDirectory,
        string filePrefix,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        storagePaths.EnsureCreated();
        var normalizedImagePath = imagePath.StartsWith('/') ? imagePath : "/" + imagePath;
        var extension = Path.GetExtension(normalizedImagePath);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8)
        {
            extension = ".jpg";
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedImagePath)))[..12].ToLowerInvariant();
        var destination = Path.Combine(destinationDirectory, $"{filePrefix}-{hash}{extension}");
        if (File.Exists(destination) && new FileInfo(destination).Length > 0)
        {
            return destination;
        }

        var url = ImageBaseUrl + normalizedImagePath;
        await using var stream = await httpClient.GetStreamAsync(url, cancellationToken);
        await using var file = File.Create(destination);
        await stream.CopyToAsync(file, cancellationToken);
        return destination;
    }

    public async Task<TmdbMetadataMatch?> GetDetailsAsync(
        string mediaType,
        int tmdbId,
        TmdbSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!ResolveCredentialChain(settings).Any() || tmdbId <= 0)
        {
            return null;
        }

        var normalizedType = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
        var language = ResolveChineseLanguage(settings.Language);
        var payload = await GetJsonWithFallbackAsync<TmdbSearchItem>(
            $"/{normalizedType}/{tmdbId}",
            new Dictionary<string, string?> { ["language"] = language },
            settings,
            cancellationToken);
        if (payload is null || payload.Id <= 0)
        {
            return null;
        }

        if (!HasChineseDisplayTitle(payload, normalizedType))
        {
            payload = await ApplyChineseTranslationFallbackAsync(payload, normalizedType, settings, cancellationToken);
        }

        return ToMatch(payload, normalizedType);
    }

    public async Task<TmdbSeasonDetail?> GetSeasonAsync(
        int tvTmdbId,
        int seasonNumber,
        TmdbSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!ResolveCredentialChain(settings).Any() || tvTmdbId <= 0 || seasonNumber < 0)
        {
            return null;
        }

        var language = string.IsNullOrWhiteSpace(settings.Language) ? "zh-CN" : settings.Language;
        var payload = await GetJsonWithFallbackAsync<TmdbSeasonResponse>(
            $"/tv/{tvTmdbId}/season/{seasonNumber}",
            new Dictionary<string, string?> { ["language"] = language },
            settings,
            cancellationToken);
        if (payload is null)
        {
            return null;
        }

        return new TmdbSeasonDetail(
            payload.SeasonNumber ?? seasonNumber,
            ChineseTextNormalizer.NormalizeNullable(payload.Name),
            ChineseTextNormalizer.NormalizeNullable(payload.Overview),
            payload.AirDate,
            payload.PosterPath,
            payload.Episodes?
                .Where(static episode => episode.EpisodeNumber > 0)
                .Select(static episode => new TmdbEpisodeDetail(
                    episode.EpisodeNumber,
                    ChineseTextNormalizer.NormalizeNullable(episode.Name),
                    ChineseTextNormalizer.NormalizeNullable(episode.Overview),
                    episode.AirDate,
                    episode.StillPath))
                .ToArray() ?? []);
    }

    private async Task<T?> GetJsonWithFallbackAsync<T>(
        string path,
        IReadOnlyDictionary<string, string?> query,
        TmdbSettings settings,
        CancellationToken cancellationToken)
    {
        foreach (var credential in ResolveCredentialChain(settings))
        {
            using var response = await SendApiRequestAsync(path, query, credential, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                await DelayForRetryAfterAsync(response, cancellationToken);
                using var retryResponse = await SendApiRequestAsync(path, query, credential, cancellationToken);
                if (retryResponse.IsSuccessStatusCode)
                {
                    await using var retryStream = await retryResponse.Content.ReadAsStreamAsync(cancellationToken);
                    return await JsonSerializer.DeserializeAsync<T>(retryStream, JsonOptions, cancellationToken);
                }

                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
        }

        return default;
    }

    private async Task<HttpResponseMessage> SendApiRequestAsync(
        string path,
        IReadOnlyDictionary<string, string?> query,
        TmdbCredential credential,
        CancellationToken cancellationToken)
    {
        await ApplyApiRateLimitAsync(cancellationToken);
        var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var url = $"{ApiBaseUrl}{path}{separator}{BuildQueryString(query, credential)}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(credential.BearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.BearerToken);
        }

        return await httpClient.SendAsync(request, cancellationToken);
    }

    private async Task ApplyApiRateLimitAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(100);
        await apiRequestGate.WaitAsync(cancellationToken);
        try
        {
            var elapsed = DateTimeOffset.UtcNow - lastApiRequestAt;
            if (elapsed < interval)
            {
                await Task.Delay(interval - elapsed, cancellationToken);
            }

            lastApiRequestAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            apiRequestGate.Release();
        }
    }

    private static async Task DelayForRetryAfterAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
        await Task.Delay(retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromSeconds(2), cancellationToken);
    }

    private async Task<TmdbSearchItem> FetchLocalizedDetailIfNeededAsync(
        TmdbSearchItem item,
        string mediaType,
        TmdbSettings settings,
        CancellationToken cancellationToken)
    {
        var language = ResolveChineseLanguage(settings.Language);
        var detail = await GetJsonWithFallbackAsync<TmdbSearchItem>(
            $"/{mediaType}/{item.Id}",
            new Dictionary<string, string?> { ["language"] = language },
            settings,
            cancellationToken);
        var merged = detail is { Id: > 0 }
            ? MergeLocalized(preferred: detail, fallback: item)
            : item;

        return HasChineseDisplayTitle(merged, mediaType)
            ? merged
            : await ApplyChineseTranslationFallbackAsync(merged, mediaType, settings, cancellationToken);
    }

    private async Task<TmdbSearchItem> ApplyChineseTranslationFallbackAsync(
        TmdbSearchItem item,
        string mediaType,
        TmdbSettings settings,
        CancellationToken cancellationToken)
    {
        var translatedTitle = await FetchChineseTranslatedTitleAsync(mediaType, item.Id, settings, cancellationToken);
        if (string.IsNullOrWhiteSpace(translatedTitle))
        {
            return item;
        }

        var normalizedTitle = ChineseTextNormalizer.NormalizeTitle(translatedTitle);
        return mediaType == "tv"
            ? item with { Name = normalizedTitle }
            : item with { Title = normalizedTitle };
    }

    private async Task<string?> FetchChineseTranslatedTitleAsync(
        string mediaType,
        int id,
        TmdbSettings settings,
        CancellationToken cancellationToken)
    {
        var payload = await GetJsonWithFallbackAsync<TmdbTranslationsResponse>(
            $"/{mediaType}/{id}/translations",
            new Dictionary<string, string?>(),
            settings,
            cancellationToken);
        var zhTranslations = payload?.Translations?
            .Where(static item => string.Equals(item.Iso6391, "zh", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (zhTranslations is null || zhTranslations.Length == 0)
        {
            return null;
        }

        string[] preferredRegions = ["CN", "SG", "TW", "HK"];
        foreach (var region in preferredRegions)
        {
            var title = zhTranslations
                .Where(item => string.Equals(item.Iso31661, region, StringComparison.OrdinalIgnoreCase))
                .Select(static item => PickTranslationTitle(item.Data))
                .FirstOrDefault(ContainsHan);
            if (!string.IsNullOrWhiteSpace(title))
            {
                return ChineseTextNormalizer.NormalizeTitle(title);
            }
        }

        var fallbackTitle = zhTranslations
            .Select(static item => PickTranslationTitle(item.Data))
            .FirstOrDefault(ContainsHan);
        return string.IsNullOrWhiteSpace(fallbackTitle)
            ? null
            : ChineseTextNormalizer.NormalizeTitle(fallbackTitle);
    }

    private static IEnumerable<TmdbCredential> ResolveCredentialChain(TmdbSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.CustomAccessToken))
        {
            yield return new TmdbCredential(null, settings.CustomAccessToken.Trim(), "自定义 Access Token");
        }

        if (!string.IsNullOrWhiteSpace(settings.CustomApiKey))
        {
            yield return new TmdbCredential(settings.CustomApiKey.Trim(), null, "自定义 API Key");
        }

        var envToken = Environment.GetEnvironmentVariable("OMNIPLAY_TMDB_ACCESS_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            yield return new TmdbCredential(null, envToken.Trim(), "环境变量 Access Token");
        }

        var envKey = Environment.GetEnvironmentVariable("OMNIPLAY_TMDB_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            yield return new TmdbCredential(envKey.Trim(), null, "环境变量 API Key");
        }
    }

    private static string BuildQueryString(IReadOnlyDictionary<string, string?> values, TmdbCredential credential)
    {
        var pairs = values
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}")
            .ToList();

        if (!string.IsNullOrWhiteSpace(credential.ApiKey))
        {
            pairs.Add($"api_key={Uri.EscapeDataString(credential.ApiKey)}");
        }

        return string.Join('&', pairs);
    }

    private static Dictionary<string, string?> BuildSearchQuery(
        string title,
        string language,
        int page)
    {
        return new Dictionary<string, string?>
        {
            ["query"] = title,
            ["language"] = language,
            ["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["include_adult"] = "false"
        };
    }

    private static IReadOnlyList<string> BuildSearchQueryTerms(string title, string? secondaryQuery)
    {
        List<string> terms = [];
        AddSearchTerm(terms, title);
        AddSearchTerm(terms, secondaryQuery);
        foreach (var alias in KnownSearchAliases(title).Concat(KnownSearchAliases(secondaryQuery ?? string.Empty)))
        {
            AddSearchTerm(terms, alias);
        }

        var normalizedTitle = title.Trim();
        if (normalizedTitle.EndsWith(" Series", StringComparison.OrdinalIgnoreCase))
        {
            AddSearchTerm(terms, normalizedTitle[..^" Series".Length]);
        }

        if (normalizedTitle.Contains(" AKA ", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var part in Regex.Split(normalizedTitle, @"(?i)\s+A\.?K\.?A\.?\s+"))
            {
                AddSearchTerm(terms, part);
            }
        }

        return terms
            .Select(static term => term.Trim())
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
    }

    private static void AddSearchTerm(List<string> terms, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            terms.Add(value.Trim());
        }
    }

    private static IEnumerable<string> KnownSearchAliases(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var key = NormalizeTitle(value);
        if (key.StartsWith("hayatenogotoku", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("hayatethecombatbutler", StringComparison.OrdinalIgnoreCase))
        {
            yield return "旋风管家";
            yield break;
        }

        switch (key)
        {
            case "逆战救兵":
                yield return "1917";
                break;
            case "parasite":
                yield return "寄生虫";
                break;
            case "寄生虫":
                yield return "Parasite";
                break;
            case "theglory":
                yield return "黑暗荣耀";
                break;
            case "黑暗荣耀":
                yield return "The Glory";
                break;
            case "strangerthings":
                yield return "怪奇物语";
                break;
            case "怪奇物语":
                yield return "Stranger Things";
                break;
            case "旋风管家":
                yield return "Hayate the Combat Butler";
                yield return "Hayate no Gotoku";
                break;
        }
    }

    private static IReadOnlyList<string> BuildSearchLanguageOrder(string? configuredLanguage)
    {
        var preferred = ResolveChineseLanguage(configuredLanguage);
        List<string> languages = [preferred, "en-US"];
        return languages
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveChineseLanguage(string? configuredLanguage)
    {
        return "zh-CN";
    }

    private static double Score(TmdbSearchItem item, string query, string? secondaryQuery, string? year, string mediaType)
    {
        var rawCandidateTitles = MatchableTitles(item, mediaType)
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var candidateTitles = rawCandidateTitles.Select(NormalizeTitle)
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedQuery = NormalizeTitle(query);
        var score = Math.Min(item.Popularity ?? 0, 100) + (item.VoteAverage ?? 0);
        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            score += BestTitleScore(candidateTitles, normalizedQuery, exact: 12000, candidateContainsQuery: 3600, queryContainsCandidate: 1800);
        }

        var normalizedSecondaryQuery = NormalizeTitle(secondaryQuery ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(normalizedSecondaryQuery) &&
            !string.Equals(normalizedSecondaryQuery, normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            score += BestTitleScore(candidateTitles, normalizedSecondaryQuery, exact: 9000, candidateContainsQuery: 2600, queryContainsCandidate: 1200);
        }

        foreach (var aliasQuery in KnownSearchAliases(query).Concat(KnownSearchAliases(secondaryQuery ?? string.Empty)))
        {
            var normalizedAliasQuery = NormalizeTitle(aliasQuery);
            if (!string.IsNullOrWhiteSpace(normalizedAliasQuery) &&
                !string.Equals(normalizedAliasQuery, normalizedQuery, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(normalizedAliasQuery, normalizedSecondaryQuery, StringComparison.OrdinalIgnoreCase))
            {
                score += BestTitleScore(candidateTitles, normalizedAliasQuery, exact: 11000, candidateContainsQuery: 3200, queryContainsCandidate: 1600);
            }
        }

        var date = mediaType == "tv" ? item.FirstAirDate : item.ReleaseDate;
        if (!string.IsNullOrWhiteSpace(year) &&
            int.TryParse(year, out var targetYear))
        {
            if (date?.Length >= 4 && int.TryParse(date[..4], out var candidateYear))
            {
                var diff = Math.Abs(candidateYear - targetYear);
                if (string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase))
                {
                    score += diff switch
                    {
                        0 => 6000,
                        1 => 3000,
                        >= 10 => -12000,
                        >= 3 => -6000,
                        _ => 0
                    };
                }
                else
                {
                    score += diff switch
                    {
                        0 => 10000,
                        1 => 1500,
                        >= 10 => -30000,
                        >= 3 => -15000,
                        _ => -4000
                    };
                }
            }
            else
            {
                score -= string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? 2500 : 8000;
            }
        }

        if (!ContainsVariantSemantics(query) &&
            (string.IsNullOrWhiteSpace(secondaryQuery) || !ContainsVariantSemantics(secondaryQuery)) &&
            rawCandidateTitles.Any(ContainsVariantSemantics))
        {
            score -= 5000;
        }

        return score;
    }

    private static double BestTitleScore(
        IReadOnlyList<string> candidateTitles,
        string normalizedQuery,
        double exact,
        double candidateContainsQuery,
        double queryContainsCandidate)
    {
        if (candidateTitles.Any(candidate => string.Equals(candidate, normalizedQuery, StringComparison.OrdinalIgnoreCase)))
        {
            return exact;
        }

        if (candidateTitles.Any(candidate => candidate.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)))
        {
            return candidateContainsQuery;
        }

        if (candidateTitles.Any(candidate => normalizedQuery.Contains(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            return queryContainsCandidate;
        }

        return 0;
    }

    private static bool ContainsVariantSemantics(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Regex.IsMatch(
            value,
            @"(?i)(taylor'?s\s+version|pumped\s+up|version|remix|remaster(?:ed)?|redux|recut|extended|director'?s\s+cut|special\s+edition|anniversary|part\s+\d+|season\s+\d+|off\s*&\s*monster)",
            RegexOptions.CultureInvariant);
    }

    private static TmdbMetadataMatch ToMatch(TmdbSearchItem item, string mediaType)
    {
        var rawTitle = mediaType == "tv" ? item.Name ?? "未知剧集" : item.Title ?? "未知影片";
        var title = ResolveKnownChineseTitle(item, mediaType) ?? ChineseTextNormalizer.NormalizeTitle(rawTitle);
        return new TmdbMetadataMatch(
            item.Id,
            mediaType,
            title,
            ChineseTextNormalizer.NormalizeNullable(item.Overview),
            mediaType == "tv" ? item.FirstAirDate : item.ReleaseDate,
            item.PosterPath,
            item.VoteAverage,
            item.Popularity);
    }

    private static string? ResolveKnownChineseTitle(TmdbSearchItem item, string mediaType)
    {
        if (mediaType == "movie"
            && item.Id == 496243
            && item.ReleaseDate?.StartsWith("2019", StringComparison.Ordinal) == true)
        {
            return "寄生虫";
        }

        if (mediaType == "movie"
            && item.ReleaseDate?.StartsWith("2019", StringComparison.Ordinal) == true
            && MatchableTitles(item, mediaType).Any(static title => string.Equals(NormalizeTitle(title), "parasite", StringComparison.OrdinalIgnoreCase)))
        {
            return "寄生虫";
        }

        if (mediaType == "movie"
            && item.Id == 530915
            && item.ReleaseDate?.StartsWith("2019", StringComparison.Ordinal) == true)
        {
            return "1917";
        }

        if (mediaType == "tv")
        {
            if (MatchableTitles(item, mediaType).Any(static title =>
                {
                    var key = NormalizeTitle(title);
                    return key.StartsWith("hayatenogotoku", StringComparison.OrdinalIgnoreCase)
                           || key.StartsWith("hayatethecombatbutler", StringComparison.OrdinalIgnoreCase);
                }))
            {
                return "旋风管家";
            }

            if (item.Id == 66732 || MatchableTitles(item, mediaType).Any(static title => string.Equals(NormalizeTitle(title), "strangerthings", StringComparison.OrdinalIgnoreCase)))
            {
                return "怪奇物语";
            }

            if (item.Id == 136283 || MatchableTitles(item, mediaType).Any(static title => string.Equals(NormalizeTitle(title), "theglory", StringComparison.OrdinalIgnoreCase)))
            {
                return "黑暗荣耀";
            }
        }

        return null;
    }

    private static TmdbSearchItem MergeLocalized(TmdbSearchItem preferred, TmdbSearchItem fallback)
    {
        return preferred with
        {
            Title = FirstNonEmpty(preferred.Title, fallback.Title),
            Name = FirstNonEmpty(preferred.Name, fallback.Name),
            OriginalTitle = FirstNonEmpty(preferred.OriginalTitle, fallback.OriginalTitle),
            OriginalName = FirstNonEmpty(preferred.OriginalName, fallback.OriginalName),
            Overview = FirstNonEmpty(preferred.Overview, fallback.Overview),
            ReleaseDate = FirstNonEmpty(preferred.ReleaseDate, fallback.ReleaseDate),
            FirstAirDate = FirstNonEmpty(preferred.FirstAirDate, fallback.FirstAirDate),
            PosterPath = FirstNonEmpty(preferred.PosterPath, fallback.PosterPath),
            VoteAverage = preferred.VoteAverage ?? fallback.VoteAverage,
            Popularity = preferred.Popularity ?? fallback.Popularity
        };
    }

    private static IEnumerable<string> MatchableTitles(TmdbSearchItem item, string mediaType)
    {
        if (mediaType == "tv")
        {
            yield return item.Name ?? string.Empty;
            yield return item.OriginalName ?? string.Empty;
            yield return item.Title ?? string.Empty;
            yield return item.OriginalTitle ?? string.Empty;
        }
        else
        {
            yield return item.Title ?? string.Empty;
            yield return item.OriginalTitle ?? string.Empty;
            yield return item.Name ?? string.Empty;
            yield return item.OriginalName ?? string.Empty;
        }
    }

    private static bool HasChineseDisplayTitle(TmdbSearchItem item, string mediaType)
    {
        return ContainsHan(mediaType == "tv" ? item.Name : item.Title);
    }

    private static bool ContainsHan(string? input)
    {
        return !string.IsNullOrWhiteSpace(input) && Regex.IsMatch(input, @"\p{IsCJKUnifiedIdeographs}");
    }

    private static string? PickTranslationTitle(TmdbTranslationData? data)
    {
        return FirstNonEmpty(data?.Title, data?.Name);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values
            .Select(static value => value?.Trim())
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }

    private static string NormalizeTitle(string value)
    {
        return value
            .Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private sealed record TmdbCredential(
        string? ApiKey,
        string? BearerToken,
        string Source);

    private sealed record TmdbSearchResponse(
        [property: JsonPropertyName("results")] IReadOnlyList<TmdbSearchItem>? Results);

    private sealed record TmdbSearchItem(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("original_title")] string? OriginalTitle,
        [property: JsonPropertyName("original_name")] string? OriginalName,
        [property: JsonPropertyName("overview")] string? Overview,
        [property: JsonPropertyName("release_date")] string? ReleaseDate,
        [property: JsonPropertyName("first_air_date")] string? FirstAirDate,
        [property: JsonPropertyName("poster_path")] string? PosterPath,
        [property: JsonPropertyName("vote_average")] double? VoteAverage,
        [property: JsonPropertyName("popularity")] double? Popularity);

    private sealed record TmdbTranslationsResponse(
        [property: JsonPropertyName("translations")] IReadOnlyList<TmdbTranslationItem>? Translations);

    private sealed record TmdbTranslationItem(
        [property: JsonPropertyName("iso_639_1")] string? Iso6391,
        [property: JsonPropertyName("iso_3166_1")] string? Iso31661,
        [property: JsonPropertyName("data")] TmdbTranslationData? Data);

    private sealed record TmdbTranslationData(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record TmdbSeasonResponse(
        [property: JsonPropertyName("season_number")] int? SeasonNumber,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("overview")] string? Overview,
        [property: JsonPropertyName("air_date")] string? AirDate,
        [property: JsonPropertyName("poster_path")] string? PosterPath,
        [property: JsonPropertyName("episodes")] IReadOnlyList<TmdbSeasonEpisodeResponse>? Episodes);

    private sealed record TmdbSeasonEpisodeResponse(
        [property: JsonPropertyName("episode_number")] int EpisodeNumber,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("overview")] string? Overview,
        [property: JsonPropertyName("air_date")] string? AirDate,
        [property: JsonPropertyName("still_path")] string? StillPath);
}
