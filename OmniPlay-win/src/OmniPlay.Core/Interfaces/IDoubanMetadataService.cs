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

    Task<DoubanMetadata?> GetAsync(
        string itemKind,
        long entityId,
        CancellationToken cancellationToken = default);

    Task UnbindAsync(
        string itemKind,
        long entityId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 豆瓣风控（403/429/验证码）触发后，服务进入冷却期；冷却期内的请求会抛出该异常。
/// </summary>
public sealed class DoubanRateLimitException : Exception
{
    public DoubanRateLimitException(string message)
        : base(message)
    {
    }
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
    DateTimeOffset FetchedAt,
    string? PosterLocalPath = null)
{
    /// <summary>豆瓣页面里的中文导演名单，可补齐 TMDB 缺失的人名。</summary>
    public IReadOnlyList<string> Directors { get; init; } = [];

    /// <summary>豆瓣页面里的中文主演名单。</summary>
    public IReadOnlyList<string> Casts { get; init; } = [];

    public string? DirectorText => Directors.Count > 0 ? string.Join("、", Directors) : null;

    public string? CastText => Casts.Count > 0 ? string.Join("、", Casts) : null;
}
