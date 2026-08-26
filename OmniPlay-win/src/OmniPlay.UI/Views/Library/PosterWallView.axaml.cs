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
    private const int WindowProcedureIndex = -4;
    private const int WindowMessageKeyDown = 0x0100;
    private const int WindowMessageSysKeyDown = 0x0104;
    private const int WindowMessageSysCommand = 0x0112;
    private const int WindowMessageNonClientHitTest = 0x0084;
    private const int WindowMessageNonClientLeftButtonDown = 0x00A1;
    private const int SystemCommandMaximize = 0xF030;
    private const int NativeHitTestTransparent = -1;
    private const int NativeHitTestLeft = 10;
    private const int NativeHitTestRight = 11;
    private const int NativeHitTestTop = 12;
    private const int NativeHitTestTopLeft = 13;
    private const int NativeHitTestTopRight = 14;
    private const int NativeHitTestBottom = 15;
    private const int NativeHitTestBottomLeft = 16;
    private const int NativeHitTestBottomRight = 17;
    private const int NativeCursorSizeNorthSouth = 32645;
    private const int NativeCursorSizeWestEast = 32644;
    private const int NativeCursorSizeNorthWestSouthEast = 32642;
    private const int NativeCursorSizeNorthEastSouthWest = 32643;
    private const double LibraryPosterBaseItemWidth = 224;
    private const double LibraryPosterAspectRatio = 1.5;
    private const double EpisodeThumbnailAspectRatio = 9.0 / 16.0;
    private const double LibraryPosterTargetColumns = 8;
    private const double OverlayWindowControlsHotZoneHeight = 48;
    private const double OverlayResizeBorderThickness = 10;
    private const double OverlayResizeCornerSize = 14;
    private const double ContinueWatchingDragThreshold = 4;
    private const int OverlaySpaceShortcutDeduplicationMilliseconds = 180;
    private static readonly TimeSpan OverlayWindowControlsMonitorInterval = TimeSpan.FromMilliseconds(75);

    public static readonly StyledProperty<double> LibraryPosterCardWidthProperty =
        AvaloniaProperty.Register<PosterWallView, double>(nameof(LibraryPosterCardWidth), 204);

    public static readonly StyledProperty<double> LibraryPosterCardHeightProperty =
        AvaloniaProperty.Register<PosterWallView, double>(nameof(LibraryPosterCardHeight), 306);

    public static readonly StyledProperty<double> LibraryPosterItemWidthProperty =
        AvaloniaProperty.Register<PosterWallView, double>(nameof(LibraryPosterItemWidth), 224);

    public static readonly StyledProperty<double> LibraryPosterItemHeightProperty =
        AvaloniaProperty.Register<PosterWallView, double>(nameof(LibraryPosterItemHeight), 420);

    public static readonly StyledProperty<double> LibraryPosterSubtitleWidthProperty =
        AvaloniaProperty.Register<PosterWallView, double>(nameof(LibraryPosterSubtitleWidth), 166);

    public static readonly StyledProperty<double> LibraryPosterTitleFontSizeProperty =
        AvaloniaProperty.Register<PosterWallView, double>(nameof(LibraryPosterTitleFontSize), 16);

    public static readonly StyledProperty<double> EpisodeThumbnailCardWidthProperty =
        AvaloniaProperty.Register<PosterWallView, double>(nameof(EpisodeThumbnailCardWidth), 398);

    public static readonly StyledProperty<double> EpisodeThumbnailCardHeightProperty =
        AvaloniaProperty.Register<PosterWallView, double>(nameof(EpisodeThumbnailCardHeight), 224);

    public static readonly StyledProperty<double> EpisodeThumbnailItemWidthProperty =
        AvaloniaProperty.Register<PosterWallView, double>(nameof(EpisodeThumbnailItemWidth), 430);

    public static readonly StyledProperty<double> EpisodeThumbnailItemHeightProperty =
        AvaloniaProperty.Register<PosterWallView, double>(nameof(EpisodeThumbnailItemHeight), 340);

    public static readonly StyledProperty<double> EpisodeThumbnailSpacingProperty =
        AvaloniaProperty.Register<PosterWallView, double>(nameof(EpisodeThumbnailSpacing), 56);

    public static readonly StyledProperty<int> EpisodeThumbnailItemsPerRowProperty =
        AvaloniaProperty.Register<PosterWallView, int>(nameof(EpisodeThumbnailItemsPerRow), 4);

    private readonly DispatcherTimer overlayControlsHideTimer;
    private readonly DispatcherTimer overlayWindowControlsHideTimer;
    private readonly DispatcherTimer overlayVolumePopupHideTimer;
    private WrapPanel? libraryItemsWrapPanel;
    private PosterWallViewModel? currentViewModel;
    private PlayerSurfaceHost? overlayPlayerSurfaceHost;
    private WindowState? windowStateBeforeOverlay;
    private SystemDecorations? windowDecorationsBeforeOverlayFullscreen;
    private Window? overlayKeyboardWindow;
    private IntPtr overlayWindowHandle;
    private IntPtr overlayPreviousWindowProcedure;
    private NativeWindowProcedure? overlayWindowProcedure;
    private bool isContinueWatchingScrollDragActive;
    private bool isContinueWatchingScrollDragging;
    private Point continueWatchingScrollDragStartPoint;
    private Vector continueWatchingScrollDragStartOffset;
    private bool openingPlayback;
    private bool isOverlayVolumeInteractionActive;
    private bool overlayFullscreenApplied;
    private bool overlayResizeChromeVisible;
    private bool suppressNextOverlayMaximizedToFullscreen;
    private long lastOverlaySpaceShortcutTick;

    public PosterWallView()
    {
        InitializeComponent();
        ConfigureOverlayResizeCursors();

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
        ContinueWatchingScrollViewer.AddHandler(PointerPressedEvent, ContinueWatchingScrollViewer_OnPointerPressed, RoutingStrategies.Tunnel, true);
        ContinueWatchingScrollViewer.AddHandler(PointerMovedEvent, ContinueWatchingScrollViewer_OnPointerMoved, RoutingStrategies.Tunnel, true);
        ContinueWatchingScrollViewer.AddHandler(PointerReleasedEvent, ContinueWatchingScrollViewer_OnPointerReleased, RoutingStrategies.Tunnel, true);

        HomeContentStack.SizeChanged += HomeContentStack_OnSizeChanged;
        DetailContentStack.SizeChanged += DetailContentStack_OnSizeChanged;
        LibraryItemsControl.AttachedToVisualTree += LibraryItemsControl_OnAttachedToVisualTree;
        EpisodeItemsControl.AttachedToVisualTree += EpisodeItemsControl_OnAttachedToVisualTree;
        DataContextChanged += OnDataContextChanged;
    }

    private void ConfigureOverlayResizeCursors()
    {
        OverlayNorthResizeGrip.Cursor = new Cursor(StandardCursorType.TopSide);
        OverlaySouthResizeGrip.Cursor = new Cursor(StandardCursorType.BottomSide);
        OverlayWestResizeGrip.Cursor = new Cursor(StandardCursorType.LeftSide);
        OverlayEastResizeGrip.Cursor = new Cursor(StandardCursorType.RightSide);
        OverlayNorthWestResizeGrip.Cursor = new Cursor(StandardCursorType.TopLeftCorner);
        OverlayNorthEastResizeGrip.Cursor = new Cursor(StandardCursorType.TopRightCorner);
        OverlaySouthWestResizeGrip.Cursor = new Cursor(StandardCursorType.BottomLeftCorner);
        OverlaySouthEastResizeGrip.Cursor = new Cursor(StandardCursorType.BottomRightCorner);
        OverlayPopupNorthResizeGrip.Cursor = new Cursor(StandardCursorType.TopSide);
        OverlayPopupSouthResizeGrip.Cursor = new Cursor(StandardCursorType.BottomSide);
        OverlayPopupWestResizeGrip.Cursor = new Cursor(StandardCursorType.LeftSide);
        OverlayPopupEastResizeGrip.Cursor = new Cursor(StandardCursorType.RightSide);
        OverlayPopupNorthWestResizeGrip.Cursor = new Cursor(StandardCursorType.TopLeftCorner);
        OverlayPopupNorthEastResizeGrip.Cursor = new Cursor(StandardCursorType.TopRightCorner);
        OverlayPopupSouthWestResizeGrip.Cursor = new Cursor(StandardCursorType.BottomLeftCorner);
        OverlayPopupSouthEastResizeGrip.Cursor = new Cursor(StandardCursorType.BottomRightCorner);
    }

    public double LibraryPosterCardWidth
    {
        get => GetValue(LibraryPosterCardWidthProperty);
        private set => SetValue(LibraryPosterCardWidthProperty, value);
    }

    public double LibraryPosterCardHeight
    {
        get => GetValue(LibraryPosterCardHeightProperty);
        private set => SetValue(LibraryPosterCardHeightProperty, value);
    }

    public double LibraryPosterItemWidth
    {
        get => GetValue(LibraryPosterItemWidthProperty);
        private set => SetValue(LibraryPosterItemWidthProperty, value);
    }

    public double LibraryPosterItemHeight
    {
        get => GetValue(LibraryPosterItemHeightProperty);
        private set => SetValue(LibraryPosterItemHeightProperty, value);
    }

    public double LibraryPosterSubtitleWidth
    {
        get => GetValue(LibraryPosterSubtitleWidthProperty);
        private set => SetValue(LibraryPosterSubtitleWidthProperty, value);
    }

    public double LibraryPosterTitleFontSize
    {
        get => GetValue(LibraryPosterTitleFontSizeProperty);
        private set => SetValue(LibraryPosterTitleFontSizeProperty, value);
    }

    public double EpisodeThumbnailCardWidth
    {
        get => GetValue(EpisodeThumbnailCardWidthProperty);
        private set => SetValue(EpisodeThumbnailCardWidthProperty, value);
    }

    public double EpisodeThumbnailCardHeight
    {
        get => GetValue(EpisodeThumbnailCardHeightProperty);
        private set => SetValue(EpisodeThumbnailCardHeightProperty, value);
    }

    public double EpisodeThumbnailItemWidth
    {
        get => GetValue(EpisodeThumbnailItemWidthProperty);
        private set => SetValue(EpisodeThumbnailItemWidthProperty, value);
    }

    public double EpisodeThumbnailItemHeight
    {
        get => GetValue(EpisodeThumbnailItemHeightProperty);
        private set => SetValue(EpisodeThumbnailItemHeightProperty, value);
    }

    public double EpisodeThumbnailSpacing
    {
        get => GetValue(EpisodeThumbnailSpacingProperty);
        private set => SetValue(EpisodeThumbnailSpacingProperty, value);
    }

    public int EpisodeThumbnailItemsPerRow
    {
        get => GetValue(EpisodeThumbnailItemsPerRowProperty);
        private set => SetValue(EpisodeThumbnailItemsPerRowProperty, value);
    }

    private void HomeContentStack_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateLibraryPosterItemWidth();
    }

    private void DetailContentStack_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateEpisodeThumbnailItemWidth();
    }

    private void LibraryItemsControl_OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Dispatcher.UIThread.Post(UpdateLibraryPosterItemWidth, DispatcherPriority.Loaded);
    }

    private void EpisodeItemsControl_OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Dispatcher.UIThread.Post(UpdateEpisodeThumbnailItemWidth, DispatcherPriority.Loaded);
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

        var physicalWidth = EstimatePhysicalWidth(availableWidth);
        var targetColumns = ResolveLibraryPosterColumns(availableWidth, physicalWidth);
        var cardWidth = ResolveLibraryPosterCardWidth(availableWidth, physicalWidth, targetColumns);
        var cardHeight = Math.Round(cardWidth * LibraryPosterAspectRatio);
        var minimumItemWidth = Math.Max(
            cardWidth + 16,
            physicalWidth <= 2100 ? 0 : LibraryPosterBaseItemWidth);
        var itemWidth = Math.Max(
            minimumItemWidth,
            Math.Floor(availableWidth / targetColumns));
        var itemHeight = Math.Round(cardHeight + Math.Max(92, cardWidth * 0.44));

        LibraryPosterCardWidth = cardWidth;
        LibraryPosterCardHeight = cardHeight;
        LibraryPosterItemWidth = itemWidth;
        LibraryPosterItemHeight = itemHeight;
        LibraryPosterSubtitleWidth = Math.Max(120, cardWidth - 38);
        LibraryPosterTitleFontSize = cardWidth < 196 ? 15 : 16;

        if (Math.Abs(libraryItemsWrapPanel.ItemWidth - itemWidth) > 0.5)
        {
            libraryItemsWrapPanel.ItemWidth = itemWidth;
        }

        if (Math.Abs(libraryItemsWrapPanel.ItemHeight - itemHeight) > 0.5)
        {
            libraryItemsWrapPanel.ItemHeight = itemHeight;
        }
    }

    private void UpdateEpisodeThumbnailItemWidth()
    {
        var availableWidth = DetailContentStack.Bounds.Width;
        if (availableWidth <= 0)
        {
            return;
        }

        var physicalWidth = EstimatePhysicalWidth(availableWidth);
        var targetColumns = ResolveEpisodeThumbnailColumns(availableWidth, physicalWidth);
        var cardWidth = ResolveEpisodeThumbnailCardWidth(availableWidth, physicalWidth, targetColumns);
        var cardHeight = Math.Round(cardWidth * EpisodeThumbnailAspectRatio);
        var spacing = ResolveEpisodeThumbnailSpacing(physicalWidth);
        var itemWidth = Math.Round(cardWidth + spacing);
        var itemHeight = Math.Round(cardHeight + 118);

        EpisodeThumbnailCardWidth = cardWidth;
        EpisodeThumbnailCardHeight = cardHeight;
        EpisodeThumbnailItemWidth = itemWidth;
        EpisodeThumbnailItemHeight = itemHeight;
        EpisodeThumbnailSpacing = spacing;
        EpisodeThumbnailItemsPerRow = (int)targetColumns;
    }

    private double EstimatePhysicalWidth(double availableWidth)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        return availableWidth * scaling;
    }

    private static double ResolveLibraryPosterColumns(double availableWidth, double physicalWidth)
    {
        if (physicalWidth >= 3400)
        {
            return availableWidth >= 3000 ? 12 : LibraryPosterTargetColumns;
        }

        if (physicalWidth <= 2100)
        {
            return availableWidth >= 1700 ? LibraryPosterTargetColumns : 7;
        }

        return LibraryPosterTargetColumns;
    }

    private static double ResolveLibraryPosterCardWidth(
        double availableWidth,
        double physicalWidth,
        double targetColumns)
    {
        if (physicalWidth >= 3400)
        {
            return Clamp(Math.Floor(availableWidth / targetColumns) - 24, 236, 292);
        }

        if (physicalWidth <= 2100)
        {
            return availableWidth >= 1700 ? 196 : 188;
        }

        return 204;
    }

    private static double ResolveEpisodeThumbnailColumns(double availableWidth, double physicalWidth)
    {
        if (physicalWidth >= 3400)
        {
            if (availableWidth >= 3200)
            {
                return 7;
            }

            return availableWidth >= 2200 ? 5 : 4;
        }

        if (physicalWidth <= 2100)
        {
            return availableWidth >= 1700 ? 4 : 3;
        }

        return Math.Max(3, Math.Floor(availableWidth / 430));
    }

    private static double ResolveEpisodeThumbnailCardWidth(
        double availableWidth,
        double physicalWidth,
        double targetColumns)
    {
        if (physicalWidth >= 3400)
        {
            return Clamp(Math.Floor(availableWidth / targetColumns) - 34, 420, 470);
        }

        if (physicalWidth <= 2100)
        {
            return Clamp(Math.Floor(availableWidth / targetColumns) - 34, 320, 366);
        }

        return 398;
    }

    private static double ResolveEpisodeThumbnailSpacing(double physicalWidth)
    {
        if (physicalWidth >= 3400)
        {
            return 64;
        }

        return physicalWidth <= 2100 ? 46 : 56;
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        return Math.Min(maximum, Math.Max(minimum, value));
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
        UpdateOverlayResizeChrome();

        if (currentViewModel is not null)
        {
            currentViewModel.PropertyChanged += OnViewModelPropertyChanged;
            currentViewModel.Player.PropertyChanged += OnPlayerPropertyChanged;

            if (currentViewModel.IsPlayerOverlayOpen)
            {
                EnterOverlayPlaybackMode();
            }

            UpdateOverlayPopupState();
            UpdateOverlayResizeChrome();
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
            UpdateOverlayResizeChrome();
        }

        if (e.PropertyName == nameof(PosterWallViewModel.IsDetailOpen))
        {
            Dispatcher.UIThread.Post(UpdateEpisodeThumbnailItemWidth, DispatcherPriority.Loaded);
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
                        currentViewModel.PendingPlaybackDisplayPath,
                        currentViewModel.PendingPlaybackMediaFileName),
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
            or nameof(PlayerViewModel.IsPaused)
            or nameof(PlayerViewModel.IsOpeningMovie))
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

    private void ContinueWatchingScrollViewer_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ContinueWatchingScrollViewer).Properties.IsLeftButtonPressed ||
            !CanDragContinueWatchingScroll())
        {
            return;
        }

        isContinueWatchingScrollDragActive = true;
        isContinueWatchingScrollDragging = false;
        continueWatchingScrollDragStartPoint = e.GetPosition(ContinueWatchingScrollViewer);
        continueWatchingScrollDragStartOffset = ContinueWatchingScrollViewer.Offset;
    }

    private void ContinueWatchingScrollViewer_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!isContinueWatchingScrollDragActive)
        {
            return;
        }

        var point = e.GetPosition(ContinueWatchingScrollViewer);
        var delta = point - continueWatchingScrollDragStartPoint;
        if (!isContinueWatchingScrollDragging &&
            Math.Abs(delta.X) < ContinueWatchingDragThreshold &&
            Math.Abs(delta.Y) < ContinueWatchingDragThreshold)
        {
            return;
        }

        isContinueWatchingScrollDragging = true;
        if (e.Pointer.Captured != ContinueWatchingScrollViewer)
        {
            e.Pointer.Capture(ContinueWatchingScrollViewer);
        }

        var maxOffset = Math.Max(0, ContinueWatchingScrollViewer.Extent.Width - ContinueWatchingScrollViewer.Viewport.Width);
        var nextOffset = Clamp(continueWatchingScrollDragStartOffset.X - delta.X, 0, maxOffset);
        ContinueWatchingScrollViewer.Offset = new Vector(nextOffset, ContinueWatchingScrollViewer.Offset.Y);
        e.Handled = true;
    }

    private void ContinueWatchingScrollViewer_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!isContinueWatchingScrollDragActive)
        {
            return;
        }

        var wasDragging = isContinueWatchingScrollDragging;
        isContinueWatchingScrollDragActive = false;
        isContinueWatchingScrollDragging = false;
        if (e.Pointer.Captured == ContinueWatchingScrollViewer)
        {
            e.Pointer.Capture(null);
        }

        if (wasDragging)
        {
            e.Handled = true;
        }
    }

    private bool CanDragContinueWatchingScroll()
    {
        return ContinueWatchingScrollViewer.Extent.Width > ContinueWatchingScrollViewer.Viewport.Width;
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
        overlayPlayerSurfaceHost.NativeHitTest += OverlayPlayerSurfaceHost_OnNativeHitTest;
        overlayPlayerSurfaceHost.NativeKeyDown += OverlayPlayerSurfaceHost_OnNativeKeyDown;
        OverlayPlayerSurfaceHostContainer.Content = overlayPlayerSurfaceHost;
        return overlayPlayerSurfaceHost;
    }

    private void OverlayPlayerSurfaceHost_OnNativePointerMoved(object? sender, NativePointerActivityEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateOverlayNativeResizeCursor(e);
            ShowOverlayControlsTemporarily();

            if (IsOverlayFullscreen()
                && e.Y <= OverlayWindowControlsHotZoneHeight
                && !OverlayWindowControlsPopup.IsOpen)
            {
                ShowOverlayWindowControlsTemporarily();
            }
        });
    }

    private void OverlayPlayerSurfaceHost_OnNativePrimaryButtonPressed(object? sender, NativePrimaryButtonPressedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess() && TryBeginOverlayNativeResizeDrag(e.Pointer))
        {
            e.Handled = true;
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            ToggleOverlayControlsVisibility();
        });
    }

    private void OverlayPlayerSurfaceHost_OnNativeHitTest(object? sender, NativeHitTestEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess() ||
            !CanShowOverlayResizeChrome() ||
            ResolveOverlayResizeEdgeFromNativePointer(e.ClientPointer) is null)
        {
            return;
        }

        e.Result = NativeHitTestTransparent;
        e.Handled = true;
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
        UpdateOverlayResizeCursor(e);
        ShowOverlayWindowControlsTemporarily();
    }

    private void OverlayWindowControlsHotZone_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TryBeginOverlayResizeDrag(e))
        {
            return;
        }

        ShowOverlayWindowControlsTemporarily();
    }

    private void OverlayWindowControlsPanel_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        UpdateOverlayResizeCursor(e);
        ShowOverlayWindowControlsTemporarily();
    }

    private void OverlayWindowControlsPanel_OnPointerExited(object? sender, PointerEventArgs e)
    {
        ResetOverlayResizeCursor();
        EnsureOverlayWindowControlsMonitorStarted();
    }

    private void OverlayWindowControlsPanel_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        UpdateOverlayResizeCursor(e);
        ShowOverlayWindowControlsTemporarily();
    }

    private void OverlayWindowControlsPanel_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TryBeginOverlayResizeDrag(e))
        {
            return;
        }

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

    private void ShowOverlayWindowControlsAfterWindowStateChange()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (currentViewModel?.IsPlayerOverlayOpen != true)
            {
                return;
            }

            UpdateOverlayPopupState();
            ShowOverlayWindowControlsTemporarily();
        }, DispatcherPriority.Background);
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
        OverlayStatusPopup.IsOpen = canShowOverlayPopups && player?.ShouldShowOpeningMovieStatus == true;
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

        UpdateOverlayResizeChrome();
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

    private void OverlayResizeBorder_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!CanBeginOverlayResizeDrag(e))
        {
            return;
        }

        if (sender is not Control { Tag: string edgeName } ||
            !Enum.TryParse<WindowEdge>(edgeName, out var edge))
        {
            return;
        }

        BeginOverlayResizeDrag(edge, e);
    }

    private bool TryBeginOverlayResizeDrag(PointerPressedEventArgs e)
    {
        if (!CanBeginOverlayResizeDrag(e))
        {
            return false;
        }

        var edge = ResolveOverlayResizeEdge(e);
        if (edge is null)
        {
            return false;
        }

        BeginOverlayResizeDrag(edge.Value, e);
        return true;
    }

    private bool TryBeginOverlayNativeResizeDrag(NativePointerActivityEventArgs? pointer = null)
    {
        if (!CanShowOverlayResizeChrome())
        {
            return false;
        }

        var edge = ResolveOverlayResizeEdgeFromScreen() ??
            (pointer is null ? null : ResolveOverlayResizeEdgeFromNativePointer(pointer));
        if (edge is null)
        {
            return false;
        }

        BeginOverlayNativeResizeDrag(edge.Value);
        return true;
    }

    private bool CanBeginOverlayResizeDrag(PointerPressedEventArgs e)
    {
        return CanShowOverlayResizeChrome() &&
               !IsWithinOverlayButton(e.Source as Control) &&
               e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
    }

    private void BeginOverlayResizeDrag(WindowEdge edge, PointerPressedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        window.BeginResizeDrag(edge, e);
        e.Handled = true;
    }

    private void BeginOverlayNativeResizeDrag(WindowEdge edge)
    {
        if (!OperatingSystem.IsWindows() ||
            TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        ReleaseCapture();
        var pointer = GetCursorPos(out var cursor)
            ? MakePointerLParam(cursor.X, cursor.Y)
            : IntPtr.Zero;
        SendMessage(handle, WindowMessageNonClientLeftButtonDown, ResolveNativeResizeHitTest(edge), pointer);
    }

    private WindowEdge? ResolveOverlayResizeEdge(PointerEventArgs e)
    {
        return ResolveOverlayResizeEdgeFromScreen() ??
               ResolveOverlayResizeEdge(e.GetPosition(OverlayPlaybackRoot));
    }

    private WindowEdge? ResolveOverlayResizeEdgeFromScreen()
    {
        if (!OperatingSystem.IsWindows() ||
            TopLevel.GetTopLevel(this) is not Window window)
        {
            return null;
        }

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero ||
            !GetWindowRect(handle, out var windowRect) ||
            !GetCursorPos(out var cursor))
        {
            return null;
        }

        return ResolveOverlayResizeEdgeFromScreenPoint(window, windowRect, cursor);
    }

    private WindowEdge? ResolveOverlayResizeEdgeFromScreenPoint(
        Window window,
        NativeRect windowRect,
        CursorPoint cursor)
    {
        if (cursor.X < windowRect.Left ||
            cursor.X > windowRect.Right ||
            cursor.Y < windowRect.Top ||
            cursor.Y > windowRect.Bottom)
        {
            return null;
        }

        var scaling = window.DesktopScaling;
        if (scaling <= 0)
        {
            scaling = 1;
        }

        return ResolveOverlayResizeEdge(
            new Point((cursor.X - windowRect.Left) / scaling, (cursor.Y - windowRect.Top) / scaling),
            (windowRect.Right - windowRect.Left) / scaling,
            (windowRect.Bottom - windowRect.Top) / scaling);
    }

    private WindowEdge? ResolveOverlayResizeEdgeFromNativePointer(NativePointerActivityEventArgs pointer)
    {
        if (TopLevel.GetTopLevel(this) is not Window window)
        {
            return null;
        }

        var scaling = window.DesktopScaling;
        if (scaling <= 0)
        {
            scaling = 1;
        }

        return ResolveOverlayResizeEdge(
            new Point(pointer.X / scaling, pointer.Y / scaling),
            OverlayPlaybackRoot.Bounds.Width,
            OverlayPlaybackRoot.Bounds.Height);
    }

    private WindowEdge? ResolveOverlayResizeEdge(Point point)
    {
        return ResolveOverlayResizeEdge(point, OverlayPlaybackRoot.Bounds.Width, OverlayPlaybackRoot.Bounds.Height);
    }

    private static WindowEdge? ResolveOverlayResizeEdge(Point point, double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var north = point.Y <= OverlayResizeBorderThickness;
        var south = point.Y >= height - OverlayResizeBorderThickness;
        var west = point.X <= OverlayResizeBorderThickness;
        var east = point.X >= width - OverlayResizeBorderThickness;
        var northCorner = point.Y <= OverlayResizeCornerSize;
        var southCorner = point.Y >= height - OverlayResizeCornerSize;
        var westCorner = point.X <= OverlayResizeCornerSize;
        var eastCorner = point.X >= width - OverlayResizeCornerSize;

        if (northCorner && westCorner)
        {
            return WindowEdge.NorthWest;
        }

        if (northCorner && eastCorner)
        {
            return WindowEdge.NorthEast;
        }

        if (southCorner && westCorner)
        {
            return WindowEdge.SouthWest;
        }

        if (southCorner && eastCorner)
        {
            return WindowEdge.SouthEast;
        }

        if (north)
        {
            return WindowEdge.North;
        }

        if (south)
        {
            return WindowEdge.South;
        }

        if (west)
        {
            return WindowEdge.West;
        }

        return east ? WindowEdge.East : null;
    }

    private void UpdateOverlayResizeCursor(PointerEventArgs e)
    {
        if (!CanShowOverlayResizeChrome() ||
            IsWithinOverlayButton(e.Source as Control))
        {
            ResetOverlayResizeCursor();
            return;
        }

        SetOverlayResizeCursor(ResolveOverlayResizeCursor(ResolveOverlayResizeEdge(e)));
    }

    private void UpdateOverlayResizeChrome(Window? window = null)
    {
        var canResize = CanShowOverlayResizeChrome(window);
        if (overlayResizeChromeVisible == canResize)
        {
            return;
        }

        overlayResizeChromeVisible = canResize;
        OverlayResizeLayer.IsVisible = canResize;
        OverlayWindowBorder.IsVisible = canResize;
        OverlayResizePopup.IsOpen = canResize;

        if (!canResize)
        {
            ResetOverlayResizeCursor();
        }
    }

    private bool CanShowOverlayResizeChrome(Window? window = null)
    {
        var targetWindow = window ?? (TopLevel.GetTopLevel(this) as Window);
        return currentViewModel?.IsPlayerOverlayOpen == true &&
               targetWindow?.WindowState == WindowState.Normal;
    }

    private void SetOverlayResizeCursor(Cursor? cursor)
    {
        OverlayPlaybackRoot.Cursor = cursor;
        OverlayWindowControlsPanel.Cursor = cursor;
        OverlayWindowControlsHotZone.Cursor = cursor;
        OverlayControlsArea.Cursor = cursor;
        OverlayBottomHotZone.Cursor = cursor;
    }

    private void ResetOverlayResizeCursor()
    {
        SetOverlayResizeCursor(null);
    }

    private void UpdateOverlayNativeResizeCursor(NativePointerActivityEventArgs? pointer = null)
    {
        if (!OperatingSystem.IsWindows() ||
            !CanShowOverlayResizeChrome())
        {
            return;
        }

        var edge = ResolveOverlayResizeEdgeFromScreen() ??
            (pointer is null ? null : ResolveOverlayResizeEdgeFromNativePointer(pointer));
        if (edge is null)
        {
            return;
        }

        var cursorHandle = LoadCursor(IntPtr.Zero, ResolveNativeResizeCursorId(edge.Value));
        if (cursorHandle != IntPtr.Zero)
        {
            SetCursor(cursorHandle);
        }
    }

    private static Cursor? ResolveOverlayResizeCursor(WindowEdge? edge)
    {
        return edge switch
        {
            WindowEdge.North => new Cursor(StandardCursorType.TopSide),
            WindowEdge.South => new Cursor(StandardCursorType.BottomSide),
            WindowEdge.West => new Cursor(StandardCursorType.LeftSide),
            WindowEdge.East => new Cursor(StandardCursorType.RightSide),
            WindowEdge.NorthWest => new Cursor(StandardCursorType.TopLeftCorner),
            WindowEdge.NorthEast => new Cursor(StandardCursorType.TopRightCorner),
            WindowEdge.SouthWest => new Cursor(StandardCursorType.BottomLeftCorner),
            WindowEdge.SouthEast => new Cursor(StandardCursorType.BottomRightCorner),
            _ => null
        };
    }

    private static IntPtr ResolveNativeResizeHitTest(WindowEdge edge)
    {
        var hitTest = edge switch
        {
            WindowEdge.North => NativeHitTestTop,
            WindowEdge.South => NativeHitTestBottom,
            WindowEdge.West => NativeHitTestLeft,
            WindowEdge.East => NativeHitTestRight,
            WindowEdge.NorthWest => NativeHitTestTopLeft,
            WindowEdge.NorthEast => NativeHitTestTopRight,
            WindowEdge.SouthWest => NativeHitTestBottomLeft,
            WindowEdge.SouthEast => NativeHitTestBottomRight,
            _ => NativeHitTestRight
        };

        return new IntPtr(hitTest);
    }

    private static IntPtr ResolveNativeResizeCursorId(WindowEdge edge)
    {
        var cursorId = edge switch
        {
            WindowEdge.North or WindowEdge.South => NativeCursorSizeNorthSouth,
            WindowEdge.West or WindowEdge.East => NativeCursorSizeWestEast,
            WindowEdge.NorthWest or WindowEdge.SouthEast => NativeCursorSizeNorthWestSouthEast,
            WindowEdge.NorthEast or WindowEdge.SouthWest => NativeCursorSizeNorthEastSouthWest,
            _ => NativeCursorSizeWestEast
        };

        return new IntPtr(cursorId);
    }

    private static CursorPoint DecodePointerLParam(IntPtr lParam)
    {
        var value = unchecked((int)lParam.ToInt64());
        return new CursorPoint(
            unchecked((short)(value & 0xFFFF)),
            unchecked((short)((value >> 16) & 0xFFFF)));
    }

    private static IntPtr MakePointerLParam(int x, int y)
    {
        var value = unchecked(((ushort)(short)y << 16) | (ushort)(short)x);
        return new IntPtr(value);
    }

    private static bool IsWithinOverlayButton(Control? control)
    {
        for (var current = control; current is not null; current = current.Parent as Control)
        {
            if (current is Button)
            {
                return true;
            }
        }

        return false;
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
        ToggleOverlayFullscreenFromMaximizeButton();
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
            currentViewModel.BeginPlayerTimelineSeekInteraction();
            ShowOverlayControlsTemporarily();
        }
    }

    private async void OverlaySlider_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Slider slider && currentViewModel is not null)
        {
            await currentViewModel.CommitPlayerTimelineSeekAsync(slider.Value);
            ShowOverlayControlsTemporarily();
        }
    }

    private void OverlaySlider_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is Slider slider
            && e.Property == RangeBase.ValueProperty
            && currentViewModel is not null)
        {
            currentViewModel.UpdatePlayerTimelineSeekPreview(slider.Value);
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
        UpdateOverlayResizeCursor(e);
        ShowOverlayControlsTemporarily();
    }

    private void OverlayControlsArea_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        UpdateOverlayResizeCursor(e);
        ShowOverlayControlsTemporarily();
    }

    private void OverlayControlsArea_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TryBeginOverlayResizeDrag(e))
        {
            return;
        }
    }

    private void OverlayBottomHotZone_OnPointerActivity(object? sender, PointerEventArgs e)
    {
        UpdateOverlayResizeCursor(e);
        ShowOverlayControlsTemporarily();
    }

    private void OverlayBottomHotZone_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TryBeginOverlayResizeDrag(e))
        {
            return;
        }

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
            UpdateOverlayResizeChrome(window);
            InstallOverlayNativeShortcutHook(window);
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

        RemoveOverlayNativeShortcutHook();

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
            UpdateOverlayResizeChrome(window);
        }
        else if (TopLevel.GetTopLevel(this) is Window currentWindow)
        {
            RestoreOverlayWindowChrome(currentWindow);
            UpdateOverlayResizeChrome(currentWindow);
        }

        overlayFullscreenApplied = false;
        windowStateBeforeOverlay = null;
        windowDecorationsBeforeOverlayFullscreen = null;
        UpdateOverlayPopupState();
        UpdateOverlayResizeChrome();
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
                UpdateOverlayResizeChrome(window);
                FocusOverlayPlaybackRoot();
                SetOverlayWindowControlsVisible(false);
                return;
            }

            suppressNextOverlayMaximizedToFullscreen = false;
            UpdateOverlayWindowChrome(window);
            UpdateOverlayResizeChrome(window);
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

    private void InstallOverlayNativeShortcutHook(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero || handle == overlayWindowHandle)
        {
            return;
        }

        RemoveOverlayNativeShortcutHook();
        overlayWindowProcedure = OverlayNativeWindowProcedure;
        overlayPreviousWindowProcedure = SetWindowLongPtr(handle, WindowProcedureIndex, overlayWindowProcedure);
        overlayWindowHandle = handle;
    }

    private void RemoveOverlayNativeShortcutHook()
    {
        if (!OperatingSystem.IsWindows() ||
            overlayWindowHandle == IntPtr.Zero ||
            overlayPreviousWindowProcedure == IntPtr.Zero)
        {
            overlayWindowHandle = IntPtr.Zero;
            overlayPreviousWindowProcedure = IntPtr.Zero;
            overlayWindowProcedure = null;
            return;
        }

        SetWindowLongPtr(overlayWindowHandle, WindowProcedureIndex, overlayPreviousWindowProcedure);
        overlayWindowHandle = IntPtr.Zero;
        overlayPreviousWindowProcedure = IntPtr.Zero;
        overlayWindowProcedure = null;
    }

    private IntPtr OverlayNativeWindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam)
    {
        if (currentViewModel?.IsPlayerOverlayOpen == true)
        {
            if (message == WindowMessageNonClientHitTest &&
                TryResolveOverlayNativeHitTest(lParam) is { } hitTest)
            {
                return hitTest;
            }

            if (message is WindowMessageKeyDown or WindowMessageSysKeyDown &&
                TryHandleOverlayNativeShortcut(wParam.ToInt32()))
            {
                return IntPtr.Zero;
            }

            if (message == WindowMessageSysCommand &&
                ((int)wParam & 0xFFF0) == SystemCommandMaximize)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ToggleOverlayFullscreenFromMaximizeButton();
                    ShowOverlayControlsTemporarily();
                });
                return IntPtr.Zero;
            }
        }

        return overlayPreviousWindowProcedure == IntPtr.Zero
            ? IntPtr.Zero
            : CallWindowProc(overlayPreviousWindowProcedure, hwnd, message, wParam, lParam);
    }

    private IntPtr? TryResolveOverlayNativeHitTest(IntPtr lParam)
    {
        if (!CanShowOverlayResizeChrome() ||
            TopLevel.GetTopLevel(this) is not Window window)
        {
            return null;
        }

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero ||
            !GetWindowRect(handle, out var windowRect))
        {
            return null;
        }

        var edge = ResolveOverlayResizeEdgeFromScreenPoint(window, windowRect, DecodePointerLParam(lParam));
        return edge is null ? null : ResolveNativeResizeHitTest(edge.Value);
    }

    private bool TryHandleOverlayNativeShortcut(int virtualKey)
    {
        var key = MapNativeVirtualKey(virtualKey);
        if (key is null || !IsOverlayShortcutKey(key.Value))
        {
            return false;
        }

        Dispatcher.UIThread.Post(async () =>
        {
            if (!TryBeginOverlayShortcut(key.Value))
            {
                return;
            }

            await HandleOverlayShortcutKeyAsync(key.Value);
        });
        return true;
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
                    ExitOverlayFullscreen(window);
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
            ExitOverlayFullscreen(window);
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
        UpdateOverlayResizeChrome(window);
        FocusOverlayPlaybackRoot();
        SetOverlayWindowControlsVisible(false);
    }

    private void ToggleOverlayFullscreenFromMaximizeButton()
    {
        if (TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        if (window.WindowState == WindowState.FullScreen)
        {
            ExitOverlayFullscreenToWindowed(window);
            SetOverlayWindowControlsVisible(false);
            return;
        }

        EnterOverlayFullscreen(window);
    }

    private void EnterOverlayFullscreen(Window window)
    {
        if (!overlayFullscreenApplied)
        {
            windowStateBeforeOverlay = window.WindowState;
            overlayFullscreenApplied = true;
        }

        window.WindowState = WindowState.FullScreen;
        UpdateOverlayWindowChrome(window);
        UpdateOverlayResizeChrome(window);
        FocusOverlayPlaybackRoot();
        SetOverlayWindowControlsVisible(false);
    }

    private void ExitOverlayFullscreen(Window window)
    {
        var targetState = ResolveOverlayExitFullscreenState();
        suppressNextOverlayMaximizedToFullscreen = targetState == WindowState.Maximized;
        window.WindowState = targetState;
        RestoreOverlayWindowChrome(window);
        UpdateOverlayResizeChrome(window);
        FocusOverlayPlaybackRoot();
        ShowOverlayWindowControlsAfterWindowStateChange();
    }

    private void ExitOverlayFullscreenToWindowed(Window window)
    {
        suppressNextOverlayMaximizedToFullscreen = false;
        window.WindowState = WindowState.Normal;
        RestoreOverlayWindowChrome(window);
        UpdateOverlayResizeChrome(window);
        FocusOverlayPlaybackRoot();
        ShowOverlayWindowControlsAfterWindowStateChange();
    }

    private WindowState ResolveOverlayExitFullscreenState()
    {
        return windowStateBeforeOverlay == WindowState.Maximized
            ? WindowState.Maximized
            : WindowState.Normal;
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SendMessage(
        IntPtr handle,
        int message,
        IntPtr wParam,
        IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadCursor(IntPtr instanceHandle, IntPtr cursorName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursorHandle);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, NativeWindowProcedure procedure);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallWindowProc(
        IntPtr previousWindowProcedure,
        IntPtr handle,
        int message,
        IntPtr wParam,
        IntPtr lParam);

    private delegate IntPtr NativeWindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private readonly struct CursorPoint
    {
        public CursorPoint(int x, int y)
        {
            X = x;
            Y = y;
        }

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
