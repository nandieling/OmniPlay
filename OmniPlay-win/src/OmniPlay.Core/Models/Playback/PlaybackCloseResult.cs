namespace OmniPlay.Core.Models.Playback;

public sealed record PlaybackCloseResult(
    string FilePath,
    double PositionSeconds,
    double DurationSeconds);
