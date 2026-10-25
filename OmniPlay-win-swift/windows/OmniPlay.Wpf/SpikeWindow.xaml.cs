
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
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

    private StackPanel? _topContent;
    private StackPanel? _centerContent;
    private StackPanel? _logContent;
    private StackPanel? _chipHost;
    private ToggleButton _glassToggle = null!;
    private GlassPlate? _topPlate;
    private GlassPlate? _centerPlate;
    private GlassPlate? _logPlate;
    private HomeBoard _home = null!;
    private NavRail _nav = null!;
    private DetailView _detail = null!;
    private GlassPlate? _navPlate;
    private TextBlock? _selectionText;
    private TextBlock? _nowPlayingText;
    // 正在放的片子与上次回写进度的时刻。进度每 5 秒回写一次，关窗前必须再写一次
    private HomeBoard.Card? _playing;
    private DateTime _lastProgressWrite = DateTime.MinValue;
    // 起播那一刻文件往往还没装载完，直接 seek 会被 mpv 吞掉，等读到时长再跳
    private double _pendingResume;
    private TextBlock? _titleBlock;
    private TextBlock? _progressText;
    private TextBlock? _statusText;
    private TextBlock? _logText;
    private ThemeSpec _spec = null!;
    private string _colorId = "crystal";
    private bool _glass = true;

    public SpikeWindow()
    {
        StartupLog.Mark("窗口开始构造");
        StartupLog.ArmWatchdog();
        // 字体枚举是 WPF 首帧前最慢的一步，也是中文机器上常见的坑（一款坏字体能让
        // DirectWrite 转半天）。先在这儿自己走一遍：卡住的话断点就写在这行上。
        StartupLog.Mark("字体回源（DirectWrite 找微软雅黑）");
        var probe = new FormattedText("觅影 0123", System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 14, Brushes.White, 1.25);
        StartupLog.Mark($"字体回源通了（量出 {probe.WidthIncludingTrailingWhitespace:F0} 像素宽），XAML 待解析");
        InitializeComponent();
        StartupLog.Mark("XAML 解析完，等源就绪");
        // 轮询里的异常不能带崩进程：停表并把原因写在状态行上
        _poll.Tick += (_, _) => PollTick();
        // 首帧这行是分隔线：日志停在这行之前 = 死在渲染，停在之后 = 死在读库
        ContentRendered += (_, _) =>
        {
            StartupLog.NoteRendered();
            StartupLog.Mark("第一帧已画出");
        };
        SourceInitialized += (_, _) => StartupLog.Mark("窗口句柄已建好");
    }

    private void PollTick()
    {
        try
        {
            RefreshPlaybackStatus();
        }
        catch (Exception error)
        {
            _poll.Stop();
            if (_statusText is not null) _statusText.Text = "状态轮询已停：" + error.Message;
            StartupLog.Fail("状态轮询", error);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly)
                Log("这次用软件渲染：显卡特效先关掉");
            var version = _core.Invoke("version");
            Log($"核心 {_core.Version}，平台 {version["platform"]?.GetValue<string>()}");
            // 核心会往宿主注册的回调上推消息，库落到哪儿了这类事就靠它带话
            _core.EventReceived += OnCoreEvent;

            BuildTopContent();
            StartupLog.Mark("顶栏完成");
            BuildHome();
            StartupLog.Mark("海报墙与导航完成");
            BuildCenterContent();
            BuildLogContent();
            VideoArea.Children.Add(_video);
            ApplyTheme();
            _poll.Start();
            StartupLog.Mark("界面搭建完成");
            // 壳先站稳，再去读库：库在哪儿、开不开得动，都不该把窗口一起带走
            Dispatcher.BeginInvoke(new Action(LoadLibraryData), DispatcherPriority.Background);
        }
        catch (Exception error)
        {
            // 版面搭到一半就炸：弹窗说清原因，别让窗口无声消失
            App.ReportFailure("界面搭建", error);
        }
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

    /// 真片库：分组与排序都由核心的 library.sections 给，这块只是把它画出来。
    /// 海报墙同时是玻璃板的采样源，滚到哪儿玻璃里的颜色就跟到哪儿。
    private void BuildHome()
    {
        _home = new HomeBoard(_core) { Margin = new Thickness(180, 0, 0, 0) };
        _home.StatusChanged += line =>
        {
            Log(line);
            StartupLog.Write("片库：" + line);   // 日志里有这行，说明库真的读到数了
        };
        _home.SelectionChanged += card =>
        {
            _detail.Show(card);
            if (_selectionText is not null)
                _selectionText.Text = card is null ? "没选片子" : $"选中：{card.Title}";
        };
        VideoArea.Children.Add(_home);

        _detail = new DetailView((Style)Resources["Pill"]) { Margin = new Thickness(0, 10, 12, 10) };
        _detail.PlayRequested += PlayCard;
        _detail.MarkRequested += (card, watched) => Mark(watched);
        VideoArea.Children.Add(_detail);

        _nav = new NavRail();
        _nav.ViewChosen += view => _home.Show(view);
    }

    /// 标记已看 / 未看：写完让核心重读一遍，界面不自己猜结果
    private void Mark(bool watched)
    {
        var card = _home.Selected;
        if (card is null)
        {
            Log("先点一张海报再标记");
            return;
        }
        try
        {
            _core.Invoke("library.mark", new JsonObject { ["fileId"] = card.FileId, ["watched"] = watched });
            Log($"library.mark → 文件 {card.FileId} {(watched ? "已看" : "未看")}");
            LoadLibraryData();
        }
        catch (Exception error)
        {
            Log("标记没写进去：" + error.Message);
        }
    }

    /// 读库。失败只记一笔并把话写在日志区，界面上已经画出来的那部分照常能用。
    private void LoadLibraryData()
    {
        if (App.NoLibrary)
        {
            StartupLog.Write("片库：按 --no-library 跳过");
            Log("按 --no-library 启动：没读片库");
            return;
        }
        try
        {
            StartupLog.Write("片库：开始读取");
            _home.Refresh();
            StartupLog.Write("片库：读取完成");   // 日志里的这一行是「库这层走完了」的凭据
        }
        catch (Exception error)
        {
            App.NoteFailure("片库读取", error);
            Log("片库读不出来：" + error.Message);
        }
        RefreshGlassSamples();
    }

    /// 海报内容换了，采样的玻璃得跟着重算一次
    private void RefreshGlassSamples() => _centerPlate?.RefreshSample();

    /// 核心推来的消息。阶段那条当场落盘：进程要是硬死了，日志里最后一条阶段就是断点。
    private void OnCoreEvent(string json)
    {
        if (!json.Contains("\"library.stage\""))
        {
            Dispatcher.BeginInvoke(new Action(() => LogEvent(json)));
            return;
        }
        try
        {
            var at = JsonNode.Parse(json)?["payload"]?["at"]?.GetValue<string>() ?? "?";
            StartupLog.Write("片库：阶段 " + at);
        }
        catch (Exception) { /* 看不懂的消息直接丢 */ }
    }

    /// 只挑需要给人看的几条说，别把日志区刷成瀑布。
    private void LogEvent(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node?["topic"]?.GetValue<string>() != "library.location") return;
            var file = node?["payload"]?["file"]?.GetValue<string>() ?? "?";
            var notes = new List<string>();
            foreach (var item in node?["payload"]?["notes"]?.AsArray() ?? new JsonArray())
                notes.Add(item?.GetValue<string>() ?? "?");
            Log(notes.Count == 0 ? $"片库：{file}" : $"片库换到 {file} 去了：" + string.Join("；", notes));
        }
        catch (Exception) { /* 看不懂的消息直接丢 */ }
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
        var watched = new Button { Content = "已看", Style = (Style)Resources["Pill"] };
        watched.Click += (_, _) => Mark(true);
        var unwatched = new Button { Content = "未看", Style = (Style)Resources["Pill"] };
        unwatched.Click += (_, _) => Mark(false);
        _selectionText = new TextBlock
        {
            Text = "没选片子",
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(play);
        panel.Children.Add(watched);
        panel.Children.Add(unwatched);
        panel.Children.Add(_selectionText);
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
        _centerPlate?.Apply(spec, _home);
        _logPlate?.Apply(spec, Backdrop);
        _navPlate?.Apply(spec, Backdrop);
        _nav.Apply(spec);
        _detail.Apply(spec);

        _home.Apply(spec);
        if (_titleBlock is not null) _titleBlock.Foreground = new SolidColorBrush(spec.TextPrimary);
        if (_selectionText is not null) _selectionText.Foreground = new SolidColorBrush(spec.TextPrimary);
        if (_progressText is not null) _progressText.Foreground = new SolidColorBrush(spec.TextPrimary);
        if (_statusText is not null) _statusText.Foreground = new SolidColorBrush(spec.TextSecondary);
        if (_logText is not null) _logText.Foreground = new SolidColorBrush(spec.TextSecondary);
        foreach (var (id, accent, chip) in _themeChips)
        {
            chip.Background = new SolidColorBrush(id == _colorId ? accent : ThemeSpec.Fade(accent, 0.4));
            chip.Foreground = new SolidColorBrush(ContrastOn(accent));
        }
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
            _centerPlate = new GlassPlate(spec, _centerContent, _home)
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 26),
            };
            VideoArea.Children.Add(_centerPlate);
        }
        if (_navPlate is null && _nav is not null)
        {
            _navPlate = new GlassPlate(spec, _nav, Backdrop)
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(14, 14, 0, 0),
            };
            VideoArea.Children.Add(_navPlate);
        }
        if (_logPlate is null && _logContent is not null)
        {
            _logPlate = new GlassPlate(spec, _logContent, Backdrop) { Margin = new Thickness(14, 10, 14, 14) };
            LogHost.Children.Add(_logPlate);
        }
    }

    private void Put(string key, Color color) => Resources[key] = new SolidColorBrush(color);

    // MARK: - 播放

    private void OnPlaySample(object sender, RoutedEventArgs e)
    {
        _pendingResume = 0;
        StartPlayback(SampleFile(), null);
    }

    /// 详情页里点播放。演示片库的路径是假的，拿随包示例片顶一下，
    /// 至少「点海报 → 起播 → 回写进度 → 进继续播放」这条链是真的。
    private void PlayCard(HomeBoard.Card card)
    {
        var path = card.Path.StartsWith("demo://", StringComparison.Ordinal) ? SampleFile() : card.Path;
        if (path.Length == 0)
        {
            Log("这部片没有可播的路径");
            return;
        }
        _pendingResume = card.Position > 1 ? card.Position : 0;
        StartPlayback(path, card);
    }

    private static string SampleFile() => Path.Combine(AppContext.BaseDirectory, "testdata", "sample.mp4");

    private void StartPlayback(string path, HomeBoard.Card? card)
    {
        if (!File.Exists(path))
        {
            Log($"找不到视频文件：{path}");
            return;
        }
        if (_video.RenderHandle == IntPtr.Zero)
        {
            Log("播放子窗口还没建好");
            return;
        }
        var parameters = new JsonObject { ["wid"] = (long)_video.RenderHandle, ["path"] = path };
        var result = _core.Invoke("player.start", parameters);
        Log($"player.start → {result.ToJsonString()}");
        if (result["started"]?.GetValue<bool>() != true) return;

        // 画面进来后，海报与中间那条玻璃会被子 HWND 盖住（airspace），
        // 控制条改由 Popup 承担：这正是这个演示要验的东西。
        _playing = card;
        _lastProgressWrite = DateTime.Now;
        if (_pendingResume > 0)
            Log($"续播：等装载完跳到 {_pendingResume:F0}s");
        _home.Visibility = Visibility.Collapsed;
        if (_centerPlate is not null) _centerPlate.Visibility = Visibility.Collapsed;
        BuildPopupBar();
        if (_nowPlayingText is not null) _nowPlayingText.Text = card?.Title ?? "示例片段";
    }

    /// 停机：先落进度，再让核心停机，最后把海报墙刷回来
    private void StopPlayback()
    {
        WriteProgress(force: true);
        _core.Invoke("player.stop");
        _playing = null;
        _pendingResume = 0;
        _home.Visibility = Visibility.Visible;
        if (_centerPlate is not null) _centerPlate.Visibility = Visibility.Visible;
        if (ControlPopup.Child is not null) ControlPopup.IsOpen = false;
        LoadLibraryData();
        Log("已停机，进度已回写");
    }

    /// 进度回写。每 5 秒一次；关窗与停机时强制写一次。
    private void WriteProgress(bool force = false)
    {
        var card = _playing;
        if (card is null) return;
        var now = DateTime.Now;
        if (!force && (now - _lastProgressWrite).TotalSeconds < 5) return;
        var status = _core.Invoke("player.status");
        var position = status["timePos"]?.GetValue<double>() ?? 0;
        var duration = status["duration"]?.GetValue<double>() ?? 0;
        if (position <= 0) return;
        _lastProgressWrite = now;
        _core.Invoke("library.progress", new JsonObject
        {
            ["fileId"] = card.FileId, ["position"] = position, ["duration"] = duration,
        });
    }

    private void BuildPopupBar()
    {
        if (ControlPopup.Child is not null) return;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var play = new Button { Content = "⏯", Style = (Style)Resources["Pill"], Padding = new Thickness(16, 6, 16, 6) };
        play.Click += (_, _) => TogglePause();
        var stop = new Button { Content = "停止", Style = (Style)Resources["Pill"] };
        stop.Click += (_, _) => StopPlayback();
        _nowPlayingText = new TextBlock
        {
            Text = _playing?.Title ?? "示例片段",
            Margin = new Thickness(14, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(_spec.TextPrimary),
        };
        panel.Children.Add(_nowPlayingText);
        _progressText = new TextBlock
        {
            Text = "0.0s",
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(_spec.TextPrimary),
        };
        panel.Children.Add(play);
        panel.Children.Add(stop);
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
        if (_pendingResume > 0 && duration > 0)
        {
            var at = _pendingResume;
            _pendingResume = 0;
            _core.Invoke("player.seek", new JsonObject { ["seconds"] = at });
            Log($"续播：已跳到 {at:F0}s");
        }
        WriteProgress();
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _poll.Stop();
        try
        {
            WriteProgress(force: true);
            _core.Invoke("player.stop");
        }
        catch (Exception error) { Log("停止播放失败：" + error.Message); }
        _core.Dispose();
        StartupLog.Write("窗口已关闭，核心已释放");
        ArmExitBackstop();
    }

    /// 窗口关掉了就等于这趟结束了。后面那段收尾（WPF 渲染线程退出、AppDomain 卸载、
    /// Swift 运行时各 DLL 的 DllMain）在构建机上实测能挂一分多钟——库和日志都已经
    /// 落盘，没必要等它。3 秒还没走开就直接结束进程。
    private static void ArmExitBackstop()
    {
        new Thread(() =>
        {
            Thread.Sleep(3000);
            StartupLog.Write(App.Failed ? "退出（1）（关窗后 3 秒兜底）" : "退出（0）（关窗后 3 秒兜底）");
            NativePreflight.HardExit(App.Failed ? 1 : 0);
        })
        { IsBackground = false }.Start();
    }

    private Color ContrastOn(Color background) => ThemeSpec.Luminance(background) > 150 ? Colors.Black : Colors.White;

    private void Log(string message)
    {
        if (_logText is not null)
            _logText.Text = DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine + _logText.Text;
    }
}
