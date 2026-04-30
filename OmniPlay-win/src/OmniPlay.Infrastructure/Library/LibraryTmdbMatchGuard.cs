using OmniPlay.Infrastructure.Tmdb;

namespace OmniPlay.Infrastructure.Library;

internal static class LibraryTmdbMatchGuard
{
    public static bool IsYearPlausibleMatch(
        TmdbMetadataMatch match,
        string? targetYear,
        string preferredMediaType,
        int? preferredSeason = null,
        int tolerance = 1)
    {
        var isTv = string.Equals(preferredMediaType, "tv", StringComparison.OrdinalIgnoreCase);
        if (isTv && preferredSeason is > 1)
        {
            return true;
        }

        if (!TryParseYear(targetYear, out var target) ||
            !TryParseYear(match.ReleaseDate ?? match.FirstAirDate, out var candidate))
        {
            return true;
        }

        var effectiveTolerance = isTv ? Math.Max(tolerance, 10) : tolerance;
        return Math.Abs(candidate - target) <= effectiveTolerance;
    }

    private static bool TryParseYear(string? value, out int year)
    {
        year = 0;
        return !string.IsNullOrWhiteSpace(value) &&
               value!.Length >= 4 &&
               int.TryParse(value[..4], out year);
    }
}
