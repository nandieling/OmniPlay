using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OmniPlay.WpfSpike;

/// 左侧导航。列表本身就是片库的四个架子加两个分类视图，界面这边不定义「有哪几页」，
/// 只把核心认得的视图 id 传回去。
public sealed class NavRail : StackPanel
{
    private sealed record Item(string View, string Label, string Glyph);

    private static readonly Item[] Items =
    {
        new("home", "首页", "\uE80F"),
        new("continue", "继续播放", "\uE769"),
        new("watched", "已看", "\uE73E"),
        new("unwatched", "未看", "\uE892"),
        new("director", "导演", "\uE716"),
        new("cast", "主演", "\uE77B"),
    };

    private readonly List<(Item Item, Border Plate, TextBlock Label)> _entries = new();
    private ThemeSpec _spec = null!;
    private string _current = "home";

    public event Action<string>? ViewChosen;

    public NavRail()
    {
        Orientation = Orientation.Vertical;
        Width = 156;
        foreach (var item in Items)
        {
            var glyph = new TextBlock
            {
                Text = item.Glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 15,
                Width = 22,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var label = new TextBlock
            {
                Text = item.Label,
                FontSize = 13,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var plate = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(9, 7, 9, 7),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { glyph, label },
                },
            };
            var view = item.View;
            plate.MouseLeftButtonUp += (_, _) => Choose(view);
            _entries.Add((item, plate, label));
            Children.Add(plate);
        }
    }

    public void Apply(ThemeSpec spec)
    {
        if (spec is null) return;
        _spec = spec;
        foreach (var (item, plate, label) in _entries)
        {
            var on = item.View == _current;
            plate.Background = new SolidColorBrush(on
                ? ThemeSpec.Fade(spec.Accent, 0.30)
                : ThemeSpec.Fade(spec.TextPrimary, 0.05));
            plate.BorderBrush = new SolidColorBrush(on
                ? ThemeSpec.Fade(spec.Accent, 0.90)
                : ThemeSpec.Fade(spec.TextPrimary, 0.08));
            plate.BorderThickness = new Thickness(1);
            label.Foreground = new SolidColorBrush(on ? spec.TextPrimary : spec.TextSecondary);
            if (((StackPanel)plate.Child).Children[0] is TextBlock glyph)
                glyph.Foreground = new SolidColorBrush(on ? spec.Accent : spec.TextSecondary);
        }
    }

    private void Choose(string view)
    {
        if (_current == view) return;
        _current = view;
        Apply(_spec);
        ViewChosen?.Invoke(view);
    }
}
