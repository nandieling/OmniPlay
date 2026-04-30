using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Core.Models.Library;
using OmniPlay.Core.Models.Network;
using OmniPlay.Core.Models.Playback;
using OmniPlay.Core.Settings;
using OmniPlay.Core.ViewModels.Player;
using OmniPlay.Core.ViewModels.Settings;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;

namespace OmniPlay.Core.ViewModels.Library;

public partial class PosterWallViewModel : ObservableObject
{
    private readonly IMovieRepository movieRepository;
    private readonly ITvShowRepository tvShowRepository;
    private readonly IMediaSourceRepository mediaSourceRepository;
    private readonly IVideoFileRepository videoFileRepository;
    private readonly ILibraryScanner libraryScanner;
    private readonly ILibraryMetadataEditor libraryMetadataEditor;
    private readonly ILibraryMetadataEnricher libraryMetadataEnricher;
    private readonly ILibraryThumbnailEnricher libraryThumbnailEnricher;
    private readonly IFolderPickerService folderPickerService;
    private readonly IPosterImagePickerService posterImagePickerService;
    private readonly IWebDavConnectionTester webDavConnectionTester;
    private readonly INetworkShareDiscoveryService networkShareDiscoveryService;
    private readonly IPlaybackLauncher playbackLauncher;
    private readonly List<Movie> allMovies = [];
    private readonly List<TvShow> allTvShows = [];
    private readonly List<LibraryPosterItem> allLibraryItems = [];
    private readonly List<LibraryVideoItem> allDetailFiles = [];
    private readonly SemaphoreSlim libraryRefreshGate = new(1, 1);
    private CancellationTokenSource? libraryAutomationCancellationTokenSource;
    private CancellationTokenSource? playbackProgressSyncCancellationTokenSource;
    private long? currentDetailMovieId;
    private long? currentDetailTvShowId;
    private string currentPlayingVideoId = string.Empty;
    private int? selectedPlaybackNavigationSeason;
    private string? preferredDetailVideoId;
    private string? suppressedPlaybackPersistenceVideoId;
    private string? autoAdvancedPlaybackVideoId;
    private bool isAutoAdvancingToNextEpisode;
    private Task? libraryRefreshTask;
    private bool forceRefreshQueued;
    private bool startupScanAttempted;
    private bool startupScanInProgress;
    private bool networkDiscoveryInProgress;
    private PlaybackMode currentPlaybackMode;
    private double lastSyncedPlaybackPositionSeconds = double.NaN;
    private double lastSyncedPlaybackDurationSeconds = double.NaN;

    public PosterWallViewModel(
        IMovieRepository movieRepository,
        ITvShowRepository tvShowRepository,
        IMediaSourceRepository mediaSourceRepository,
        IVideoFileRepository videoFileRepository,
        ILibraryScanner libraryScanner,
        ILibraryMetadataEditor libraryMetadataEditor,
        ILibraryMetadataEnricher libraryMetadataEnricher,
        ILibraryThumbnailEnricher libraryThumbnailEnricher,
        IFolderPickerService folderPickerService,
        IPosterImagePickerService posterImagePickerService,
        IWebDavConnectionTester webDavConnectionTester,
        INetworkShareDiscoveryService networkShareDiscoveryService,
        IPlaybackLauncher playbackLauncher,
        SettingsViewModel settings,
        PlayerViewModel player)
    {
        this.movieRepository = movieRepository;
        this.tvShowRepository = tvShowRepository;
        this.mediaSourceRepository = mediaSourceRepository;
        this.videoFileRepository = videoFileRepository;
        this.libraryScanner = libraryScanner;
        this.libraryMetadataEditor = libraryMetadataEditor;
        this.libraryMetadataEnricher = libraryMetadataEnricher;
        this.libraryThumbnailEnricher = libraryThumbnailEnricher;
        this.folderPickerService = folderPickerService;
        this.posterImagePickerService = posterImagePickerService;
        this.webDavConnectionTester = webDavConnectionTester;
        this.networkShareDiscoveryService = networkShareDiscoveryService;
        this.playbackLauncher = playbackLauncher;

        Settings = settings;
        Player = player;

        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy && !startupScanInProgress);
        AddFolderSourceCommand = new AsyncRelayCommand(AddFolderSourceAsync, () => !IsBusy);
        AddWebDavSourceCommand = new AsyncRelayCommand(AddWebDavSourceAsync, () => !IsBusy);
        TestWebDavSourceCommand = new AsyncRelayCommand(TestWebDavSourceAsync, () => !IsBusy);
        OpenDetailCommand = new AsyncRelayCommand<LibraryPosterItem?>(OpenDetailAsync, item => item is not null);
        PlayVideoCommand = new AsyncRelayCommand<LibraryVideoItem?>(PlayVideoAsync, video => video is not null);
        PlayPrimaryCommand = new AsyncRelayCommand(PlayPrimaryAsync, () => DetailPrimaryFile is not null);
        OpenStandalonePlayerCommand = new AsyncRelayCommand<LibraryVideoItem?>(OpenStandalonePlayerAsync, video => video is not null);
        OpenStandalonePrimaryCommand = new AsyncRelayCommand(OpenStandalonePrimaryAsync, () => DetailPrimaryFile is not null);
        RefreshDetailMetadataCommand = new AsyncRelayCommand(
            RefreshDetailMetadataAsync,
            () => (currentDetailMovieId.HasValue || currentDetailTvShowId.HasValue) && !IsBusy);
        SearchDetailMetadataCandidatesCommand = new AsyncRelayCommand(
            SearchDetailMetadataCandidatesAsync,
            () => (currentDetailMovieId.HasValue || currentDetailTvShowId.HasValue) && !IsBusy);
        ApplyDetailMetadataCandidateCommand = new AsyncRelayCommand<LibraryMetadataSearchCandidate?>(
            ApplyDetailMetadataCandidateAsync,
            candidate => candidate is not null && !IsBusy);
        ToggleDetailLockCommand = new AsyncRelayCommand(
            ToggleDetailLockAsync,
            () => (currentDetailMovieId.HasValue || currentDetailTvShowId.HasValue) && !IsBusy);
        ShowDetailMetadataCandidatesPanelCommand = new RelayCommand(
            ShowDetailMetadataCandidatesPanel,
            () => HasDetailMetadataCandidates && !IsDetailMetadataCandidatePanelOpen && !IsBusy);
        HideDetailMetadataCandidatesPanelCommand = new RelayCommand(
            HideDetailMetadataCandidatesPanel,
            () => IsDetailMetadataCandidatePanelOpen && !IsBusy);
        ClearDetailMetadataCandidatesCommand = new RelayCommand(ClearDetailMetadataCandidates);
        SelectDetailFileCommand = new RelayCommand<LibraryVideoItem?>(SelectDetailFile);
        ToggleDetailWatchedCommand = new AsyncRelayCommand<LibraryVideoItem?>(ToggleDetailWatchedAsync, video => video is not null && !IsBusy);
        OpenPosterWatchStateConfirmationCommand = new RelayCommand<LibraryPosterItem?>(
            OpenPosterWatchStateConfirmation,
            item => item is not null && !IsBusy);
        CancelPosterWatchStateConfirmationCommand = new RelayCommand(
            ClosePosterWatchStateConfirmation,
            () => IsPosterWatchStateConfirmationOpen && !IsBusy);
        ConfirmPosterWatchStateCommand = new AsyncRelayCommand(
            ConfirmPosterWatchStateAsync,
            () => posterWatchStateTarget is not null && !IsBusy);
        OpenPosterScrapeCommand = new AsyncRelayCommand<LibraryPosterItem?>(OpenPosterScrapeAsync, item => item is not null);
        ClosePosterScrapeCommand = new RelayCommand(ClosePosterScrape);
        SearchPosterMetadataCandidatesCommand = new AsyncRelayCommand(SearchPosterMetadataCandidatesAsync, () => posterMetadataTarget is not null && !IsBusy);
        ApplyPosterMetadataCandidateCommand = new AsyncRelayCommand<LibraryMetadataSearchCandidate?>(
            ApplyPosterMetadataCandidateAsync,
            candidate => candidate is not null && posterMetadataTarget is not null && !IsBusy);
        OpenPosterEditCommand = new AsyncRelayCommand<LibraryPosterItem?>(OpenPosterEditAsync, item => item is not null);
        ClosePosterEditCommand = new RelayCommand(ClosePosterEdit);
        ChoosePosterImageCommand = new AsyncRelayCommand(ChoosePosterImageAsync, () => isPosterEditPanelOpen && !IsBusy);
        SavePosterMetadataEditCommand = new AsyncRelayCommand(SavePosterMetadataEditAsync, () => posterMetadataTarget is not null && !IsBusy);
        OpenEpisodeEditCommand = new RelayCommand<LibraryVideoItem?>(OpenEpisodeEdit, video => video is not null && IsDetailSeries);
        CloseEpisodeEditCommand = new RelayCommand(CloseEpisodeEdit);
        SaveEpisodeEditCommand = new AsyncRelayCommand(SaveEpisodeEditAsync, () => episodeEditTarget is not null && !IsBusy);
        ChooseEpisodeThumbnailImageCommand = new AsyncRelayCommand(ChooseEpisodeThumbnailImageAsync, () => isEpisodeEditPanelOpen && !IsBusy);
        EditSourceCommand = new AsyncRelayCommand<MediaSource?>(EditSourceAsync, source => source is not null && !IsBusy);
        CancelWebDavEditCommand = new RelayCommand(CancelWebDavEdit, () => IsEditingWebDavSource && !IsBusy);
        RemoveSourceCommand = new AsyncRelayCommand<MediaSource?>(RemoveSourceAsync, source => source?.Id is not null);
        ToggleSourceEnabledCommand = new AsyncRelayCommand<MediaSource?>(ToggleSourceEnabledAsync, source => source?.Id is not null && !IsBusy);
        RefreshNetworkSourcesCommand = new AsyncRelayCommand(RefreshNetworkSourcesAsync, () => !networkDiscoveryInProgress);
        OpenNetworkLoginCommand = new RelayCommand<NetworkSourceDiscoveryItem?>(OpenNetworkLogin, item => item is not null && !IsBusy);
        OpenManualNetworkLoginCommand = new RelayCommand(OpenManualNetworkLogin, () => !IsBusy);
        CloseNetworkLoginCommand = new RelayCommand(CloseNetworkLogin);
        SaveNetworkLoginCommand = new AsyncRelayCommand(SaveNetworkLoginAsync, () => IsNetworkLoginPanelOpen && !IsBusy);
        MountNetworkFolderCommand = new AsyncRelayCommand<NetworkShareFolderItem?>(MountNetworkFolderAsync, folder => folder is not null && !IsBusy);
        SelectSortOptionCommand = new RelayCommand<LibrarySortOptionItem?>(SelectSortOption);
        ToggleSortDirectionCommand = new RelayCommand(ToggleSortDirection);
        SelectSeasonCommand = new RelayCommand<int?>(SelectSeason);
        ToggleSearchPopupCommand = new RelayCommand(ToggleSearchPopup);
        ToggleSortPopupCommand = new RelayCommand(ToggleSortPopup);
        ToggleSourcePopupCommand = new RelayCommand(ToggleSourcePopup);
        ToggleSettingsPopupCommand = new RelayCommand(ToggleSettingsPopup);
        CloseDetailCommand = new RelayCommand(CloseDetail);
        ClosePlayerOverlayCommand = new AsyncRelayCommand(ClosePlayerOverlayAsync, () => IsPlayerOverlayOpen);

        Settings.SettingsSaved += OnSettingsSaved;
        Player.PropertyChanged += OnPlayerPropertyChanged;
        selectedSortOptionItem = SortOptions[0];
    }

    public ObservableCollection<LibraryPosterItem> LibraryItems { get; } = [];

    public ObservableCollection<LibraryPosterItem> ContinueWatchingItems { get; } = [];

    public ObservableCollection<MediaSource> MediaSources { get; } = [];

    public ObservableCollection<NetworkSourceDiscoveryItem> DiscoveredNetworkSources { get; } = [];

    public ObservableCollection<NetworkShareFolderItem> NetworkShareFolders { get; } = [];

    public ObservableCollection<LibraryVideoItem> DetailFiles { get; } = [];

    public ObservableCollection<LibraryMetadataSearchCandidate> DetailMetadataCandidates { get; } = [];

    public ObservableCollection<LibraryMetadataSearchCandidate> PosterMetadataCandidates { get; } = [];

    public ObservableCollection<int> AvailableSeasons { get; } = [];

    public IReadOnlyList<LibrarySortOptionItem> SortOptions { get; } =
    [
        new(LibrarySortOption.Title, "\u6309\u6807\u9898"),
        new(LibrarySortOption.Year, "\u6309\u5E74\u4EFD"),
        new(LibrarySortOption.Rating, "\u6309\u8BC4\u5206")
    ];

    public SettingsViewModel Settings { get; }

    public PlayerViewModel Player { get; }

    public IAsyncRelayCommand ScanCommand { get; }

    public IAsyncRelayCommand AddFolderSourceCommand { get; }

    public IAsyncRelayCommand AddWebDavSourceCommand { get; }

    public IAsyncRelayCommand TestWebDavSourceCommand { get; }

    public IAsyncRelayCommand<LibraryPosterItem?> OpenDetailCommand { get; }

    public IAsyncRelayCommand<LibraryVideoItem?> PlayVideoCommand { get; }

    public IAsyncRelayCommand PlayPrimaryCommand { get; }

    public IAsyncRelayCommand<LibraryVideoItem?> OpenStandalonePlayerCommand { get; }

    public IAsyncRelayCommand OpenStandalonePrimaryCommand { get; }

    public IAsyncRelayCommand RefreshDetailMetadataCommand { get; }

    public IAsyncRelayCommand SearchDetailMetadataCandidatesCommand { get; }

    public IAsyncRelayCommand<LibraryMetadataSearchCandidate?> ApplyDetailMetadataCandidateCommand { get; }

    public IAsyncRelayCommand ToggleDetailLockCommand { get; }

    public IRelayCommand ShowDetailMetadataCandidatesPanelCommand { get; }

    public IRelayCommand HideDetailMetadataCandidatesPanelCommand { get; }

    public IRelayCommand ClearDetailMetadataCandidatesCommand { get; }

    public IRelayCommand<LibraryVideoItem?> SelectDetailFileCommand { get; }

    public IAsyncRelayCommand<LibraryVideoItem?> ToggleDetailWatchedCommand { get; }

    public IRelayCommand<LibraryPosterItem?> OpenPosterWatchStateConfirmationCommand { get; }

    public IRelayCommand CancelPosterWatchStateConfirmationCommand { get; }

    public IAsyncRelayCommand ConfirmPosterWatchStateCommand { get; }

    public IAsyncRelayCommand<LibraryPosterItem?> OpenPosterScrapeCommand { get; }

    public IRelayCommand ClosePosterScrapeCommand { get; }

    public IAsyncRelayCommand SearchPosterMetadataCandidatesCommand { get; }

    public IAsyncRelayCommand<LibraryMetadataSearchCandidate?> ApplyPosterMetadataCandidateCommand { get; }

    public IAsyncRelayCommand<LibraryPosterItem?> OpenPosterEditCommand { get; }

    public IRelayCommand ClosePosterEditCommand { get; }

    public IAsyncRelayCommand ChoosePosterImageCommand { get; }

    public IAsyncRelayCommand SavePosterMetadataEditCommand { get; }

    public IRelayCommand<LibraryVideoItem?> OpenEpisodeEditCommand { get; }

    public IRelayCommand CloseEpisodeEditCommand { get; }

    public IAsyncRelayCommand SaveEpisodeEditCommand { get; }

    public IAsyncRelayCommand ChooseEpisodeThumbnailImageCommand { get; }

    public IAsyncRelayCommand<MediaSource?> EditSourceCommand { get; }

    public IRelayCommand CancelWebDavEditCommand { get; }

    public IAsyncRelayCommand<MediaSource?> RemoveSourceCommand { get; }

    public IAsyncRelayCommand<MediaSource?> ToggleSourceEnabledCommand { get; }

    public IAsyncRelayCommand RefreshNetworkSourcesCommand { get; }

    public IRelayCommand<NetworkSourceDiscoveryItem?> OpenNetworkLoginCommand { get; }

    public IRelayCommand OpenManualNetworkLoginCommand { get; }

    public IRelayCommand CloseNetworkLoginCommand { get; }

    public IAsyncRelayCommand SaveNetworkLoginCommand { get; }

    public IAsyncRelayCommand<NetworkShareFolderItem?> MountNetworkFolderCommand { get; }

    public IRelayCommand<LibrarySortOptionItem?> SelectSortOptionCommand { get; }

    public IRelayCommand ToggleSortDirectionCommand { get; }

    public IRelayCommand<int?> SelectSeasonCommand { get; }

    public IRelayCommand ToggleSearchPopupCommand { get; }

    public IRelayCommand ToggleSortPopupCommand { get; }

    public IRelayCommand ToggleSourcePopupCommand { get; }

    public IRelayCommand ToggleSettingsPopupCommand { get; }

    public IRelayCommand CloseDetailCommand { get; }

    public IAsyncRelayCommand ClosePlayerOverlayCommand { get; }

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isSourcePopupOpen;

    [ObservableProperty]
    private string pendingWebDavName = string.Empty;

    [ObservableProperty]
    private string pendingWebDavUrl = string.Empty;

    [ObservableProperty]
    private string pendingWebDavUsername = string.Empty;

    [ObservableProperty]
    private string pendingWebDavPassword = string.Empty;

    [ObservableProperty]
    private long? editingWebDavSourceId;

    [ObservableProperty]
    private bool isNetworkLoginPanelOpen;

    [ObservableProperty]
    private string networkLoginTitle = "登录网络媒体源";

    [ObservableProperty]
    private string pendingNetworkProtocolType = string.Empty;

    [ObservableProperty]
    private string pendingNetworkBaseUrl = string.Empty;

    [ObservableProperty]
    private string pendingNetworkDisplayName = string.Empty;

    [ObservableProperty]
    private string pendingNetworkUsername = string.Empty;

    [ObservableProperty]
    private string pendingNetworkPassword = string.Empty;

    [ObservableProperty]
    private string networkSourceStatus = string.Empty;

    [ObservableProperty]
    private bool isSettingsPopupOpen;

    [ObservableProperty]
    private bool isSearchPopupOpen;

    [ObservableProperty]
    private bool isSortPopupOpen;

    [ObservableProperty]
    private bool isDetailOpen;

    [ObservableProperty]
    private bool isPlayerOverlayOpen;

    [ObservableProperty]
    private bool isLibraryScanInProgress;

    [ObservableProperty]
    private string pendingPlaybackFilePath = string.Empty;

    [ObservableProperty]
    private string pendingPlaybackDisplayPath = string.Empty;

    [ObservableProperty]
    private double pendingPlaybackStartPositionSeconds;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private LibraryScanSummary? lastScanSummary;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private LibrarySortOption selectedSortOption = LibrarySortOption.Title;

    [ObservableProperty]
    private LibrarySortOptionItem selectedSortOptionItem;

    [ObservableProperty]
    private bool isSortDescending;

    [ObservableProperty]
    private string detailTitle = "\u672A\u9009\u62E9\u6761\u76EE";

    [ObservableProperty]
    private string detailMeta = "\u70B9\u51FB\u6D77\u62A5\u540E\u53EF\u67E5\u770B\u8BE6\u60C5\u3001\u5206\u96C6\u548C\u64AD\u653E\u8FDB\u5EA6\u3002";

    [ObservableProperty]
    private string detailOverview = "Windows \u7248\u6B63\u5728\u6309 mac \u7248\u7684\u5A92\u4F53\u5E93\u4EA4\u4E92\u9010\u6B65\u8865\u9F50\u3002";

    [ObservableProperty]
    private string? detailPosterPath;

    [ObservableProperty]
    private string detailRatingText = "\u8BC4\u5206\u5F85\u8865\u5145";

    [ObservableProperty]
    private string detailKindText = "\u672A\u5206\u7C7B";

    [ObservableProperty]
    private bool isDetailLocked;

    [ObservableProperty]
    private string detailLockStateText = "\u672A\u9501\u5B9A";

    [ObservableProperty]
    private string detailLockActionText = "\u9501\u5B9A\u5143\u6570\u636E";

    [ObservableProperty]
    private string detailScrapeQuery = string.Empty;

    [ObservableProperty]
    private bool isDetailMetadataCandidatePanelOpen;

    [ObservableProperty]
    private bool isPosterScrapePanelOpen;

    [ObservableProperty]
    private bool isPosterEditPanelOpen;

    [ObservableProperty]
    private bool isEpisodeEditPanelOpen;

    [ObservableProperty]
    private bool isPosterWatchStateConfirmationOpen;

    [ObservableProperty]
    private string posterWatchStateConfirmationTitle = string.Empty;

    [ObservableProperty]
    private string posterWatchStateConfirmationMessage = string.Empty;

    [ObservableProperty]
    private string posterWatchStateConfirmationActionText = string.Empty;

    [ObservableProperty]
    private string posterPanelTitle = string.Empty;

    [ObservableProperty]
    private string posterScrapeQuery = string.Empty;

    [ObservableProperty]
    private string posterScrapeYear = string.Empty;

    [ObservableProperty]
    private string posterScrapeStatus = string.Empty;

    [ObservableProperty]
    private string posterSourceFileText = string.Empty;

    [ObservableProperty]
    private string posterEditTitle = string.Empty;

    [ObservableProperty]
    private string posterEditDate = string.Empty;

    [ObservableProperty]
    private string posterEditVoteAverage = string.Empty;

    [ObservableProperty]
    private string posterEditOverview = string.Empty;

    [ObservableProperty]
    private string posterEditPosterPath = string.Empty;

    private LibraryPosterItem? posterMetadataTarget;

    private LibraryPosterItem? posterWatchStateTarget;

    private bool pendingPosterWatchStateMarkWatched;

    [ObservableProperty]
    private string episodeEditPanelTitle = string.Empty;

    [ObservableProperty]
    private string episodeEditSourceFileText = string.Empty;

    [ObservableProperty]
    private string episodeEditSeason = string.Empty;

    [ObservableProperty]
    private string episodeEditEpisode = string.Empty;

    [ObservableProperty]
    private string episodeEditYear = string.Empty;

    [ObservableProperty]
    private string episodeEditSubtitle = string.Empty;

    [ObservableProperty]
    private string episodeEditThumbnailPath = string.Empty;

    private LibraryVideoItem? episodeEditTarget;

    [ObservableProperty]
    private string detailFileSummary = "\u5C1A\u672A\u8F7D\u5165\u6587\u4EF6";

    [ObservableProperty]
    private string detailProgressSummary = "\u6682\u65E0\u64AD\u653E\u8FDB\u5EA6";

    [ObservableProperty]
    private string detailPrimaryFileProgressText = "\u672A\u770B";

    [ObservableProperty]
    private string detailPrimaryTimeText = "00:00 / \u672A\u77E5\u65F6\u957F";

    [ObservableProperty]
    private string detailPrimaryActionText = "\u5F00\u59CB\u64AD\u653E";

    [ObservableProperty]
    private string detailSelectionHint = "\u70B9\u51FB\u4E0B\u65B9\u6761\u76EE\u53EF\u5207\u6362\u4E3B\u6587\u4EF6\u3002";

    [ObservableProperty]
    private string detailWatchedActionText = "\u672A\u770B";

    [ObservableProperty]
    private string detailCollectionHeading = "\u6587\u4EF6\u5217\u8868";

    [ObservableProperty]
    private double detailPrimaryProgressRatio;

    [ObservableProperty]
    private double detailPlaybackProgressRatio;

    [ObservableProperty]
    private bool isDetailSeries;

    [ObservableProperty]
    private LibraryVideoItem? detailPrimaryFile;

    [ObservableProperty]
    private int selectedSeason = 1;

    public bool HasDetailFiles => DetailFiles.Count > 0;

    public bool HasContinueWatching => ContinueWatchingItems.Count > 0;

    public bool HasSeasonTabs => IsDetailSeries && AvailableSeasons.Count > 1;

    public bool HasDetailMetadataCandidates => DetailMetadataCandidates.Count > 0;

    public bool CanShowDetailMetadataCandidatesPanel => HasDetailMetadataCandidates && !IsDetailMetadataCandidatePanelOpen;

    public bool CanShowDetailPrimaryProgress =>
        DetailPrimaryFile is { Duration: > 0, PlayProgress: > 0 } file && !file.IsWatched;

    public bool HasStatusMessage => IsLibraryScanInProgress && ShouldShowHomeStatusMessage(HomeStatusMessage);

    public string HomeStatusMessage =>
        string.IsNullOrWhiteSpace(StatusMessage) && IsLibraryScanInProgress
            ? "正在扫描媒体源..."
            : StatusMessage;

    public bool HasLastScanSummary => LastScanSummary is not null;

    public bool HasLastScanDiagnostics => LastScanSummary?.HasDiagnostics ?? false;

    public IReadOnlyList<string> LastScanDiagnostics => LastScanSummary?.Diagnostics ?? [];

    public string LastScanOverviewText => LastScanSummary is null
        ? string.Empty
        : $"最近扫描了 {LastScanSummary.SourceCount} 个媒体源，新增 {LastScanSummary.NewMovieCount} 部电影、{LastScanSummary.NewTvShowCount} 部剧集、{LastScanSummary.NewVideoFileCount} 个视频文件，移除了 {LastScanSummary.RemovedVideoFileCount} 个失效文件。";

    public bool ShowMediaSourceRealPath => Settings.ShowMediaSourceRealPath;

    public bool HasMediaSources => MediaSources.Count > 0;

    public bool HasDiscoveredNetworkSources => DiscoveredNetworkSources.Count > 0;

    public bool HasNetworkShareFolders => NetworkShareFolders.Count > 0;

    public bool IsEditingWebDavSource => EditingWebDavSourceId.HasValue;

    public bool IsSortAscending => !IsSortDescending;

    public string SortDirectionLabel => IsSortDescending ? "\u964D\u5E8F" : "\u5347\u5E8F";

    public string SortDirectionToolTip => IsSortDescending
        ? "\u5F53\u524D\u4E3A\u964D\u5E8F\uFF0C\u70B9\u51FB\u5207\u6362\u4E3A\u5347\u5E8F"
        : "\u5F53\u524D\u4E3A\u5347\u5E8F\uFF0C\u70B9\u51FB\u5207\u6362\u4E3A\u964D\u5E8F";

    public int? SelectedSeasonOption
    {
        get => SelectedSeason;
        set
        {
            if (value.HasValue && AvailableSeasons.Contains(value.Value) && SelectedSeason != value.Value)
            {
                SelectedSeason = value.Value;
            }
            else if (!value.HasValue)
            {
                OnPropertyChanged();
            }
        }
    }

    public string WebDavFormTitle => IsEditingWebDavSource ? "编辑 WebDAV" : "添加 WebDAV";

    public string WebDavSubmitActionText => IsEditingWebDavSource ? "保存更改" : "添加 WebDAV";

    public async Task LoadAsync()
    {
        try
        {
            await Settings.LoadAsync();
            ApplyPlaybackPreferencesToPlayer();
            await mediaSourceRepository.PurgeExpiredInactiveAsync(DateTimeOffset.UtcNow);
            OnPropertyChanged(nameof(ShowMediaSourceRealPath));
            StatusMessage = "\u6B63\u5728\u52A0\u8F7D\u5A92\u4F53\u5E93...";
            await ReloadLibraryAsync();
            StatusMessage = string.Empty;
            _ = RefreshNetworkSourcesAsync(updateBusyState: false);
        }
        catch
        {
            StatusMessage = "加载媒体库时发生异常。";
            throw;
        }
    }

    public async Task ScanAsync()
    {
        var automationCancellationTokenSource = BeginLibraryAutomation();
        var cancellationToken = automationCancellationTokenSource.Token;
        startupScanInProgress = true;
        IsSearchPopupOpen = false;
        IsSortPopupOpen = false;
        IsSourcePopupOpen = false;
        IsSettingsPopupOpen = false;
        OnCommandStateChanged();

        try
        {
            StatusMessage = "\u6B63\u5728\u626B\u63CF\u5A92\u4F53\u6E90...";
            LastScanSummary = await libraryScanner.ScanAllAsync(cancellationToken);
            await ReloadLibraryAsync();
            var scanPrefix = $"\u626B\u63CF\u5B8C\u6210\uFF1A\u65B0\u589E {LastScanSummary.NewMovieCount} \u90E8\u7535\u5F71\u3001{LastScanSummary.NewTvShowCount} \u90E8\u5267\u96C6\uFF0C\u79FB\u9664 {LastScanSummary.RemovedVideoFileCount} \u4E2A\u89C6\u9891\u6587\u4EF6\u3002";
            if (LastScanSummary.HasDiagnostics)
            {
                scanPrefix = $"{scanPrefix} 另有 {LastScanSummary.Diagnostics.Count} 条扫描诊断。";
            }

            await RefreshLibraryArtworkAsync(isExplicitRequest: true, forceThumbnails: true, cancellationToken);
            StatusMessage = string.IsNullOrWhiteSpace(StatusMessage)
                ? scanPrefix
                : $"{scanPrefix} {StatusMessage}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "\u5DF2\u505C\u6B62\u626B\u63CF\u548C\u522E\u524A\u4EFB\u52A1\u3002";
        }
        finally
        {
            startupScanInProgress = false;
            CompleteLibraryAutomation(automationCancellationTokenSource);
            OnCommandStateChanged();
        }
    }

    public async Task AddFolderSourceAsync()
    {
        IsSourcePopupOpen = false;
        IsSettingsPopupOpen = false;
        ResetWebDavForm();
        var selectedPath = await folderPickerService.PickFolderAsync();

        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            StatusMessage = "\u5DF2\u53D6\u6D88\u9009\u62E9\u6587\u4EF6\u5939\u3002";
            return;
        }

        if (!Directory.Exists(selectedPath))
        {
            StatusMessage = $"\u6587\u4EF6\u5939\u4E0D\u5B58\u5728\uFF1A{selectedPath}";
            return;
        }

        IsBusy = true;
        OnCommandStateChanged();

        try
        {
            var source = CreateLocalSource(selectedPath);
            var sourceId = await mediaSourceRepository.AddAsync(source);
            await ReloadLibraryAsync();
            StatusMessage = sourceId > 0
                ? $"已添加本地媒体源：{source.Name}"
                : "已保存本地媒体源。";
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    public async Task AddWebDavSourceAsync()
    {
        IsSettingsPopupOpen = false;
        var source = BuildPendingWebDavSource();
        if (source is null)
        {
            return;
        }

        IsBusy = true;
        OnCommandStateChanged();

        try
        {
            if (EditingWebDavSourceId.HasValue)
            {
                source.Id = EditingWebDavSourceId.Value;
                var updated = await mediaSourceRepository.UpdateAsync(source);
                if (!updated)
                {
                    StatusMessage = "保存 WebDAV 失败，可能是媒体源不存在，或与现有地址重复。";
                    return;
                }

                await ReloadLibraryAsync();
                ResetWebDavForm();
                IsSourcePopupOpen = false;
                StatusMessage = $"已保存 WebDAV 媒体源：{source.Name}";
                return;
            }

            var sourceId = await mediaSourceRepository.AddAsync(source);
            await ReloadLibraryAsync();
            ResetWebDavForm();
            IsSourcePopupOpen = false;
            StatusMessage = sourceId > 0
                ? $"已添加 WebDAV 媒体源：{source.Name}"
                : "已保存 WebDAV 媒体源。";
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    public async Task TestWebDavSourceAsync()
    {
        var source = BuildPendingWebDavSource();
        if (source is null)
        {
            return;
        }

        OnCommandStateChanged();

        try
        {
            var result = await webDavConnectionTester.TestConnectionAsync(source);
            StatusMessage = result.Message;
        }
        finally
        {
            OnCommandStateChanged();
        }
    }

    public void StartOverlayPlaybackDiagnostic(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            StatusMessage = string.IsNullOrWhiteSpace(filePath)
                ? "未提供用于覆盖层诊断的媒体文件。"
                : $"覆盖层诊断文件不存在：{filePath}";
            return;
        }

        IsSearchPopupOpen = false;
        IsSortPopupOpen = false;
        IsSourcePopupOpen = false;
        IsSettingsPopupOpen = false;
        IsPlayerOverlayOpen = true;
        PendingPlaybackFilePath = string.Empty;
        PendingPlaybackDisplayPath = string.Empty;
        PendingPlaybackFilePath = filePath;
        PendingPlaybackDisplayPath = filePath;
        PendingPlaybackStartPositionSeconds = 0;
        currentPlaybackMode = PlaybackMode.Overlay;
        currentPlayingVideoId = string.Empty;
        selectedPlaybackNavigationSeason = null;
        suppressedPlaybackPersistenceVideoId = null;
        ResetAutoAdvanceState();
        UpdatePlayerNextEpisodeAction();
        StatusMessage = $"正在准备覆盖层诊断播放：{Path.GetFileName(filePath)}";
        OnCommandStateChanged();
    }

    public Task CloseOverlayPlaybackDiagnosticAsync()
    {
        return ClosePlayerOverlayAsync();
    }

    private async Task ReloadLibraryAsync()
    {
        var movies = await movieRepository.GetAllAsync();
        var tvShows = await tvShowRepository.GetAllAsync();
        var sources = await mediaSourceRepository.GetAllAsync();
        var continueWatching = await videoFileRepository.GetContinueWatchingAsync();
        var playbackStates = await videoFileRepository.GetLibraryPlaybackStatesAsync();

        allMovies.Clear();
        allMovies.AddRange(movies);
        allTvShows.Clear();
        allTvShows.AddRange(tvShows);
        ReplaceItems(MediaSources, sources);
        OnPropertyChanged(nameof(HasMediaSources));
        ReplaceItems(ContinueWatchingItems, continueWatching);
        OnPropertyChanged(nameof(HasContinueWatching));

        var continuingIds = continueWatching
            .Select(static item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        allLibraryItems.Clear();
        allLibraryItems.AddRange(movies.Select(movie =>
        {
            var itemId = $"movie-{movie.Id}";
            var watchState = ResolvePosterWatchState(playbackStates, itemId);
            return new LibraryPosterItem
            {
                Id = itemId,
                Title = movie.Title,
                Subtitle = FormatMovieSubtitle(movie),
                PosterPath = movie.PosterPath,
                VoteAverage = movie.VoteAverage,
                MediaKind = "\u7535\u5F71",
                IsContinuing = continuingIds.Contains(itemId),
                WatchState = watchState,
                MovieId = movie.Id
            };
        }));
        allLibraryItems.AddRange(tvShows.Select(show =>
        {
            var itemId = $"tv-{show.Id}";
            var watchState = ResolvePosterWatchState(playbackStates, itemId);
            return new LibraryPosterItem
            {
                Id = itemId,
                Title = show.Title,
                Subtitle = FormatTvShowSubtitle(show),
                PosterPath = show.PosterPath,
                VoteAverage = show.VoteAverage,
                MediaKind = "\u5267\u96C6",
                IsContinuing = continuingIds.Contains(itemId),
                WatchState = watchState,
                TvShowId = show.Id
            };
        }));

        ApplyFilters();
    }

    private async Task OpenDetailAsync(LibraryPosterItem? item)
    {
        if (item is null)
        {
            return;
        }

        preferredDetailVideoId = null;

        if (item.MovieId.HasValue)
        {
            var movie = allMovies.FirstOrDefault(x => x.Id == item.MovieId);
            if (movie is not null)
            {
                currentDetailMovieId = movie.Id;
                currentDetailTvShowId = null;

                await OpenDetailInternalAsync(
                    movie.Title,
                    movie.ReleaseDate ?? "\u7535\u5F71",
                    movie.Overview ?? "\u6682\u65E0\u7B80\u4ECB\u3002",
                    movie.PosterPath,
                    movie.VoteAverage,
                    "\u7535\u5F71",
                    movie.IsLocked,
                    false,
                    false,
                    () => videoFileRepository.GetByMovieAsync(movie.Id ?? 0));
            }

            return;
        }

        if (item.TvShowId.HasValue)
        {
            var show = allTvShows.FirstOrDefault(x => x.Id == item.TvShowId.Value);
            if (show is not null)
            {
                currentDetailMovieId = null;
                currentDetailTvShowId = show.Id;

                await OpenDetailInternalAsync(
                    show.Title,
                    FormatTvShowSubtitle(show.FirstAirDate),
                    show.Overview ?? "\u8FD9\u91CC\u4F1A\u6309\u5B63\u6574\u7406\u6240\u6709\u5206\u96C6\uFF0C\u4FBF\u4E8E\u8FD8\u539F mac \u7248\u7684\u4E3B\u96C6\u9009\u62E9\u548C\u7EE7\u7EED\u89C2\u770B\u903B\u8F91\u3002",
                    show.PosterPath,
                    show.VoteAverage,
                    "\u5267\u96C6",
                    show.IsLocked,
                    true,
                    false,
                    () => videoFileRepository.GetByTvShowAsync(show.Id));
            }
        }
    }

    private async Task OpenDetailInternalAsync(
        string title,
        string meta,
        string overview,
        string? posterPath,
        double? voteAverage,
        string kind,
        bool isLocked,
        bool isSeries,
        bool preserveMetadataCandidates,
        Func<Task<IReadOnlyList<LibraryVideoItem>>> fileLoader)
    {
        IsSearchPopupOpen = false;
        IsSortPopupOpen = false;
        IsSourcePopupOpen = false;
        IsSettingsPopupOpen = false;
        IsDetailSeries = isSeries;
        IsDetailOpen = true;

        DetailTitle = title;
        DetailMeta = meta;
        DetailOverview = overview;
        DetailPosterPath = string.IsNullOrWhiteSpace(posterPath) ? null : posterPath;
        DetailKindText = kind;
        IsDetailLocked = isLocked;
        DetailLockStateText = isLocked ? "\u5DF2\u9501\u5B9A\u5143\u6570\u636E" : "\u672A\u9501\u5B9A\uFF0C\u540E\u7EED\u626B\u63CF\u53EF\u81EA\u52A8\u66F4\u65B0";
        DetailLockActionText = isLocked ? "\u89E3\u9664\u9501\u5B9A" : "\u9501\u5B9A\u5143\u6570\u636E";
        DetailScrapeQuery = title;
        if (!preserveMetadataCandidates)
        {
            ClearDetailMetadataCandidates();
        }
        DetailRatingText = voteAverage.HasValue ? $"\u8BC4\u5206 {voteAverage.Value:F1}" : "\u8BC4\u5206\u5F85\u8865\u5145";
        DetailCollectionHeading = isSeries ? "\u5206\u96C6\u5217\u8868" : "\u6587\u4EF6\u5217\u8868";
        DetailFileSummary = "\u6B63\u5728\u8BFB\u53D6\u6587\u4EF6...";

        allDetailFiles.Clear();
        ReplaceItems(DetailFiles, []);
        ReplaceItems(AvailableSeasons, []);
        selectedPlaybackNavigationSeason = null;
        DetailPrimaryFile = null;
        RefreshDetailHeaderState();
        UpdatePlayerNextEpisodeAction();

        var files = await fileLoader();
        allDetailFiles.AddRange(files);
        BuildSeasonState(files, isSeries);
        ApplyDetailFilter();

        StatusMessage = $"\u5DF2\u6253\u5F00\u300A{title}\u300B\u8BE6\u60C5\u3002";
        OnPropertyChanged(nameof(HasDetailFiles));
        OnCommandStateChanged();
    }

    private async Task PlayPrimaryAsync()
    {
        await PlayVideoAsync(DetailPrimaryFile);
    }

    private async Task OpenStandalonePrimaryAsync()
    {
        await OpenStandalonePlayerAsync(DetailPrimaryFile);
    }

    private async Task RefreshDetailMetadataAsync()
    {
        if (!currentDetailMovieId.HasValue && !currentDetailTvShowId.HasValue)
        {
            return;
        }

        IsBusy = true;
        OnCommandStateChanged();

        try
        {
            var query = string.IsNullOrWhiteSpace(DetailScrapeQuery)
                ? DetailTitle
                : DetailScrapeQuery.Trim();
            var tvShowIdToRefresh = currentDetailTvShowId;

            StatusMessage = "\u6B63\u5728\u91CD\u65B0\u522E\u524A\u5F53\u524D\u6761\u76EE...";
            var result = currentDetailMovieId.HasValue
                ? await libraryMetadataEditor.RefreshMovieAsync(currentDetailMovieId.Value, query)
                : await libraryMetadataEditor.RefreshTvShowAsync(currentDetailTvShowId!.Value, query);

            if (result.Updated)
            {
                await ReloadLibraryAsync();
                await RefreshOpenDetailIfNeededAsync();
            }

            var thumbnailSummary = tvShowIdToRefresh.HasValue
                ? await RefreshTvShowThumbnailsAndDetailFilesAsync(tvShowIdToRefresh.Value)
                : new LibraryThumbnailEnrichmentSummary();

            HideDetailMetadataCandidatesPanel();
            StatusMessage = AppendThumbnailRefreshStatusMessage(result.Message, thumbnailSummary);
        }
        catch
        {
            StatusMessage = "当前条目刮削时发生异常。";
        }
        finally
        {
            OnCommandStateChanged();
        }
    }

    public async Task RunStartupScanIfEnabledAsync()
    {
        if (startupScanAttempted)
        {
            return;
        }

        startupScanAttempted = true;
        if (!Settings.AutoScanOnStartup || MediaSources.Count == 0)
        {
            return;
        }

        var automationCancellationTokenSource = BeginLibraryAutomation();
        var cancellationToken = automationCancellationTokenSource.Token;
        startupScanInProgress = true;
        OnCommandStateChanged();

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
            StatusMessage = "正在后台扫描媒体源...";
            LastScanSummary = await libraryScanner.ScanAllAsync(cancellationToken);
            await ReloadLibraryAsync();

            var scanPrefix = $"后台扫描完成：新增 {LastScanSummary.NewMovieCount} 部电影、{LastScanSummary.NewTvShowCount} 部剧集，移除 {LastScanSummary.RemovedVideoFileCount} 个视频文件。";
            if (LastScanSummary.HasDiagnostics)
            {
                scanPrefix = $"{scanPrefix} 另有 {LastScanSummary.Diagnostics.Count} 条扫描诊断。";
            }

            await RefreshLibraryArtworkAsync(isExplicitRequest: false, forceThumbnails: true, cancellationToken);
            StatusMessage = string.IsNullOrWhiteSpace(StatusMessage)
                ? scanPrefix
                : $"{scanPrefix} {StatusMessage}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "\u5DF2\u505C\u6B62\u540E\u53F0\u626B\u63CF\u548C\u522E\u524A\u4EFB\u52A1\u3002";
        }
        catch
        {
            StatusMessage = "后台扫描媒体源时发生异常。";
        }
        finally
        {
            startupScanInProgress = false;
            CompleteLibraryAutomation(automationCancellationTokenSource);
            OnCommandStateChanged();
        }
    }

    private async Task SearchDetailMetadataCandidatesAsync()
    {
        if (!currentDetailMovieId.HasValue && !currentDetailTvShowId.HasValue)
        {
            return;
        }

        IsBusy = true;
        OnCommandStateChanged();

        try
        {
            var query = string.IsNullOrWhiteSpace(DetailScrapeQuery)
                ? DetailTitle
                : DetailScrapeQuery.Trim();

            StatusMessage = "\u6B63\u5728\u641C\u7D22 TMDB \u5019\u9009\u7ED3\u679C...";
            var candidates = currentDetailMovieId.HasValue
                ? await libraryMetadataEditor.SearchMovieMatchesAsync(currentDetailMovieId.Value, query)
                : await libraryMetadataEditor.SearchTvShowMatchesAsync(currentDetailTvShowId!.Value, query);

            ReplaceItems(DetailMetadataCandidates, candidates);
            IsDetailMetadataCandidatePanelOpen = candidates.Count > 0;
            OnDetailMetadataCandidatesStateChanged();

            StatusMessage = candidates.Count > 0
                ? BuildDetailMetadataCandidatesMessage(candidates)
                : $"\u672A\u627E\u5230\u4E0E\u300C{query}\u300D\u5339\u914D\u7684 TMDB \u5019\u9009\u7ED3\u679C，\u5DF2\u81EA\u52A8\u5C1D\u8BD5\u56DE\u9000\u67E5\u8BE2\u3002";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            HideDetailMetadataCandidatesPanel();
            StatusMessage = $"\u641C\u7D22 TMDB \u5019\u9009\u7ED3\u679C\u5931\u8D25\uFF1A{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private async Task ApplyDetailMetadataCandidateAsync(LibraryMetadataSearchCandidate? candidate)
    {
        if (candidate is null || (!currentDetailMovieId.HasValue && !currentDetailTvShowId.HasValue))
        {
            return;
        }

        IsBusy = true;
        OnCommandStateChanged();

        try
        {
            var tvShowIdToRefresh = currentDetailTvShowId;
            StatusMessage = "\u6B63\u5728\u5E94\u7528\u6240\u9009 TMDB \u5339\u914D...";
            var result = currentDetailMovieId.HasValue
                ? await libraryMetadataEditor.ApplyMovieMatchAsync(currentDetailMovieId.Value, candidate)
                : await libraryMetadataEditor.ApplyTvShowMatchAsync(currentDetailTvShowId!.Value, candidate);

            if (result.Updated)
            {
                await ReloadLibraryAsync();
                await RefreshOpenDetailIfNeededAsync();
            }

            var thumbnailSummary = tvShowIdToRefresh.HasValue
                ? await RefreshTvShowThumbnailsAndDetailFilesAsync(tvShowIdToRefresh.Value)
                : new LibraryThumbnailEnrichmentSummary();

            HideDetailMetadataCandidatesPanel();
            StatusMessage = AppendThumbnailRefreshStatusMessage(result.Message, thumbnailSummary);
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private static string BuildDetailMetadataCandidatesMessage(IReadOnlyList<LibraryMetadataSearchCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return string.Empty;
        }

        var first = candidates[0];
        return first.HasMatchedQuery
            ? $"\u5DF2\u901A\u8FC7{first.MatchedQueryText}\u627E\u5230 {candidates.Count} \u4E2A TMDB \u5019\u9009\u7ED3\u679C\u3002"
            : $"\u5DF2\u627E\u5230 {candidates.Count} \u4E2A TMDB \u5019\u9009\u7ED3\u679C\u3002";
    }

    private async Task<LibraryThumbnailEnrichmentSummary> RefreshTvShowThumbnailsAndDetailFilesAsync(long tvShowId)
    {
        var tmdbSettings = Settings.BuildTmdbSettings();
        if (!tmdbSettings.EnableEpisodeThumbnailDownloads)
        {
            return new LibraryThumbnailEnrichmentSummary();
        }

        LibraryThumbnailEnrichmentSummary summary;
        await libraryRefreshGate.WaitAsync();
        try
        {
            StatusMessage = "\u6B63\u5728\u522E\u524A\u5F53\u524D\u5267\u96C6\u7684\u5206\u96C6\u5267\u7167...";
            summary = await libraryThumbnailEnricher.EnrichMissingThumbnailsForTvShowAsync(
                tvShowId,
                tmdbSettings);
        }
        finally
        {
            libraryRefreshGate.Release();
        }

        if (summary.HasChanges &&
            IsDetailOpen &&
            currentDetailTvShowId.HasValue &&
            currentDetailTvShowId.Value == tvShowId)
        {
            await RefreshDetailFilesAsync();
        }

        return summary;
    }

    private static string AppendThumbnailRefreshStatusMessage(
        string? message,
        LibraryThumbnailEnrichmentSummary summary)
    {
        var thumbnailMessage = ComposeAutomaticThumbnailRefreshStatusMessage(summary);
        if (string.IsNullOrWhiteSpace(thumbnailMessage))
        {
            return message ?? string.Empty;
        }

        return string.IsNullOrWhiteSpace(message)
            ? thumbnailMessage
            : $"{message} {thumbnailMessage}";
    }

    private static string ComposeAutomaticThumbnailRefreshStatusMessage(LibraryThumbnailEnrichmentSummary summary)
    {
        if (summary.EncounteredNetworkError)
        {
            return string.IsNullOrWhiteSpace(summary.ErrorMessage)
                ? "分集剧照下载失败：无法连接网络或 TMDB。"
                : $"分集剧照下载失败：{summary.ErrorMessage}";
        }

        if (summary.DownloadedThumbnailCount > 0)
        {
            return $"已刮削 {summary.DownloadedThumbnailCount} 张分集剧照。";
        }

        return string.Empty;
    }

    private async Task ToggleDetailLockAsync()
    {
        if (!currentDetailMovieId.HasValue && !currentDetailTvShowId.HasValue)
        {
            return;
        }

        IsBusy = true;
        OnCommandStateChanged();

        try
        {
            var nextLocked = !IsDetailLocked;

            if (currentDetailMovieId.HasValue)
            {
                await libraryMetadataEditor.SetMovieLockedAsync(currentDetailMovieId.Value, nextLocked);
            }
            else
            {
                await libraryMetadataEditor.SetTvShowLockedAsync(currentDetailTvShowId!.Value, nextLocked);
            }

            HideDetailMetadataCandidatesPanel();
            await ReloadLibraryAsync();
            await RefreshOpenDetailIfNeededAsync();
            StatusMessage = nextLocked
                ? "\u5F53\u524D\u6761\u76EE\u5DF2\u9501\u5B9A\uFF0C\u540E\u7EED\u81EA\u52A8\u522E\u524A\u4E0D\u4F1A\u8986\u76D6\u5B83\u3002"
                : "\u5F53\u524D\u6761\u76EE\u5DF2\u89E3\u9664\u9501\u5B9A\uFF0C\u540E\u7EED\u53EF\u7EE7\u7EED\u81EA\u52A8\u8865\u5168\u3002";
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private void ShowDetailMetadataCandidatesPanel()
    {
        if (!HasDetailMetadataCandidates)
        {
            return;
        }

        IsDetailMetadataCandidatePanelOpen = true;
        OnDetailMetadataCandidatesStateChanged();
    }

    private void HideDetailMetadataCandidatesPanel()
    {
        if (!IsDetailMetadataCandidatePanelOpen)
        {
            return;
        }

        IsDetailMetadataCandidatePanelOpen = false;
        OnDetailMetadataCandidatesStateChanged();
    }

    private void ClearDetailMetadataCandidates()
    {
        IsDetailMetadataCandidatePanelOpen = false;
        ReplaceItems(DetailMetadataCandidates, []);
        OnDetailMetadataCandidatesStateChanged();
    }

    private void OnDetailMetadataCandidatesStateChanged()
    {
        OnPropertyChanged(nameof(HasDetailMetadataCandidates));
        OnPropertyChanged(nameof(CanShowDetailMetadataCandidatesPanel));
        ShowDetailMetadataCandidatesPanelCommand.NotifyCanExecuteChanged();
        HideDetailMetadataCandidatesPanelCommand.NotifyCanExecuteChanged();
    }

    private async Task PlayVideoAsync(LibraryVideoItem? video)
    {
        if (video is null || !MediaSourcePathResolver.IsPlayableLocation(video.EffectivePlaybackPath))
        {
            StatusMessage = video is null ? "\u6CA1\u6709\u53EF\u64AD\u653E\u7684\u6587\u4EF6\u3002" : $"\u6587\u4EF6\u4E0D\u5B58\u5728\uFF1A{video.FileName}";
            return;
        }

        if (currentPlaybackMode == PlaybackMode.Standalone)
        {
            await playbackLauncher.CloseAsync();
        }
        else if (currentPlaybackMode == PlaybackMode.Overlay && IsPlayerOverlayOpen)
        {
            await ClosePlayerOverlayAsync();
        }

        ApplyPreferredDetailVideoSelection(video);
        var playbackStartPosition = GetPlaybackStartPosition(video);
        ApplyPlaybackPreferencesToPlayer();

        IsPlayerOverlayOpen = true;
        PendingPlaybackFilePath = string.Empty;
        PendingPlaybackDisplayPath = string.Empty;
        PendingPlaybackStartPositionSeconds = playbackStartPosition ?? 0;
        PendingPlaybackFilePath = video.EffectivePlaybackPath;
        PendingPlaybackDisplayPath = video.AbsolutePath;
        currentPlaybackMode = PlaybackMode.Overlay;
        currentPlayingVideoId = video.Id;
        suppressedPlaybackPersistenceVideoId = null;
        ResetAutoAdvanceState();
        ResetPlaybackProgressSyncState();
        UpdatePlayerNextEpisodeAction();
        StatusMessage = playbackStartPosition.HasValue
            ? $"\u6B63\u5728\u7EE7\u7EED\u64AD\u653E\uFF1A{video.FileName}"
            : $"\u6B63\u5728\u51C6\u5907\u64AD\u653E\uFF1A{video.FileName}";
        OnCommandStateChanged();
    }

    private Task OpenStandalonePlayerAsync(LibraryVideoItem? video)
    {
        return OpenStandalonePlayerInternalAsync(
            video,
            replaceCurrentSession: false,
            persistCurrentPlaybackState: true);
    }

    private async Task OpenStandalonePlayerInternalAsync(
        LibraryVideoItem? video,
        bool replaceCurrentSession,
        bool persistCurrentPlaybackState)
    {
        if (video is null || !MediaSourcePathResolver.IsPlayableLocation(video.EffectivePlaybackPath))
        {
            StatusMessage = video is null ? "没有可独立播放的文件。" : $"文件不存在：{video.FileName}";
            return;
        }

        if (currentPlaybackMode == PlaybackMode.Overlay && IsPlayerOverlayOpen)
        {
            await ClosePlayerOverlayAsync();
        }

        var isReplacingStandaloneSession =
            currentPlaybackMode == PlaybackMode.Standalone &&
            !string.IsNullOrWhiteSpace(currentPlayingVideoId) &&
            !string.Equals(currentPlayingVideoId, video.Id, StringComparison.Ordinal);

        if (persistCurrentPlaybackState && isReplacingStandaloneSession)
        {
            var currentVideo = ResolveCurrentPlaybackVideo();
            if (currentVideo is not null)
            {
                StopPlaybackProgressSync();
                await PersistTransitionPlaybackStateAsync(currentVideo);
                preferredDetailVideoId = video.Id;
                if (video.IsTvEpisode)
                {
                    selectedPlaybackNavigationSeason = video.SeasonNumber;
                }

                await RefreshDetailFilesAsync();
                await RefreshContinueWatchingAsync();
                video = ResolveDetailVideo(video.Id) ?? video;
            }
        }

        ApplyPreferredDetailVideoSelection(video);
        var playbackStartPosition = GetPlaybackStartPosition(video);
        ApplyPlaybackPreferencesToPlayer();

        var opened = await playbackLauncher.OpenAsync(
            new PlaybackOpenRequest(video.EffectivePlaybackPath, video.AbsolutePath),
            result => HandleStandalonePlaybackClosedAsync(video.Id, video.FileName, result),
            playbackStartPosition,
            replaceCurrentSession || isReplacingStandaloneSession);

        if (!opened)
        {
            StatusMessage = $"无法在独立窗口打开：{video.FileName}";
            return;
        }

        currentPlaybackMode = PlaybackMode.Standalone;
        currentPlayingVideoId = video.Id;
        suppressedPlaybackPersistenceVideoId = null;
        ResetAutoAdvanceState();
        ResetPlaybackProgressSyncState();
        UpdatePlayerNextEpisodeAction();
        if (Player.IsPlaying)
        {
            StartPlaybackProgressSync();
        }
        StatusMessage = playbackStartPosition.HasValue
            ? $"已在独立窗口继续播放：{video.FileName}"
            : $"已在独立窗口打开：{video.FileName}";
        OnCommandStateChanged();
    }

    private void SelectDetailFile(LibraryVideoItem? video)
    {
        if (video is null)
        {
            return;
        }

        ApplyPreferredDetailVideoSelection(video);
    }

    private async Task ToggleDetailWatchedAsync(LibraryVideoItem? video)
    {
        if (video is null)
        {
            return;
        }

        var nextDuration = video.Duration > 0 ? video.Duration : 100;
        var nextProgress = video.IsWatched ? 0 : nextDuration;

        await videoFileRepository.UpdatePlaybackStateAsync(
            video.Id,
            nextProgress,
            video.IsWatched ? null : nextDuration);
        preferredDetailVideoId = video.Id;
        await RefreshDetailFilesAsync();
        await RefreshContinueWatchingAsync();
        StatusMessage = video.IsWatched
            ? $"\u5DF2\u5C06 {video.FileName} \u6807\u8BB0\u4E3A\u672A\u770B\u3002"
            : $"\u5DF2\u5C06 {video.FileName} \u6807\u8BB0\u4E3A\u5DF2\u770B\u3002";
    }

    private void OpenPosterWatchStateConfirmation(LibraryPosterItem? item)
    {
        if (item is null)
        {
            return;
        }

        pendingPosterWatchStateMarkWatched = item.WatchState != PlaybackWatchState.Watched;
        posterWatchStateTarget = item;

        IsSearchPopupOpen = false;
        IsSortPopupOpen = false;
        IsSourcePopupOpen = false;
        IsSettingsPopupOpen = false;
        IsPosterScrapePanelOpen = false;
        IsPosterEditPanelOpen = false;
        IsEpisodeEditPanelOpen = false;

        var nextStateText = pendingPosterWatchStateMarkWatched ? "\u5DF2\u770B" : "\u672A\u770B";
        var mediaKind = string.IsNullOrWhiteSpace(item.MediaKind) ? "\u6761\u76EE" : item.MediaKind;
        PosterWatchStateConfirmationTitle = pendingPosterWatchStateMarkWatched
            ? "\u6807\u8BB0\u4E3A\u5DF2\u770B"
            : "\u6807\u8BB0\u4E3A\u672A\u770B";
        PosterWatchStateConfirmationMessage =
            $"\u786E\u8BA4\u5C06\u300A{item.Title}\u300B\u6574\u90E8{mediaKind}\u6807\u8BB0\u4E3A{nextStateText}\uFF1F\u8FD9\u4F1A\u66F4\u65B0\u5176\u6240\u6709\u89C6\u9891\u6587\u4EF6\u7684\u64AD\u653E\u8FDB\u5EA6\u3002";
        PosterWatchStateConfirmationActionText = pendingPosterWatchStateMarkWatched
            ? "\u6807\u8BB0\u4E3A\u5DF2\u770B"
            : "\u6807\u8BB0\u4E3A\u672A\u770B";
        IsPosterWatchStateConfirmationOpen = true;
        OnCommandStateChanged();
    }

    private void ClosePosterWatchStateConfirmation()
    {
        ResetPosterWatchStateConfirmation();
        OnCommandStateChanged();
    }

    private async Task ConfirmPosterWatchStateAsync()
    {
        var target = posterWatchStateTarget;
        if (target is null)
        {
            return;
        }

        IsBusy = true;
        OnCommandStateChanged();

        try
        {
            var files = await LoadPosterTargetFilesAsync(target);
            if (files.Count == 0)
            {
                StatusMessage = $"\u300A{target.Title}\u300B\u6CA1\u6709\u53EF\u66F4\u65B0\u7684\u89C6\u9891\u6587\u4EF6\u3002";
                return;
            }

            foreach (var file in files)
            {
                var duration = file.Duration > 0 ? file.Duration : 100;
                await videoFileRepository.UpdatePlaybackStateAsync(
                    file.Id,
                    pendingPosterWatchStateMarkWatched ? duration : 0,
                    pendingPosterWatchStateMarkWatched ? duration : null);
            }

            await RefreshContinueWatchingAsync();
            if (IsPosterTargetCurrentDetail(target))
            {
                await RefreshDetailFilesAsync();
            }

            var actionText = pendingPosterWatchStateMarkWatched ? "\u5DF2\u770B" : "\u672A\u770B";
            StatusMessage = $"\u5DF2\u5C06\u300A{target.Title}\u300B\u6574\u90E8\u6807\u8BB0\u4E3A{actionText}\u3002";
        }
        finally
        {
            ResetPosterWatchStateConfirmation();
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private async Task<IReadOnlyList<LibraryVideoItem>> LoadPosterTargetFilesAsync(LibraryPosterItem item)
    {
        if (item.MovieId.HasValue)
        {
            return await videoFileRepository.GetByMovieAsync(item.MovieId.Value);
        }

        if (item.TvShowId.HasValue)
        {
            return await videoFileRepository.GetByTvShowAsync(item.TvShowId.Value);
        }

        return [];
    }

    private bool IsPosterTargetCurrentDetail(LibraryPosterItem item)
    {
        return IsDetailOpen &&
               ((item.MovieId.HasValue && currentDetailMovieId == item.MovieId.Value) ||
                (item.TvShowId.HasValue && currentDetailTvShowId == item.TvShowId.Value));
    }

    private void ResetPosterWatchStateConfirmation()
    {
        IsPosterWatchStateConfirmationOpen = false;
        PosterWatchStateConfirmationTitle = string.Empty;
        PosterWatchStateConfirmationMessage = string.Empty;
        PosterWatchStateConfirmationActionText = string.Empty;
        posterWatchStateTarget = null;
        pendingPosterWatchStateMarkWatched = false;
    }

    private async Task OpenPosterScrapeAsync(LibraryPosterItem? item)
    {
        if (item is null)
        {
            return;
        }

        await PreparePosterMetadataTargetAsync(item);
        PosterPanelTitle = $"\u91CD\u65B0\u5339\u914D TMDB \u00B7 {item.Title}";
        PosterScrapeQuery = ResolveInitialPosterScrapeQuery(item, PosterSourceFileText);
        PosterScrapeYear = ExtractFourDigitYear(item.Subtitle) ?? ExtractFourDigitYear(PosterSourceFileText) ?? string.Empty;
        PosterScrapeStatus = "\u6B63\u5728\u6309\u5F53\u524D\u6761\u76EE\u81EA\u52A8\u641C\u7D22 TMDB \u5019\u9009\u7ED3\u679C...";
        StatusMessage = PosterScrapeStatus;
        ReplaceItems(PosterMetadataCandidates, []);
        IsPosterEditPanelOpen = false;
        IsPosterScrapePanelOpen = true;
        OnCommandStateChanged();
        await SearchPosterMetadataCandidatesAsync();
    }

    private void ClosePosterScrape()
    {
        IsPosterScrapePanelOpen = false;
        ReplaceItems(PosterMetadataCandidates, []);
        PosterScrapeStatus = string.Empty;
        PosterScrapeYear = string.Empty;
        PosterSourceFileText = string.Empty;
        posterMetadataTarget = null;
        OnCommandStateChanged();
    }

    private async Task SearchPosterMetadataCandidatesAsync()
    {
        var target = posterMetadataTarget;
        if (target is null)
        {
            return;
        }

        IsBusy = true;
        OnCommandStateChanged();

        try
        {
            var query = NormalizePosterQuery(target);
            var year = NormalizePosterYear();
            PosterScrapeStatus = "\u6B63\u5728\u641C\u7D22 TMDB \u5019\u9009\u7ED3\u679C...";
            StatusMessage = PosterScrapeStatus;
            var candidates = target.MovieId.HasValue
                ? await libraryMetadataEditor.SearchMovieMatchesAsync(target.MovieId.Value, query, year)
                : await libraryMetadataEditor.SearchTvShowMatchesAsync(target.TvShowId!.Value, query, year);

            ReplaceItems(PosterMetadataCandidates, candidates);
            PosterScrapeStatus = candidates.Count > 0
                ? BuildDetailMetadataCandidatesMessage(candidates)
                : $"\u672A\u627E\u5230\u4E0E\u300C{BuildPosterSearchLabel(query, year)}\u300D\u5339\u914D\u7684 TMDB \u5019\u9009\u7ED3\u679C\u3002";
            StatusMessage = PosterScrapeStatus;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            PosterScrapeStatus = $"\u641C\u7D22 TMDB \u5019\u9009\u7ED3\u679C\u5931\u8D25\uFF1A{ex.Message}";
            StatusMessage = PosterScrapeStatus;
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private async Task ApplyPosterMetadataCandidateAsync(LibraryMetadataSearchCandidate? candidate)
    {
        var target = posterMetadataTarget;
        if (target is null || candidate is null)
        {
            return;
        }

        IsBusy = true;
        OnCommandStateChanged();

        try
        {
            var tvShowIdToRefresh = target.TvShowId;
            PosterScrapeStatus = "\u6B63\u5728\u5E94\u7528\u6240\u9009 TMDB \u5339\u914D...";
            StatusMessage = PosterScrapeStatus;
            var result = target.MovieId.HasValue
                ? await libraryMetadataEditor.ApplyMovieMatchAsync(target.MovieId.Value, candidate)
                : await libraryMetadataEditor.ApplyTvShowMatchAsync(target.TvShowId!.Value, candidate);

            if (result.Updated)
            {
                await ReloadLibraryAsync();
                ClosePosterScrape();
            }

            var thumbnailSummary = tvShowIdToRefresh.HasValue
                ? await RefreshTvShowThumbnailsAndDetailFilesAsync(tvShowIdToRefresh.Value)
                : new LibraryThumbnailEnrichmentSummary();

            StatusMessage = AppendThumbnailRefreshStatusMessage(result.Message, thumbnailSummary);
            PosterScrapeStatus = StatusMessage;
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private async Task OpenPosterEditAsync(LibraryPosterItem? item)
    {
        if (item is null)
        {
            return;
        }

        await PreparePosterMetadataTargetAsync(item);
        PosterPanelTitle = $"\u624B\u52A8\u7F16\u8F91\u8D44\u6599 \u00B7 {item.Title}";
        PosterEditTitle = item.Title;
        PosterEditDate = item.MovieId.HasValue
            ? allMovies.FirstOrDefault(movie => movie.Id == item.MovieId.Value)?.ReleaseDate ?? string.Empty
            : allTvShows.FirstOrDefault(show => show.Id == item.TvShowId!.Value)?.FirstAirDate ?? string.Empty;
        PosterEditVoteAverage = item.VoteAverage.HasValue
            ? item.VoteAverage.Value.ToString("0.0", CultureInfo.InvariantCulture)
            : string.Empty;
        PosterEditOverview = item.MovieId.HasValue
            ? allMovies.FirstOrDefault(movie => movie.Id == item.MovieId.Value)?.Overview ?? string.Empty
            : allTvShows.FirstOrDefault(show => show.Id == item.TvShowId!.Value)?.Overview ?? string.Empty;
        PosterEditPosterPath = item.PosterPath ?? string.Empty;
        IsPosterScrapePanelOpen = false;
        ReplaceItems(PosterMetadataCandidates, []);
        IsPosterEditPanelOpen = true;
        OnCommandStateChanged();
    }

    private void ClosePosterEdit()
    {
        IsPosterEditPanelOpen = false;
        PosterSourceFileText = string.Empty;
        posterMetadataTarget = null;
        OnCommandStateChanged();
    }

    private void OpenEpisodeEdit(LibraryVideoItem? video)
    {
        if (video is null || !IsDetailSeries)
        {
            return;
        }

        episodeEditTarget = video;
        EpisodeEditPanelTitle = $"编辑分集 · {video.SeasonEpisodeText}";
        EpisodeEditSourceFileText = FormatSourceFileDisplayName(video);
        EpisodeEditSeason = video.SeasonNumber.ToString(CultureInfo.InvariantCulture);
        EpisodeEditEpisode = video.EpisodeNumber.ToString(CultureInfo.InvariantCulture);
        EpisodeEditYear = video.EpisodeYear ?? string.Empty;
        EpisodeEditSubtitle = video.CustomEpisodeSubtitle ?? string.Empty;
        EpisodeEditThumbnailPath = video.CustomThumbnailPath ?? video.ThumbnailPath ?? string.Empty;
        IsPosterScrapePanelOpen = false;
        IsPosterEditPanelOpen = false;
        IsEpisodeEditPanelOpen = true;
        OnCommandStateChanged();
    }

    private void CloseEpisodeEdit()
    {
        IsEpisodeEditPanelOpen = false;
        EpisodeEditPanelTitle = string.Empty;
        EpisodeEditSourceFileText = string.Empty;
        EpisodeEditSeason = string.Empty;
        EpisodeEditEpisode = string.Empty;
        EpisodeEditYear = string.Empty;
        EpisodeEditSubtitle = string.Empty;
        EpisodeEditThumbnailPath = string.Empty;
        episodeEditTarget = null;
        OnCommandStateChanged();
    }

    private async Task SaveEpisodeEditAsync()
    {
        var target = episodeEditTarget;
        if (target is null)
        {
            return;
        }

        if (!TryParseEpisodeNumber(EpisodeEditSeason, allowZero: true, out var seasonNumber))
        {
            StatusMessage = "季数需要是 0 或更大的整数。";
            return;
        }

        if (!TryParseEpisodeNumber(EpisodeEditEpisode, allowZero: false, out var episodeNumber))
        {
            StatusMessage = "集数需要是大于 0 的整数。";
            return;
        }

        var year = NormalizeEpisodeYear(EpisodeEditYear);
        if (year is null && !string.IsNullOrWhiteSpace(EpisodeEditYear))
        {
            StatusMessage = "年份需要是 4 位数字。";
            return;
        }

        IsBusy = true;
        OnCommandStateChanged();

        try
        {
            preferredDetailVideoId = target.Id;
            await videoFileRepository.UpdateEpisodeMetadataAsync(
                target.Id,
                new LibraryEpisodeEditRequest(
                    seasonNumber,
                    episodeNumber,
                    year,
                    EpisodeEditSubtitle,
                    EpisodeEditThumbnailPath));
            await RefreshDetailFilesAsync();
            CloseEpisodeEdit();
            StatusMessage = "已保存分集自定义信息。";
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private async Task ChooseEpisodeThumbnailImageAsync()
    {
        var imagePath = await posterImagePickerService.PickPosterImageAsync();
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            EpisodeEditThumbnailPath = imagePath;
        }
    }

    private async Task PreparePosterMetadataTargetAsync(LibraryPosterItem item)
    {
        posterMetadataTarget = item;
        PosterSourceFileText = await LoadPosterSourceFileTextAsync(item);
    }

    private async Task<string> LoadPosterSourceFileTextAsync(LibraryPosterItem item)
    {
        IReadOnlyList<LibraryVideoItem> files = item.MovieId.HasValue
            ? await videoFileRepository.GetByMovieAsync(item.MovieId.Value)
            : item.TvShowId.HasValue
                ? await videoFileRepository.GetByTvShowAsync(item.TvShowId.Value)
                : [];

        var file = files.FirstOrDefault();
        if (file is null)
        {
            return "\u672A\u627E\u5230\u5173\u8054\u7684\u89C6\u9891\u6587\u4EF6";
        }

        return FormatSourceFileDisplayName(file);
    }

    private static string FormatSourceFileDisplayName(LibraryVideoItem file)
    {
        return TryResolveDecodedDisplayPath(file.RelativePath)
               ?? TryResolveDecodedDisplayPath(file.AbsolutePath)
               ?? TryResolveDecodedDisplayPath(file.PlaybackPath)
               ?? TryResolveDecodedDisplayPath(file.FileName)
               ?? file.FileName;
    }

    private static string? TryResolveDecodedDisplayPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        string? displayPath = null;
        if (MediaSourcePathResolver.IsRemoteHttpUrl(trimmed) &&
            Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            displayPath = uri.AbsolutePath.Trim('/');
        }
        else if (IsUncPath(trimmed))
        {
            displayPath = RemoveUncServerAndShare(trimmed);
        }
        else
        {
            displayPath = trimmed;
        }

        displayPath = DecodePercentEncodedText(NormalizeDisplayPath(displayPath ?? trimmed)).Trim();
        return string.IsNullOrWhiteSpace(displayPath)
            ? null
            : displayPath;
    }

    private static bool IsUncPath(string value)
    {
        return value.StartsWith(@"\\", StringComparison.Ordinal) ||
               value.StartsWith("//", StringComparison.Ordinal);
    }

    private static string RemoveUncServerAndShare(string value)
    {
        var normalized = value.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length > 2
            ? string.Join('/', segments.Skip(2))
            : normalized;
    }

    private static string NormalizeDisplayPath(string value)
    {
        var normalized = value.Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized.Trim('/');
    }

    private static string DecodePercentEncodedText(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private async Task ChoosePosterImageAsync()
    {
        var posterPath = await posterImagePickerService.PickPosterImageAsync();
        if (!string.IsNullOrWhiteSpace(posterPath))
        {
            PosterEditPosterPath = posterPath;
        }
    }

    private async Task SavePosterMetadataEditAsync()
    {
        var target = posterMetadataTarget;
        if (target is null)
        {
            return;
        }

        var title = PosterEditTitle.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            StatusMessage = "\u540D\u79F0\u4E0D\u80FD\u4E3A\u7A7A\u3002";
            return;
        }

        if (!TryParsePosterVoteAverage(out var voteAverage))
        {
            StatusMessage = "\u8BC4\u5206\u9700\u8981\u662F 0 \u5230 10 \u4E4B\u95F4\u7684\u6570\u5B57\u3002";
            return;
        }

        IsBusy = true;
        OnCommandStateChanged();

        try
        {
            var request = new LibraryMetadataEditRequest(
                title,
                PosterEditDate,
                PosterEditOverview,
                PosterEditPosterPath,
                voteAverage);

            var result = target.MovieId.HasValue
                ? await libraryMetadataEditor.UpdateMovieMetadataAsync(target.MovieId.Value, request)
                : await libraryMetadataEditor.UpdateTvShowMetadataAsync(target.TvShowId!.Value, request);

            if (result.Updated)
            {
                await ReloadLibraryAsync();
                ClosePosterEdit();
            }

            StatusMessage = result.Message;
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private string NormalizePosterQuery(LibraryPosterItem target)
    {
        var query = string.IsNullOrWhiteSpace(PosterScrapeQuery)
            ? target.Title
            : PosterScrapeQuery.Trim();
        PosterScrapeQuery = query;
        return query;
    }

    private static string ResolveInitialPosterScrapeQuery(LibraryPosterItem item, string sourceFileText)
    {
        return ExtractTitleBeforeReleaseYear(sourceFileText) ?? item.Title;
    }

    private static string? ExtractTitleBeforeReleaseYear(string sourceFileText)
    {
        if (string.IsNullOrWhiteSpace(sourceFileText))
        {
            return null;
        }

        var normalizedPath = sourceFileText.Replace('\\', '/');
        var fileStem = ResolvePosterTitleSource(normalizedPath);
        var candidate = string.IsNullOrWhiteSpace(fileStem)
            ? normalizedPath
            : fileStem;
        candidate = Regex.Replace(candidate, @"[._]+", " ");
        candidate = Regex.Replace(candidate, @"[\[\]\(\)\{\}]+", " ");
        candidate = Regex.Replace(candidate, @"\s+", " ").Trim();
        candidate = NormalizePosterTitleCandidate(candidate);

        var year = ChoosePosterReleaseYear(candidate);
        if (string.IsNullOrWhiteSpace(year))
        {
            return HasPosterTitleText(candidate) ? candidate : null;
        }

        foreach (Match match in Regex.Matches(candidate, $@"(?<!\d){Regex.Escape(year)}(?!\d)"))
        {
            var prefix = NormalizePosterTitleCandidate(candidate[..match.Index]);
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                return prefix;
            }
        }

        return null;
    }

    private static bool HasPosterTitleText(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Any(static character => char.IsLetter(character) || IsCjkCharacter(character));
    }

    private static bool IsCjkCharacter(char character)
    {
        return character >= '\u4E00' && character <= '\u9FFF';
    }

    private static string ResolvePosterTitleSource(string normalizedPath)
    {
        var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bdmvIndex = Array.FindIndex(parts, static part => part.Equals("BDMV", StringComparison.OrdinalIgnoreCase));
        if (bdmvIndex > 0)
        {
            return parts[bdmvIndex - 1];
        }

        return Path.GetFileNameWithoutExtension(normalizedPath);
    }

    private static string? ChoosePosterReleaseYear(string text)
    {
        var candidates = Regex.Matches(text, @"(?<!\d)(19\d{2}|20\d{2})(?!\d)")
            .Select(static match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count >= 2)
        {
            var compactPrefix = Regex.Replace(text[..Math.Min(text.Length, 12)], @"[.\-_/\s]+", string.Empty);
            if (compactPrefix.StartsWith(candidates[0], StringComparison.Ordinal))
            {
                return candidates[1];
            }
        }

        return candidates[0];
    }

    private static string NormalizePosterTitleCandidate(string value)
    {
        var normalized = Regex.Replace(value, @"[._]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        normalized = TruncatePosterReleaseTail(normalized);
        return RemovePosterReleaseTitleNoise(normalized.Trim(' ', '.', '-', '_', '/', '\\', '(', ')', '[', ']', '{', '}'));
    }

    private static string TruncatePosterReleaseTail(string value)
    {
        var tokens = Regex.Split(value, @"\s+")
            .Where(static token => !string.IsNullOrWhiteSpace(token))
            .ToList();
        for (var index = 0; index < tokens.Count; index++)
        {
            if (!IsPosterReleaseToken(tokens[index]))
            {
                continue;
            }

            var prefix = string.Join(' ', tokens.Take(index)).Trim();
            if (HasPosterTitleText(prefix))
            {
                return prefix;
            }
        }

        return value;
    }

    private static bool IsPosterReleaseToken(string token)
    {
        var lower = token.Trim().ToLowerInvariant();
        return Regex.IsMatch(lower, @"^s\d{1,2}e[p]?\d{1,3}$") ||
               Regex.IsMatch(lower, @"^s\d{1,2}$") ||
               Regex.IsMatch(lower, @"^ep?\d{1,3}$") ||
               Regex.IsMatch(lower, @"^episode\d{1,3}$") ||
               Regex.IsMatch(lower, @"^\d{3,4}p$") ||
               Regex.IsMatch(lower, @"^(4k|uhd|hdtv|uhdtv|hdr|dv|atmos|ddp\d*(\.\d+)?|aac\d*(\.\d+)?|ac3|eac3|dts|truehd|flac|lpcm|avc)$") ||
               Regex.IsMatch(lower, @"^(x|h)?26[45]$") ||
               Regex.IsMatch(lower, @"^(web|webdl|webrip|bluray|bdrip|remux|blu-ray)$") ||
               Regex.IsMatch(lower, @"^(nf|amzn|netflix|dsnp|disney|hmax|max|atvp|hulu|cr)$") ||
               Regex.IsMatch(lower, @"^(bonus|extra|extras|featurette|trailer|sample|complete)$");
    }

    private static string RemovePosterReleaseTitleNoise(string value)
    {
        var normalized = Regex.Replace(value, @"[._]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        normalized = RemovePosterDecorativeBracketSegments(normalized);
        normalized = Regex.Replace(
            normalized,
            @"(?i)^(?:disc|disk|cd|dvd|vol|volume)\s*\d{0,2}\s*[-–—:]+\s*",
            string.Empty);
        normalized = Regex.Replace(normalized, @"^[\s._\-–—:]+", string.Empty);
        normalized = Regex.Replace(normalized, @"(?i)\b(?:cctv4k|cctv)\b", " ");
        normalized = Regex.Replace(normalized, @"(?i)\b[s]\d{1,2}[e][p]?\d{1,3}\b", " ");
        normalized = Regex.Replace(normalized, @"(?i)\b[s]\d{1,2}\b", " ");
        normalized = Regex.Replace(normalized, @"(?i)\bepisode\s*\d{1,3}\b", " ");
        normalized = Regex.Replace(normalized, @"(?i)\b(?:1080p|2160p|720p|480p|4k|uhd|hdtv|uhdtv|blu[- ]?ray|bluray|web[- ]?dl|webrip|remux|x264|x265|h\.?264|h\.?265|hevc|hdr|dv|ddp\d*(?:\.\d+)?|dts(?:-hd)?|aac|atmos|avc|flac|lpcm|truehd|complete|nf)\b", " ");
        normalized = Regex.Replace(normalized, @"(?:剧场版|纪念版|花絮|特典|番外|幕后花絮)", " ");

        var withoutMovieSuffix = Regex.Replace(
            normalized,
            @"(?i)(?:\s*[-–—:]\s*|\s+)the\s+movie\s*$",
            string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(withoutMovieSuffix))
        {
            normalized = withoutMovieSuffix;
        }

        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static string RemovePosterDecorativeBracketSegments(string value)
    {
        return Regex.Replace(
            value,
            @"[\[\(\{（【]\s*[^]\)\}）】]*(?:剧场版|纪念版|花絮|特典|番外|幕后花絮|anniversary|edition|featurette|bonus|extra|extras|trailer)[^]\)\}）】]*[\]\)\}）】]",
            " ",
            RegexOptions.IgnoreCase);
    }

    private string NormalizePosterYear()
    {
        var year = ExtractFourDigitYear(PosterScrapeYear);
        if (year is not null)
        {
            PosterScrapeYear = year;
            return year;
        }

        var trimmed = PosterScrapeYear.Trim();
        PosterScrapeYear = trimmed;
        return trimmed;
    }

    private static string BuildPosterSearchLabel(string query, string year)
    {
        return string.IsNullOrWhiteSpace(year)
            ? query
            : $"{query} / {year}";
    }

    private static string? ExtractFourDigitYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(value, @"(?<!\d)(19\d{2}|20\d{2})(?!\d)");
        return match.Success ? match.Value : null;
    }

    private bool TryParsePosterVoteAverage(out double? value)
    {
        var text = PosterEditVoteAverage.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            value = null;
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariantValue) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out invariantValue))
        {
            value = Math.Clamp(invariantValue, 0, 10);
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryParseEpisodeNumber(string value, bool allowZero, out int number)
    {
        number = 0;
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        if (allowZero ? parsed < 0 : parsed <= 0)
        {
            return false;
        }

        number = parsed;
        return true;
    }

    private static string? NormalizeEpisodeYear(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        return trimmed.Length == 4 && trimmed.All(char.IsDigit)
            ? trimmed
            : null;
    }

    private async Task ClosePlayerOverlayAsync()
    {
        StopPlaybackProgressSync();
        var progress = Math.Max(Player.CurrentPositionSeconds, 0);
        var duration = Math.Max(Player.DurationSeconds, 0);
        var currentVideoId = currentPlayingVideoId;
        var shouldPersistPlaybackState =
            !string.IsNullOrWhiteSpace(currentVideoId) &&
            !ShouldSuppressPlaybackPersistence(currentVideoId);

        PendingPlaybackStartPositionSeconds = 0;
        PendingPlaybackFilePath = string.Empty;
        PendingPlaybackDisplayPath = string.Empty;
        IsPlayerOverlayOpen = false;
        await Player.StopAsync();

        if (shouldPersistPlaybackState)
        {
            await PersistPlaybackStateAsync(currentVideoId, progress, duration);
        }

        currentPlaybackMode = PlaybackMode.None;
        currentPlayingVideoId = string.Empty;
        selectedPlaybackNavigationSeason = null;
        ResetAutoAdvanceState();
        UpdatePlayerNextEpisodeAction();
        StatusMessage = "\u5DF2\u5173\u95ED\u64AD\u653E\u5668\u8986\u76D6\u5C42\u3002";
        OnCommandStateChanged();
    }

    private void ToggleSearchPopup()
    {
        IsSortPopupOpen = false;
        IsSourcePopupOpen = false;
        IsSettingsPopupOpen = false;
        IsSearchPopupOpen = !IsSearchPopupOpen;
    }

    private void ToggleSortPopup()
    {
        IsSearchPopupOpen = false;
        IsSourcePopupOpen = false;
        IsSettingsPopupOpen = false;
        IsSortPopupOpen = !IsSortPopupOpen;
    }

    private void ToggleSourcePopup()
    {
        IsSearchPopupOpen = false;
        IsSortPopupOpen = false;
        IsSettingsPopupOpen = false;
        IsSourcePopupOpen = !IsSourcePopupOpen;
        if (IsSourcePopupOpen && DiscoveredNetworkSources.Count == 0)
        {
            _ = RefreshNetworkSourcesAsync();
        }
    }

    private void ToggleSettingsPopup()
    {
        IsSearchPopupOpen = false;
        IsSortPopupOpen = false;
        IsSourcePopupOpen = false;
        IsSettingsPopupOpen = !IsSettingsPopupOpen;
    }

    private void CloseDetail()
    {
        IsDetailOpen = false;
        currentDetailMovieId = null;
        currentDetailTvShowId = null;
        preferredDetailVideoId = null;
        allDetailFiles.Clear();
        ReplaceItems(DetailFiles, []);
        ReplaceItems(AvailableSeasons, []);
        selectedPlaybackNavigationSeason = null;
        DetailPrimaryFile = null;
        IsDetailLocked = false;
        DetailLockStateText = "\u672A\u9501\u5B9A";
        DetailLockActionText = "\u9501\u5B9A\u5143\u6570\u636E";
        DetailScrapeQuery = string.Empty;
        ClearDetailMetadataCandidates();
        RefreshDetailHeaderState();
        ResetAutoAdvanceState();
        UpdatePlayerNextEpisodeAction();
        OnPropertyChanged(nameof(HasSeasonTabs));
        OnCommandStateChanged();
    }

    private void SelectSortOption(LibrarySortOptionItem? option)
    {
        if (option is null)
        {
            return;
        }

        SelectedSortOptionItem = option;
        IsSortPopupOpen = false;
    }

    private void ToggleSortDirection()
    {
        IsSortDescending = !IsSortDescending;
    }

    private void SelectSeason(int? season)
    {
        if (!season.HasValue || SelectedSeason == season.Value)
        {
            return;
        }

        SelectedSeason = season.Value;
    }

    private Task RefreshNetworkSourcesAsync()
    {
        return RefreshNetworkSourcesAsync(updateBusyState: true);
    }

    private async Task RefreshNetworkSourcesAsync(bool updateBusyState)
    {
        if (networkDiscoveryInProgress)
        {
            return;
        }

        networkDiscoveryInProgress = true;
        OnCommandStateChanged();

        try
        {
            NetworkSourceStatus = "正在预扫描 WebDAV 和 SMB...";
            var sources = await networkShareDiscoveryService.DiscoverAsync();
            ReplaceItems(DiscoveredNetworkSources, sources);
            OnPropertyChanged(nameof(HasDiscoveredNetworkSources));
            NetworkSourceStatus = sources.Count == 0
                ? "未发现 SMB 或 WebDAV。"
                : $"已预扫描到 {sources.Count} 个网络入口。";
        }
        catch
        {
            ReplaceItems(DiscoveredNetworkSources, []);
            OnPropertyChanged(nameof(HasDiscoveredNetworkSources));
            NetworkSourceStatus = "预扫描网络媒体源失败。";
        }
        finally
        {
            networkDiscoveryInProgress = false;
            OnCommandStateChanged();
        }
    }

    private void OpenNetworkLogin(NetworkSourceDiscoveryItem? item)
    {
        if (item is null)
        {
            return;
        }

        PendingNetworkProtocolType = item.ProtocolType;
        PendingNetworkBaseUrl = item.BaseUrl;
        PendingNetworkDisplayName = item.Name;
        PendingNetworkUsername = string.Empty;
        PendingNetworkPassword = string.Empty;
        NetworkLoginTitle = $"登录 {item.ProtocolLabel}";
        ReplaceItems(NetworkShareFolders, []);
        OnPropertyChanged(nameof(HasNetworkShareFolders));
        IsNetworkLoginPanelOpen = true;
        NetworkSourceStatus = $"正在准备登录：{item.Name}";
        OnCommandStateChanged();
    }

    private void OpenManualNetworkLogin()
    {
        PendingNetworkProtocolType = "webdav";
        PendingNetworkBaseUrl = string.Empty;
        PendingNetworkDisplayName = string.Empty;
        PendingNetworkUsername = string.Empty;
        PendingNetworkPassword = string.Empty;
        NetworkLoginTitle = "添加局域网媒体源";
        ReplaceItems(NetworkShareFolders, []);
        OnPropertyChanged(nameof(HasNetworkShareFolders));
        IsNetworkLoginPanelOpen = true;
        NetworkSourceStatus = "输入 WebDAV 地址或 SMB 路径。";
        OnCommandStateChanged();
    }

    private void CloseNetworkLogin()
    {
        ClearNetworkLoginPanel(clearStatus: true);
        OnCommandStateChanged();
    }

    private void ClearNetworkLoginPanel(bool clearStatus)
    {
        IsNetworkLoginPanelOpen = false;
        PendingNetworkProtocolType = string.Empty;
        PendingNetworkBaseUrl = string.Empty;
        PendingNetworkDisplayName = string.Empty;
        PendingNetworkUsername = string.Empty;
        PendingNetworkPassword = string.Empty;
        ReplaceItems(NetworkShareFolders, []);
        OnPropertyChanged(nameof(HasNetworkShareFolders));
        if (clearStatus)
        {
            NetworkSourceStatus = string.Empty;
        }
    }

    private async Task SaveNetworkLoginAsync()
    {
        var normalizedBaseUrl = NormalizePendingNetworkBaseUrl(PendingNetworkBaseUrl.Trim());
        var protocolType = ResolvePendingNetworkProtocolType(PendingNetworkProtocolType, normalizedBaseUrl);
        PendingNetworkBaseUrl = normalizedBaseUrl;
        var source = new NetworkSourceDiscoveryItem
        {
            Name = normalizedBaseUrl,
            ProtocolType = protocolType,
            BaseUrl = normalizedBaseUrl,
            Description = normalizedBaseUrl
        };

        if (source.ProtocolKind is null || string.IsNullOrWhiteSpace(source.BaseUrl))
        {
            NetworkSourceStatus = "网络媒体源地址无效。";
            return;
        }

        IsBusy = true;
        OnCommandStateChanged();

        try
        {
            NetworkSourceStatus = "正在读取共享文件夹...";
            var folders = await networkShareDiscoveryService.ListFoldersAsync(
                source,
                PendingNetworkUsername,
                PendingNetworkPassword);
            ReplaceItems(NetworkShareFolders, folders);
            OnPropertyChanged(nameof(HasNetworkShareFolders));
            NetworkSourceStatus = folders.Count == 0
                ? "没有读取到可挂载的共享文件夹，请检查地址、用户名或密码。"
                : $"已读取到 {folders.Count} 个可挂载文件夹，点击星号挂载。";
        }
        catch (UnauthorizedAccessException)
        {
            ReplaceItems(NetworkShareFolders, []);
            OnPropertyChanged(nameof(HasNetworkShareFolders));
            NetworkSourceStatus = "登录失败：用户名或密码无效。";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
        {
            ReplaceItems(NetworkShareFolders, []);
            OnPropertyChanged(nameof(HasNetworkShareFolders));
            NetworkSourceStatus = $"读取共享文件夹失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private static string NormalizePendingNetworkBaseUrl(string baseUrl)
    {
        if (!baseUrl.StartsWith("smb://", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return baseUrl;
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath)
            .Trim('/')
            .Replace('/', '\\');

        return string.IsNullOrWhiteSpace(path)
            ? $@"\\{uri.Host}"
            : $@"\\{uri.Host}\{path}";
    }

    private static string ResolvePendingNetworkProtocolType(string protocolType, string baseUrl)
    {
        if (baseUrl.StartsWith(@"\\", StringComparison.Ordinal) ||
            baseUrl.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
        {
            return "smb";
        }

        if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "webdav";
        }

        return string.IsNullOrWhiteSpace(protocolType)
            ? "webdav"
            : protocolType.Trim().ToLowerInvariant();
    }

    private async Task MountNetworkFolderAsync(NetworkShareFolderItem? folder)
    {
        if (folder is null)
        {
            return;
        }

        var automationCancellationTokenSource = BeginLibraryAutomation();
        var cancellationToken = automationCancellationTokenSource.Token;
        IsBusy = true;
        OnCommandStateChanged();

        try
        {
            var source = folder.ToMediaSource();
            var sourceId = await mediaSourceRepository.AddAsync(source);
            ClearNetworkLoginPanel(clearStatus: false);
            await ReloadLibraryAsync();
            NetworkSourceStatus = sourceId > 0
                ? $"已挂载媒体源：{source.Name}，正在扫描并刮削..."
                : $"已保存媒体源：{source.Name}，正在扫描并刮削...";
            StatusMessage = NetworkSourceStatus;

            LastScanSummary = await libraryScanner.ScanAllAsync(cancellationToken);
            await ReloadLibraryAsync();
            await RefreshLibraryArtworkAsync(isExplicitRequest: true, forceThumbnails: true, cancellationToken);
            StatusMessage = string.IsNullOrWhiteSpace(StatusMessage)
                ? $"已挂载并扫描媒体源：{source.Name}"
                : $"已挂载媒体源：{source.Name}。{StatusMessage}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "\u5DF2\u505C\u6B62\u65B0\u6302\u8F7D\u5A92\u4F53\u6E90\u7684\u626B\u63CF\u548C\u522E\u524A\u4EFB\u52A1\u3002";
        }
        finally
        {
            IsBusy = false;
            CompleteLibraryAutomation(automationCancellationTokenSource);
            OnCommandStateChanged();
        }
    }

    private async Task EditSourceAsync(MediaSource? source)
    {
        if (source is null)
        {
            return;
        }

        if (source.Id is null)
        {
            StatusMessage = $"当前暂不支持编辑此媒体源：{source.Name}";
            return;
        }

        if (source.SupportsPickerEditing)
        {
            await EditLocalSourceAsync(source);
            return;
        }

        if (!source.SupportsInlineEditing)
        {
            StatusMessage = $"当前暂不支持编辑此媒体源：{source.Name}";
            return;
        }

        BeginEditWebDavSource(source);
    }

    private void BeginEditWebDavSource(MediaSource source)
    {
        if (source.Id is not long sourceId)
        {
            StatusMessage = $"当前暂不支持编辑此媒体源：{source.Name}";
            return;
        }

        var auth = MediaSourceAuthConfigSerializer.DeserializeWebDav(source.AuthConfig);

        EditingWebDavSourceId = sourceId;
        PendingWebDavName = source.Name;
        PendingWebDavUrl = source.BaseUrl;
        PendingWebDavUsername = auth?.Username ?? string.Empty;
        PendingWebDavPassword = auth?.Password ?? string.Empty;
        IsSourcePopupOpen = true;
        IsSearchPopupOpen = false;
        IsSortPopupOpen = false;
        IsSettingsPopupOpen = false;
        StatusMessage = $"正在编辑媒体源：{source.Name}";
        NotifyWebDavFormStateChanged();
        OnCommandStateChanged();
    }

    private async Task EditLocalSourceAsync(MediaSource source)
    {
        IsSourcePopupOpen = false;
        IsSettingsPopupOpen = false;
        ResetWebDavForm();

        var selectedPath = await folderPickerService.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            StatusMessage = $"已取消更换目录：{source.Name}";
            return;
        }

        if (!Directory.Exists(selectedPath))
        {
            StatusMessage = $"\u6587\u4EF6\u5939\u4E0D\u5B58\u5728\uFF1A{selectedPath}";
            return;
        }

        IsBusy = true;
        OnCommandStateChanged();

        try
        {
            var updatedSource = CreateLocalSource(selectedPath, source.Id);
            var updated = await mediaSourceRepository.UpdateAsync(updatedSource);
            if (!updated)
            {
                StatusMessage = "保存本地媒体源失败，可能是媒体源不存在，或与现有目录重复。";
                return;
            }

            await ReloadLibraryAsync();
            StatusMessage = $"已更新本地媒体源：{updatedSource.Name}";
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private void CancelWebDavEdit()
    {
        ResetWebDavForm();
        StatusMessage = "已取消 WebDAV 编辑。";
    }

    private void OnCommandStateChanged()
    {
        ScanCommand.NotifyCanExecuteChanged();
        AddFolderSourceCommand.NotifyCanExecuteChanged();
        AddWebDavSourceCommand.NotifyCanExecuteChanged();
        TestWebDavSourceCommand.NotifyCanExecuteChanged();
        OpenDetailCommand.NotifyCanExecuteChanged();
        PlayVideoCommand.NotifyCanExecuteChanged();
        PlayPrimaryCommand.NotifyCanExecuteChanged();
        OpenStandalonePlayerCommand.NotifyCanExecuteChanged();
        OpenStandalonePrimaryCommand.NotifyCanExecuteChanged();
        RefreshDetailMetadataCommand.NotifyCanExecuteChanged();
        SearchDetailMetadataCandidatesCommand.NotifyCanExecuteChanged();
        ApplyDetailMetadataCandidateCommand.NotifyCanExecuteChanged();
        ToggleDetailLockCommand.NotifyCanExecuteChanged();
        ShowDetailMetadataCandidatesPanelCommand.NotifyCanExecuteChanged();
        HideDetailMetadataCandidatesPanelCommand.NotifyCanExecuteChanged();
        ToggleDetailWatchedCommand.NotifyCanExecuteChanged();
        OpenPosterWatchStateConfirmationCommand.NotifyCanExecuteChanged();
        CancelPosterWatchStateConfirmationCommand.NotifyCanExecuteChanged();
        ConfirmPosterWatchStateCommand.NotifyCanExecuteChanged();
        OpenPosterScrapeCommand.NotifyCanExecuteChanged();
        SearchPosterMetadataCandidatesCommand.NotifyCanExecuteChanged();
        ApplyPosterMetadataCandidateCommand.NotifyCanExecuteChanged();
        OpenPosterEditCommand.NotifyCanExecuteChanged();
        ChoosePosterImageCommand.NotifyCanExecuteChanged();
        SavePosterMetadataEditCommand.NotifyCanExecuteChanged();
        OpenEpisodeEditCommand.NotifyCanExecuteChanged();
        SaveEpisodeEditCommand.NotifyCanExecuteChanged();
        ChooseEpisodeThumbnailImageCommand.NotifyCanExecuteChanged();
        EditSourceCommand.NotifyCanExecuteChanged();
        CancelWebDavEditCommand.NotifyCanExecuteChanged();
        RemoveSourceCommand.NotifyCanExecuteChanged();
        ToggleSourceEnabledCommand.NotifyCanExecuteChanged();
        RefreshNetworkSourcesCommand.NotifyCanExecuteChanged();
        OpenNetworkLoginCommand.NotifyCanExecuteChanged();
        OpenManualNetworkLoginCommand.NotifyCanExecuteChanged();
        SaveNetworkLoginCommand.NotifyCanExecuteChanged();
        MountNetworkFolderCommand.NotifyCanExecuteChanged();
        ClosePlayerOverlayCommand.NotifyCanExecuteChanged();
    }

    private CancellationTokenSource BeginLibraryAutomation()
    {
        CancelActiveLibraryAutomation();
        var cancellationTokenSource = new CancellationTokenSource();
        libraryAutomationCancellationTokenSource = cancellationTokenSource;
        IsLibraryScanInProgress = true;
        return cancellationTokenSource;
    }

    private void CancelActiveLibraryAutomation()
    {
        forceRefreshQueued = false;
        var cancellationTokenSource = libraryAutomationCancellationTokenSource;
        if (cancellationTokenSource is null || cancellationTokenSource.IsCancellationRequested)
        {
            return;
        }

        cancellationTokenSource.Cancel();
    }

    private void CompleteLibraryAutomation(CancellationTokenSource cancellationTokenSource)
    {
        if (ReferenceEquals(libraryAutomationCancellationTokenSource, cancellationTokenSource))
        {
            libraryAutomationCancellationTokenSource = null;
            IsLibraryScanInProgress = false;
        }

        cancellationTokenSource.Dispose();
    }

    private MediaSource? BuildPendingWebDavSource()
    {
        var normalizedUrl = PendingWebDavUrl.Trim();
        if (string.IsNullOrWhiteSpace(normalizedUrl))
        {
            StatusMessage = "请输入 WebDAV 地址。";
            return null;
        }

        var source = new MediaSource
        {
            Name = PendingWebDavName.Trim(),
            ProtocolType = "webdav",
            BaseUrl = normalizedUrl,
            AuthConfig = MediaSourceAuthConfigSerializer.SerializeWebDav(new WebDavAuthConfig(
                PendingWebDavUsername,
                PendingWebDavPassword))
        };

        if (!source.IsValidConfiguration())
        {
            StatusMessage = "WebDAV 地址无效，请输入以 http:// 或 https:// 开头的目录地址。";
            return null;
        }

        if (string.IsNullOrWhiteSpace(source.Name))
        {
            source.Name = ResolveWebDavSourceName(source.BaseUrl);
        }

        return source;
    }

    private static MediaSource CreateLocalSource(string selectedPath, long? sourceId = null)
    {
        var normalizedPath = selectedPath.Trim();
        var displayName = Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = normalizedPath;
        }

        return new MediaSource
        {
            Id = sourceId,
            Name = displayName,
            ProtocolType = "local",
            BaseUrl = normalizedPath
        };
    }

    private void ResetWebDavForm()
    {
        EditingWebDavSourceId = null;
        PendingWebDavName = string.Empty;
        PendingWebDavUrl = string.Empty;
        PendingWebDavUsername = string.Empty;
        PendingWebDavPassword = string.Empty;
        NotifyWebDavFormStateChanged();
        OnCommandStateChanged();
    }

    private void NotifyWebDavFormStateChanged()
    {
        OnPropertyChanged(nameof(IsEditingWebDavSource));
        OnPropertyChanged(nameof(WebDavFormTitle));
        OnPropertyChanged(nameof(WebDavSubmitActionText));
    }

    private static string ResolveWebDavSourceName(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return "WebDAV";
        }

        var lastSegment = uri.Segments.LastOrDefault()?.Trim('/');
        if (!string.IsNullOrWhiteSpace(lastSegment))
        {
            return Uri.UnescapeDataString(lastSegment);
        }

        return uri.Host;
    }

    private async Task ToggleSourceEnabledAsync(MediaSource? source)
    {
        if (source?.Id is null)
        {
            return;
        }

        var shouldEnable = !source.IsEnabled;
        if (!shouldEnable)
        {
            CancelActiveLibraryAutomation();
        }

        IsBusy = true;
        OnCommandStateChanged();

        try
        {
            var updated = await mediaSourceRepository.SetEnabledAsync(source.Id.Value, shouldEnable, DateTimeOffset.UtcNow);
            if (!updated)
            {
                StatusMessage = $"未找到媒体源：{source.Name}";
                return;
            }

            await ReloadLibraryAsync();
            if (currentDetailMovieId.HasValue || currentDetailTvShowId.HasValue)
            {
                await RefreshDetailFilesAsync();
            }

            if (!shouldEnable)
            {
                StatusMessage = $"已关闭媒体源：{source.Name}。首页已隐藏对应海报，扫描和刮削数据保留 30 天。";
                return;
            }

            StatusMessage = $"已开启媒体源：{source.Name}，正在扫描并刮削...";
            var automationCancellationTokenSource = BeginLibraryAutomation();
            var cancellationToken = automationCancellationTokenSource.Token;
            try
            {
                LastScanSummary = await libraryScanner.ScanAllAsync(cancellationToken);
                await ReloadLibraryAsync();
                await RefreshLibraryArtworkAsync(isExplicitRequest: true, forceThumbnails: true, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                StatusMessage = "\u5DF2\u505C\u6B62\u5A92\u4F53\u6E90\u7684\u626B\u63CF\u548C\u522E\u524A\u4EFB\u52A1\u3002";
                return;
            }
            finally
            {
                CompleteLibraryAutomation(automationCancellationTokenSource);
            }

            StatusMessage = string.IsNullOrWhiteSpace(StatusMessage)
                ? $"已开启并扫描媒体源：{source.Name}"
                : $"已开启媒体源：{source.Name}。{StatusMessage}";
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private async Task RemoveSourceAsync(MediaSource? source)
    {
        if (source?.Id is null)
        {
            return;
        }

        CancelActiveLibraryAutomation();
        OnCommandStateChanged();

        try
        {
            var removed = await mediaSourceRepository.SoftRemoveAsync(source.Id.Value, DateTimeOffset.UtcNow);
            if (removed && EditingWebDavSourceId == source.Id.Value)
            {
                ResetWebDavForm();
            }

            await ReloadLibraryAsync();

            if (currentDetailMovieId.HasValue || currentDetailTvShowId.HasValue)
            {
                await RefreshDetailFilesAsync();
            }

            StatusMessage = removed
                ? $"已移除媒体源：{source.Name}。已停止对应扫描、刮削和分集剧照刮削任务；首页已隐藏对应海报，扫描和刮削数据保留 30 天；重新挂载需要再次登录并标星。"
                : $"\u672A\u627E\u5230\u8981\u79FB\u9664\u7684\u5A92\u4F53\u6E90\uFF1A{source.Name}";
        }
        finally
        {
            OnCommandStateChanged();
        }
    }

    private static void ReplaceItems<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        if (target.SequenceEqual(source))
        {
            return;
        }

        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.IsPlaybackCompleted))
        {
            if (Player.IsPlaybackCompleted)
            {
                _ = TryAutoAdvanceToNextEpisodeAsync();
            }

            return;
        }

        if (e.PropertyName != nameof(PlayerViewModel.IsPlaying))
        {
            return;
        }

        if (Player.IsPlaying && HasActivePlaybackSession())
        {
            StartPlaybackProgressSync();
            return;
        }

        StopPlaybackProgressSync();
    }

    private async Task TryAutoAdvanceToNextEpisodeAsync()
    {
        if (isAutoAdvancingToNextEpisode ||
            !HasActivePlaybackSession() ||
            string.IsNullOrWhiteSpace(currentPlayingVideoId) ||
            string.Equals(autoAdvancedPlaybackVideoId, currentPlayingVideoId, StringComparison.Ordinal))
        {
            return;
        }

        var currentEpisode = ResolveCurrentPlaybackEpisode();
        if (currentEpisode is null || FindNextEpisode(currentEpisode) is null)
        {
            return;
        }

        autoAdvancedPlaybackVideoId = currentPlayingVideoId;
        isAutoAdvancingToNextEpisode = true;
        try
        {
            await PlayNextEpisodeAsync();
        }
        finally
        {
            isAutoAdvancingToNextEpisode = false;
        }
    }

    private async Task HandleStandalonePlaybackClosedAsync(
        string videoId,
        string fileName,
        PlaybackCloseResult result)
    {
        StopPlaybackProgressSync();
        var shouldPersistPlaybackState = !ShouldSuppressPlaybackPersistence(videoId);
        if (shouldPersistPlaybackState)
        {
            await PersistPlaybackStateAsync(videoId, result.PositionSeconds, result.DurationSeconds);
        }

        if (currentPlaybackMode == PlaybackMode.Standalone &&
            string.Equals(currentPlayingVideoId, videoId, StringComparison.Ordinal))
        {
            currentPlaybackMode = PlaybackMode.None;
            currentPlayingVideoId = string.Empty;
            selectedPlaybackNavigationSeason = null;
            ResetAutoAdvanceState();
            UpdatePlayerNextEpisodeAction();
            if (shouldPersistPlaybackState)
            {
                StatusMessage = $"已记录独立窗口播放进度：{fileName}";
            }
            OnCommandStateChanged();
        }
    }

    private async Task PersistPlaybackStateAsync(
        string videoId,
        double progress,
        double duration,
        bool refreshCollections = true)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return;
        }

        await videoFileRepository.UpdatePlaybackStateAsync(videoId, NormalizePersistedProgress(progress, duration), duration);
        preferredDetailVideoId = videoId;

        if (!refreshCollections)
        {
            return;
        }

        if (IsDetailOpen && (currentDetailMovieId.HasValue || currentDetailTvShowId.HasValue))
        {
            await RefreshDetailFilesAsync();
        }

        await RefreshContinueWatchingAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilters();
    }

    partial void OnSelectedSortOptionChanged(LibrarySortOption value)
    {
        ApplyFilters();
    }

    partial void OnIsSortDescendingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSortAscending));
        OnPropertyChanged(nameof(SortDirectionLabel));
        OnPropertyChanged(nameof(SortDirectionToolTip));
        ApplyFilters();
    }

    partial void OnSelectedSortOptionItemChanged(LibrarySortOptionItem value)
    {
        if (value is not null)
        {
            SelectedSortOption = value.Value;
        }
    }

    partial void OnDetailPrimaryFileChanged(LibraryVideoItem? value)
    {
        RefreshDetailHeaderState();
        OnCommandStateChanged();
    }

    partial void OnSelectedSeasonChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedSeasonOption));
        ApplyDetailFilter();
    }

    partial void OnIsDetailSeriesChanged(bool value)
    {
        OnPropertyChanged(nameof(HasSeasonTabs));
        OpenEpisodeEditCommand.NotifyCanExecuteChanged();
    }

    partial void OnStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasStatusMessage));
        OnPropertyChanged(nameof(HomeStatusMessage));
    }

    partial void OnIsLibraryScanInProgressChanged(bool value)
    {
        OnPropertyChanged(nameof(HasStatusMessage));
        OnPropertyChanged(nameof(HomeStatusMessage));
    }

    private static bool ShouldShowHomeStatusMessage(string? statusMessage)
    {
        if (string.IsNullOrWhiteSpace(statusMessage))
        {
            return false;
        }

        var normalized = statusMessage.Trim();
        return normalized.Contains("\u6B63\u5728", StringComparison.Ordinal) &&
               (normalized.Contains("\u626B\u63CF", StringComparison.Ordinal) ||
                normalized.Contains("\u522E\u524A", StringComparison.Ordinal) ||
                normalized.Contains("\u5206\u96C6\u5267\u7167", StringComparison.Ordinal));
    }

    partial void OnLastScanSummaryChanged(LibraryScanSummary? value)
    {
        OnPropertyChanged(nameof(HasLastScanSummary));
        OnPropertyChanged(nameof(HasLastScanDiagnostics));
        OnPropertyChanged(nameof(LastScanDiagnostics));
        OnPropertyChanged(nameof(LastScanOverviewText));
    }

    private void ApplyFilters()
    {
        IEnumerable<LibraryPosterItem> items = allLibraryItems;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            items = items.Where(x => x.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        items = SortLibraryItems(items);

        ReplaceItems(LibraryItems, items.ToList());
    }

    private IEnumerable<LibraryPosterItem> SortLibraryItems(IEnumerable<LibraryPosterItem> items)
    {
        return SelectedSortOption switch
        {
            LibrarySortOption.Year => IsSortDescending
                ? items
                    .OrderBy(static item => SortYearMissingRank(item))
                    .ThenByDescending(static item => SortYearValue(item))
                    .ThenBy(static item => item.Title, StringComparer.OrdinalIgnoreCase)
                : items
                    .OrderBy(static item => SortYearMissingRank(item))
                    .ThenBy(static item => SortYearValue(item))
                    .ThenBy(static item => item.Title, StringComparer.OrdinalIgnoreCase),
            LibrarySortOption.Rating => IsSortDescending
                ? items
                    .OrderBy(static item => SortRatingMissingRank(item))
                    .ThenByDescending(static item => item.VoteAverage)
                    .ThenBy(static item => item.Title, StringComparer.OrdinalIgnoreCase)
                : items
                    .OrderBy(static item => SortRatingMissingRank(item))
                    .ThenBy(static item => item.VoteAverage)
                    .ThenBy(static item => item.Title, StringComparer.OrdinalIgnoreCase),
            _ => IsSortDescending
                ? items.OrderByDescending(static item => item.Title, StringComparer.OrdinalIgnoreCase)
                : items.OrderBy(static item => item.Title, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static int SortYearMissingRank(LibraryPosterItem item)
    {
        return SortYearValue(item).HasValue ? 0 : 1;
    }

    private static int? SortYearValue(LibraryPosterItem item)
    {
        return int.TryParse(FormatMediaYear(item.Subtitle), out var year) ? year : null;
    }

    private static int SortRatingMissingRank(LibraryPosterItem item)
    {
        return item.VoteAverage.HasValue ? 0 : 1;
    }

    private async Task RefreshDetailFilesAsync()
    {
        IReadOnlyList<LibraryVideoItem> files = [];

        if (currentDetailMovieId.HasValue)
        {
            files = await videoFileRepository.GetByMovieAsync(currentDetailMovieId.Value);
        }
        else if (currentDetailTvShowId.HasValue)
        {
            files = await videoFileRepository.GetByTvShowAsync(currentDetailTvShowId.Value);
        }

        allDetailFiles.Clear();
        allDetailFiles.AddRange(files);
        BuildSeasonState(files, IsDetailSeries);
        ApplyDetailFilter();
    }

    private async Task RefreshContinueWatchingAsync()
    {
        var continueWatching = await videoFileRepository.GetContinueWatchingAsync();
        ReplaceItems(ContinueWatchingItems, continueWatching);
        OnPropertyChanged(nameof(HasContinueWatching));
        await RefreshLibraryPlaybackStatesAsync();
    }

    private async Task RefreshLibraryPlaybackStatesAsync()
    {
        if (allLibraryItems.Count == 0)
        {
            return;
        }

        var playbackStates = await videoFileRepository.GetLibraryPlaybackStatesAsync();
        var continuingIds = ContinueWatchingItems
            .Select(static item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var libraryStateChanged = false;
        for (var index = 0; index < allLibraryItems.Count; index++)
        {
            var item = allLibraryItems[index];
            var watchState = ResolvePosterWatchState(playbackStates, item.Id);
            var isContinuing = continuingIds.Contains(item.Id);
            if (item.WatchState != watchState || item.IsContinuing != isContinuing)
            {
                libraryStateChanged = true;
                allLibraryItems[index] = CopyPosterItem(
                    item,
                    watchState,
                    isContinuing);
            }
        }

        if (libraryStateChanged)
        {
            ApplyFilters();
        }
    }

    private static PlaybackWatchState ResolvePosterWatchState(
        IReadOnlyDictionary<string, PlaybackWatchState> playbackStates,
        string itemId)
    {
        return playbackStates.TryGetValue(itemId, out var watchState)
            ? watchState
            : PlaybackWatchState.Unwatched;
    }

    private static LibraryPosterItem CopyPosterItem(
        LibraryPosterItem item,
        PlaybackWatchState watchState,
        bool isContinuing)
    {
        return new LibraryPosterItem
        {
            Id = item.Id,
            Title = item.Title,
            Subtitle = item.Subtitle,
            PosterPath = item.PosterPath,
            VoteAverage = item.VoteAverage,
            MediaKind = item.MediaKind,
            ContinueWatchingProgress = item.ContinueWatchingProgress,
            ContinueWatchingLabel = item.ContinueWatchingLabel,
            IsContinuing = isContinuing,
            WatchState = watchState,
            MovieId = item.MovieId,
            TvShowId = item.TvShowId
        };
    }

    private void BuildSeasonState(IReadOnlyList<LibraryVideoItem> files, bool isSeries)
    {
        if (!isSeries)
        {
            ReplaceItems(AvailableSeasons, []);
            SelectedSeason = 1;
            selectedPlaybackNavigationSeason = null;
            OnPropertyChanged(nameof(HasSeasonTabs));
            return;
        }

        var seasons = files
            .Where(static file => file.IsTvEpisode)
            .Select(static file => file.SeasonNumber)
            .Distinct()
            .OrderBy(static season => season == 0 ? int.MaxValue : season)
            .ToList();

        if (seasons.Count == 0)
        {
            seasons = [1];
        }

        ReplaceItems(AvailableSeasons, seasons);

        var preferred = files.FirstOrDefault(file => file.Id == preferredDetailVideoId);
        var nextUp = files
            .Where(static file => !file.IsWatched)
            .OrderByDescending(static file => file.ProgressRatio)
            .ThenBy(static file => file.SeasonNumber == 0 ? int.MaxValue : file.SeasonNumber)
            .ThenBy(static file => file.EpisodeNumber)
            .FirstOrDefault();

        SelectedSeason = preferred?.SeasonNumber ?? nextUp?.SeasonNumber ?? seasons[0];
        selectedPlaybackNavigationSeason = preferred?.SeasonNumber ?? nextUp?.SeasonNumber ?? seasons[0];
        OnPropertyChanged(nameof(HasSeasonTabs));
    }

    private void ApplyDetailFilter()
    {
        IReadOnlyList<LibraryVideoItem> filtered;

        if (IsDetailSeries)
        {
            filtered = allDetailFiles
                .Where(file => file.SeasonNumber == SelectedSeason)
                .OrderBy(static file => file.EpisodeNumber)
                .ThenBy(static file => file.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else
        {
            filtered = allDetailFiles
                .OrderBy(static file => file.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        ReplaceItems(DetailFiles, filtered);
        ChooseDetailPrimaryFile(filtered);
        RefreshDetailHeaderState();
        OnPropertyChanged(nameof(HasDetailFiles));
        UpdatePlayerNextEpisodeAction();
    }

    private void ChooseDetailPrimaryFile(IReadOnlyList<LibraryVideoItem> files)
    {
        if (files.Count == 0)
        {
            DetailPrimaryFile = null;
            return;
        }

        var preferred = preferredDetailVideoId is not null
            ? files.FirstOrDefault(file => file.Id == preferredDetailVideoId)
            : null;
        var inProgress = files
            .Where(static file => !file.IsWatched)
            .OrderByDescending(static file => file.ProgressRatio)
            .ThenBy(static file => file.SeasonNumber == 0 ? int.MaxValue : file.SeasonNumber)
            .ThenBy(static file => file.EpisodeNumber)
            .FirstOrDefault();

        DetailPrimaryFile = preferred ?? inProgress ?? files.First();
    }

    private void ApplyPreferredDetailVideoSelection(LibraryVideoItem video)
    {
        preferredDetailVideoId = video.Id;
        if (video.IsTvEpisode)
        {
            selectedPlaybackNavigationSeason = video.SeasonNumber;
        }

        if (IsDetailSeries && video.IsTvEpisode && SelectedSeason != video.SeasonNumber)
        {
            SelectedSeason = video.SeasonNumber;
            return;
        }

        DetailPrimaryFile = video;
        RefreshDetailHeaderState();
        UpdatePlayerNextEpisodeAction();
    }

    private void UpdatePlayerNextEpisodeAction()
    {
        UpdatePlayerPlaybackNavigation();

        var currentEpisode = ResolveCurrentPlaybackEpisode();
        var previousEpisode = currentEpisode is null ? null : FindPreviousEpisode(currentEpisode);
        var nextEpisode = currentEpisode is null ? null : FindNextEpisode(currentEpisode);
        if (previousEpisode is null)
        {
            Player.ConfigurePreviousEpisodeAction(null);
        }
        else
        {
            var previousActionLabel = string.IsNullOrWhiteSpace(previousEpisode.EpisodeLabel)
                ? FormatEpisodeTitle(previousEpisode)
                : previousEpisode.EpisodeLabel;
            Player.ConfigurePreviousEpisodeAction(PlayPreviousEpisodeAsync, $"播放上一集 · {previousActionLabel}");
        }

        if (nextEpisode is null)
        {
            Player.ConfigureNextEpisodeAction(null);
            return;
        }

        var nextActionLabel = string.IsNullOrWhiteSpace(nextEpisode.EpisodeLabel)
            ? FormatEpisodeTitle(nextEpisode)
            : nextEpisode.EpisodeLabel;
        Player.ConfigureNextEpisodeAction(PlayNextEpisodeAsync, $"播放下一集 · {nextActionLabel}");
    }

    private void UpdatePlayerPlaybackNavigation()
    {
        if (!currentDetailTvShowId.HasValue)
        {
            Player.ConfigurePlaybackNavigation([], null);
            return;
        }

        var orderedEpisodes = GetOrderedEpisodes();

        if (orderedEpisodes.Count == 0)
        {
            Player.ConfigurePlaybackNavigation([], null);
            return;
        }

        var seasons = orderedEpisodes
            .Select(static file => file.SeasonNumber)
            .Distinct()
            .OrderBy(static season => season == 0 ? int.MaxValue : season)
            .ToList();
        var selectedSeason = ResolvePlaybackNavigationSeason(orderedEpisodes);
        var currentEpisodeId = !string.IsNullOrWhiteSpace(currentPlayingVideoId)
            ? currentPlayingVideoId
            : DetailPrimaryFile?.Id;
        var items = orderedEpisodes
            .Where(file => file.SeasonNumber == selectedSeason)
            .Select(file => new PlayerNavigationItem(
                file.Id,
                BuildPlaybackNavigationItemTitle(file),
                BuildPlaybackNavigationItemSubtitle(file),
                BuildPlaybackNavigationItemStatus(file, currentEpisodeId),
                string.Equals(file.Id, currentEpisodeId, StringComparison.Ordinal),
                file.IsWatched))
            .ToList();

        Player.ConfigurePlaybackNavigation(
            items,
            PlayNavigationItemAsync,
            "剧集导航",
            seasons,
            selectedSeason,
            SelectPlaybackNavigationSeasonAsync);
    }

    private LibraryVideoItem? ResolveCurrentPlaybackEpisode()
    {
        if (!currentDetailTvShowId.HasValue ||
            string.IsNullOrWhiteSpace(currentPlayingVideoId) ||
            allDetailFiles.Count == 0)
        {
            return null;
        }

        return allDetailFiles.FirstOrDefault(file =>
            file.IsTvEpisode &&
            string.Equals(file.Id, currentPlayingVideoId, StringComparison.Ordinal));
    }

    private LibraryVideoItem? ResolveCurrentPlaybackVideo()
    {
        if (string.IsNullOrWhiteSpace(currentPlayingVideoId) || allDetailFiles.Count == 0)
        {
            return null;
        }

        return allDetailFiles.FirstOrDefault(file =>
            string.Equals(file.Id, currentPlayingVideoId, StringComparison.Ordinal));
    }

    private List<LibraryVideoItem> GetOrderedEpisodes()
    {
        return allDetailFiles
            .Where(static file => file.IsTvEpisode)
            .OrderBy(static file => file.SeasonNumber == 0 ? int.MaxValue : file.SeasonNumber)
            .ThenBy(static file => file.EpisodeNumber)
            .ThenBy(static file => file.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private int ResolvePlaybackNavigationSeason(IReadOnlyList<LibraryVideoItem> orderedEpisodes)
    {
        var currentEpisode = ResolveCurrentPlaybackEpisode();
        if (currentEpisode is not null)
        {
            selectedPlaybackNavigationSeason = currentEpisode.SeasonNumber;
            return currentEpisode.SeasonNumber;
        }

        if (selectedPlaybackNavigationSeason.HasValue &&
            orderedEpisodes.Any(file => file.SeasonNumber == selectedPlaybackNavigationSeason.Value))
        {
            return selectedPlaybackNavigationSeason.Value;
        }

        var preferredSeason = DetailPrimaryFile?.SeasonNumber
            ?? orderedEpisodes.FirstOrDefault(file => file.Id == preferredDetailVideoId)?.SeasonNumber;
        if (preferredSeason.HasValue)
        {
            selectedPlaybackNavigationSeason = preferredSeason.Value;
            return preferredSeason.Value;
        }

        selectedPlaybackNavigationSeason = orderedEpisodes[0].SeasonNumber;
        return orderedEpisodes[0].SeasonNumber;
    }

    private LibraryVideoItem? FindPreviousEpisode(LibraryVideoItem currentEpisode)
    {
        if (!currentEpisode.IsTvEpisode)
        {
            return null;
        }

        var orderedEpisodes = GetOrderedEpisodes();
        var currentIndex = orderedEpisodes.FindIndex(file =>
            string.Equals(file.Id, currentEpisode.Id, StringComparison.Ordinal));
        if (currentIndex <= 0)
        {
            return null;
        }

        return orderedEpisodes[currentIndex - 1];
    }

    private LibraryVideoItem? FindNextEpisode(LibraryVideoItem currentEpisode)
    {
        if (!currentEpisode.IsTvEpisode)
        {
            return null;
        }

        var orderedEpisodes = GetOrderedEpisodes();

        var currentIndex = orderedEpisodes.FindIndex(file =>
            string.Equals(file.Id, currentEpisode.Id, StringComparison.Ordinal));
        if (currentIndex < 0 || currentIndex + 1 >= orderedEpisodes.Count)
        {
            return null;
        }

        return orderedEpisodes[currentIndex + 1];
    }

    private async Task PlayPreviousEpisodeAsync()
    {
        var currentEpisode = ResolveCurrentPlaybackEpisode();
        var previousEpisode = currentEpisode is null ? null : FindPreviousEpisode(currentEpisode);
        if (currentEpisode is null || previousEpisode is null)
        {
            UpdatePlayerNextEpisodeAction();
            return;
        }

        if (!MediaSourcePathResolver.IsPlayableLocation(previousEpisode.EffectivePlaybackPath))
        {
            StatusMessage = $"上一集文件不可播放：{previousEpisode.FileName}";
            return;
        }

        await TransitionToEpisodeAsync(previousEpisode);
    }

    private async Task PlayNextEpisodeAsync()
    {
        var currentEpisode = ResolveCurrentPlaybackEpisode();
        var nextEpisode = currentEpisode is null ? null : FindNextEpisode(currentEpisode);
        if (currentEpisode is null || nextEpisode is null)
        {
            UpdatePlayerNextEpisodeAction();
            return;
        }

        if (!MediaSourcePathResolver.IsPlayableLocation(nextEpisode.EffectivePlaybackPath))
        {
            StatusMessage = $"下一集文件不可播放：{nextEpisode.FileName}";
            return;
        }

        await TransitionToEpisodeAsync(nextEpisode);
    }

    private Task SelectPlaybackNavigationSeasonAsync(int season)
    {
        if (!currentDetailTvShowId.HasValue)
        {
            return Task.CompletedTask;
        }

        var orderedEpisodes = GetOrderedEpisodes();
        if (orderedEpisodes.Count == 0 || !orderedEpisodes.Any(file => file.SeasonNumber == season))
        {
            return Task.CompletedTask;
        }

        selectedPlaybackNavigationSeason = season;
        UpdatePlayerPlaybackNavigation();
        return Task.CompletedTask;
    }

    private async Task PlayNavigationItemAsync(string videoId)
    {
        var targetEpisode = ResolveDetailVideo(videoId);
        if (targetEpisode is null)
        {
            return;
        }

        await TransitionToEpisodeAsync(targetEpisode);
    }

    private async Task TransitionToEpisodeAsync(LibraryVideoItem targetEpisode)
    {
        if (!MediaSourcePathResolver.IsPlayableLocation(targetEpisode.EffectivePlaybackPath))
        {
            StatusMessage = $"文件不可播放：{targetEpisode.FileName}";
            return;
        }

        if (string.Equals(currentPlayingVideoId, targetEpisode.Id, StringComparison.Ordinal))
        {
            ApplyPreferredDetailVideoSelection(targetEpisode);
            return;
        }

        var currentEpisode = ResolveCurrentPlaybackEpisode();
        if (currentEpisode is not null)
        {
            StopPlaybackProgressSync();
            await PersistTransitionPlaybackStateAsync(currentEpisode);
            suppressedPlaybackPersistenceVideoId =
                currentPlaybackMode == PlaybackMode.Overlay
                    ? currentEpisode.Id
                    : null;
        }
        else
        {
            suppressedPlaybackPersistenceVideoId = null;
        }

        selectedPlaybackNavigationSeason = targetEpisode.SeasonNumber;
        preferredDetailVideoId = targetEpisode.Id;
        await RefreshDetailFilesAsync();
        await RefreshContinueWatchingAsync();

        var resolvedTargetEpisode = ResolveDetailVideo(targetEpisode.Id) ?? targetEpisode;
        ApplyPreferredDetailVideoSelection(resolvedTargetEpisode);

        switch (currentPlaybackMode)
        {
            case PlaybackMode.Overlay:
                await PlayVideoAsync(resolvedTargetEpisode);
                break;
            case PlaybackMode.Standalone:
                await OpenStandalonePlayerInternalAsync(
                    resolvedTargetEpisode,
                    replaceCurrentSession: true,
                    persistCurrentPlaybackState: false);
                break;
            default:
                suppressedPlaybackPersistenceVideoId = null;
                UpdatePlayerNextEpisodeAction();
                break;
        }
    }

    private static string BuildPlaybackNavigationItemTitle(LibraryVideoItem episode)
    {
        return episode.IsTvEpisode
            ? $"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}"
            : episode.FileName;
    }

    private static string BuildPlaybackNavigationItemSubtitle(LibraryVideoItem episode)
    {
        if (episode.IsTvEpisode && !string.IsNullOrWhiteSpace(episode.EpisodeLabel))
        {
            return episode.EpisodeLabel;
        }

        return episode.FileName;
    }

    private static string BuildPlaybackNavigationItemStatus(LibraryVideoItem episode, string? currentEpisodeId)
    {
        if (!string.IsNullOrWhiteSpace(currentEpisodeId) &&
            string.Equals(episode.Id, currentEpisodeId, StringComparison.Ordinal))
        {
            return "当前播放";
        }

        return episode.ProgressText;
    }

    private LibraryVideoItem? ResolveDetailVideo(string videoId)
    {
        return allDetailFiles.FirstOrDefault(file => string.Equals(file.Id, videoId, StringComparison.Ordinal));
    }

    private async Task PersistTransitionPlaybackStateAsync(LibraryVideoItem episode)
    {
        var duration = Math.Max(episode.Duration, Player.DurationSeconds);
        var progress = Math.Max(episode.PlayProgress, Player.CurrentPositionSeconds);
        var shouldMarkWatched = episode.IsWatched || Player.PlaybackProgressRatio >= PlaybackProgressRules.CompletionRatio;
        var persistedProgress = shouldMarkWatched && duration > 0
            ? duration
            : NormalizePersistedProgress(progress, duration);

        await videoFileRepository.UpdatePlaybackStateAsync(
            episode.Id,
            persistedProgress,
            duration > 0 ? duration : null);
    }

    private bool ShouldSuppressPlaybackPersistence(string videoId)
    {
        if (string.IsNullOrWhiteSpace(suppressedPlaybackPersistenceVideoId) ||
            !string.Equals(suppressedPlaybackPersistenceVideoId, videoId, StringComparison.Ordinal))
        {
            return false;
        }

        suppressedPlaybackPersistenceVideoId = null;
        return true;
    }

    private void ResetAutoAdvanceState()
    {
        autoAdvancedPlaybackVideoId = null;
        isAutoAdvancingToNextEpisode = false;
    }

    private void RefreshDetailHeaderState()
    {
        if (DetailFiles.Count == 0)
        {
            DetailFileSummary = "\u6B63\u5728\u8BFB\u53D6\u6587\u4EF6...";
            DetailPrimaryFileProgressText = "\u672A\u770B";
            DetailPrimaryTimeText = "00:00 / \u672A\u77E5\u65F6\u957F";
            DetailPrimaryActionText = "\u5F00\u59CB\u64AD\u653E";
            DetailPrimaryProgressRatio = 0;
            DetailPlaybackProgressRatio = 0;
            DetailSelectionHint = "\u5148\u6DFB\u52A0\u76EE\u5F55\u5E76\u6267\u884C\u626B\u63CF\uFF0C\u968F\u540E\u8FD9\u91CC\u4F1A\u663E\u793A\u6587\u4EF6\u3002";
            DetailWatchedActionText = "\u672A\u770B";
            OnPropertyChanged(nameof(CanShowDetailPrimaryProgress));
            return;
        }

        DetailFileSummary = IsDetailSeries
            ? $"\u5F53\u524D\u5B63\u5171 {DetailFiles.Count} \u96C6"
            : $"\u5171 {DetailFiles.Count} \u4E2A\u6587\u4EF6";

        if (DetailPrimaryFile is null)
        {
            DetailProgressSummary = "\u8BF7\u9009\u62E9\u6587\u4EF6";
            DetailPrimaryFileProgressText = "\u672A\u770B";
            DetailPrimaryTimeText = "00:00 / \u672A\u77E5\u65F6\u957F";
            DetailPrimaryActionText = "\u5F00\u59CB\u64AD\u653E";
            DetailPrimaryProgressRatio = 0;
            DetailPlaybackProgressRatio = 0;
            DetailSelectionHint = "\u70B9\u51FB\u4E0B\u65B9\u6761\u76EE\u53EF\u5207\u6362\u4E3B\u6587\u4EF6\u3002";
            DetailWatchedActionText = "\u672A\u770B";
            OnPropertyChanged(nameof(CanShowDetailPrimaryProgress));
            return;
        }

        var detailWatchState = ResolveDetailWatchState();
        var detailProgressRatio = ResolveDetailProgressRatio(detailWatchState);

        DetailPrimaryProgressRatio = detailProgressRatio;
        DetailPlaybackProgressRatio = DetailPrimaryFile.ProgressRatio;
        DetailPrimaryFileProgressText = DetailPrimaryFile.ProgressText;
        DetailPrimaryTimeText = $"{DetailPrimaryFile.PositionText} / {DetailPrimaryFile.DurationText}";
        DetailPrimaryActionText = BuildPrimaryActionText(DetailPrimaryFile);
        DetailProgressSummary = FormatWatchStateSummary(detailWatchState, detailProgressRatio);

        DetailSelectionHint = DetailPrimaryFile.IsTvEpisode
            ? $"\u5F53\u524D\u4E3B\u96C6\uFF1A{FormatEpisodeTitle(DetailPrimaryFile)}"
            : "\u5F53\u524D\u4E3B\u6587\u4EF6\u5DF2\u9009\u4E2D\u3002";
        DetailWatchedActionText = DetailPrimaryFile.WatchStateText;
        OnPropertyChanged(nameof(CanShowDetailPrimaryProgress));
    }

    private PlaybackWatchState ResolveDetailWatchState()
    {
        var files = allDetailFiles.Count > 0 ? allDetailFiles : DetailFiles.ToList();
        if (files.Count == 0 || files.All(static file => file.WatchState == PlaybackWatchState.Unwatched))
        {
            return PlaybackWatchState.Unwatched;
        }

        if (IsDetailSeries)
        {
            return files.All(static file => file.WatchState == PlaybackWatchState.Watched)
                ? PlaybackWatchState.Watched
                : PlaybackWatchState.InProgress;
        }

        return files.Any(static file => file.WatchState == PlaybackWatchState.Watched)
            ? PlaybackWatchState.Watched
            : PlaybackWatchState.InProgress;
    }

    private double ResolveDetailProgressRatio(PlaybackWatchState watchState)
    {
        if (watchState == PlaybackWatchState.Watched)
        {
            return 1;
        }

        var files = allDetailFiles.Count > 0 ? allDetailFiles : DetailFiles.ToList();
        if (files.Count == 0)
        {
            return 0;
        }

        if (IsDetailSeries)
        {
            return files.Average(static file => file.WatchState == PlaybackWatchState.Watched ? 1 : file.ProgressRatio);
        }

        return files.Max(static file => file.ProgressRatio);
    }

    private static string FormatWatchStateSummary(PlaybackWatchState watchState, double progressRatio)
    {
        return watchState switch
        {
            PlaybackWatchState.Watched => "\u5DF2\u770B",
            PlaybackWatchState.InProgress when progressRatio > 0 => $"\u672A\u770B\u5B8C \u00B7 {(Math.Clamp(progressRatio, 0, 1) * 100):F0}%",
            PlaybackWatchState.InProgress => "\u672A\u770B\u5B8C",
            _ => "\u672A\u770B"
        };
    }

    private static string BuildPrimaryActionText(LibraryVideoItem file)
    {
        var prefix = file.HasProgress && !file.IsWatched
            ? "\u7EE7\u7EED\u64AD\u653E"
            : "\u5F00\u59CB\u64AD\u653E";

        return file.IsTvEpisode
            ? $"{prefix} {FormatEpisodeTitle(file)}"
            : prefix;
    }

    private static string FormatEpisodeTitle(LibraryVideoItem file)
    {
        var seasonText = file.SeasonNumber == 0
            ? "\u7279\u522B\u7BC7"
            : $"\u7B2C {file.SeasonNumber} \u5B63";
        return $"{seasonText} \u7B2C {file.EpisodeNumber} \u96C6";
    }

    private static string FormatMovieSubtitle(Movie movie)
    {
        return HasVisibleMetadataYear(movie)
            ? FormatMediaYear(movie.ReleaseDate)
            : string.Empty;
    }

    private static string FormatTvShowSubtitle(TvShow show)
    {
        return HasVisibleMetadataYear(show)
            ? FormatMediaYear(show.FirstAirDate)
            : string.Empty;
    }

    private static string FormatTvShowSubtitle(string? firstAirDate)
    {
        return FormatMediaYear(firstAirDate);
    }

    private static string FormatMediaYear(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        return trimmed.Length >= 4
            ? trimmed[..4]
            : trimmed;
    }

    private static bool HasVisibleMetadataYear(Movie movie)
    {
        return !string.IsNullOrWhiteSpace(movie.ReleaseDate) &&
               (movie.IsLocked ||
                !string.IsNullOrWhiteSpace(movie.Overview) ||
                !string.IsNullOrWhiteSpace(movie.PosterPath) ||
                movie.VoteAverage.HasValue ||
                !string.IsNullOrWhiteSpace(movie.ProductionCountryCodes) ||
                !string.IsNullOrWhiteSpace(movie.OriginalLanguage));
    }

    private static bool HasVisibleMetadataYear(TvShow show)
    {
        return !string.IsNullOrWhiteSpace(show.FirstAirDate) &&
               (show.IsLocked ||
                !string.IsNullOrWhiteSpace(show.Overview) ||
                !string.IsNullOrWhiteSpace(show.PosterPath) ||
                show.VoteAverage.HasValue ||
                !string.IsNullOrWhiteSpace(show.ProductionCountryCodes) ||
                !string.IsNullOrWhiteSpace(show.OriginalLanguage));
    }

    private void QueueLibraryRefreshIfNeeded(bool force = false)
    {
        var tmdbSettings = Settings.BuildTmdbSettings();

        if (libraryRefreshTask is { IsCompleted: false })
        {
            if (force && !forceRefreshQueued)
            {
                forceRefreshQueued = true;
                libraryRefreshTask = libraryRefreshTask.ContinueWith(
                    async _ =>
                    {
                        if (!forceRefreshQueued)
                        {
                            return;
                        }

                        forceRefreshQueued = false;
                        await RunLibraryArtworkRefreshAsync(isExplicitRequest: false, forceThumbnails: true);
                    },
                    TaskScheduler.Default).Unwrap();
            }

            return;
        }

        if (!force && !NeedsMetadataRefresh(tmdbSettings) && !ShouldRefreshThumbnails(tmdbSettings, forceThumbnails: false))
        {
            return;
        }

        libraryRefreshTask = RunLibraryArtworkRefreshAsync(isExplicitRequest: false, forceThumbnails: force);
    }

    private async Task RunLibraryArtworkRefreshAsync(bool isExplicitRequest, bool forceThumbnails)
    {
        var automationCancellationTokenSource = BeginLibraryAutomation();
        var cancellationToken = automationCancellationTokenSource.Token;
        try
        {
            await RefreshLibraryArtworkAsync(isExplicitRequest, forceThumbnails, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "\u5DF2\u505C\u6B62\u81EA\u52A8\u522E\u524A\u548C\u5206\u96C6\u5267\u7167\u522E\u524A\u4EFB\u52A1\u3002";
        }
        finally
        {
            CompleteLibraryAutomation(automationCancellationTokenSource);
        }
    }

    private bool NeedsMetadataRefresh(TmdbSettings settings)
    {
        return allMovies.Any(movie => NeedsMovieRefresh(movie, settings)) ||
               allTvShows.Any(show => NeedsTvShowRefresh(show, settings));
    }

    private async Task RefreshLibraryArtworkAsync(
        bool isExplicitRequest,
        bool forceThumbnails,
        CancellationToken cancellationToken)
    {
        await libraryRefreshGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tmdbSettings = Settings.BuildTmdbSettings();
            var shouldRefreshMetadata = NeedsMetadataRefresh(tmdbSettings);
            var shouldRefreshThumbnails = ShouldRefreshThumbnails(tmdbSettings, forceThumbnails);

            if (!shouldRefreshMetadata && !shouldRefreshThumbnails)
            {
                if (isExplicitRequest)
                {
                    StatusMessage = IsTmdbAutomationDisabled(tmdbSettings)
                        ? "\u5F53\u524D\u5DF2\u5173\u95ED\u81EA\u52A8\u522E\u524A\u548C\u5206\u96C6\u5267\u7167\u4E0B\u8F7D\u3002"
                        : "\u5F53\u524D\u5A92\u4F53\u5E93\u5DF2\u65E0\u9700\u8981\u522E\u524A\u7684\u5185\u5BB9\u3002";
                }

                return;
            }

            StatusMessage = BuildArtworkRefreshProgressMessage(
                isExplicitRequest,
                shouldRefreshMetadata,
                shouldRefreshThumbnails);

            var movieQueue = allMovies
                .Where(movie => movie.Id.HasValue && NeedsMovieRefresh(movie, tmdbSettings))
                .Select(movie => (Id: movie.Id!.Value, movie.Title))
                .ToList();
            var tvShowQueue = allTvShows
                .Where(show => NeedsTvShowRefresh(show, tmdbSettings) || shouldRefreshThumbnails)
                .Select(show => (show.Id, show.Title, NeedsMetadata: NeedsTvShowRefresh(show, tmdbSettings)))
                .ToList();
            var totals = new LibraryArtworkRefreshTotals();

            foreach (var movie in movieQueue)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StatusMessage = $"\u6B63\u5728\u522E\u524A\u7535\u5F71\u5143\u6570\u636E\uFF1A{movie.Title}...";
                var metadataSummary = await libraryMetadataEnricher.EnrichMissingMovieMetadataAsync(
                    movie.Id,
                    tmdbSettings,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                totals.AddMetadata(metadataSummary);

                if (metadataSummary.HasChanges)
                {
                    await ReloadLibraryAsync();
                    await RefreshOpenDetailIfNeededAsync();
                }

                if (metadataSummary.EncounteredNetworkError)
                {
                    StatusMessage = ComposeArtworkRefreshStatusMessage(isExplicitRequest, tmdbSettings, totals);
                    return;
                }
            }

            foreach (var show in tvShowQueue)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (show.NeedsMetadata)
                {
                    StatusMessage = $"\u6B63\u5728\u522E\u524A\u5267\u96C6\u5143\u6570\u636E\uFF1A{show.Title}...";
                    var metadataSummary = await libraryMetadataEnricher.EnrichMissingTvShowMetadataAsync(
                        show.Id,
                        tmdbSettings,
                        cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    totals.AddMetadata(metadataSummary);

                    if (metadataSummary.HasChanges)
                    {
                        await ReloadLibraryAsync();
                        await RefreshOpenDetailIfNeededAsync();
                    }

                    if (metadataSummary.EncounteredNetworkError)
                    {
                        StatusMessage = ComposeArtworkRefreshStatusMessage(isExplicitRequest, tmdbSettings, totals);
                        return;
                    }
                }

                if (shouldRefreshThumbnails)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    StatusMessage = $"\u6B63\u5728\u522E\u524A\u5206\u96C6\u5267\u7167\uFF1A{show.Title}...";
                    var thumbnailSummary = await libraryThumbnailEnricher.EnrichMissingThumbnailsForTvShowAsync(
                        show.Id,
                        tmdbSettings,
                        cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    totals.AddThumbnails(thumbnailSummary);

                    if (thumbnailSummary.HasChanges &&
                        IsDetailOpen &&
                        currentDetailTvShowId.HasValue &&
                        currentDetailTvShowId.Value == show.Id)
                    {
                        await RefreshDetailFilesAsync();
                    }

                    if (thumbnailSummary.EncounteredNetworkError)
                    {
                        StatusMessage = ComposeArtworkRefreshStatusMessage(isExplicitRequest, tmdbSettings, totals);
                        return;
                    }
                }
            }

            var finalMessage = ComposeArtworkRefreshStatusMessage(isExplicitRequest, tmdbSettings, totals);
            if (!string.IsNullOrWhiteSpace(finalMessage))
            {
                StatusMessage = finalMessage;
            }
            else if (!isExplicitRequest)
            {
                StatusMessage = string.Empty;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            StatusMessage = "\u81EA\u52A8\u522E\u524A\u65F6\u53D1\u751F\u5F02\u5E38\u3002";
        }
        finally
        {
            libraryRefreshGate.Release();
        }
    }

    private static string ComposeArtworkRefreshStatusMessage(
        bool isExplicitRequest,
        TmdbSettings settings,
        LibraryArtworkRefreshTotals totals)
    {
        if (totals.MetadataNetworkError)
        {
            return string.IsNullOrWhiteSpace(totals.MetadataErrorMessage)
                ? "\u81EA\u52A8\u522E\u524A\u5931\u8D25\uFF1A\u65E0\u6CD5\u8FDE\u63A5\u7F51\u7EDC\u6216 TMDB\u3002"
                : $"\u81EA\u52A8\u522E\u524A\u5931\u8D25\uFF1A{totals.MetadataErrorMessage}";
        }

        if (totals.ThumbnailNetworkError)
        {
            return string.IsNullOrWhiteSpace(totals.ThumbnailErrorMessage)
                ? "\u5206\u96C6\u5267\u7167\u522E\u524A\u5931\u8D25\uFF1A\u65E0\u6CD5\u8FDE\u63A5\u7F51\u7EDC\u6216 TMDB\u3002"
                : $"\u5206\u96C6\u5267\u7167\u522E\u524A\u5931\u8D25\uFF1A{totals.ThumbnailErrorMessage}";
        }

        if (totals.HasChanges)
        {
            return $"\u5DF2\u5B8C\u6210\u81EA\u52A8\u522E\u524A\uFF1A{totals.UpdatedMovieCount} \u90E8\u7535\u5F71\u3001{totals.UpdatedTvShowCount} \u90E8\u5267\u96C6\uFF0C\u4E0B\u8F7D {totals.DownloadedPosterCount} \u5F20\u6D77\u62A5\u3001{totals.DownloadedThumbnailCount} \u5F20\u5206\u96C6\u5267\u7167\u3002";
        }

        return isExplicitRequest
            ? IsTmdbAutomationDisabled(settings)
                ? "\u5F53\u524D\u5DF2\u5173\u95ED\u81EA\u52A8\u522E\u524A\u548C\u5206\u96C6\u5267\u7167\u4E0B\u8F7D\u3002"
                : "\u626B\u63CF\u5B8C\u6210\uFF0C\u4F46\u5F53\u524D\u6CA1\u6709\u65B0\u7684\u5185\u5BB9\u9700\u8981\u522E\u524A\u3002"
            : string.Empty;
    }

    private static string BuildArtworkRefreshProgressMessage(
        bool isExplicitRequest,
        bool refreshMetadata,
        bool refreshThumbnails)
    {
        var modeText = isExplicitRequest ? "\u6B63\u5728\u6267\u884C" : "\u6B63\u5728\u540E\u53F0\u6267\u884C";
        if (refreshMetadata && refreshThumbnails)
        {
            return $"{modeText}\u5143\u6570\u636E\u3001\u6D77\u62A5\u548C\u5206\u96C6\u5267\u7167\u522E\u524A...";
        }

        if (refreshMetadata)
        {
            return $"{modeText}\u5143\u6570\u636E\u548C\u6D77\u62A5\u522E\u524A...";
        }

        return $"{modeText}\u5206\u96C6\u5267\u7167\u522E\u524A...";
    }

    private sealed class LibraryArtworkRefreshTotals
    {
        public int UpdatedMovieCount { get; private set; }

        public int UpdatedTvShowCount { get; private set; }

        public int DownloadedPosterCount { get; private set; }

        public int DownloadedThumbnailCount { get; private set; }

        public bool MetadataNetworkError { get; private set; }

        public string? MetadataErrorMessage { get; private set; }

        public bool ThumbnailNetworkError { get; private set; }

        public string? ThumbnailErrorMessage { get; private set; }

        public bool HasChanges =>
            UpdatedMovieCount > 0 ||
            UpdatedTvShowCount > 0 ||
            DownloadedPosterCount > 0 ||
            DownloadedThumbnailCount > 0;

        public void AddMetadata(LibraryMetadataEnrichmentSummary summary)
        {
            UpdatedMovieCount += summary.UpdatedMovieCount;
            UpdatedTvShowCount += summary.UpdatedTvShowCount;
            DownloadedPosterCount += summary.DownloadedPosterCount;
            MetadataNetworkError |= summary.EncounteredNetworkError;
            MetadataErrorMessage ??= summary.ErrorMessage;
        }

        public void AddThumbnails(LibraryThumbnailEnrichmentSummary summary)
        {
            DownloadedThumbnailCount += summary.DownloadedThumbnailCount;
            ThumbnailNetworkError |= summary.EncounteredNetworkError;
            ThumbnailErrorMessage ??= summary.ErrorMessage;
        }
    }

    private static bool NeedsMovieRefresh(Movie movie, TmdbSettings settings)
    {
        if (movie.IsLocked)
        {
            return false;
        }

        var needsPoster = settings.EnablePosterDownloads && !IsUsableLocalPoster(movie.PosterPath);
        var needsMetadata = settings.EnableMetadataEnrichment &&
                            (NeedsMetadataLanguageRefresh(movie.MetadataLanguage, settings)
                             || string.IsNullOrWhiteSpace(movie.ReleaseDate)
                             || string.IsNullOrWhiteSpace(movie.Overview)
                             || string.IsNullOrWhiteSpace(movie.ProductionCountryCodes)
                             || movie.VoteAverage is null);
        return needsPoster || needsMetadata;
    }

    private static bool NeedsTvShowRefresh(TvShow show, TmdbSettings settings)
    {
        if (show.IsLocked)
        {
            return false;
        }

        var needsPoster = settings.EnablePosterDownloads && !IsUsableLocalPoster(show.PosterPath);
        var needsMetadata = settings.EnableMetadataEnrichment &&
                            (NeedsMetadataLanguageRefresh(show.MetadataLanguage, settings)
                             || string.IsNullOrWhiteSpace(show.FirstAirDate)
                             || string.IsNullOrWhiteSpace(show.Overview)
                             || string.IsNullOrWhiteSpace(show.ProductionCountryCodes)
                             || show.VoteAverage is null);
        return needsPoster || needsMetadata;
    }

    private static bool NeedsMetadataLanguageRefresh(string? metadataLanguage, TmdbSettings settings)
    {
        var desiredLanguage = ResolveDesiredMetadataLanguage(settings.Language);
        return !string.IsNullOrWhiteSpace(desiredLanguage) &&
               !string.Equals(NormalizeMetadataLanguage(metadataLanguage), desiredLanguage, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDesiredMetadataLanguage(string? language)
    {
        var trimmed = language?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? TmdbSettings.DefaultLanguage : trimmed;
    }

    private static string? NormalizeMetadataLanguage(string? language)
    {
        var trimmed = language?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private bool ShouldRefreshThumbnails(TmdbSettings settings, bool forceThumbnails)
    {
        return settings.EnableEpisodeThumbnailDownloads && allTvShows.Count > 0 && (forceThumbnails || allTvShows.Count > 0);
    }

    private static bool IsUsableLocalPoster(string? posterPath)
    {
        return !string.IsNullOrWhiteSpace(posterPath)
               && Path.IsPathRooted(posterPath)
               && File.Exists(posterPath);
    }

    private static bool IsTmdbAutomationDisabled(TmdbSettings settings)
    {
        return !settings.EnableMetadataEnrichment
               && !settings.EnablePosterDownloads
               && !settings.EnableEpisodeThumbnailDownloads;
    }

    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        ApplyPlaybackPreferencesToPlayer();
        OnPropertyChanged(nameof(ShowMediaSourceRealPath));
        QueueLibraryRefreshIfNeeded(force: true);
    }

    private void ApplyPlaybackPreferencesToPlayer()
    {
        Player.ConfigureDefaultTracks(
            Settings.DefaultAudioTrackMode,
            Settings.DefaultSubtitleTrack,
            ResolveSmartAudioTrackMode());
    }

    private string? ResolveSmartAudioTrackMode()
    {
        if (!string.Equals(
                Settings.DefaultAudioTrackMode,
                PlaybackPreferenceSettings.DefaultAudioSmart,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var countryCodes = currentDetailMovieId.HasValue
            ? allMovies.FirstOrDefault(movie => movie.Id == currentDetailMovieId.Value)?.ProductionCountryCodes
            : currentDetailTvShowId.HasValue
                ? allTvShows.FirstOrDefault(show => show.Id == currentDetailTvShowId.Value)?.ProductionCountryCodes
                : null;
        var originalLanguage = currentDetailMovieId.HasValue
            ? allMovies.FirstOrDefault(movie => movie.Id == currentDetailMovieId.Value)?.OriginalLanguage
            : currentDetailTvShowId.HasValue
                ? allTvShows.FirstOrDefault(show => show.Id == currentDetailTvShowId.Value)?.OriginalLanguage
                : null;

        return ResolveAudioTrackModeFromTmdbMetadata(countryCodes, originalLanguage);
    }

    private static string? ResolveAudioTrackModeFromTmdbMetadata(string? productionCountryCodes, string? originalLanguage)
    {
        foreach (var countryCode in SplitMetadataCodes(productionCountryCodes))
        {
            var trackMode = ResolveAudioTrackModeFromCountryCode(countryCode);
            if (!string.IsNullOrWhiteSpace(trackMode))
            {
                return trackMode;
            }
        }

        return ResolveAudioTrackModeFromLanguage(originalLanguage);
    }

    private static string? ResolveAudioTrackModeFromCountryCode(string countryCode)
    {
        return countryCode.Trim().ToUpperInvariant() switch
        {
            "CN" or "HK" or "MO" or "TW" or "SG" => PlaybackPreferenceSettings.AudioChinese,
            "US" or "GB" or "AU" or "CA" or "NZ" or "IE" => PlaybackPreferenceSettings.AudioEnglish,
            "JP" => PlaybackPreferenceSettings.AudioJapanese,
            _ => null
        };
    }

    private static string? ResolveAudioTrackModeFromLanguage(string? language)
    {
        var normalized = language?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "zh" or "cn" or "chi" or "zho" or "cmn" or "yue" => PlaybackPreferenceSettings.AudioChinese,
            "en" or "eng" => PlaybackPreferenceSettings.AudioEnglish,
            "ja" or "jpn" => PlaybackPreferenceSettings.AudioJapanese,
            _ => null
        };
    }

    private static IEnumerable<string> SplitMetadataCodes(string? codes)
    {
        return string.IsNullOrWhiteSpace(codes)
            ? []
            : codes.Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private async Task RefreshOpenDetailIfNeededAsync()
    {
        if (!IsDetailOpen)
        {
            return;
        }

        if (currentDetailMovieId.HasValue)
        {
            var movie = allMovies.FirstOrDefault(x => x.Id == currentDetailMovieId);
            if (movie is not null)
            {
                await OpenDetailInternalAsync(
                    movie.Title,
                    movie.ReleaseDate ?? "\u7535\u5F71",
                    movie.Overview ?? "\u6682\u65E0\u7B80\u4ECB\u3002",
                    movie.PosterPath,
                    movie.VoteAverage,
                    "\u7535\u5F71",
                    movie.IsLocked,
                    false,
                    true,
                    () => videoFileRepository.GetByMovieAsync(movie.Id ?? 0));
                return;
            }
        }

        if (currentDetailTvShowId.HasValue)
        {
            var show = allTvShows.FirstOrDefault(x => x.Id == currentDetailTvShowId.Value);
            if (show is not null)
            {
                await OpenDetailInternalAsync(
                    show.Title,
                    FormatTvShowSubtitle(show.FirstAirDate),
                    show.Overview ?? "\u8FD9\u91CC\u4F1A\u6309\u5B63\u6574\u7406\u6240\u6709\u5206\u96C6\uFF0C\u4FBF\u4E8E\u8FD8\u539F mac \u7248\u7684\u4E3B\u96C6\u9009\u62E9\u548C\u7EE7\u7EED\u89C2\u770B\u903B\u8F91\u3002",
                    show.PosterPath,
                    show.VoteAverage,
                    "\u5267\u96C6",
                    show.IsLocked,
                    true,
                    true,
                    () => videoFileRepository.GetByTvShowAsync(show.Id));
            }
        }
    }

    private void StartPlaybackProgressSync()
    {
        if (playbackProgressSyncCancellationTokenSource is not null || !HasActivePlaybackSession())
        {
            return;
        }

        var cancellationTokenSource = new CancellationTokenSource();
        playbackProgressSyncCancellationTokenSource = cancellationTokenSource;
        _ = RunPlaybackProgressSyncAsync(cancellationTokenSource.Token);
    }

    private async Task RunPlaybackProgressSyncAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!HasActivePlaybackSession() ||
                    string.IsNullOrWhiteSpace(currentPlayingVideoId) ||
                    !Player.IsPlaying)
                {
                    continue;
                }

                var progress = Math.Max(Player.CurrentPositionSeconds, 0);
                var duration = Math.Max(Player.DurationSeconds, 0);
                if (!ShouldSyncPlaybackProgress(progress, duration))
                {
                    continue;
                }

                await PersistPlaybackStateAsync(currentPlayingVideoId, progress, duration, refreshCollections: false);
                lastSyncedPlaybackPositionSeconds = progress;
                lastSyncedPlaybackDurationSeconds = duration;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StopPlaybackProgressSync()
    {
        playbackProgressSyncCancellationTokenSource?.Cancel();
        playbackProgressSyncCancellationTokenSource?.Dispose();
        playbackProgressSyncCancellationTokenSource = null;
        ResetPlaybackProgressSyncState();
    }

    private void ResetPlaybackProgressSyncState()
    {
        lastSyncedPlaybackPositionSeconds = double.NaN;
        lastSyncedPlaybackDurationSeconds = double.NaN;
    }

    private bool HasActivePlaybackSession()
    {
        return currentPlaybackMode != PlaybackMode.None &&
               !string.IsNullOrWhiteSpace(currentPlayingVideoId);
    }

    private bool ShouldSyncPlaybackProgress(double progress, double duration)
    {
        if (progress <= 5 && duration <= 0)
        {
            return false;
        }

        if (double.IsNaN(lastSyncedPlaybackPositionSeconds))
        {
            return true;
        }

        if (duration > 0 &&
            progress >= duration * PlaybackProgressRules.CompletionRatio &&
            lastSyncedPlaybackPositionSeconds < duration * PlaybackProgressRules.CompletionRatio)
        {
            return true;
        }

        return Math.Abs(progress - lastSyncedPlaybackPositionSeconds) >= 10 ||
               Math.Abs(duration - lastSyncedPlaybackDurationSeconds) >= 1;
    }

    private static double NormalizePersistedProgress(double progress, double duration)
    {
        var normalizedProgress = Math.Max(progress, 0);
        if (duration > 0 && normalizedProgress >= duration - 2)
        {
            return duration;
        }

        return normalizedProgress;
    }

    private static double? GetPlaybackStartPosition(LibraryVideoItem video)
    {
        if (!video.HasProgress || video.IsWatched)
        {
            return null;
        }

        var normalizedProgress = Math.Max(video.PlayProgress, 0);
        if (video.Duration > 0)
        {
            var remaining = video.Duration - normalizedProgress;
            if (remaining <= 5)
            {
                return null;
            }

            normalizedProgress = Math.Min(normalizedProgress, Math.Max(video.Duration - 5, 0));
        }

        return normalizedProgress > 5 ? normalizedProgress : null;
    }

    private enum PlaybackMode
    {
        None,
        Overlay,
        Standalone
    }
}
