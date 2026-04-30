namespace OmniPlay.Core.Models.Playback;

public enum PlaybackWatchState
{
    Unwatched,
    InProgress,
    Watched
}

public static class PlaybackProgressRules
{
    public const double CompletionRatio = 0.97;
    public const double MinimumProgressSeconds = 0;

    public static double GetProgressRatio(double playProgress, double duration)
    {
        return duration > 0
            ? Math.Clamp(Math.Max(playProgress, 0) / duration, 0, 1)
            : 0;
    }

    public static bool HasStarted(double playProgress)
    {
        return playProgress > MinimumProgressSeconds;
    }

    public static bool IsCompleted(double playProgress, double duration)
    {
        return duration > 0 && GetProgressRatio(playProgress, duration) >= CompletionRatio;
    }

    public static PlaybackWatchState GetWatchState(double playProgress, double duration)
    {
        if (IsCompleted(playProgress, duration))
        {
            return PlaybackWatchState.Watched;
        }

        return HasStarted(playProgress)
            ? PlaybackWatchState.InProgress
            : PlaybackWatchState.Unwatched;
    }

    public static string GetWatchStateText(PlaybackWatchState state)
    {
        return state switch
        {
            PlaybackWatchState.Watched => "\u5DF2\u770B",
            PlaybackWatchState.InProgress => "\u672A\u770B\u5B8C",
            _ => "\u672A\u770B"
        };
    }
}
