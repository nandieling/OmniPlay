namespace OmniPlay.Core.Interfaces;

public interface IDoubanMetadataService
{
    Task<DoubanMetadata?> BindAsync(
        string itemKind,
        long entityId,
        string subject,
        string fallbackTitle,
        string? fallbackYear,
        CancellationToken cancellationToken = default);
}

public sealed record DoubanMetadata(
    string ItemKind,
    long EntityId,
    string SubjectId,
    string SubjectUrl,
    string Title,
    string? OriginalTitle,
    string? Year,
    double? Rating,
    int? RatingCount,
    string? Summary,
    string? PosterUrl,
    DateTimeOffset FetchedAt);
