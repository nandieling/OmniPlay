using OmniPlay.Core.Models.Playback;

namespace OmniPlay.Core.Models.Library;

public sealed class LibraryPosterItem
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? Subtitle { get; init; }

    public string? PosterPath { get; init; }

    public double? VoteAverage { get; init; }

    public double? DoubanRating { get; init; }

    public string RatingDisplayText
    {
        get
        {
            var parts = new List<string>();
            if (VoteAverage.HasValue)
            {
                parts.Add($"TMDB {VoteAverage.Value:F1}");
            }

            if (DoubanRating.HasValue)
            {
                parts.Add($"豆瓣 {DoubanRating.Value:F1}");
            }

            return parts.Count == 0 ? "暂无评分" : string.Join("  ", parts);
        }
    }

    public required string MediaKind { get; init; }

    public double ContinueWatchingProgress { get; init; }

    public double? LastPlayedAt { get; init; }

    public string? ContinueWatchingLabel { get; init; }

    public bool IsContinuing { get; init; }

    public PlaybackWatchState WatchState { get; init; } = PlaybackWatchState.Unwatched;

    public bool OfflineCacheIsDownloading { get; init; }

    public bool OfflineCacheIsCached { get; init; }

    public bool OfflineCacheUnavailable { get; init; }

    public double OfflineCacheProgress { get; init; }

    public bool ShowOfflineCacheProgress => OfflineCacheIsDownloading;

    public bool ShowOfflineCacheIcon => !OfflineCacheIsDownloading;

    public string OfflineCachePercentText => $"{Math.Clamp(OfflineCacheProgress, 0, 1) * 100:F0}%";

    public string OfflineCacheGlyph =>
        OfflineCacheIsCached ? "\u2713" :
        OfflineCacheUnavailable ? "\u26A0" :
        "\u2193";

    public string OfflineCacheTip =>
        OfflineCacheIsDownloading ? "\u6B63\u5728\u79BB\u7EBF\u7F13\u5B58" :
        OfflineCacheIsCached ? "\u5DF2\u7F13\u5B58\u5230\u672C\u5730" :
        OfflineCacheUnavailable ? "\u8FDC\u7A0B\u6E90\u6216\u6E90\u6587\u4EF6\u4E0D\u53EF\u7528" :
        "\u79BB\u7EBF\u7F13\u5B58\u6574\u90E8\u5F71\u7247\u6216\u5267\u96C6";

    public string WatchStateText => PlaybackProgressRules.GetWatchStateText(WatchState);

    public bool IsWatchedState => WatchState == PlaybackWatchState.Watched;

    public bool IsInProgressState => WatchState == PlaybackWatchState.InProgress;

    public bool IsUnwatchedState => WatchState == PlaybackWatchState.Unwatched;

    public long? MovieId { get; init; }

    public long? TvShowId { get; init; }
}
