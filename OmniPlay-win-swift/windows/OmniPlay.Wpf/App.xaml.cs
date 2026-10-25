using System.Windows;
using System.Windows.Threading;

namespace OmniPlay.WpfSpike;

public partial class App : Application
{
    private static bool _smoke;

    /// 出过岔子。CI 冒烟靠退出码判断结果，弹窗那种形式在无人值守的机器上没有意义。
    public static bool Failed { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupLog.Reset();
        _smoke = e.Args.Contains("--smoke");
        StartupLog.Write($"启动：.NET {Environment.Version}，参数「{string.Join(" ", e.Args)}」");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportFailure("进程级未捕获异常", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        { StartupLog.Fail("后台任务异常", args.Exception); args.SetObserved(); };
        DispatcherUnhandledException += (_, args) =>
        { ReportFailure("界面线程未捕获异常", args.Exception); args.Handled = true; };

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
        Failed = true;
        StartupLog.Fail(stage, error);
        if (_smoke) return;
        MessageBox.Show($"{stage}失败：\n{error?.GetType().Name}：{error?.Message}\n\n详情见\n{StartupLog.FilePath}",
                        "觅影 Windows 演示", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
