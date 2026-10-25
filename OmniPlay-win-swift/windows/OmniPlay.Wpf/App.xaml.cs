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

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupLog.Reset();
        _smoke = e.Args.Contains("--smoke");
        NoLibrary = e.Args.Contains("--no-library");
        StartupLog.Write($"启动：.NET {Environment.Version}，参数「{string.Join(" ", e.Args)}」");

        // 上一趟崩在哪儿，这一趟就先绕开哪儿；手动开关留着给人工二分用。
        var softRender = e.Args.Contains("--soft-render") ||
                         Environment.GetEnvironmentVariable("OMNIPLAY_SOFT_RENDER") == "1" ||
                         StartupLog.PreviousRunDiedBeforeFirstFrame;
        if (softRender)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            StartupLog.Write(StartupLog.PreviousRunDiedBeforeFirstFrame
                ? "上次崩在首帧之前：本次改用软件渲染（不用显卡特效）"
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
        StartupLog.Write("主窗口已创建");

        if (_smoke)
        {
            // 冒烟：界面立住两秒就走，退出码和启动日志就是结论
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                StartupLog.Write(Failed ? "冒烟：期间出过错" : "冒烟：界面活着");
                Shutdown();
            };
            timer.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Failed) e.ApplicationExitCode = 1;
        StartupLog.Write($"退出（{e.ApplicationExitCode}）");
        base.OnExit(e);
        NativePreflight.HardExit(e.ApplicationExitCode);
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
