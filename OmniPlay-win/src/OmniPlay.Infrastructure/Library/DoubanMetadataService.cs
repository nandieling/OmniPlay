using System.Text.RegularExpressions;
using Dapper;
using OmniPlay.Core.Interfaces;
using OmniPlay.Infrastructure.Data;

namespace OmniPlay.Infrastructure.Library;

public sealed class DoubanMetadataService : IDoubanMetadataService
{
    private readonly SqliteDatabase database;
    private readonly HttpClient httpClient;

    public DoubanMetadataService(SqliteDatabase database, HttpClient httpClient)
    {
        this.database = database;
        this.httpClient = httpClient;
    }

    public async Task<DoubanMetadata?> BindAsync(
        string itemKind,
        long entityId,
        string subject,
        string fallbackTitle,
        string? fallbackYear,
        CancellationToken cancellationToken = default)
    {
        var normalizedKind = NormalizeKind(itemKind);
        var subjectId = ExtractSubjectId(subject);
        if (string.IsNullOrWhiteSpace(subjectId))
        {
            throw new ArgumentException("请填写豆瓣 subject ID 或豆瓣电影链接。", nameof(subject));
        }

        var metadata = await FetchSubjectAsync(normalizedKind, entityId, subjectId, fallbackTitle, fallbackYear, cancellationToken)
                       ?? CreatePlaceholder(normalizedKind, entityId, subjectId, fallbackTitle, fallbackYear);
        await SaveAsync(metadata, cancellationToken);
        return metadata;
    }

    private async Task<DoubanMetadata?> FetchSubjectAsync(
        string itemKind,
        long entityId,
        string subjectId,
        string fallbackTitle,
        string? fallbackYear,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://m.douban.com/movie/subject/{subjectId}/");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            request.Headers.Referrer = new Uri("https://m.douban.com/movie/");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (html.Contains("sec.douban.com", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var title = HtmlDecode(FirstMatch(html, "<title>(.*?)</title>"))?
                .Replace("(豆瓣)", string.Empty, StringComparison.Ordinal)
                .Trim();
            var ratingText = FirstMatch(html, "\"ratingValue\"\\s*:\\s*\"?([0-9.]+)");
            var countText = FirstMatch(html, "\"ratingCount\"\\s*:\\s*\"?(\\d+)");
            var summary = HtmlDecode(FirstMatch(html, "\"description\"\\s*:\\s*\"(.*?)\""));
            var poster = FirstMatch(html, "(https?://img[0-9]?\\.doubanio\\.com/view/photo/[^\"]+)");

            return new DoubanMetadata(
                itemKind,
                entityId,
                subjectId,
                $"https://movie.douban.com/subject/{subjectId}/",
                string.IsNullOrWhiteSpace(title) ? fallbackTitle : title,
                null,
                NormalizeYear(fallbackYear),
                double.TryParse(ratingText, System.Globalization.CultureInfo.InvariantCulture, out var rating) ? rating : null,
                int.TryParse(countText, out var ratingCount) ? ratingCount : null,
                string.IsNullOrWhiteSpace(summary) ? null : summary,
                poster,
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    private static DoubanMetadata CreatePlaceholder(string itemKind, long entityId, string subjectId, string title, string? year)
    {
        return new DoubanMetadata(
            itemKind,
            entityId,
            subjectId,
            $"https://movie.douban.com/subject/{subjectId}/",
            title,
            null,
            NormalizeYear(year),
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow);
    }

    private async Task SaveAsync(DoubanMetadata metadata, CancellationToken cancellationToken)
    {
        using var connection = database.OpenConnection();
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO doubanMetadata (
                    itemKind, entityId, subjectId, subjectUrl, title, originalTitle, year,
                    rating, ratingCount, summary, posterUrl, fetchedAt)
                VALUES (
                    @ItemKind, @EntityId, @SubjectId, @SubjectUrl, @Title, @OriginalTitle, @Year,
                    @Rating, @RatingCount, @Summary, @PosterUrl, @FetchedAt)
                ON CONFLICT(itemKind, entityId) DO UPDATE SET
                    subjectId = excluded.subjectId,
                    subjectUrl = excluded.subjectUrl,
                    title = excluded.title,
                    originalTitle = excluded.originalTitle,
                    year = excluded.year,
                    rating = excluded.rating,
                    ratingCount = excluded.ratingCount,
                    summary = excluded.summary,
                    posterUrl = excluded.posterUrl,
                    fetchedAt = excluded.fetchedAt
                """,
                new
                {
                    metadata.ItemKind,
                    metadata.EntityId,
                    metadata.SubjectId,
                    metadata.SubjectUrl,
                    metadata.Title,
                    metadata.OriginalTitle,
                    metadata.Year,
                    metadata.Rating,
                    metadata.RatingCount,
                    metadata.Summary,
                    metadata.PosterUrl,
                    FetchedAt = metadata.FetchedAt.ToString("O")
                },
                cancellationToken: cancellationToken));
    }

    private static string NormalizeKind(string itemKind)
    {
        return string.Equals(itemKind, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
    }

    private static string? ExtractSubjectId(string value)
    {
        var trimmed = value.Trim();
        if (Regex.IsMatch(trimmed, "^\\d+$"))
        {
            return trimmed;
        }

        return FirstMatch(trimmed, "(?:movie\\.douban\\.com/subject|m\\.douban\\.com/movie/subject)/(\\d+)");
    }

    private static string? FirstMatch(string value, string pattern)
    {
        var match = Regex.Match(value, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? HtmlDecode(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : System.Net.WebUtility.HtmlDecode(value).Trim();
    }

    private static string? NormalizeYear(string? value)
    {
        var match = Regex.Match(value ?? string.Empty, "\\d{4}");
        return match.Success ? match.Value : null;
    }
}
