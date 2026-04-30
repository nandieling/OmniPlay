using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OmniPlay.Core.Models.Playback;
using OmniPlay.Core.ViewModels.Player;
using OmniPlay.Desktop.Diagnostics;
using OmniPlay.UI.Controls.Player;

namespace OmniPlay.Desktop.Windows;

public partial class StandalonePlayerWindow : Window
{
    private readonly DispatcherTimer controlsHideTimer;
    private readonly DispatcherTimer windowControlsHideTimer;
    private PlayerSurfaceHost? playerSurfaceHost;
    private DispatcherTimer? seekRepeatTimer;
    private Func<Task>? repeatSeekAction;
    private bool returnToShellInProgress;

    public StandalonePlayerWindow()
    {
        InitializeComponent();

        controlsHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        controlsHideTimer.Tick += ControlsHideTimer_OnTick;

        windowControlsHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        windowControlsHideTimer.Tick += WindowControlsHideTimer_OnTick;

        PositionSlider.AddHandler(PointerPressedEvent, PositionSlider_OnPointerPressed, RoutingStrategies.Tunnel, true);
        PositionSlider.AddHandler(PointerReleasedEvent, PositionSlider_OnPointerReleased, RoutingStrategies.Tunnel, true);
    }

    public bool ReturnToShellOnClose { get; set; }

    public event Func<Task>? ReturnToShellRequested;

    public async Task AttachAndOpenAsync(
        PlayerViewModel playerViewModel,
        string filePath,
        double? startPositionSeconds = null,
        CancellationToken cancellationToken = default)
    {
        await AttachAndOpenAsync(
            playerViewModel,
            new PlaybackOpenRequest(filePath),
            startPositionSeconds,
            cancellationToken);
    }

    public async Task AttachAndOpenAsync(
        PlayerViewModel playerViewModel,
        PlaybackOpenRequest request,
        double? startPositionSeconds = null,
        CancellationToken cancellationToken = default)
    {
        DataContext = playerViewModel;
        WindowState = WindowState.FullScreen;

        var hostHandle = await EnsurePlayerSurfaceHost().GetHandleAsync(cancellationToken);
        playerViewModel.AttachToHost(hostHandle);
        await playerViewModel.OpenAsync(request, startPositionSeconds, cancellationToken);
        ShowWindowControlsTemporarily();
        ShowControlsTemporarily();
    }

    private PlayerSurfaceHost EnsurePlayerSurfaceHost()
    {
        if (playerSurfaceHost is not null)
        {
            return playerSurfaceHost;
        }

        playerSurfaceHost = new PlayerSurfaceHost();
        playerSurfaceHost.NativePointerActivity += PlayerSurfaceHost_OnNativePointerActivity;
        playerSurfaceHost.NativePrimaryButtonPressed += PlayerSurfaceHost_OnNativePrimaryButtonPressed;
        PlayerSurfaceHostContainer.Content = playerSurfaceHost;
        return playerSurfaceHost;
    }

    private void PlayerSurfaceHost_OnNativePointerActivity(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(ShowControlsTemporarily);
    }

    private void PlayerSurfaceHost_OnNativePrimaryButtonPressed(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(ToggleControlsVisibility);
    }

    private void WindowControlsHideTimer_OnTick(object? sender, EventArgs e)
    {
        windowControlsHideTimer.Stop();
        WindowControlsPanel.IsVisible = false;
    }

    private void ShowWindowControlsTemporarily()
    {
        WindowControlsPanel.IsVisible = true;
        windowControlsHideTimer.Stop();
        windowControlsHideTimer.Start();
    }

    private void PositionSlider_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is PlayerViewModel playerViewModel)
        {
            playerViewModel.BeginSeekInteraction();
        }
    }

    private async void PositionSlider_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Slider slider && DataContext is PlayerViewModel playerViewModel)
        {
            await playerViewModel.CommitSeekAsync(slider.Value);
        }
    }

    private void PositionSlider_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is Slider slider
            && e.Property == RangeBase.ValueProperty
            && DataContext is PlayerViewModel playerViewModel)
        {
            playerViewModel.UpdateSeekPreview(slider.Value);
        }
    }

    private void FullscreenButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ToggleFullscreen();
        ShowControlsTemporarily();
    }

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ReturnToShellOnClose)
        {
            _ = RequestReturnToShellAsync();
            return;
        }

        Close();
    }

    private void MinimizeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState switch
        {
            WindowState.FullScreen => WindowState.Normal,
            WindowState.Maximized => WindowState.Normal,
            _ => WindowState.Maximized
        };

        ShowWindowControlsTemporarily();
    }

    private void Window_OnOpened(object? sender, EventArgs e)
    {
        ShowControlsTemporarily();
    }

    private void Window_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        ShowControlsTemporarily();
    }

    private void WindowControlsHotZone_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        ShowWindowControlsTemporarily();
    }

    private void TapSurface_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ToggleControlsVisibility();
            e.Handled = true;
        }
    }

    private void SeekBackwardButton_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        StartSeekRepeat(sender, e, ExecuteSeekBackwardAsync);
    }

    private void SeekForwardButton_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        StartSeekRepeat(sender, e, ExecuteSeekForwardAsync);
    }

    private void SeekRepeatButton_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        StopSeekRepeat();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void SeekRepeatButton_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        StopSeekRepeat();
    }

    private async void Window_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not PlayerViewModel playerViewModel)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                if (playerViewModel.TogglePlayPauseCommand.CanExecute(null))
                {
                    await playerViewModel.TogglePlayPauseCommand.ExecuteAsync(null);
                    ShowControlsTemporarily();
                    e.Handled = true;
                }
                break;
            case Key.Left:
                if (playerViewModel.SeekBackwardCommand.CanExecute(null))
                {
                    await playerViewModel.SeekBackwardCommand.ExecuteAsync(null);
                    ShowControlsTemporarily();
                    e.Handled = true;
                }
                break;
            case Key.Right:
                if (playerViewModel.SeekForwardCommand.CanExecute(null))
                {
                    await playerViewModel.SeekForwardCommand.ExecuteAsync(null);
                    ShowControlsTemporarily();
                    e.Handled = true;
                }
                break;
            case Key.Up:
            case Key.Add:
            case Key.OemPlus:
                if (playerViewModel.IncreaseVolumeCommand.CanExecute(null))
                {
                    await playerViewModel.IncreaseVolumeCommand.ExecuteAsync(null);
                    ShowControlsTemporarily();
                    e.Handled = true;
                }
                break;
            case Key.Down:
            case Key.Subtract:
            case Key.OemMinus:
                if (playerViewModel.DecreaseVolumeCommand.CanExecute(null))
                {
                    await playerViewModel.DecreaseVolumeCommand.ExecuteAsync(null);
                    ShowControlsTemporarily();
                    e.Handled = true;
                }
                break;
            case Key.G:
                if (playerViewModel.DecreaseSubtitleDelayCommand.CanExecute(null))
                {
                    await playerViewModel.DecreaseSubtitleDelayCommand.ExecuteAsync(null);
                    ShowControlsTemporarily();
                    e.Handled = true;
                }
                break;
            case Key.H:
                if (playerViewModel.IncreaseSubtitleDelayCommand.CanExecute(null))
                {
                    await playerViewModel.IncreaseSubtitleDelayCommand.ExecuteAsync(null);
                    ShowControlsTemporarily();
                    e.Handled = true;
                }
                break;
            case Key.M:
                if (playerViewModel.ToggleMuteCommand.CanExecute(null))
                {
                    await playerViewModel.ToggleMuteCommand.ExecuteAsync(null);
                    ShowControlsTemporarily();
                    e.Handled = true;
                }
                break;
            case Key.P:
            case Key.PageUp:
                if (playerViewModel.PlayPreviousEpisodeCommand.CanExecute(null))
                {
                    await playerViewModel.PlayPreviousEpisodeCommand.ExecuteAsync(null);
                    ShowControlsTemporarily();
                    e.Handled = true;
                }
                break;
            case Key.N:
            case Key.PageDown:
                if (playerViewModel.PlayNextEpisodeCommand.CanExecute(null))
                {
                    await playerViewModel.PlayNextEpisodeCommand.ExecuteAsync(null);
                    ShowControlsTemporarily();
                    e.Handled = true;
                }
                break;
            case Key.F:
            case Key.F11:
                ToggleFullscreen();
                ShowControlsTemporarily();
                e.Handled = true;
                break;
            case Key.Escape:
                if (WindowState == WindowState.FullScreen)
                {
                    WindowState = WindowState.Normal;
                    ShowControlsTemporarily();
                    e.Handled = true;
                }
                break;
        }
    }

    private void ToggleFullscreen()
    {
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
    }

    protected override void OnClosed(EventArgs e)
    {
        AppLog.Info(
            $"Standalone player window closed. ReturnToShellOnClose={ReturnToShellOnClose}, " +
            $"ReturnInProgress={returnToShellInProgress}, IsVisible={IsVisible}, WindowState={WindowState}");
        controlsHideTimer.Stop();
        windowControlsHideTimer.Stop();
        StopSeekRepeat();
        base.OnClosed(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        AppLog.Info(
            $"Standalone player window closing requested. Reason={e.CloseReason}, " +
            $"IsProgrammatic={e.IsProgrammatic}, ReturnToShellOnClose={ReturnToShellOnClose}, " +
            $"ReturnInProgress={returnToShellInProgress}, IsVisible={IsVisible}, " +
            $"HasOwner={Owner is not null}, WindowState={WindowState}");

        if (ShouldReturnToShellInsteadOfClose(e))
        {
            e.Cancel = true;
            AppLog.Info("Standalone player window close intercepted; returning to shell detail page.");
            _ = RequestReturnToShellAsync();
            return;
        }

        base.OnClosing(e);
    }

    private bool ShouldReturnToShellInsteadOfClose(WindowClosingEventArgs e)
    {
        if (!ReturnToShellOnClose || returnToShellInProgress)
        {
            return false;
        }

        return e.CloseReason is not WindowCloseReason.ApplicationShutdown
            and not WindowCloseReason.OSShutdown;
    }

    private async Task RequestReturnToShellAsync()
    {
        if (returnToShellInProgress)
        {
            AppLog.Info("Standalone player return-to-shell request ignored because another request is already running.");
            return;
        }

        returnToShellInProgress = true;
        try
        {
            AppLog.Info("Standalone player return-to-shell started.");
            controlsHideTimer.Stop();
            StopSeekRepeat();

            if (WindowState == WindowState.FullScreen)
            {
                AppLog.Info("Standalone player leaving fullscreen before return-to-shell.");
                WindowState = WindowState.Normal;
            }

            if (ReturnToShellRequested is not null)
            {
                AppLog.Info("Standalone player invoking return-to-shell handler.");
                await ReturnToShellRequested();
                AppLog.Info("Standalone player return-to-shell handler completed.");
            }
            else
            {
                AppLog.Info("Standalone player has no return-to-shell handler; hiding window directly.");
                Hide();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Standalone player return-to-shell failed.", ex);

            if (IsVisible)
            {
                Hide();
            }
        }
        finally
        {
            returnToShellInProgress = false;
            AppLog.Info("Standalone player return-to-shell finished.");
        }
    }

    private void ControlsHideTimer_OnTick(object? sender, EventArgs e)
    {
        controlsHideTimer.Stop();
        if (DataContext is PlayerViewModel { IsPlaying: true, IsPaused: false } playerViewModel)
        {
            playerViewModel.AreControlsVisible = false;
        }
    }

    private void ShowControlsTemporarily()
    {
        if (DataContext is not PlayerViewModel playerViewModel)
        {
            return;
        }

        playerViewModel.AreControlsVisible = true;
        controlsHideTimer.Stop();

        if (playerViewModel.IsPlaying && !playerViewModel.IsPaused)
        {
            controlsHideTimer.Start();
        }
    }

    private void ToggleControlsVisibility()
    {
        if (DataContext is not PlayerViewModel playerViewModel)
        {
            return;
        }

        playerViewModel.AreControlsVisible = !playerViewModel.AreControlsVisible;
        controlsHideTimer.Stop();

        if (playerViewModel.AreControlsVisible && playerViewModel.IsPlaying && !playerViewModel.IsPaused)
        {
            controlsHideTimer.Start();
        }
    }

    private void StartSeekRepeat(object? sender, PointerPressedEventArgs e, Func<Task> seekAction)
    {
        StopSeekRepeat();
        repeatSeekAction = seekAction;
        _ = seekAction();
        ShowControlsTemporarily();

        seekRepeatTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        seekRepeatTimer.Tick += SeekRepeatTimer_OnTick;
        seekRepeatTimer.Start();

        if (sender is IInputElement inputElement)
        {
            e.Pointer.Capture(inputElement);
        }

        e.Handled = true;
    }

    private async void SeekRepeatTimer_OnTick(object? sender, EventArgs e)
    {
        if (repeatSeekAction is not null)
        {
            await repeatSeekAction();
            ShowControlsTemporarily();
        }
    }

    private void StopSeekRepeat()
    {
        if (seekRepeatTimer is not null)
        {
            seekRepeatTimer.Stop();
            seekRepeatTimer.Tick -= SeekRepeatTimer_OnTick;
            seekRepeatTimer = null;
        }

        repeatSeekAction = null;
    }

    private Task ExecuteSeekBackwardAsync()
    {
        return DataContext is PlayerViewModel playerViewModel &&
               playerViewModel.SeekBackwardCommand.CanExecute(null)
            ? playerViewModel.SeekBackwardCommand.ExecuteAsync(null)
            : Task.CompletedTask;
    }

    private Task ExecuteSeekForwardAsync()
    {
        return DataContext is PlayerViewModel playerViewModel &&
               playerViewModel.SeekForwardCommand.CanExecute(null)
            ? playerViewModel.SeekForwardCommand.ExecuteAsync(null)
            : Task.CompletedTask;
    }
}
