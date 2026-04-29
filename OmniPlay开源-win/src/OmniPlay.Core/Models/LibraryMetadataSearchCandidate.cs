namespace OmniPlay.Core.Models;

public sealed record LibraryMetadataSearchCandidate(
    int TmdbId,
    string MediaType,
    string Title,
    string? Overview,
    string? ReleaseDate,
    string? FirstAirDate,
    string? PosterPath,
    string? PreviewImagePath,
    double? VoteAverage,
    double? Popularity,
    string? OriginalTitle = null,
    string? MatchedQuery = null,
    string? MatchedQueryLabel = null,
    string? ProductionCountryCodes = null,
    string? OriginalLanguage = null)
{
    public string DateText =>
        !string.IsNullOrWhiteSpace(ReleaseDate)
            ? ReleaseDate!
            : !string.IsNullOrWhiteSpace(FirstAirDate)
                ? FirstAirDate!
                : "\u65E5\u671F\u5F85\u8865\u5145";

    public string MediaTypeText =>
        string.Equals(MediaType, "tv", StringComparison.OrdinalIgnoreCase)
            ? "\u5267\u96C6"
            : "\u7535\u5F71";

    public string RatingText =>
        VoteAverage.HasValue
            ? $"\u8BC4\u5206 {VoteAverage.Value:F1}"
            : "\u8BC4\u5206\u5F85\u8865\u5145";

    public string OverviewText =>
        string.IsNullOrWhiteSpace(Overview)
            ? "\u6682\u65E0\u7B80\u4ECB\u3002"
            : Overview!;

    public bool HasOriginalTitle =>
        !string.IsNullOrWhiteSpace(OriginalTitle) &&
        !string.Equals(OriginalTitle.Trim(), Title.Trim(), StringComparison.OrdinalIgnoreCase);

    public string OriginalTitleText =>
        HasOriginalTitle
            ? OriginalTitle!
            : string.Empty;

    public bool HasMatchedQuery =>
        !string.IsNullOrWhiteSpace(MatchedQuery);

    public string MatchedQueryText =>
        !HasMatchedQuery
            ? string.Empty
            : string.IsNullOrWhiteSpace(MatchedQueryLabel)
                ? $"命中查询：{MatchedQuery}"
                : $"{MatchedQueryLabel}：{MatchedQuery}";

    public string MatchMetaText => $"{DateText} \u00B7 {RatingText}";
}
