namespace OmniPlay.Core.Models;

public sealed record CacheSettings(
    int HlsRetentionHours = 24,
    int HlsMaxGb = 30,
    string HlsCachePath = "",
    string ImageCleanupScope = "orphans-and-untracked",
    int WebDavRetentionHours = 72,
    int WebDavMaxGb = 20,
    string SubtitleCachePath = "",
    int SubtitleMaxGb = 20,
    string SubtitleCacheStrategy = SubtitleCacheStrategies.Disabled);

public static class SubtitleCacheStrategies
{
    public const string Disabled = "disabled";
    public const string Optimized = "optimized";
    public const string Full = "full";

    public static string Normalize(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            Full => Full,
            Optimized => Optimized,
            _ => Disabled
        };
    }
}
