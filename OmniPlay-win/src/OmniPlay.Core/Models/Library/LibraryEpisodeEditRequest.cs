namespace OmniPlay.Core.Models.Library;

public sealed record LibraryEpisodeEditRequest(
    int? SeasonNumber,
    int? EpisodeNumber,
    string? Year,
    string? Subtitle,
    string? ThumbnailPath);
