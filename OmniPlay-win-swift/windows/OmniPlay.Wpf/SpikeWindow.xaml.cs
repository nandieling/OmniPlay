
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace OmniPlay.WpfSpike;

/// 演示窗：自绘玻璃、播放子 HWND、Popup 控制条三件事一次验完。
/// 所有颜色都来自核心的 theme.get，这里只做「配方到 WPF 笔刷」的翻译。
public partial class SpikeWindow : Window
{
    private readonly CoreBridge _core = new();
    private readonly VideoHost _video = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly List<(string Id, Color Accent, Button Chip)> _themeChips = new();
    private readonly List<Border> _posterCards = new();

    private StackPanel? _topContent;
    private StackPanel? _centerContent;
    private StackPanel? _logContent;
    private StackPanel? _chipHost;
    private ToggleButton _glassToggle = null!;
    private GlassPlate? _topPlate;
    private GlassPlate? _centerPlate;
    private GlassPlate? _logPlate;
    private WrapPanel? _posters;
    private TextBlock? _titleBlock;
    private TextBlock? _progressText;
    private TextBlock? _statusText;
    private TextBlock? _logText;
    private ThemeSpec _spec = null!;
    private string _colorId = "crystal";
    private bool _glass = true;

    public SpikeWindow()
    {
        InitializeComponent();
        _poll.Tick += (_, _) => RefreshPlaybackStatus();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var version = _core.Invoke("version");
        Log($"核心 {_core.Version}，平台 {version["platform"]?.GetValue<string>()}");

        BuildTopContent();
        BuildPosterBoard();
        BuildCenterContent();
        BuildLogContent();
        VideoArea.Children.Add(_video);
        ApplyTheme();
        _poll.Start();
    }

    // MARK: - 版面（只搭内容，玻璃板在 EnsurePlates 里统一包）

    private void BuildTopContent()
    {
        _titleBlock = new TextBlock
        {
            Text = "觅影",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
        };
        _chipHost = new StackPanel { Orientation = Orientation.Horizontal };
        BuildThemeChips();

        _glassToggle = new ToggleButton
        {
            Content = "液态玻璃",
            IsChecked = true,
            Style = (Style)Resources["PillToggle"],
            Margin = new Thickness(14, 0, 0, 0),
        };
        _glassToggle.Checked += (_, _) => { _glass = true; ApplyTheme(); };
        _glassToggle.Unchecked += (_, _) => { _glass = false; ApplyTheme(); };

        var play = new Button { Content = "播放示例", Style = (Style)Resources["Pill"] };
        play.Click += OnPlaySample;

        _topContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _topContent.Children.Add(_titleBlock);
        _topContent.Children.Add(_chipHost);
        _topContent.Children.Add(_glassToggle);
        _topContent.Children.Add(play);
    }

    /// 海报墙占位：给玻璃板提供真正的采样内容，换主题时玻璃里的颜色跟着走。
    private void BuildPosterBoard()
    {
        var board = new WrapPanel { Margin = new Thickness(24, 20, 24, 20), ItemWidth = 148, ItemHeight = 214 };
        for (var i = 0; i < 12; i++)
        {
            var card = new Border
            {
                Width = 132,
                Height = 198,
                Margin = new Thickness(6),
                CornerRadius = new CornerRadius(9),
            };
            _posterCards.Add(card);
            board.Children.Add(card);
        }
        _posters = board;
        VideoArea.Children.Add(board);
    }

    private void BuildCenterContent()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var play = new Button { Content = "▶", Style = (Style)Resources["Pill"], Padding = new Thickness(16, 6, 16, 6) };
        play.Click += (_, _) => TogglePause();
        _progressText = new TextBlock
        {
            Text = "未播放",
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(play);
        panel.Children.Add(new Button { Content = "已看", Style = (Style)Resources["Pill"] });
        panel.Children.Add(new Button { Content = "未看", Style = (Style)Resources["Pill"] });
        panel.Children.Add(_progressText);
        _centerContent = panel;
    }

    private void BuildLogContent()
    {
        _statusText = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12 };
        _logText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
        };
        _logContent = new StackPanel();
        _logContent.Children.Add(_statusText);
        _logContent.Children.Add(new ScrollViewer
        {
            MaxHeight = 72,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _logText,
        });
    }

    private void BuildThemeChips()
    {
        var themes = _core.Invoke("theme.list")["themes"]?.AsArray() ?? new JsonArray();
        foreach (var theme in themes)
        {
            var id = theme?["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id)) continue;
            var accent = ThemeSpec.ParseArgb(theme!["accent"]?.GetValue<string>());
            var chip = new Button
            {
                Content = theme["displayName"]?.GetValue<string>() ?? id,
                Style = (Style)Resources["Pill"],
            };
            chip.Click += (_, _) =>
            {
                _colorId = id!;
                ApplyTheme();
            };
            _themeChips.Add((id!, accent, chip));
            _chipHost?.Children.Add(chip);
        }
    }

    // MARK: - 主题

    private void ApplyTheme()
    {
        var parameters = new JsonObject { ["color"] = _colorId, ["style"] = _glass ? "liquidGlass" : "classic" };
        _spec = ThemeSpec.FromJson(_core.Invoke("theme.get", parameters));
        var spec = _spec;
        var glass = spec.Glass;

        EnsurePlates(spec);
        Backdrop.Apply(spec);
        Put("TextPrimaryBrush", spec.TextPrimary);
        Put("TextSecondaryBrush", spec.TextSecondary);
        Put("AccentBrush", spec.Accent);
        Put("ChipBrush", glass?.Tint ?? ThemeSpec.Fade(spec.Surface, 0.92));
        Put("ChipBorderBrush", glass?.Border ?? ThemeSpec.Fade(spec.TextPrimary, 0.14));

        _topPlate?.Apply(spec, Backdrop);
        _centerPlate?.Apply(spec, _posters);
        _logPlate?.Apply(spec, Backdrop);

        if (_titleBlock is not null) _titleBlock.Foreground = new SolidColorBrush(spec.TextPrimary);
        if (_progressText is not null) _progressText.Foreground = new SolidColorBrush(spec.TextPrimary);
        if (_statusText is not null) _statusText.Foreground = new SolidColorBrush(spec.TextSecondary);
        if (_logText is not null) _logText.Foreground = new SolidColorBrush(spec.TextSecondary);
        foreach (var (id, accent, chip) in _themeChips)
        {
            chip.Background = new SolidColorBrush(id == _colorId ? accent : ThemeSpec.Fade(accent, 0.4));
            chip.Foreground = new SolidColorBrush(ContrastOn(accent));
        }
        RecolorPosters(spec);
        Log($"主题：{spec.DisplayName} / {(spec.IsGlass ? "液态玻璃（自绘）" : "经典")}");
    }

    private void EnsurePlates(ThemeSpec spec)
    {
        if (_topPlate is null && _topContent is not null)
        {
            _topPlate = new GlassPlate(spec, _topContent, Backdrop)
            {
                Margin = new Thickness(14, 14, 14, 0),
            };
            TopHost.Children.Add(_topPlate);
        }
        if (_centerPlate is null && _centerContent is not null)
        {
            _centerPlate = new GlassPlate(spec, _centerContent, _posters)
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 26),
            };
            VideoArea.Children.Add(_centerPlate);
        }
        if (_logPlate is null && _logContent is not null)
        {
            _logPlate = new GlassPlate(spec, _logContent, Backdrop) { Margin = new Thickness(14, 10, 14, 14) };
            LogHost.Children.Add(_logPlate);
        }
    }

    private void RecolorPosters(ThemeSpec spec)
    {
        for (var i = 0; i < _posterCards.Count; i++)
        {
            var top = ThemeSpec.Fade(spec.Accent, 0.22 + (i % 5) * 0.13);
            var bottom = ThemeSpec.Fade(spec.TextPrimary, 0.30 + (i % 3) * 0.16);
            _posterCards[i].Background = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops = new GradientStopCollection { new GradientStop(top, 0), new GradientStop(bottom, 1) },
            };
        }
    }

    private void Put(string key, Color color) => Resources[key] = new SolidColorBrush(color);

    // MARK: - 播放

    private void OnPlaySample(object sender, RoutedEventArgs e)
    {
        var sample = Path.Combine(AppContext.BaseDirectory, "testdata", "sample.mp4");
        if (!File.Exists(sample))
        {
            Log($"找不到示例视频：{sample}");
            return;
        }
        if (_video.RenderHandle == IntPtr.Zero)
        {
            Log("播放子窗口还没建好");
            return;
        }
        var parameters = new JsonObject { ["wid"] = (long)_video.RenderHandle, ["path"] = sample };
        var result = _core.Invoke("player.start", parameters);
        Log($"player.start → {result.ToJsonString()}");
        if (result["started"]?.GetValue<bool>() != true) return;

        // 画面进来后，海报与中间那条玻璃会被子 HWND 盖住（airspace），
        // 控制条改由 Popup 承担：这正是这个演示要验的东西。
        if (_posters is not null) _posters.Visibility = Visibility.Collapsed;
        if (_centerPlate is not null) _centerPlate.Visibility = Visibility.Collapsed;
        BuildPopupBar();
    }

    private void BuildPopupBar()
    {
        if (ControlPopup.Child is not null) return;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var play = new Button { Content = "⏯", Style = (Style)Resources["Pill"], Padding = new Thickness(16, 6, 16, 6) };
        play.Click += (_, _) => TogglePause();
        _progressText = new TextBlock
        {
            Text = "0.0s",
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(_spec.TextPrimary),
        };
        panel.Children.Add(play);
        panel.Children.Add(_progressText);
        // Popup 与窗口内容不同视觉树，采不到画面，退回纯染色玻璃
        ControlPopup.Child = new GlassPlate(_spec, panel, null);
        ControlPopup.IsOpen = true;
    }

    private void TogglePause()
    {
        var status = _core.Invoke("player.status");
        var paused = status["paused"]?.GetValue<bool>() ?? true;
        _core.Invoke("player.pause", new JsonObject { ["paused"] = !paused });
    }

    private void RefreshPlaybackStatus()
    {
        var status = _core.Invoke("player.status");
        if (status["available"]?.GetValue<bool>() != true)
        {
            if (_statusText is not null)
                _statusText.Text = "播放器不可用：" + (status["reason"]?.GetValue<string>() ?? "未知原因");
            return;
        }
        var position = status["timePos"]?.GetValue<double>() ?? 0;
        var duration = status["duration"]?.GetValue<double>() ?? 0;
        var paused = status["paused"]?.GetValue<bool>() ?? true;
        if (_statusText is not null)
            _statusText.Text = $"player：{position:F1}s / {duration:F1}s  {(paused ? "已暂停" : "播放中")}";
        if (_progressText is not null) _progressText.Text = $"{position:F1}s / {duration:F1}s";
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _poll.Stop();
        try { _core.Invoke("player.stop"); } catch (Exception error) { Log("停止播放失败：" + error.Message); }
        _core.Dispose();
    }

    private Color ContrastOn(Color background) => ThemeSpec.Luminance(background) > 150 ? Colors.Black : Colors.White;

    private void Log(string message)
    {
        if (_logText is not null)
            _logText.Text = DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine + _logText.Text;
    }
}
