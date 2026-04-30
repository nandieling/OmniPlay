using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmniPlay.Core.Models.Playback;
using OmniPlay.Core.ViewModels.Library;
using OmniPlay.Core.ViewModels.Player;
using OmniPlay.UI.Controls.Player;

namespace OmniPlay.UI.Views.Library;

public partial class PosterWallView : UserControl
{
    private const int VirtualKeyEnter = 0x0D;
    private const int VirtualKeyEscape = 0x1B;
    private const int VirtualKeySpace = 0x20;
    private const int VirtualKeyLeft = 0x25;
    private const int VirtualKeyUp = 0x26;
    private const int VirtualKeyRight = 0x27;
    private const int VirtualKeyDown = 0x28;
    private const int VirtualKeyAdd = 0x6B;
    private const int VirtualKeySubtract = 0x6D;
    private const int VirtualKeyOemPlus = 0xBB;
    private const int VirtualKeyOemMinus = 0xBD;
    private const double LibraryPosterBaseItemWidth = 224;
    private const double LibraryPosterTargetColumns = 8;
    private const double OverlayWindowControlsHotZoneHeight = 48;
    private const int OverlaySpaceShortcutDeduplicationMilliseconds = 180;
    private static readonly TimeSpan OverlayWindowControlsMonitorInterval = TimeSpan.FromMilliseconds(75);

    private readonly DispatcherTimer overlayControlsHideTimer;
    private readonly DispatcherTimer overlayWindowControlsHideTimer;
    private readonly DispatcherTimer overlayVolumePopupHideTimer;
    private WrapPanel? libraryItemsWrapPanel;
    private PosterWallViewModel? currentViewModel;
    private PlayerSurfaceHost? overlayPlayerSurfaceHost;
    private WindowState? windowStateBeforeOverlay;
    private SystemDecorations? windowDecorationsBeforeOverlayFullscreen;
    private Window? overlayKeyboardWindow;
    private bool openingPlayback;
    private bool isOverlayVolumeInteractionActive;
    private bool overlayFullscreenApplied;
    private bool suppressNextOverlayMaximizedToFullscreen;
    private long lastOverlaySpaceShortcutTick;

    public PosterWallView()
    {
        InitializeComponent();

        overlayControlsHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        overlayControlsHideTimer.Tick += OverlayControlsHideTimer_OnTick;

        overlayWindowControlsHideTimer = new DispatcherTimer
        {
            Interval = OverlayWindowControlsMonitorInterval
        };
        overlayWindowControlsHideTimer.Tick += OverlayWindowControlsHideTimer_OnTick;

        overlayVolumePopupHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        overlayVolumePopupHideTimer.Tick += OverlayVolumePopupHideTimer_OnTick;

        OverlayPositionSlider.AddHandler(PointerPressedEvent, OverlaySlider_OnPointerPressed, RoutingStrategies.Tunnel, true);
        OverlayPositionSlider.AddHandler(PointerReleasedEvent, OverlaySlider_OnPointerReleased, RoutingStrategies.Tunnel, true);
        OverlayVolumeSlider.AddHandler(PointerPressedEvent, OverlayVolumeSlider_OnPointerPressed, RoutingStrategies.Tunnel, true);
        OverlayVolumeSlider.AddHandler(PointerReleasedEvent, OverlayVolumeSlider_OnPointerReleased, RoutingStrategies.Tunnel, true);

        HomeContentStack.SizeChanged += HomeContentStack_OnSizeChanged;
        LibraryItemsControl.AttachedToVisualTree += LibraryItemsControl_OnAttachedToVisualTree;
        DataContextChanged += OnDataContextChanged;
    }

    private void HomeContentStack_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateLibraryPosterItemWidth();
    }

    private void LibraryItemsControl_OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Dispatcher.UIThread.Post(UpdateLibraryPosterItemWidth, DispatcherPriority.Loaded);
    }

    private void UpdateLibraryPosterItemWidth()
    {
        var availableWidth = HomeContentStack.Bounds.Width;
        if (availableWidth <= 0)
        {
            return;
        }

        libraryItemsWrapPanel ??= LibraryItemsControl
            .GetVisualDescendants()
            .OfType<WrapPanel>()
            .FirstOrDefault();

        if (libraryItemsWrapPanel is null)
        {
            Dispatcher.UIThread.Post(UpdateLibraryPosterItemWidth, DispatcherPriority.Loaded);
            return;
        }

        var itemWidth = Math.Max(
            LibraryPosterBaseItemWidth,
            Math.Floor(availableWidth / LibraryPosterTargetColumns));

        if (Math.Abs(libraryItemsWrapPanel.ItemWidth - itemWidth) > 0.5)
        {
            libraryItemsWrapPanel.ItemWidth = itemWidth;
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (currentViewModel is not null)
        {
            currentViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            currentViewModel.Player.PropertyChanged -= OnPlayerPropertyChanged;
        }

        CloseOverlayPopups();

        currentViewModel = DataContext as PosterWallViewModel;

        if (currentViewModel is not null)
        {
            currentViewModel.PropertyChanged += OnViewModelPropertyChanged;
            currentViewModel.Player.PropertyChanged += OnPlayerPropertyChanged;

            if (currentViewModel.IsPlayerOverlayOpen)
            {
                EnterOverlayPlaybackMode();
            }

            UpdateOverlayPopupState();
        }
    }

    private async void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PosterWallViewModel.IsPlayerOverlayOpen) && currentViewModel is not null)
        {
            if (currentViewModel.IsPlayerOverlayOpen)
            {
                EnterOverlayPlaybackMode();
            }
            else
            {
                ExitOverlayPlaybackMode();
            }

            UpdateOverlayPopupState();
        }

        if (e.PropertyName == nameof(PosterWallViewModel.PendingPlaybackFilePath)
            && currentViewModel is not null
            && currentViewModel.IsPlayerOverlayOpen
            && !string.IsNullOrWhiteSpace(currentViewModel.PendingPlaybackFilePath)
            && !openingPlayback)
        {
            openingPlayback = true;

            try
            {
                var handle = await EnsureOverlayPlayerSurfaceHost().GetHandleAsync();
                currentViewModel.Player.AttachToHost(handle);
                await currentViewModel.Player.OpenAsync(
                    new PlaybackOpenRequest(
                        currentViewModel.PendingPlaybackFilePath,
                        currentViewModel.PendingPlaybackDisplayPath),
                    currentViewModel.PendingPlaybackStartPositionSeconds);
                ShowOverlayControlsTemporarily();
                UpdateOverlayPopupState();
            }
            finally
            {
                openingPlayback = false;
            }
        }
    }

    private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (currentViewModel is null)
        {
            return;
        }

        if (e.PropertyName is nameof(PlayerViewModel.AreControlsVisible)
            or nameof(PlayerViewModel.IsPlaying)
            or nameof(PlayerViewModel.IsPaused))
        {
            UpdateOverlayPopupState();
        }

        if (!currentViewModel.IsPlayerOverlayOpen)
        {
            return;
        }

        if (e.PropertyName is nameof(PlayerViewModel.IsPlaying) or nameof(PlayerViewModel.IsPaused))
        {
            ShowOverlayControlsTemporarily();
        }
    }

    private PlayerSurfaceHost EnsureOverlayPlayerSurfaceHost()
    {
        if (overlayPlayerSurfaceHost is not null)
        {
            return overlayPlayerSurfaceHost;
        }

        overlayPlayerSurfaceHost = new PlayerSurfaceHost();
        overlayPlayerSurfaceHost.NativePointerMoved += OverlayPlayerSurfaceHost_OnNativePointerMoved;
        overlayPlayerSurfaceHost.NativePrimaryButtonPressed += OverlayPlayerSurfaceHost_OnNativePrimaryButtonPressed;
        overlayPlayerSurfaceHost.NativeKeyDown += OverlayPlayerSurfaceHost_OnNativeKeyDown;
        OverlayPlayerSurfaceHostContainer.Content = overlayPlayerSurfaceHost;
        return overlayPlayerSurfaceHost;
    }

    private void OverlayPlayerSurfaceHost_OnNativePointerMoved(object? sender, NativePointerActivityEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ShowOverlayControlsTemporarily();

            if (IsOverlayFullscreen()
                && e.Y <= OverlayWindowControlsHotZoneHeight
                && !OverlayWindowControlsPopup.IsOpen)
            {
                ShowOverlayWindowControlsTemporarily();
            }
        });
    }

    private void OverlayPlayerSurfaceHost_OnNativePrimaryButtonPressed(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(ToggleOverlayControlsVisibility);
    }

    private void OverlayPlayerSurfaceHost_OnNativeKeyDown(object? sender, NativeKeyActivityEventArgs e)
    {
        var key = MapNativeVirtualKey(e.VirtualKey);
        if (key is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(async () =>
        {
            if (!TryBeginOverlayShortcut(key.Value))
            {
                return;
            }

            await HandleOverlayShortcutKeyAsync(key.Value);
        });
    }

    private void OverlayWindowControlsHotZone_OnPointerActivity(object? sender, PointerEventArgs e)
    {
        ShowOverlayWindowControlsTemporarily();
    }

    private void OverlayWindowControlsPanel_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        ShowOverlayWindowControlsTemporarily();
    }

    private void OverlayWindowControlsPanel_OnPointerExited(object? sender, PointerEventArgs e)
    {
        EnsureOverlayWindowControlsMonitorStarted();
    }

    private void OverlayWindowControlsPanel_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        ShowOverlayWindowControlsTemporarily();
    }

    private void OverlayAudioButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CloseOverlayVolumePopup();
        OverlaySubtitlePopup.IsOpen = false;
        OverlaySubtitleSizePopup.IsOpen = false;
        OverlayAudioPopup.IsOpen = !OverlayAudioPopup.IsOpen;
        ShowOverlayControlsTemporarily();
    }

    private void OverlaySubtitleButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CloseOverlayVolumePopup();
        OverlayAudioPopup.IsOpen = false;
        OverlaySubtitleSizePopup.IsOpen = false;
        OverlaySubtitlePopup.IsOpen = !OverlaySubtitlePopup.IsOpen;
        ShowOverlayControlsTemporarily();
    }

    private async void OverlayAudioTrackButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PlayerTrackInfo track && currentViewModel is not null)
        {
            await currentViewModel.Player.SelectAudioTrackAsync(track);
            OverlayAudioPopup.IsOpen = false;
            ShowOverlayControlsTemporarily();
        }
    }

    private async void OverlaySubtitleTrackButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PlayerTrackInfo track && currentViewModel is not null)
        {
            await currentViewModel.Player.SelectSubtitleTrackAsync(track);
            OverlaySubtitlePopup.IsOpen = false;
            OverlaySubtitleSizePopup.IsOpen = false;
            ShowOverlayControlsTemporarily();
        }
    }

    private void OverlaySubtitleSizeMenuButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OverlaySubtitleSizePopup.IsOpen = !OverlaySubtitleSizePopup.IsOpen;
        ShowOverlayControlsTemporarily();
    }

    private void OverlaySubtitleSizeButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PlayerSubtitleSizeOption option && currentViewModel is not null)
        {
            currentViewModel.Player.SelectedSubtitleSizeOption = option;
            OverlaySubtitleSizePopup.IsOpen = false;
            ShowOverlayControlsTemporarily();
        }
    }

    private void OverlayWindowControlsHideTimer_OnTick(object? sender, EventArgs e)
    {
        if (!ShouldMonitorOverlayWindowControls())
        {
            SetOverlayWindowControlsVisible(false);
            StopOverlayWindowControlsMonitor();
            return;
        }

        if (!OverlayWindowControlsPopup.IsOpen)
        {
            return;
        }

        if (IsPointerOverOverlayWindowControlsHotZone())
        {
            return;
        }

        SetOverlayWindowControlsVisible(false);
    }

    private void ShowOverlayWindowControlsTemporarily()
    {
        if (!ShouldMonitorOverlayWindowControls())
        {
            SetOverlayWindowControlsVisible(false);
            StopOverlayWindowControlsMonitor();
            return;
        }

        SetOverlayWindowControlsVisible(true);
        EnsureOverlayWindowControlsMonitorStarted();
    }

    private void SetOverlayWindowControlsVisible(bool isVisible)
    {
        var shouldShow = isVisible && CanShowOverlayPopups();

        OverlayWindowControlsPanel.IsVisible = shouldShow;
        OverlayWindowControlsPopup.IsOpen = shouldShow;

        UpdateOverlayWindowControlsMonitorState();
    }

    private void EnsureOverlayWindowControlsMonitorStarted()
    {
        if (ShouldMonitorOverlayWindowControls() && !overlayWindowControlsHideTimer.IsEnabled)
        {
            overlayWindowControlsHideTimer.Start();
        }
    }

    private void StopOverlayWindowControlsMonitor()
    {
        overlayWindowControlsHideTimer.Stop();
    }

    private void UpdateOverlayWindowControlsMonitorState()
    {
        var shouldMonitor = ShouldMonitorOverlayWindowControls();
        OverlayWindowControlsHotZonePopup.IsOpen = shouldMonitor && !OverlayWindowControlsPopup.IsOpen;

        if (shouldMonitor)
        {
            EnsureOverlayWindowControlsMonitorStarted();
            return;
        }

        StopOverlayWindowControlsMonitor();
    }

    private bool ShouldMonitorOverlayWindowControls()
    {
        return CanShowOverlayPopups();
    }

    private bool IsPointerOverOverlayWindowControlsHotZone()
    {
        if (!OperatingSystem.IsWindows() ||
            TopLevel.GetTopLevel(this) is not Window window ||
            window.TryGetPlatformHandle()?.Handle is not { } handle ||
            handle == IntPtr.Zero ||
            !GetWindowRect(handle, out var windowRect) ||
            !GetCursorPos(out var cursor))
        {
            return false;
        }

        var scaling = window.DesktopScaling;
        var controlsHeight = (int)Math.Ceiling(OverlayWindowControlsHotZoneHeight * scaling);
        var top = windowRect.Top;
        var bottom = windowRect.Top + controlsHeight;

        return cursor.X >= windowRect.Left &&
               cursor.X <= windowRect.Right &&
               cursor.Y >= top &&
               cursor.Y <= bottom;
    }

    private void UpdateOverlayPopupState()
    {
        var isOverlayOpen = currentViewModel?.IsPlayerOverlayOpen == true;
        var canShowOverlayPopups = isOverlayOpen && CanShowOverlayPopups();
        var player = currentViewModel?.Player;

        OverlayControlsPopup.IsOpen = canShowOverlayPopups && player?.AreControlsVisible == true;
        OverlayStatusPopup.IsOpen = canShowOverlayPopups && player?.IsPlaying != true;
        OverlayBottomHotZonePopup.IsOpen = canShowOverlayPopups && player?.AreControlsVisible != true;

        if (!canShowOverlayPopups || player?.AreControlsVisible != true)
        {
            CloseOverlayVolumePopup();
            OverlayAudioPopup.IsOpen = false;
            OverlaySubtitlePopup.IsOpen = false;
            OverlaySubtitleSizePopup.IsOpen = false;
        }

        if (!canShowOverlayPopups)
        {
            SetOverlayWindowControlsVisible(false);
        }
    }

    private bool CanShowOverlayPopups()
    {
        return currentViewModel?.IsPlayerOverlayOpen == true &&
            TopLevel.GetTopLevel(this) is Window { WindowState: not WindowState.Minimized, IsActive: true };
    }

    private void CloseOverlayPopups()
    {
        OverlayControlsPopup.IsOpen = false;
        OverlayStatusPopup.IsOpen = false;
        OverlayBottomHotZonePopup.IsOpen = false;
        OverlayWindowControlsHotZonePopup.IsOpen = false;
        OverlayAudioPopup.IsOpen = false;
        OverlaySubtitlePopup.IsOpen = false;
        OverlaySubtitleSizePopup.IsOpen = false;
        CloseOverlayVolumePopup();
        SetOverlayWindowControlsVisible(false);
    }

    private bool IsOverlayFullscreen()
    {
        return TopLevel.GetTopLevel(this) is Window { WindowState: WindowState.FullScreen };
    }

    private void OverlayMinimizeButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    private void OverlayMaximizeButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ToggleOverlayFullscreen();
        e.Handled = true;
    }

    private void OverlayMaximizeButton_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        ToggleOverlayFullscreen();
        e.Handled = true;
    }

    private async void OverlayCloseButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (currentViewModel?.ClosePlayerOverlayCommand.CanExecute(null) == true)
        {
            await currentViewModel.ClosePlayerOverlayCommand.ExecuteAsync(null);
        }
    }

    private void OverlaySlider_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (currentViewModel is not null)
        {
            currentViewModel.Player.BeginSeekInteraction();
            ShowOverlayControlsTemporarily();
        }
    }

    private async void OverlaySlider_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Slider slider && currentViewModel is not null)
        {
            await currentViewModel.Player.CommitSeekAsync(slider.Value);
            ShowOverlayControlsTemporarily();
        }
    }

    private void OverlaySlider_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is Slider slider
            && e.Property == RangeBase.ValueProperty
            && currentViewModel is not null)
        {
            currentViewModel.Player.UpdateSeekPreview(slider.Value);
        }
    }

    private void OverlayVolumeSlider_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        overlayVolumePopupHideTimer.Stop();
        isOverlayVolumeInteractionActive = true;
        currentViewModel?.Player.BeginVolumeInteraction();
        ShowOverlayControlsTemporarily();
    }

    private async void OverlayVolumeSlider_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Slider slider && currentViewModel is not null)
        {
            await currentViewModel.Player.CommitVolumeAsync(slider.Value);
            isOverlayVolumeInteractionActive = false;
            ShowOverlayControlsTemporarily();
            StartOverlayVolumePopupHideTimer();
        }
    }

    private void OverlayVolumeSlider_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is Slider slider
            && e.Property == RangeBase.ValueProperty
            && currentViewModel is not null)
        {
            currentViewModel.Player.UpdateVolumePreview(slider.Value);
        }
    }

    private void OverlayVolumePointer_OnActivity(object? sender, PointerEventArgs e)
    {
        ShowOverlayVolumePopup();
    }

    private void OverlayVolumePointer_OnExited(object? sender, PointerEventArgs e)
    {
        StartOverlayVolumePopupHideTimer();
    }

    private void OverlayVolumePopupHideTimer_OnTick(object? sender, EventArgs e)
    {
        CloseOverlayVolumePopup();
    }

    private void ShowOverlayVolumePopup()
    {
        if (!CanShowOverlayPopups())
        {
            CloseOverlayVolumePopup();
            return;
        }

        overlayVolumePopupHideTimer.Stop();
        ShowOverlayControlsTemporarily();
    }

    private void StartOverlayVolumePopupHideTimer()
    {
        overlayVolumePopupHideTimer.Stop();
        overlayVolumePopupHideTimer.Start();
    }

    private void CloseOverlayVolumePopup()
    {
        overlayVolumePopupHideTimer.Stop();
        isOverlayVolumeInteractionActive = false;
    }

    private void Overlay_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        ShowOverlayControlsTemporarily();
    }

    private void OverlayControlsArea_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        ShowOverlayControlsTemporarily();
    }

    private void OverlayBottomHotZone_OnPointerActivity(object? sender, PointerEventArgs e)
    {
        ShowOverlayControlsTemporarily();
    }

    private void OverlayBottomHotZone_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ShowOverlayControlsTemporarily();
        e.Handled = true;
    }

    private void OverlayTapSurface_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ToggleOverlayControlsVisibility();
            e.Handled = true;
        }
    }

    private void OverlayControlsHideTimer_OnTick(object? sender, EventArgs e)
    {
        overlayControlsHideTimer.Stop();
        if (OverlayAudioPopup.IsOpen || OverlaySubtitlePopup.IsOpen || OverlaySubtitleSizePopup.IsOpen || isOverlayVolumeInteractionActive)
        {
            ShowOverlayControlsTemporarily();
            return;
        }

        if (currentViewModel?.Player is { IsPlaying: true, IsPaused: false } player)
        {
            player.AreControlsVisible = false;
            UpdateOverlayPopupState();
        }
    }

    private void ShowOverlayControlsTemporarily()
    {
        if (currentViewModel?.Player is not { } player)
        {
            return;
        }

        if (!CanShowOverlayPopups())
        {
            UpdateOverlayPopupState();
            return;
        }

        player.AreControlsVisible = true;
        UpdateOverlayPopupState();
        overlayControlsHideTimer.Stop();

        if (player.IsPlaying && !player.IsPaused)
        {
            overlayControlsHideTimer.Start();
        }
    }

    private void ToggleOverlayControlsVisibility()
    {
        if (currentViewModel?.Player is not { } player)
        {
            return;
        }

        if (!CanShowOverlayPopups())
        {
            UpdateOverlayPopupState();
            return;
        }

        player.AreControlsVisible = !player.AreControlsVisible;
        if (!player.AreControlsVisible)
        {
            CloseOverlayVolumePopup();
            OverlayAudioPopup.IsOpen = false;
            OverlaySubtitlePopup.IsOpen = false;
            OverlaySubtitleSizePopup.IsOpen = false;
        }

        UpdateOverlayPopupState();
        overlayControlsHideTimer.Stop();

        if (player.AreControlsVisible && player.IsPlaying && !player.IsPaused)
        {
            overlayControlsHideTimer.Start();
        }
    }

    private void EnterOverlayPlaybackMode()
    {
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            if (!ReferenceEquals(overlayKeyboardWindow, window))
            {
                if (overlayKeyboardWindow is not null)
                {
                    overlayKeyboardWindow.RemoveHandler(InputElement.KeyDownEvent, OverlayWindow_OnKeyDown);
                    overlayKeyboardWindow.PropertyChanged -= OverlayWindow_OnPropertyChanged;
                    overlayKeyboardWindow.Activated -= OverlayWindow_OnActivated;
                    overlayKeyboardWindow.Deactivated -= OverlayWindow_OnDeactivated;
                }

                overlayKeyboardWindow = window;
                overlayKeyboardWindow.AddHandler(InputElement.KeyDownEvent, OverlayWindow_OnKeyDown, RoutingStrategies.Tunnel, true);
                overlayKeyboardWindow.PropertyChanged += OverlayWindow_OnPropertyChanged;
                overlayKeyboardWindow.Activated += OverlayWindow_OnActivated;
                overlayKeyboardWindow.Deactivated += OverlayWindow_OnDeactivated;
            }

            if (!overlayFullscreenApplied)
            {
                windowStateBeforeOverlay = window.WindowState;
                overlayFullscreenApplied = true;
            }

            window.WindowState = WindowState.FullScreen;
            UpdateOverlayWindowChrome(window);
            FocusOverlayPlaybackRoot();
        }

        SetOverlayWindowControlsVisible(false);
        ShowOverlayControlsTemporarily();
    }

    private void ExitOverlayPlaybackMode()
    {
        overlayControlsHideTimer.Stop();
        overlayWindowControlsHideTimer.Stop();
        overlayVolumePopupHideTimer.Stop();
        CloseOverlayPopups();

        if (overlayKeyboardWindow is not null)
        {
            overlayKeyboardWindow.RemoveHandler(InputElement.KeyDownEvent, OverlayWindow_OnKeyDown);
            overlayKeyboardWindow.PropertyChanged -= OverlayWindow_OnPropertyChanged;
            overlayKeyboardWindow.Activated -= OverlayWindow_OnActivated;
            overlayKeyboardWindow.Deactivated -= OverlayWindow_OnDeactivated;
            overlayKeyboardWindow = null;
        }

        if (currentViewModel?.Player is { } player)
        {
            player.AreControlsVisible = false;
        }

        if (overlayFullscreenApplied &&
            TopLevel.GetTopLevel(this) is Window window &&
            windowStateBeforeOverlay.HasValue &&
            window.WindowState == WindowState.FullScreen)
        {
            window.WindowState = windowStateBeforeOverlay.Value;
            RestoreOverlayWindowChrome(window);
        }
        else if (TopLevel.GetTopLevel(this) is Window currentWindow)
        {
            RestoreOverlayWindowChrome(currentWindow);
        }

        overlayFullscreenApplied = false;
        windowStateBeforeOverlay = null;
        windowDecorationsBeforeOverlayFullscreen = null;
        UpdateOverlayPopupState();
    }

    private void OverlayWindow_OnActivated(object? sender, EventArgs e)
    {
        if (currentViewModel?.IsPlayerOverlayOpen != true)
        {
            return;
        }

        UpdateOverlayPopupState();
        UpdateOverlayWindowControlsMonitorState();
        Dispatcher.UIThread.Post(() =>
        {
            if (currentViewModel?.IsPlayerOverlayOpen != true)
            {
                return;
            }

            UpdateOverlayPopupState();
            UpdateOverlayWindowControlsMonitorState();
        }, DispatcherPriority.Background);
    }

    private void OverlayWindow_OnDeactivated(object? sender, EventArgs e)
    {
        if (currentViewModel?.IsPlayerOverlayOpen != true)
        {
            return;
        }

        overlayControlsHideTimer.Stop();
        overlayWindowControlsHideTimer.Stop();
        overlayVolumePopupHideTimer.Stop();

        if (currentViewModel.Player is { } player)
        {
            player.AreControlsVisible = false;
        }

        CloseOverlayPopups();
    }

    private void OverlayWindow_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.WindowStateProperty ||
            currentViewModel?.IsPlayerOverlayOpen != true)
        {
            return;
        }

        if (sender is Window window)
        {
            if (window.WindowState == WindowState.Maximized && !suppressNextOverlayMaximizedToFullscreen)
            {
                window.WindowState = WindowState.FullScreen;
                UpdateOverlayWindowChrome(window);
                FocusOverlayPlaybackRoot();
                SetOverlayWindowControlsVisible(false);
                return;
            }

            suppressNextOverlayMaximizedToFullscreen = false;
            UpdateOverlayWindowChrome(window);
        }

        SetOverlayWindowControlsVisible(false);
    }

    private async void OverlayWindow_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (currentViewModel is not { IsPlayerOverlayOpen: true })
        {
            return;
        }

        if (!IsOverlayShortcutKey(e.Key))
        {
            return;
        }

        e.Handled = true;
        if (!TryBeginOverlayShortcut(e.Key))
        {
            return;
        }

        await HandleOverlayShortcutKeyAsync(e.Key);
    }

    private bool TryBeginOverlayShortcut(Key key)
    {
        if (key != Key.Space)
        {
            return true;
        }

        var now = Environment.TickCount64;
        if (now - lastOverlaySpaceShortcutTick < OverlaySpaceShortcutDeduplicationMilliseconds)
        {
            return false;
        }

        lastOverlaySpaceShortcutTick = now;
        return true;
    }

    private async Task HandleOverlayShortcutKeyAsync(Key key)
    {
        if (currentViewModel is not { IsPlayerOverlayOpen: true } viewModel)
        {
            return;
        }

        switch (key)
        {
            case Key.Space:
                if (viewModel.Player.TogglePlayPauseCommand.CanExecute(null))
                {
                    await viewModel.Player.TogglePlayPauseCommand.ExecuteAsync(null);
                    ShowOverlayControlsTemporarily();
                }
                break;
            case Key.Enter:
                ToggleOverlayFullscreen();
                ShowOverlayControlsTemporarily();
                break;
            case Key.Left:
                if (viewModel.Player.SeekBackwardCommand.CanExecute(null))
                {
                    await viewModel.Player.SeekBackwardCommand.ExecuteAsync(null);
                    ShowOverlayControlsTemporarily();
                }
                break;
            case Key.Right:
                if (viewModel.Player.SeekForwardCommand.CanExecute(null))
                {
                    await viewModel.Player.SeekForwardCommand.ExecuteAsync(null);
                    ShowOverlayControlsTemporarily();
                }
                break;
            case Key.Escape:
                if (TopLevel.GetTopLevel(this) is Window { WindowState: WindowState.FullScreen } window)
                {
                    suppressNextOverlayMaximizedToFullscreen = (windowStateBeforeOverlay ?? WindowState.Normal) == WindowState.Maximized;
                    window.WindowState = windowStateBeforeOverlay ?? WindowState.Normal;
                    SetOverlayWindowControlsVisible(false);
                    ShowOverlayControlsTemporarily();
                }
                break;
            case Key.Up:
            case Key.Add:
            case Key.OemPlus:
                if (viewModel.Player.IncreaseVolumeCommand.CanExecute(null))
                {
                    await viewModel.Player.IncreaseVolumeCommand.ExecuteAsync(null);
                    ShowOverlayControlsTemporarily();
                }
                break;
            case Key.Down:
            case Key.Subtract:
            case Key.OemMinus:
                if (viewModel.Player.DecreaseVolumeCommand.CanExecute(null))
                {
                    await viewModel.Player.DecreaseVolumeCommand.ExecuteAsync(null);
                    ShowOverlayControlsTemporarily();
                }
                break;
        }
    }

    private void ToggleOverlayFullscreen()
    {
        if (TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        if (window.WindowState == WindowState.FullScreen)
        {
            suppressNextOverlayMaximizedToFullscreen = (windowStateBeforeOverlay ?? WindowState.Normal) == WindowState.Maximized;
            window.WindowState = windowStateBeforeOverlay ?? WindowState.Normal;
            SetOverlayWindowControlsVisible(false);
            return;
        }

        if (!overlayFullscreenApplied)
        {
            windowStateBeforeOverlay = window.WindowState;
            overlayFullscreenApplied = true;
        }

        window.WindowState = WindowState.FullScreen;
        UpdateOverlayWindowChrome(window);
        FocusOverlayPlaybackRoot();
        SetOverlayWindowControlsVisible(false);
    }

    private void FocusOverlayPlaybackRoot()
    {
        Dispatcher.UIThread.Post(() => OverlayPlaybackRoot.Focus());
    }

    private static bool IsOverlayShortcutKey(Key key)
    {
        return key is Key.Space
            or Key.Enter
            or Key.Escape
            or Key.Left
            or Key.Right
            or Key.Up
            or Key.Add
            or Key.OemPlus
            or Key.Down
            or Key.Subtract
            or Key.OemMinus;
    }

    private static Key? MapNativeVirtualKey(int virtualKey)
    {
        return virtualKey switch
        {
            VirtualKeySpace => Key.Space,
            VirtualKeyEnter => Key.Enter,
            VirtualKeyEscape => Key.Escape,
            VirtualKeyLeft => Key.Left,
            VirtualKeyUp => Key.Up,
            VirtualKeyRight => Key.Right,
            VirtualKeyAdd => Key.Add,
            VirtualKeyOemPlus => Key.OemPlus,
            VirtualKeyDown => Key.Down,
            VirtualKeySubtract => Key.Subtract,
            VirtualKeyOemMinus => Key.OemMinus,
            _ => null
        };
    }

    private void UpdateOverlayWindowChrome(Window window)
    {
        if (currentViewModel?.IsPlayerOverlayOpen != true)
        {
            RestoreOverlayWindowChrome(window);
            return;
        }

        if (window.WindowState == WindowState.FullScreen)
        {
            windowDecorationsBeforeOverlayFullscreen ??= window.SystemDecorations;
            window.SystemDecorations = SystemDecorations.None;
            return;
        }

        RestoreOverlayWindowChrome(window);
    }

    private void RestoreOverlayWindowChrome(Window window)
    {
        if (windowDecorationsBeforeOverlayFullscreen.HasValue)
        {
            window.SystemDecorations = windowDecorationsBeforeOverlayFullscreen.Value;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private readonly struct CursorPoint
    {
        public readonly int X;
        public readonly int Y;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}
