using OmniPlay.Core.Models.Playback;

namespace OmniPlay.Core.Models.Library;

public sealed class LibraryVideoItem
{
    public string Id { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public string RelativePath { get; init; } = string.Empty;

    public string AbsolutePath { get; init; } = string.Empty;

    public string PlaybackPath { get; init; } = string.Empty;

    public string EffectivePlaybackPath =>
        string.IsNullOrWhiteSpace(PlaybackPath)
            ? AbsolutePath
            : PlaybackPath;

    public string? ThumbnailPath { get; init; }

    public string? FallbackImagePath { get; init; }

    public string? PreviewImagePath => CustomThumbnailPath ?? ThumbnailPath ?? FallbackImagePath;

    public double PlayProgress { get; init; }

    public double Duration { get; init; }

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
                : $"\u7B2C {SeasonNumber} \u5B63";
            return $"{seasonText} \u7B2C {EpisodeNumber} \u96C6";
        }
    }

    public string EpisodeDisplayTitle => SeasonEpisodeText;

    public string EpisodeDisplaySubtitle
    {
        get
        {
            var subtitle = CustomEpisodeSubtitle?.Trim();
            var year = EpisodeYear?.Trim();
            if (!IsTvEpisode)
            {
                return subtitle ?? string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(year) && !string.IsNullOrWhiteSpace(subtitle))
            {
                return $"{year} \u00B7 {subtitle}";
            }

            return !string.IsNullOrWhiteSpace(subtitle)
                ? subtitle
                : year ?? string.Empty;
        }
    }

    public bool HasEpisodeDisplaySubtitle => !string.IsNullOrWhiteSpace(EpisodeDisplaySubtitle);

    public double ProgressRatio => PlaybackProgressRules.GetProgressRatio(PlayProgress, Duration);

    public bool HasProgress => PlaybackProgressRules.HasStarted(PlayProgress);

    public bool IsWatched => PlaybackProgressRules.IsCompleted(PlayProgress, Duration);

    public PlaybackWatchState WatchState => PlaybackProgressRules.GetWatchState(PlayProgress, Duration);

    public bool ShouldShowEpisodeProgress => WatchState == PlaybackWatchState.InProgress && ProgressRatio > 0;

    public double EpisodeThumbnailDimOpacity =>
        WatchState switch
        {
            PlaybackWatchState.Watched => 0,
            PlaybackWatchState.InProgress => 0.16,
            _ => 0.38
        };

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
            PlaybackWatchState.Watched => "\u5DF2\u770B",
            PlaybackWatchState.InProgress when Duration > 0 => $"\u672A\u770B\u5B8C \u00B7 {(ProgressRatio * 100):F0}%",
            PlaybackWatchState.InProgress => "\u672A\u770B\u5B8C",
            _ => "\u672A\u770B"
        };
}
