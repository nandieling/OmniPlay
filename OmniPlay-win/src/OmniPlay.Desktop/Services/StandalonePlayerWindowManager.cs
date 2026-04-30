using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Core.Models.Playback;
using OmniPlay.Core.ViewModels.Player;
using OmniPlay.Desktop.Diagnostics;
using OmniPlay.Desktop.Windows;

namespace OmniPlay.Desktop.Services;

public sealed class StandalonePlayerWindowManager
{
    private readonly PlayerViewModel playerViewModel;
    private StandalonePlayerWindow? playerWindow;
    private Func<PlaybackCloseResult, Task>? currentPlaybackClosedHandler;
    private string? currentFilePath;
    private TaskCompletionSource? closeCompletionSource;
    private bool completingSession;
    private bool returningToShell;

    public StandalonePlayerWindowManager(PlayerViewModel playerViewModel)
    {
        this.playerViewModel = playerViewModel;
    }

    public async Task ShowAsync(
        PlaybackOpenRequest request,
        Func<PlaybackCloseResult, Task>? onPlaybackClosed = null,
        double? startPositionSeconds = null,
        bool replaceCurrentSession = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (playerWindow is null)
        {
            AppLog.Info("Creating standalone player window for shell playback.");
            playerWindow = new StandalonePlayerWindow
            {
                ReturnToShellOnClose = true
            };
            playerWindow.ReturnToShellRequested += OnPlayerWindowReturnToShellRequestedAsync;
            playerWindow.Closed += OnPlayerWindowClosed;
        }

        var window = playerWindow;
        if (window is null)
        {
            return;
        }

        var displayPath = request.EffectiveDisplayPath;
        if (window.IsVisible && IsSameFile(currentFilePath, displayPath))
        {
            AppLog.Info($"Standalone player already visible for requested file. File={displayPath}");
            if (onPlaybackClosed is not null)
            {
                currentPlaybackClosedHandler = onPlaybackClosed;
            }

            window.Activate();
            return;
        }

        if (!replaceCurrentSession)
        {
            await CompleteActiveSessionAsync(cancellationToken);
        }

        currentFilePath = displayPath;
        currentPlaybackClosedHandler = onPlaybackClosed;

        if (!window.IsVisible)
        {
            var owner = ResolveShellWindow(window);
            if (owner is not null)
            {
                AppLog.Info(
                    $"Showing standalone player window with shell owner. OwnerVisible={owner.IsVisible}, " +
                    $"OwnerState={owner.WindowState}, File={displayPath}");
                window.Show(owner);
            }
            else
            {
                AppLog.Info($"Showing standalone player window without owner. File={displayPath}");
                window.Show();
            }
        }

        await window.AttachAndOpenAsync(playerViewModel, request, startPositionSeconds, cancellationToken);
        window.Activate();
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        var window = playerWindow;
        if (window is null)
        {
            AppLog.Info("Standalone player manager close requested with no window; completing active session.");
            await CompleteActiveSessionAsync(cancellationToken);
            return;
        }

        if (!window.IsVisible)
        {
            AppLog.Info("Standalone player manager close requested while window is hidden; completing active session.");
            await CompleteActiveSessionAsync(cancellationToken);
            window.Closed -= OnPlayerWindowClosed;
            playerWindow = null;
            return;
        }

        AppLog.Info("Standalone player manager close requested; returning window to shell.");
        closeCompletionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await ReturnPlayerWindowToShellAsync(window, cancellationToken);
        await closeCompletionSource.Task.WaitAsync(cancellationToken);
    }

    private Task OnPlayerWindowReturnToShellRequestedAsync()
    {
        AppLog.Info("Standalone player window requested return to shell.");
        var window = playerWindow;
        return window is null
            ? Task.CompletedTask
            : ReturnPlayerWindowToShellAsync(window);
    }

    private async void OnPlayerWindowClosed(object? sender, EventArgs e)
    {
        AppLog.Info("Standalone player window actually closed; completing active playback session.");
        try
        {
            await CompleteActiveSessionAsync();
        }
        finally
        {
            if (sender is StandalonePlayerWindow window)
            {
                window.ReturnToShellRequested -= OnPlayerWindowReturnToShellRequestedAsync;
                window.Closed -= OnPlayerWindowClosed;
            }

            if (ReferenceEquals(playerWindow, sender))
            {
                playerWindow = null;
            }

            closeCompletionSource?.TrySetResult();
            closeCompletionSource = null;
            ActivateShellWindow();
        }
    }

    private async Task ReturnPlayerWindowToShellAsync(
        StandalonePlayerWindow window,
        CancellationToken cancellationToken = default)
    {
        if (returningToShell)
        {
            AppLog.Info("Standalone player return-to-shell already in progress; completing waiter.");
            closeCompletionSource?.TrySetResult();
            return;
        }

        returningToShell = true;
        try
        {
            AppLog.Info(
                $"Standalone player return-to-shell manager started. WindowVisible={window.IsVisible}, " +
                $"WindowState={window.WindowState}, CurrentFile={currentFilePath ?? playerViewModel.CurrentFilePath}");
            await CompleteActiveSessionAsync(cancellationToken);

            if (window.IsVisible)
            {
                if (window.WindowState == WindowState.FullScreen)
                {
                    AppLog.Info("Standalone player manager leaving fullscreen before hide.");
                    window.WindowState = WindowState.Normal;
                }

                AppLog.Info("Standalone player manager hiding player window.");
                window.Hide();
            }

            ActivateShellWindow();
            closeCompletionSource?.TrySetResult();
            AppLog.Info("Standalone player return-to-shell manager completed.");
        }
        finally
        {
            returningToShell = false;
        }
    }

    private async Task CompleteActiveSessionAsync(CancellationToken cancellationToken = default)
    {
        if (completingSession)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(currentFilePath) &&
            string.IsNullOrWhiteSpace(playerViewModel.CurrentFilePath) &&
            currentPlaybackClosedHandler is null)
        {
            return;
        }

        completingSession = true;
        try
        {
            AppLog.Info(
                $"Completing standalone playback session. CurrentFile={currentFilePath ?? playerViewModel.CurrentFilePath}, " +
                $"Position={playerViewModel.CurrentPositionSeconds:0.###}, Duration={playerViewModel.DurationSeconds:0.###}");
            var snapshot = new PlaybackCloseResult(
                string.IsNullOrWhiteSpace(currentFilePath) ? playerViewModel.CurrentFilePath : currentFilePath,
                Math.Max(playerViewModel.CurrentPositionSeconds, 0),
                Math.Max(playerViewModel.DurationSeconds, 0));
            var callback = currentPlaybackClosedHandler;

            currentFilePath = null;
            currentPlaybackClosedHandler = null;

            await playerViewModel.StopAsync(cancellationToken);

            if (callback is not null)
            {
                await callback(snapshot);
            }

            AppLog.Info("Standalone playback session completed.");
        }
        finally
        {
            completingSession = false;
        }
    }

    private static bool IsSameFile(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        if (MediaSourcePathResolver.IsRemoteHttpUrl(left) || MediaSourcePathResolver.IsRemoteHttpUrl(right))
        {
            return MediaSourcePathResolver.IsRemoteHttpUrl(left) &&
                   MediaSourcePathResolver.IsRemoteHttpUrl(right) &&
                   string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal);
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }

    private static Window? ResolveShellWindow(Window playerWindow)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        var mainWindow = desktop.MainWindow;
        return mainWindow is not null && !ReferenceEquals(mainWindow, playerWindow)
            ? mainWindow
            : null;
    }

    private static void ActivateShellWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppLog.Info("Cannot activate shell window because classic desktop lifetime is unavailable.");
            return;
        }

        var mainWindow = desktop.MainWindow;
        if (mainWindow is null || !mainWindow.IsVisible)
        {
            AppLog.Info("Cannot activate shell window because main window is unavailable or hidden.");
            return;
        }

        if (mainWindow.WindowState == WindowState.Minimized)
        {
            mainWindow.WindowState = WindowState.Normal;
        }

        AppLog.Info($"Activating shell window. State={mainWindow.WindowState}");
        mainWindow.Activate();
    }
}
