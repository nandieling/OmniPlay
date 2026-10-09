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

    public bool HasTmdbRating => VoteAverage.HasValue && VoteAverage.Value > 0;

    public bool HasDoubanRating => DoubanRating.HasValue && DoubanRating.Value > 0;

    public string TmdbRatingText => VoteAverage.HasValue
        ? VoteAverage.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
        : string.Empty;

    public string DoubanRatingText => DoubanRating.HasValue
        ? DoubanRating.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
        : string.Empty;

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
    public string? Director { get; init; }

    /// <summary>导演名单 JSON，缺失时回退到 <see cref="Director"/> 扁平列。</summary>
    public string? Directors { get; init; }

    /// <summary>主演名单 JSON，缺失时回退到 <see cref="MainCast"/> 扁平列。</summary>
    public string? Casts { get; init; }

    public System.Collections.Generic.List<string> DirectorList => OmniPlay.Core.Models.PersonNames.Decode(Directors ?? Director);

    public System.Collections.Generic.List<string> CastList => OmniPlay.Core.Models.PersonNames.Decode(Casts ?? MainCast);

    public string? MainCast { get; init; }

    public bool IsFolder { get; init; }

    public string? FolderKey { get; init; }

    /// <summary>分类文件夹内的影视数量，卡片上与名称同行显示。</summary>
    public int? FolderItemCount { get; init; }

    public string FolderCountText =>
        FolderItemCount.HasValue
            ? FolderItemCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
}
