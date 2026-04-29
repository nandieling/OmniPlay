using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using OmniPlay.Core.ViewModels;
using OmniPlay.Core.ViewModels.Player;
using OmniPlay.Desktop.Bootstrap;
using OmniPlay.Desktop.Diagnostics;
using OmniPlay.Desktop.Startup;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Desktop.Windows;
using OmniPlay.UI.Views.Shell;

namespace OmniPlay.Desktop;

public partial class App : Application
{
    public IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            Services = ServiceRegistration.BuildServices();
            AppLog.Info("依赖注入容器已创建");

            Services.GetRequiredService<SqliteDatabase>().EnsureInitialized();
            AppLog.Info("SQLite 数据库初始化完成");

            AppLog.Info("媒体库初始化加载完成");
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var launchOptions = CommandLineOptions.Current;
                var mainWindow = CreateMainWindowForLaunchMode();
                desktop.ShutdownMode = launchOptions.HasPlaybackRequest || launchOptions.HasOverlayPlaybackRequest
                    ? ShutdownMode.OnMainWindowClose
                    : ShutdownMode.OnExplicitShutdown;
                desktop.MainWindow = mainWindow;

                if (!launchOptions.HasPlaybackRequest && !launchOptions.HasOverlayPlaybackRequest)
                {
                    mainWindow.Closing += async (_, e) =>
                    {
                        var overlayOpen = mainWindow.DataContext is ShellViewModel shellForOverlay &&
                                          shellForOverlay.PosterWall.IsPlayerOverlayOpen;

                        AppLog.Info(
                            $"Shell main window closing requested. Reason={e.CloseReason}, " +
                            $"IsProgrammatic={e.IsProgrammatic}, OverlayOpen={overlayOpen}");

                        if (e.CloseReason != WindowCloseReason.WindowClosing || !overlayOpen)
                        {
                            return;
                        }

                        e.Cancel = true;
                        AppLog.Info("Shell main window close intercepted while player overlay is open; returning to detail page.");

                        try
                        {
                            if (mainWindow.DataContext is ShellViewModel shellForClose &&
                                shellForClose.PosterWall.ClosePlayerOverlayCommand.CanExecute(null))
                            {
                                await shellForClose.PosterWall.ClosePlayerOverlayCommand.ExecuteAsync(null);
                            }

                            mainWindow.Activate();
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("Failed to close player overlay while intercepting shell main window close.", ex);
                        }
                    };

                    mainWindow.Closed += (_, _) =>
                    {
                        AppLog.Info("Shell main window closed; shutting down application.");
                        desktop.Shutdown();
                    };
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("框架初始化阶段发生异常。", ex);
            throw;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private Avalonia.Controls.Window CreateMainWindowForLaunchMode()
    {
        var launchOptions = CommandLineOptions.Current;
        if (launchOptions.HasOverlayPlaybackRequest)
        {
            return CreateOverlayPlaybackDiagnosticWindow(launchOptions);
        }

        if (launchOptions.HasPlaybackRequest)
        {
            return CreatePlaybackDiagnosticWindow(launchOptions);
        }

        var window = new MainWindow
        {
            DataContext = Services!.GetRequiredService<ShellViewModel>()
        };
        AppLog.Info("主窗口已创建");
        window.Opened += async (_, _) =>
        {
            if (window.DataContext is not ShellViewModel shellViewModel)
            {
                return;
            }

            try
            {
                AppLog.Info("Shell media library initialization started.");
                await shellViewModel.PosterWall.LoadAsync();
                AppLog.Info("Shell media library initialization completed.");

                _ = shellViewModel.PosterWall.RunStartupScanIfEnabledAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error("Shell window initialization failed.", ex);
            }
        };
        return window;
    }

    private StandalonePlayerWindow CreatePlaybackDiagnosticWindow(CommandLineOptions launchOptions)
    {
        var playbackFilePath = launchOptions.PlayFilePath!;
        if (!File.Exists(playbackFilePath))
        {
            throw new FileNotFoundException($"命令行播放诊断文件不存在: {playbackFilePath}", playbackFilePath);
        }

        var playerViewModel = Services!.GetRequiredService<PlayerViewModel>();
        var window = new StandalonePlayerWindow();

        window.Opened += async (_, _) =>
        {
            try
            {
                AppLog.Info($"命令行播放诊断启动: {playbackFilePath}");
                await window.AttachAndOpenAsync(playerViewModel, playbackFilePath);
                AppLog.Info(CreatePlaybackSnapshot("opened", playerViewModel));

                if (launchOptions.CloseAfter.HasValue)
                {
                    _ = RunPlaybackDiagnosticCloseAsync(window, playerViewModel, launchOptions.CloseAfter.Value);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("命令行播放诊断启动失败。", ex);
                Dispatcher.UIThread.Post(window.Close);
            }
        };

        window.Closed += async (_, _) =>
        {
            AppLog.Info(CreatePlaybackSnapshot("closed", playerViewModel));
            await playerViewModel.StopAsync();
        };

        AppLog.Info("命令行播放诊断窗口已创建");
        return window;
    }

    private MainWindow CreateOverlayPlaybackDiagnosticWindow(CommandLineOptions launchOptions)
    {
        var playbackFilePath = launchOptions.OverlayPlayFilePath!;
        if (!File.Exists(playbackFilePath))
        {
            throw new FileNotFoundException($"命令行覆盖层诊断文件不存在: {playbackFilePath}", playbackFilePath);
        }

        var shellViewModel = Services!.GetRequiredService<ShellViewModel>();
        var playerViewModel = shellViewModel.PosterWall.Player;
        var window = new MainWindow
        {
            DataContext = shellViewModel
        };

        window.Opened += async (_, _) =>
        {
            try
            {
                AppLog.Info($"命令行覆盖层诊断启动: {playbackFilePath}");
                shellViewModel.PosterWall.StartOverlayPlaybackDiagnostic(playbackFilePath);
                await WaitForPlaybackStartAsync(playerViewModel);
                AppLog.Info(CreatePlaybackSnapshot("overlay-opened", playerViewModel));

                if (launchOptions.CloseAfter.HasValue)
                {
                    _ = RunOverlayPlaybackDiagnosticCloseAsync(
                        window,
                        shellViewModel.PosterWall,
                        playerViewModel,
                        launchOptions.CloseAfter.Value);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("命令行覆盖层诊断启动失败。", ex);
                Dispatcher.UIThread.Post(window.Close);
            }
        };

        window.Closed += async (_, _) =>
        {
            if (shellViewModel.PosterWall.IsPlayerOverlayOpen)
            {
                await shellViewModel.PosterWall.CloseOverlayPlaybackDiagnosticAsync();
            }
            else
            {
                await playerViewModel.StopAsync();
            }
        };

        AppLog.Info("命令行覆盖层诊断主窗口已创建");
        return window;
    }

    private static async Task RunPlaybackDiagnosticCloseAsync(
        StandalonePlayerWindow window,
        PlayerViewModel playerViewModel,
        TimeSpan closeAfter)
    {
        try
        {
            var firstSnapshotDelay = closeAfter > TimeSpan.FromSeconds(1)
                ? TimeSpan.FromSeconds(1)
                : closeAfter;

            await Task.Delay(firstSnapshotDelay);
            AppLog.Info(CreatePlaybackSnapshot("snapshot-1", playerViewModel));

            var remaining = closeAfter - firstSnapshotDelay;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining);
            }

            AppLog.Info(CreatePlaybackSnapshot("snapshot-final", playerViewModel));
        }
        catch (Exception ex)
        {
            AppLog.Error("命令行播放诊断自动关闭阶段发生异常。", ex);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (window.IsVisible)
                {
                    window.Close();
                }
            });
        }
    }

    private static async Task RunOverlayPlaybackDiagnosticCloseAsync(
        MainWindow window,
        OmniPlay.Core.ViewModels.Library.PosterWallViewModel posterWallViewModel,
        PlayerViewModel playerViewModel,
        TimeSpan closeAfter)
    {
        try
        {
            var firstSnapshotDelay = closeAfter > TimeSpan.FromSeconds(1)
                ? TimeSpan.FromSeconds(1)
                : closeAfter;

            await Task.Delay(firstSnapshotDelay);
            AppLog.Info(CreatePlaybackSnapshot("overlay-snapshot-1", playerViewModel));

            var remaining = closeAfter - firstSnapshotDelay;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining);
            }

            AppLog.Info(CreatePlaybackSnapshot("overlay-snapshot-final", playerViewModel));
        }
        catch (Exception ex)
        {
            AppLog.Error("命令行覆盖层诊断自动关闭阶段发生异常。", ex);
        }
        finally
        {
            var closingSnapshot = CreatePlaybackSnapshot("overlay-closed", playerViewModel);

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await posterWallViewModel.CloseOverlayPlaybackDiagnosticAsync();
                AppLog.Info(closingSnapshot);

                if (window.IsVisible)
                {
                    window.Close();
                }
            });
        }
    }

    private static async Task WaitForPlaybackStartAsync(PlayerViewModel playerViewModel, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        while (!timeout.Token.IsCancellationRequested)
        {
            if (playerViewModel.IsPlaying && playerViewModel.IsAvailable)
            {
                return;
            }

            await Task.Delay(50, timeout.Token);
        }

        throw new TimeoutException("等待播放器进入播放状态超时。");
    }

    private static string CreatePlaybackSnapshot(string stage, PlayerViewModel playerViewModel)
    {
        return $"PlaybackDiagnostic[{stage}] " +
               $"File={playerViewModel.CurrentFilePath}, " +
               $"Status={playerViewModel.StatusMessage}, " +
               $"Backend={playerViewModel.BackendName}, " +
               $"IsAvailable={playerViewModel.IsAvailable}, " +
               $"IsPlaying={playerViewModel.IsPlaying}, " +
               $"IsPaused={playerViewModel.IsPaused}, " +
               $"Position={playerViewModel.CurrentPositionSeconds:0.###}, " +
               $"Duration={playerViewModel.DurationSeconds:0.###}";
    }
}
