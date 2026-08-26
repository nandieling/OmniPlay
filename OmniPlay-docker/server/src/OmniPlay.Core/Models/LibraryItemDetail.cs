namespace OmniPlay.Core.Models;

public sealed record LibraryItemDetail(
    string Id,
    string ItemKind,
    string Title,
    string? ReleaseDate,
    string? Overview,
    string? PosterAssetId,
    double? VoteAverage,
    double? DoubanRating,
    DoubanMetadata? Douban,
    bool IsLocked,
    bool IsWatched,
    int VideoFileCount,
    double MaxProgressSeconds,
    double MaxDurationSeconds,
    DateTimeOffset UpdatedAt,
    int? TmdbId,
    IReadOnlyList<VideoFileSummary> VideoFiles,
    IReadOnlyList<SeasonDetail> Seasons);

public sealed record SeasonDetail(
    string Id,
    int SeasonNumber,
    string? Title,
    string? PosterAssetId,
    IReadOnlyList<EpisodeDetail> Episodes);

public sealed record EpisodeDetail(
    string Id,
    string SeasonId,
    int SeasonNumber,
    int EpisodeNumber,
    string? Title,
    string? Overview,
    string? StillAssetId,
    string? AirDate,
    VideoFileSummary? VideoFile,
    IReadOnlyList<VideoFileSummary> VideoFiles);

public sealed record VideoFileSummary(
    string Id,
    long SourceId,
    string SourceName,
    string RelativePath,
    string FileName,
    string MediaKind,
    long? FileSizeBytes,
    double DurationSeconds,
    double PositionSeconds,
    bool IsWatched,
    string? EpisodeId,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? EpisodeTitle,
    string? Container,
    string? VideoCodec,
    string? AudioCodec,
    string? SubtitleSummary,
    IReadOnlyList<VideoFileStreamSummary> AudioTracks,
    IReadOnlyList<VideoFileStreamSummary> SubtitleStreams);
