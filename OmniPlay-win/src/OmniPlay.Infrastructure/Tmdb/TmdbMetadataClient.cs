using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models.Library;
using OmniPlay.Core.Models;
using OmniPlay.Core.Settings;

namespace OmniPlay.Infrastructure.Tmdb;

public sealed class TmdbMetadataClient : ITmdbMetadataClient, ITmdbConnectionTester
{
    private const string TmdbApiBaseUrl = "https://api.themoviedb.org/3";
    private const string TmdbImageBaseUrl = "https://image.tmdb.org/t/p/w500";
    private const int DefaultSearchQueryLimit = 4;
    private const int DefaultSearchCandidateLimit = 12;
    private const int DefaultSearchPagesPerLanguage = 2;
    private const int DefaultSeasonProbeLimit = 12;
    private const int DefaultLocalizationProbeLimit = 12;
    private static readonly string[] ChineseTranslationRegionOrder = ["CN", "SG", "TW", "HK"];
    private static readonly Regex ConsecutiveWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex TokenSeparatorRegex = new(@"[._\-]+", RegexOptions.Compiled);
    private static readonly Regex SequelRegex = new(
        @"(?ix)
          (续集|第\s*[二三四五六七八九十两\d]+\s*(部|季))
          |
          \b(part\s*(2|ii|iii|iv|v)|part(2|ii|iii|iv|v)|season\s*\d+|season\d+|s\d{1,2})\b",
        RegexOptions.Compiled);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient;
    private readonly IStoragePaths storagePaths;
    private readonly ISettingsService settingsService;
    private readonly ConcurrentDictionary<int, int?> tvSeasonCountCache = new();
    private readonly ConcurrentDictionary<int, IReadOnlyList<TmdbSeasonSummary>> tvSeasonSummaryCache = new();
    private readonly ConcurrentDictionary<string, int?> tvSeasonAirYearCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TmdbRequestLimiter> rateLimiters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TmdbCredits?> creditsCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyList<TmdbFilmographyItem>?> filmographyCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string?> chineseTitleCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TmdbSearchItem> localizedResultCache = new(StringComparer.Ordinal);

    public TmdbMetadataClient(HttpClient httpClient, IStoragePaths storagePaths, ISettingsService settingsService)
    {
        this.httpClient = httpClient;
        this.storagePaths = storagePaths;
        this.settingsService = settingsService;

        if (this.httpClient.Timeout == Timeout.InfiniteTimeSpan)
        {
            this.httpClient.Timeout = TimeSpan.FromSeconds(12);
        }

        if (this.httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            this.httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("OmniPlay.Windows/0.1");
        }
    }

    public Task<TmdbMetadataMatch?> SearchMovieAsync(
        IReadOnlyList<string> candidateTitles,
        string? year,
        CancellationToken cancellationToken = default,
        TmdbSearchOptions? options = null)
    {
        return SearchBestMatchAsync("movie", candidateTitles, year, options, cancellationToken);
    }

    public Task<IReadOnlyList<TmdbMetadataMatch>> SearchMovieCandidatesAsync(
        IReadOnlyList<string> candidateTitles,
        string? year,
        CancellationToken cancellationToken = default,
        TmdbSearchOptions? options = null)
    {
        return SearchMatchesAsync("movie", candidateTitles, year, options, cancellationToken);
    }

    public Task<TmdbMetadataMatch?> SearchTvShowAsync(
        IReadOnlyList<string> candidateTitles,
        string? year,
        CancellationToken cancellationToken = default,
        TmdbSearchOptions? options = null)
    {
        return SearchBestMatchAsync("tv", candidateTitles, year, options, cancellationToken);
    }

    public Task<IReadOnlyList<TmdbMetadataMatch>> SearchTvShowCandidatesAsync(
        IReadOnlyList<string> candidateTitles,
        string? year,
        CancellationToken cancellationToken = default,
        TmdbSearchOptions? options = null)
    {
        return SearchMatchesAsync("tv", candidateTitles, year, options, cancellationToken);
    }

    public async Task<TmdbCredits?> FetchCreditsAsync(
        int tmdbId,
        string mediaType,
        CancellationToken cancellationToken = default)
    {
        var kind = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
        var cacheKey = kind + "-" + tmdbId;
        if (creditsCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var configurations = await ResolveConfigurationsAsync(null, cancellationToken);
        var configuration = configurations.Primary;
        if (!configuration.IsConfigured)
        {
            return null;
        }

        var path = $"/{kind}/{tmdbId}/credits?language={Uri.EscapeDataString(configuration.Language)}";
        using var response = await SendTmdbApiRequestAsync(path, configuration, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<TmdbCreditsResponse>(stream, SerializerOptions, cancellationToken);
        if (payload is null)
        {
            return null;
        }

        // 导演取全部 director（剧集再加 creator），主演取排序靠前的演员；
        // 与 mac 版一致：按名字去重（大小写不敏感），导演最多 5 人、主演最多 8 人。
        var directorJobs = kind == "tv"
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "director", "creator", "created by" }
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "director" };

        var directors = CollectPeople(payload.Crew ?? [], person =>
            directorJobs.Contains(person.Job ?? string.Empty) ||
            (kind == "tv" && string.Equals(person.Department, "Created By", StringComparison.OrdinalIgnoreCase)),
            limit: 5);
        var casts = CollectPeople(payload.Cast ?? [], _ => true, limit: 8);

        var credits = directors.Count == 0 && casts.Count == 0
            ? null
            : new TmdbCredits(directors, casts);
        creditsCache[cacheKey] = credits;
        return credits;
    }

    private static List<TmdbPersonInfo> CollectPeople(
        IReadOnlyList<TmdbCreditsPerson> people,
        Func<TmdbCreditsPerson, bool> accept,
        int limit)
    {
        List<TmdbPersonInfo> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (var person in people)
        {
            if (result.Count >= limit)
            {
                break;
            }

            if (!accept(person))
            {
                continue;
            }

            var name = person.Name?.Trim();
            if (string.IsNullOrEmpty(name) || !seen.Add(name))
            {
                continue;
            }

            result.Add(new TmdbPersonInfo(person.Id ?? 0, name, person.ProfilePath));
        }

        return result;
    }

    public async Task<TmdbConnectionTestResult> TestConnectionAsync(
        TmdbSettings settings,
        CancellationToken cancellationToken = default)
    {
        var configurations = await ResolveConfigurationsAsync(settings, cancellationToken);
        var configuration = configurations.Primary;
        if (!configuration.IsConfigured)
        {
            return new TmdbConnectionTestResult(false, "未配置 TMDB API Key 或 Read Access Token。");
        }

        if (TmdbProxyGateway.HasInvalidInput(configuration.ProxyUrl))
        {
            return new TmdbConnectionTestResult(false, "TMDB 代理网关地址无效。");
        }

        try
        {
            var result = await TestConnectionWithConfigurationAsync(configuration, cancellationToken);
            if (result.Success)
            {
                return new TmdbConnectionTestResult(true, result.Message);
            }

            return new TmdbConnectionTestResult(false, result.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TmdbConnectionTestResult(false, "连接 TMDB 超时。");
        }
        catch (HttpRequestException ex)
        {
            return new TmdbConnectionTestResult(false, BuildConnectionFailureMessage(ex));
        }
    }

    public async Task<string?> DownloadPosterAsync(
        string posterPath,
        string mediaType,
        int tmdbId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(posterPath))
        {
            return null;
        }

        if (Path.IsPathRooted(posterPath) && File.Exists(posterPath))
        {
            return posterPath;
        }

        var posterUri = ResolvePosterUri(posterPath);
        if (posterUri is null)
        {
            return null;
        }

        storagePaths.EnsureCreated();

        var extension = Path.GetExtension(posterUri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8)
        {
            extension = ".jpg";
        }

        var safeMediaType = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase)
            ? "tv"
            : "movie";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(posterPath)))[..12].ToLowerInvariant();
        var destinationPath = Path.Combine(storagePaths.PostersDirectory, $"{safeMediaType}-{tmdbId}-{hash}{extension}");

        if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
        {
            return destinationPath;
        }

        await DownloadBinaryAsync(posterUri, destinationPath, cancellationToken);
        return destinationPath;
    }

    public async Task<string?> DownloadProfileImageAsync(
        string profilePath,
        int personId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profilePath))
        {
            return null;
        }

        if (Path.IsPathRooted(profilePath) && File.Exists(profilePath))
        {
            return profilePath;
        }

        Uri? profileUri = ResolvePosterUri(profilePath);
        if (profileUri is null)
        {
            return null;
        }

        storagePaths.EnsureCreated();

        var extension = Path.GetExtension(profileUri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8)
        {
            extension = ".jpg";
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profilePath)))[..12].ToLowerInvariant();
        var destinationPath = Path.Combine(storagePaths.PostersDirectory, $"person-{Math.Max(personId, 0)}-{hash}{extension}");
        if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
        {
            return destinationPath;
        }

        await DownloadBinaryAsync(profileUri, destinationPath, cancellationToken);
        return destinationPath;
    }

    public async Task<string?> DownloadEpisodeStillAsync(
        int tmdbShowId,
        int seasonNumber,
        int episodeNumber,
        string videoFileId,
        CancellationToken cancellationToken = default)
    {
        if (tmdbShowId <= 0 || seasonNumber <= 0 || episodeNumber <= 0 || string.IsNullOrWhiteSpace(videoFileId))
        {
            return null;
        }

        storagePaths.EnsureCreated();
        var destinationPath = Path.Combine(storagePaths.ThumbnailsDirectory, $"{videoFileId}.jpg");
        if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
        {
            return destinationPath;
        }

        var configurations = await ResolveConfigurationsAsync(null, cancellationToken);
        var configuration = configurations.Primary;
        if (!configuration.IsConfigured)
        {
            return null;
        }

        var stillPath = await FetchMappedEpisodeStillPathAsync(
            tmdbShowId,
            seasonNumber,
            episodeNumber,
            configuration,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(stillPath))
        {
            return null;
        }

        var stillUri = ResolvePosterUri(stillPath);
        if (stillUri is null)
        {
            return null;
        }

        await DownloadBinaryAsync(stillUri, destinationPath, cancellationToken);
        return destinationPath;
    }

    private async Task<string?> FetchMappedEpisodeStillPathAsync(
        int tmdbShowId,
        int seasonNumber,
        int episodeNumber,
        TmdbResolvedConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var candidates = await ResolveEpisodeStillCandidatesAsync(
            tmdbShowId,
            seasonNumber,
            episodeNumber,
            configuration,
            cancellationToken);
        foreach (var candidate in candidates)
        {
            var stillPath = await FetchEpisodeStillPathAsync(
                tmdbShowId,
                candidate.Season,
                candidate.Episode,
                configuration,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(stillPath))
            {
                return stillPath;
            }
        }

        return null;
    }

    private async Task<string?> FetchEpisodeStillPathAsync(
        int tmdbShowId,
        int seasonNumber,
        int episodeNumber,
        TmdbResolvedConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var relativePath = $"/tv/{tmdbShowId}/season/{seasonNumber}/episode/{episodeNumber}?language={Uri.EscapeDataString(configuration.Language)}";
        using var response = await SendTmdbApiRequestAsync(relativePath, configuration, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<TmdbEpisodeDetailResponse>(
            responseStream,
            SerializerOptions,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(payload?.StillPath))
        {
            return payload.StillPath;
        }

        return await FetchEpisodeStillImagePathAsync(
            tmdbShowId,
            seasonNumber,
            episodeNumber,
            configuration,
            cancellationToken);
    }

    private async Task<IReadOnlyList<(int Season, int Episode)>> ResolveEpisodeStillCandidatesAsync(
        int tmdbShowId,
        int requestedSeason,
        int requestedEpisode,
        TmdbResolvedConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (requestedEpisode <= 0)
        {
            return [(requestedSeason, requestedEpisode)];
        }

        var candidates = new List<(int Season, int Episode)> { (requestedSeason, requestedEpisode) };
        var summaries = await FetchTvSeasonSummariesAsync(tmdbShowId, configuration, cancellationToken);
        var regularSeasons = summaries
            .Where(static summary => summary.SeasonNumber > 0 && summary.EpisodeCount > 0)
            .OrderBy(static summary => summary.SeasonNumber)
            .ThenByDescending(static summary => summary.EpisodeCount)
            .ToList();

        if (regularSeasons.Count == 1 &&
            regularSeasons[0].SeasonNumber != requestedSeason &&
            regularSeasons[0].EpisodeCount >= requestedEpisode)
        {
            AddUniqueEpisodeCandidate(candidates, regularSeasons[0].SeasonNumber, requestedEpisode);
        }

        var seasonOne = regularSeasons.FirstOrDefault(static summary => summary.SeasonNumber == 1);
        if (seasonOne is not null && seasonOne.EpisodeCount >= requestedEpisode)
        {
            AddUniqueEpisodeCandidate(candidates, seasonOne.SeasonNumber, requestedEpisode);
        }

        foreach (var summary in regularSeasons
                     .OrderByDescending(static item => item.EpisodeCount)
                     .ThenBy(static item => item.SeasonNumber)
                     .Where(summary => summary.EpisodeCount >= requestedEpisode))
        {
            AddUniqueEpisodeCandidate(candidates, summary.SeasonNumber, requestedEpisode);
        }

        return candidates;
    }

    private static void AddUniqueEpisodeCandidate(List<(int Season, int Episode)> candidates, int season, int episode)
    {
        if (!candidates.Any(candidate => candidate.Season == season && candidate.Episode == episode))
        {
            candidates.Add((season, episode));
        }
    }

    private async Task<IReadOnlyList<TmdbSeasonSummary>> FetchTvSeasonSummariesAsync(
        int tvId,
        TmdbResolvedConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (tvSeasonSummaryCache.TryGetValue(tvId, out var cached))
        {
            return cached;
        }

        var path = $"/tv/{tvId}?language=en-US";
        using var response = await SendTmdbApiRequestAsync(path, configuration, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            ThrowIfCredentialUnavailable(response.StatusCode, configuration);
            return [];
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<TmdbTvDetailResponse>(
            responseStream,
            SerializerOptions,
            cancellationToken);
        var summaries = payload?.Seasons?
            .Where(static season => season.SeasonNumber.HasValue && season.EpisodeCount.HasValue && season.EpisodeCount.Value > 0)
            .Select(static season => new TmdbSeasonSummary(season.SeasonNumber!.Value, season.EpisodeCount!.Value))
            .ToArray() ?? [];
        tvSeasonSummaryCache[tvId] = summaries;
        if (payload?.NumberOfSeasons is not null)
        {
            tvSeasonCountCache[tvId] = payload.NumberOfSeasons;
        }
        return summaries;
    }

    private async Task<string?> FetchEpisodeStillImagePathAsync(
        int tmdbShowId,
        int seasonNumber,
        int episodeNumber,
        TmdbResolvedConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var imageLanguage = string.Equals(configuration.Language, "en-US", StringComparison.OrdinalIgnoreCase)
            ? "en,null"
            : "zh,null,en";
        var relativePath = $"/tv/{tmdbShowId}/season/{seasonNumber}/episode/{episodeNumber}/images?include_image_language={Uri.EscapeDataString(imageLanguage)}";
        using var response = await SendTmdbApiRequestAsync(relativePath, configuration, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<TmdbEpisodeImagesResponse>(
            responseStream,
            SerializerOptions,
            cancellationToken);
        return payload?.Stills
            .OrderByDescending(static image => (image.VoteAverage ?? 0) + (image.VoteCount ?? 0) * 0.1)
            .Select(static image => image.FilePath)
            .FirstOrDefault(static path => !string.IsNullOrWhiteSpace(path));
    }

    private async Task<TmdbMetadataMatch?> SearchBestMatchAsync(
        string mediaType,
        IReadOnlyList<string> candidateTitles,
        string? year,
        TmdbSearchOptions? options,
        CancellationToken cancellationToken)
    {
        var matches = await SearchMatchesAsync(mediaType, candidateTitles, year, options, cancellationToken);
        return matches.FirstOrDefault();
    }

    private async Task<IReadOnlyList<TmdbMetadataMatch>> SearchMatchesAsync(
        string mediaType,
        IReadOnlyList<string> candidateTitles,
        string? year,
        TmdbSearchOptions? options,
        CancellationToken cancellationToken)
    {
        var queries = DeduplicateQueries(candidateTitles);
        if (queries.Count == 0)
        {
            return [];
        }

        var configurations = await ResolveConfigurationsAsync(options?.SettingsOverride, cancellationToken);
        var configuration = configurations.Primary;
        if (!configuration.IsConfigured)
        {
            return [];
        }

        try
        {
            return await SearchMatchesWithConfigurationAsync(
                mediaType,
                queries,
                year,
                options,
                configuration,
                cancellationToken);
        }
        catch (TmdbSourceUnavailableException)
        {
            return [];
        }
        catch (InvalidTmdbProxyException)
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<TmdbMetadataMatch>> SearchMatchesWithConfigurationAsync(
        string mediaType,
        IReadOnlyList<string> queries,
        string? year,
        TmdbSearchOptions? options,
        TmdbResolvedConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var searchQueryLimit = DefaultSearchQueryLimit;
        var candidateLimit = DefaultSearchCandidateLimit;
        var pageLimit = DefaultSearchPagesPerLanguage;
        var seasonProbeLimit = DefaultSeasonProbeLimit;
        var localizationProbeLimit = DefaultLocalizationProbeLimit;

        var searchContext = BuildSearchContext(mediaType, queries, year, options);
        var languageOrder = ResolveSearchLanguageOrder(configuration.Language);
        Dictionary<int, (TmdbSearchItem Item, int LanguageRank)> merged = [];

        foreach (var query in queries.Take(searchQueryLimit))
        {
            for (var languageIndex = 0; languageIndex < languageOrder.Count; languageIndex++)
            {
                var language = languageOrder[languageIndex];
                for (var page = 1; page <= pageLimit; page++)
                {
                    var searchPage = await FetchSearchPageAsync(
                        mediaType,
                        query,
                        language,
                        page,
                        configuration,
                        cancellationToken);

                    foreach (var item in searchPage.Results)
                    {
                        if (item.Id <= 0 || string.IsNullOrWhiteSpace(item.DisplayTitle))
                        {
                            continue;
                        }

                        if (merged.TryGetValue(item.Id, out var existing))
                        {
                            if (languageIndex < existing.LanguageRank)
                            {
                                merged[item.Id] = (MergeLocalized(item, existing.Item), languageIndex);
                            }
                            else
                            {
                                merged[item.Id] = (MergeLocalized(existing.Item, item), existing.LanguageRank);
                            }
                        }
                        else
                        {
                            merged[item.Id] = (item, languageIndex);
                        }
                    }
                }
            }
        }

        if (merged.Count == 0)
        {
            return [];
        }

        var mergedItems = merged.Values
            .Select(static entry => entry.Item)
            .ToList();

        var seasonCountByTvId = await FetchSeasonCountsAsync(
            mediaType,
            mergedItems,
            searchContext.PreferredSeason,
            seasonProbeLimit,
            configuration,
            cancellationToken);
        var seasonAirYearByTvId = await FetchSeasonAirYearsAsync(
            mediaType,
            mergedItems,
            searchContext.PreferredSeason,
            seasonCountByTvId,
            seasonProbeLimit,
            configuration,
            cancellationToken);

        var ranked = mergedItems
            .Select(item => new
            {
                Item = item,
                Score = Score(item, searchContext, seasonCountByTvId, seasonAirYearByTvId)
            })
            .OrderByDescending(static entry => entry.Score)
            .ThenByDescending(entry => entry.Item.Popularity ?? 0)
            .Select(static entry => entry.Item)
            .Take(candidateLimit)
            .ToList();

        var localized = await LocalizeCandidatesForCurrentLanguageAsync(
            mediaType,
            ranked,
            configuration,
            localizationProbeLimit,
            cancellationToken);

        return localized
            .Take(candidateLimit)
            .Select(item => ToMatch(item, mediaType))
            .ToList();
    }

    private async Task<TmdbSearchResponse> FetchSearchPageAsync(
        string mediaType,
        string query,
        string language,
        int page,
        TmdbResolvedConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var path =
            $"/search/{mediaType}?query={Uri.EscapeDataString(query)}&language={Uri.EscapeDataString(language)}&page={page}";
        using var response = await SendTmdbApiRequestAsync(path, configuration, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            ThrowIfCredentialUnavailable(response.StatusCode, configuration);
            return new TmdbSearchResponse();
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<TmdbSearchResponse>(
            responseStream,
            SerializerOptions,
            cancellationToken);

        return payload ?? new TmdbSearchResponse();
    }

    private async Task<Dictionary<int, int>> FetchSeasonCountsAsync(
        string mediaType,
        IReadOnlyList<TmdbSearchItem> items,
        int? preferredSeason,
        int probeLimit,
        TmdbResolvedConfiguration configuration,
        CancellationToken cancellationToken)
    {
        Dictionary<int, int> seasonCountByTvId = [];
        if (!string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ||
            preferredSeason is null or <= 0)
        {
            return seasonCountByTvId;
        }

        foreach (var tvId in items
                     .OrderByDescending(static item => item.Popularity ?? 0)
                     .Take(probeLimit)
                     .Select(static item => item.Id))
        {
            var seasonCount = await FetchTvSeasonCountAsync(tvId, configuration, cancellationToken);
            if (seasonCount is > 0)
            {
                seasonCountByTvId[tvId] = seasonCount.Value;
            }
        }

        return seasonCountByTvId;
    }

    private async Task<Dictionary<int, int>> FetchSeasonAirYearsAsync(
        string mediaType,
        IReadOnlyList<TmdbSearchItem> items,
        int? preferredSeason,
        IReadOnlyDictionary<int, int> seasonCountByTvId,
        int probeLimit,
        TmdbResolvedConfiguration configuration,
        CancellationToken cancellationToken)
    {
        Dictionary<int, int> seasonAirYearByTvId = [];
        if (!string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ||
            preferredSeason is null or <= 0)
        {
            return seasonAirYearByTvId;
        }

        foreach (var tvId in items
                     .OrderByDescending(static item => item.Popularity ?? 0)
                     .Take(probeLimit)
                     .Select(static item => item.Id))
        {
            if (!seasonCountByTvId.TryGetValue(tvId, out var seasonCount) || seasonCount <= 0)
            {
                continue;
            }

            var mappedSeason = Math.Clamp(preferredSeason.Value, 1, Math.Max(1, seasonCount));
            var airYear = await FetchTvSeasonAirYearAsync(tvId, mappedSeason, configuration, cancellationToken);
            if (airYear is > 0)
            {
                seasonAirYearByTvId[tvId] = airYear.Value;
            }
        }

        return seasonAirYearByTvId;
    }

    private async Task<int?> FetchTvSeasonCountAsync(
        int tvId,
        TmdbResolvedConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (tvSeasonCountCache.TryGetValue(tvId, out var cached))
        {
            return cached;
        }

        var path = $"/tv/{tvId}?language=en-US";
        using var response = await SendTmdbApiRequestAsync(path, configuration, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            ThrowIfCredentialUnavailable(response.StatusCode, configuration);
            return null;
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<TmdbTvDetailResponse>(
            responseStream,
            SerializerOptions,
            cancellationToken);
        tvSeasonCountCache[tvId] = payload?.NumberOfSeasons;
        return payload?.NumberOfSeasons;
    }

    private async Task<int?> FetchTvSeasonAirYearAsync(
        int tvId,
        int season,
        TmdbResolvedConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{tvId}#{season}";
        if (tvSeasonAirYearCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var path = $"/tv/{tvId}/season/{season}?language=en-US";
        using var response = await SendTmdbApiRequestAsync(path, configuration, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            ThrowIfCredentialUnavailable(response.StatusCode, configuration);
            return null;
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<TmdbSeasonDetailResponse>(
            responseStream,
            SerializerOptions,
            cancellationToken);
        var airYear = TryParseYear(payload?.AirDate, out var parsedYear) ? parsedYear : (int?)null;
        tvSeasonAirYearCache[cacheKey] = airYear;
        return airYear;
    }

    private async Task<IReadOnlyList<TmdbSearchItem>> LocalizeCandidatesForCurrentLanguageAsync(
        string mediaType,
        IReadOnlyList<TmdbSearchItem> items,
        TmdbResolvedConfiguration configuration,
        int probeLimit,
        CancellationToken cancellationToken)
    {
        if (!ShouldAttemptChineseLocalization(configuration.Language))
        {
            return items;
        }

        List<TmdbSearchItem> localized = [];
        localized.Capacity = items.Count;

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (index < probeLimit &&
                (!item.HasChineseDisplayTitle || string.IsNullOrWhiteSpace(item.ProductionCountryCodes)))
            {
                try
                {
                    var refined = await FetchLocalizedDetailIfNeededAsync(
                        mediaType,
                        item,
                        configuration.Language,
                        configuration,
                        cancellationToken);
                    localized.Add(refined ?? item);
                    continue;
                }
                catch (TmdbSourceUnavailableException)
                {
                    throw;
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    localized.Add(item);
                    continue;
                }
            }

            localized.Add(item);
        }

        return localized;
    }

    private async Task<TmdbSearchItem?> FetchLocalizedDetailIfNeededAsync(
        string mediaType,
        TmdbSearchItem item,
        string language,
        TmdbResolvedConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(mediaType, "movie", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var cacheKey = $"{mediaType}#{item.Id}#{language}";
        if (localizedResultCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var path = $"/{mediaType}/{item.Id}?language={Uri.EscapeDataString(language)}";
        using var response = await SendTmdbApiRequestAsync(path, configuration, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            ThrowIfCredentialUnavailable(response.StatusCode, configuration);
            return null;
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var detail = await JsonSerializer.DeserializeAsync<TmdbSearchItem>(
            responseStream,
            SerializerOptions,
            cancellationToken);
        if (detail is null)
        {
            return null;
        }

        var merged = MergeLocalized(
            detail with
            {
                Title = SimplifyChineseIfNeeded(detail.Title),
                Name = SimplifyChineseIfNeeded(detail.Name)
            },
            item);

        if (!merged.HasChineseDisplayTitle)
        {
            var translatedChineseTitle = await FetchChineseTranslatedTitleAsync(
                mediaType,
                item.Id,
                configuration,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(translatedChineseTitle) && ContainsHan(translatedChineseTitle))
            {
                merged = string.Equals(mediaType, "movie", StringComparison.OrdinalIgnoreCase)
                    ? merged with { Title = translatedChineseTitle }
                    : merged with { Name = translatedChineseTitle };
            }
        }

        localizedResultCache[cacheKey] = merged;
        return merged;
    }

    private async Task<string?> FetchChineseTranslatedTitleAsync(
        string mediaType,
        int id,
        TmdbResolvedConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var path = $"/{mediaType}/{id}/translations";
        using var response = await SendTmdbApiRequestAsync(path, configuration, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            ThrowIfCredentialUnavailable(response.StatusCode, configuration);
            return null;
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<TmdbTranslationsResponse>(
            responseStream,
            SerializerOptions,
            cancellationToken);
        if (payload?.Translations is null || payload.Translations.Count == 0)
        {
            return null;
        }

        var chineseTranslations = payload.Translations
            .Where(static item => string.Equals(item.Iso639_1, "zh", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (chineseTranslations.Count == 0)
        {
            return null;
        }

        foreach (var region in ChineseTranslationRegionOrder)
        {
            var regionalMatch = chineseTranslations.FirstOrDefault(item =>
                string.Equals(item.Iso3166_1, region, StringComparison.OrdinalIgnoreCase));
            var regionalTitle = ExtractTranslatedTitle(regionalMatch);
            if (!string.IsNullOrWhiteSpace(regionalTitle))
            {
                return regionalTitle;
            }
        }

        foreach (var item in chineseTranslations)
        {
            var title = ExtractTranslatedTitle(item);
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }
        }

        return null;
    }

    /// <summary>
    /// 人物中文译名：先读 <c>/person/{id}</c> 的 zh 名称，再读 translations 里的中文译名，
    /// 最后退到 <c>also_known_as</c> 里的中文别名。TMDB 确认没有中文条目时返回
    /// <see cref="TmdbChineseNameResultKind.NotTranslated"/>（可负缓存），网络/凭证问题返回
    /// <see cref="TmdbChineseNameResultKind.RequestFailed"/>（不写负缓存，下轮重试）。
    /// </summary>
    public async Task<TmdbChineseNameResult> FetchChinesePersonNameAsync(
        int personId,
        CancellationToken cancellationToken = default)
    {
        var configurations = await ResolveConfigurationsAsync(null, cancellationToken);
        var configuration = configurations.Primary;
        if (!configuration.IsConfigured || personId <= 0)
        {
            return TmdbChineseNameResult.RequestFailed;
        }

        var language = ShouldAttemptChineseLocalization(configuration.Language) ? configuration.Language : "zh-CN";
        var path = $"/person/{personId}?language={Uri.EscapeDataString(language)}&append_to_response=translations";
        using var response = await SendTmdbApiRequestAsync(path, configuration, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return TmdbChineseNameResult.NotTranslated;
        }

        if (!response.IsSuccessStatusCode)
        {
            return TmdbChineseNameResult.RequestFailed;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<TmdbPersonDetailResponse>(
            stream,
            SerializerOptions,
            cancellationToken);
        if (payload is null)
        {
            return TmdbChineseNameResult.RequestFailed;
        }

        var chinese = PickChinesePersonName(payload, configuration.Language);
        return chinese is null ? TmdbChineseNameResult.NotTranslated : TmdbChineseNameResult.Found(chinese);
    }

    /// <summary>
    /// 作品中文片名：TMDB 在缺少 zh 标题时会直接回传原文，此时改查
    /// <c>/{type}/{id}/alternative_titles</c>，按 CN/SG/TW/HK 取官方中文别名。
    /// </summary>
    public async Task<TmdbChineseNameResult> FetchChineseTitleAsync(
        string? mediaType,
        int id,
        CancellationToken cancellationToken = default)
    {
        var configurations = await ResolveConfigurationsAsync(null, cancellationToken);
        var configuration = configurations.Primary;
        if (!configuration.IsConfigured || id <= 0)
        {
            return TmdbChineseNameResult.RequestFailed;
        }

        if (chineseTitleCache.TryGetValue(mediaType + "#" + id, out var cached))
        {
            return cached is null ? TmdbChineseNameResult.NotTranslated : TmdbChineseNameResult.Found(cached);
        }

        // mediaType 未知时（老缓存只有 id）两种类型各探测一次。
        var candidates = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase)
            ? new[] { "tv" }
            : string.Equals(mediaType, "movie", StringComparison.OrdinalIgnoreCase)
                ? new[] { "movie" }
                : new[] { "movie", "tv" };

        foreach (var kind in candidates)
        {
            var path = $"/{kind}/{id}/alternative_titles";
            using var response = await SendTmdbApiRequestAsync(path, configuration, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return TmdbChineseNameResult.RequestFailed;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<TmdbAlternativeTitlesResponse>(
                stream,
                SerializerOptions,
                cancellationToken);
            var title = PickChineseAlternativeTitle(payload);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            chineseTitleCache[mediaType + "#" + id] = title;
            return TmdbChineseNameResult.Found(title);
        }

        chineseTitleCache[mediaType + "#" + id] = null;
        return TmdbChineseNameResult.NotTranslated;
    }

    /// <summary>
    /// 人物作品列表（导演取 crew 里的 director/creator，主演取 cast），
    /// 结果按 mac 版规则去重并按出现顺序返回。
    /// </summary>
    public async Task<IReadOnlyList<TmdbFilmographyItem>?> FetchPersonFilmographyAsync(
        int personId,
        string kind,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = kind + "-" + personId;
        if (filmographyCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var configurations = await ResolveConfigurationsAsync(null, cancellationToken);
        var configuration = configurations.Primary;
        if (!configuration.IsConfigured || personId <= 0)
        {
            return null;
        }

        var path = $"/person/{personId}/combined_credits?language={Uri.EscapeDataString(configuration.Language)}";
        using var response = await SendTmdbApiRequestAsync(path, configuration, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<TmdbCombinedCreditsResponse>(
            stream,
            SerializerOptions,
            cancellationToken);
        if (payload is null)
        {
            return null;
        }

        var source = string.Equals(kind, "director", StringComparison.OrdinalIgnoreCase)
            ? payload.Crew.Where(member =>
                string.Equals(member.Job, "director", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(member.Job, "creator", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(member.Job, "Created by", StringComparison.OrdinalIgnoreCase))
            : payload.Cast;

        var byKey = new Dictionary<string, TmdbFilmographyItem>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var member in source)
        {
            var memberType = member.MediaType?.ToLowerInvariant();
            var rawTitle = memberType == "tv" ? member.Name : member.Title;
            var title = rawTitle?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                continue;
            }

            var key = (member.Id ?? 0) + "-" + (member.MediaType ?? "");
            if (!byKey.TryGetValue(key, out var existing))
            {
                var rawDate = member.ReleaseDate ?? member.FirstAirDate;
                existing = new TmdbFilmographyItem
                {
                    Title = title,
                    Rating = member.VoteAverage,
                    Year = TryParseYear(rawDate, out var year) ? year : null,
                    Date = rawDate,
                    TmdbId = member.Id,
                    MediaType = memberType
                };
                byKey[key] = existing;
                order.Add(key);
            }
            else if (member.VoteAverage.HasValue && (existing.Rating ?? -1) < member.VoteAverage.Value)
            {
                existing.Rating = member.VoteAverage;
            }
        }

        var items = order.Select(key => byKey[key]).ToList();
        filmographyCache[cacheKey] = items;
        return items;
    }

    /// <summary>人物档案（中文名、头像、简介、生日、出生地、别名）。</summary>
    public async Task<TmdbPersonDetail?> FetchPersonDetailAsync(
        int personId,
        CancellationToken cancellationToken = default)
    {
        var configurations = await ResolveConfigurationsAsync(null, cancellationToken);
        var configuration = configurations.Primary;
        if (!configuration.IsConfigured || personId <= 0)
        {
            return null;
        }

        var path = $"/person/{personId}?language={Uri.EscapeDataString(configuration.Language)}&append_to_response=translations";
        using var response = await SendTmdbApiRequestAsync(path, configuration, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<TmdbPersonDetailResponse>(
            stream,
            SerializerOptions,
            cancellationToken);
        if (payload is null)
        {
            return null;
        }

        return new TmdbPersonDetail(
            personId,
            payload.Name,
            PickChinesePersonName(payload, configuration.Language),
            string.IsNullOrWhiteSpace(payload.Biography) ? null : payload.Biography.Trim(),
            string.IsNullOrWhiteSpace(payload.Birthday) ? null : payload.Birthday,
            string.IsNullOrWhiteSpace(payload.PlaceOfBirth) ? null : payload.PlaceOfBirth,
            payload.ProfilePath,
            (payload.AlsoKnownAs ?? []).Where(static name => !string.IsNullOrWhiteSpace(name)).Select(static name => name.Trim()).ToList());
    }

    /// <summary>从人物详情载荷里挑中文名：主名称已是中文则直接用，其次中文译名，最后中文别名。</summary>
    public static string? PickChinesePersonName(TmdbPersonDetailResponse payload, string configuredLanguage)
    {
        var primary = payload.Name?.Trim();
        if (!string.IsNullOrEmpty(primary) && PersonNames.ContainsChinese(primary))
        {
            return primary;
        }

        var translations = payload.Translations?.Translations ?? [];
        var zhTranslations = translations
            .Where(item => string.Equals(item.Iso639_1, "zh", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var region in ChineseTranslationRegionOrder)
        {
            var hit = zhTranslations.FirstOrDefault(item =>
                string.Equals(item.Iso3166_1, region, StringComparison.OrdinalIgnoreCase));
            var name = ExtractTranslatedTitle(hit);
            if (!string.IsNullOrWhiteSpace(name) && PersonNames.ContainsChinese(name))
            {
                return name;
            }
        }

        foreach (var item in zhTranslations)
        {
            var name = ExtractTranslatedTitle(item);
            if (!string.IsNullOrWhiteSpace(name) && PersonNames.ContainsChinese(name))
            {
                return name;
            }
        }

        // 中文界面下 zh-CN 主名称可能已经是中文，只有配置为其他语言时才需要退到中文别名。
        _ = ShouldAttemptChineseLocalization(configuredLanguage);
        var alias = (payload.AlsoKnownAs ?? [])
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name) && PersonNames.ContainsChinese(name));
        return alias?.Trim();
    }

    /// <summary>从官方别名载荷里挑中文片名（电影用 titles，剧集用 results）。</summary>
    public static string? PickChineseAlternativeTitle(TmdbAlternativeTitlesResponse? payload)
    {
        var titles = new List<TmdbAlternativeTitle>();
        titles.AddRange(payload?.Titles ?? []);
        titles.AddRange(payload?.Results ?? []);
        if (titles.Count == 0)
        {
            return null;
        }

        foreach (var region in ChineseTranslationRegionOrder)
        {
            var hit = titles.FirstOrDefault(title =>
                string.Equals(title.Iso3166_1, region, StringComparison.OrdinalIgnoreCase));
            var candidate = hit?.Title?.Trim();
            if (!string.IsNullOrEmpty(candidate) && PersonNames.ContainsChinese(candidate))
            {
                return candidate;
            }
        }

        var anyChinese = titles
            .Select(title => title.Title?.Trim())
            .FirstOrDefault(candidate => !string.IsNullOrEmpty(candidate) && PersonNames.ContainsChinese(candidate));
        return anyChinese;
    }

    public sealed class TmdbPersonDetailResponse
    {
        public int? Id { get; init; }

        public string? Name { get; init; }

        public string? Biography { get; init; }

        public string? Birthday { get; init; }

        [JsonPropertyName("place_of_birth")]
        public string? PlaceOfBirth { get; init; }

        [JsonPropertyName("profile_path")]
        public string? ProfilePath { get; init; }

        [JsonPropertyName("also_known_as")]
        public List<string>? AlsoKnownAs { get; init; }

        public TmdbPersonTranslations? Translations { get; init; }
    }

    public sealed class TmdbPersonTranslations
    {
        public List<TmdbTranslationItem>? Translations { get; init; }
    }

    public sealed class TmdbAlternativeTitlesResponse
    {
        public List<TmdbAlternativeTitle>? Titles { get; init; }

        public List<TmdbAlternativeTitle>? Results { get; init; }
    }

    public sealed class TmdbAlternativeTitle
    {
        [JsonPropertyName("iso_3166_1")]
        public string? Iso3166_1 { get; init; }

        public string? Title { get; init; }

        public string? Type { get; init; }
    }

    private sealed class TmdbCombinedCreditsResponse
    {
        public List<TmdbCreditsPerson> Cast { get; init; } = [];

        public List<TmdbCreditsPerson> Crew { get; init; } = [];
    }

    private HttpRequestMessage CreateRequest(string relativePath, TmdbResolvedConfiguration configuration)
    {
        var officialUrl = $"{TmdbApiBaseUrl}{relativePath}";
        var requestedUrl = TmdbProxyGateway.ResolveRequestUrl(officialUrl, configuration.ProxyUrl);
        if (requestedUrl is null)
        {
            throw new InvalidTmdbProxyException();
        }

        var uriBuilder = new StringBuilder(requestedUrl);
        if (!configuration.UseBearerToken)
        {
            var separator = relativePath.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            uriBuilder.Append(separator);
            uriBuilder.Append("api_key=");
            uriBuilder.Append(Uri.EscapeDataString(configuration.Credential));
        }

        var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.ToString());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (configuration.UseBearerToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.Credential);
        }

        return request;
    }

    private static string BuildConnectionFailureMessage(HttpRequestException exception)
    {
        var details = FlattenExceptionMessages(exception).ToArray();
        var detailText = details.Length == 0
            ? exception.Message
            : string.Join("；", details);
        var hint = BuildConnectionFailureHint(exception, details);

        return string.IsNullOrWhiteSpace(hint)
            ? $"无法连接 TMDB：{detailText}"
            : $"无法连接 TMDB：{detailText}。{hint}";
    }

    private static IEnumerable<string> FlattenExceptionMessages(Exception exception)
    {
        HashSet<string> seenMessages = new(StringComparer.Ordinal);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message.Trim();
            if (!string.IsNullOrWhiteSpace(message) && seenMessages.Add(message))
            {
                yield return message;
            }
        }
    }

    private static string BuildConnectionFailureHint(HttpRequestException exception, IReadOnlyList<string> details)
    {
        var combinedDetails = string.Join(' ', details);
        if (exception.InnerException is AuthenticationException ||
            combinedDetails.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
            combinedDetails.Contains("certificate", StringComparison.OrdinalIgnoreCase) ||
            combinedDetails.Contains("CERT_", StringComparison.OrdinalIgnoreCase))
        {
            return "这是 TLS/证书握手失败，通常与系统时间错误、代理/VPN 根证书未受信任、杀毒软件 HTTPS 扫描或 DNS 劫持有关。请先确认 Windows 时间正确，并检查代理/VPN 证书是否已安装到受信任的根证书颁发机构。";
        }

        if (ContainsInnerException<SocketException>(exception))
        {
            return "这是网络或 DNS 连接失败，请检查 api.themoviedb.org 是否能通过当前网络、代理或 VPN 访问。";
        }

        return string.Empty;
    }

    private static bool ContainsInnerException<TException>(Exception exception)
        where TException : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException)
            {
                return true;
            }
        }

        return false;
    }

    private static TmdbSearchItem MergeLocalized(TmdbSearchItem preferred, TmdbSearchItem fallback)
    {
        return preferred with
        {
            Id = preferred.Id > 0 ? preferred.Id : fallback.Id,
            Title = preferred.Title ?? fallback.Title,
            Name = preferred.Name ?? fallback.Name,
            OriginalTitle = preferred.OriginalTitle ?? fallback.OriginalTitle,
            OriginalName = preferred.OriginalName ?? fallback.OriginalName,
            Overview = preferred.Overview ?? fallback.Overview,
            PosterPath = preferred.PosterPath ?? fallback.PosterPath,
            ReleaseDate = preferred.ReleaseDate ?? fallback.ReleaseDate,
            FirstAirDate = preferred.FirstAirDate ?? fallback.FirstAirDate,
            VoteAverage = preferred.VoteAverage ?? fallback.VoteAverage,
            Popularity = preferred.Popularity ?? fallback.Popularity,
            OriginalLanguage = preferred.OriginalLanguage ?? fallback.OriginalLanguage,
            OriginCountries = preferred.OriginCountries.Count > 0 ? preferred.OriginCountries : fallback.OriginCountries,
            ProductionCountries = preferred.ProductionCountries.Count > 0 ? preferred.ProductionCountries : fallback.ProductionCountries
        };
    }

    private static TmdbSearchContext BuildSearchContext(
        string mediaType,
        IReadOnlyList<string> queries,
        string? year,
        TmdbSearchOptions? options)
    {
        var primaryQuery = queries[0];
        var secondaryQuery = DeriveSecondaryQuery(queries, primaryQuery, options?.SecondaryQuery);
        return new TmdbSearchContext(
            mediaType,
            primaryQuery,
            secondaryQuery,
            year,
            options?.PreferredSeason);
    }

    private static string? DeriveSecondaryQuery(
        IReadOnlyList<string> queries,
        string primaryQuery,
        string? explicitSecondaryQuery)
    {
        if (!string.IsNullOrWhiteSpace(explicitSecondaryQuery))
        {
            var trimmed = explicitSecondaryQuery.Trim();
            if (!string.Equals(NormalizeTitle(trimmed), NormalizeTitle(primaryQuery), StringComparison.Ordinal))
            {
                return trimmed;
            }
        }

        var normalizedPrimary = NormalizeTitle(primaryQuery);
        if (ContainsHan(primaryQuery))
        {
            foreach (var query in queries.Skip(1))
            {
                if (string.Equals(NormalizeTitle(query), normalizedPrimary, StringComparison.Ordinal) || ContainsHan(query))
                {
                    continue;
                }

                return query;
            }
        }

        foreach (var query in queries.Skip(1))
        {
            if (!string.Equals(NormalizeTitle(query), normalizedPrimary, StringComparison.Ordinal))
            {
                return query;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ResolveSearchLanguageOrder(string preferredLanguage)
    {
        var primaryLanguage = string.IsNullOrWhiteSpace(preferredLanguage)
            ? TmdbSettings.DefaultLanguage
            : preferredLanguage.Trim();
        var fallbackLanguage = primaryLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? "zh-CN"
            : "en-US";

        List<string> order = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        AddLanguage(primaryLanguage);
        AddLanguage(fallbackLanguage);

        return order;

        void AddLanguage(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return;
            }

            var trimmed = language.Trim();
            if (seen.Add(trimmed))
            {
                order.Add(trimmed);
            }
        }
    }

    private static double Score(
        TmdbSearchItem item,
        TmdbSearchContext context,
        IReadOnlyDictionary<int, int> seasonCountByTvId,
        IReadOnlyDictionary<int, int> seasonAirYearByTvId)
    {
        var score = Math.Min(item.Popularity ?? 0, 500);
        var queryNorm = NormalizeTitle(context.PrimaryQuery);
        var candidateTitles = MatchableTitles(item);
        var normalizedCandidateTitles = candidateTitles
            .Select(NormalizeTitle)
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(queryNorm))
        {
            var titleBonus = normalizedCandidateTitles
                .Select(candidate => candidate switch
                {
                    _ when string.Equals(candidate, queryNorm, StringComparison.Ordinal) => 12000d,
                    _ when candidate.Contains(queryNorm, StringComparison.Ordinal) => 4000d,
                    _ when queryNorm.Contains(candidate, StringComparison.Ordinal) => 2000d,
                    _ => 0d
                })
                .DefaultIfEmpty()
                .Max();
            score += titleBonus;
        }

        if (!string.IsNullOrWhiteSpace(context.SecondaryQuery))
        {
            var secondaryNorm = NormalizeTitle(context.SecondaryQuery);
            if (!string.IsNullOrWhiteSpace(secondaryNorm) &&
                !string.Equals(secondaryNorm, queryNorm, StringComparison.Ordinal))
            {
                var secondaryBonus = normalizedCandidateTitles
                    .Select(candidate => candidate switch
                    {
                        _ when string.Equals(candidate, secondaryNorm, StringComparison.Ordinal) => 8000d,
                        _ when candidate.Contains(secondaryNorm, StringComparison.Ordinal) => 2600d,
                        _ when secondaryNorm.Contains(candidate, StringComparison.Ordinal) => 1200d,
                        _ => 0d
                    })
                    .DefaultIfEmpty()
                    .Max();
                score += secondaryBonus;
            }
        }

        var queryHasSequelSemantics =
            ContainsSequelSemantics(context.PrimaryQuery) || ContainsSequelSemantics(queryNorm);
        var titleHasSequelSemantics = candidateTitles.Any(ContainsSequelSemantics) ||
                                      normalizedCandidateTitles.Any(ContainsSequelSemantics);
        if (!queryHasSequelSemantics && titleHasSequelSemantics)
        {
            score -= 6000;
        }

        if (string.Equals(item.MediaKind, context.PreferredMediaType, StringComparison.OrdinalIgnoreCase))
        {
            score += 9000;
        }
        else if (!string.IsNullOrWhiteSpace(item.MediaKind))
        {
            score -= 3500;
        }

        if (context.PreferredSeason is > 0 && string.Equals(context.PreferredMediaType, "tv", StringComparison.OrdinalIgnoreCase))
        {
            if (seasonCountByTvId.TryGetValue(item.Id, out var seasonCount))
            {
                score += seasonCount >= context.PreferredSeason.Value ? 4500 : -800;
            }
        }

        var queryTokenCount = TokenCount(context.PrimaryQuery);
        if (queryTokenCount <= 2)
        {
            var longestCandidateTokenCount = candidateTitles.Select(TokenCount).DefaultIfEmpty().Max();
            var hasExactTitle = normalizedCandidateTitles.Contains(queryNorm, StringComparer.Ordinal);
            if (!hasExactTitle && longestCandidateTokenCount >= 4)
            {
                score -= 2200;
            }
        }

        if (TryParseYear(context.TargetYear, out var targetYear))
        {
            var scoredYear = seasonAirYearByTvId.TryGetValue(item.Id, out var seasonAirYear)
                ? seasonAirYear
                : TryParseYear(item.ReleaseDate ?? item.FirstAirDate, out var itemYear)
                    ? itemYear
                    : (int?)null;

            if (scoredYear.HasValue)
            {
                var yearDiff = Math.Abs(scoredYear.Value - targetYear);
                score += yearDiff switch
                {
                    0 => 10000,
                    1 => 5000,
                    >= 10 => -20000,
                    >= 3 when context.PreferredSeason is > 0 && string.Equals(context.PreferredMediaType, "tv", StringComparison.OrdinalIgnoreCase) => -14500,
                    >= 3 => -12000,
                    _ => 0
                };
            }
        }

        if (!string.IsNullOrWhiteSpace(item.PosterPath))
        {
            score += 250;
        }

        return score;
    }

    private static IReadOnlyList<string> MatchableTitles(TmdbSearchItem item)
    {
        List<string> titles = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        void Add(string? value)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return;
            }

            var normalized = NormalizeTitle(trimmed);
            if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
            {
                return;
            }

            titles.Add(trimmed);
        }

        Add(item.DisplayTitle);
        Add(item.Title);
        Add(item.Name);
        Add(item.OriginalTitle);
        Add(item.OriginalName);

        return titles;
    }

    private static List<string> DeduplicateQueries(IReadOnlyList<string> candidateTitles)
    {
        List<string> normalized = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (var title in candidateTitles)
        {
            var trimmed = title?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var key = NormalizeTitle(trimmed);
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
            {
                continue;
            }

            normalized.Add(trimmed);
        }

        return normalized;
    }

    private static string NormalizeTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static int TokenCount(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var tokens = TokenSeparatorRegex.Replace(value, " ");
        tokens = ConsecutiveWhitespaceRegex.Replace(tokens, " ").Trim();
        return tokens.Length == 0
            ? 0
            : tokens.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }

    private static bool ContainsSequelSemantics(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && SequelRegex.IsMatch(value);
    }

    private static bool TryParseYear(string? value, out int year)
    {
        year = 0;
        if (string.IsNullOrWhiteSpace(value) || value!.Length < 4)
        {
            return false;
        }

        return int.TryParse(value[..4], out year);
    }

    private static bool ContainsHan(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character >= 0x4E00 && character <= 0x9FFF)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldAttemptChineseLocalization(string language)
    {
        return !string.IsNullOrWhiteSpace(language) &&
               language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractTranslatedTitle(TmdbTranslationItem? item)
    {
        if (item?.Data is null)
        {
            return null;
        }

        var title = string.IsNullOrWhiteSpace(item.Data.Title) ? item.Data.Name : item.Data.Title;
        return string.IsNullOrWhiteSpace(title) ? null : SimplifyChineseIfNeeded(title.Trim());
    }

    private static string? SimplifyChineseIfNeeded(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !ContainsHan(value))
        {
            return value;
        }

        return value;
    }

    private static TmdbMetadataMatch ToMatch(TmdbSearchItem item, string mediaType)
    {
        return new TmdbMetadataMatch(
            item.Id,
            mediaType,
            item.DisplayTitle,
            item.Overview,
            item.ReleaseDate,
            item.FirstAirDate,
            item.PosterPath,
            item.VoteAverage,
            item.Popularity,
            item.OriginalTitle ?? item.OriginalName,
            item.ProductionCountryCodes,
            item.OriginalLanguage);
    }

    private static Uri? ResolvePosterUri(string posterPath)
    {
        if (Uri.TryCreate(posterPath, UriKind.Absolute, out var absoluteUri) &&
            (string.Equals(absoluteUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(absoluteUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return absoluteUri;
        }

        var normalizedPosterPath = posterPath.StartsWith("/", StringComparison.Ordinal)
            ? posterPath
            : $"/{posterPath}";
        return Uri.TryCreate($"{TmdbImageBaseUrl}{normalizedPosterPath}", UriKind.Absolute, out var tmdbUri)
            ? tmdbUri
            : null;
    }

    private async Task<TmdbResolvedConfigurations> ResolveConfigurationsAsync(
        TmdbSettings? overrideSettings,
        CancellationToken cancellationToken)
    {
        var settings = overrideSettings ?? (await settingsService.LoadAsync(cancellationToken)).Tmdb;
        var language = ResolveLanguage(settings);
        var proxyUrl = settings.ProxyUrl?.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(settings.CustomAccessToken))
        {
            return new TmdbResolvedConfigurations(
                new TmdbResolvedConfiguration(
                    settings.CustomAccessToken.Trim(),
                    UseBearerToken: true,
                    language,
                    "自定义 Access Token",
                    TmdbSourceKind.CustomAccessToken,
                    proxyUrl));
        }

        if (!string.IsNullOrWhiteSpace(settings.CustomApiKey))
        {
            return new TmdbResolvedConfigurations(
                new TmdbResolvedConfiguration(
                    settings.CustomApiKey.Trim(),
                    UseBearerToken: false,
                    language,
                    "自定义 API Key",
                    TmdbSourceKind.CustomApiKey,
                    proxyUrl));
        }

        var envAccessToken = ResolveAccessToken();
        if (!string.IsNullOrWhiteSpace(envAccessToken))
        {
            return new TmdbResolvedConfigurations(
                new TmdbResolvedConfiguration(
                    envAccessToken,
                    UseBearerToken: true,
                    language,
                    "环境变量 Access Token",
                    TmdbSourceKind.EnvironmentAccessToken,
                    proxyUrl));
        }

        var envApiKey = ResolveApiKey();
        if (!string.IsNullOrWhiteSpace(envApiKey))
        {
            return new TmdbResolvedConfigurations(
                new TmdbResolvedConfiguration(
                    envApiKey,
                    UseBearerToken: false,
                    language,
                    "环境变量 API Key",
                    TmdbSourceKind.EnvironmentApiKey,
                    proxyUrl));
        }

        return new TmdbResolvedConfigurations(TmdbResolvedConfiguration.Unconfigured(language));
    }

    private static string? ResolveApiKey()
    {
        return Environment.GetEnvironmentVariable("OMNIPLAY_TMDB_API_KEY")
               ?? Environment.GetEnvironmentVariable("TMDB_API_KEY");
    }

    private static string? ResolveAccessToken()
    {
        return Environment.GetEnvironmentVariable("OMNIPLAY_TMDB_ACCESS_TOKEN")
               ?? Environment.GetEnvironmentVariable("TMDB_ACCESS_TOKEN");
    }

    private static string ResolveLanguage(TmdbSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Language))
        {
            return settings.Language.Trim();
        }

        return ResolveEnvironmentLanguage() ?? TmdbSettings.DefaultLanguage;
    }

    private static string? ResolveEnvironmentLanguage()
    {
        return Environment.GetEnvironmentVariable("OMNIPLAY_TMDB_LANGUAGE")
               ?? Environment.GetEnvironmentVariable("TMDB_LANGUAGE");
    }

    private async Task<TmdbConnectionTestAttempt> TestConnectionWithConfigurationAsync(
        TmdbResolvedConfiguration configuration,
        CancellationToken cancellationToken)
    {
        using var response = await SendTmdbApiRequestAsync("/configuration", configuration, cancellationToken, maxAttempts: 1);

        if (response.IsSuccessStatusCode)
        {
            var successMessage = $"TMDB 连通性正常（HTTP {(int)response.StatusCode}，{configuration.SourceLabel}）。";
            return new TmdbConnectionTestAttempt(true, successMessage, response.StatusCode);
        }

        return new TmdbConnectionTestAttempt(
            false,
            BuildTmdbStatusFailureMessage(response.StatusCode, configuration),
            response.StatusCode);
    }

    private static string BuildTmdbStatusFailureMessage(
        HttpStatusCode statusCode,
        TmdbResolvedConfiguration configuration)
    {
        return statusCode switch
        {
            HttpStatusCode.Unauthorized => $"{configuration.SourceLabel} 返回 401，请检查 API Key 或 Access Token 是否有效。",
            HttpStatusCode.Forbidden => $"{configuration.SourceLabel} 返回 403，请检查 API Key 权限或来源限制。",
            HttpStatusCode.TooManyRequests => $"{configuration.SourceLabel} 返回 429，请稍后重试。",
            _ => $"TMDB 返回 {(int)statusCode}。"
        };
    }

    private static void ThrowIfCredentialUnavailable(
        HttpStatusCode statusCode,
        TmdbResolvedConfiguration configuration)
    {
        if (statusCode is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.TooManyRequests)
        {
            throw new TmdbSourceUnavailableException(configuration, statusCode);
        }
    }

    /// <summary>
    /// 发送 TMDB API 请求：先过节流器，再对 429/5xx 与传输层异常做退避重试
    /// （对齐 mac 版 <c>performRequest</c>：<c>Retry-After</c> 优先，否则按尝试次数线性退避）。
    /// 每次尝试都重建请求，因为 <see cref="HttpRequestMessage"/> 不能重复发送。
    /// </summary>
    private async Task<HttpResponseMessage> SendTmdbApiRequestAsync(
        string relativePath,
        TmdbResolvedConfiguration configuration,
        CancellationToken cancellationToken,
        int maxAttempts = 3)
    {
        var limiter = RateLimiterFor(configuration.SourceKind);
        for (var attempt = 0; ; attempt++)
        {
            await limiter.AcquireAsync(cancellationToken).ConfigureAwait(false);
            using var request = CreateRequest(relativePath, configuration);
            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < maxAttempts - 1)
            {
                await Task.Delay(RetryDelay(TimeSpan.Zero, attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            var statusCode = (int)response.StatusCode;
            if (attempt < maxAttempts - 1 && (statusCode == 429 || statusCode >= 500))
            {
                var retryAfter = RetryAfterHeader(response);
                response.Dispose();
                await Task.Delay(RetryDelay(retryAfter, attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            return response;
        }
    }

    private TmdbRequestLimiter RateLimiterFor(TmdbSourceKind sourceKind)
    {
        // 共用受限量 key 更容易触发配额，节流更保守；用户自带凭据按 3.2 RPS（同 mac 版）。
        var key = sourceKind is TmdbSourceKind.CustomApiKey or TmdbSourceKind.CustomAccessToken ? "custom" : "shared";
        var requestsPerSecond = key == "custom" ? 3.2 : 1.5;
        return rateLimiters.GetOrAdd(key, static (kind, rps) => new TmdbRequestLimiter(rps), requestsPerSecond);
    }

    private static TimeSpan? RetryAfterHeader(HttpResponseMessage response)
    {
        var raw = response.Headers.RetryAfter?.ToString();
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(Math.Min(seconds, 30));
        }

        return null;
    }

    private static TimeSpan RetryDelay(TimeSpan? retryAfter, int attempt)
    {
        // 1.5s / 3s / 4.5s 线性退避，服务端显式给出 Retry-After 时优先听服务端的。
        return retryAfter ?? TimeSpan.FromSeconds(1.5 * (attempt + 1));
    }

    private async Task DownloadBinaryAsync(Uri sourceUri, string destinationPath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path.Combine(
            string.IsNullOrWhiteSpace(directory) ? Path.GetTempPath() : directory,
            $"{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using var response = await httpClient.GetAsync(
                sourceUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var localFile = File.Create(tempPath))
            {
                await responseStream.CopyToAsync(localFile, cancellationToken);
            }

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                }
            }
        }
    }

    private sealed class TmdbSearchResponse
    {
        [JsonPropertyName("results")]
        public List<TmdbSearchItem> Results { get; init; } = [];
    }

    private sealed class TmdbEpisodeDetailResponse
    {
        [JsonPropertyName("still_path")]
        public string? StillPath { get; init; }
    }

    private sealed record TmdbSeasonSummary(int SeasonNumber, int EpisodeCount);

    private sealed class TmdbEpisodeImagesResponse
    {
        [JsonPropertyName("stills")]
        public List<TmdbEpisodeImageItem> Stills { get; init; } = [];
    }

    private sealed class TmdbEpisodeImageItem
    {
        [JsonPropertyName("file_path")]
        public string? FilePath { get; init; }

        [JsonPropertyName("vote_average")]
        public double? VoteAverage { get; init; }

        [JsonPropertyName("vote_count")]
        public int? VoteCount { get; init; }
    }

    private sealed class TmdbTvDetailResponse
    {
        [JsonPropertyName("number_of_seasons")]
        public int? NumberOfSeasons { get; init; }

        [JsonPropertyName("seasons")]
        public List<TmdbSeasonSummaryItem> Seasons { get; init; } = [];
    }

    private sealed class TmdbCreditsResponse
    {
        public List<TmdbCreditsPerson> Cast { get; init; } = [];

        public List<TmdbCreditsPerson> Crew { get; init; } = [];
    }

    private sealed class TmdbCreditsPerson
    {
        public int? Id { get; init; }

        public string? Name { get; init; }

        /// <summary>combined_credits 里电影用 title，剧集用 name。</summary>
        public string? Title { get; init; }

        public string? Job { get; init; }

        public string? Department { get; init; }

        [JsonPropertyName("profile_path")]
        public string? ProfilePath { get; init; }

        [JsonPropertyName("media_type")]
        public string? MediaType { get; init; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; init; }

        [JsonPropertyName("first_air_date")]
        public string? FirstAirDate { get; init; }

        [JsonPropertyName("vote_average")]
        public double? VoteAverage { get; init; }
    }

    private sealed class TmdbSeasonSummaryItem
    {
        [JsonPropertyName("season_number")]
        public int? SeasonNumber { get; init; }

        [JsonPropertyName("episode_count")]
        public int? EpisodeCount { get; init; }
    }

    private sealed class TmdbSeasonDetailResponse
    {
        [JsonPropertyName("air_date")]
        public string? AirDate { get; init; }
    }

    private sealed class TmdbTranslationsResponse
    {
        [JsonPropertyName("translations")]
        public List<TmdbTranslationItem> Translations { get; init; } = [];
    }

    public sealed class TmdbTranslationItem
    {
        [JsonPropertyName("iso_639_1")]
        public string? Iso639_1 { get; init; }

        [JsonPropertyName("iso_3166_1")]
        public string? Iso3166_1 { get; init; }

        [JsonPropertyName("data")]
        public TmdbTranslationData? Data { get; init; }
    }

    public sealed class TmdbTranslationData
    {
        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    private sealed record TmdbSearchItem
    {
        [JsonPropertyName("id")]
        public int Id { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("original_title")]
        public string? OriginalTitle { get; init; }

        [JsonPropertyName("original_name")]
        public string? OriginalName { get; init; }

        [JsonPropertyName("overview")]
        public string? Overview { get; init; }

        [JsonPropertyName("poster_path")]
        public string? PosterPath { get; init; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; init; }

        [JsonPropertyName("first_air_date")]
        public string? FirstAirDate { get; init; }

        [JsonPropertyName("vote_average")]
        public double? VoteAverage { get; init; }

        [JsonPropertyName("popularity")]
        public double? Popularity { get; init; }

        [JsonPropertyName("original_language")]
        public string? OriginalLanguage { get; init; }

        [JsonPropertyName("origin_country")]
        public List<string> OriginCountries { get; init; } = [];

        [JsonPropertyName("production_countries")]
        public List<TmdbCountryItem> ProductionCountries { get; init; } = [];

        public string DisplayTitle => Title ?? Name ?? OriginalTitle ?? OriginalName ?? string.Empty;

        public bool HasChineseDisplayTitle => ContainsHan(DisplayTitle);

        public string MediaKind => !string.IsNullOrWhiteSpace(Name) || !string.IsNullOrWhiteSpace(FirstAirDate)
            ? "tv"
            : "movie";

        public string ProductionCountryCodes
        {
            get
            {
                var codes = ProductionCountries
                    .Select(static country => country.Iso3166_1)
                    .Concat(OriginCountries)
                    .Where(static code => !string.IsNullOrWhiteSpace(code))
                    .Select(static code => code!.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return string.Join(',', codes);
            }
        }
    }

    private sealed record TmdbCountryItem
    {
        [JsonPropertyName("iso_3166_1")]
        public string? Iso3166_1 { get; init; }
    }

    private sealed record TmdbResolvedConfigurations(TmdbResolvedConfiguration Primary);

    private sealed record TmdbConnectionTestAttempt(
        bool Success,
        string Message,
        HttpStatusCode? StatusCode);

    private sealed record TmdbResolvedConfiguration(
        string Credential,
        bool UseBearerToken,
        string Language,
        string SourceLabel,
        TmdbSourceKind SourceKind,
        string ProxyUrl)
    {
        public bool IsConfigured => !string.IsNullOrWhiteSpace(Credential);

        public static TmdbResolvedConfiguration Unconfigured(string language)
        {
            return new TmdbResolvedConfiguration(string.Empty, false, language, "未配置", TmdbSourceKind.Unconfigured, string.Empty);
        }
    }

    private sealed class InvalidTmdbProxyException : Exception
    {
        internal InvalidTmdbProxyException()
            : base("TMDB 代理网关地址无效。")
        {
        }
    }

    private sealed class TmdbSourceUnavailableException(
        TmdbResolvedConfiguration configuration,
        HttpStatusCode statusCode) : Exception
    {
        public TmdbResolvedConfiguration Configuration { get; } = configuration;

        public HttpStatusCode StatusCode { get; } = statusCode;
    }

    private sealed record TmdbSearchContext(
        string PreferredMediaType,
        string PrimaryQuery,
        string? SecondaryQuery,
        string? TargetYear,
        int? PreferredSeason);

    private enum TmdbSourceKind
    {
        Unconfigured = 0,
        CustomApiKey = 1,
        CustomAccessToken = 2,
        EnvironmentAccessToken = 3,
        EnvironmentApiKey = 4
    }
}
