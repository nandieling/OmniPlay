namespace OmniPlay.Core.Models.Playback;

public sealed record PlaybackOpenRequest(
    string PlaybackPath,
    string? DisplayPath = null)
{
    public string EffectiveDisplayPath =>
        string.IsNullOrWhiteSpace(DisplayPath)
            ? PlaybackPath
            : DisplayPath;
}
