namespace OmniPlay.Core.Models.Playback;

public sealed record PlayerNavigationItem(
    string Id,
    string Title,
    string Subtitle,
    string StatusText,
    bool IsCurrent = false,
    bool IsWatched = false);
