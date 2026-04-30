namespace OmniPlay.Infrastructure.Tmdb;

public interface ITmdbMetadataClient
{
    Task<IReadOnlyList<TmdbMetadataMatch>> SearchMovieCandidatesAsync(
        IReadOnlyList<string> candidateTitles,
        string? year,
        CancellationToken cancellationToken = default,
        TmdbSearchOptions? options = null);

    Task<TmdbMetadataMatch?> SearchMovieAsync(
        IReadOnlyList<string> candidateTitles,
        string? year,
        CancellationToken cancellationToken = default,
        TmdbSearchOptions? options = null);

    Task<IReadOnlyList<TmdbMetadataMatch>> SearchTvShowCandidatesAsync(
        IReadOnlyList<string> candidateTitles,
        string? year,
        CancellationToken cancellationToken = default,
        TmdbSearchOptions? options = null);

    Task<TmdbMetadataMatch?> SearchTvShowAsync(
        IReadOnlyList<string> candidateTitles,
        string? year,
        CancellationToken cancellationToken = default,
        TmdbSearchOptions? options = null);

    Task<string?> DownloadPosterAsync(
        string posterPath,
        string mediaType,
        int tmdbId,
        CancellationToken cancellationToken = default);

    Task<string?> DownloadEpisodeStillAsync(
        int tmdbShowId,
        int seasonNumber,
        int episodeNumber,
        string videoFileId,
        CancellationToken cancellationToken = default);
}
