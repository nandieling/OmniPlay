using System.Text;
using System.Text.RegularExpressions;
using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Infrastructure.Library;

public static class LibraryLookupTitleBuilder
{
    public static List<string> Build(
        string currentTitle,
        string? sourceProtocolType,
        string? baseUrl,
        string? relativePath,
        string? manualQuery = null)
    {
        List<string> titles = [];

        AddIfPresent(titles, manualQuery);

        if (!string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(relativePath))
        {
            var metadataPath = MediaSourcePathResolver.ResolveMetadataPath(
                sourceProtocolType,
                baseUrl,
                relativePath);
            var metadata = MediaNameParser.ExtractSearchMetadata(metadataPath);
            var parentChineseTitle = MediaNameParser.ExtractParentFolderChineseTitle(metadataPath);

            AddIfPresent(titles, metadata.ChineseTitle);
            AddIfPresent(titles, parentChineseTitle);
            AddIfPresent(titles, currentTitle);

            foreach (var foreignQuery in BuildForeignQueryCandidates(metadata.ForeignTitle))
            {
                AddIfPresent(titles, foreignQuery);
            }

            AddIfPresent(titles, metadata.FullCleanTitle);
            AddIfPresent(titles, BuildPureNameFallback(metadata.FullCleanTitle));
        }
        else
        {
            AddIfPresent(titles, currentTitle);
        }

        return DeduplicateTitles(titles);
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
        normalized = normalized
            .Replace("(AKA)", "AKA", StringComparison.OrdinalIgnoreCase)
            .Replace("（AKA）", "AKA", StringComparison.OrdinalIgnoreCase)
            .Replace(" aka ", " AKA ", StringComparison.OrdinalIgnoreCase);
        if (normalized.Contains("AKA", StringComparison.Ordinal))
        {
            candidates.AddRange(normalized
                .Split("AKA", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return DeduplicateTitles(candidates);
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

    private static List<string> DeduplicateTitles(IReadOnlyList<string> source)
    {
        List<string> titles = [];
        HashSet<string> seen = [];

        foreach (var item in source)
        {
            var trimmed = item?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var key = NormalizeTitle(trimmed);
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
            {
                continue;
            }

            titles.Add(trimmed);
        }

        return titles;
    }

    private static void AddIfPresent(List<string> titles, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            titles.Add(value.Trim());
        }
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
}
