namespace OmniPlay.Core.Models.Playback;

public sealed record PlayerTrackInfo(
    string Type,
    long? TrackId,
    string DisplayName,
    bool IsSelected = false,
    bool IsOffOption = false,
    string Language = "");
