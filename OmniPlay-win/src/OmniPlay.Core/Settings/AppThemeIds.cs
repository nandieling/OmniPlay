namespace OmniPlay.Core.Settings;

public static class AppThemeIds
{
    public const string CloudWhite = "cloud-white";

    public const string DeepTeal = "deep-teal";

    public const string DuskViolet = "dusk-violet";

    public const string InkNight = "ink-night";

    public const string WarmEarth = "warm-earth";

    public static readonly string[] All = [CloudWhite, DeepTeal, DuskViolet, InkNight, WarmEarth];

    public static string Normalize(string? themeId)
    {
        return All.Any(id => string.Equals(id, themeId, StringComparison.OrdinalIgnoreCase))
            ? All.First(id => string.Equals(id, themeId, StringComparison.OrdinalIgnoreCase))
            : CloudWhite;
    }
}
