using System.Text;
using System.Text.RegularExpressions;
using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Infrastructure.Library;

internal static class LibraryManualScrapeQueryPlanner
{
    public static IReadOnlyList<LibraryScrapeQueryAttempt> Build(
        string currentTitle,
        string? sourceProtocolType,
        string? baseUrl,
        string? relativePath,
        string? manualQuery = null)
    {
        var seedQuery = string.IsNullOrWhiteSpace(manualQuery)
            ? currentTitle
            : manualQuery.Trim();
        var seedMetadata = MediaNameParser.ExtractSearchMetadata(seedQuery);

        string? metadataPath = null;
        SearchMetadataSnapshot? pathMetadata = null;
        if (!string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(relativePath))
        {
            metadataPath = MediaSourcePathResolver.ResolveMetadataPath(
                sourceProtocolType,
                baseUrl,
                relativePath);
            var extracted = MediaNameParser.ExtractSearchMetadata(metadataPath);
            pathMetadata = new SearchMetadataSnapshot(
                extracted.ChineseTitle,
                extracted.ForeignTitle,
                extracted.FullCleanTitle,
                MediaNameParser.ExtractParentFolderChineseTitle(metadataPath));
        }

        var primaryQuery = seedMetadata.ChineseTitle
                           ?? seedMetadata.ForeignTitle
                           ?? seedMetadata.FullCleanTitle
                           ?? seedQuery;
        var chineseTitle = seedMetadata.ChineseTitle
                           ?? pathMetadata?.ChineseTitle
                           ?? pathMetadata?.ParentChineseTitle;
        var foreignTitle = seedMetadata.ForeignTitle
                           ?? pathMetadata?.ForeignTitle;
        var fullCleanTitle = seedMetadata.FullCleanTitle
                             ?? pathMetadata?.FullCleanTitle;
        var pureNameFallback = BuildPureNameFallback(fullCleanTitle);
        var pathPureNameFallback = BuildPureNameFallback(pathMetadata?.FullCleanTitle);
        var primaryLabel = string.IsNullOrWhiteSpace(manualQuery)
            ? "当前标题"
            : "手动输入";

        List<LibraryScrapeQueryAttempt> attempts = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        AddAttempt(primaryQuery, primaryLabel, BuildSecondaryQuery(primaryQuery, foreignTitle, chineseTitle));
        AddAttempt(pathMetadata?.ParentChineseTitle, "父目录中文名", BuildSecondaryQuery(pathMetadata?.ParentChineseTitle, foreignTitle, chineseTitle));
        AddAttempt(pathMetadata?.ChineseTitle, "源文件中文名", BuildSecondaryQuery(pathMetadata?.ChineseTitle, foreignTitle, chineseTitle));

        foreach (var foreignCandidate in BuildForeignQueryCandidates(seedMetadata.ForeignTitle))
        {
            AddAttempt(foreignCandidate, "外文名", BuildSecondaryQuery(foreignCandidate, chineseTitle, foreignTitle));
        }

        foreach (var foreignCandidate in BuildForeignQueryCandidates(pathMetadata?.ForeignTitle))
        {
            AddAttempt(foreignCandidate, "源文件外文名", BuildSecondaryQuery(foreignCandidate, chineseTitle, foreignTitle));
        }

        AddAttempt(fullCleanTitle, "完整清洗名", BuildSecondaryQuery(fullCleanTitle, foreignTitle, chineseTitle));
        AddAttempt(pathMetadata?.FullCleanTitle, "源文件清洗名", BuildSecondaryQuery(pathMetadata?.FullCleanTitle, foreignTitle, chineseTitle));
        AddAttempt(pureNameFallback, "纯名字降级", BuildSecondaryQuery(pureNameFallback, foreignTitle, chineseTitle));
        AddAttempt(pathPureNameFallback, "源文件纯名字降级", BuildSecondaryQuery(pathPureNameFallback, foreignTitle, chineseTitle));

        return attempts;

        void AddAttempt(string? query, string label, string? secondaryQuery)
        {
            var trimmed = query?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return;
            }

            var key = NormalizeTitle(trimmed);
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
            {
                return;
            }

            attempts.Add(new LibraryScrapeQueryAttempt(trimmed, label, secondaryQuery));
        }
    }

    private static IReadOnlyList<string> BuildForeignQueryCandidates(string? foreignTitle)
    {
        var trimmed = foreignTitle?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return [];
        }

        List<string> candidates = [trimmed];
        var normalized = Regex.Replace(trimmed, @"\bA\.?K\.?A\.?\b", "AKA", RegexOptions.IgnoreCase);
        normalized = normalized.Replace(" aka ", " AKA ", StringComparison.OrdinalIgnoreCase);
        if (normalized.Contains("AKA", StringComparison.Ordinal))
        {
            candidates.AddRange(normalized.Split("AKA", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return Deduplicate(candidates);
    }

    private static string? BuildPureNameFallback(string? fullCleanTitle)
    {
        if (string.IsNullOrWhiteSpace(fullCleanTitle))
        {
            return null;
        }

        var fallback = Regex.Replace(fullCleanTitle, @"[\s\d]+", string.Empty).Trim();
        return fallback.Length == 0 || string.Equals(fallback, fullCleanTitle, StringComparison.Ordinal)
            ? null
            : fallback;
    }

    private static string? BuildSecondaryQuery(string? query, params string?[] candidates)
    {
        var normalizedQuery = NormalizeTitle(query ?? string.Empty);
        foreach (var candidate in candidates)
        {
            var trimmed = candidate?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (!string.Equals(NormalizeTitle(trimmed), normalizedQuery, StringComparison.Ordinal))
            {
                return trimmed;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> Deduplicate(IEnumerable<string> source)
    {
        List<string> items = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (var value in source)
        {
            var trimmed = value.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var key = NormalizeTitle(trimmed);
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
            {
                continue;
            }

            items.Add(trimmed);
        }

        return items;
    }

    private static string NormalizeTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private sealed record SearchMetadataSnapshot(
        string? ChineseTitle,
        string? ForeignTitle,
        string? FullCleanTitle,
        string? ParentChineseTitle);
}

internal sealed record LibraryScrapeQueryAttempt(
    string Query,
    string Label,
    string? SecondaryQuery);
