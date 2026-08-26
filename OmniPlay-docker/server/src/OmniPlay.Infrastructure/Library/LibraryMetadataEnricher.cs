using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.Tmdb;

namespace OmniPlay.Infrastructure.Library;

public sealed class LibraryMetadataEnricher : ILibraryMetadataEnricher
{
    private readonly SqliteDatabase database;
    private readonly IAppSettingsRepository settingsRepository;
    private readonly ITmdbMetadataClient tmdbClient;

    public LibraryMetadataEnricher(
        SqliteDatabase database,
        IAppSettingsRepository settingsRepository,
        ITmdbMetadataClient tmdbClient)
    {
        this.database = database;
        this.settingsRepository = settingsRepository;
        this.tmdbClient = tmdbClient;
    }

    public async Task<LibraryMetadataEnrichmentSummary> EnrichMissingAsync(CancellationToken cancellationToken = default)
    {
        return await EnrichMissingAsync(null, cancellationToken);
    }

    public async Task<LibraryMetadataEnrichmentSummary> EnrichMissingAsync(
        IProgress<LibraryMetadataEnrichmentProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        return await EnrichMissingAsync(progress, new LibraryRefreshRequest(), cancellationToken);
    }

    public async Task<LibraryMetadataEnrichmentSummary> EnrichMissingAsync(
        IProgress<LibraryMetadataEnrichmentProgress>? progress,
        LibraryRefreshRequest order,
        CancellationToken cancellationToken = default)
    {
        var candidates = await LoadCandidatesAsync(
            libraryItemId: null,
            order,
            includeLocked: false,
            cancellationToken);
        return await EnrichAsync(
            candidates,
            progress,
            revealUnmatchedAtEnd: true,
            allowLockedUpdates: false,
            cancellationToken);
    }

    public async Task<LibraryMetadataEnrichmentSummary> EnrichItemAsync(
        string libraryItemId,
        CancellationToken cancellationToken = default)
    {
        return await EnrichItemAsync(libraryItemId, null, cancellationToken);
    }

    public async Task<LibraryMetadataEnrichmentSummary> EnrichItemAsync(
        string libraryItemId,
        IProgress<LibraryMetadataEnrichmentProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var candidates = await LoadCandidatesAsync(
            libraryItemId,
            order: null,
            includeLocked: true,
            cancellationToken);
        return await EnrichAsync(
            candidates,
            progress,
            revealUnmatchedAtEnd: false,
            allowLockedUpdates: true,
            cancellationToken);
    }

    private async Task<LibraryMetadataEnrichmentSummary> EnrichAsync(
        IReadOnlyList<LibraryMetadataCandidate> candidates,
        IProgress<LibraryMetadataEnrichmentProgress>? progress,
        bool revealUnmatchedAtEnd,
        bool allowLockedUpdates,
        CancellationToken cancellationToken)
    {
        var settings = (await settingsRepository.GetAsync(cancellationToken)).Tmdb;
        if (!settings.EnableMetadataEnrichment && !settings.EnablePosterDownloads)
        {
            ReportProgress(progress, "disabled", candidates.Count, 0, 0, 0, 0, null);
            if (revealUnmatchedAtEnd)
            {
                await RevealHiddenLibraryItemsAsync(cancellationToken);
            }

            return new LibraryMetadataEnrichmentSummary(Diagnostics: ["TMDB 刮削已关闭。"]);
        }

        var matched = 0;
        var updated = 0;
        var downloadedPosters = 0;
        var processed = 0;
        List<string> diagnostics = [];
        List<MatchedTvCandidate> matchedTvCandidates = [];

        ReportProgress(progress, "starting", candidates.Count, processed, matched, updated, downloadedPosters, null);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ReportProgress(
                    progress,
                    candidate.TmdbId.HasValue ? "fetching-details" : "searching",
                    candidates.Count,
                    processed,
                    matched,
                    updated,
                    downloadedPosters,
                    candidate);

                var match = await FindMatchAsync(candidate, settings, cancellationToken);
                if (match is null)
                {
                    diagnostics.Add(candidate.TmdbId.HasValue
                        ? $"TMDB 详情刷新失败：{candidate.Title} ({candidate.TmdbId})"
                        : $"未匹配：{candidate.Title}");
                    processed++;
                    ReportProgress(
                        progress,
                        candidate.TmdbId.HasValue ? "fetching-details" : "searching",
                        candidates.Count,
                        processed,
                        matched,
                        updated,
                        downloadedPosters,
                        candidate);
                    continue;
                }

                matched++;
                ReportProgress(progress, "downloading-poster", candidates.Count, processed, matched, updated, downloadedPosters, candidate);
                var posterLocalPath = await TryDownloadMainPosterAsync(candidate, match, settings, allowLockedUpdates, diagnostics, cancellationToken);
                if (NeedsPosterBeforeReveal(candidate, match, settings, allowLockedUpdates) && string.IsNullOrWhiteSpace(posterLocalPath))
                {
                    processed++;
                    ReportProgress(progress, "downloading-poster", candidates.Count, processed, matched, updated, downloadedPosters, candidate);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(posterLocalPath))
                {
                    downloadedPosters++;
                }

                ReportProgress(progress, "updating", candidates.Count, processed, matched, updated, downloadedPosters, candidate);
                var itemUpdated = await UpdateLibraryItemAsync(candidate, match, posterLocalPath, allowLockedUpdates, cancellationToken);
                if (itemUpdated)
                {
                    updated++;
                    if (string.Equals(candidate.ItemKind, "tv", StringComparison.OrdinalIgnoreCase))
                    {
                        matchedTvCandidates.Add(new MatchedTvCandidate(candidate.Id, candidate.Title, match.Id));
                    }
                }

                processed++;
                ReportProgress(progress, "updating", candidates.Count, processed, matched, updated, downloadedPosters, candidate);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsRecoverableMetadataException(ex))
            {
                diagnostics.Add($"刮削失败：{candidate.Title}（{UserFacingErrorMessages.FromException(ex)}）");
                processed++;
                ReportProgress(progress, "failed", candidates.Count, processed, matched, updated, downloadedPosters, candidate);
            }
        }

        if (revealUnmatchedAtEnd)
        {
            await RevealHiddenLibraryItemsAsync(cancellationToken);
        }

        foreach (var tvCandidate in matchedTvCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progressCandidate = new LibraryMetadataCandidate(
                tvCandidate.Id,
                "tv",
                tvCandidate.Title,
                null,
                null,
                null,
                null,
                true,
                tvCandidate.TmdbId,
                null,
                null);
            try
            {
                ReportProgress(progress, "fetching-episodes", candidates.Count, processed, matched, updated, downloadedPosters, progressCandidate);
                downloadedPosters += await RefreshTvEpisodeMetadataAsync(
                    tvCandidate.Id,
                    tvCandidate.Title,
                    tvCandidate.TmdbId,
                    settings,
                    diagnostics,
                    progress,
                    candidates.Count,
                    processed,
                    matched,
                    updated,
                    downloadedPosters,
                    progressCandidate,
                    cancellationToken);
                ReportProgress(progress, "fetching-episodes", candidates.Count, processed, matched, updated, downloadedPosters, progressCandidate);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsRecoverableMetadataException(ex))
            {
                diagnostics.Add($"分集剧照刷新失败：{tvCandidate.Title}（{UserFacingErrorMessages.FromException(ex)}）");
                ReportProgress(progress, "fetching-episodes", candidates.Count, processed, matched, updated, downloadedPosters, progressCandidate);
            }
        }

        return new LibraryMetadataEnrichmentSummary(
            candidates.Count,
            matched,
            updated,
            downloadedPosters,
            diagnostics);
    }

    private async Task<TmdbMetadataMatch?> FindMatchAsync(
        LibraryMetadataCandidate candidate,
        TmdbSettings settings,
        CancellationToken cancellationToken)
    {
        if (candidate.TmdbId.HasValue)
        {
            return await tmdbClient.GetDetailsAsync(
                candidate.ItemKind,
                candidate.TmdbId.Value,
                settings,
                cancellationToken);
        }

        var sourceMetadata = ResolveSourceMetadata(candidate);
        var searchTitle = ResolveSearchTitle(candidate, sourceMetadata);
        var preferredSeason = ResolvePreferredSeason(candidate);
        var secondaryQuery = BuildSecondaryQuery(
            searchTitle,
            sourceMetadata?.ForeignTitle,
            sourceMetadata?.ChineseTitle,
            sourceMetadata?.ParentChineseTitle,
            sourceMetadata?.FullCleanTitle);
        var year = string.Equals(candidate.ItemKind, "tv", StringComparison.OrdinalIgnoreCase) && preferredSeason is 0 or > 1
            ? null
            : ResolveSearchYear(candidate.ReleaseDate, sourceMetadata?.Year);
        return await tmdbClient.SearchAsync(
            candidate.ItemKind,
            searchTitle,
            year,
            settings,
            secondaryQuery,
            cancellationToken);
    }

    private static MediaNameParser.CombinedSearchMetadataResult? ResolveSourceMetadata(LibraryMetadataCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.SourceRelativePath) &&
            string.IsNullOrWhiteSpace(candidate.SourceFileName))
        {
            return null;
        }

        return MediaNameParser.CombinedSearchMetadata(
            candidate.SourceRelativePath ?? string.Empty,
            string.IsNullOrWhiteSpace(candidate.SourceFileName)
                ? Path.GetFileName(candidate.SourceRelativePath) ?? string.Empty
                : candidate.SourceFileName);
    }

    private static string ResolveSearchTitle(
        LibraryMetadataCandidate candidate,
        MediaNameParser.CombinedSearchMetadataResult? sourceMetadata)
    {
        if (MediaNameParser.IsUsableLibraryDisplayTitle(candidate.Title))
        {
            return candidate.Title.Trim();
        }

        return sourceMetadata?.ChineseTitle
               ?? sourceMetadata?.ParentChineseTitle
               ?? sourceMetadata?.ForeignTitle
               ?? sourceMetadata?.FullCleanTitle
               ?? candidate.Title.Trim();
    }

    private static int? ResolvePreferredSeason(LibraryMetadataCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.SourceRelativePath) &&
            string.IsNullOrWhiteSpace(candidate.SourceFileName))
        {
            return null;
        }

        return MediaNameParser.ResolvePreferredSeason(
            candidate.SourceRelativePath ?? string.Empty,
            string.IsNullOrWhiteSpace(candidate.SourceFileName)
                ? Path.GetFileName(candidate.SourceRelativePath) ?? string.Empty
                : candidate.SourceFileName);
    }

    private static string? BuildSecondaryQuery(string primaryQuery, params string?[] candidates)
    {
        var normalizedPrimary = NormalizeSearchKey(primaryQuery);
        foreach (var candidate in candidates)
        {
            var trimmed = candidate?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (!string.Equals(NormalizeSearchKey(trimmed), normalizedPrimary, StringComparison.Ordinal))
            {
                return trimmed;
            }
        }

        return null;
    }

    private async Task<string?> TryDownloadMainPosterAsync(
        LibraryMetadataCandidate candidate,
        TmdbMetadataMatch match,
        TmdbSettings settings,
        bool allowLockedUpdates,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!settings.EnablePosterDownloads || string.IsNullOrWhiteSpace(match.PosterPath))
        {
            if (NeedsPosterBeforeReveal(candidate, match, settings, allowLockedUpdates))
            {
                diagnostics.Add($"主海报缺失：{candidate.Title}");
            }

            return null;
        }

        try
        {
            return await tmdbClient.DownloadPosterAsync(match.PosterPath, match.MediaType, match.Id, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverableMetadataException(ex))
        {
            diagnostics.Add($"主海报下载失败：{candidate.Title}（{UserFacingErrorMessages.FromException(ex)}）");
            return null;
        }
    }

    private static bool NeedsPosterBeforeReveal(
        LibraryMetadataCandidate candidate,
        TmdbMetadataMatch match,
        TmdbSettings settings,
        bool allowLockedUpdates)
    {
        return settings.EnablePosterDownloads
               && !allowLockedUpdates
               && !candidate.IsVisible
               && string.IsNullOrWhiteSpace(candidate.PosterAssetId);
    }

    private static string? ResolveSearchYear(string? currentReleaseDate, string? sourceYear)
    {
        var currentYear = NormalizeYear(currentReleaseDate);
        return string.IsNullOrWhiteSpace(currentYear) ? NormalizeYear(sourceYear) : currentYear;
    }

    private static string? NormalizeYear(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        return trimmed.Length >= 4 ? trimmed[..4] : trimmed;
    }

    private static string NormalizeSearchKey(string value)
    {
        return Regex.Replace(value.Trim().ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", string.Empty);
    }

    private async Task<IReadOnlyList<LibraryMetadataCandidate>> LoadCandidatesAsync(
        string? libraryItemId,
        LibraryRefreshRequest? order,
        bool includeLocked,
        CancellationToken cancellationToken)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT li.id,
                   li.item_kind,
                   li.title,
                   li.release_date,
                   li.overview,
                   li.poster_asset_id,
                   li.vote_average,
                   li.is_visible,
                   CASE
                       WHEN li.item_kind = 'tv' THEN tv.tmdb_id
                       ELSE m.tmdb_id
                   END AS tmdb_id,
                   (
                       SELECT vf.relative_path
                       FROM video_files vf
                       WHERE vf.library_item_id = li.id
                         AND vf.missing_at IS NULL
                       ORDER BY vf.relative_path COLLATE NOCASE ASC
                       LIMIT 1
                   ) AS source_relative_path,
                   (
                       SELECT vf.file_name
                       FROM video_files vf
                       WHERE vf.library_item_id = li.id
                         AND vf.missing_at IS NULL
                       ORDER BY vf.relative_path COLLATE NOCASE ASC
                       LIMIT 1
                   ) AS source_file_name
            FROM library_items li
            LEFT JOIN movies m ON m.library_item_id = li.id
            LEFT JOIN tv_shows tv ON tv.library_item_id = li.id
            WHERE ($includeLocked = 1 OR li.is_locked = 0)
              AND ($id IS NULL OR li.id = $id)
            ORDER BY {BuildCandidateOrderBy(order)};
            """;
        command.Parameters.AddWithValue("$id", string.IsNullOrWhiteSpace(libraryItemId) ? DBNull.Value : libraryItemId);
        command.Parameters.AddWithValue("$includeLocked", includeLocked ? 1 : 0);

        List<LibraryMetadataCandidate> candidates = [];
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new LibraryMetadataCandidate(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                !reader.IsDBNull(7) && reader.GetInt32(7) != 0,
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return candidates;
    }

    private async Task<bool> UpdateLibraryItemAsync(
        LibraryMetadataCandidate candidate,
        TmdbMetadataMatch match,
        string? posterLocalPath,
        bool allowLockedUpdates,
        CancellationToken cancellationToken)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var posterAssetId = candidate.PosterAssetId;

        if (!string.IsNullOrWhiteSpace(posterLocalPath))
        {
            posterAssetId = StableId.Create("poster", match.MediaType, match.Id.ToString(), match.PosterPath ?? posterLocalPath);
            using var poster = connection.CreateCommand();
            poster.Transaction = transaction;
            poster.CommandText = """
                INSERT INTO poster_assets (id, remote_path, local_path, width, height, created_at)
                VALUES ($id, $remotePath, $localPath, NULL, NULL, $createdAt)
                ON CONFLICT(id) DO UPDATE SET
                    remote_path = excluded.remote_path,
                    local_path = excluded.local_path;
                """;
            poster.Parameters.AddWithValue("$id", posterAssetId);
            poster.Parameters.AddWithValue("$remotePath", match.PosterPath ?? (object)DBNull.Value);
            poster.Parameters.AddWithValue("$localPath", posterLocalPath);
            poster.Parameters.AddWithValue("$createdAt", now);
            await poster.ExecuteNonQueryAsync(cancellationToken);
        }

        var displayTitle = ResolveDisplayTitle(candidate, match);
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE library_items
            SET title = $title,
                sort_title = $sortTitle,
                release_date = COALESCE($releaseDate, release_date),
                overview = COALESCE($overview, overview),
                poster_asset_id = COALESCE($posterAssetId, poster_asset_id),
                vote_average = COALESCE($voteAverage, vote_average),
                is_locked = 1,
                is_visible = 1,
                updated_at = $updatedAt
            WHERE id = $id
              AND ($allowLockedUpdates = 1 OR is_locked = 0);
            """;
        update.Parameters.AddWithValue("$id", candidate.Id);
        update.Parameters.AddWithValue("$title", displayTitle);
        update.Parameters.AddWithValue("$sortTitle", displayTitle.Trim().ToLowerInvariant());
        update.Parameters.AddWithValue("$releaseDate", string.IsNullOrWhiteSpace(match.ReleaseDate) ? DBNull.Value : match.ReleaseDate);
        update.Parameters.AddWithValue("$overview", string.IsNullOrWhiteSpace(match.Overview) ? DBNull.Value : match.Overview);
        update.Parameters.AddWithValue("$posterAssetId", string.IsNullOrWhiteSpace(posterAssetId) ? DBNull.Value : posterAssetId);
        update.Parameters.AddWithValue("$voteAverage", match.VoteAverage ?? (object)DBNull.Value);
        update.Parameters.AddWithValue("$allowLockedUpdates", allowLockedUpdates ? 1 : 0);
        update.Parameters.AddWithValue("$updatedAt", now);
        var updated = await update.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (updated)
        {
            await UpdateTmdbIdAsync(connection, transaction, candidate.Id, candidate.ItemKind, match.Id, cancellationToken);
            if (string.Equals(candidate.ItemKind, "tv", StringComparison.OrdinalIgnoreCase))
            {
                await TvLibraryItemMerger.MergeDuplicatesByTmdbIdAsync(
                    connection,
                    transaction,
                    candidate.Id,
                    match.Id,
                    now,
                    cancellationToken);
            }
            else
            {
                await MergeDuplicateMovieItemsByTmdbIdAsync(
                    connection,
                    transaction,
                    candidate.Id,
                    match.Id,
                    now,
                    cancellationToken);
            }
        }

        transaction.Commit();
        return updated;
    }

    private static string ResolveDisplayTitle(LibraryMetadataCandidate candidate, TmdbMetadataMatch match)
    {
        var normalizedMatchTitle = ChineseTextNormalizer.NormalizeTitle(match.Title);
        if (ContainsHan(normalizedMatchTitle))
        {
            return normalizedMatchTitle;
        }

        var sourceMetadata = ResolveSourceMetadata(candidate);
        var sourceChineseTitle = sourceMetadata?.ChineseTitle ?? sourceMetadata?.ParentChineseTitle;
        return string.IsNullOrWhiteSpace(sourceChineseTitle)
            ? normalizedMatchTitle
            : ChineseTextNormalizer.NormalizeTitle(sourceChineseTitle);
    }

    private static bool ContainsHan(string? input)
    {
        return !string.IsNullOrWhiteSpace(input) && Regex.IsMatch(input, @"\p{IsCJKUnifiedIdeographs}");
    }

    private async Task<int> RefreshTvEpisodeMetadataAsync(
        string libraryItemId,
        string title,
        int tvTmdbId,
        TmdbSettings settings,
        List<string> diagnostics,
        IProgress<LibraryMetadataEnrichmentProgress>? progress,
        int targetItemCount,
        int processedItemCount,
        int matchedItemCount,
        int updatedItemCount,
        int baseDownloadedPosterCount,
        LibraryMetadataCandidate progressCandidate,
        CancellationToken cancellationToken)
    {
        var localSeasons = await LoadLocalTvSeasonsAsync(libraryItemId, cancellationToken);
        if (localSeasons.Count == 0)
        {
            return 0;
        }

        var downloadedImages = 0;
        var remoteSeasons = new List<RemoteSeasonRefresh>();
        foreach (var season in localSeasons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var remoteSeason = await tmdbClient.GetSeasonAsync(
                    tvTmdbId,
                    season.SeasonNumber,
                    settings,
                    cancellationToken);
                if (remoteSeason is null)
                {
                    continue;
                }

                remoteSeasons.Add(new RemoteSeasonRefresh(season, remoteSeason));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsRecoverableMetadataException(ex))
            {
                diagnostics.Add($"分季刷新失败：{title} 第 {season.SeasonNumber} 季（{UserFacingErrorMessages.FromException(ex)}）");
            }
        }

        var episodeTargetCount = remoteSeasons.Sum(static item =>
            item.RemoteSeason.Episodes.Count(episode => item.LocalSeason.Episodes.ContainsKey(episode.EpisodeNumber)));
        var episodeProcessedCount = 0;
        ReportProgress(
            progress,
            "fetching-episodes",
            targetItemCount,
            processedItemCount,
            matchedItemCount,
            updatedItemCount,
            baseDownloadedPosterCount + downloadedImages,
            progressCandidate,
            episodeTargetCount,
            episodeProcessedCount);

        foreach (var refresh in remoteSeasons)
        {
            var season = refresh.LocalSeason;
            var remoteSeason = refresh.RemoteSeason;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string? seasonPosterLocalPath = null;
                if (settings.EnablePosterDownloads && !string.IsNullOrWhiteSpace(remoteSeason.PosterPath))
                {
                    seasonPosterLocalPath = await TryDownloadSeasonPosterAsync(
                        title,
                        tvTmdbId,
                        season.SeasonNumber,
                        remoteSeason.PosterPath,
                        diagnostics,
                        cancellationToken);
                    if (!string.IsNullOrWhiteSpace(seasonPosterLocalPath))
                    {
                        downloadedImages++;
                    }
                }

                var episodeStillLocalPaths = new Dictionary<int, string>(capacity: remoteSeason.Episodes.Count);
                if (settings.EnablePosterDownloads)
                {
                    foreach (var episode in remoteSeason.Episodes)
                    {
                        if (!season.Episodes.ContainsKey(episode.EpisodeNumber))
                        {
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(episode.StillPath))
                        {
                            var stillLocalPath = await TryDownloadEpisodeStillAsync(
                                title,
                                tvTmdbId,
                                season.SeasonNumber,
                                episode,
                                diagnostics,
                                cancellationToken);
                            if (!string.IsNullOrWhiteSpace(stillLocalPath))
                            {
                                episodeStillLocalPaths[episode.EpisodeNumber] = stillLocalPath;
                                downloadedImages++;
                            }
                        }

                        episodeProcessedCount++;
                        ReportProgress(
                            progress,
                            "fetching-episodes",
                            targetItemCount,
                            processedItemCount,
                            matchedItemCount,
                            updatedItemCount,
                            baseDownloadedPosterCount + downloadedImages,
                            progressCandidate,
                            episodeTargetCount,
                            episodeProcessedCount);
                    }
                }
                else
                {
                    foreach (var episode in remoteSeason.Episodes)
                    {
                        if (!season.Episodes.ContainsKey(episode.EpisodeNumber))
                        {
                            continue;
                        }

                        episodeProcessedCount++;
                        ReportProgress(
                            progress,
                            "fetching-episodes",
                            targetItemCount,
                            processedItemCount,
                            matchedItemCount,
                            updatedItemCount,
                            baseDownloadedPosterCount + downloadedImages,
                            progressCandidate,
                            episodeTargetCount,
                            episodeProcessedCount);
                    }
                }

                await UpdateSeasonEpisodeMetadataAsync(
                    season,
                    remoteSeason,
                    title,
                    tvTmdbId,
                    seasonPosterLocalPath,
                    episodeStillLocalPaths,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsRecoverableMetadataException(ex))
            {
                diagnostics.Add($"分季刷新失败：{title} 第 {season.SeasonNumber} 季（{UserFacingErrorMessages.FromException(ex)}）");
            }
        }

        return downloadedImages;
    }

    private async Task<string?> TryDownloadSeasonPosterAsync(
        string title,
        int tvTmdbId,
        int seasonNumber,
        string posterPath,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            return await tmdbClient.DownloadPosterAsync(posterPath, "tv", tvTmdbId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverableMetadataException(ex))
        {
            diagnostics.Add($"分季海报下载失败：{title} 第 {seasonNumber} 季（{UserFacingErrorMessages.FromException(ex)}）");
            return null;
        }
    }

    private async Task<string?> TryDownloadEpisodeStillAsync(
        string title,
        int tvTmdbId,
        int seasonNumber,
        TmdbEpisodeDetail episode,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            return await tmdbClient.DownloadStillAsync(
                episode.StillPath ?? string.Empty,
                tvTmdbId,
                seasonNumber,
                episode.EpisodeNumber,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverableMetadataException(ex))
        {
            diagnostics.Add($"分集剧照下载失败：{title} S{seasonNumber:00}E{episode.EpisodeNumber:00}（{UserFacingErrorMessages.FromException(ex)}）");
            return null;
        }
    }

    private async Task<IReadOnlyList<LocalSeason>> LoadLocalTvSeasonsAsync(
        string libraryItemId,
        CancellationToken cancellationToken)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id,
                   s.season_number,
                   e.id,
                   e.episode_number,
                   vf.id
            FROM tv_shows tv
            JOIN seasons s ON s.tv_show_id = tv.id
            LEFT JOIN episodes e ON e.season_id = s.id
            LEFT JOIN video_files vf ON vf.episode_id = e.id AND vf.missing_at IS NULL
            WHERE tv.library_item_id = $libraryItemId
            ORDER BY s.season_number ASC, e.episode_number ASC;
            """;
        command.Parameters.AddWithValue("$libraryItemId", libraryItemId);

        Dictionary<string, LocalSeason> seasons = [];
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var seasonId = reader.GetString(0);
            if (!seasons.TryGetValue(seasonId, out var season))
            {
                season = new LocalSeason(seasonId, reader.GetInt32(1));
                seasons[seasonId] = season;
            }

            if (!reader.IsDBNull(2))
            {
                var episodeNumber = reader.GetInt32(3);
                season.Episodes.TryAdd(
                    episodeNumber,
                    new LocalEpisode(
                        reader.GetString(2),
                        episodeNumber,
                        reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        return seasons.Values.OrderBy(static season => season.SeasonNumber).ToArray();
    }

    private async Task UpdateSeasonEpisodeMetadataAsync(
        LocalSeason localSeason,
        TmdbSeasonDetail remoteSeason,
        string showTitle,
        int tvTmdbId,
        string? seasonPosterLocalPath,
        IReadOnlyDictionary<int, string> episodeStillLocalPaths,
        CancellationToken cancellationToken)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");
        string? seasonPosterAssetId = null;
        if (!string.IsNullOrWhiteSpace(seasonPosterLocalPath))
        {
            seasonPosterAssetId = StableId.Create(
                "poster",
                "tv-season",
                tvTmdbId.ToString(),
                localSeason.SeasonNumber.ToString(),
                remoteSeason.PosterPath ?? seasonPosterLocalPath);
            using var posterCommand = connection.CreateCommand();
            posterCommand.Transaction = transaction;
            posterCommand.CommandText = """
                INSERT INTO poster_assets (id, remote_path, local_path, width, height, created_at)
                VALUES ($id, $remotePath, $localPath, NULL, NULL, $createdAt)
                ON CONFLICT(id) DO UPDATE SET
                    remote_path = excluded.remote_path,
                    local_path = excluded.local_path;
                """;
            posterCommand.Parameters.AddWithValue("$id", seasonPosterAssetId);
            posterCommand.Parameters.AddWithValue("$remotePath", NullIfWhiteSpace(remoteSeason.PosterPath));
            posterCommand.Parameters.AddWithValue("$localPath", seasonPosterLocalPath);
            posterCommand.Parameters.AddWithValue("$createdAt", now);
            await posterCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        using var seasonCommand = connection.CreateCommand();
        seasonCommand.Transaction = transaction;
        seasonCommand.CommandText = """
            UPDATE seasons
            SET title = COALESCE($title, title),
                poster_asset_id = COALESCE($posterAssetId, poster_asset_id)
            WHERE id = $id;
            """;
        seasonCommand.Parameters.AddWithValue("$id", localSeason.Id);
        seasonCommand.Parameters.AddWithValue("$title", NullIfWhiteSpace(ResolveSeasonTitle(showTitle, localSeason.SeasonNumber, remoteSeason.Title)));
        seasonCommand.Parameters.AddWithValue("$posterAssetId", NullIfWhiteSpace(seasonPosterAssetId));
        await seasonCommand.ExecuteNonQueryAsync(cancellationToken);

        foreach (var episode in remoteSeason.Episodes)
        {
            if (!localSeason.Episodes.TryGetValue(episode.EpisodeNumber, out var localEpisode))
            {
                continue;
            }

            string? stillAssetId = null;
            if (episodeStillLocalPaths.TryGetValue(episode.EpisodeNumber, out var stillLocalPath))
            {
                stillAssetId = StableId.Create(
                    "thumbnail",
                    "tv",
                    tvTmdbId.ToString(),
                    localSeason.SeasonNumber.ToString(),
                    episode.EpisodeNumber.ToString(),
                    episode.StillPath ?? stillLocalPath);
                using var thumbnailCommand = connection.CreateCommand();
                thumbnailCommand.Transaction = transaction;
                thumbnailCommand.CommandText = """
                    INSERT INTO thumbnail_assets (id, video_file_id, local_path, width, height, created_at)
                    VALUES ($id, $videoFileId, $localPath, NULL, NULL, $createdAt)
                    ON CONFLICT(id) DO UPDATE SET
                        video_file_id = excluded.video_file_id,
                        local_path = excluded.local_path;
                    """;
                thumbnailCommand.Parameters.AddWithValue("$id", stillAssetId);
                thumbnailCommand.Parameters.AddWithValue("$videoFileId", NullIfWhiteSpace(localEpisode.VideoFileId));
                thumbnailCommand.Parameters.AddWithValue("$localPath", stillLocalPath);
                thumbnailCommand.Parameters.AddWithValue("$createdAt", now);
                await thumbnailCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            using var episodeCommand = connection.CreateCommand();
            episodeCommand.Transaction = transaction;
            episodeCommand.CommandText = """
                UPDATE episodes
                SET title = CASE
                        WHEN $title IS NULL THEN title
                        WHEN title IS NOT NULL AND INSTR(title, '·') > 0 THEN $title || SUBSTR(title, INSTR(title, '·'))
                        ELSE $title
                    END,
                    overview = COALESCE($overview, overview),
                    still_asset_id = COALESCE($stillAssetId, still_asset_id),
                    air_date = COALESCE($airDate, air_date)
                WHERE season_id = $seasonId
                  AND episode_number = $episodeNumber;
                """;
            episodeCommand.Parameters.AddWithValue("$seasonId", localSeason.Id);
            episodeCommand.Parameters.AddWithValue("$episodeNumber", episode.EpisodeNumber);
            episodeCommand.Parameters.AddWithValue("$title", NullIfWhiteSpace(episode.Title));
            episodeCommand.Parameters.AddWithValue("$overview", NullIfWhiteSpace(episode.Overview));
            episodeCommand.Parameters.AddWithValue("$stillAssetId", NullIfWhiteSpace(stillAssetId));
            episodeCommand.Parameters.AddWithValue("$airDate", NullIfWhiteSpace(episode.AirDate));
            await episodeCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
    }

    private static string? ResolveSeasonTitle(string showTitle, int seasonNumber, string? remoteTitle)
    {
        if (string.IsNullOrWhiteSpace(remoteTitle))
        {
            return null;
        }

        var normalizedRemoteTitle = ChineseTextNormalizer.NormalizeTitle(remoteTitle);
        var normalizedShowTitle = ChineseTextNormalizer.NormalizeTitle(showTitle);
        if (!string.IsNullOrWhiteSpace(normalizedShowTitle)
            && normalizedRemoteTitle.Contains(normalizedShowTitle, StringComparison.Ordinal))
        {
            return seasonNumber == 0 ? "特别篇" : $"第 {seasonNumber} 季";
        }

        return normalizedRemoteTitle;
    }

    private static async Task UpdateTmdbIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string libraryItemId,
        string itemKind,
        int tmdbId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var isTv = string.Equals(itemKind, "tv", StringComparison.OrdinalIgnoreCase);
        command.CommandText = isTv
            ? """
                INSERT INTO tv_shows (id, library_item_id, tmdb_id, original_name, first_air_date)
                VALUES ($id, $libraryItemId, $tmdbId, NULL, NULL)
                ON CONFLICT(id) DO UPDATE SET
                    library_item_id = excluded.library_item_id,
                    tmdb_id = excluded.tmdb_id;
                """
            : """
                INSERT INTO movies (id, library_item_id, tmdb_id, original_title, runtime_seconds)
                VALUES ($id, $libraryItemId, $tmdbId, NULL, NULL)
                ON CONFLICT(id) DO UPDATE SET
                    library_item_id = excluded.library_item_id,
                    tmdb_id = excluded.tmdb_id;
                """;
        command.Parameters.AddWithValue("$id", StableId.Create(isTv ? "show" : "movie", libraryItemId));
        command.Parameters.AddWithValue("$libraryItemId", libraryItemId);
        command.Parameters.AddWithValue("$tmdbId", tmdbId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> MergeDuplicateMovieItemsByTmdbIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string canonicalLibraryItemId,
        int tmdbId,
        string updatedAt,
        CancellationToken cancellationToken)
    {
        if (tmdbId <= 0 || string.IsNullOrWhiteSpace(canonicalLibraryItemId))
        {
            return false;
        }

        await ExecuteMergeCommandAsync(connection, transaction, """
            CREATE TEMP TABLE IF NOT EXISTS merge_duplicate_movie_item_ids (
                id TEXT PRIMARY KEY
            );
            """, cancellationToken);
        await ExecuteMergeCommandAsync(connection, transaction, "DELETE FROM merge_duplicate_movie_item_ids;", cancellationToken);
        await ExecuteMergeCommandAsync(connection, transaction, """
            INSERT OR IGNORE INTO merge_duplicate_movie_item_ids (id)
            SELECT DISTINCT m.library_item_id
            FROM movies m
            JOIN library_items li ON li.id = m.library_item_id
            WHERE m.tmdb_id = $tmdbId
              AND m.library_item_id <> $canonicalLibraryItemId
              AND li.item_kind = 'movie'
              AND EXISTS (
                  SELECT 1
                  FROM video_files vf
                  WHERE vf.library_item_id = m.library_item_id
                    AND vf.media_kind = 'movie'
                    AND vf.missing_at IS NULL
              );
            """,
            cancellationToken,
            ("$tmdbId", tmdbId),
            ("$canonicalLibraryItemId", canonicalLibraryItemId));

        var duplicateCount = await ExecuteMergeScalarAsync(connection, transaction, """
            SELECT COUNT(*)
            FROM merge_duplicate_movie_item_ids;
            """, cancellationToken);
        if (duplicateCount <= 0)
        {
            return false;
        }

        await ExecuteMergeCommandAsync(connection, transaction, """
            UPDATE video_files
            SET library_item_id = $canonicalLibraryItemId,
                episode_id = NULL,
                media_kind = 'movie',
                updated_at = $updatedAt
            WHERE library_item_id IN (SELECT id FROM merge_duplicate_movie_item_ids)
              AND media_kind = 'movie';
            """,
            cancellationToken,
            ("$canonicalLibraryItemId", canonicalLibraryItemId),
            ("$updatedAt", updatedAt));
        await ExecuteMergeCommandAsync(connection, transaction, """
            DELETE FROM movies
            WHERE library_item_id IN (SELECT id FROM merge_duplicate_movie_item_ids);
            """, cancellationToken);
        await ExecuteMergeCommandAsync(connection, transaction, """
            DELETE FROM library_items
            WHERE id IN (SELECT id FROM merge_duplicate_movie_item_ids)
              AND NOT EXISTS (
                  SELECT 1
                  FROM video_files vf
                  WHERE vf.library_item_id = library_items.id
                    AND vf.missing_at IS NULL
              );
            """, cancellationToken);
        return true;
    }

    private static async Task ExecuteMergeCommandAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ExecuteMergeScalarAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static object NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    }

    private static bool IsRecoverableMetadataException(Exception ex)
    {
        return ex is HttpRequestException
            or IOException
            or JsonException
            or TaskCanceledException
            or UnauthorizedAccessException;
    }

    private async Task RevealHiddenLibraryItemsAsync(CancellationToken cancellationToken)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE library_items
            SET is_visible = 1,
                updated_at = $updatedAt
            WHERE is_visible = 0;
            """;
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildCandidateOrderBy(LibraryRefreshRequest? order)
    {
        var sortKey = order?.SortKey?.Trim().ToLowerInvariant();
        var descending = !string.Equals(order?.SortDirection, "asc", StringComparison.OrdinalIgnoreCase);
        return sortKey switch
        {
            "title" => descending
                ? "li.sort_title COLLATE NOCASE DESC, li.updated_at ASC"
                : "li.sort_title COLLATE NOCASE ASC, li.updated_at ASC",
            "rating" => descending
                ? "li.vote_average IS NULL ASC, li.vote_average DESC, li.sort_title COLLATE NOCASE ASC"
                : "li.vote_average IS NULL ASC, li.vote_average ASC, li.sort_title COLLATE NOCASE ASC",
            _ => descending
                ? "li.release_date IS NULL ASC, li.release_date DESC, li.sort_title COLLATE NOCASE ASC"
                : "li.release_date IS NULL ASC, li.release_date ASC, li.sort_title COLLATE NOCASE ASC"
        };
    }

    private sealed record LibraryMetadataCandidate(
        string Id,
        string ItemKind,
        string Title,
        string? ReleaseDate,
        string? Overview,
        string? PosterAssetId,
        double? VoteAverage,
        bool IsVisible,
        int? TmdbId,
        string? SourceRelativePath,
        string? SourceFileName);

    private sealed record MatchedTvCandidate(string Id, string Title, int TmdbId);

    private sealed record RemoteSeasonRefresh(LocalSeason LocalSeason, TmdbSeasonDetail RemoteSeason);

    private sealed record LocalSeason(string Id, int SeasonNumber)
    {
        public Dictionary<int, LocalEpisode> Episodes { get; } = [];
    }

    private sealed record LocalEpisode(string Id, int EpisodeNumber, string? VideoFileId);

    private static void ReportProgress(
        IProgress<LibraryMetadataEnrichmentProgress>? progress,
        string phase,
        int targetItemCount,
        int processedItemCount,
        int matchedItemCount,
        int updatedItemCount,
        int downloadedPosterCount,
        LibraryMetadataCandidate? candidate,
        int? phaseTargetCount = null,
        int? phaseProcessedCount = null)
    {
        progress?.Report(new LibraryMetadataEnrichmentProgress(
            phase,
            targetItemCount,
            processedItemCount,
            matchedItemCount,
            updatedItemCount,
            downloadedPosterCount,
            candidate?.Id,
            candidate?.Title,
            DateTimeOffset.UtcNow,
            phaseTargetCount,
            phaseProcessedCount));
    }
}
