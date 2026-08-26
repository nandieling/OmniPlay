using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Core.Interfaces;

public interface IOmniPlayDockerClient
{
    Task<IReadOnlyList<OmniPlayDockerLibraryItem>> GetLibraryItemsAsync(
        MediaSource source,
        CancellationToken cancellationToken = default);

    Task<OmniPlayDockerLibraryDetail?> GetLibraryDetailAsync(
        MediaSource source,
        string libraryItemId,
        CancellationToken cancellationToken = default);

    Task<byte[]> GetPosterDataAsync(
        MediaSource source,
        string posterAssetId,
        CancellationToken cancellationToken = default);

    Task<byte[]> GetThumbnailDataAsync(
        MediaSource source,
        string thumbnailAssetId,
        CancellationToken cancellationToken = default);

    Task<string?> GetPlaybackUrlAsync(
        string baseUrl,
        string? authConfig,
        string videoFileId,
        CancellationToken cancellationToken = default);

    Task<OmniPlayDockerPlaybackProgress?> GetPlaybackProgressAsync(
        string baseUrl,
        string? authConfig,
        string videoFileId,
        CancellationToken cancellationToken = default);

    Task UpdateProgressAsync(
        string baseUrl,
        string? authConfig,
        string videoFileId,
        double positionSeconds,
        double durationSeconds,
        CancellationToken cancellationToken = default);

    Task ImportDoubanMetadataAsync(
        MediaSource source,
        string libraryItemId,
        DoubanMetadata metadata,
        CancellationToken cancellationToken = default);
}

public sealed record OmniPlayDockerLibraryItem(
    string Id,
    string ItemKind,
    string Title,
    string? ReleaseDate,
    string? Overview,
    string? PosterAssetId,
    double? VoteAverage,
    double? DoubanRating,
    double MaxProgressSeconds,
    double MaxDurationSeconds);

public sealed record OmniPlayDockerLibraryDetail(
    string Id,
    string ItemKind,
    string Title,
    string? ReleaseDate,
    string? Overview,
    string? PosterAssetId,
    double? VoteAverage,
    double? DoubanRating,
    OmniPlayDockerDoubanMetadata? Douban,
    IReadOnlyList<OmniPlayDockerVideoFile> VideoFiles,
    IReadOnlyList<OmniPlayDockerSeason> Seasons);

public sealed record OmniPlayDockerDoubanMetadata(
    string SubjectId,
    string SubjectUrl,
    string Title,
    string? OriginalTitle,
    string? Year,
    double? Rating,
    int? RatingCount,
    string? Summary,
    string? Genres,
    string? Countries,
    string? PosterUrl,
    DateTimeOffset FetchedAt);

public sealed record OmniPlayDockerSeason(IReadOnlyList<OmniPlayDockerEpisode> Episodes);

public sealed record OmniPlayDockerEpisode(
    string Id,
    int SeasonNumber,
    int EpisodeNumber,
    string? Title,
    string? Overview,
    string? StillAssetId,
    string? AirDate,
    OmniPlayDockerVideoFile? VideoFile,
    IReadOnlyList<OmniPlayDockerVideoFile>? VideoFiles = null);

public sealed record OmniPlayDockerVideoFile(
    string Id,
    string RelativePath,
    string FileName,
    string MediaKind,
    long? FileSizeBytes,
    double DurationSeconds,
    double PositionSeconds,
    bool IsWatched);

public sealed record OmniPlayDockerPlaybackProgress(
    double PositionSeconds,
    double DurationSeconds,
    bool IsWatched);
