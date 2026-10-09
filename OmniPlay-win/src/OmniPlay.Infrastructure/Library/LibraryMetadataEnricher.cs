using Dapper;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Core.Models.Library;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Core.Settings;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.Tmdb;

namespace OmniPlay.Infrastructure.Library;

public sealed class LibraryMetadataEnricher : ILibraryMetadataEnricher
{
    private readonly SqliteDatabase database;
    private readonly ITmdbMetadataClient tmdbMetadataClient;
    private readonly ISettingsService? settingsService;
    private readonly ILocalMetadataSidecarService? localMetadataSidecarService;
    private readonly ICategoryFolderStore? categoryFolderStore;

    public LibraryMetadataEnricher(
        SqliteDatabase database,
        ITmdbMetadataClient tmdbMetadataClient,
        ISettingsService? settingsService = null,
        ILocalMetadataSidecarService? localMetadataSidecarService = null,
        ICategoryFolderStore? categoryFolderStore = null)
    {
        this.database = database;
        this.tmdbMetadataClient = tmdbMetadataClient;
        this.settingsService = settingsService;
        this.localMetadataSidecarService = localMetadataSidecarService;
        this.categoryFolderStore = categoryFolderStore;
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
        var localMetadataSettings = await LoadLocalMetadataSettingsAsync(cancellationToken);
        var movies = await LoadMovieCandidatesAsync(movieId: null, cancellationToken);
        await EnrichMoviesAsync(movies, state, settings, localMetadataSettings, cancellationToken);

        if (!state.EncounteredNetworkError)
        {
            var tvShows = await LoadTvShowCandidatesAsync(tvShowId: null, cancellationToken);
            await EnrichTvShowsAsync(tvShows, state, settings, localMetadataSettings, cancellationToken);
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
        var localMetadataSettings = await LoadLocalMetadataSettingsAsync(cancellationToken);
        var movies = await LoadMovieCandidatesAsync(movieId, cancellationToken);
        await EnrichMoviesAsync(movies, state, settings, localMetadataSettings, cancellationToken);
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
        var localMetadataSettings = await LoadLocalMetadataSettingsAsync(cancellationToken);
        var tvShows = await LoadTvShowCandidatesAsync(tvShowId, cancellationToken);
        await EnrichTvShowsAsync(tvShows, state, settings, localMetadataSettings, cancellationToken);
        return state.ToSummary();
    }

    private async Task EnrichMoviesAsync(
        IReadOnlyList<MovieMetadataCandidate> candidates,
        EnrichmentState state,
        TmdbSettings settings,
        LocalMetadataSettings localMetadataSettings,
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
                    candidate.RelativePath,
                    fileName: candidate.FileName);
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

                if (localMetadataSettings.EnableLocalMetadataExport && result.Metadata is not null)
                {
                    await TryExportMovieSidecarAsync(candidate, result.Metadata, cancellationToken);
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
        LocalMetadataSettings localMetadataSettings,
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
                    candidate.RelativePath,
                    fileName: candidate.FileName);
                var searchYear = ResolveTvShowSearchYear(candidate);
                var preferredSeason = ResolvePreferredSeason(candidate.RelativePath, candidate.FileName);
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

                if (localMetadataSettings.EnableLocalMetadataExport && result.Metadata is not null)
                {
                    await TryExportTvShowSidecarAsync(candidate, result.Metadata, cancellationToken);
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

    private async Task<LocalMetadataSettings> LoadLocalMetadataSettingsAsync(CancellationToken cancellationToken)
    {
        if (settingsService is null)
        {
            return new LocalMetadataSettings();
        }

        var settings = await settingsService.LoadAsync(cancellationToken);
        return settings.LocalMetadata;
    }

    private async Task TryExportMovieSidecarAsync(
        MovieMetadataCandidate candidate,
        LocalSidecarMetadata metadata,
        CancellationToken cancellationToken)
    {
        if (localMetadataSidecarService is null)
        {
            return;
        }

        try
        {
            await localMetadataSidecarService.ExportMovieAsync(
                candidate.SourceProtocolType,
                candidate.BaseUrl,
                candidate.RelativePath,
                metadata,
                cancellationToken);
        }
        catch
        {
        }
    }

    private async Task TryExportTvShowSidecarAsync(
        TvShowMetadataCandidate candidate,
        LocalSidecarMetadata metadata,
        CancellationToken cancellationToken)
    {
        if (localMetadataSidecarService is null)
        {
            return;
        }

        try
        {
            await localMetadataSidecarService.ExportTvShowAsync(
                candidate.SourceProtocolType,
                candidate.BaseUrl,
                candidate.RelativePath,
                metadata,
                cancellationToken);
        }
        catch
        {
        }
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
                       ) AS RelativePath,
                       (
                           SELECT vf.fileName
                           FROM videoFile vf
                           WHERE vf.movieId = movie.id AND vf.mediaType = 'movie'
                           ORDER BY vf.relativePath COLLATE NOCASE ASC
                           LIMIT 1
                       ) AS FileName,
                       movie.director AS Director,
                       movie.mainCast AS MainCast,
                       movie.tmdbId AS TmdbId
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
                       ) AS RelativePath,
                       (
                           SELECT vf.fileName
                           FROM videoFile vf
                           WHERE vf.episodeId = tvShow.id AND vf.mediaType = 'tv'
                           ORDER BY vf.relativePath COLLATE NOCASE ASC
                           LIMIT 1
                       ) AS FileName,
                       tvShow.director AS Director,
                       tvShow.mainCast AS MainCast
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
                candidate.RelativePath,
                candidate.FileName)
            : candidate.Title;
        var newReleaseDate = settings.EnableMetadataEnrichment ? match.ReleaseDate ?? candidate.ReleaseDate : candidate.ReleaseDate;
        var newOverview = settings.EnableMetadataEnrichment ? PreferOverview(match.Overview, candidate.Overview) : candidate.Overview;
        var newPosterPath = posterPath ?? candidate.PosterPath;
        var newVoteAverage = settings.EnableMetadataEnrichment ? match.VoteAverage ?? candidate.VoteAverage : candidate.VoteAverage;
        var newProductionCountryCodes = settings.EnableMetadataEnrichment ? PreferMetadataValue(match.ProductionCountryCodes, candidate.ProductionCountryCodes) : candidate.ProductionCountryCodes;
        var newOriginalLanguage = settings.EnableMetadataEnrichment ? PreferMetadataValue(match.OriginalLanguage, candidate.OriginalLanguage) : candidate.OriginalLanguage;
        var newMetadataLanguage = settings.EnableMetadataEnrichment ? ResolveDesiredMetadataLanguage(settings.Language) : candidate.MetadataLanguage;
        var credits = settings.EnableMetadataEnrichment ? await TryFetchCreditsAsync(match, cancellationToken) : null;
        credits = await RefreshPeopleAsync(credits, cancellationToken);
        var people = PeopleColumns.FromCredits(credits, candidate.Director, candidate.MainCast);
        var newDirector = people.DirectorText;
        var newMainCast = people.MainCastText;

        var updatedMetadata =
            !string.Equals(newTitle, candidate.Title, StringComparison.Ordinal) ||
            !string.Equals(newReleaseDate, candidate.ReleaseDate, StringComparison.Ordinal) ||
            !string.Equals(newOverview, candidate.Overview, StringComparison.Ordinal) ||
            !Nullable.Equals(newVoteAverage, candidate.VoteAverage) ||
            !string.Equals(newProductionCountryCodes, candidate.ProductionCountryCodes, StringComparison.Ordinal) ||
            !string.Equals(newOriginalLanguage, candidate.OriginalLanguage, StringComparison.Ordinal) ||
            !string.Equals(newMetadataLanguage, candidate.MetadataLanguage, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(newDirector, candidate.Director, StringComparison.Ordinal) ||
            !string.Equals(newMainCast, candidate.MainCast, StringComparison.Ordinal);

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var affectedRows = await connection.ExecuteAsync(
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
                    metadataLanguage = @MetadataLanguage,
                    director = @Director,
                    mainCast = @MainCast,
                    directors = @Directors,
                    casts = @Casts,
                    tmdbId = @TmdbId
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
                    MetadataLanguage = newMetadataLanguage,
                    Director = newDirector,
                    MainCast = newMainCast,
                    Directors = people.Directors,
                    Casts = people.Casts,
                    TmdbId = match.Id
                },
                transaction,
                cancellationToken: cancellationToken));
        if (affectedRows == 0)
        {
            transaction.Rollback();
            return new UpdateResult(false, false);
        }

        var mergedDuplicate = await MergeDuplicateMoviesByTmdbIdAsync(
            connection,
            transaction,
            candidate.Id,
            match.Id,
            cancellationToken);
        transaction.Commit();

        return new UpdateResult(
            updatedMetadata || mergedDuplicate,
            !string.Equals(newPosterPath, candidate.PosterPath, StringComparison.Ordinal) && IsUsableLocalPoster(newPosterPath),
            new LocalSidecarMetadata
            {
                Title = newTitle,
                Date = newReleaseDate,
                Overview = newOverview,
                PosterPath = newPosterPath,
                VoteAverage = newVoteAverage,
                TmdbId = match.Id,
                Directors = PersonNames.Decode(people.Directors),
                Casts = PersonNames.Decode(people.Casts)
            });
    }

    private static async Task<bool> MergeDuplicateMoviesByTmdbIdAsync(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        long canonicalMovieId,
        int tmdbId,
        CancellationToken cancellationToken)
    {
        if (tmdbId <= 0)
        {
            return false;
        }

        var duplicateIds = (await connection.QueryAsync<long>(
            new CommandDefinition(
                """
                SELECT id
                FROM movie
                WHERE tmdbId = @TmdbId
                  AND id <> @CanonicalMovieId
                  AND EXISTS (
                      SELECT 1
                      FROM videoFile
                      WHERE videoFile.movieId = movie.id
                        AND videoFile.mediaType = 'movie'
                  )
                """,
                new { TmdbId = tmdbId, CanonicalMovieId = canonicalMovieId },
                transaction,
                cancellationToken: cancellationToken))).ToArray();
        if (duplicateIds.Length == 0)
        {
            return false;
        }

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE videoFile
                SET mediaType = 'movie',
                    movieId = @CanonicalMovieId,
                    episodeId = NULL
                WHERE movieId IN @DuplicateIds
                  AND mediaType = 'movie'
                """,
                new { CanonicalMovieId = canonicalMovieId, DuplicateIds = duplicateIds },
                transaction,
                cancellationToken: cancellationToken));
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                DELETE FROM movie
                WHERE id IN @DuplicateIds
                  AND NOT EXISTS (
                      SELECT 1
                      FROM videoFile
                      WHERE videoFile.movieId = movie.id
                        AND videoFile.mediaType = 'movie'
                  )
                """,
                new { DuplicateIds = duplicateIds },
                transaction,
                cancellationToken: cancellationToken));
        return true;
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
                candidate.RelativePath,
                candidate.FileName)
            : candidate.Title;
        var newFirstAirDate = settings.EnableMetadataEnrichment ? match.FirstAirDate ?? candidate.FirstAirDate : candidate.FirstAirDate;
        var newOverview = settings.EnableMetadataEnrichment ? PreferOverview(match.Overview, candidate.Overview) : candidate.Overview;
        var newPosterPath = posterPath ?? candidate.PosterPath;
        var newVoteAverage = settings.EnableMetadataEnrichment ? match.VoteAverage ?? candidate.VoteAverage : candidate.VoteAverage;
        var newProductionCountryCodes = settings.EnableMetadataEnrichment ? PreferMetadataValue(match.ProductionCountryCodes, candidate.ProductionCountryCodes) : candidate.ProductionCountryCodes;
        var newOriginalLanguage = settings.EnableMetadataEnrichment ? PreferMetadataValue(match.OriginalLanguage, candidate.OriginalLanguage) : candidate.OriginalLanguage;
        var newMetadataLanguage = settings.EnableMetadataEnrichment ? ResolveDesiredMetadataLanguage(settings.Language) : candidate.MetadataLanguage;
        var credits = settings.EnableMetadataEnrichment ? await TryFetchCreditsAsync(match, cancellationToken) : null;
        credits = await RefreshPeopleAsync(credits, cancellationToken);
        var people = PeopleColumns.FromCredits(credits, candidate.Director, candidate.MainCast);
        var newDirector = people.DirectorText;
        var newMainCast = people.MainCastText;

        var updatedMetadata =
            !string.Equals(newTitle, candidate.Title, StringComparison.Ordinal) ||
            !string.Equals(newFirstAirDate, candidate.FirstAirDate, StringComparison.Ordinal) ||
            !string.Equals(newOverview, candidate.Overview, StringComparison.Ordinal) ||
            !Nullable.Equals(newVoteAverage, candidate.VoteAverage) ||
            !string.Equals(newProductionCountryCodes, candidate.ProductionCountryCodes, StringComparison.Ordinal) ||
            !string.Equals(newOriginalLanguage, candidate.OriginalLanguage, StringComparison.Ordinal) ||
            !string.Equals(newMetadataLanguage, candidate.MetadataLanguage, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(newDirector, candidate.Director, StringComparison.Ordinal) ||
            !string.Equals(newMainCast, candidate.MainCast, StringComparison.Ordinal);

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
                    metadataLanguage = @MetadataLanguage,
                    director = @Director,
                    mainCast = @MainCast,
                    directors = @Directors,
                    casts = @Casts
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
                    MetadataLanguage = newMetadataLanguage,
                    Director = newDirector,
                    MainCast = newMainCast,
                    Directors = people.Directors,
                    Casts = people.Casts
                },
                cancellationToken: cancellationToken));

        return new UpdateResult(
            updatedMetadata,
            !string.Equals(newPosterPath, candidate.PosterPath, StringComparison.Ordinal) && IsUsableLocalPoster(newPosterPath),
            new LocalSidecarMetadata
            {
                Title = newTitle,
                Date = newFirstAirDate,
                Overview = newOverview,
                PosterPath = newPosterPath,
                VoteAverage = newVoteAverage,
                TmdbId = match.Id,
                Directors = PersonNames.Decode(people.Directors),
                Casts = PersonNames.Decode(people.Casts)
            });
    }

    /// <summary>用中文译名覆盖刮削到的外文人名，并把本片人物登记进导演/主演档案。</summary>
    private Task<TmdbCredits?> RefreshPeopleAsync(TmdbCredits? credits, CancellationToken cancellationToken) =>
        PeopleColumnFactory.RefreshPeopleAsync(categoryFolderStore, credits, cancellationToken);

    private async Task<TmdbCredits?> TryFetchCreditsAsync(TmdbMetadataMatch match, CancellationToken cancellationToken)
    {
        try
        {
            return await tmdbMetadataClient.FetchCreditsAsync(match.Id, match.MediaType, cancellationToken);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool NeedsMovieRefresh(MovieMetadataCandidate candidate, TmdbSettings settings)
    {
        var needsPoster = settings.EnablePosterDownloads && !IsUsableLocalPoster(candidate.PosterPath);
        var needsMetadata = settings.EnableMetadataEnrichment &&
                            (NeedsLanguageRefresh(candidate.MetadataLanguage, settings)
                             || string.IsNullOrWhiteSpace(candidate.ReleaseDate)
                             || string.IsNullOrWhiteSpace(candidate.Overview)
                             || string.IsNullOrWhiteSpace(candidate.ProductionCountryCodes)
                             || candidate.VoteAverage is null
                             || (candidate.TmdbId.HasValue
                                 && (string.IsNullOrWhiteSpace(candidate.Director)
                                     || string.IsNullOrWhiteSpace(candidate.MainCast))));
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
                             || candidate.VoteAverage is null
                             || (!string.IsNullOrWhiteSpace(candidate.MetadataLanguage)
                                 && (string.IsNullOrWhiteSpace(candidate.Director)
                                     || string.IsNullOrWhiteSpace(candidate.MainCast))));
        return needsPoster || needsMetadata;
    }

    private static bool IsUsableLocalPoster(string? posterPath)
    {
        return !string.IsNullOrWhiteSpace(posterPath)
               && Path.IsPathRooted(posterPath)
               && File.Exists(posterPath);
    }

    private static int? ResolvePreferredSeason(string? relativePath, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(relativePath) && string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        return MediaNameParser.ResolvePreferredSeason(
            relativePath ?? string.Empty,
            string.IsNullOrWhiteSpace(fileName)
                ? Path.GetFileName(relativePath) ?? relativePath ?? string.Empty
                : fileName);
    }

    private static string? ResolveMovieSearchYear(MovieMetadataCandidate candidate)
    {
        var currentYear = NormalizeYear(candidate.ReleaseDate);
        if (!string.IsNullOrWhiteSpace(currentYear))
        {
            return currentYear;
        }

        var combinedYear = NormalizeYear(MediaNameParser.CombinedSearchMetadata(
            candidate.RelativePath ?? string.Empty,
            candidate.FileName ?? string.Empty).Year);
        if (!string.IsNullOrWhiteSpace(combinedYear))
        {
            return combinedYear;
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

        var combinedYear = NormalizeYear(MediaNameParser.CombinedSearchMetadata(
            candidate.RelativePath ?? string.Empty,
            candidate.FileName ?? string.Empty).Year);
        if (!string.IsNullOrWhiteSpace(combinedYear))
        {
            return combinedYear;
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
        string? RelativePath,
        string? FileName,
        string? Director,
        string? MainCast,
        long? TmdbId);

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
        string? RelativePath,
        string? FileName,
        string? Director,
        string? MainCast);

    private sealed record UpdateResult(
        bool UpdatedMetadata,
        bool DownloadedPoster,
        LocalSidecarMetadata? Metadata = null);

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
