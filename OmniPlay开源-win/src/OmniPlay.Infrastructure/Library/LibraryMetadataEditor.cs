using Dapper;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Core.Settings;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.Tmdb;

namespace OmniPlay.Infrastructure.Library;

public sealed class LibraryMetadataEditor : ILibraryMetadataEditor
{
    private readonly SqliteDatabase database;
    private readonly ITmdbMetadataClient tmdbMetadataClient;
    private readonly ISettingsService? settingsService;

    public LibraryMetadataEditor(SqliteDatabase database, ITmdbMetadataClient tmdbMetadataClient)
        : this(database, tmdbMetadataClient, null)
    {
    }

    public LibraryMetadataEditor(
        SqliteDatabase database,
        ITmdbMetadataClient tmdbMetadataClient,
        ISettingsService? settingsService)
    {
        this.database = database;
        this.tmdbMetadataClient = tmdbMetadataClient;
        this.settingsService = settingsService;
    }

    public async Task<IReadOnlyList<LibraryMetadataSearchCandidate>> SearchMovieMatchesAsync(
        long movieId,
        string? manualQuery = null,
        string? manualYear = null,
        CancellationToken cancellationToken = default)
    {
        var candidate = await LoadMovieCandidateAsync(movieId, cancellationToken);
        if (candidate is null)
        {
            return [];
        }

        var result = await SearchMovieCandidatesWithFallbackAsync(candidate, manualQuery, manualYear, cancellationToken);
        return result.Candidates;
    }

    public async Task<IReadOnlyList<LibraryMetadataSearchCandidate>> SearchTvShowMatchesAsync(
        long tvShowId,
        string? manualQuery = null,
        string? manualYear = null,
        CancellationToken cancellationToken = default)
    {
        var candidate = await LoadTvShowCandidateAsync(tvShowId, cancellationToken);
        if (candidate is null)
        {
            return [];
        }

        var result = await SearchTvShowCandidatesWithFallbackAsync(candidate, manualQuery, manualYear, cancellationToken);
        return result.Candidates;
    }

    public async Task<LibraryMetadataRefreshResult> RefreshMovieAsync(
        long movieId,
        string? manualQuery = null,
        string? manualYear = null,
        CancellationToken cancellationToken = default)
    {
        var candidate = await LoadMovieCandidateAsync(movieId, cancellationToken);
        if (candidate is null)
        {
            return new LibraryMetadataRefreshResult(Message: "\u5F53\u524D\u7535\u5F71\u5DF2\u4E0D\u5B58\u5728\uFF0C\u65E0\u6CD5\u91CD\u65B0\u522E\u524A\u3002");
        }

        try
        {
            var searchResult = await SearchMovieBestMatchWithFallbackAsync(candidate, manualQuery, manualYear, cancellationToken);
            if (searchResult is null)
            {
                return new LibraryMetadataRefreshResult(
                    Message: BuildNoMatchMessage(
                        manualQuery,
                        candidate.Title,
                        "\u7535\u5F71"));
            }

            return await ApplyMovieMatchCoreAsync(
                candidate,
                searchResult.Match,
                $"\u5DF2\u6309\u300C{searchResult.Attempt.Query}\u300D\u91CD\u65B0\u522E\u524A\u7535\u5F71\uFF1A{{0}}",
                "\u5DF2\u627E\u5230\u7535\u5F71\u300A{0}\u300B\uFF0C\u5F53\u524D\u5143\u6570\u636E\u65E0\u9700\u66F4\u65B0\u3002",
                "\u91CD\u65B0\u522E\u524A\u7535\u5F71\u5931\u8D25\uFF1A{0}",
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new LibraryMetadataRefreshResult(
                EncounteredNetworkError: true,
                Message: $"\u91CD\u65B0\u522E\u524A\u7535\u5F71\u5931\u8D25\uFF1A{ex.Message}");
        }
    }

    public async Task<LibraryMetadataRefreshResult> RefreshTvShowAsync(
        long tvShowId,
        string? manualQuery = null,
        string? manualYear = null,
        CancellationToken cancellationToken = default)
    {
        var candidate = await LoadTvShowCandidateAsync(tvShowId, cancellationToken);
        if (candidate is null)
        {
            return new LibraryMetadataRefreshResult(Message: "\u5F53\u524D\u5267\u96C6\u5DF2\u4E0D\u5B58\u5728\uFF0C\u65E0\u6CD5\u91CD\u65B0\u522E\u524A\u3002");
        }

        try
        {
            var searchResult = await SearchTvShowBestMatchWithFallbackAsync(candidate, manualQuery, manualYear, cancellationToken);
            if (searchResult is null)
            {
                return new LibraryMetadataRefreshResult(
                    Message: BuildNoMatchMessage(
                        manualQuery,
                        candidate.Title,
                        "\u5267\u96C6"));
            }

            return await ApplyTvShowMatchCoreAsync(
                candidate,
                searchResult.Match,
                $"\u5DF2\u6309\u300C{searchResult.Attempt.Query}\u300D\u91CD\u65B0\u522E\u524A\u5267\u96C6\uFF1A{{0}}",
                "\u5DF2\u627E\u5230\u5267\u96C6\u300A{0}\u300B\uFF0C\u5F53\u524D\u5143\u6570\u636E\u65E0\u9700\u66F4\u65B0\u3002",
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new LibraryMetadataRefreshResult(
                EncounteredNetworkError: true,
                Message: $"\u91CD\u65B0\u522E\u524A\u5267\u96C6\u5931\u8D25\uFF1A{ex.Message}");
        }
    }

    public async Task<LibraryMetadataRefreshResult> ApplyMovieMatchAsync(
        long movieId,
        LibraryMetadataSearchCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var movie = await LoadMovieCandidateAsync(movieId, cancellationToken);
        if (movie is null)
        {
            return new LibraryMetadataRefreshResult(Message: "\u5F53\u524D\u7535\u5F71\u5DF2\u4E0D\u5B58\u5728\uFF0C\u65E0\u6CD5\u5E94\u7528 TMDB \u5339\u914D\u3002");
        }

        try
        {
            return await ApplyMovieMatchCoreAsync(
                movie,
                ToTmdbMatch(candidate),
                "\u5DF2\u5E94\u7528\u6240\u9009 TMDB \u7535\u5F71\uFF1A{0}",
                "\u6240\u9009 TMDB \u7535\u5F71\u5DF2\u4E0E\u5F53\u524D\u5143\u6570\u636E\u4E00\u81F4\u3002",
                "\u5E94\u7528 TMDB \u7535\u5F71\u5339\u914D\u5931\u8D25\uFF1A{0}",
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new LibraryMetadataRefreshResult(
                EncounteredNetworkError: true,
                Message: $"\u5E94\u7528 TMDB \u7535\u5F71\u5339\u914D\u5931\u8D25\uFF1A{ex.Message}");
        }
    }

    public async Task<LibraryMetadataRefreshResult> ApplyTvShowMatchAsync(
        long tvShowId,
        LibraryMetadataSearchCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var show = await LoadTvShowCandidateAsync(tvShowId, cancellationToken);
        if (show is null)
        {
            return new LibraryMetadataRefreshResult(Message: "\u5F53\u524D\u5267\u96C6\u5DF2\u4E0D\u5B58\u5728\uFF0C\u65E0\u6CD5\u5E94\u7528 TMDB \u5339\u914D\u3002");
        }

        try
        {
            return await ApplyTvShowMatchCoreAsync(
                show,
                ToTmdbMatch(candidate),
                "\u5DF2\u5E94\u7528\u6240\u9009 TMDB \u5267\u96C6\uFF1A{0}",
                "\u6240\u9009 TMDB \u5267\u96C6\u5DF2\u4E0E\u5F53\u524D\u5143\u6570\u636E\u4E00\u81F4\u3002",
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new LibraryMetadataRefreshResult(
                EncounteredNetworkError: true,
                Message: $"\u5E94\u7528 TMDB \u5267\u96C6\u5339\u914D\u5931\u8D25\uFF1A{ex.Message}");
        }
    }

    public async Task<LibraryMetadataRefreshResult> UpdateMovieMetadataAsync(
        long movieId,
        LibraryMetadataEditRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var title = NormalizeRequiredTitle(request.Title);
        if (string.IsNullOrWhiteSpace(title))
        {
            return new LibraryMetadataRefreshResult(Message: "\u7535\u5F71\u540D\u79F0\u4E0D\u80FD\u4E3A\u7A7A\u3002");
        }

        using var connection = database.OpenConnection();
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE movie
                SET title = @Title,
                    releaseDate = @ReleaseDate,
                    overview = @Overview,
                    posterPath = @PosterPath,
                    voteAverage = @VoteAverage,
                    isLocked = @IsLocked
                WHERE id = @Id
                """,
                new
                {
                    Id = movieId,
                    Title = title,
                    ReleaseDate = NormalizeOptionalText(request.Date),
                    Overview = NormalizeOptionalText(request.Overview),
                    PosterPath = NormalizeOptionalText(request.PosterPath),
                    VoteAverage = NormalizeVoteAverage(request.VoteAverage),
                    IsLocked = request.IsLocked
                },
                cancellationToken: cancellationToken));

        return affected > 0
            ? new LibraryMetadataRefreshResult(Updated: true, Message: $"\u5DF2\u4FDD\u5B58\u7535\u5F71\u300A{title}\u300B\u7684\u624B\u52A8\u8D44\u6599\u3002")
            : new LibraryMetadataRefreshResult(Message: "\u5F53\u524D\u7535\u5F71\u5DF2\u4E0D\u5B58\u5728\uFF0C\u65E0\u6CD5\u4FDD\u5B58\u624B\u52A8\u8D44\u6599\u3002");
    }

    public async Task<LibraryMetadataRefreshResult> UpdateTvShowMetadataAsync(
        long tvShowId,
        LibraryMetadataEditRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var title = NormalizeRequiredTitle(request.Title);
        if (string.IsNullOrWhiteSpace(title))
        {
            return new LibraryMetadataRefreshResult(Message: "\u5267\u96C6\u540D\u79F0\u4E0D\u80FD\u4E3A\u7A7A\u3002");
        }

        using var connection = database.OpenConnection();
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE tvShow
                SET title = @Title,
                    firstAirDate = @FirstAirDate,
                    overview = @Overview,
                    posterPath = @PosterPath,
                    voteAverage = @VoteAverage,
                    isLocked = @IsLocked
                WHERE id = @Id
                """,
                new
                {
                    Id = tvShowId,
                    Title = title,
                    FirstAirDate = NormalizeOptionalText(request.Date),
                    Overview = NormalizeOptionalText(request.Overview),
                    PosterPath = NormalizeOptionalText(request.PosterPath),
                    VoteAverage = NormalizeVoteAverage(request.VoteAverage),
                    IsLocked = request.IsLocked
                },
                cancellationToken: cancellationToken));

        return affected > 0
            ? new LibraryMetadataRefreshResult(Updated: true, Message: $"\u5DF2\u4FDD\u5B58\u5267\u96C6\u300A{title}\u300B\u7684\u624B\u52A8\u8D44\u6599\u3002")
            : new LibraryMetadataRefreshResult(Message: "\u5F53\u524D\u5267\u96C6\u5DF2\u4E0D\u5B58\u5728\uFF0C\u65E0\u6CD5\u4FDD\u5B58\u624B\u52A8\u8D44\u6599\u3002");
    }

    public async Task SetMovieLockedAsync(long movieId, bool isLocked, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE movie SET isLocked = @IsLocked WHERE id = @Id",
                new
                {
                    Id = movieId,
                    IsLocked = isLocked
                },
                cancellationToken: cancellationToken));
    }

    public async Task SetTvShowLockedAsync(long tvShowId, bool isLocked, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE tvShow SET isLocked = @IsLocked WHERE id = @Id",
                new
                {
                    Id = tvShowId,
                    IsLocked = isLocked
                },
                cancellationToken: cancellationToken));
    }

    private static string NormalizeRequiredTitle(string value)
    {
        return value.Trim();
    }

    private static string? NormalizeOptionalText(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static double? NormalizeVoteAverage(double? value)
    {
        return value.HasValue
            ? Math.Clamp(value.Value, 0, 10)
            : null;
    }

    private async Task<MovieCandidate?> LoadMovieCandidateAsync(long movieId, CancellationToken cancellationToken)
    {
        using var connection = database.OpenConnection();
        return await connection.QuerySingleOrDefaultAsync<MovieCandidate>(
            new CommandDefinition(
                """
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
                WHERE movie.id = @MovieId
                LIMIT 1
                """,
                new { MovieId = movieId },
                cancellationToken: cancellationToken));
    }

    private async Task<TvShowCandidate?> LoadTvShowCandidateAsync(long tvShowId, CancellationToken cancellationToken)
    {
        using var connection = database.OpenConnection();
        return await connection.QuerySingleOrDefaultAsync<TvShowCandidate>(
            new CommandDefinition(
                """
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
                WHERE tvShow.id = @TvShowId
                LIMIT 1
                """,
                new { TvShowId = tvShowId },
                cancellationToken: cancellationToken));
    }

    private async Task<LibraryMetadataRefreshResult> ApplyMovieMatchCoreAsync(
        MovieCandidate candidate,
        TmdbMetadataMatch match,
        string updatedMessageFormat,
        string unchangedMessageFormat,
        string networkFailureMessageFormat,
        CancellationToken cancellationToken)
    {
        try
        {
            var preferredLanguage = await ResolvePreferredLanguageAsync(cancellationToken);
            var posterPath = await ResolvePosterPathAsync(candidate.PosterPath, match, cancellationToken);
            var result = await UpdateMovieAsync(candidate, match, posterPath, preferredLanguage, cancellationToken);
            return result.Updated
                ? new LibraryMetadataRefreshResult(
                    FoundMatch: true,
                    Updated: true,
                    DownloadedPoster: result.DownloadedPoster,
                    MatchedTitle: match.Title,
                    Message: string.Format(updatedMessageFormat, match.Title))
                : new LibraryMetadataRefreshResult(
                    FoundMatch: true,
                    Updated: false,
                    DownloadedPoster: result.DownloadedPoster,
                    MatchedTitle: match.Title,
                    Message: string.Format(unchangedMessageFormat, match.Title));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new LibraryMetadataRefreshResult(
                EncounteredNetworkError: true,
                Message: string.Format(networkFailureMessageFormat, ex.Message));
        }
    }

    private async Task<LibraryMetadataRefreshResult> ApplyTvShowMatchCoreAsync(
        TvShowCandidate candidate,
        TmdbMetadataMatch match,
        string updatedMessageFormat,
        string unchangedMessageFormat,
        CancellationToken cancellationToken)
    {
        try
        {
            var preferredLanguage = await ResolvePreferredLanguageAsync(cancellationToken);
            var posterPath = await ResolvePosterPathAsync(candidate.PosterPath, match, cancellationToken);
            var result = await UpdateTvShowAsync(candidate, match, posterPath, preferredLanguage, cancellationToken);
            return result.Updated
                ? new LibraryMetadataRefreshResult(
                    FoundMatch: true,
                    Updated: true,
                    DownloadedPoster: result.DownloadedPoster,
                    MatchedTitle: match.Title,
                    Message: string.Format(updatedMessageFormat, match.Title))
                : new LibraryMetadataRefreshResult(
                    FoundMatch: true,
                    Updated: false,
                    DownloadedPoster: result.DownloadedPoster,
                    MatchedTitle: match.Title,
                    Message: string.Format(unchangedMessageFormat, match.Title));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new LibraryMetadataRefreshResult(
                EncounteredNetworkError: true,
                Message: $"\u5E94\u7528 TMDB \u5267\u96C6\u5339\u914D\u5931\u8D25\uFF1A{ex.Message}");
        }
    }

    private async Task<string?> ResolvePosterPathAsync(
        string? currentPosterPath,
        TmdbMetadataMatch match,
        CancellationToken cancellationToken)
    {
        return string.IsNullOrWhiteSpace(match.PosterPath)
            ? currentPosterPath
            : await tmdbMetadataClient.DownloadPosterAsync(
                match.PosterPath,
                match.MediaType,
                match.Id,
                cancellationToken);
    }

    private async Task<UpdateResult> UpdateMovieAsync(
        MovieCandidate candidate,
        TmdbMetadataMatch match,
        string? posterPath,
        string? preferredLanguage,
        CancellationToken cancellationToken)
    {
        var newTitle = LibraryPreferredTitleResolver.Resolve(
            match.Title,
            candidate.Title,
            preferredLanguage,
            candidate.SourceProtocolType,
            candidate.BaseUrl,
            candidate.RelativePath);
        var newReleaseDate = match.ReleaseDate ?? candidate.ReleaseDate;
        var newOverview = string.IsNullOrWhiteSpace(match.Overview) ? candidate.Overview : match.Overview;
        var newPosterPath = posterPath ?? candidate.PosterPath;
        var newVoteAverage = match.VoteAverage ?? candidate.VoteAverage;
        var newProductionCountryCodes = PreferMetadataValue(match.ProductionCountryCodes, candidate.ProductionCountryCodes);
        var newOriginalLanguage = PreferMetadataValue(match.OriginalLanguage, candidate.OriginalLanguage);
        var newMetadataLanguage = NormalizeMetadataLanguage(preferredLanguage);

        if (string.Equals(newTitle, candidate.Title, StringComparison.Ordinal) &&
            string.Equals(newReleaseDate, candidate.ReleaseDate, StringComparison.Ordinal) &&
            string.Equals(newOverview, candidate.Overview, StringComparison.Ordinal) &&
            string.Equals(newPosterPath, candidate.PosterPath, StringComparison.Ordinal) &&
            Nullable.Equals(newVoteAverage, candidate.VoteAverage) &&
            string.Equals(newProductionCountryCodes, candidate.ProductionCountryCodes, StringComparison.Ordinal) &&
            string.Equals(newOriginalLanguage, candidate.OriginalLanguage, StringComparison.Ordinal) &&
            string.Equals(newMetadataLanguage, candidate.MetadataLanguage, StringComparison.OrdinalIgnoreCase))
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
            Updated: true,
            DownloadedPoster: !string.Equals(newPosterPath, candidate.PosterPath, StringComparison.Ordinal) &&
                              IsUsableLocalPoster(newPosterPath));
    }

    private async Task<UpdateResult> UpdateTvShowAsync(
        TvShowCandidate candidate,
        TmdbMetadataMatch match,
        string? posterPath,
        string? preferredLanguage,
        CancellationToken cancellationToken)
    {
        var newTitle = LibraryPreferredTitleResolver.Resolve(
            match.Title,
            candidate.Title,
            preferredLanguage,
            candidate.SourceProtocolType,
            candidate.BaseUrl,
            candidate.RelativePath);
        var newFirstAirDate = match.FirstAirDate ?? candidate.FirstAirDate;
        var newOverview = string.IsNullOrWhiteSpace(match.Overview) ? candidate.Overview : match.Overview;
        var newPosterPath = posterPath ?? candidate.PosterPath;
        var newVoteAverage = match.VoteAverage ?? candidate.VoteAverage;
        var newProductionCountryCodes = PreferMetadataValue(match.ProductionCountryCodes, candidate.ProductionCountryCodes);
        var newOriginalLanguage = PreferMetadataValue(match.OriginalLanguage, candidate.OriginalLanguage);
        var newMetadataLanguage = NormalizeMetadataLanguage(preferredLanguage);

        if (string.Equals(newTitle, candidate.Title, StringComparison.Ordinal) &&
            string.Equals(newFirstAirDate, candidate.FirstAirDate, StringComparison.Ordinal) &&
            string.Equals(newOverview, candidate.Overview, StringComparison.Ordinal) &&
            string.Equals(newPosterPath, candidate.PosterPath, StringComparison.Ordinal) &&
            Nullable.Equals(newVoteAverage, candidate.VoteAverage) &&
            string.Equals(newProductionCountryCodes, candidate.ProductionCountryCodes, StringComparison.Ordinal) &&
            string.Equals(newOriginalLanguage, candidate.OriginalLanguage, StringComparison.Ordinal) &&
            string.Equals(newMetadataLanguage, candidate.MetadataLanguage, StringComparison.OrdinalIgnoreCase))
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
            Updated: true,
            DownloadedPoster: !string.Equals(newPosterPath, candidate.PosterPath, StringComparison.Ordinal) &&
                              IsUsableLocalPoster(newPosterPath));
    }

    private async Task<IReadOnlyList<LibraryMetadataSearchCandidate>> ToSearchCandidatesAsync(
        IReadOnlyList<TmdbMetadataMatch> matches,
        LibraryScrapeQueryAttempt attempt,
        CancellationToken cancellationToken)
    {
        List<LibraryMetadataSearchCandidate> candidates = [];

        foreach (var match in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? previewImagePath = null;
            if (!string.IsNullOrWhiteSpace(match.PosterPath))
            {
                try
                {
                    previewImagePath = await tmdbMetadataClient.DownloadPosterAsync(
                        match.PosterPath,
                        match.MediaType,
                        match.Id,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // Keep the candidate result even if preview poster download fails.
                }
            }

            candidates.Add(ToSearchCandidate(match, previewImagePath, attempt));
        }

        return candidates;
    }

    private async Task<SearchCandidatesResult> SearchMovieCandidatesWithFallbackAsync(
        MovieCandidate candidate,
        string? manualQuery,
        string? manualYear,
        CancellationToken cancellationToken)
    {
        var searchYear = ResolveSearchYear(
            candidate.ReleaseDate,
            manualYear,
            candidate.SourceProtocolType,
            candidate.BaseUrl,
            candidate.RelativePath);
        var attempts = BuildQueryAttempts(
            candidate.Title,
            candidate.SourceProtocolType,
            candidate.BaseUrl,
            candidate.RelativePath,
            manualQuery);
        foreach (var attempt in attempts)
        {
            var matches = await tmdbMetadataClient.SearchMovieCandidatesAsync(
                [attempt.Query],
                searchYear,
                cancellationToken,
                new TmdbSearchOptions
                {
                    SecondaryQuery = attempt.SecondaryQuery
                });
            if (matches.Count > 0)
            {
                return new SearchCandidatesResult(
                    await ToSearchCandidatesAsync(matches, attempt, cancellationToken),
                    attempts.Count > 1);
            }
        }

        return new SearchCandidatesResult([], attempts.Count > 1);
    }

    private async Task<SearchCandidatesResult> SearchTvShowCandidatesWithFallbackAsync(
        TvShowCandidate candidate,
        string? manualQuery,
        string? manualYear,
        CancellationToken cancellationToken)
    {
        var preferredSeason = ResolvePreferredSeason(candidate.RelativePath);
        var searchYear = ResolveSearchYear(
            candidate.FirstAirDate,
            manualYear,
            candidate.SourceProtocolType,
            candidate.BaseUrl,
            candidate.RelativePath);
        var attempts = BuildQueryAttempts(
            candidate.Title,
            candidate.SourceProtocolType,
            candidate.BaseUrl,
            candidate.RelativePath,
            manualQuery);
        foreach (var attempt in attempts)
        {
            var matches = await tmdbMetadataClient.SearchTvShowCandidatesAsync(
                [attempt.Query],
                searchYear,
                cancellationToken,
                new TmdbSearchOptions
                {
                    PreferredSeason = preferredSeason,
                    SecondaryQuery = attempt.SecondaryQuery
                });
            if (matches.Count > 0)
            {
                return new SearchCandidatesResult(
                    await ToSearchCandidatesAsync(matches, attempt, cancellationToken),
                    attempts.Count > 1);
            }
        }

        return new SearchCandidatesResult([], attempts.Count > 1);
    }

    private async Task<SearchMatchResult?> SearchMovieBestMatchWithFallbackAsync(
        MovieCandidate candidate,
        string? manualQuery,
        string? manualYear,
        CancellationToken cancellationToken)
    {
        var searchYear = ResolveSearchYear(
            candidate.ReleaseDate,
            manualYear,
            candidate.SourceProtocolType,
            candidate.BaseUrl,
            candidate.RelativePath);
        var attempts = BuildQueryAttempts(
            candidate.Title,
            candidate.SourceProtocolType,
            candidate.BaseUrl,
            candidate.RelativePath,
            manualQuery);
        foreach (var attempt in attempts)
        {
            var match = await tmdbMetadataClient.SearchMovieAsync(
                [attempt.Query],
                searchYear,
                cancellationToken,
                new TmdbSearchOptions
                {
                    SecondaryQuery = attempt.SecondaryQuery
                });
            if (match is not null && LibraryTmdbMatchGuard.IsYearPlausibleMatch(match, searchYear, "movie"))
            {
                return new SearchMatchResult(match, attempt);
            }
        }

        return null;
    }

    private async Task<SearchMatchResult?> SearchTvShowBestMatchWithFallbackAsync(
        TvShowCandidate candidate,
        string? manualQuery,
        string? manualYear,
        CancellationToken cancellationToken)
    {
        var preferredSeason = ResolvePreferredSeason(candidate.RelativePath);
        var searchYear = ResolveSearchYear(
            candidate.FirstAirDate,
            manualYear,
            candidate.SourceProtocolType,
            candidate.BaseUrl,
            candidate.RelativePath);
        var attempts = BuildQueryAttempts(
            candidate.Title,
            candidate.SourceProtocolType,
            candidate.BaseUrl,
            candidate.RelativePath,
            manualQuery);
        foreach (var attempt in attempts)
        {
            var match = await tmdbMetadataClient.SearchTvShowAsync(
                [attempt.Query],
                searchYear,
                cancellationToken,
                new TmdbSearchOptions
                {
                    PreferredSeason = preferredSeason,
                    SecondaryQuery = attempt.SecondaryQuery
                });
            if (match is not null &&
                LibraryTmdbMatchGuard.IsYearPlausibleMatch(
                    match,
                    searchYear,
                    "tv",
                    preferredSeason,
                    tolerance: 10))
            {
                return new SearchMatchResult(match, attempt);
            }
        }

        return null;
    }

    private static string? ResolveSearchYear(
        string? currentYear,
        string? manualYear,
        string? sourceProtocolType,
        string? baseUrl,
        string? relativePath)
    {
        if (manualYear is not null)
        {
            return NormalizeSearchYear(manualYear);
        }

        var normalizedCurrentYear = NormalizeSearchYear(currentYear);
        if (!string.IsNullOrWhiteSpace(normalizedCurrentYear))
        {
            return normalizedCurrentYear;
        }

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var metadataPath = MediaSourcePathResolver.ResolveMetadataPath(
            sourceProtocolType,
            baseUrl,
            relativePath);
        return NormalizeSearchYear(MediaNameParser.ExtractSearchMetadata(metadataPath).Year);
    }

    private static string? NormalizeSearchYear(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        return trimmed.Length >= 4 ? trimmed[..4] : trimmed;
    }

    private static IReadOnlyList<LibraryScrapeQueryAttempt> BuildQueryAttempts(
        string currentTitle,
        string? sourceProtocolType,
        string? baseUrl,
        string? relativePath,
        string? manualQuery)
    {
        return LibraryManualScrapeQueryPlanner.Build(
            currentTitle,
            sourceProtocolType,
            baseUrl,
            relativePath,
            manualQuery);
    }

    private static string BuildNoMatchMessage(
        string? manualQuery,
        string currentTitle,
        string mediaLabel)
    {
        var query = string.IsNullOrWhiteSpace(manualQuery)
            ? currentTitle
            : manualQuery.Trim();
        return $"\u672A\u627E\u5230\u4E0E\u300C{query}\u300D\u5339\u914D\u7684 TMDB {mediaLabel}\u7ED3\u679C，\u5DF2\u81EA\u52A8\u5C1D\u8BD5\u7236\u76EE\u5F55\u4E2D\u6587\u540D、\u5916\u6587\u540D\u548C\u964D\u7EA7\u67E5\u8BE2\u3002";
    }

    private async Task<string?> ResolvePreferredLanguageAsync(CancellationToken cancellationToken)
    {
        if (settingsService is null)
        {
            return TmdbSettings.DefaultLanguage;
        }

        var settings = await settingsService.LoadAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(settings.Tmdb.Language)
            ? TmdbSettings.DefaultLanguage
            : settings.Tmdb.Language;
    }

    private static LibraryMetadataSearchCandidate ToSearchCandidate(
        TmdbMetadataMatch match,
        string? previewImagePath,
        LibraryScrapeQueryAttempt attempt)
    {
        return new LibraryMetadataSearchCandidate(
            match.Id,
            match.MediaType,
            match.Title,
            match.Overview,
            match.ReleaseDate,
            match.FirstAirDate,
            match.PosterPath,
            previewImagePath,
            match.VoteAverage,
            match.Popularity,
            match.OriginalTitle,
            attempt.Query,
            attempt.Label,
            match.ProductionCountryCodes,
            match.OriginalLanguage);
    }

    private static TmdbMetadataMatch ToTmdbMatch(LibraryMetadataSearchCandidate candidate)
    {
        return new TmdbMetadataMatch(
            candidate.TmdbId,
            candidate.MediaType,
            candidate.Title,
            candidate.Overview,
            candidate.ReleaseDate,
            candidate.FirstAirDate,
            candidate.PosterPath,
            candidate.VoteAverage,
            candidate.Popularity,
            candidate.OriginalTitle,
            candidate.ProductionCountryCodes,
            candidate.OriginalLanguage);
    }

    private static string? PreferMetadataValue(string? matchedValue, string? currentValue)
    {
        return string.IsNullOrWhiteSpace(matchedValue)
            ? currentValue
            : matchedValue.Trim();
    }

    private static string? NormalizeMetadataLanguage(string? language)
    {
        var trimmed = language?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
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

    private static bool IsUsableLocalPoster(string? posterPath)
    {
        return !string.IsNullOrWhiteSpace(posterPath)
               && Path.IsPathRooted(posterPath)
               && File.Exists(posterPath);
    }

    private sealed class MovieCandidate
    {
        public long Id { get; init; }

        public string Title { get; init; } = string.Empty;

        public string? ReleaseDate { get; init; }

        public string? Overview { get; init; }

        public string? PosterPath { get; init; }

        public double? VoteAverage { get; init; }

        public string? ProductionCountryCodes { get; init; }

        public string? OriginalLanguage { get; init; }

        public string? MetadataLanguage { get; init; }

        public string? SourceProtocolType { get; init; }

        public string? BaseUrl { get; init; }

        public string? RelativePath { get; init; }
    }

    private sealed class TvShowCandidate
    {
        public long Id { get; init; }

        public string Title { get; init; } = string.Empty;

        public string? FirstAirDate { get; init; }

        public string? Overview { get; init; }

        public string? PosterPath { get; init; }

        public double? VoteAverage { get; init; }

        public string? ProductionCountryCodes { get; init; }

        public string? OriginalLanguage { get; init; }

        public string? MetadataLanguage { get; init; }

        public string? SourceProtocolType { get; init; }

        public string? BaseUrl { get; init; }

        public string? RelativePath { get; init; }
    }

    private sealed record UpdateResult(bool Updated, bool DownloadedPoster);

    private sealed record SearchCandidatesResult(
        IReadOnlyList<LibraryMetadataSearchCandidate> Candidates,
        bool UsedFallback);

    private sealed record SearchMatchResult(
        TmdbMetadataMatch Match,
        LibraryScrapeQueryAttempt Attempt);
}
