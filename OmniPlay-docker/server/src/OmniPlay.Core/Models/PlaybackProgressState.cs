namespace OmniPlay.Core.Models;

public sealed record PlaybackProgressState(
    double PositionSeconds,
    double DurationSeconds,
    bool IsWatched);
