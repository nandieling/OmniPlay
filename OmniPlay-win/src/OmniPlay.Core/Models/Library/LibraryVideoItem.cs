using OmniPlay.Core.Models.Playback;

namespace OmniPlay.Core.Models.Library;

public sealed class LibraryVideoItem
{
    public string Id { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public string RelativePath { get; init; } = string.Empty;

    public string? MetadataPath { get; init; }

    public string AbsolutePath { get; init; } = string.Empty;

    public string PlaybackPath { get; init; } = string.Empty;

    public string SourceProtocolType { get; init; } = string.Empty;

    public string SourceBasePath { get; init; } = string.Empty;

    public string? SourceAuthConfig { get; init; }

    public string? LocalIsoPlaybackPath { get; init; }

    public string? OfflineCachePath { get; init; }

    public string EffectivePlaybackPath =>
        !string.IsNullOrWhiteSpace(OfflineCachePath)
            ? OfflineCachePath
            : !string.IsNullOrWhiteSpace(LocalIsoPlaybackPath)
            ? LocalIsoPlaybackPath
            : string.IsNullOrWhiteSpace(PlaybackPath)
                ? AbsolutePath
                : PlaybackPath;

    public string? ThumbnailPath { get; init; }

    public string? FallbackImagePath { get; init; }

    public string? PreviewImagePath => CustomThumbnailPath ?? ThumbnailPath ?? FallbackImagePath;

    public double PlayProgress { get; init; }

    public double Duration { get; init; }

    public double? LastPlayedAt { get; init; }

    public int SeasonNumber { get; init; }

    public int EpisodeNumber { get; init; }

    public bool IsTvEpisode { get; init; }

    public string EpisodeLabel { get; init; } = string.Empty;

    public string? EpisodeSubtitle { get; init; }

    public string? CustomEpisodeSubtitle { get; init; }

    public string? EpisodeYear { get; init; }

    public string? CustomThumbnailPath { get; init; }

    public string SeasonEpisodeText
    {
        get
        {
            if (!IsTvEpisode)
            {
                return FileName;
            }

            var seasonText = SeasonNumber == 0
                ? "\u7279\u522B\u7BC7"
                : $"\u7B2C{SeasonNumber}\u5B63";
            return $"{seasonText}\u7B2C{EpisodeNumber}\u96C6";
        }
    }

    public string EpisodeDisplayTitle
    {
        get
        {
            if (!IsTvEpisode)
            {
                return SeasonEpisodeText;
            }

            var subtitle = EpisodeDisplaySubtitle;
            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                return string.Concat(SeasonEpisodeText, " \u00B7 ", subtitle);
            }

            return SeasonEpisodeText;
        }
    }

    public string EpisodeDisplaySubtitle
    {
        get
        {
            var subtitle = CustomEpisodeSubtitle?.Trim();
            if (!IsTvEpisode)
            {
                return subtitle ?? string.Empty;
            }

            return subtitle ?? EpisodeSubtitle?.Trim() ?? string.Empty;
        }
    }

    public bool HasEpisodeDisplaySubtitle => false;

    public double ProgressRatio => PlaybackProgressRules.GetProgressRatio(PlayProgress, Duration);

    public bool HasProgress => PlaybackProgressRules.HasStarted(PlayProgress);

    public bool IsWatched => PlaybackProgressRules.IsCompleted(PlayProgress, Duration);

    public PlaybackWatchState WatchState => PlaybackProgressRules.GetWatchState(PlayProgress, Duration);

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
        "\u79BB\u7EBF\u7F13\u5B58\u8FD9\u4E00\u96C6";

    public bool ShouldShowEpisodeProgress => WatchState == PlaybackWatchState.InProgress && ProgressRatio > 0;

    public bool IsWatchedState => WatchState == PlaybackWatchState.Watched;

    public bool IsInProgressState => WatchState == PlaybackWatchState.InProgress;

    public bool IsUnwatchedState => WatchState == PlaybackWatchState.Unwatched;

    public string WatchStateText => PlaybackProgressRules.GetWatchStateText(WatchState);

    public string PositionText =>
        PlayProgress > 0
            ? TimeSpan.FromSeconds(PlayProgress).ToString(Duration >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss")
            : "00:00";

    public string DurationText =>
        Duration > 0
            ? TimeSpan.FromSeconds(Duration).ToString(Duration >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss")
            : "\u672A\u77E5\u65F6\u957F";

    public string ProgressText =>
        WatchState switch
        {
            PlaybackWatchState.Watched => "\u5DF2\u64AD",
            PlaybackWatchState.InProgress when Duration > 0 => $"\u672A\u64AD\u5B8C \u00B7 {(ProgressRatio * 100):F0}%",
            PlaybackWatchState.InProgress => "\u672A\u64AD\u5B8C",
            _ => "\u672A\u64AD"
        };
}
