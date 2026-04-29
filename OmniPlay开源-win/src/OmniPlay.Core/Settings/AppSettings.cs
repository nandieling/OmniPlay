namespace OmniPlay.Core.Settings;

public sealed record AppSettings
{
    public bool AutoScanOnStartup { get; init; } = true;

    public bool ShowMediaSourceRealPath { get; init; } = true;

    public TmdbSettings Tmdb { get; init; } = new();

    public PlaybackPreferenceSettings Playback { get; init; } = new();
}
