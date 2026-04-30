using Avalonia;
using OmniPlay.Desktop.Diagnostics;
using OmniPlay.Desktop.Startup;

namespace OmniPlay.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppLog.Initialize();
        var commandLineOptions = CommandLineOptions.Initialize(args);
        AppLog.Info("应用启动");
        AppLog.Info($"命令行参数: {string.Join(' ', args)}");
        AppLog.Info(
            $"启动模式: {ResolveLaunchMode(commandLineOptions)}, " +
            $"PlayFile={commandLineOptions.PlayFilePath ?? "<none>"}, " +
            $"OverlayPlayFile={commandLineOptions.OverlayPlayFilePath ?? "<none>"}, " +
            $"CloseAfterSeconds={(commandLineOptions.CloseAfter?.TotalSeconds.ToString("0.###") ?? "<none>")}");

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            AppLog.Error("捕获到 AppDomain 未处理异常。", eventArgs.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            AppLog.Error("捕获到未观察任务异常。", eventArgs.Exception);
            eventArgs.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            AppLog.Info("应用正常退出");
        }
        catch (Exception ex)
        {
            AppLog.Error("应用启动或运行过程中发生致命异常。", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }

    private static string ResolveLaunchMode(CommandLineOptions options)
    {
        if (options.HasOverlayPlaybackRequest)
        {
            return "overlay-playback-diagnostic";
        }

        if (options.HasStandalonePlaybackRequest)
        {
            return "playback-diagnostic";
        }

        return "shell";
    }
}
