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

    public LibraryThumbnailEnricher(
        SqliteDatabase database,
        IStoragePaths storagePaths,
        ITmdbMetadataClient tmdbMetadataClient)
    {
        this.database = database;
        this.storagePaths = storagePaths;
        this.tmdbMetadataClient = tmdbMetadataClient;
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

        var candidates = await LoadCandidatesAsync(tvShowId, cancellationToken);
        if (candidates.Count == 0)
        {
            return new LibraryThumbnailEnrichmentSummary();
        }

        var downloadedCount = 0;
        var skippedCount = 0;
        Dictionary<long, TmdbMetadataMatch?> showCache = [];

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var thumbnailPath = Path.Combine(storagePaths.ThumbnailsDirectory, $"{candidate.VideoFileId}.jpg");
            if (File.Exists(thumbnailPath))
            {
                continue;
            }

            var parsedEpisode = MediaNameParser.ParseEpisodeInfo(candidate.FileName, candidate.SortIndex);
            var seasonNumber = candidate.CustomSeasonNumber ?? parsedEpisode.Season;
            var episodeNumber = candidate.CustomEpisodeNumber ?? parsedEpisode.Episode;
            if (!parsedEpisode.IsTvShow || seasonNumber <= 0 || episodeNumber <= 0)
            {
                skippedCount++;
                continue;
            }

            try
            {
                if (!showCache.TryGetValue(candidate.TvShowId, out var showMatch))
                {
                    var lookupTitles = LibraryLookupTitleBuilder.Build(
                        candidate.ShowTitle,
                        candidate.SourceProtocolType,
                        candidate.SourceBasePath,
                        candidate.RelativePath);
                    showMatch = await tmdbMetadataClient.SearchTvShowAsync(
                        lookupTitles,
                        candidate.FirstAirDate,
                        cancellationToken,
                        new TmdbSearchOptions
                        {
                            PreferredSeason = seasonNumber
                        });
                    showCache[candidate.TvShowId] = showMatch;
                }

                if (showMatch is null)
                {
                    skippedCount++;
                    continue;
                }

                var localPath = await tmdbMetadataClient.DownloadEpisodeStillAsync(
                    showMatch.Id,
                    seasonNumber,
                    episodeNumber,
                    candidate.VideoFileId,
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
                {
                    downloadedCount++;
                }
                else
                {
                    skippedCount++;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return new LibraryThumbnailEnrichmentSummary(
                    downloadedCount,
                    skippedCount,
                    EncounteredNetworkError: true,
                    ErrorMessage: ex.Message);
            }
            catch
            {
                skippedCount++;
            }
        }

        return new LibraryThumbnailEnrichmentSummary(downloadedCount, skippedCount);
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
