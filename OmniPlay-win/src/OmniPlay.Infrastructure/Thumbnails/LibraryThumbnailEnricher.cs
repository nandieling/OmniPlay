using Dapper;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Core.Settings;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.Library;
using OmniPlay.Infrastructure.Tmdb;

namespace OmniPlay.Infrastructure.Thumbnails;

public sealed class LibraryThumbnailEnricher : ILibraryThumbnailEnricher
{
    private readonly SqliteDatabase database;
    private readonly IStoragePaths storagePaths;
    private readonly ITmdbMetadataClient tmdbMetadataClient;
    private readonly ISettingsService? settingsService;
    private readonly ILocalMetadataSidecarService? localMetadataSidecarService;

    public LibraryThumbnailEnricher(
        SqliteDatabase database,
        IStoragePaths storagePaths,
        ITmdbMetadataClient tmdbMetadataClient,
        ISettingsService? settingsService = null,
        ILocalMetadataSidecarService? localMetadataSidecarService = null)
    {
        this.database = database;
        this.storagePaths = storagePaths;
        this.tmdbMetadataClient = tmdbMetadataClient;
        this.settingsService = settingsService;
        this.localMetadataSidecarService = localMetadataSidecarService;
    }

    public async Task<LibraryThumbnailEnrichmentSummary> EnrichMissingThumbnailsAsync(
        TmdbSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        return await EnrichMissingThumbnailsInternalAsync(
            tvShowId: null,
            settings,
            cancellationToken);
    }

    public async Task<LibraryThumbnailEnrichmentSummary> EnrichMissingThumbnailsForTvShowAsync(
        long tvShowId,
        TmdbSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        if (tvShowId == 0)
        {
            return new LibraryThumbnailEnrichmentSummary();
        }

        return await EnrichMissingThumbnailsInternalAsync(
            tvShowId,
            settings,
            cancellationToken);
    }

    /// <summary>确认"该集没有剧照"的负缓存有效期，避免每次扫描都重复请求。</summary>
    private static readonly TimeSpan MissingStillTtl = TimeSpan.FromDays(30);

    /// <summary>同时处理的剧集数；剧照请求本身另有客户端限速。</summary>
    private const int MaxParallelShows = 3;

    private async Task<LibraryThumbnailEnrichmentSummary> EnrichMissingThumbnailsInternalAsync(
        long? tvShowId,
        TmdbSettings? settings,
        CancellationToken cancellationToken)
    {
        settings ??= new TmdbSettings();
        if (!settings.EnableEpisodeThumbnailDownloads)
        {
            return new LibraryThumbnailEnrichmentSummary();
        }

        var missingStills = LoadMissingStillCache();
        var pendingMissing = new List<string>();
        var candidates = await LoadCandidatesAsync(tvShowId, cancellationToken);
        var exportLocalMetadata = await ShouldExportLocalMetadataAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        List<ThumbnailCandidate> pending = [];
        var skippedCount = 0;
        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(storagePaths.ThumbnailsDirectory, $"{candidate.VideoFileId}.jpg")))
            {
                continue;
            }

            if (missingStills.TryGetValue(candidate.VideoFileId, out var cachedAt) &&
                now - cachedAt < (long)MissingStillTtl.TotalSeconds)
            {
                continue;
            }

            pending.Add(candidate);
        }

        // 按剧集分组：一部剧集只需搜索一次，也能把网络故障的影响限制在该剧集内。
        Dictionary<long, List<(ThumbnailCandidate Candidate, int Season, int Episode)>> shows = [];
        List<long> showOrder = [];
        foreach (var candidate in pending)
        {
            var parsedEpisode = MediaNameParser.ParseEpisodeInfo(candidate.FileName, candidate.SortIndex);
            var seasonNumber = candidate.CustomSeasonNumber ?? parsedEpisode.Season;
            var episodeNumber = candidate.CustomEpisodeNumber ?? parsedEpisode.Episode;
            if (!parsedEpisode.IsTvShow || seasonNumber <= 0 || episodeNumber <= 0)
            {
                skippedCount++;
                continue;
            }

            if (!shows.TryGetValue(candidate.TvShowId, out var entries))
            {
                entries = [];
                shows[candidate.TvShowId] = entries;
                showOrder.Add(candidate.TvShowId);
            }

            entries.Add((candidate, seasonNumber, episodeNumber));
        }

        var downloadedCount = 0;
        var gate = new SemaphoreSlim(MaxParallelShows);
        using var showResults = new CancellationTokenSource();
        var tasks = showOrder.Select(async showId =>
        {
            await gate.WaitAsync(CancellationToken.None);
            try
            {
                var outcome = await EnrichShowAsync(
                    shows[showId],
                    exportLocalMetadata,
                    pendingMissing,
                    showResults.Token,
                    cancellationToken);
                return (outcome.Downloaded, outcome.Skipped, outcome.NetworkError);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        var failures = 0;
        string? firstError = null;
        foreach (var task in tasks)
        {
            try
            {
                var (showDownloaded, showSkipped, networkError) = await task;
                downloadedCount += showDownloaded;
                skippedCount += showSkipped;
                if (networkError)
                {
                    failures++;
                }
            }
            catch (Exception ex)
            {
                failures++;
                firstError ??= ex.Message;
            }
        }

        if (pendingMissing.Count > 0)
        {
            SaveMissingStillCache(missingStills, pendingMissing, now);
        }

        // 只有全线失败才算"遇到网络错误"，个别剧集失败不该让整轮刮削停下来。
        var totalNetworkFailure = showOrder.Count > 0 && failures == showOrder.Count;
        return new LibraryThumbnailEnrichmentSummary(
            downloadedCount,
            skippedCount,
            EncounteredNetworkError: totalNetworkFailure,
            ErrorMessage: totalNetworkFailure ? firstError : null);
    }

    private async Task<(int Downloaded, int Skipped, bool NetworkError)> EnrichShowAsync(
        List<(ThumbnailCandidate Candidate, int Season, int Episode)> entries,
        bool exportLocalMetadata,
        List<string> pendingMissing,
        CancellationToken passToken,
        CancellationToken cancellationToken)
    {
        var downloaded = 0;
        var skipped = 0;
        var networkError = false;
        var first = entries[0].Candidate;
        TmdbMetadataMatch? showMatch;
        try
        {
            var lookupTitles = LibraryLookupTitleBuilder.Build(
                first.ShowTitle,
                first.SourceProtocolType,
                first.SourceBasePath,
                first.RelativePath,
                fileName: first.FileName);
            showMatch = await tmdbMetadataClient.SearchTvShowAsync(
                lookupTitles,
                first.FirstAirDate,
                cancellationToken,
                new TmdbSearchOptions
                {
                    PreferredSeason = entries[0].Season
                });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (0, entries.Count, true);
        }

        if (showMatch is null)
        {
            return (0, entries.Count, false);
        }

        foreach (var (candidate, seasonNumber, episodeNumber) in entries)
        {
            if (passToken.IsCancellationRequested)
            {
                skipped += 1;
                continue;
            }

            try
            {
                var localPath = await tmdbMetadataClient.DownloadEpisodeStillAsync(
                    showMatch.Id,
                    seasonNumber,
                    episodeNumber,
                    candidate.VideoFileId,
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
                {
                    if (exportLocalMetadata)
                    {
                        await TryExportEpisodeThumbnailAsync(candidate, localPath, cancellationToken);
                    }

                    downloaded++;
                }
                else
                {
                    // 请求成功但没有剧照：记负缓存，下轮扫描不再重复请求。
                    pendingMissing.Add(candidate.VideoFileId);
                    skipped++;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                networkError = true;
                skipped += 1;
                break;
            }
            catch
            {
                skipped++;
            }
        }

        return (downloaded, skipped, networkError);
    }

    private string MissingStillCachePath => Path.Combine(storagePaths.ThumbnailsDirectory, "no-still.json");

    private Dictionary<string, long> LoadMissingStillCache()
    {
        try
        {
            var path = MissingStillCachePath;
            if (!File.Exists(path))
            {
                return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            }

            var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(
                File.ReadAllText(path));
            return loaded ?? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveMissingStillCache(Dictionary<string, long> cache, IEnumerable<string> newKeys, long now)
    {
        try
        {
            foreach (var key in newKeys)
            {
                cache[key] = now;
            }

            var cutoff = now - (long)MissingStillTtl.TotalSeconds;
            var trimmed = cache.Where(entry => entry.Value >= cutoff)
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

            Directory.CreateDirectory(storagePaths.ThumbnailsDirectory);
            File.WriteAllText(
                MissingStillCachePath,
                System.Text.Json.JsonSerializer.Serialize(trimmed));
        }
        catch
        {
            // 负缓存只是加速手段，写失败不影响本轮结果。
        }
    }

    private async Task<bool> ShouldExportLocalMetadataAsync(CancellationToken cancellationToken)
    {
        if (settingsService is null || localMetadataSidecarService is null)
        {
            return false;
        }

        var settings = await settingsService.LoadAsync(cancellationToken);
        return settings.LocalMetadata.EnableLocalMetadataExport;
    }

    private async Task TryExportEpisodeThumbnailAsync(
        ThumbnailCandidate candidate,
        string thumbnailPath,
        CancellationToken cancellationToken)
    {
        if (localMetadataSidecarService is null)
        {
            return;
        }

        try
        {
            await localMetadataSidecarService.ExportEpisodeThumbnailAsync(
                candidate.SourceProtocolType,
                candidate.SourceBasePath,
                candidate.RelativePath,
                thumbnailPath,
                cancellationToken);
        }
        catch
        {
        }
    }

    private async Task<IReadOnlyList<ThumbnailCandidate>> LoadCandidatesAsync(long? tvShowId, CancellationToken cancellationToken)
    {
        using var connection = database.OpenConnection();
        var rows = await connection.QueryAsync<ThumbnailCandidate>(
            new CommandDefinition(
                """
                SELECT videoFile.id AS VideoFileId,
                       videoFile.fileName AS FileName,
                       videoFile.relativePath AS RelativePath,
                       videoFile.customSeasonNumber AS CustomSeasonNumber,
                       videoFile.customEpisodeNumber AS CustomEpisodeNumber,
                       mediaSource.protocolType AS SourceProtocolType,
                       mediaSource.baseUrl AS SourceBasePath,
                       tvShow.id AS TvShowId,
                       tvShow.title AS ShowTitle,
                       tvShow.firstAirDate AS FirstAirDate,
                       ROW_NUMBER() OVER (PARTITION BY tvShow.id ORDER BY videoFile.fileName COLLATE NOCASE ASC) - 1 AS SortIndex
                FROM videoFile
                JOIN mediaSource ON mediaSource.id = videoFile.sourceId
                JOIN tvShow ON tvShow.id = videoFile.episodeId
                WHERE videoFile.mediaType = 'tv'
                  AND (@TvShowId IS NULL OR tvShow.id = @TvShowId)
                ORDER BY tvShow.title COLLATE NOCASE ASC, videoFile.fileName COLLATE NOCASE ASC
                """,
                new { TvShowId = tvShowId },
                cancellationToken: cancellationToken));
        return rows.ToList();
    }

    private sealed class ThumbnailCandidate
    {
        public string VideoFileId { get; init; } = string.Empty;

        public string FileName { get; init; } = string.Empty;

        public string RelativePath { get; init; } = string.Empty;

        public int? CustomSeasonNumber { get; init; }

        public int? CustomEpisodeNumber { get; init; }

        public string SourceProtocolType { get; init; } = string.Empty;

        public string SourceBasePath { get; init; } = string.Empty;

        public long TvShowId { get; init; }

        public string ShowTitle { get; init; } = string.Empty;

        public string? FirstAirDate { get; init; }

        public int SortIndex { get; init; }
    }
}
