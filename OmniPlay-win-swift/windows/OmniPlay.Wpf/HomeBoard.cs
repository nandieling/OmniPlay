using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OmniPlay.WpfSpike;

/// 首页海报墙。数据全部来自核心的 library.sections——界面这边不筛也不排，
/// 分组顺序、片名排序、已看未看都是核心算好的，跟 mac 版共用同一套规矩。
public sealed class HomeBoard : ContentControl
{
    /// 一条片子。字段跟 library.sections 给的 entry 对齐，界面不再自己算。
    public sealed record Card(
        long FileId, string Title, int? Year, int Percent, bool Watched, double Hue,
        string Path, double Position, double Duration,
        IReadOnlyList<string> Genres, IReadOnlyList<string> Directors, IReadOnlyList<string> Casts);
    public sealed record Shelf(string Id, string Title, IReadOnlyList<Card> Cards);

    private const double PosterWidth = 132;
    private const double PosterHeight = 186;

    private readonly CoreBridge _core;
    private readonly StackPanel _root = new();
    private readonly ScrollViewer _scroll;
    private readonly List<Painted> _painted = new();
    private readonly List<(Border Chip, TextBlock Text)> _chips = new();
    private ThemeSpec _spec = null!;
    private long? _selected;
    private string _view = "home";

    /// 一行库状态（片库多少部、已看多少），给状态条用。
    public event Action<string>? StatusChanged;

    /// 选中的片子变了；参数是 null 表示没选中任何片。
    public event Action<Card?>? SelectionChanged;

    public Card? Selected => _selected is null ? null : Find(_selected.Value);

    private sealed class Painted
    {
        public required Card Card { get; init; }
        public required Border Poster { get; init; }
        public required Border Frame { get; init; }
        public required TextBlock Title { get; init; }
        public required TextBlock Meta { get; init; }
        public required Border Badge { get; init; }
        public required TextBlock BadgeText { get; init; }
        public required Border ProgressBar { get; init; }
    }

    public HomeBoard(CoreBridge core)
    {
        _core = core;
        _scroll = new ScrollViewer
        {
            Content = _root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(18, 14, 18, 14),
        };
        Content = _scroll;
    }

    /// 换一页看。home = 继续播放 + 所有影视，watched / unwatched = 单个架子，
    /// director / cast = 分类页（分桶由核心的 library.categories 算）。
    public void Show(string view)
    {
        _view = view;
        Refresh();
    }

    /// 重新读库、重画。核心每次都会顺带把空库铺上演示数据，这里不用额外判断。
    public void Refresh()
    {
        _root.Children.Clear();
        _painted.Clear();
        _chips.Clear();

        if (_view is "director" or "cast") BuildCategoryPage();
        else BuildShelves();

        if (_painted.Count == 0 && _chips.Count == 0)
            _root.Children.Add(new TextBlock { Text = "这里还没有内容", Margin = new Thickness(6, 10, 0, 0) });

        Apply(_spec);
        var stats = _core.Invoke("library.stats");
        StatusChanged?.Invoke(
            $"片库 {Int(stats, "movies")} 部 · 文件 {Int(stats, "files")} · 已看 {Int(stats, "watched")} · 可续播 {Int(stats, "resumable")}");
    }

    private void BuildShelves()
    {
        var wanted = _view switch
        {
            "watched" => new[] { "watched" },
            "unwatched" => new[] { "unwatched" },
            "continue" => new[] { "continue" },
            _ => new[] { "continue", "all" },
        };
        var result = _core.Invoke("library.sections");
        foreach (var node in result["sections"]?.AsArray() ?? new JsonArray())
        {
            var id = node?["id"]?.GetValue<string>() ?? string.Empty;
            if (!wanted.Contains(id)) continue;
            var shelf = new Shelf(id, node?["title"]?.GetValue<string>() ?? string.Empty,
                                  ReadCards(node?["entries"]).ToList());
            if (shelf.Cards.Count == 0) continue;
            _root.Children.Add(BuildHeader(shelf));
            _root.Children.Add(BuildRow(shelf));
        }
    }

    /// 分类页：导演 / 主演分桶由核心排好，这里只把「谁 + 几部」画成一排胶囊
    private void BuildCategoryPage()
    {
        var mode = _view;
        var title = mode == "director" ? "导演" : "主演";
        _root.Children.Add(BuildHeader(new Shelf(mode, title, Array.Empty<Card>())));
        var result = _core.Invoke("library.categories", new JsonObject { ["mode"] = mode });
        var row = new WrapPanel { Margin = new Thickness(-6, 0, -6, 0) };
        foreach (var group in result["groups"]?.AsArray() ?? new JsonArray())
        {
            var name = group?["name"]?.GetValue<string>() ?? "未标注";
            var count = group?["count"]?.GetValue<double>() is double value ? (int)value : 0;
            var text = new TextBlock { Text = name + " · " + count + " 部", FontSize = 13 };
            var chip = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(11, 6, 11, 6),
                Margin = new Thickness(6, 4, 6, 4),
                Child = text,
            };
            _chips.Add((chip, text));
            row.Children.Add(chip);
        }
        _root.Children.Add(row);
    }

    public void Apply(ThemeSpec spec)
    {
        if (spec is null) return;
        _spec = spec;
        var accent = spec.Accent;
        foreach (var painted in _painted)
        {
            // 每张片子的色相由核心按片名算，再和主题色对半混：换配色时整面墙跟着变
            var hue = ThemeSpec.FromHsv(painted.Card.Hue, 0.44, 0.82);
            var top = ThemeSpec.Mix(hue, accent, 0.5);
            var bottom = ThemeSpec.Mix(top, spec.Background, 0.62);
            painted.Poster.Background = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops = new GradientStopCollection
                {
                    new(ThemeSpec.Mix(top, Colors.White, 0.12), 0),
                    new(bottom, 1),
                },
            };
            painted.Title.Foreground = new SolidColorBrush(spec.TextPrimary);
            painted.Meta.Foreground = new SolidColorBrush(spec.TextSecondary);
            var badgeColor = ThemeSpec.Fade(accent, painted.Card.Watched ? 0.92 : 0.55);
            painted.Badge.Background = new SolidColorBrush(badgeColor);
            painted.BadgeText.Foreground = new SolidColorBrush(spec.TextOn(badgeColor));
            painted.ProgressBar.Background = new SolidColorBrush(ThemeSpec.Fade(accent, 0.95));
            var on = _selected == painted.Card.FileId;
            painted.Frame.BorderBrush = new SolidColorBrush(on ? accent : ThemeSpec.Fade(spec.TextPrimary, 0.10));
            painted.Frame.BorderThickness = new Thickness(on ? 2 : 1);
        }
        foreach (var (chip, text) in _chips)
        {
            chip.Background = new SolidColorBrush(ThemeSpec.Fade(accent, 0.20));
            chip.BorderBrush = new SolidColorBrush(ThemeSpec.Fade(accent, 0.55));
            chip.BorderThickness = new Thickness(1);
            text.Foreground = new SolidColorBrush(spec.TextPrimary);
        }
    }

    // MARK: - 建版

    private static IEnumerable<Card> ReadCards(JsonNode? entries)
    {
        foreach (var entry in entries?.AsArray() ?? new JsonArray())
        {
            yield return new Card(
                (long)(entry?["fileId"]?.GetValue<double>() ?? 0),
                entry?["title"]?.GetValue<string>() ?? string.Empty,
                ReadYear(entry?["year"]),
                entry?["percent"]?.GetValue<double>() is double percent ? (int)percent : 0,
                entry?["watched"]?.GetValue<bool>() ?? false,
                entry?["hue"]?.GetValue<double>() ?? 0,
                entry?["path"]?.GetValue<string>() ?? string.Empty,
                entry?["position"]?.GetValue<double>() ?? 0,
                entry?["duration"]?.GetValue<double>() ?? 0,
                ReadStrings(entry?["genres"]),
                ReadStrings(entry?["directors"]),
                ReadStrings(entry?["casts"]));
        }
    }

    private static string[] ReadStrings(JsonNode? node)
    {
        var list = new List<string>();
        foreach (var item in node?.AsArray() ?? new JsonArray())
            list.Add(item?.GetValue<string>() ?? string.Empty);
        return list.ToArray();
    }

    private static int? ReadYear(JsonNode? node)
    {
        if (node is null) return null;
        try { return (int)(node.GetValue<double>()); }
        catch (Exception) { return null; }
    }

    private static int Int(JsonNode node, string key) => node?[key]?.GetValue<double>() is double value ? (int)value : 0;

    private FrameworkElement BuildHeader(Shelf shelf) => new Grid
    {
        Margin = new Thickness(2, 12, 2, 6),
        Children =
        {
            new TextBlock
            {
                Text = shelf.Title,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Left,
            },
            new TextBlock
            {
                Text = $"{shelf.Cards.Count} 部",
                FontSize = 12,
                Margin = new Thickness(0, 3, 2, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
            },
        },
    };

    private FrameworkElement BuildRow(Shelf shelf)
    {
        var row = new WrapPanel { Margin = new Thickness(-6, 0, -6, 0) };
        foreach (var card in shelf.Cards) row.Children.Add(BuildCard(card));
        return row;
    }

    private FrameworkElement BuildCard(Card card)
    {
        var poster = new Border { Height = PosterHeight, CornerRadius = new CornerRadius(9) };
        var badgeText = new TextBlock
        {
            Text = card.Watched ? "已看" : "未看",
            FontSize = 11,
            Margin = new Thickness(6, 4, 6, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var badge = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(1, 0, 1, 0),
            Child = badgeText,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0),
        };
        var progress = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(6, 0, 6, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Width = card.Percent > 0 ? Math.Max(6, PosterWidth * card.Percent / 100 - 12) : 0,
        };
        poster.Child = new Grid { Children = { badge, progress } };

        var frame = new Border
        {
            Child = poster,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
        };

        var title = new TextBlock
        {
            Text = card.Title,
            FontSize = 12.5,
            Margin = new Thickness(2, 6, 2, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 34,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var meta = new TextBlock
        {
            Text = MetaLine(card),
            FontSize = 11,
            Margin = new Thickness(2, 1, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var card0 = new StackPanel { Width = PosterWidth, Margin = new Thickness(6, 0, 6, 10), Cursor = Cursors.Hand };
        card0.Children.Add(frame);
        card0.Children.Add(title);
        card0.Children.Add(meta);
        card0.MouseLeftButtonUp += (_, _) => Select(card.FileId);

        _painted.Add(new Painted
        {
            Card = card, Poster = poster, Frame = frame, Title = title, Meta = meta,
            Badge = badge, BadgeText = badgeText, ProgressBar = progress,
        });
        return card0;
    }

    private static string MetaLine(Card card)
    {
        var year = card.Year?.ToString() ?? "年份未知";
        return card.Percent > 0 ? $"{year} · 看到 {card.Percent}%" : year;
    }

    private void Select(long fileId)
    {
        _selected = fileId;
        Apply(_spec);
        SelectionChanged?.Invoke(Selected);
    }

    private Card? Find(long fileId) => _painted.FirstOrDefault(p => p.Card.FileId == fileId)?.Card;
}
