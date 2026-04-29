namespace OmniPlay.Player.Mpv.Models;

public sealed record PlayerStateSnapshot(
    bool IsPlaying,
    double PositionSeconds,
    double DurationSeconds);
