namespace OmniPlay.Core.Models.Playback;

public sealed record EpisodeInfo(
    int Season,
    int Episode,
    string DisplayName,
    bool IsTvShow,
    int? PartNumber = null,
    string? Subtitle = null,
    string? Year = null);
