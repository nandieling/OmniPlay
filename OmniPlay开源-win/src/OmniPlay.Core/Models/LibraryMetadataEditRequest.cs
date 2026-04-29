namespace OmniPlay.Core.Models;

public sealed record LibraryMetadataEditRequest(
    string Title,
    string? Date,
    string? Overview,
    string? PosterPath,
    double? VoteAverage,
    bool IsLocked = true);
