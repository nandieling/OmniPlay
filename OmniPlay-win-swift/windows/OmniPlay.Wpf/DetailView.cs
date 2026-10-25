using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OmniPlay.WpfSpike;

/// 详情页：叠在海报墙右侧的一栏，不另起窗口。里面不放任何判断——
/// 片子信息、进度、已看未看都来自 library.sections 给的那条记录。
public sealed class DetailView : Border
{
    private readonly Style _pill;
    private readonly StackPanel _body = new() { Margin = new Thickness(16, 14, 16, 14) };
    private readonly TextBlock _title = new() { FontSize = 21, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _meta = new() { FontSize = 12.5, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _credits = new() { FontSize = 12.5, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _hint = new() { FontSize = 12.5, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Button _play = new() { Content = "开始播放" };
    private readonly Button _watched = new() { Content = "已看" };
    private readonly Button _unwatched = new() { Content = "未看" };
    private HomeBoard.Card? _card;

    public event Action<HomeBoard.Card>? PlayRequested;
    public event Action<HomeBoard.Card, bool>? MarkRequested;

    public DetailView(Style pill)
    {
        _pill = pill;
        Width = 360;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Stretch;
        CornerRadius = new CornerRadius(12);
        Visibility = Visibility.Collapsed;

        _play.Click += (_, _) => { if (_card is not null) PlayRequested?.Invoke(_card); };
        _watched.Click += (_, _) => { if (_card is not null) MarkRequested?.Invoke(_card, true); };
        _unwatched.Click += (_, _) => { if (_card is not null) MarkRequested?.Invoke(_card, false); };
        foreach (var (button, index) in new[] { _play, _watched, _unwatched }.Select((b, i) => (b, i)))
        {
            button.Style = pill;
            button.Margin = new Thickness(index == 0 ? 0 : 6, 0, 0, 0);
        }

        _body.Children.Add(_title);
        _body.Children.Add(_meta);
        _body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { _play, _watched, _unwatched },
        });
        _body.Children.Add(_credits);
        _body.Children.Add(_hint);
        Child = new ScrollViewer
        {
            Content = _body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    public void Show(HomeBoard.Card? card)
    {
        _card = card;
        if (card is null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        _title.Text = card.Title;
        _meta.Text = MetaLine(card);
        _play.Content = card.Percent > 0 ? $"继续播放（{card.Percent}%）" : "开始播放";
        _credits.Text = string.Join("\n", new[]
        {
            card.Directors.Count > 0 ? "导演：" + string.Join("、", card.Directors) : "",
            card.Casts.Count > 0 ? "主演：" + string.Join("、", card.Casts) : "",
            card.Genres.Count > 0 ? "类型：" + string.Join("、", card.Genres) : "",
        }.Where(line => line.Length > 0));
        _hint.Text = card.Path.StartsWith("demo://", StringComparison.Ordinal)
            ? "演示数据：路径是假的，播的是随包示例片"
            : card.Path;
    }

    public void Apply(ThemeSpec spec)
    {
        if (spec is null) return;
        var glass = spec.Glass;
        // 左浅右深、上浅下深：和 mac 那侧弹窗同一个方向
        var light = ThemeSpec.Mix(spec.Surface, Colors.White, 0.10);
        var dark = ThemeSpec.Mix(spec.Surface, spec.Background, 0.55);
        Background = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops = new GradientStopCollection { new(light, 0), new(dark, 1) },
        };
        BorderBrush = new SolidColorBrush(glass?.Border ?? ThemeSpec.Fade(spec.TextPrimary, 0.12));
        BorderThickness = new Thickness(1);
        _title.Foreground = new SolidColorBrush(spec.TextPrimary);
        _meta.Foreground = new SolidColorBrush(spec.TextSecondary);
        _credits.Foreground = new SolidColorBrush(spec.TextSecondary);
        _hint.Foreground = new SolidColorBrush(spec.TextSecondary);
        _play.Foreground = new SolidColorBrush(spec.TextOn(spec.Accent));
        _play.Background = new SolidColorBrush(spec.Accent);
    }

    private static string MetaLine(HomeBoard.Card card)
    {
        var parts = new List<string>
        {
            card.Year?.ToString() ?? "年份未知",
            card.Duration > 0 ? FormatDuration(card.Duration) : "时长未知",
            card.Watched ? "已看" : "未看",
        };
        if (card.Percent > 0) parts.Add($"看到 {card.Percent}%");
        return string.Join(" · ", parts);
    }

    private static string FormatDuration(double seconds)
    {
        var total = (int)seconds;
        return total >= 3600 ? $"{total / 3600} 小时 {total % 3600 / 60} 分" : $"{total / 60} 分";
    }
}
