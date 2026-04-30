using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Core.Models.Playback;
using OmniPlay.Core.Settings;

namespace OmniPlay.Core.ViewModels.Player;

public partial class PlayerViewModel : ObservableObject
{
    private readonly IMediaPlayer mediaPlayer;
    private readonly ISubtitlePickerService? subtitlePickerService;
    private readonly SynchronizationContext? synchronizationContext;
    private Func<Task>? playPreviousEpisodeAction;
    private Func<Task>? playNextEpisodeAction;
    private Func<string, Task>? selectPlaybackNavigationItemAction;
    private Func<int, Task>? selectPlaybackNavigationSeasonAction;
    private CancellationTokenSource? pollingCancellationTokenSource;
    private bool isSeekInteractionActive;
    private bool isVolumeInteractionActive;
    private bool suppressPlaybackNavigationSeasonChange;
    private bool suppressTrackSelectionChange;
    private bool suppressSubtitleSizeSelectionChange;
    private double? pendingStartupSeekSeconds;
    private string defaultAudioTrackMode = PlaybackPreferenceSettings.DefaultAudioSmart;
    private string smartAudioTrackMode = string.Empty;
    private string defaultSubtitleTrack = PlaybackPreferenceSettings.DefaultSubtitleChinese;
    private bool defaultAudioTrackApplied;
    private bool defaultSubtitleTrackApplied;

    public PlayerViewModel(
        IMediaPlayer mediaPlayer,
        ISubtitlePickerService? subtitlePickerService = null)
    {
        this.mediaPlayer = mediaPlayer;
        this.subtitlePickerService = subtitlePickerService;
        synchronizationContext = SynchronizationContext.Current;

        TogglePlayPauseCommand = new AsyncRelayCommand(TogglePlayPauseAsync, CanControlPlayback);
        StopCommand = new AsyncRelayCommand(StopAsync, CanControlPlayback);
        ToggleMuteCommand = new AsyncRelayCommand(ToggleMuteAsync, CanControlPlayback);
        SeekBackwardCommand = new AsyncRelayCommand(() => SeekRelativeAsync(-10), CanSeekPlayback);
        SeekForwardCommand = new AsyncRelayCommand(() => SeekRelativeAsync(10), CanSeekPlayback);
        DecreaseVolumeCommand = new AsyncRelayCommand(() => AdjustVolumeAsync(-10), CanControlPlayback);
        IncreaseVolumeCommand = new AsyncRelayCommand(() => AdjustVolumeAsync(10), CanControlPlayback);
        LoadExternalSubtitleCommand = new AsyncRelayCommand(LoadExternalSubtitleAsync, CanLoadExternalSubtitle);
        DecreaseSubtitleDelayCommand = new AsyncRelayCommand(() => AdjustSubtitleDelayAsync(-0.5), CanAdjustSubtitleStyle);
        IncreaseSubtitleDelayCommand = new AsyncRelayCommand(() => AdjustSubtitleDelayAsync(0.5), CanAdjustSubtitleStyle);
        PlayPreviousEpisodeCommand = new AsyncRelayCommand(PlayPreviousEpisodeAsync, CanPlayPreviousEpisode);
        PlayNextEpisodeCommand = new AsyncRelayCommand(PlayNextEpisodeAsync, CanPlayNextEpisode);
        SelectPlaybackNavigationItemCommand = new AsyncRelayCommand<PlayerNavigationItem?>(SelectPlaybackNavigationItemAsync, CanSelectPlaybackNavigationItem);

        BackendName = mediaPlayer.BackendName;
        SelectSubtitleSizeOption(CurrentSubtitleFontSize);
        StatusMessage = mediaPlayer.IsAvailable
            ? "播放器已就绪。"
            : "尚未检测到 libmpv，当前仅打通了应用内播放器窗口链路。";
    }

    public IAsyncRelayCommand TogglePlayPauseCommand { get; }

    public IAsyncRelayCommand StopCommand { get; }

    public IAsyncRelayCommand ToggleMuteCommand { get; }

    public IAsyncRelayCommand SeekBackwardCommand { get; }

    public IAsyncRelayCommand SeekForwardCommand { get; }

    public IAsyncRelayCommand DecreaseVolumeCommand { get; }

    public IAsyncRelayCommand IncreaseVolumeCommand { get; }

    public IAsyncRelayCommand LoadExternalSubtitleCommand { get; }

    public IAsyncRelayCommand DecreaseSubtitleDelayCommand { get; }

    public IAsyncRelayCommand IncreaseSubtitleDelayCommand { get; }

    public IAsyncRelayCommand PlayPreviousEpisodeCommand { get; }

    public IAsyncRelayCommand PlayNextEpisodeCommand { get; }

    public IAsyncRelayCommand<PlayerNavigationItem?> SelectPlaybackNavigationItemCommand { get; }

    public ObservableCollection<PlayerTrackInfo> AudioTracks { get; } = [];

    public ObservableCollection<PlayerTrackInfo> SubtitleTracks { get; } = [];

    public ObservableCollection<PlayerNavigationItem> PlaybackNavigationItems { get; } = [];

    public ObservableCollection<int> PlaybackNavigationSeasons { get; } = [];

    public ObservableCollection<PlayerSubtitleSizeOption> SubtitleSizeOptions { get; } =
    [
        new(12, "小号"),
        new(16, "标准"),
        new(20, "大号"),
        new(24, "特大")
    ];

    public string VolumeText => $"{Math.Round(VolumePercent):0}%";

    public string RemainingTimeText
    {
        get
        {
            if (DurationSeconds <= 0)
            {
                return "-00:00";
            }

            var displayedPosition = isSeekInteractionActive ? SeekPositionSeconds : CurrentPositionSeconds;
            return $"-{FormatTime(Math.Max(DurationSeconds - displayedPosition, 0))}";
        }
    }

    public string SubtitleDelayText => $"{SubtitleDelaySeconds:+0.0;-0.0;0.0}s";

    public string SubtitleSizeText => SelectedSubtitleSizeOption?.Label ?? $"{CurrentSubtitleFontSize}";

    public double PlaybackProgressRatio =>
        DurationSeconds > 0
            ? Math.Clamp(CurrentPositionSeconds / DurationSeconds, 0, 1)
            : 0;

    public bool HasAudioTrackSelection => AudioTracks.Count > 1;

    public bool HasSubtitleTrackSelection => SubtitleTracks.Count > 0;

    public bool HasTrackSelection => HasAudioTrackSelection || HasSubtitleTrackSelection;

    public bool HasExternalSubtitlePicker => subtitlePickerService is not null;

    public bool HasTrackControls => HasTrackSelection || (HasExternalSubtitlePicker && IsPlaying);

    public bool HasSubtitleStyleControls => IsPlaying && IsAvailable;

    public bool HasPreviousEpisodeAction => playPreviousEpisodeAction is not null;

    public bool HasNextEpisodeAction => playNextEpisodeAction is not null;

    public bool ShouldShowNextEpisodeAction =>
        HasNextEpisodeAction && IsPlaying && PlaybackProgressRatio >= PlaybackProgressRules.CompletionRatio;

    public bool HasPlaybackNavigation => PlaybackNavigationItems.Count > 0;

    public bool HasPlaybackNavigationSeasons => PlaybackNavigationSeasons.Count > 1;

    [ObservableProperty]
    private string title = "OmniPlay Player";

    [ObservableProperty]
    private string currentFileName = "未选择文件";

    [ObservableProperty]
    private string currentFilePath = string.Empty;

    [ObservableProperty]
    private string backendName = "libmpv";

    [ObservableProperty]
    private string statusMessage = "正在初始化播放器...";

    [ObservableProperty]
    private bool isAvailable;

    [ObservableProperty]
    private bool isPlaying;

    [ObservableProperty]
    private bool isPaused;

    [ObservableProperty]
    private bool isPlaybackCompleted;

    [ObservableProperty]
    private bool isMuted;

    [ObservableProperty]
    private double volumePercent = 100;

    [ObservableProperty]
    private double currentPositionSeconds;

    [ObservableProperty]
    private double durationSeconds;

    [ObservableProperty]
    private double seekPositionSeconds;

    [ObservableProperty]
    private string currentPositionText = "00:00";

    [ObservableProperty]
    private string durationText = "00:00";

    [ObservableProperty]
    private string playPauseGlyph = ">";

    [ObservableProperty]
    private string muteGlyph = "V";

    [ObservableProperty]
    private PlayerTrackInfo? selectedAudioTrack;

    [ObservableProperty]
    private PlayerTrackInfo? selectedSubtitleTrack;

    [ObservableProperty]
    private string nextEpisodeActionText = "播放下一集";

    [ObservableProperty]
    private string playbackNavigationTitle = "剧集导航";

    [ObservableProperty]
    private string previousEpisodeActionText = "播放上一集";

    [ObservableProperty]
    private int selectedPlaybackNavigationSeason = 1;

    [ObservableProperty]
    private bool areControlsVisible = true;

    [ObservableProperty]
    private double subtitleDelaySeconds;

    [ObservableProperty]
    private int currentSubtitleFontSize = 16;

    [ObservableProperty]
    private PlayerSubtitleSizeOption? selectedSubtitleSizeOption;

    public void AttachToHost(IntPtr hostHandle)
    {
        mediaPlayer.AttachToHost(hostHandle);
    }

    public void ConfigureNextEpisodeAction(Func<Task>? action, string? actionText = null)
    {
        playNextEpisodeAction = action;
        NextEpisodeActionText = string.IsNullOrWhiteSpace(actionText) ? "播放下一集" : actionText.Trim();
        NotifyPlaybackNavigationStateChanged();
    }

    public void ConfigurePreviousEpisodeAction(Func<Task>? action, string? actionText = null)
    {
        playPreviousEpisodeAction = action;
        PreviousEpisodeActionText = string.IsNullOrWhiteSpace(actionText) ? "播放上一集" : actionText.Trim();
        NotifyPlaybackNavigationStateChanged();
    }

    public void ConfigureDefaultTracks(string? audioTrackMode, string? subtitleTrack, string? smartAudioTrackMode = null)
    {
        defaultAudioTrackMode = NormalizeDefaultAudioTrack(audioTrackMode);
        this.smartAudioTrackMode = NormalizeSmartAudioTrack(smartAudioTrackMode);
        defaultSubtitleTrack = NormalizeDefaultSubtitleTrack(subtitleTrack);
        defaultAudioTrackApplied = false;
        defaultSubtitleTrackApplied = false;

        if (IsPlaying && (AudioTracks.Count > 0 || SubtitleTracks.Count > 0))
        {
            ApplyDefaultTrackSelectionIfNeeded();
        }
    }

    public void ConfigurePlaybackNavigation(
        IReadOnlyList<PlayerNavigationItem> items,
        Func<string, Task>? action,
        string? title = null,
        IReadOnlyList<int>? seasons = null,
        int? selectedSeason = null,
        Func<int, Task>? seasonAction = null)
    {
        selectPlaybackNavigationItemAction = action;
        selectPlaybackNavigationSeasonAction = seasonAction;
        PlaybackNavigationTitle = string.IsNullOrWhiteSpace(title) ? "剧集导航" : title.Trim();
        ReplaceItems(PlaybackNavigationItems, items ?? []);
        ReplaceItems(PlaybackNavigationSeasons, seasons ?? []);

        suppressPlaybackNavigationSeasonChange = true;
        try
        {
            SelectedPlaybackNavigationSeason = selectedSeason
                ?? (PlaybackNavigationSeasons.Count > 0 ? PlaybackNavigationSeasons[0] : 1);
        }
        finally
        {
            suppressPlaybackNavigationSeasonChange = false;
        }
        OnPropertyChanged(nameof(HasPlaybackNavigation));
        OnPropertyChanged(nameof(HasPlaybackNavigationSeasons));
        SelectPlaybackNavigationItemCommand.NotifyCanExecuteChanged();
    }

    public Task OpenAsync(
        string filePath,
        double? startPositionSeconds = null,
        CancellationToken cancellationToken = default)
    {
        return OpenAsync(new PlaybackOpenRequest(filePath), startPositionSeconds, cancellationToken);
    }

    public async Task OpenAsync(
        PlaybackOpenRequest request,
        double? startPositionSeconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        CurrentFilePath = request.EffectiveDisplayPath;
        CurrentFileName = MediaSourcePathResolver.GetDisplayName(request.EffectiveDisplayPath);
        Title = $"OmniPlay Player - {CurrentFileName}";
        CurrentPositionSeconds = 0;
        DurationSeconds = 0;
        SeekPositionSeconds = 0;
        VolumePercent = 100;
        AreControlsVisible = true;
        IsPlaybackCompleted = false;
        pendingStartupSeekSeconds = NormalizeStartupSeekPosition(startPositionSeconds);
        defaultAudioTrackApplied = false;
        defaultSubtitleTrackApplied = false;
        ResetTrackSelection();
        RefreshTimeTexts();
        StatusMessage = "正在打开视频...";

        mediaPlayer.Initialize();
        IsAvailable = mediaPlayer.IsAvailable;
        BackendName = mediaPlayer.BackendName;

        var result = await mediaPlayer.OpenAsync(request, cancellationToken);
        StatusMessage = result.Message;
        IsPlaying = result.Succeeded;
        IsPaused = false;

        RefreshComputedProperties();

        if (result.Succeeded)
        {
            await RefreshPlaybackStateAsync(cancellationToken);
            await ApplyStartupSeekIfNeededAsync(cancellationToken);
            StartStatePolling();
        }
        else
        {
            StopStatePolling();
            ResetTrackSelection();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        StopStatePolling();
        await mediaPlayer.StopAsync(cancellationToken);

        IsPlaying = false;
        IsPaused = false;
        IsPlaybackCompleted = false;
        IsMuted = false;
        VolumePercent = 100;
        CurrentPositionSeconds = 0;
        DurationSeconds = 0;
        SeekPositionSeconds = 0;
        SubtitleDelaySeconds = 0;
        CurrentSubtitleFontSize = 16;
        SelectSubtitleSizeOption(CurrentSubtitleFontSize);
        ResetTrackSelection();
        StatusMessage = mediaPlayer.IsAvailable ? "播放已停止。" : "播放器尚未加载 libmpv。";
        RefreshTimeTexts();
        RefreshComputedProperties();
    }

    public void BeginSeekInteraction()
    {
        isSeekInteractionActive = true;
        SeekPositionSeconds = CurrentPositionSeconds;
        RefreshTimeTexts();
    }

    public void UpdateSeekPreview(double value)
    {
        if (!isSeekInteractionActive)
        {
            return;
        }

        SeekPositionSeconds = ClampPosition(value);
        RefreshTimeTexts();
    }

    public async Task CommitSeekAsync(double value, CancellationToken cancellationToken = default)
    {
        if (!CanSeekPlayback())
        {
            return;
        }

        var target = ClampPosition(value);
        isSeekInteractionActive = false;
        await mediaPlayer.SeekAsync(target, cancellationToken);
        CurrentPositionSeconds = target;
        SeekPositionSeconds = target;
        RefreshTimeTexts();
    }

    public void BeginVolumeInteraction()
    {
        isVolumeInteractionActive = true;
    }

    public void UpdateVolumePreview(double value)
    {
        if (!isVolumeInteractionActive)
        {
            return;
        }

        VolumePercent = NormalizeVolume(value);
    }

    public async Task CommitVolumeAsync(double value, CancellationToken cancellationToken = default)
    {
        if (!CanControlPlayback())
        {
            return;
        }

        isVolumeInteractionActive = false;
        await SetVolumePercentAsync(value, cancellationToken);
    }

    public async Task SetVolumePercentAsync(double value, CancellationToken cancellationToken = default)
    {
        var target = NormalizeVolume(value);
        await mediaPlayer.SetVolumeAsync(target, cancellationToken);
        VolumePercent = target;
        StatusMessage = IsMuted
            ? $"当前静音中，音量 {VolumeText}。"
            : $"音量 {VolumeText}。";
        RefreshComputedProperties();
    }

    public async Task SelectAudioTrackAsync(PlayerTrackInfo? track, CancellationToken cancellationToken = default)
    {
        if (!CanControlPlayback() || track?.TrackId is null)
        {
            return;
        }

        await mediaPlayer.SelectAudioTrackAsync(track.TrackId, cancellationToken);
        StatusMessage = $"已切换音轨：{track.DisplayName}";
    }

    public async Task SelectSubtitleTrackAsync(PlayerTrackInfo? track, CancellationToken cancellationToken = default)
    {
        if (!CanControlPlayback())
        {
            return;
        }

        if (track is not null && !track.IsOffOption && track.TrackId is null)
        {
            return;
        }

        var trackId = track is null || track.IsOffOption ? null : track.TrackId;
        await mediaPlayer.SelectSubtitleTrackAsync(trackId, cancellationToken);
        StatusMessage = trackId is null
            ? "已关闭字幕。"
            : $"已切换字幕：{track!.DisplayName}";
    }

    public async Task LoadExternalSubtitleAsync(CancellationToken cancellationToken = default)
    {
        if (!CanControlPlayback())
        {
            return;
        }

        if (subtitlePickerService is null)
        {
            StatusMessage = "当前环境不支持选择外挂字幕。";
            return;
        }

        var subtitlePath = await subtitlePickerService.PickSubtitleFileAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(subtitlePath))
        {
            return;
        }

        if (!File.Exists(subtitlePath))
        {
            StatusMessage = $"字幕文件不存在：{subtitlePath}";
            return;
        }

        var loaded = await mediaPlayer.LoadExternalSubtitleAsync(subtitlePath, cancellationToken);
        if (!loaded)
        {
            StatusMessage = $"加载外挂字幕失败：{Path.GetFileName(subtitlePath)}";
            return;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(80), cancellationToken);
        await RefreshPlaybackStateAsync(cancellationToken);
        StatusMessage = $"已加载外挂字幕：{Path.GetFileName(subtitlePath)}";
    }

    private async Task AdjustSubtitleDelayAsync(double deltaSeconds)
    {
        var target = Math.Round(SubtitleDelaySeconds + deltaSeconds, 1);
        await mediaPlayer.SetSubtitleDelayAsync(target);
        SubtitleDelaySeconds = target;
        StatusMessage = $"字幕同步 {SubtitleDelayText}";
    }

    private async Task ApplySubtitleSizeAsync(PlayerSubtitleSizeOption option)
    {
        await mediaPlayer.SetSubtitleFontSizeAsync(option.Size);
        CurrentSubtitleFontSize = option.Size;
        StatusMessage = $"字幕字号：{option.Label}";
    }

    private async Task TogglePlayPauseAsync()
    {
        var nextPaused = !IsPaused;
        await mediaPlayer.SetPausedAsync(nextPaused);
        IsPaused = nextPaused;
        StatusMessage = nextPaused ? "播放已暂停。" : "正在播放。";
        RefreshComputedProperties();
    }

    private async Task ToggleMuteAsync()
    {
        var nextMuted = !IsMuted;
        await mediaPlayer.SetMutedAsync(nextMuted);
        IsMuted = nextMuted;
        StatusMessage = nextMuted ? "已静音。" : "已取消静音。";
        RefreshComputedProperties();
    }

    private async Task AdjustVolumeAsync(double deltaPercent)
    {
        await SetVolumePercentAsync(VolumePercent + deltaPercent);
    }

    private async Task SeekRelativeAsync(double deltaSeconds)
    {
        var target = ClampPosition(CurrentPositionSeconds + deltaSeconds);
        await mediaPlayer.SeekAsync(target);
        CurrentPositionSeconds = target;
        SeekPositionSeconds = target;
        RefreshTimeTexts();
    }

    private async Task PlayPreviousEpisodeAsync()
    {
        var action = playPreviousEpisodeAction;
        if (action is null)
        {
            return;
        }

        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = $"播放上一集失败：{ex.Message}";
        }
    }

    private async Task PlayNextEpisodeAsync()
    {
        var action = playNextEpisodeAction;
        if (action is null)
        {
            return;
        }

        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = $"播放下一集失败：{ex.Message}";
        }
    }

    private async Task SelectPlaybackNavigationItemAsync(PlayerNavigationItem? item)
    {
        var action = selectPlaybackNavigationItemAction;
        if (action is null || item is null || string.IsNullOrWhiteSpace(item.Id) || item.IsCurrent)
        {
            return;
        }

        try
        {
            await action(item.Id);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = $"切换剧集失败：{ex.Message}";
        }
    }

    private async Task ApplyPlaybackNavigationSeasonAsync(int season)
    {
        var action = selectPlaybackNavigationSeasonAction;
        if (action is null)
        {
            return;
        }

        try
        {
            await action(season);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PostToUi(() => StatusMessage = $"切换季失败：{ex.Message}");
        }
    }

    private void StartStatePolling()
    {
        StopStatePolling();

        pollingCancellationTokenSource = new CancellationTokenSource();
        _ = PollStateAsync(pollingCancellationTokenSource.Token);
    }

    private void StopStatePolling()
    {
        pollingCancellationTokenSource?.Cancel();
        pollingCancellationTokenSource?.Dispose();
        pollingCancellationTokenSource = null;
        isSeekInteractionActive = false;
        isVolumeInteractionActive = false;
        pendingStartupSeekSeconds = null;
    }

    private async Task PollStateAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var state = await mediaPlayer.GetStateAsync(cancellationToken);
                PostToUi(() => ApplyPlaybackState(state));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshPlaybackStateAsync(CancellationToken cancellationToken)
    {
        var state = await mediaPlayer.GetStateAsync(cancellationToken);
        PostToUi(() => ApplyPlaybackState(state));
    }

    private void ApplyPlaybackState(PlayerPlaybackState state)
    {
        if (!IsPlaying)
        {
            return;
        }

        IsPaused = state.IsPaused;
        IsPlaybackCompleted = state.IsPlaybackCompleted;
        IsMuted = state.IsMuted;
        if (!isVolumeInteractionActive)
        {
            VolumePercent = NormalizeVolume(state.VolumePercent);
        }
        DurationSeconds = state.DurationSeconds > 0 ? state.DurationSeconds : DurationSeconds;
        SubtitleDelaySeconds = NormalizeSubtitleDelay(state.SubtitleDelaySeconds);
        CurrentSubtitleFontSize = NormalizeSubtitleFontSize(state.SubtitleFontSize);
        SelectSubtitleSizeOption(CurrentSubtitleFontSize);

        if (!isSeekInteractionActive)
        {
            CurrentPositionSeconds = state.PositionSeconds;
            SeekPositionSeconds = CurrentPositionSeconds;
        }

        ApplyTrackState(state.AudioTracks, state.SubtitleTracks);
        RefreshTimeTexts();
        RefreshComputedProperties();
    }

    private void ApplyTrackState(
        IReadOnlyList<PlayerTrackInfo> audioTracks,
        IReadOnlyList<PlayerTrackInfo> subtitleTracks)
    {
        var effectiveAudioTracks = audioTracks ?? [];
        var effectiveSubtitleTracks = BuildSubtitleTrackOptions(subtitleTracks ?? []);

        suppressTrackSelectionChange = true;
        try
        {
            var audioTracksChanged = ReplaceItems(AudioTracks, effectiveAudioTracks);
            var subtitleTracksChanged = ReplaceItems(SubtitleTracks, effectiveSubtitleTracks);
            SelectedAudioTrack = AudioTracks.FirstOrDefault(track => track.IsSelected) ?? AudioTracks.FirstOrDefault();
            SelectedSubtitleTrack =
                SubtitleTracks.FirstOrDefault(track => track.IsSelected)
                ?? SubtitleTracks.FirstOrDefault(track => track.IsOffOption);

            if (audioTracksChanged)
            {
                OnPropertyChanged(nameof(HasAudioTrackSelection));
            }

            if (subtitleTracksChanged)
            {
                OnPropertyChanged(nameof(HasSubtitleTrackSelection));
            }

            if (audioTracksChanged || subtitleTracksChanged)
            {
                OnPropertyChanged(nameof(HasTrackSelection));
                OnPropertyChanged(nameof(HasTrackControls));
            }
        }
        finally
        {
            suppressTrackSelectionChange = false;
        }

        ApplyDefaultTrackSelectionIfNeeded();
    }

    private void ApplyDefaultTrackSelectionIfNeeded()
    {
        if (!CanControlPlayback())
        {
            return;
        }

        if (!defaultAudioTrackApplied && AudioTracks.Count > 0)
        {
            defaultAudioTrackApplied = true;
            var preferredAudioTrack = ResolvePreferredAudioTrack();
            if (preferredAudioTrack is not null &&
                preferredAudioTrack.TrackId is not null &&
                preferredAudioTrack.TrackId != SelectedAudioTrack?.TrackId)
            {
                suppressTrackSelectionChange = true;
                try
                {
                    SelectedAudioTrack = preferredAudioTrack;
                }
                finally
                {
                    suppressTrackSelectionChange = false;
                }

                _ = ApplySelectedAudioTrackAsync(preferredAudioTrack);
            }
        }

        if (!defaultSubtitleTrackApplied && SubtitleTracks.Count > 0)
        {
            defaultSubtitleTrackApplied = true;
            var preferredSubtitleTrack = ResolvePreferredSubtitleTrack();
            if (preferredSubtitleTrack is not null &&
                preferredSubtitleTrack.TrackId != SelectedSubtitleTrack?.TrackId &&
                !preferredSubtitleTrack.IsOffOption)
            {
                suppressTrackSelectionChange = true;
                try
                {
                    SelectedSubtitleTrack = preferredSubtitleTrack;
                }
                finally
                {
                    suppressTrackSelectionChange = false;
                }

                _ = ApplySelectedSubtitleTrackAsync(preferredSubtitleTrack);
            }
        }
    }

    private PlayerTrackInfo? ResolvePreferredAudioTrack()
    {
        if (defaultAudioTrackMode == PlaybackPreferenceSettings.DefaultAudioSmart)
        {
            return string.IsNullOrWhiteSpace(smartAudioTrackMode)
                ? SelectedAudioTrack
                : AudioTracks.FirstOrDefault(track => MatchesPreferredTrackLanguage(track, smartAudioTrackMode)) ?? SelectedAudioTrack;
        }

        return AudioTracks.FirstOrDefault(track => MatchesPreferredTrackLanguage(track, defaultAudioTrackMode));
    }

    private PlayerTrackInfo? ResolvePreferredSubtitleTrack()
    {
        return SubtitleTracks.FirstOrDefault(track =>
            !track.IsOffOption && MatchesPreferredTrackLanguage(track, defaultSubtitleTrack));
    }

    private void ResetTrackSelection()
    {
        suppressTrackSelectionChange = true;
        try
        {
            AudioTracks.Clear();
            SubtitleTracks.Clear();
            SelectedAudioTrack = null;
            SelectedSubtitleTrack = null;
        }
        finally
        {
            suppressTrackSelectionChange = false;
        }

        OnPropertyChanged(nameof(HasAudioTrackSelection));
        OnPropertyChanged(nameof(HasSubtitleTrackSelection));
        OnPropertyChanged(nameof(HasTrackSelection));
        OnPropertyChanged(nameof(HasTrackControls));
    }

    private void RefreshComputedProperties()
    {
        PlayPauseGlyph = IsPaused ? ">" : "II";
        MuteGlyph = IsMuted ? "M" : "V";
        OnPropertyChanged(nameof(RemainingTimeText));
        OnPropertyChanged(nameof(SubtitleDelayText));
        OnPropertyChanged(nameof(SubtitleSizeText));
        OnPropertyChanged(nameof(HasSubtitleStyleControls));

        TogglePlayPauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ToggleMuteCommand.NotifyCanExecuteChanged();
        SeekBackwardCommand.NotifyCanExecuteChanged();
        SeekForwardCommand.NotifyCanExecuteChanged();
        DecreaseVolumeCommand.NotifyCanExecuteChanged();
        IncreaseVolumeCommand.NotifyCanExecuteChanged();
        LoadExternalSubtitleCommand.NotifyCanExecuteChanged();
        DecreaseSubtitleDelayCommand.NotifyCanExecuteChanged();
        IncreaseSubtitleDelayCommand.NotifyCanExecuteChanged();
        PlayPreviousEpisodeCommand.NotifyCanExecuteChanged();
        PlayNextEpisodeCommand.NotifyCanExecuteChanged();
        SelectPlaybackNavigationItemCommand.NotifyCanExecuteChanged();
    }

    private void RefreshTimeTexts()
    {
        var displayedPosition = isSeekInteractionActive ? SeekPositionSeconds : CurrentPositionSeconds;
        CurrentPositionText = FormatTime(displayedPosition);
        DurationText = FormatTime(DurationSeconds);
        OnPropertyChanged(nameof(RemainingTimeText));
    }

    private bool CanControlPlayback() => IsPlaying && IsAvailable;

    private bool CanSeekPlayback() => CanControlPlayback() && DurationSeconds > 0;

    private bool CanLoadExternalSubtitle() => CanControlPlayback() && HasExternalSubtitlePicker;

    private bool CanAdjustSubtitleStyle() => CanControlPlayback();

    private bool CanPlayPreviousEpisode() => IsPlaying && playPreviousEpisodeAction is not null;

    private bool CanPlayNextEpisode() => IsPlaying && playNextEpisodeAction is not null;

    private bool CanSelectPlaybackNavigationItem(PlayerNavigationItem? item) =>
        selectPlaybackNavigationItemAction is not null &&
        item is not null &&
        !string.IsNullOrWhiteSpace(item.Id) &&
        !item.IsCurrent;

    private double ClampPosition(double value)
    {
        var max = DurationSeconds > 0 ? DurationSeconds : Math.Max(value, 0);
        return Math.Clamp(value, 0, max);
    }

    private void PostToUi(Action action)
    {
        if (synchronizationContext is null || SynchronizationContext.Current == synchronizationContext)
        {
            action();
            return;
        }

        synchronizationContext.Post(_ => action(), null);
    }

    private static string NormalizeDefaultAudioTrack(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return PlaybackPreferenceSettings.DefaultAudioSmart;
        }

        return normalized.ToLowerInvariant() switch
        {
            "chi" or "zho" or "zh" or "zh-cn" or "cn" => PlaybackPreferenceSettings.AudioChinese,
            "eng" or "en" or "en-us" => PlaybackPreferenceSettings.AudioEnglish,
            "jpn" or "ja" or "ja-jp" => PlaybackPreferenceSettings.AudioJapanese,
            _ => PlaybackPreferenceSettings.DefaultAudioSmart
        };
    }

    private static string NormalizeDefaultSubtitleTrack(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return PlaybackPreferenceSettings.DefaultSubtitleChinese;
        }

        return normalized.ToLowerInvariant() switch
        {
            "eng" or "en" or "en-us" => PlaybackPreferenceSettings.SubtitleEnglish,
            _ => PlaybackPreferenceSettings.DefaultSubtitleChinese
        };
    }

    private static string NormalizeSmartAudioTrack(string? value)
    {
        var normalized = NormalizeDefaultAudioTrack(value);
        return normalized == PlaybackPreferenceSettings.DefaultAudioSmart
            ? string.Empty
            : normalized;
    }

    private static bool MatchesPreferredTrackLanguage(PlayerTrackInfo track, string preferredLanguage)
    {
        if (track.IsOffOption)
        {
            return false;
        }

        var language = track.Language.Trim().ToLowerInvariant();
        var displayName = track.DisplayName.Trim().ToLowerInvariant();

        return preferredLanguage switch
        {
            PlaybackPreferenceSettings.AudioChinese => IsChineseTrack(language, displayName),
            PlaybackPreferenceSettings.AudioEnglish => IsEnglishTrack(language, displayName),
            PlaybackPreferenceSettings.AudioJapanese => IsJapaneseTrack(language, displayName),
            _ => IsChineseTrack(language, displayName)
        };
    }

    private static bool IsChineseTrack(string language, string displayName)
    {
        return MatchesAny(language, "chi", "zho", "zh", "zh-cn", "zh-hans", "zh-tw", "zh-hant", "chs", "cht", "cn") ||
               ContainsAny(displayName, "中文", "国语", "普通话", "简体", "繁体", "简中", "繁中", "粤语", "chinese", "mandarin", "cantonese", "chi", "zho", "chs", "cht");
    }

    private static bool IsEnglishTrack(string language, string displayName)
    {
        return MatchesAny(language, "eng", "en", "en-us", "en-gb") ||
               ContainsAny(displayName, "英语", "英文", "english", "eng");
    }

    private static bool IsJapaneseTrack(string language, string displayName)
    {
        return MatchesAny(language, "jpn", "ja", "ja-jp") ||
               ContainsAny(displayName, "日语", "日文", "japanese", "jpn");
    }

    private static bool MatchesAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<PlayerTrackInfo> BuildSubtitleTrackOptions(IReadOnlyList<PlayerTrackInfo> tracks)
    {
        if (tracks.Count == 0)
        {
            return [];
        }

        var hasSelectedTrack = tracks.Any(track => track.IsSelected);
        var items = new List<PlayerTrackInfo>(tracks.Count + 1)
        {
            new("sub", null, "关闭字幕", !hasSelectedTrack, true)
        };

        items.AddRange(tracks);
        return items;
    }

    private static bool ReplaceItems<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        if (target.SequenceEqual(source))
        {
            return false;
        }

        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }

        return true;
    }

    private static string FormatTime(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return "00:00";
        }

        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    private async Task ApplyStartupSeekIfNeededAsync(CancellationToken cancellationToken)
    {
        if (pendingStartupSeekSeconds is not > 5)
        {
            return;
        }

        var target = pendingStartupSeekSeconds.Value;
        pendingStartupSeekSeconds = null;
        await ApplyStartupSeekAsync(target, cancellationToken);
    }

    private async Task ApplyStartupSeekAsync(double startPositionSeconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(180), cancellationToken);
            await mediaPlayer.SeekAsync(startPositionSeconds, cancellationToken);
            PostToUi(() =>
            {
                CurrentPositionSeconds = startPositionSeconds;
                SeekPositionSeconds = startPositionSeconds;
                RefreshTimeTexts();
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ApplySelectedAudioTrackAsync(PlayerTrackInfo? track)
    {
        try
        {
            await SelectAudioTrackAsync(track);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PostToUi(() => StatusMessage = $"切换音轨失败：{ex.Message}");
        }
    }

    private async Task ApplySelectedSubtitleTrackAsync(PlayerTrackInfo? track)
    {
        try
        {
            await SelectSubtitleTrackAsync(track);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PostToUi(() => StatusMessage = $"切换字幕失败：{ex.Message}");
        }
    }

    private static double? NormalizeStartupSeekPosition(double? value)
    {
        if (value is not > 5 || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return null;
        }

        return Math.Max(value.Value, 0);
    }

    private static double NormalizeVolume(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 100;
        }

        return Math.Clamp(value, 0, 100);
    }

    private static double NormalizeSubtitleDelay(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        return Math.Round(value, 1);
    }

    private static int NormalizeSubtitleFontSize(int value)
    {
        return value > 0 ? value : 16;
    }

    private PlayerSubtitleSizeOption ResolveSubtitleSizeOption(int fontSize)
    {
        return SubtitleSizeOptions.FirstOrDefault(option => option.Size == fontSize)
            ?? SubtitleSizeOptions.First(option => option.Size == 16);
    }

    private void SelectSubtitleSizeOption(int fontSize)
    {
        suppressSubtitleSizeSelectionChange = true;
        try
        {
            SelectedSubtitleSizeOption = ResolveSubtitleSizeOption(fontSize);
        }
        finally
        {
            suppressSubtitleSizeSelectionChange = false;
        }
    }

    partial void OnVolumePercentChanged(double value)
    {
        var normalized = NormalizeVolume(value);
        if (Math.Abs(normalized - value) > 0.001)
        {
            VolumePercent = normalized;
            return;
        }

        OnPropertyChanged(nameof(VolumeText));
    }

    partial void OnSubtitleDelaySecondsChanged(double value)
    {
        OnPropertyChanged(nameof(SubtitleDelayText));
    }

    partial void OnCurrentSubtitleFontSizeChanged(int value)
    {
        OnPropertyChanged(nameof(SubtitleSizeText));
    }

    partial void OnSelectedSubtitleSizeOptionChanged(PlayerSubtitleSizeOption? value)
    {
        OnPropertyChanged(nameof(SubtitleSizeText));

        if (suppressSubtitleSizeSelectionChange || value is null || !CanAdjustSubtitleStyle())
        {
            return;
        }

        _ = ApplySubtitleSizeAsync(value);
    }

    partial void OnSelectedAudioTrackChanged(PlayerTrackInfo? value)
    {
        if (suppressTrackSelectionChange || value is null)
        {
            return;
        }

        _ = ApplySelectedAudioTrackAsync(value);
    }

    partial void OnSelectedSubtitleTrackChanged(PlayerTrackInfo? value)
    {
        if (suppressTrackSelectionChange || value is null)
        {
            return;
        }

        _ = ApplySelectedSubtitleTrackAsync(value);
    }

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(HasTrackControls));
        OnPropertyChanged(nameof(HasSubtitleStyleControls));
        NotifyPlaybackNavigationStateChanged();
    }

    partial void OnCurrentPositionSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(RemainingTimeText));
        NotifyPlaybackNavigationStateChanged();
    }

    partial void OnDurationSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(RemainingTimeText));
        NotifyPlaybackNavigationStateChanged();
    }

    partial void OnSelectedPlaybackNavigationSeasonChanged(int value)
    {
        if (suppressPlaybackNavigationSeasonChange)
        {
            return;
        }

        _ = ApplyPlaybackNavigationSeasonAsync(value);
    }

    private void NotifyPlaybackNavigationStateChanged()
    {
        OnPropertyChanged(nameof(PlaybackProgressRatio));
        OnPropertyChanged(nameof(HasPreviousEpisodeAction));
        OnPropertyChanged(nameof(HasNextEpisodeAction));
        OnPropertyChanged(nameof(ShouldShowNextEpisodeAction));
        PlayPreviousEpisodeCommand.NotifyCanExecuteChanged();
        PlayNextEpisodeCommand.NotifyCanExecuteChanged();
    }
}
