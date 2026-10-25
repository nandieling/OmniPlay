using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace OmniPlay.WpfSpike;

public partial class App : Application
{
    private static bool _smoke;

    /// --no-library：不碰片库，只把壳立起来。装到别人机器上出岔子时用它二分：
    /// 这个开关能开 → 问题在库与数据；连这个都开不了 → 问题在装载或建版。
    public static bool NoLibrary { get; private set; }

    /// 出过岔子。CI 冒烟靠退出码判断结果，弹窗那种形式在无人值守的机器上没有意义。
    public static bool Failed { get; private set; }

    /// --probe：只做核心那摊事，一个窗口都不建。界面起不来时用它把「核心行不行」和
    /// 「WPF 行不行」切开——日志最后那行探针结论就是答案。
    public static bool Probe { get; private set; }

    /// --min-wpf：只建一个裸窗口，没有 XAML、没有玻璃、没有海报墙。它出得来而主窗口
    /// 起不来，问题在我们自己的内容；它也卡住，就是 WPF 初始化本身。
    public static bool MinimalWpf { get; private set; }

    /// --win32-probe：完全不碰 WPF，只用 user32 建个最普通的窗口。见 UiProbe。
    public static bool Win32Probe { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupLog.Reset();
        _smoke = e.Args.Contains("--smoke");
        Probe = e.Args.Contains("--probe");
        MinimalWpf = e.Args.Contains("--min-wpf");
        Win32Probe = e.Args.Contains("--win32-probe");
        NoLibrary = e.Args.Contains("--no-library");
        StartupLog.Write($"启动：.NET {Environment.Version}，参数「{string.Join(" ", e.Args)}」");

        // 上一趟崩在哪儿，这一趟就先绕开哪儿；手动开关留着给人工二分用。
        GlassPlate.PlainOnly = e.Args.Contains("--no-glass") ||
                               Environment.GetEnvironmentVariable("OMNIPLAY_NO_GLASS") == "1";
        if (GlassPlate.PlainOnly) StartupLog.Write("按要求关掉玻璃采样与模糊（--no-glass）");

        var softRender = e.Args.Contains("--soft-render") ||
                         Environment.GetEnvironmentVariable("OMNIPLAY_SOFT_RENDER") == "1" ||
                         StartupLog.PreviousRunDiedBeforeFirstFrame;
        if (softRender)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            // 上一趟连一帧都没画出来，这一趟就把显卡特效全卸了：软件渲染 + 不采样不模糊。
            // 少一层玻璃只是难看一点，界面起不来才是没法用。
            GlassPlate.PlainOnly = true;
            StartupLog.Write(StartupLog.PreviousRunDiedBeforeFirstFrame
                ? "上次没画到首帧：本次软件渲染 + 关掉玻璃采样与模糊"
                : "按要求用软件渲染");
        }
        // 片库这条路上有岔子时，先再给一次机会（留得下阶段日志），连着两趟都断在
        // 同一处就不再碰它：宁可海报是空的，也别让用户点开只剩一个「已停止工作」。
        var forceLibrary = Environment.GetEnvironmentVariable("OMNIPLAY_LIBRARY") == "1";
        if (!NoLibrary && !forceLibrary &&
            (StartupLog.PreviousRunDiedBeforeLibrary || StartupLog.LibraryDeaths >= 2))
        {
            NoLibrary = true;
            StartupLog.Write(StartupLog.LibraryDeaths >= 2
                ? $"片库连着 {StartupLog.LibraryDeaths} 趟断在读取里：本次跳过（OMNIPLAY_LIBRARY=1 可强行再试）"
                : "上次崩在片库读取之前：本次先跳过片库");
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportFailure("进程级未捕获异常", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        { StartupLog.Fail("后台任务异常", args.Exception); args.SetObserved(); };
        DispatcherUnhandledException += (_, args) =>
        { ReportFailure("界面线程未捕获异常", args.Exception); args.Handled = true; };

        StartupLog.Write(RenderCapability.Tier > 0
            ? "渲染：显卡加速"
            : "渲染：软件渲染（无显卡加速）：玻璃退回纯染色，不做实时采样");

        NativePreflight.SilenceErrorDialogs();
        NativePreflight.InstallCrashReporter();
        NativePreflight.PinSearchPaths();
        try
        {
            NativePreflight.EnsureCoreLoaded();
        }
        catch (NativePreflight.StartupFailureException error)
        {
            ReportFailure("启动自检", error);
            Shutdown();
            return;
        }

        // 冒烟的看门狗：窗口建到一半卡住也得有个人收尸，不然 CI 只能等到超时
        if (_smoke)
        {
            var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            watchdog.Tick += (_, _) =>
            {
                watchdog.Stop();
                StartupLog.Write("冒烟超时：界面没立起来");
                NativePreflight.HardExit(1);
            };
            watchdog.Start();
        }

        base.OnStartup(e);

        if (Probe)
        {
            RunProbe();
            StartupLog.MarkFinished("探针结束");
            StartupLog.Write("退出（探针）");
            StartupLog.Write("退出（探针）");
            NativePreflight.HardExit(Failed ? 1 : 0);
        }
        if (Win32Probe)
        {
            UiProbe.Run(_smoke);
            StartupLog.MarkFinished("Win32 探针结束");
            StartupLog.Write("退出（Win32 探针）");
            StartupLog.Write("退出（Win32 探针）");
            NativePreflight.HardExit(Failed ? 1 : 0);
        }

        // 窗口由这里自己建（不用 StartupUri）：探针模式要能完全不碰 WPF 的建窗路径
        if (MinimalWpf)
        {
            StartupLog.Mark("建最小窗口");
            StartupLog.ArmWatchdog();
            var bare = new Window
            {
                Title = "觅影 · 最小窗口探针",
                Width = 460,
                Height = 180,
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = "看到这行字，说明 WPF 建窗与显示都正常",
                    Margin = new Thickness(24),
                    FontSize = 16,
                },
            };
            bare.SourceInitialized += (_, _) => StartupLog.Mark("最小窗口：句柄已建好");
            bare.ContentRendered += (_, _) =>
            {
                StartupLog.NoteRendered();
                StartupLog.Mark("最小窗口：第一帧已画出");
            };
            bare.Show();
            MainWindow = bare;
            StartupLog.Write("最小窗口：Show 已返回");
        }
        else
        {
            StartupLog.Mark("建主窗口");
            var window = new SpikeWindow();
            MainWindow = window;
            window.Show();
            StartupLog.Write("主窗口已创建");
        }

        if (_smoke)
        {
            // 冒烟：界面立住两秒就走，退出码和启动日志就是结论
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                StartupLog.Write(Failed ? "冒烟：期间出过错" : "冒烟：界面活着");
                // 关窗而不是 Shutdown：Shutdown 要走到 AppDomain 卸载，那里得等 Swift
                // 运行时各 DLL 的 DllMain 收尾，顺序一错就是 fastfail 或者干脆挂住。
                // 关窗同样会触发 OnExit，那里写完收尾日志就直接结束进程。
                foreach (var window in Windows.Cast<Window>().ToList()) window.Close();
            };
            timer.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Failed) e.ApplicationExitCode = 1;
        StartupLog.MarkFinished("已退出");
        StartupLog.Write($"退出（{e.ApplicationExitCode}）");
        base.OnExit(e);
        NativePreflight.HardExit(e.ApplicationExitCode);
    }

    /// 核心侧自检：装载、theme.get、片库开库与分组，全走真实调用，但不开窗口。
    private static void RunProbe()
    {
        try
        {
            using var core = new CoreBridge();
            StartupLog.Write($"探针：核心 {core.Version}，platform " +
                             core.Invoke("version")["platform"]?.GetValue<string>());
            var theme = core.Invoke("theme.get",
                new JsonObject { ["color"] = "crystal", ["style"] = "liquidGlass" });
            StartupLog.Write($"探针：theme.get 通了（{theme.ToJsonString().Length} 字符）");
            var sections = core.Invoke("library.sections");
            var stats = core.Invoke("library.stats");
            StartupLog.Write($"探针：library.sections 通了（{sections.ToJsonString().Length} 字符）；" +
                             $"stats {stats.ToJsonString()}");
            StartupLog.Write("探针：核心这一层全通，卡点在 WPF 建窗");
        }
        catch (Exception error)
        {
            ReportFailure("探针", error);
        }
    }

    /// 把原因写进日志，再（非冒烟时）弹窗告诉用户日志在哪。
    public static void ReportFailure(string stage, Exception? error)
    {
        NoteFailure(stage, error);
        if (_smoke) return;
        MessageBox.Show($"{stage}失败：\n{error?.GetType().Name}：{error?.Message}\n\n详情见\n{StartupLog.WhereToLook}",
                        "觅影 Windows 演示", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// 记一笔但不弹窗：界面还能用的小岔子（比如片库读不出来）走这条，
    /// 冒烟照样按失败处理，不让它在 CI 里蒙混过去。
    public static void NoteFailure(string stage, Exception? error)
    {
        Failed = true;
        StartupLog.Fail(stage, error);
    }
}
