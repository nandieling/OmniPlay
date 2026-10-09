namespace OmniPlay.Core.Settings;

public sealed record AppSettings
{
    /// <summary>界面与刮削语言：<c>zh-Hans</c>（默认）或 <c>en</c>；对齐 mac 版的 <c>appLanguage</c>。</summary>
    public string AppLanguage { get; init; } = "zh-Hans";

    public bool AutoScanOnStartup { get; init; } = true;

    public bool AutoCheckUpdatesOnStartup { get; init; } = true;

    public DateTimeOffset? LastUpdateCheckAt { get; init; }

    public bool ShowMediaSourceRealPath { get; init; } = true;

    public LibraryViewSettings LibraryView { get; init; } = new();

    public string OfflineCacheDirectory { get; init; } = string.Empty;

    public TmdbSettings Tmdb { get; init; } = new();

    public LocalMetadataSettings LocalMetadata { get; init; } = new();

    public PlaybackPreferenceSettings Playback { get; init; } = new();

    public string ThemeId { get; init; } = string.Empty;

    public string HomeCategoryMode { get; init; } = "none";

    public Dictionary<string, string> HomeFolderPosters { get; init; } = [];
    public LuckyStunSettings LuckyStun { get; init; } = new();
}
