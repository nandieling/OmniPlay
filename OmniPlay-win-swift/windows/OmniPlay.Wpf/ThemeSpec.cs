using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace OmniPlay.WpfSpike;

/// 核心算好的一份主题配方（theme.get 的响应）。界面只翻译不计算：
/// 颜色混合、光斑色相、玻璃描边全在 Swift 那侧，WPF 里不出现第二套调色逻辑。
public sealed class ThemeSpec
{
    public sealed record Glow(Color Color, double X, double Y, double Spread);

    public sealed record GlassSpec(
        Color Tint, Color SheenTop, Color SheenBottom, double SheenEndX, double SheenEndY,
        Color Border, Color Edge, Color Shadow, double CornerRadius, IReadOnlyList<Glow> Glows);

    public string Id { get; private init; } = string.Empty;
    public string DisplayName { get; private init; } = string.Empty;
    public bool IsGlass { get; private init; }
    public string Style { get; private init; } = "classic";
    public Color Accent { get; private init; }
    public Color Background { get; private init; }
    public Color Surface { get; private init; }
    public Color TextPrimary { get; private init; }
    public Color TextSecondary { get; private init; }
    public IReadOnlyList<Color> Backdrop { get; private init; } = Array.Empty<Color>();
    public Point BackdropFrom { get; private init; }
    public Point BackdropTo { get; private init; } = new(1, 1);
    public GlassSpec? Glass { get; private init; }

    public bool OnTopIsLightest => Backdrop.Count > 0 && Luminance(Backdrop[0]) >= Luminance(Backdrop[^1]);

    public static ThemeSpec FromJson(JsonNode node)
    {
        var backdrop = new List<Color>();
        foreach (var entry in node["backdrop"]?.AsArray() ?? new JsonArray())
            backdrop.Add(ReadColor(entry));
        var (fromX, fromY) = ReadPoint(node["backdropFrom"]);
        var (toX, toY) = ReadPoint(node["backdropTo"]);

        return new ThemeSpec
        {
            Id = node["id"]?.GetValue<string>() ?? string.Empty,
            DisplayName = node["displayName"]?.GetValue<string>() ?? string.Empty,
            Style = node["style"]?.GetValue<string>() ?? "classic",
            IsGlass = (node["style"]?.GetValue<string>() ?? "") == "liquidGlass",
            Accent = ReadColor(node["accent"]),
            Background = ReadColor(node["background"]),
            Surface = ReadColor(node["surface"]),
            TextPrimary = ReadColor(node["textPrimary"]),
            TextSecondary = ReadColor(node["textSecondary"]),
            Backdrop = backdrop,
            BackdropFrom = new Point(fromX, fromY),
            BackdropTo = new Point(toX, toY),
            Glass = ReadGlass(node["glass"]),
        };
    }

    public static Color ParseArgb(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex!.Length != 9 || hex[0] != '#') return Colors.Magenta;
        return Color.FromArgb(
            Convert.ToByte(hex.Substring(1, 2), 16),
            Convert.ToByte(hex.Substring(3, 2), 16),
            Convert.ToByte(hex.Substring(5, 2), 16),
            Convert.ToByte(hex.Substring(7, 2), 16));
    }

    public static Color Fade(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);

    public static double Luminance(Color color) =>
        0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;

    public Color TextOn(Color background) => Luminance(background) > 150 ? Colors.Black : Colors.White;

    /// 两色线性插值。海报占位色要「跟着主题走又每张不同」，靠的就是这个混合。
    public static Color Mix(Color from, Color to, double weight)
    {
        var t = Math.Clamp(weight, 0, 1);
        return Color.FromArgb(
            (byte)Math.Round(from.A + (to.A - from.A) * t),
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }

    /// 色相环取色（0–1）。核心给每张片子算了一个稳定的 hue，界面只负责把它画出来。
    public static Color FromHsv(double hue, double saturation, double brightness)
    {
        var h = (hue - Math.Floor(hue)) * 6;
        var sector = (int)Math.Floor(h);
        var f = h - sector;
        var value = (byte)Math.Round(Math.Clamp(brightness, 0, 1) * 255);
        var dim = (byte)Math.Round(brightness * (1 - Math.Clamp(saturation, 0, 1)) * 255);
        var edge = (byte)Math.Round(brightness * (1 - Math.Clamp(saturation, 0, 1) * (sector % 2 == 0 ? f : 1 - f)) * 255);
        return sector switch
        {
            0 => Color.FromRgb(value, edge, dim),
            1 => Color.FromRgb(edge, value, dim),
            2 => Color.FromRgb(dim, value, edge),
            3 => Color.FromRgb(dim, edge, value),
            4 => Color.FromRgb(edge, dim, value),
            _ => Color.FromRgb(value, dim, edge),
        };
    }

    private static GlassSpec? ReadGlass(JsonNode? node)
    {
        if (node is not JsonObject glass) return null;
        var glows = new List<Glow>();
        foreach (var entry in glass["glows"]?.AsArray() ?? new JsonArray())
        {
            var (x, y) = ReadPoint(entry?["center"]);
            glows.Add(new Glow(ReadColor(entry?["color"]), x, y, entry?["spread"]?.GetValue<double>() ?? 0.5));
        }
        var (endX, endY) = ReadPoint(glass["sheenEnd"], 0.5, 0.34);
        return new GlassSpec(
            ReadColor(glass["tint"]), ReadColor(glass["sheenTop"]), ReadColor(glass["sheenBottom"]),
            endX, endY, ReadColor(glass["border"]), ReadColor(glass["edge"]), ReadColor(glass["shadow"]),
            glass["cornerRadius"]?.GetValue<double>() ?? 10, glows);
    }

    private static (double, double) ReadPoint(JsonNode? node, double fallbackX = 0, double fallbackY = 0)
    {
        var array = node?.AsArray();
        if (array is null || array.Count < 2) return (fallbackX, fallbackY);
        return (array[0]?.GetValue<double>() ?? fallbackX, array[1]?.GetValue<double>() ?? fallbackY);
    }

    private static Color ReadColor(JsonNode? node) => node is null ? Colors.Magenta : ParseArgb(node["argb"]?.GetValue<string>());
}
