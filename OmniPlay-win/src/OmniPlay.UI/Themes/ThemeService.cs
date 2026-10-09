using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using OmniPlay.Core.Settings;

namespace OmniPlay.UI.Themes;

/// <summary>
/// 应用级主题服务：按主题 ID 生成并切换语义颜色资源，UI 线程调用后立即生效。
/// </summary>
public static class ThemeService
{
    public const string PageBackgroundKey = "PageBackgroundBrush";
    public const string SurfaceKey = "SurfaceBrush";
    public const string SurfaceAltKey = "SurfaceAltBrush";
    public const string SurfaceBorderKey = "SurfaceBorderBrush";
    public const string SurfaceBorderStrongKey = "SurfaceBorderStrongBrush";
    public const string TextPrimaryKey = "TextPrimaryBrush";
    public const string TextSecondaryKey = "TextSecondaryBrush";
    public const string TextMutedKey = "TextMutedBrush";
    public const string TextFaintKey = "TextFaintBrush";
    public const string AccentKey = "AccentBrush";
    public const string AccentHoverKey = "AccentHoverBrush";
    public const string AccentSoftKey = "AccentSoftBrush";
    public const string AccentForegroundKey = "AccentForegroundBrush";
    public const string DangerKey = "DangerBrush";
    public const string DangerHoverKey = "DangerHoverBrush";
    public const string DangerSoftKey = "DangerSoftBrush";
    public const string DangerBorderKey = "DangerBorderBrush";
    public const string DetailSurfaceKey = "DetailSurfaceBrush";
    public const string MediaOverlayKey = "MediaOverlayBrush";

    private sealed record ThemePalette(
        string Page,
        string Surface,
        string SurfaceAlt,
        string Border,
        string BorderStrong,
        string TextPrimary,
        string TextSecondary,
        string TextMuted,
        string TextFaint,
        string Accent,
        string AccentHover,
        string AccentSoft,
        string AccentForeground,
        string Danger,
        string DangerHover,
        string DangerSoft,
        string DangerBorder,
        string[]? DetailGradient)
    {
        public bool IsDark => DetailGradient == null;
    }

    private static readonly IReadOnlyDictionary<string, ThemePalette> Palettes =
        new Dictionary<string, ThemePalette>(StringComparer.OrdinalIgnoreCase)
        {
            [AppThemeIds.CloudWhite] = new(
                "#F8F9FA", "#FFFFFF", "#F3F4F6", "#E5E5E5", "#D1D1D1",
                "#1A1A1A", "#323130", "#605E5C", "#8A8886",
                "#0067C0", "#0054A6", "#E6F2FC", "#FFFFFF",
                "#D13438", "#FBE3E4", "#FDF3F4", "#FBCBCB",
                new[] { "#FFFFFF", "#F3F4F6", "#EAECEF" }),
            [AppThemeIds.DeepTeal] = new(
                "#EFF5F4", "#FFFFFF", "#E3EEEC", "#CCDFDB", "#B5D0CB",
                "#14231F", "#2B403B", "#526A64", "#7C918B",
                "#0F766E", "#0B5D57", "#DFF2EF", "#FFFFFF",
                "#C62F2F", "#F9E4E4", "#FBF1F1", "#EBC7C7",
                new[] { "#FFFFFF", "#EAF4F2", "#DEEDEA" }),
            [AppThemeIds.DuskViolet] = new(
                "#F6F4FA", "#FFFFFF", "#EEEAF6", "#DED7EA", "#C8BCE2",
                "#211B2D", "#3A3148", "#665C78", "#8B819C",
                "#6D28D9", "#5B21B6", "#ECE6FA", "#FFFFFF",
                "#C62F2F", "#F6E4EC", "#FAF1F5", "#E5C6D6",
                new[] { "#FFFFFF", "#F2EEF9", "#E9E3F4" }),
            [AppThemeIds.InkNight] = new(
                "#14161A", "#1E2126", "#262B33", "#333947", "#414A5A",
                "#ECEEF1", "#C9CFD8", "#98A0AD", "#6D7684",
                "#4C9AFF", "#6BAEFF", "#22334B", "#0B1220",
                "#FF9B9E", "#4A2C2E", "#3A2426", "#6E4043",
                null),
            [AppThemeIds.WarmEarth] = new(
                "#F9F5EF", "#FFFFFF", "#F3EADD", "#E6DAC8", "#D6C5A8",
                "#2A2118", "#463A2B", "#6F6250", "#94876F",
                "#B45309", "#93450B", "#FAEBDB", "#FFFFFF",
                "#C62F2F", "#F7E4E0", "#FBF2F0", "#ECCBC4",
                new[] { "#FFFFFF", "#F7F0E6", "#EFE4D3" })
        };

    private static ResourceDictionary? activeDictionary;

    public static string CurrentId { get; private set; } = AppThemeIds.CloudWhite;

    public static bool IsDarkActive => Palettes.TryGetValue(CurrentId, out var active) && active.IsDark;

    public static void Apply(string? themeId)
    {
        var id = AppThemeIds.Normalize(themeId);
        if (id == CurrentId && activeDictionary != null)
        {
            return;
        }

        CurrentId = id;
        var app = Application.Current;
        if (app == null)
        {
            return;
        }

        if (activeDictionary != null)
        {
            app.Resources.MergedDictionaries.Remove(activeDictionary);
        }

        activeDictionary = BuildDictionary(id);
        app.Resources.MergedDictionaries.Add(activeDictionary);
    }

    private static ResourceDictionary BuildDictionary(string themeId)
    {
        var palette = Palettes[themeId];
        var dictionary = new ResourceDictionary
        {
            [PageBackgroundKey] = Brush(palette.Page),
            [SurfaceKey] = Brush(palette.Surface),
            [SurfaceAltKey] = Brush(palette.SurfaceAlt),
            [SurfaceBorderKey] = Brush(palette.Border),
            [SurfaceBorderStrongKey] = Brush(palette.BorderStrong),
            [TextPrimaryKey] = Brush(palette.TextPrimary),
            [TextSecondaryKey] = Brush(palette.TextSecondary),
            [TextMutedKey] = Brush(palette.TextMuted),
            [TextFaintKey] = Brush(palette.TextFaint),
            [AccentKey] = Brush(palette.Accent),
            [AccentHoverKey] = Brush(palette.AccentHover),
            [AccentSoftKey] = Brush(palette.AccentSoft),
            [AccentForegroundKey] = Brush(palette.AccentForeground),
            [DangerKey] = Brush(palette.Danger),
            [DangerHoverKey] = Brush(palette.DangerHover),
            [DangerSoftKey] = Brush(palette.DangerSoft),
            [DangerBorderKey] = Brush(palette.DangerBorder),
            [MediaOverlayKey] = new SolidColorBrush(Color.Parse("#FFFFFF")),
            [DetailSurfaceKey] = palette.DetailGradient != null
                ? new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(new Point(0, 0), RelativeUnit.Relative),
                    EndPoint = new RelativePoint(new Point(1, 1), RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Parse(palette.DetailGradient[0]), 0),
                        new GradientStop(Parse(palette.DetailGradient[1]), 0.5),
                        new GradientStop(Parse(palette.DetailGradient[2]), 1)
                    }
                }
                : Brush(palette.SurfaceAlt)
        };
        return dictionary;
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Parse(hex));

    private static Color Parse(string hex) => Color.Parse(hex);
}
