namespace OmniPlay.Core.Settings;

public sealed record AppSettings
{
    public bool AutoScanOnStartup { get; init; } = true;

    public bool AutoCheckUpdatesOnStartup { get; init; } = true;

    public DateTimeOffset? LastUpdateCheckAt { get; init; }

    public bool ShowMediaSourceRealPath { get; init; } = true;

    public LibraryViewSettings LibraryView { get; init; } = new();

    public string OfflineCacheDirectory { get; init; } = string.Empty;

    public TmdbSettings Tmdb { get; init; } = new();

    public LocalMetadataSettings LocalMetadata { get; init; } = new();

    public PlaybackPreferenceSettings Playback { get; init; } = new();

    public LuckyStunSettings LuckyStun { get; init; } = new();
}
