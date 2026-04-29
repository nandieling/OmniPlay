using System.Text.RegularExpressions;
using OmniPlay.Core.Models.Playback;

namespace OmniPlay.Infrastructure.Library;

public static partial class MediaNameParser
{
    public static string CleanedTitleSource(string rawPath)
    {
        var normalized = rawPath.Replace('\\', '/');

        if (normalized.Contains("BDMV", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".m2t", StringComparison.OrdinalIgnoreCase))
        {
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var bdmvIndex = Array.FindIndex(parts, static x => x.Equals("BDMV", StringComparison.OrdinalIgnoreCase));
            if (bdmvIndex > 0)
            {
                var candidate = parts[bdmvIndex - 1];
                if (IsGenericDiscOrVolumeFolder(candidate) && bdmvIndex > 1)
                {
                    candidate = parts[bdmvIndex - 2];
                    if (bdmvIndex > 2)
                    {
                        candidate = MergeSplitTitleSegments(parts[bdmvIndex - 3], candidate);
                    }
                }

                return NormalizeDiscTitleCandidate(candidate);
            }
        }

        var fileStem = Path.GetFileNameWithoutExtension(normalized);
        if (string.IsNullOrWhiteSpace(fileStem))
        {
            return rawPath;
        }

        var parentName = Path.GetFileName(Path.GetDirectoryName(normalized) ?? string.Empty);
        if (!ContainsHan(fileStem) && ContainsHan(parentName))
        {
            return $"{parentName} {fileStem}".Trim();
        }

        return fileStem;
    }

    public static SearchMetadata ExtractSearchMetadata(string rawPath)
    {
        var originalText = CleanedTitleSource(rawPath);
        var textToParse = TruncateBeforeReleaseMetadata(originalText);

        var extractedYear = ChooseReleaseYear(originalText) ?? ChooseReleaseYear(textToParse);
        if (!string.IsNullOrWhiteSpace(extractedYear))
        {
            textToParse = PreferTitleTextBeforeReleaseYear(textToParse, extractedYear);
        }

        textToParse = RemoveDecorativeBracketSegments(textToParse);
        textToParse = RemoveReleaseTitleNoise(textToParse);
        var titleScopedText = textToParse;
        textToParse = BracketLikeCharactersRegex().Replace(textToParse, " ");

        foreach (var pattern in CleanupPatterns)
        {
            textToParse = Regex.Replace(textToParse, pattern, " ", RegexOptions.IgnoreCase);
        }

        textToParse = Regex.Replace(textToParse, @"[._]+", " ");
        textToParse = Regex.Replace(textToParse, @"\[[^\]]*\]|\([^\)]*\)", " ");
        textToParse = BracketLikeCharactersRegex().Replace(textToParse, " ");
        textToParse = Regex.Replace(textToParse, @"\s+", " ").Trim();

        var tokens = Tokenize(textToParse);
        var originalTokens = Tokenize(Regex.Replace(BracketLikeCharactersRegex().Replace(titleScopedText, " "), @"\s+", " ").Trim());

        return new SearchMetadata(
            ExtractChineseTitle(tokens) ?? ExtractChineseTitle(originalTokens),
            ExtractForeignTitle(tokens),
            string.IsNullOrWhiteSpace(textToParse) ? null : textToParse,
            extractedYear);
    }

    public static string? ExtractParentFolderChineseTitle(string rawPath)
    {
        var parent = Path.GetDirectoryName(rawPath)?.Trim();
        if (string.IsNullOrWhiteSpace(parent))
        {
            return null;
        }

        var parentName = Path.GetFileName(parent)?.Trim();
        if (string.IsNullOrWhiteSpace(parentName) || !ContainsHan(parentName))
        {
            return null;
        }

        return ExtractBracketedChineseTitle(parentName) ?? ExtractSearchMetadata(parentName).ChineseTitle;
    }

    public static EpisodeInfo ParseEpisodeInfo(string fileName, int fallbackIndex)
    {
        var episode = fallbackIndex + 1;
        var season = 1;

        var seasonEpisodeMatch = Regex.Match(fileName, @"[sS](\d{1,2})[eE][pP]?(\d{1,3})");
        if (seasonEpisodeMatch.Success)
        {
            season = ParseOrDefault(seasonEpisodeMatch.Groups[1].Value, 1);
            episode = ParseOrDefault(seasonEpisodeMatch.Groups[2].Value, episode);
            var partNumber = ParsePartNumberAfterMatch(fileName, seasonEpisodeMatch);
            var year = ExtractEpisodeYearAfterMatch(fileName, seasonEpisodeMatch);
            var subtitle = ExtractEpisodeSubtitleAfterMatch(fileName, seasonEpisodeMatch);
            return new EpisodeInfo(season, episode, BuildEpisodeDisplayName(episode, partNumber), true, partNumber, subtitle, year);
        }

        var episodeOnlyMatch = Regex.Match(fileName, @"[eE][pP]?(\d{1,3})");
        if (episodeOnlyMatch.Success)
        {
            episode = ParseOrDefault(episodeOnlyMatch.Groups[1].Value, episode);
            var partNumber = ParsePartNumberAfterMatch(fileName, episodeOnlyMatch);
            var year = ExtractEpisodeYearAfterMatch(fileName, episodeOnlyMatch);
            var subtitle = ExtractEpisodeSubtitleAfterMatch(fileName, episodeOnlyMatch);
            return new EpisodeInfo(season, episode, BuildEpisodeDisplayName(episode, partNumber), true, partNumber, subtitle, year);
        }

        return new EpisodeInfo(season, episode, fileName, false);
    }

    public static int? ParsePreferredSeason(string rawPath)
    {
        var match = Regex.Match(rawPath, @"[sS](\d{1,2})(?!\d)");
        if (match.Success)
        {
            return ParseOrDefault(match.Groups[1].Value, 0);
        }

        match = Regex.Match(rawPath, @"(?i)season[\s._-]*(\d{1,2})");
        if (match.Success)
        {
            return ParseOrDefault(match.Groups[1].Value, 0);
        }

        return null;
    }

    public static int? ResolvePreferredSeason(string rawPath, string fileName, int fallbackIndex = 0)
    {
        var parsed = ParseEpisodeInfo(fileName, fallbackIndex);
        return parsed.IsTvShow && parsed.Season > 0 ? parsed.Season : ParsePreferredSeason(rawPath);
    }

    public static bool IsLikelyTvEpisodePath(string rawPath)
    {
        var lower = rawPath.ToLowerInvariant();
        return Regex.IsMatch(lower, @"[s]\d{1,2}[e][p]?\d{1,3}")
               || Regex.IsMatch(lower, @"\bep?\d{1,3}\b")
               || Regex.IsMatch(lower, @"\bs\d{1,2}\b")
               || Regex.IsMatch(lower, @"\bseason[\s._-]*\d{1,2}\b");
    }

    public static bool IsLikelyMoviePath(string rawPath)
    {
        var lower = rawPath.Replace('\\', '/').ToLowerInvariant();
        if (lower.Contains("/bdmv/") || lower.EndsWith(".iso") || lower.EndsWith(".m2ts") || lower.EndsWith(".m2t"))
        {
            return true;
        }

        var fileName = Path.GetFileName(lower);
        return !string.IsNullOrWhiteSpace(fileName) &&
               Regex.IsMatch(fileName, @"(disc|disk|dvd|cd|vol|volume)[-_ ]?\d{0,2}");
    }

    public static (int Season, int Episode, int Part, string FileName) EpisodeSortKey(string fileName, int fallbackIndex)
    {
        var parsed = ParseEpisodeInfo(fileName, fallbackIndex);
        var seasonOrder = parsed.Season == 0 ? int.MaxValue : parsed.Season;
        return (seasonOrder, parsed.Episode, parsed.PartNumber ?? 0, fileName);
    }

    private static readonly string[] CleanupPatterns =
    [
        @"\b(1080p|2160p|4k|720p|480p|blu[- ]?ray|bluray|bdrip|web[- ]?dl|webrip|remux|x264|x265|h\.?264|h\.?265|hevc|aac|dts|hdr|dv)\b",
        @"\b[sS]\d{1,2}[eE][pP]?\d{1,3}\b",
        @"\b[sS]\d{1,2}\b",
        @"\b[eE][pP]?\d{1,3}\b",
        @"\bepisode[\s._-]*\d{1,3}\b",
        @"\b(?:part|pt)[\s._-]*\d{1,2}\b",
        @"\bseason\s*\d{1,2}\b",
        @"\bcomplete\b",
        @"\b(cctv4k|cctv)\b",
        @"\b\d{1,2}bit\b",
        @"\b(aac|ac3|eac3|ddp|dts|truehd|flac|mp3)\d*(\.\d+)?\b",
        @"\b(bonus|extras?|featurette|behind[- ]?the[- ]?scenes|trailer|sample)\b",
        @"\b(disc|disk|cd|dvd)\b",
        @"\b(disc|disk|cd|dvd)\s*[-_ ]?\d{1,2}\b",
        @"\b(bdrom|bdmv)\b",
        @"\b(vol|volume)\s*[-_ ]?\d{1,2}\b",
        @"\b(special\s*features?|featurettes?)\b",
        @"\b\d{1,3}(st|nd|rd|th)\s+anniversary(\s+edition)?\b",
        @"\b(anniversary|edition)\b",
        @"(剧场版|纪念版|花絮|特典|番外|幕后花絮)"
    ];

    private static string? ExtractChineseTitle(IReadOnlyList<string> tokens)
    {
        List<string>? current = null;
        List<List<string>> groups = [];

        foreach (var token in tokens)
        {
            var hasHan = ContainsHan(token);
            var isNumericSuffix = current is { Count: > 0 } && Regex.IsMatch(token, @"^\d+$");
            if (hasHan)
            {
                current ??= [];
                current.Add(token);
            }
            else if (isNumericSuffix)
            {
                current!.Add(token);
            }
            else if (current is { Count: > 0 })
            {
                groups.Add(current);
                current = null;
            }
        }

        if (current is { Count: > 0 })
        {
            groups.Add(current);
        }

        return groups
            .OrderByDescending(static x => string.Concat(x).Length)
            .Select(static x => string.Concat(x).Trim())
            .FirstOrDefault(static x => x.Length > 0);
    }

    private static string? ExtractForeignTitle(IReadOnlyList<string> tokens)
    {
        var noiseTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "tx", "mweb", "adweb", "web", "dl", "bluray", "bdrip", "webrip",
            "proper", "repack", "remastered", "extended", "unrated",
            "vol", "volume", "disc", "disk", "cd", "part", "bonus", "extra", "extras",
            "featurette", "trailer", "sample", "cmctv", "bdrom", "bdmv", "special", "features",
            "anniversary", "edition", "complete", "episode", "cctv", "cctv4k"
        };

        var filtered = tokens.Where(token =>
        {
            var lower = token.ToLowerInvariant();
            if (noiseTokens.Contains(lower)) return false;
            if (ContainsHan(token)) return false;
            if (!Regex.IsMatch(token, @"^(?=.*[\p{L}0-9])[\p{L}0-9'&:+-]+$")) return false;
            if (Regex.IsMatch(lower, @"^\d+$")) return false;
            if (Regex.IsMatch(lower, @"^(disc|disk|cd|dvd)[-_ ]?\d{0,2}$")) return false;
            if (Regex.IsMatch(lower, @"^(vol|volume)[-_ ]?\d{0,2}$")) return false;
            if (Regex.IsMatch(lower, @"^[se]\d{1,3}$")) return false;
            if (Regex.IsMatch(lower, @"^(part|pt)\d{1,2}$")) return false;
            if (Regex.IsMatch(lower, @"^(x|h)?26[45]$")) return false;
            return true;
        });

        var merged = string.Join(' ', filtered).Trim();
        return merged.Length == 0 ? null : merged;
    }

    private static string TruncateBeforeReleaseMetadata(string input)
    {
        var normalized = Regex.Replace(input, @"[._]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        normalized = RemoveDecorativeBracketSegments(normalized);
        var tokens = Tokenize(normalized);
        if (tokens.Count == 0)
        {
            return input;
        }

        var endIndex = tokens.Count;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (IsReleaseMetadataToken(tokens[i]))
            {
                endIndex = i;
                break;
            }
        }

        return endIndex <= 0 ? normalized : string.Join(' ', tokens.Take(endIndex));
    }

    private static string NormalizeDiscTitleCandidate(string candidate)
    {
        return RemoveReleaseTitleNoise(candidate);
    }

    private static string RemoveDecorativeBracketSegments(string input)
    {
        return Regex.Replace(
            input,
            @"[\[\(\{（【]\s*[^]\)\}）】]*(?:剧场版|纪念版|花絮|特典|番外|幕后花絮|anniversary|edition|featurette|bonus|extra|extras|trailer)[^]\)\}）】]*[\]\)\}）】]",
            " ",
            RegexOptions.IgnoreCase);
    }

    private static string RemoveReleaseTitleNoise(string input)
    {
        var normalized = Regex.Replace(input, @"[._]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();

        normalized = Regex.Replace(
            normalized,
            @"(?i)^(?:disc|disk|cd|dvd|vol|volume)\s*\d{0,2}\s*[-–—:]+\s*",
            string.Empty);
        normalized = Regex.Replace(normalized, @"^[\s._\-–—:]+", string.Empty);

        normalized = Regex.Replace(normalized, @"(?i)\b(?:cctv4k|cctv)\b", " ");
        normalized = Regex.Replace(normalized, @"(?i)\bepisode\s*\d{1,3}\b", " ");
        normalized = Regex.Replace(normalized, @"(?:剧场版|纪念版|花絮|特典|番外|幕后花絮)", " ");

        var withoutMovieSuffix = Regex.Replace(
            normalized,
            @"(?i)(?:\s*[-–—:]\s*|\s+)the\s+movie\s*$",
            string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(withoutMovieSuffix))
        {
            normalized = withoutMovieSuffix;
        }

        foreach (var pattern in CleanupPatterns)
        {
            normalized = Regex.Replace(normalized, pattern, " ", RegexOptions.IgnoreCase);
        }

        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static string PreferTitleTextBeforeReleaseYear(string input, string year)
    {
        var matches = Regex.Matches(input, $@"(?<!\d){Regex.Escape(year)}(?!\d)");
        foreach (Match match in matches)
        {
            var prefix = input[..match.Index];
            if (!string.IsNullOrWhiteSpace(NormalizeTitleSegment(prefix)))
            {
                return prefix;
            }
        }

        return Regex.Replace(
            input,
            $@"[ \.\-_\(\)\[\]\{{\}}]*{Regex.Escape(year)}[ \.\-_\(\)\[\]\{{\}}]*",
            " ",
            RegexOptions.IgnoreCase);
    }

    private static string NormalizeTitleSegment(string input)
    {
        var normalized = BracketLikeCharactersRegex().Replace(input, " ");
        normalized = Regex.Replace(normalized, @"[._]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized.Trim(' ', '.', '-', '_', '/', '\\', '(', ')', '[', ']', '{', '}');
    }

    private static bool IsReleaseMetadataToken(string token)
    {
        var lower = token.ToLowerInvariant();
        return Regex.IsMatch(lower, @"^[se]\d{1,3}$")
               || Regex.IsMatch(lower, @"^s\d{1,2}e[p]?\d{1,3}$")
               || Regex.IsMatch(lower, @"^ep?\d{1,3}$")
               || Regex.IsMatch(lower, @"^\d{3,4}p$")
               || Regex.IsMatch(lower, @"^(4k|uhd|hdr|dv|atmos|ddp\d*(\.\d+)?|aac\d*(\.\d+)?|ac3|eac3|dts|truehd|flac|mp3)$")
               || Regex.IsMatch(lower, @"^(x|h)?26[45]$")
               || Regex.IsMatch(lower, @"^(web|webdl|webrip|bluray|bdrip|remux)$")
               || Regex.IsMatch(lower, @"^(amzn|nf|netflix|dsnp|disney|hmax|max|atvp|appletv|hulu|cr)$")
               || Regex.IsMatch(lower, @"^(flux|ntb|cakes|tgx|successfulcrab)$")
               || Regex.IsMatch(lower, @"^(bonus|extra|extras|featurette|trailer|sample)$")
               || Regex.IsMatch(lower, @"^(part|pt)\d{1,2}$")
               || Regex.IsMatch(lower, @"^(disc|disk|cd|dvd)[-_ ]?\d{0,2}$")
               || Regex.IsMatch(lower, @"^(bdrom|bdmv)$")
               || Regex.IsMatch(lower, @"^(vol|volume)[-_ ]?\d{0,2}$");
    }

    private static string? ChooseReleaseYear(string text)
    {
        var candidates = YearRegex().Matches(text)
            .Select(static x => x.Value)
            .Distinct()
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count >= 2)
        {
            var compactPrefix = Regex.Replace(text[..Math.Min(text.Length, 12)], @"[.\-_/\s]+", string.Empty);
            if (compactPrefix.StartsWith(candidates[0], StringComparison.Ordinal))
            {
                return candidates[1];
            }
        }

        return candidates[0];
    }

    private static string MergeSplitTitleSegments(string previous, string current)
    {
        var prev = previous.Trim();
        var curr = current.Trim();
        if (prev.Length == 0) return curr;
        if (curr.Length == 0) return prev;
        if (IsGenericDiscOrVolumeFolder(prev) || IsReleaseMetadataToken(prev)) return curr;
        if (!Regex.IsMatch(prev, @"[A-Za-z\p{IsCJKUnifiedIdeographs}]")) return curr;
        if (!Regex.IsMatch(curr, @"[A-Za-z\p{IsCJKUnifiedIdeographs}]")) return curr;
        return $"{prev} {curr}";
    }

    private static string? ExtractBracketedChineseTitle(string text)
    {
        var match = Regex.Match(text, @"(?:\[|\()\s*([\p{IsCJKUnifiedIdeographs}\d]{2,})\s*(?:\]|\))");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static bool IsGenericDiscOrVolumeFolder(string input)
    {
        var token = Regex.Replace(input, @"[._\-\s]+", string.Empty).ToLowerInvariant();
        return Regex.IsMatch(token, @"^(vol(ume)?\d{0,2}|disc\d{0,2}|disk\d{0,2}|dvd\d{0,2}|cd\d{0,2}|bdrom|bdmv)$");
    }

    private static List<string> Tokenize(string input)
    {
        return input.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static int ParseOrDefault(string value, int fallback)
    {
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static int? ParsePartNumberAfterMatch(string fileName, Match episodeMatch)
    {
        var tailStart = episodeMatch.Index + episodeMatch.Length;
        if (tailStart >= fileName.Length)
        {
            return null;
        }

        var tail = fileName[tailStart..];
        var match = Regex.Match(
            tail,
            @"(?i)(?:^|[\s._\-\(\[])(?:part|pt)[\s._-]*(\d{1,2})(?=$|[\s._\-\)\]])");
        if (!match.Success)
        {
            return null;
        }

        var part = ParseOrDefault(match.Groups[1].Value, 0);
        return part > 0 ? part : null;
    }

    private static string? ExtractEpisodeSubtitleAfterMatch(string fileName, Match episodeMatch)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var tailStart = episodeMatch.Index + episodeMatch.Length;
        if (tailStart >= stem.Length)
        {
            return null;
        }

        var tail = stem[tailStart..];
        tail = Regex.Replace(
            tail,
            @"(?i)^(?:[\s._\-\(\[])*(?:part|pt)[\s._-]*\d{1,2}(?=$|[\s._\-\)\]])",
            string.Empty);
        tail = Regex.Replace(tail, @"^[\s._\-\)\]\(\[]+", string.Empty);
        tail = Regex.Replace(tail, @"[\s._\-\(\[]+$", string.Empty);
        if (string.IsNullOrWhiteSpace(tail))
        {
            return null;
        }

        var normalized = Regex.Replace(tail, @"[._]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        var tokens = Tokenize(normalized);
        if (tokens.Count == 0 || tokens.All(static token => IsReleaseMetadataToken(token) || IsEpisodeYearToken(token)))
        {
            return null;
        }

        var keptTokens = tokens
            .TakeWhile(static token => !IsReleaseMetadataToken(token) && !IsEpisodeYearToken(token))
            .ToList();
        if (keptTokens.Count == 0)
        {
            keptTokens = tokens
                .Where(static token => !IsReleaseMetadataToken(token) && !IsEpisodeYearToken(token))
                .ToList();
        }

        var subtitle = string.Join(' ', keptTokens).Trim();
        return subtitle.Length == 0 ? null : subtitle;
    }

    private static string? ExtractEpisodeYearAfterMatch(string fileName, Match episodeMatch)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var tailStart = episodeMatch.Index + episodeMatch.Length;
        if (tailStart >= stem.Length)
        {
            return null;
        }

        var tail = stem[tailStart..];
        var match = YearRegex().Match(tail);
        return match.Success ? match.Value : null;
    }

    private static string BuildEpisodeDisplayName(int episode, int? partNumber)
    {
        return partNumber.HasValue
            ? $"Episode {episode} Part {partNumber.Value}"
            : $"Episode {episode}";
    }

    private static bool ContainsHan(string value)
    {
        return HanRegex().IsMatch(value);
    }

    private static bool IsEpisodeYearToken(string token)
    {
        return Regex.IsMatch(token, @"^(19\d{2}|20\d{2})$");
    }

    [GeneratedRegex(@"\p{IsCJKUnifiedIdeographs}")]
    private static partial Regex HanRegex();

    [GeneratedRegex(@"[\[\]\(\)\{\}（）【】]")]
    private static partial Regex BracketLikeCharactersRegex();

    [GeneratedRegex(@"(?<!\d)(19\d{2}|20\d{2})(?!\d)")]
    private static partial Regex YearRegex();
}
