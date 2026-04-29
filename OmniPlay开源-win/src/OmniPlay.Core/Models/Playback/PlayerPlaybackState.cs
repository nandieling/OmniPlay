namespace OmniPlay.Core.Models.Playback;

public sealed class PlayerPlaybackState
{
    public static PlayerPlaybackState Empty { get; } = new();

    public bool HasMedia { get; init; }

    public bool IsPaused { get; init; }

    public bool IsPlaybackCompleted { get; init; }

    public bool IsMuted { get; init; }

    public double VolumePercent { get; init; } = 100;

    public IReadOnlyList<PlayerTrackInfo> AudioTracks { get; init; } = [];

    public IReadOnlyList<PlayerTrackInfo> SubtitleTracks { get; init; } = [];

    public double PositionSeconds { get; init; }

    public double DurationSeconds { get; init; }

    public double SubtitleDelaySeconds { get; init; }

    public int SubtitleFontSize { get; init; } = 16;
}
