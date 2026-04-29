namespace OmniPlay.Core.Models.Playback;

public sealed record SearchMetadata(
    string? ChineseTitle,
    string? ForeignTitle,
    string? FullCleanTitle,
    string? Year);
