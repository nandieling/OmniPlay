using Dapper;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Core.Settings;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.Tmdb;

namespace OmniPlay.Infrastructure.Library;

public sealed class LibraryMetadataEnricher : ILibraryMetadataEnricher
{
    private readonly SqliteDatabase database;
    private readonly ITmdbMetadataClient tmdbMetadataClient;

    public LibraryMetadataEnricher(SqliteDatabase database, ITmdbMetadataClient tmdbMetadataClient)
    {
        this.database = database;
        this.tmdbMetadataClient = tmdbMetadataClient;
    }

    public async Task<LibraryMetadataEnrichmentSummary> EnrichMissingMetadataAsync(
        TmdbSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        settings ??= new TmdbSettings();
        if (!settings.EnableMetadataEnrichment && !settings.EnablePosterDownloads)
        {
            return new LibraryMetadataEnrichmentSummary();
        }

        var state = new EnrichmentState();
        var movies = await LoadMovieCandidatesAsync(movieId: null, cancellationToken);
        await EnrichMoviesAsync(movies, state, settings, cancellationToken);

        if (!state.EncounteredNetworkError)
        {
            var tvShows = await LoadTvShowCandidatesAsync(tvShowId: null, cancellationToken);
            await EnrichTvShowsAsync(tvShows, state, settings, cancellationToken);
        }

        return state.ToSummary();
    }

    public async Task<LibraryMetadataEnrichmentSummary> EnrichMissingMovieMetadataAsync(
        long movieId,
        TmdbSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        settings ??= new TmdbSettings();
        if (movieId == 0 || (!settings.EnableMetadataEnrichment && !settings.EnablePosterDownloads))
        {
            return new LibraryMetadataEnrichmentSummary();
        }

        var state = new EnrichmentState();
        var movies = await LoadMovieCandidatesAsync(movieId, cancellationToken);
        await EnrichMoviesAsync(movies, state, settings, cancellationToken);
        return state.ToSummary();
    }

    public async Task<LibraryMetadataEnrichmentSummary> EnrichMissingTvShowMetadataAsync(
        long tvShowId,
        TmdbSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        settings ??= new TmdbSettings();
        if (tvShowId == 0 || (!settings.EnableMetadataEnrichment && !settings.EnablePosterDownloads))
        {
            return new LibraryMetadataEnrichmentSummary();
        }

        var state = new EnrichmentState();
        var tvShows = await LoadTvShowCandidatesAsync(tvShowId, cancellationToken);
        await EnrichTvShowsAsync(tvShows, state, settings, cancellationToken);
        return state.ToSummary();
    }

    private async Task EnrichMoviesAsync(
        IReadOnlyList<MovieMetadataCandidate> candidates,
        EnrichmentState state,
        TmdbSettings settings,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates.Where(candidate => NeedsMovieRefresh(candidate, settings)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var lookupTitles = LibraryLookupTitleBuilder.Build(
                    candidate.Title,
                    candidate.SourceProtocolType,
                    candidate.BaseUrl,
                    candidate.RelativePath);
                var searchYear = ResolveMovieSearchYear(candidate);
                var match = await tmdbMetadataClient.SearchMovieAsync(
                    lookupTitles,
                    searchYear,
                    cancellationToken,
                    new TmdbSearchOptions
                    {
                        SettingsOverride = settings
                    });
                if (match is null || !LibraryTmdbMatchGuard.IsYearPlausibleMatch(match, searchYear, "movie"))
                {
                    continue;
                }

                var posterPath = await ResolvePosterPathAsync(candidate.PosterPath, match, settings, cancellationToken);
                var result = await UpdateMovieAsync(candidate, match, posterPath, settings, cancellationToken);
                if (result.UpdatedMetadata)
                {
                    state.UpdatedMovies++;
                }

                if (result.DownloadedPoster)
                {
                    state.DownloadedPosters++;
                }
            }
            catch (Exception ex) when (ShouldStopOnExternalFailure(ex))
            {
                state.MarkNetworkError(ex.Message);
                return;
            }
            catch
            {
                // 元数据补全失败不应影响库扫描主流程。
            }
        }
    }

    private async Task EnrichTvShowsAsync(
        IReadOnlyList<TvShowMetadataCandidate> candidates,
        EnrichmentState state,
        TmdbSettings settings,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates.Where(candidate => NeedsTvShowRefresh(candidate, settings)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var lookupTitles = LibraryLookupTitleBuilder.Build(
                    candidate.Title,
                    candidate.SourceProtocolType,
                    candidate.BaseUrl,
                    candidate.RelativePath);
                var searchYear = ResolveTvShowSearchYear(candidate);
                var preferredSeason = ResolvePreferredSeason(candidate.RelativePath);
                var match = await tmdbMetadataClient.SearchTvShowAsync(
                    lookupTitles,
                    searchYear,
                    cancellationToken,
                    new TmdbSearchOptions
                    {
                        PreferredSeason = preferredSeason,
                        SettingsOverride = settings
                    });
                if (match is null ||
                    !LibraryTmdbMatchGuard.IsYearPlausibleMatch(
                        match,
                        searchYear,
                        "tv",
                        preferredSeason,
                        tolerance: 10))
                {
                    continue;
                }

                var posterPath = await ResolvePosterPathAsync(candidate.PosterPath, match, settings, cancellationToken);
                var result = await UpdateTvShowAsync(candidate, match, posterPath, settings, cancellationToken);
                if (result.UpdatedMetadata)
                {
                    state.UpdatedTvShows++;
                }

                if (result.DownloadedPoster)
                {
                    state.DownloadedPosters++;
                }
            }
            catch (Exception ex) when (ShouldStopOnExternalFailure(ex))
            {
                state.MarkNetworkError(ex.Message);
                return;
            }
            catch
            {
                // 元数据补全失败不应影响库扫描主流程。
            }
        }
    }

    private async Task<string?> ResolvePosterPathAsync(
        string? currentPosterPath,
        TmdbMetadataMatch match,
        TmdbSettings settings,
        CancellationToken cancellationToken)
    {
        if (IsUsableLocalPoster(currentPosterPath))
        {
            return currentPosterPath;
        }

        if (!settings.EnablePosterDownloads)
        {
            return currentPosterPath;
        }

        return string.IsNullOrWhiteSpace(match.PosterPath)
            ? currentPosterPath
            : await tmdbMetadataClient.DownloadPosterAsync(
                match.PosterPath,
                match.MediaType,
                match.Id,
                cancellationToken);
    }

    private async Task<IReadOnlyList<MovieMetadataCandidate>> LoadMovieCandidatesAsync(
        long? movieId,
        CancellationToken cancellationToken)
    {
        using var connection = database.OpenConnection();
        var filterByMovie = movieId.HasValue
            ? "AND movie.id = @MovieId"
            : string.Empty;
        var sql =
            $"""
                SELECT movie.id AS Id,
                       movie.title AS Title,
                       movie.releaseDate AS ReleaseDate,
                       movie.overview AS Overview,
                       movie.posterPath AS PosterPath,
                       movie.voteAverage AS VoteAverage,
                       movie.productionCountryCodes AS ProductionCountryCodes,
                       movie.originalLanguage AS OriginalLanguage,
                       movie.metadataLanguage AS MetadataLanguage,
                       (
                           SELECT ms.protocolType
                           FROM videoFile vf
                           JOIN mediaSource ms ON ms.id = vf.sourceId
                           WHERE vf.movieId = movie.id AND vf.mediaType = 'movie'
                           ORDER BY vf.relativePath COLLATE NOCASE ASC
                           LIMIT 1
                       ) AS SourceProtocolType,
                       (
                           SELECT ms.baseUrl
                           FROM videoFile vf
                           JOIN mediaSource ms ON ms.id = vf.sourceId
                           WHERE vf.movieId = movie.id AND vf.mediaType = 'movie'
                           ORDER BY vf.relativePath COLLATE NOCASE ASC
                           LIMIT 1
                       ) AS BaseUrl,
                       (
                           SELECT vf.relativePath
                           FROM videoFile vf
                           WHERE vf.movieId = movie.id AND vf.mediaType = 'movie'
                           ORDER BY vf.relativePath COLLATE NOCASE ASC
                           LIMIT 1
                       ) AS RelativePath
                FROM movie
                WHERE movie.isLocked = 0
                  {filterByMovie}
                  AND EXISTS (SELECT 1 FROM videoFile WHERE movieId = movie.id AND mediaType = 'movie')
                ORDER BY movie.title COLLATE NOCASE ASC
                """;
        var command = movieId.HasValue
            ? new CommandDefinition(sql, new { MovieId = movieId.Value }, cancellationToken: cancellationToken)
            : new CommandDefinition(sql, cancellationToken: cancellationToken);
        var candidates = await connection.QueryAsync<MovieMetadataCandidate>(command);
        return candidates.ToList();
    }

    private async Task<IReadOnlyList<TvShowMetadataCandidate>> LoadTvShowCandidatesAsync(
        long? tvShowId,
        CancellationToken cancellationToken)
    {
        using var connection = database.OpenConnection();
        var filterByTvShow = tvShowId.HasValue
            ? "AND tvShow.id = @TvShowId"
            : string.Empty;
        var sql =
            $"""
                SELECT tvShow.id AS Id,
                       tvShow.title AS Title,
                       tvShow.firstAirDate AS FirstAirDate,
                       tvShow.overview AS Overview,
                       tvShow.posterPath AS PosterPath,
                       tvShow.voteAverage AS VoteAverage,
                       tvShow.productionCountryCodes AS ProductionCountryCodes,
                       tvShow.originalLanguage AS OriginalLanguage,
                       tvShow.metadataLanguage AS MetadataLanguage,
                       (
                           SELECT ms.protocolType
                           FROM videoFile vf
                           JOIN mediaSource ms ON ms.id = vf.sourceId
                           WHERE vf.episodeId = tvShow.id AND vf.mediaType = 'tv'
                           ORDER BY vf.relativePath COLLATE NOCASE ASC
                           LIMIT 1
                       ) AS SourceProtocolType,
                       (
                           SELECT ms.baseUrl
                           FROM videoFile vf
                           JOIN mediaSource ms ON ms.id = vf.sourceId
                           WHERE vf.episodeId = tvShow.id AND vf.mediaType = 'tv'
                           ORDER BY vf.relativePath COLLATE NOCASE ASC
                           LIMIT 1
                       ) AS BaseUrl,
                       (
                           SELECT vf.relativePath
                           FROM videoFile vf
                           WHERE vf.episodeId = tvShow.id AND vf.mediaType = 'tv'
                           ORDER BY vf.relativePath COLLATE NOCASE ASC
                           LIMIT 1
                       ) AS RelativePath
                FROM tvShow
                WHERE tvShow.isLocked = 0
                  {filterByTvShow}
                  AND EXISTS (SELECT 1 FROM videoFile WHERE episodeId = tvShow.id AND mediaType = 'tv')
                ORDER BY tvShow.title COLLATE NOCASE ASC
                """;
        var command = tvShowId.HasValue
            ? new CommandDefinition(sql, new { TvShowId = tvShowId.Value }, cancellationToken: cancellationToken)
            : new CommandDefinition(sql, cancellationToken: cancellationToken);
        var candidates = await connection.QueryAsync<TvShowMetadataCandidate>(command);
        return candidates.ToList();
    }

    private async Task<UpdateResult> UpdateMovieAsync(
        MovieMetadataCandidate candidate,
        TmdbMetadataMatch match,
        string? posterPath,
        TmdbSettings settings,
        CancellationToken cancellationToken)
    {
        var newTitle = settings.EnableMetadataEnrichment
            ? LibraryPreferredTitleResolver.Resolve(
                match.Title,
                candidate.Title,
                ResolveDesiredMetadataLanguage(settings.Language),
                candidate.SourceProtocolType,
                candidate.BaseUrl,
                candidate.RelativePath)
            : candidate.Title;
        var newReleaseDate = settings.EnableMetadataEnrichment ? match.ReleaseDate ?? candidate.ReleaseDate : candidate.ReleaseDate;
        var newOverview = settings.EnableMetadataEnrichment ? PreferOverview(match.Overview, candidate.Overview) : candidate.Overview;
        var newPosterPath = posterPath ?? candidate.PosterPath;
        var newVoteAverage = settings.EnableMetadataEnrichment ? match.VoteAverage ?? candidate.VoteAverage : candidate.VoteAverage;
        var newProductionCountryCodes = settings.EnableMetadataEnrichment ? PreferMetadataValue(match.ProductionCountryCodes, candidate.ProductionCountryCodes) : candidate.ProductionCountryCodes;
        var newOriginalLanguage = settings.EnableMetadataEnrichment ? PreferMetadataValue(match.OriginalLanguage, candidate.OriginalLanguage) : candidate.OriginalLanguage;
        var newMetadataLanguage = settings.EnableMetadataEnrichment ? ResolveDesiredMetadataLanguage(settings.Language) : candidate.MetadataLanguage;

        var updatedMetadata =
            !string.Equals(newTitle, candidate.Title, StringComparison.Ordinal) ||
            !string.Equals(newReleaseDate, candidate.ReleaseDate, StringComparison.Ordinal) ||
            !string.Equals(newOverview, candidate.Overview, StringComparison.Ordinal) ||
            !Nullable.Equals(newVoteAverage, candidate.VoteAverage) ||
            !string.Equals(newProductionCountryCodes, candidate.ProductionCountryCodes, StringComparison.Ordinal) ||
            !string.Equals(newOriginalLanguage, candidate.OriginalLanguage, StringComparison.Ordinal) ||
            !string.Equals(newMetadataLanguage, candidate.MetadataLanguage, StringComparison.OrdinalIgnoreCase);

        if (!updatedMetadata &&
            string.Equals(newPosterPath, candidate.PosterPath, StringComparison.Ordinal))
        {
            return new UpdateResult(false, false);
        }

        using var connection = database.OpenConnection();
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE movie
                SET title = @Title,
                    releaseDate = @ReleaseDate,
                    overview = @Overview,
                    posterPath = @PosterPath,
                    voteAverage = @VoteAverage,
                    productionCountryCodes = @ProductionCountryCodes,
                    originalLanguage = @OriginalLanguage,
                    metadataLanguage = @MetadataLanguage
                WHERE id = @Id
                """,
                new
                {
                    candidate.Id,
                    Title = newTitle,
                    ReleaseDate = newReleaseDate,
                    Overview = newOverview,
                    PosterPath = newPosterPath,
                    VoteAverage = newVoteAverage,
                    ProductionCountryCodes = newProductionCountryCodes,
                    OriginalLanguage = newOriginalLanguage,
                    MetadataLanguage = newMetadataLanguage
                },
                cancellationToken: cancellationToken));

        return new UpdateResult(
            updatedMetadata,
            !string.Equals(newPosterPath, candidate.PosterPath, StringComparison.Ordinal) && IsUsableLocalPoster(newPosterPath));
    }

    private async Task<UpdateResult> UpdateTvShowAsync(
        TvShowMetadataCandidate candidate,
        TmdbMetadataMatch match,
        string? posterPath,
        TmdbSettings settings,
        CancellationToken cancellationToken)
    {
        var newTitle = settings.EnableMetadataEnrichment
            ? LibraryPreferredTitleResolver.Resolve(
                match.Title,
                candidate.Title,
                ResolveDesiredMetadataLanguage(settings.Language),
                candidate.SourceProtocolType,
                candidate.BaseUrl,
                candidate.RelativePath)
            : candidate.Title;
        var newFirstAirDate = settings.EnableMetadataEnrichment ? match.FirstAirDate ?? candidate.FirstAirDate : candidate.FirstAirDate;
        var newOverview = settings.EnableMetadataEnrichment ? PreferOverview(match.Overview, candidate.Overview) : candidate.Overview;
        var newPosterPath = posterPath ?? candidate.PosterPath;
        var newVoteAverage = settings.EnableMetadataEnrichment ? match.VoteAverage ?? candidate.VoteAverage : candidate.VoteAverage;
        var newProductionCountryCodes = settings.EnableMetadataEnrichment ? PreferMetadataValue(match.ProductionCountryCodes, candidate.ProductionCountryCodes) : candidate.ProductionCountryCodes;
        var newOriginalLanguage = settings.EnableMetadataEnrichment ? PreferMetadataValue(match.OriginalLanguage, candidate.OriginalLanguage) : candidate.OriginalLanguage;
        var newMetadataLanguage = settings.EnableMetadataEnrichment ? ResolveDesiredMetadataLanguage(settings.Language) : candidate.MetadataLanguage;

        var updatedMetadata =
            !string.Equals(newTitle, candidate.Title, StringComparison.Ordinal) ||
            !string.Equals(newFirstAirDate, candidate.FirstAirDate, StringComparison.Ordinal) ||
            !string.Equals(newOverview, candidate.Overview, StringComparison.Ordinal) ||
            !Nullable.Equals(newVoteAverage, candidate.VoteAverage) ||
            !string.Equals(newProductionCountryCodes, candidate.ProductionCountryCodes, StringComparison.Ordinal) ||
            !string.Equals(newOriginalLanguage, candidate.OriginalLanguage, StringComparison.Ordinal) ||
            !string.Equals(newMetadataLanguage, candidate.MetadataLanguage, StringComparison.OrdinalIgnoreCase);

        if (!updatedMetadata &&
            string.Equals(newPosterPath, candidate.PosterPath, StringComparison.Ordinal))
        {
            return new UpdateResult(false, false);
        }

        using var connection = database.OpenConnection();
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE tvShow
                SET title = @Title,
                    firstAirDate = @FirstAirDate,
                    overview = @Overview,
                    posterPath = @PosterPath,
                    voteAverage = @VoteAverage,
                    productionCountryCodes = @ProductionCountryCodes,
                    originalLanguage = @OriginalLanguage,
                    metadataLanguage = @MetadataLanguage
                WHERE id = @Id
                """,
                new
                {
                    candidate.Id,
                    Title = newTitle,
                    FirstAirDate = newFirstAirDate,
                    Overview = newOverview,
                    PosterPath = newPosterPath,
                    VoteAverage = newVoteAverage,
                    ProductionCountryCodes = newProductionCountryCodes,
                    OriginalLanguage = newOriginalLanguage,
                    MetadataLanguage = newMetadataLanguage
                },
                cancellationToken: cancellationToken));

        return new UpdateResult(
            updatedMetadata,
            !string.Equals(newPosterPath, candidate.PosterPath, StringComparison.Ordinal) && IsUsableLocalPoster(newPosterPath));
    }

    private static bool NeedsMovieRefresh(MovieMetadataCandidate candidate, TmdbSettings settings)
    {
        var needsPoster = settings.EnablePosterDownloads && !IsUsableLocalPoster(candidate.PosterPath);
        var needsMetadata = settings.EnableMetadataEnrichment &&
                            (NeedsLanguageRefresh(candidate.MetadataLanguage, settings)
                             || string.IsNullOrWhiteSpace(candidate.ReleaseDate)
                             || string.IsNullOrWhiteSpace(candidate.Overview)
                             || string.IsNullOrWhiteSpace(candidate.ProductionCountryCodes)
                             || candidate.VoteAverage is null);
        return needsPoster || needsMetadata;
    }

    private static bool NeedsTvShowRefresh(TvShowMetadataCandidate candidate, TmdbSettings settings)
    {
        var needsPoster = settings.EnablePosterDownloads && !IsUsableLocalPoster(candidate.PosterPath);
        var needsMetadata = settings.EnableMetadataEnrichment &&
                            (NeedsLanguageRefresh(candidate.MetadataLanguage, settings)
                             || string.IsNullOrWhiteSpace(candidate.FirstAirDate)
                             || string.IsNullOrWhiteSpace(candidate.Overview)
                             || string.IsNullOrWhiteSpace(candidate.ProductionCountryCodes)
                             || candidate.VoteAverage is null);
        return needsPoster || needsMetadata;
    }

    private static bool IsUsableLocalPoster(string? posterPath)
    {
        return !string.IsNullOrWhiteSpace(posterPath)
               && Path.IsPathRooted(posterPath)
               && File.Exists(posterPath);
    }

    private static int? ResolvePreferredSeason(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        return MediaNameParser.ResolvePreferredSeason(
            relativePath,
            Path.GetFileName(relativePath) ?? relativePath);
    }

    private static string? ResolveMovieSearchYear(MovieMetadataCandidate candidate)
    {
        var currentYear = NormalizeYear(candidate.ReleaseDate);
        if (!string.IsNullOrWhiteSpace(currentYear))
        {
            return currentYear;
        }

        if (string.IsNullOrWhiteSpace(candidate.BaseUrl) ||
            string.IsNullOrWhiteSpace(candidate.RelativePath))
        {
            return null;
        }

        var metadataPath = MediaSourcePathResolver.ResolveMetadataPath(
            candidate.SourceProtocolType,
            candidate.BaseUrl,
            candidate.RelativePath);
        return NormalizeYear(MediaNameParser.ExtractSearchMetadata(metadataPath).Year);
    }

    private static string? ResolveTvShowSearchYear(TvShowMetadataCandidate candidate)
    {
        var currentYear = NormalizeYear(candidate.FirstAirDate);
        if (!string.IsNullOrWhiteSpace(currentYear))
        {
            return currentYear;
        }

        if (string.IsNullOrWhiteSpace(candidate.BaseUrl) ||
            string.IsNullOrWhiteSpace(candidate.RelativePath))
        {
            return null;
        }

        var metadataPath = MediaSourcePathResolver.ResolveMetadataPath(
            candidate.SourceProtocolType,
            candidate.BaseUrl,
            candidate.RelativePath);
        return NormalizeYear(MediaNameParser.ExtractSearchMetadata(metadataPath).Year);
    }

    private static bool NeedsLanguageRefresh(string? metadataLanguage, TmdbSettings settings)
    {
        var desiredLanguage = ResolveDesiredMetadataLanguage(settings.Language);
        return !string.IsNullOrWhiteSpace(desiredLanguage) &&
               !string.Equals(NormalizeMetadataLanguage(metadataLanguage), desiredLanguage, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDesiredMetadataLanguage(string? language)
    {
        var trimmed = language?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? TmdbSettings.DefaultLanguage : trimmed;
    }

    private static string? NormalizeMetadataLanguage(string? language)
    {
        var trimmed = language?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
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

    private static string? PreferOverview(string? matchedOverview, string? currentOverview)
    {
        return string.IsNullOrWhiteSpace(matchedOverview)
            ? currentOverview
            : matchedOverview;
    }

    private static string? PreferMetadataValue(string? matchedValue, string? currentValue)
    {
        return string.IsNullOrWhiteSpace(matchedValue)
            ? currentValue
            : matchedValue.Trim();
    }

    private static bool ShouldStopOnExternalFailure(Exception exception)
    {
        return exception is HttpRequestException or TaskCanceledException;
    }

    private sealed record MovieMetadataCandidate(
        long Id,
        string Title,
        string? ReleaseDate,
        string? Overview,
        string? PosterPath,
        double? VoteAverage,
        string? ProductionCountryCodes,
        string? OriginalLanguage,
        string? MetadataLanguage,
        string? SourceProtocolType,
        string? BaseUrl,
        string? RelativePath);

    private sealed record TvShowMetadataCandidate(
        long Id,
        string Title,
        string? FirstAirDate,
        string? Overview,
        string? PosterPath,
        double? VoteAverage,
        string? ProductionCountryCodes,
        string? OriginalLanguage,
        string? MetadataLanguage,
        string? SourceProtocolType,
        string? BaseUrl,
        string? RelativePath);

    private sealed record UpdateResult(bool UpdatedMetadata, bool DownloadedPoster);

    private sealed class EnrichmentState
    {
        public int UpdatedMovies { get; set; }

        public int UpdatedTvShows { get; set; }

        public int DownloadedPosters { get; set; }

        public bool EncounteredNetworkError { get; private set; }

        public string? ErrorMessage { get; private set; }

        public void MarkNetworkError(string? message)
        {
            EncounteredNetworkError = true;
            ErrorMessage ??= message;
        }

        public LibraryMetadataEnrichmentSummary ToSummary()
        {
            return new LibraryMetadataEnrichmentSummary(
                UpdatedMovies,
                UpdatedTvShows,
                DownloadedPosters,
                EncounteredNetworkError,
                ErrorMessage);
        }
    }
}
