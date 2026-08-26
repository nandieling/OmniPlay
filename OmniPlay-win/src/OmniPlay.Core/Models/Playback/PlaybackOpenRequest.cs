namespace OmniPlay.Core.Models.Playback;

public sealed record PlaybackOpenRequest(
    string PlaybackPath,
    string? DisplayPath = null,
    string? MediaFileName = null)
{
    public string EffectiveDisplayPath =>
        string.IsNullOrWhiteSpace(DisplayPath)
            ? PlaybackPath
            : DisplayPath;
}
