using OmniPlay.Core.Models.Playback;

namespace OmniPlay.Core.Models.Library;

public sealed class LibraryPosterItem
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? Subtitle { get; init; }

    public string? PosterPath { get; init; }

    public double? VoteAverage { get; init; }

    public required string MediaKind { get; init; }

    public double ContinueWatchingProgress { get; init; }

    public string? ContinueWatchingLabel { get; init; }

    public bool IsContinuing { get; init; }

    public PlaybackWatchState WatchState { get; init; } = PlaybackWatchState.Unwatched;

    public string WatchStateText => PlaybackProgressRules.GetWatchStateText(WatchState);

    public bool IsWatchedState => WatchState == PlaybackWatchState.Watched;

    public bool IsInProgressState => WatchState == PlaybackWatchState.InProgress;

    public bool IsUnwatchedState => WatchState == PlaybackWatchState.Unwatched;

    public long? MovieId { get; init; }

    public long? TvShowId { get; init; }
}
