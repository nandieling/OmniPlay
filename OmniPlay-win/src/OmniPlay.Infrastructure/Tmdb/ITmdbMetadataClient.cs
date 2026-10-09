using OmniPlay.Core.Models.Library;

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

    /// <summary>人物头像下载到本地海报目录并返回本地路径；已下载过直接复用。</summary>
    Task<string?> DownloadProfileImageAsync(
        string profilePath,
        int personId,
        CancellationToken cancellationToken = default);

    Task<string?> DownloadEpisodeStillAsync(
        int tmdbShowId,
        int seasonNumber,
        int episodeNumber,
        string videoFileId,
        CancellationToken cancellationToken = default);

    Task<TmdbCredits?> FetchCreditsAsync(
        int tmdbId,
        string mediaType,
        CancellationToken cancellationToken = default);

    /// <summary>人物中文译名（详见实现注释：区分"无中文译名"与"请求失败"）。</summary>
    Task<OmniPlay.Core.Models.Library.TmdbChineseNameResult> FetchChinesePersonNameAsync(
        int personId,
        CancellationToken cancellationToken = default);

    /// <summary>作品中文片名（TMDB 官方别名表）。</summary>
    Task<OmniPlay.Core.Models.Library.TmdbChineseNameResult> FetchChineseTitleAsync(
        string? mediaType,
        int id,
        CancellationToken cancellationToken = default);

    /// <summary>人物作品列表（combined_credits）。</summary>
    Task<System.Collections.Generic.IReadOnlyList<OmniPlay.Core.Models.Library.TmdbFilmographyItem>?> FetchPersonFilmographyAsync(
        int personId,
        string kind,
        CancellationToken cancellationToken = default);

    /// <summary>人物档案（中文名、头像、简介、生日、出生地、别名）。</summary>
    Task<OmniPlay.Core.Models.Library.TmdbPersonDetail?> FetchPersonDetailAsync(
        int personId,
        CancellationToken cancellationToken = default);
}
