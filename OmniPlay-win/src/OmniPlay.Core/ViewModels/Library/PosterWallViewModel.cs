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
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace OmniPlay.Core.ViewModels.Library;

public partial class PosterWallViewModel : ObservableObject
{
    private const long MinimumFreeSpaceAfterOfflineCache = 256L * 1024L * 1024L;

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
    private readonly ITmdbConnectionTester? tmdbConnectionTester;
    private readonly INetworkShareDiscoveryService networkShareDiscoveryService;
    private readonly INetworkCredentialStore networkCredentialStore;
    private readonly IMediaServerPreflightService? mediaServerPreflightService;
    private readonly IOmniPlayDockerClient? omniPlayDockerClient;
    private readonly IDoubanMetadataService? doubanMetadataService;
    private readonly IPlaybackLauncher playbackLauncher;
    private readonly List<Movie> allMovies = [];
    private readonly List<TvShow> allTvShows = [];
    private readonly List<LibraryPosterItem> allLibraryItems = [];
    private readonly List<LibraryVideoItem> allDetailFiles = [];
    private readonly HashSet<string> knownLibraryPosterIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> newlyDiscoveredPosterOrder = new(StringComparer.OrdinalIgnoreCase);
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
    private bool isAutoAdvancingToNextMoviePart;
    private bool isMultipartTimelineSeekPreviewActive;
    private double multipartTimelineSeekPreviewSeconds;
    private double? nextPlaybackStartPositionOverride;
    private Task? libraryRefreshTask;
    private bool forceRefreshQueued;
    private bool startupScanAttempted;
    private bool startupScanInProgress;
    private bool networkDiscoveryInProgress;
    private readonly Dictionary<string, double> offlineCacheProgressByFileId = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> offlineCachedFileIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> offlineUnavailableFileIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> offlinePosterProgressByItemId = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> offlineCachedPosterIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> offlineUnavailablePosterIds = new(StringComparer.OrdinalIgnoreCase);
    private PlaybackMode currentPlaybackMode;
    private double lastSyncedPlaybackPositionSeconds = double.NaN;
    private double lastSyncedPlaybackDurationSeconds = double.NaN;
    private long nextNewlyDiscoveredPosterOrder;
    private bool libraryPosterTrackingInitialized;
    private bool suppressSortPreferencePersistence;

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
        INetworkCredentialStore networkCredentialStore,
        IPlaybackLauncher playbackLauncher,
        SettingsViewModel settings,
        PlayerViewModel player,
        IMediaServerPreflightService? mediaServerPreflightService = null,
        ITmdbConnectionTester? tmdbConnectionTester = null,
        IOmniPlayDockerClient? omniPlayDockerClient = null,
        IDoubanMetadataService? doubanMetadataService = null)
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
        this.tmdbConnectionTester = tmdbConnectionTester;
        this.networkShareDiscoveryService = networkShareDiscoveryService;
        this.networkCredentialStore = networkCredentialStore;
        this.mediaServerPreflightService = mediaServerPreflightService;
        this.omniPlayDockerClient = omniPlayDockerClient;
        this.doubanMetadataService = doubanMetadataService;
        this.playbackLauncher = playbackLauncher;

        Settings = settings;
        Player = player;

        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy && !startupScanInProgress);
        AddFolderSourceCommand = new AsyncRelayCommand(
            AddFolderSourceAsync,
            () => !IsBusy || IsLibraryScanInProgress,
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        AddWebDavSourceCommand = new AsyncRelayCommand(AddWebDavSourceAsync, () => !IsBusy);
        TestWebDavSourceCommand = new AsyncRelayCommand(TestWebDavSourceAsync, () => !IsBusy);
        OpenMediaServerPanelCommand = new RelayCommand(OpenMediaServerPanel, () => !IsBusy || IsLibraryScanInProgress);
        CloseMediaServerPanelCommand = new RelayCommand(CloseMediaServerPanel, () => !IsBusy);
        PreScanMediaServerSourceCommand = new AsyncRelayCommand(PreScanMediaServerSourceAsync, () => IsMediaServerPanelOpen && !IsBusy);
        AuthorizePlexCommand = new AsyncRelayCommand(AuthorizePlexAsync, () => IsMediaServerPanelOpen && !IsBusy && !IsPlexAuthorizationInProgress && PendingMediaServerUsesPlex);
        AddMediaServerSourceCommand = new AsyncRelayCommand(AddMediaServerSourceAsync, () => IsMediaServerPanelOpen && !IsBusy);
        OpenDetailCommand = new AsyncRelayCommand<LibraryPosterItem?>(OpenDetailAsync, item => item is not null);
        PlayVideoCommand = new AsyncRelayCommand<LibraryVideoItem?>(PlayVideoAsync, video => video is not null);
        PlayPrimaryCommand = new AsyncRelayCommand(PlayPrimaryAsync, () => DetailPrimaryFile is not null);
        OpenStandalonePlayerCommand = new AsyncRelayCommand<LibraryVideoItem?>(OpenStandalonePlayerAsync, video => video is not null);
        OpenStandalonePrimaryCommand = new AsyncRelayCommand(OpenStandalonePrimaryAsync, () => DetailPrimaryFile is not null);
        PlayerTimelineSeekBackwardCommand = new AsyncRelayCommand(() => SeekPlayerTimelineRelativeAsync(-10), CanSeekPlayerTimeline);
        PlayerTimelineSeekForwardCommand = new AsyncRelayCommand(() => SeekPlayerTimelineRelativeAsync(10), CanSeekPlayerTimeline);
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
        ToggleOfflineCacheModeCommand = new RelayCommand(ToggleHomeOfflineCacheMode);
        ToggleDetailOfflineCacheModeCommand = new RelayCommand(ToggleDetailOfflineCacheMode);
        ChooseOfflineCacheDirectoryCommand = new AsyncRelayCommand(ChooseOfflineCacheDirectoryAsync, () => !IsBusy);
        CachePosterItemCommand = new AsyncRelayCommand<LibraryPosterItem?>(CachePosterItemAsync, item => item is not null);
        CacheDetailPosterCommand = new AsyncRelayCommand(CacheDetailPosterAsync, () => DetailFiles.Count > 0);
        CacheSelectedSeasonCommand = new AsyncRelayCommand(CacheSelectedSeasonAsync, () => DetailFiles.Count > 0);
        CacheDetailEpisodeCommand = new AsyncRelayCommand<LibraryVideoItem?>(CacheDetailEpisodeAsync, video => video is not null);
        CloseOfflineCacheAlertCommand = new RelayCommand(CloseOfflineCacheAlert, () => IsOfflineCacheAlertOpen);
        ContinueWithoutTmdbStartupScanCommand = new AsyncRelayCommand(ContinueWithoutTmdbStartupScanAsync, () => IsStartupTmdbAlertOpen && !startupScanInProgress);
        OpenTmdbSettingsFromStartupAlertCommand = new RelayCommand(OpenTmdbSettingsFromStartupAlert);
        CloseStartupTmdbAlertCommand = new RelayCommand(CloseStartupTmdbAlert);
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
        BindPosterDoubanCommand = new AsyncRelayCommand(BindPosterDoubanAsync, () => posterMetadataTarget is not null && !IsBusy);
        SavePosterMetadataEditCommand = new AsyncRelayCommand(SavePosterMetadataEditAsync, () => posterMetadataTarget is not null && !IsBusy);
        OpenEpisodeEditCommand = new RelayCommand<LibraryVideoItem?>(OpenEpisodeEdit, video => video is not null && IsDetailSeries);
        CloseEpisodeEditCommand = new RelayCommand(CloseEpisodeEdit);
        SaveEpisodeEditCommand = new AsyncRelayCommand(SaveEpisodeEditAsync, () => episodeEditTarget is not null && !IsBusy);
        ChooseEpisodeThumbnailImageCommand = new AsyncRelayCommand(ChooseEpisodeThumbnailImageAsync, () => isEpisodeEditPanelOpen && !IsBusy);
        EditSourceCommand = new AsyncRelayCommand<MediaSource?>(EditSourceAsync, source => source is not null && !IsBusy);
        CancelWebDavEditCommand = new RelayCommand(CancelWebDavEdit, () => IsEditingWebDavSource && !IsBusy);
        RemoveSourceCommand = new AsyncRelayCommand<MediaSource?>(RemoveSourceAsync, source => source?.Id is not null);
        ToggleSourceEnabledCommand = new AsyncRelayCommand<MediaSource?>(
            ToggleSourceEnabledAsync,
            source => source?.Id is not null && (!IsBusy || source.IsEnabled));
        CheckSourceConnectivityCommand = new AsyncRelayCommand<MediaSource?>(
            CheckSourceConnectivityAsync,
            source => source?.Id is not null && !IsBusy);
        AddPendingExternalAddressCommand = new RelayCommand(AddPendingExternalAddress);
        RemovePendingExternalAddressCommand = new RelayCommand<MediaSourceExternalAddress?>(RemovePendingExternalAddress);
        RefreshNetworkSourcesCommand = new AsyncRelayCommand(RefreshNetworkSourcesAsync, () => !networkDiscoveryInProgress);
        OpenNetworkLoginCommand = new RelayCommand<NetworkSourceDiscoveryItem?>(OpenNetworkLogin, item => item is not null && (!IsBusy || IsLibraryScanInProgress));
        OpenManualNetworkLoginCommand = new RelayCommand(OpenManualNetworkLogin, () => !IsBusy || IsLibraryScanInProgress);
        CloseNetworkLoginCommand = new RelayCommand(CloseNetworkLogin);
        SaveNetworkLoginCommand = new AsyncRelayCommand(SaveNetworkLoginAsync, () => IsNetworkLoginPanelOpen && !IsBusy);
        ToggleNetworkPasswordVisibilityCommand = new RelayCommand(ToggleNetworkPasswordVisibility);
        ToggleMediaServerTokenVisibilityCommand = new RelayCommand(ToggleMediaServerTokenVisibility);
        MountNetworkFolderCommand = new AsyncRelayCommand<NetworkShareFolderItem?>(MountNetworkFolderAsync, folder => folder is not null && !IsBusy);
        ToggleNetworkFolderStarCommand = new RelayCommand<NetworkShareFolderItem?>(ToggleNetworkFolderStar, folder => folder is not null && !IsBusy);
        MountStarredNetworkFoldersCommand = new AsyncRelayCommand(MountStarredNetworkFoldersAsync, () => HasStarredNetworkShareFolders && !IsBusy);
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
        Settings.LuckyStunAddressUpdated += OnLuckyStunAddressUpdated;
        Player.PropertyChanged += OnPlayerPropertyChanged;
        selectedSortOptionItem = SortOptions[0];
        RefreshSortOptionSelectionStates();
        selectedMediaServerProtocolOption = MediaServerProtocolOptions[0];
    }

    public ObservableCollection<LibraryPosterItem> LibraryItems { get; } = [];

    public ObservableCollection<LibraryPosterItem> ContinueWatchingItems { get; } = [];

    public ObservableCollection<MediaSource> MediaSources { get; } = [];

    public ObservableCollection<NetworkSourceDiscoveryItem> DiscoveredNetworkSources { get; } = [];

    public ObservableCollection<NetworkShareFolderItem> NetworkShareFolders { get; } = [];

    public ObservableCollection<MediaSourceExternalAddress> PendingExternalAddresses { get; } = [];

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

    public IReadOnlyList<SettingsOptionItem> MediaServerProtocolOptions { get; } =
    [
        new("omniplay-docker", "OmniPlay Docker"),
        new("plex", "Plex"),
        new("emby", "Emby"),
        new("jellyfin", "Jellyfin")
    ];

    public string PendingMediaServerAddressWatermark => NormalizeMediaServerProtocolType(PendingMediaServerProtocolType) switch
    {
        "omniplay-docker" => "OmniPlay Docker 地址，例如 http://127.0.0.1:45722",
        "plex" => "Plex 地址，例如 http://127.0.0.1:32400",
        "emby" => "Emby 地址，例如 http://127.0.0.1:8096",
        "jellyfin" => "Jellyfin 地址，例如 http://127.0.0.1:8096",
        _ => "服务器地址，例如 http://127.0.0.1:8096"
    };

    public string PendingMediaServerUserWatermark => "用户名 / 用户 ID（可选）";

    public string PendingMediaServerTokenWatermark => NormalizeMediaServerProtocolType(PendingMediaServerProtocolType) switch
    {
        "plex" => "Plex 访问令牌（X-Plex-Token）",
        "omniplay-docker" => "OmniPlay Docker session cookie（无登录可留空）",
        _ => "密码 / API Key / 访问令牌"
    };

    public bool PendingMediaServerUsesUserId => NormalizeMediaServerProtocolType(PendingMediaServerProtocolType) is not ("plex" or "omniplay-docker");

    public bool PendingMediaServerUsesPlex => NormalizeMediaServerProtocolType(PendingMediaServerProtocolType) == "plex";

    public string PlexAuthorizeButtonText => IsPlexAuthorizationInProgress ? "等待 Plex 授权..." : "登录 Plex";

    public SettingsViewModel Settings { get; }

    public PlayerViewModel Player { get; }

    public IAsyncRelayCommand ScanCommand { get; }

    public IAsyncRelayCommand AddFolderSourceCommand { get; }

    public IAsyncRelayCommand AddWebDavSourceCommand { get; }

    public IAsyncRelayCommand TestWebDavSourceCommand { get; }

    public IRelayCommand OpenMediaServerPanelCommand { get; }

    public IRelayCommand CloseMediaServerPanelCommand { get; }

    public IAsyncRelayCommand PreScanMediaServerSourceCommand { get; }

    public IAsyncRelayCommand AuthorizePlexCommand { get; }

    public IAsyncRelayCommand AddMediaServerSourceCommand { get; }

    public IAsyncRelayCommand<LibraryPosterItem?> OpenDetailCommand { get; }

    public IAsyncRelayCommand<LibraryVideoItem?> PlayVideoCommand { get; }

    public IAsyncRelayCommand PlayPrimaryCommand { get; }

    public IAsyncRelayCommand<LibraryVideoItem?> OpenStandalonePlayerCommand { get; }

    public IAsyncRelayCommand OpenStandalonePrimaryCommand { get; }

    public IAsyncRelayCommand PlayerTimelineSeekBackwardCommand { get; }

    public IAsyncRelayCommand PlayerTimelineSeekForwardCommand { get; }

    public IAsyncRelayCommand RefreshDetailMetadataCommand { get; }

    public IAsyncRelayCommand SearchDetailMetadataCandidatesCommand { get; }

    public IAsyncRelayCommand<LibraryMetadataSearchCandidate?> ApplyDetailMetadataCandidateCommand { get; }

    public IAsyncRelayCommand ToggleDetailLockCommand { get; }

    public IRelayCommand ShowDetailMetadataCandidatesPanelCommand { get; }

    public IRelayCommand HideDetailMetadataCandidatesPanelCommand { get; }

    public IRelayCommand ClearDetailMetadataCandidatesCommand { get; }

    public IRelayCommand<LibraryVideoItem?> SelectDetailFileCommand { get; }

    public IAsyncRelayCommand<LibraryVideoItem?> ToggleDetailWatchedCommand { get; }

    public IRelayCommand ToggleOfflineCacheModeCommand { get; }

    public IRelayCommand ToggleDetailOfflineCacheModeCommand { get; }

    public IAsyncRelayCommand ChooseOfflineCacheDirectoryCommand { get; }

    public IAsyncRelayCommand<LibraryPosterItem?> CachePosterItemCommand { get; }

    public IAsyncRelayCommand CacheDetailPosterCommand { get; }

    public IAsyncRelayCommand CacheSelectedSeasonCommand { get; }

    public IAsyncRelayCommand<LibraryVideoItem?> CacheDetailEpisodeCommand { get; }

    public IRelayCommand CloseOfflineCacheAlertCommand { get; }

    public IAsyncRelayCommand ContinueWithoutTmdbStartupScanCommand { get; }

    public IRelayCommand OpenTmdbSettingsFromStartupAlertCommand { get; }

    public IRelayCommand CloseStartupTmdbAlertCommand { get; }

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

    public IAsyncRelayCommand BindPosterDoubanCommand { get; }

    public IAsyncRelayCommand SavePosterMetadataEditCommand { get; }

    public IRelayCommand<LibraryVideoItem?> OpenEpisodeEditCommand { get; }

    public IRelayCommand CloseEpisodeEditCommand { get; }

    public IAsyncRelayCommand SaveEpisodeEditCommand { get; }

    public IAsyncRelayCommand ChooseEpisodeThumbnailImageCommand { get; }

    public IAsyncRelayCommand<MediaSource?> EditSourceCommand { get; }

    public IRelayCommand CancelWebDavEditCommand { get; }

    public IAsyncRelayCommand<MediaSource?> RemoveSourceCommand { get; }

    public IAsyncRelayCommand<MediaSource?> ToggleSourceEnabledCommand { get; }

    public IAsyncRelayCommand<MediaSource?> CheckSourceConnectivityCommand { get; }

    public IRelayCommand AddPendingExternalAddressCommand { get; }

    public IRelayCommand<MediaSourceExternalAddress?> RemovePendingExternalAddressCommand { get; }

    public IAsyncRelayCommand RefreshNetworkSourcesCommand { get; }

    public IRelayCommand<NetworkSourceDiscoveryItem?> OpenNetworkLoginCommand { get; }

    public IRelayCommand OpenManualNetworkLoginCommand { get; }

    public IRelayCommand CloseNetworkLoginCommand { get; }

    public IAsyncRelayCommand SaveNetworkLoginCommand { get; }

    public IRelayCommand ToggleNetworkPasswordVisibilityCommand { get; }

    public IRelayCommand ToggleMediaServerTokenVisibilityCommand { get; }

    public IAsyncRelayCommand<NetworkShareFolderItem?> MountNetworkFolderCommand { get; }

    public IRelayCommand<NetworkShareFolderItem?> ToggleNetworkFolderStarCommand { get; }

    public IAsyncRelayCommand MountStarredNetworkFoldersCommand { get; }

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
        private bool isNetworkPasswordVisible;

    [ObservableProperty]
    private string networkSourceStatus = string.Empty;

    [ObservableProperty]
    private bool isMediaServerPanelOpen;

    [ObservableProperty]
    private string pendingMediaServerName = string.Empty;

    [ObservableProperty]
    private string pendingMediaServerProtocolType = "plex";

    [ObservableProperty]
    private SettingsOptionItem? selectedMediaServerProtocolOption;

    [ObservableProperty]
    private string pendingMediaServerBaseUrl = string.Empty;

    [ObservableProperty]
    private string pendingMediaServerToken = string.Empty;

    [ObservableProperty]
    private bool isMediaServerTokenVisible;

    [ObservableProperty]
    private string pendingMediaServerUserId = string.Empty;

    [ObservableProperty]
    private string mediaServerStatus = string.Empty;

    [ObservableProperty]
    private bool isPlexAuthorizationInProgress;

    [ObservableProperty]
    private bool isSettingsPopupOpen;

    [ObservableProperty]
    private bool isOfflineCacheModeActive;

    [ObservableProperty]
    private bool isHomeOfflineCacheModeActive;

    [ObservableProperty]
    private bool isDetailOfflineCacheModeActive;

    [ObservableProperty]
    private bool isStartupTmdbAlertOpen;

    [ObservableProperty]
    private string startupTmdbAlertMessage = string.Empty;

    [ObservableProperty]
    private bool isOfflineCacheAlertOpen;

    [ObservableProperty]
    private string offlineCacheAlertTitle = string.Empty;

    [ObservableProperty]
    private string offlineCacheAlertMessage = string.Empty;

    [ObservableProperty]
    private double selectedSeasonOfflineCacheProgress;

    [ObservableProperty]
    private bool showSelectedSeasonOfflineCacheProgress;

    [ObservableProperty]
    private double detailPosterOfflineCacheProgress;

    [ObservableProperty]
    private bool showDetailPosterOfflineCacheProgress;

    [ObservableProperty]
    private string detailPosterOfflineCacheGlyph = "\u2193";

    [ObservableProperty]
    private string detailPosterOfflineCacheTip = "\u79BB\u7EBF\u7F13\u5B58\u6574\u90E8\u5F71\u7247\u6216\u5267\u96C6";

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
    private string pendingPlaybackMediaFileName = string.Empty;

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

    [ObservableProperty]
    private string posterDoubanSubject = string.Empty;

    [ObservableProperty]
    private string posterDoubanStatus = string.Empty;

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
        IsDetailMultipartMovie
            ? ResolveMultipartSavedTimeline().DurationSeconds > 0 &&
              ResolveMultipartSavedTimeline().PositionSeconds > 0 &&
              ResolveMultipartSavedTimeline().PositionSeconds < ResolveMultipartSavedTimeline().DurationSeconds * PlaybackProgressRules.CompletionRatio
            : DetailPrimaryFile is { Duration: > 0, PlayProgress: > 0 } file && !file.IsWatched;

    public double PlayerTimelineDurationSeconds => ResolvePlayerTimeline().DurationSeconds;

    public double PlayerTimelineSeekPositionSeconds
    {
        get => !IsPlayingMultipartMovie
            ? Player.SeekPositionSeconds
            : isMultipartTimelineSeekPreviewActive
            ? multipartTimelineSeekPreviewSeconds
            : ResolvePlayerTimeline().PositionSeconds;
        set
        {
            if (IsPlayingMultipartMovie)
            {
                multipartTimelineSeekPreviewSeconds = ClampPlayerTimelinePosition(value);
                NotifyPlayerTimelineChanged();
                return;
            }

            Player.UpdateSeekPreview(value);
        }
    }

    public IReadOnlyList<double> PlayerTimelineSegmentBoundaries => ResolvePlayerTimelineSegmentBoundaries();

    public string PlayerTimelineCurrentPositionText =>
        !IsPlayingMultipartMovie
            ? Player.CurrentPositionText
            : FormatPlaybackTime(isMultipartTimelineSeekPreviewActive
            ? multipartTimelineSeekPreviewSeconds
            : ResolvePlayerTimeline().PositionSeconds);

    public string PlayerTimelineRemainingTimeText
    {
        get
        {
            if (!IsPlayingMultipartMovie)
            {
                return Player.RemainingTimeText;
            }

            var timeline = ResolvePlayerTimeline();
            if (timeline.DurationSeconds <= 0)
            {
                return "--:--";
            }

            return FormatPlaybackTime(timeline.DurationSeconds);
        }
    }

    public bool HasStatusMessage => ShouldShowHomeStatusMessage(HomeStatusMessage);

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

    public bool IsNetworkCredentialFormVisible => !HasNetworkShareFolders;

    public int StarredNetworkShareFolderCount => NetworkShareFolders.Count(static folder => folder.IsStarred);

    public bool HasStarredNetworkShareFolders => StarredNetworkShareFolderCount > 0;

    public string MountStarredNetworkFoldersActionText => HasStarredNetworkShareFolders
        ? $"关闭并挂载 {StarredNetworkShareFolderCount} 个文件夹"
        : "关闭";

    public string NetworkPasswordVisibilityGlyph => IsNetworkPasswordVisible ? "🙈" : "👁";

    public string MediaServerTokenVisibilityGlyph => IsMediaServerTokenVisible ? "🙈" : "👁";

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
            ApplySavedLibraryViewPreferences();
            _ = Settings.CheckForUpdatesOnStartupIfNeededAsync();
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
            LastScanSummary = await RunLibraryScanAndArtworkAsync(
                isExplicitRequest: true,
                forceThumbnails: true,
                cancellationToken);
            var scanPrefix = $"\u626B\u63CF\u5B8C\u6210\uFF1A\u65B0\u589E {LastScanSummary.NewMovieCount} \u90E8\u7535\u5F71\u3001{LastScanSummary.NewTvShowCount} \u90E8\u5267\u96C6\uFF0C\u79FB\u9664 {LastScanSummary.RemovedVideoFileCount} \u4E2A\u89C6\u9891\u6587\u4EF6\u3002";
            if (LastScanSummary.HasDiagnostics)
            {
                scanPrefix = $"{scanPrefix} 另有 {LastScanSummary.Diagnostics.Count} 条扫描诊断。";
            }

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

    private async Task<LibraryScanSummary> RunLibraryScanAndArtworkAsync(
        bool isExplicitRequest,
        bool forceThumbnails,
        CancellationToken cancellationToken,
        IReadOnlyCollection<long>? sourceIds = null,
        bool skipTmdbArtwork = false)
    {
        var targetSourceIds = sourceIds is null ? null : sourceIds.ToHashSet();
        var sources = (await mediaSourceRepository.GetAllAsync(cancellationToken))
            .Where(source => source is { Id: not null } &&
                             source.IsActive &&
                             (targetSourceIds is null || targetSourceIds.Contains(source.Id!.Value)))
            .OrderBy(source => source.Id)
            .ToList();

        var aggregate = new LibraryScanAggregate();
        libraryScanner.ClearDeferredUnidentifiedScanGroups();
        if (sources.Count == 0)
        {
            return aggregate.ToSummary();
        }

        try
        {
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StatusMessage = $"正在扫描媒体源：{source.Name}...";
                var sourceSummary = await libraryScanner.ScanSourceAsync(
                    source.Id!.Value,
                    cancellationToken,
                    afterItemIndexed: null,
                    deferUnidentifiedGroups: true);
                aggregate.Add(sourceSummary);
                LastScanSummary = aggregate.ToSummary();

                await ReloadLibraryAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var deferredSummary = await libraryScanner.CommitDeferredUnidentifiedScanGroupsAsync(cancellationToken);
            if (deferredSummary.NewMovieCount > 0 ||
                deferredSummary.NewTvShowCount > 0 ||
                deferredSummary.NewVideoFileCount > 0 ||
                deferredSummary.RemovedVideoFileCount > 0 ||
                deferredSummary.HasDiagnostics)
            {
                StatusMessage = "正在显示未识别视频...";
                aggregate.Add(deferredSummary);
                LastScanSummary = aggregate.ToSummary();
                await ReloadLibraryAsync();
            }

            if (forceThumbnails && !skipTmdbArtwork)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReloadLibraryAsync();
                if (!await EnsureTmdbAvailableForPendingArtworkAsync(isExplicitRequest, forceThumbnails: true, cancellationToken))
                {
                    return aggregate.ToSummary();
                }

                StatusMessage = "正在刮削元数据和分集剧照...";
                await RefreshLibraryArtworkAsync(isExplicitRequest, forceThumbnails: true, cancellationToken);
            }
            else if (!skipTmdbArtwork)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReloadLibraryAsync();
                if (await EnsureTmdbAvailableForPendingArtworkAsync(isExplicitRequest, forceThumbnails: false, cancellationToken))
                {
                    StatusMessage = "正在刮削元数据和海报...";
                    await RefreshLibraryArtworkAsync(isExplicitRequest, forceThumbnails: false, cancellationToken);
                }
            }
            else if (skipTmdbArtwork)
            {
                StatusMessage = "已完成本地扫描，等待 TMDB API 连通后再刮削。";
            }
        }
        catch
        {
            libraryScanner.ClearDeferredUnidentifiedScanGroups();
            throw;
        }

        return aggregate.ToSummary();
    }

    private async Task<bool> EnsureTmdbAvailableForPendingArtworkAsync(
        bool isExplicitRequest,
        bool forceThumbnails,
        CancellationToken cancellationToken)
    {
        var tmdbSettings = Settings.BuildTmdbSettings();
        var needsMetadata = NeedsMetadataRefresh(tmdbSettings);
        var needsThumbnails = ShouldRefreshThumbnails(tmdbSettings, forceThumbnails);
        if (!needsMetadata && !needsThumbnails)
        {
            StatusMessage = "当前媒体库已无需刮削的内容。";
            return false;
        }

        if (tmdbConnectionTester is null)
        {
            return true;
        }

        StatusMessage = "正在检测 TMDB API 连通性...";
        var connection = await tmdbConnectionTester.TestConnectionAsync(tmdbSettings, cancellationToken);
        if (connection.Success)
        {
            return true;
        }

        if (isExplicitRequest)
        {
            StatusMessage = $"TMDB API 无法连接，已保留未刮削条目：{connection.Message}";
        }
        else
        {
            StartupTmdbAlertMessage = $"建议添加自己的 TMDB API 后再刮削海报与影视信息。\n{connection.Message}";
            IsStartupTmdbAlertOpen = true;
            StatusMessage = "TMDB API 无法连接，已完成本地扫描。";
        }

        return false;
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
            if (sourceId <= 0)
            {
                await ReloadLibraryAsync();
                StatusMessage = "已保存本地媒体源。";
                return;
            }

            var automationCancellationTokenSource = BeginLibraryAutomation();
            var cancellationToken = automationCancellationTokenSource.Token;
            IsBusy = false;
            OnCommandStateChanged();
            try
            {
                StatusMessage = $"已添加本地媒体源：{source.Name}，正在扫描并刮削...";
                await ReloadLibraryAsync();
                LastScanSummary = await RunLibraryScanAndArtworkAsync(
                    isExplicitRequest: true,
                    forceThumbnails: true,
                    cancellationToken,
                    new[] { sourceId });
                StatusMessage = string.IsNullOrWhiteSpace(StatusMessage)
                    ? $"已添加并扫描本地媒体源：{source.Name}"
                    : $"已添加本地媒体源：{source.Name}。{StatusMessage}";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                StatusMessage = "已停止新媒体源的扫描和刮削任务。";
            }
            finally
            {
                CompleteLibraryAutomation(automationCancellationTokenSource);
            }
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
            ResetWebDavForm();
            IsSourcePopupOpen = false;
            if (sourceId <= 0)
            {
                await ReloadLibraryAsync();
                StatusMessage = "已保存 WebDAV 媒体源。";
                return;
            }

            var automationCancellationTokenSource = BeginLibraryAutomation();
            var cancellationToken = automationCancellationTokenSource.Token;
            IsBusy = false;
            OnCommandStateChanged();
            try
            {
                StatusMessage = $"已添加 WebDAV 媒体源：{source.Name}，正在扫描并刮削...";
                await ReloadLibraryAsync();
                LastScanSummary = await RunLibraryScanAndArtworkAsync(
                    isExplicitRequest: true,
                    forceThumbnails: true,
                    cancellationToken,
                    new[] { sourceId });
                StatusMessage = string.IsNullOrWhiteSpace(StatusMessage)
                    ? $"已添加并扫描 WebDAV 媒体源：{source.Name}"
                    : $"已添加 WebDAV 媒体源：{source.Name}。{StatusMessage}";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                StatusMessage = "已停止新媒体源的扫描和刮削任务。";
            }
            finally
            {
                CompleteLibraryAutomation(automationCancellationTokenSource);
            }
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
        PendingPlaybackMediaFileName = string.Empty;
        PendingPlaybackFilePath = filePath;
        PendingPlaybackDisplayPath = filePath;
        PendingPlaybackMediaFileName = Path.GetFileName(filePath);
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
        ReplaceItems(ContinueWatchingItems, continueWatching.Select(ApplyOfflineCacheState).ToList());
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
                DoubanRating = movie.DoubanRating,
                MediaKind = "\u7535\u5F71",
                IsContinuing = continuingIds.Contains(itemId),
                WatchState = watchState,
                MovieId = movie.Id
            };
        }));
        allLibraryItems.AddRange(GroupTvShowsForHome(tvShows).Select(group =>
        {
            var show = group.Representative;
            var itemId = $"tv-{show.Id}";
            var groupIds = group.Shows
                .Select(static groupedShow => $"tv-{groupedShow.Id}")
                .ToList();
            var watchState = ResolvePosterWatchState(playbackStates, groupIds);
            return new LibraryPosterItem
            {
                Id = itemId,
                Title = show.Title,
                Subtitle = FormatTvShowSubtitle(show),
                PosterPath = show.PosterPath,
                VoteAverage = show.VoteAverage,
                DoubanRating = show.DoubanRating,
                MediaKind = "\u5267\u96C6",
                IsContinuing = groupIds.Any(continuingIds.Contains),
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
        var doubanRating = currentDetailMovieId.HasValue
            ? allMovies.FirstOrDefault(movie => movie.Id == currentDetailMovieId.Value)?.DoubanRating
            : currentDetailTvShowId.HasValue
                ? allTvShows.FirstOrDefault(show => show.Id == currentDetailTvShowId.Value)?.DoubanRating
                : null;
        DetailRatingText = FormatCombinedRatingText(voteAverage, doubanRating);
        DetailCollectionHeading = isSeries ? "\u5206\u96C6\u5217\u8868" : "\u6587\u4EF6\u5217\u8868";
        DetailFileSummary = "\u6B63\u5728\u8BFB\u53D6\u6587\u4EF6...";

        allDetailFiles.Clear();
        ReplaceItems(DetailFiles, []);
        ReplaceItems(AvailableSeasons, []);
        selectedPlaybackNavigationSeason = null;
        DetailPrimaryFile = null;
        RefreshDetailHeaderState();
        UpdatePlayerNextEpisodeAction();

        var files = ApplyEpisodeDisplayDisambiguation(await fileLoader());
        allDetailFiles.AddRange(files.Select(ApplyOfflineCacheState));
        BuildSeasonState(files, isSeries);
        ApplyDetailFilter();

        if (!IsLibraryScanInProgress)
        {
            StatusMessage = $"\u5DF2\u6253\u5F00\u300A{title}\u300B\u8BE6\u60C5\u3002";
        }
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
            LastScanSummary = await RunLibraryScanAndArtworkAsync(
                isExplicitRequest: false,
                forceThumbnails: true,
                cancellationToken);

            var scanPrefix = $"后台扫描完成：新增 {LastScanSummary.NewMovieCount} 部电影、{LastScanSummary.NewTvShowCount} 部剧集，移除 {LastScanSummary.RemovedVideoFileCount} 个视频文件。";
            if (LastScanSummary.HasDiagnostics)
            {
                scanPrefix = $"{scanPrefix} 另有 {LastScanSummary.Diagnostics.Count} 条扫描诊断。";
            }

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

    private async Task ContinueWithoutTmdbStartupScanAsync()
    {
        if (startupScanInProgress)
        {
            return;
        }

        IsStartupTmdbAlertOpen = false;
        var automationCancellationTokenSource = BeginLibraryAutomation();
        var cancellationToken = automationCancellationTokenSource.Token;
        startupScanInProgress = true;
        OnCommandStateChanged();

        try
        {
            StatusMessage = "正在本地扫描媒体源...";
            LastScanSummary = await RunLibraryScanAndArtworkAsync(
                isExplicitRequest: false,
                forceThumbnails: false,
                cancellationToken,
                skipTmdbArtwork: true);
            StatusMessage = "本地扫描完成，未刮削条目已显示；TMDB API 连通后可重新同步刮削。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "已停止后台扫描任务。";
        }
        catch
        {
            StatusMessage = "本地扫描媒体源时发生异常。";
        }
        finally
        {
            startupScanInProgress = false;
            CompleteLibraryAutomation(automationCancellationTokenSource);
            OnCommandStateChanged();
        }
    }

    private void OpenTmdbSettingsFromStartupAlert()
    {
        IsStartupTmdbAlertOpen = false;
        IsSearchPopupOpen = false;
        IsSortPopupOpen = false;
        IsSourcePopupOpen = false;
        IsSettingsPopupOpen = true;
        StatusMessage = "请在设置中填写 TMDB API。";
    }

    private void CloseStartupTmdbAlert()
    {
        IsStartupTmdbAlertOpen = false;
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

        var dockerPlaybackStartPosition = await TryRefreshOmniPlayDockerProgressAsync(video);
        var effectivePlaybackPath = await ResolveEffectivePlaybackPathAsync(video);
        if (string.IsNullOrWhiteSpace(effectivePlaybackPath))
        {
            StatusMessage = $"无法获取播放地址：{video.FileName}";
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
        var playbackStartPosition = nextPlaybackStartPositionOverride
            ?? dockerPlaybackStartPosition
            ?? GetPlaybackStartPosition(video);
        nextPlaybackStartPositionOverride = null;
        ApplyPlaybackPreferencesToPlayer();

        IsPlayerOverlayOpen = true;
        PendingPlaybackFilePath = string.Empty;
        PendingPlaybackDisplayPath = string.Empty;
        PendingPlaybackMediaFileName = string.Empty;
        PendingPlaybackStartPositionSeconds = playbackStartPosition ?? 0;
        PendingPlaybackFilePath = effectivePlaybackPath;
        PendingPlaybackDisplayPath = video.AbsolutePath;
        PendingPlaybackMediaFileName = video.FileName;
        currentPlaybackMode = PlaybackMode.Overlay;
        currentPlayingVideoId = video.Id;
        suppressedPlaybackPersistenceVideoId = null;
        ResetAutoAdvanceState();
        ResetPlaybackProgressSyncState();
        UpdatePlayerNextEpisodeAction();
        NotifyPlayerTimelineChanged();
        StatusMessage = playbackStartPosition.HasValue
            ? $"\u6B63\u5728\u7EE7\u7EED\u64AD\u653E\uFF1A{video.FileName}"
            : $"\u6B63\u5728\u51C6\u5907\u64AD\u653E\uFF1A{video.FileName}";
        OnCommandStateChanged();
    }

    private async Task PlayVideoWithStartPositionAsync(LibraryVideoItem video, double? startPositionSeconds)
    {
        nextPlaybackStartPositionOverride = startPositionSeconds;
        try
        {
            await PlayVideoAsync(video);
        }
        finally
        {
            nextPlaybackStartPositionOverride = null;
        }
    }

    private async Task<string?> ResolveEffectivePlaybackPathAsync(LibraryVideoItem video)
    {
        if (!string.Equals(video.SourceProtocolType, "omniplay-docker", StringComparison.OrdinalIgnoreCase))
        {
            return video.EffectivePlaybackPath;
        }

        if (omniPlayDockerClient is null || !TryResolveOmniPlayDockerRemoteFileId(video.Id, video.RelativePath, out var remoteFileId))
        {
            return video.EffectivePlaybackPath;
        }

        try
        {
            return await omniPlayDockerClient.GetPlaybackUrlAsync(
                video.SourceBasePath,
                video.SourceAuthConfig,
                remoteFileId);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            StatusMessage = $"OmniPlay Docker 播放地址获取失败：{ex.Message}";
            return null;
        }
    }

    private async Task<double?> TryRefreshOmniPlayDockerProgressAsync(LibraryVideoItem video)
    {
        if (!string.Equals(video.SourceProtocolType, "omniplay-docker", StringComparison.OrdinalIgnoreCase) ||
            omniPlayDockerClient is null ||
            !TryResolveOmniPlayDockerRemoteFileId(video.Id, video.RelativePath, out var remoteFileId))
        {
            return null;
        }

        try
        {
            var remote = await omniPlayDockerClient.GetPlaybackProgressAsync(
                video.SourceBasePath,
                video.SourceAuthConfig,
                remoteFileId);
            if (remote is null)
            {
                return null;
            }

            var duration = Math.Max(video.Duration, remote.DurationSeconds);
            await videoFileRepository.UpdatePlaybackStateAsync(
                video.Id,
                Math.Max(remote.PositionSeconds, 0),
                duration > 0 ? duration : null);

            if (remote.IsWatched || remote.PositionSeconds <= 5)
            {
                return null;
            }

            if (duration > 0 && remote.PositionSeconds >= duration - 5)
            {
                return null;
            }

            return Math.Min(remote.PositionSeconds, duration > 0 ? Math.Max(duration - 5, 0) : remote.PositionSeconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return null;
        }
    }

    private static bool TryResolveOmniPlayDockerRemoteFileId(string localFileId, string relativePath, out string remoteFileId)
    {
        remoteFileId = string.Empty;
        var idParts = localFileId.Split(':', 3);
        if (idParts.Length == 3 &&
            string.Equals(idParts[0], "omniplay-docker", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(idParts[2]))
        {
            remoteFileId = Uri.UnescapeDataString(idParts[2]);
            return true;
        }

        var parts = relativePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 5 &&
            string.Equals(parts[0], "api", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(parts[1], "playback", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(parts[2], "files", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(parts[4], "stream", StringComparison.OrdinalIgnoreCase))
        {
            remoteFileId = Uri.UnescapeDataString(parts[3]);
            return true;
        }

        return false;
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
        var effectivePlaybackPath = await ResolveEffectivePlaybackPathAsync(video);
        if (string.IsNullOrWhiteSpace(effectivePlaybackPath))
        {
            StatusMessage = $"无法获取独立播放地址：{video.FileName}";
            return;
        }
        ApplyPlaybackPreferencesToPlayer();

        var opened = await playbackLauncher.OpenAsync(
            new PlaybackOpenRequest(effectivePlaybackPath, video.AbsolutePath, video.FileName),
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
        NotifyPlayerTimelineChanged();
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

    private void ToggleHomeOfflineCacheMode()
    {
        IsHomeOfflineCacheModeActive = !IsHomeOfflineCacheModeActive;
        IsOfflineCacheModeActive = IsHomeOfflineCacheModeActive;
        IsSearchPopupOpen = false;
        IsSortPopupOpen = false;
        IsSourcePopupOpen = false;
        IsSettingsPopupOpen = false;
        RefreshOfflineCachePresentation();
        StatusMessage = IsHomeOfflineCacheModeActive ? "首页已进入离线缓存模式。" : "首页已退出离线缓存模式。";
    }

    private void ToggleDetailOfflineCacheMode()
    {
        IsDetailOfflineCacheModeActive = !IsDetailOfflineCacheModeActive;
        IsSearchPopupOpen = false;
        IsSortPopupOpen = false;
        IsSourcePopupOpen = false;
        IsSettingsPopupOpen = false;
        RefreshOfflineCachePresentation();
        StatusMessage = IsDetailOfflineCacheModeActive ? "详情页已进入离线缓存模式。" : "详情页已退出离线缓存模式。";
    }

    private async Task ChooseOfflineCacheDirectoryAsync()
    {
        var selectedPath = await folderPickerService.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            StatusMessage = "已取消选择离线缓存目录。";
            return;
        }

        Directory.CreateDirectory(selectedPath);
        Settings.OfflineCacheDirectory = selectedPath;
        await Settings.SaveCommand.ExecuteAsync(null);
        RefreshOfflineCachePresentation();
        StatusMessage = $"已设置离线缓存目录：{selectedPath}";
    }

    private async Task CachePosterItemAsync(LibraryPosterItem? item)
    {
        if (item is null)
        {
            return;
        }

        var files = await LoadPosterTargetFilesAsync(item);
        await StartOfflineCacheAsync(files, item.Id, item.Title);
    }

    private Task CacheDetailPosterAsync()
    {
        return StartOfflineCacheAsync(allDetailFiles, CurrentDetailOfflineGroupId(), DetailTitle);
    }

    private Task CacheSelectedSeasonAsync()
    {
        var files = allDetailFiles.Where(file => !file.IsTvEpisode || file.SeasonNumber == SelectedSeason).ToList();
        var seasonTitle = SelectedSeason == 0 ? $"{DetailTitle} 特别篇" : $"{DetailTitle} 第 {SelectedSeason} 季";
        return StartOfflineCacheAsync(files, null, seasonTitle);
    }

    private Task CacheDetailEpisodeAsync(LibraryVideoItem? video)
    {
        return video is null
            ? Task.CompletedTask
            : StartOfflineCacheAsync(new[] { video }, null, video.FileName);
    }

    private async Task StartOfflineCacheAsync(
        IEnumerable<LibraryVideoItem> requestedFiles,
        string? posterGroupId,
        string groupTitle)
    {
        var cacheDirectory = ResolveOfflineCacheDirectory();
        if (string.IsNullOrWhiteSpace(cacheDirectory))
        {
            ShowOfflineCacheAlert("离线缓存", "请先在设置中选择离线缓存保存位置。");
            return;
        }

        Directory.CreateDirectory(cacheDirectory);
        var uniqueFiles = requestedFiles
            .Where(file => !string.IsNullOrWhiteSpace(file.Id))
            .GroupBy(file => file.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (uniqueFiles.Count == 0)
        {
            StatusMessage = "所选内容没有可缓存的视频文件。";
            return;
        }

        var plans = new List<OfflineCacheFilePlan>();
        var unsupportedCount = 0;
        foreach (var file in uniqueFiles)
        {
            var destinationPath = GetOfflineCachePath(file, cacheDirectory);
            if (File.Exists(destinationPath))
            {
                offlineCachedFileIds.Add(file.Id);
                continue;
            }

            var sourcePath = ResolveOfflineCacheSourcePath(file);
            var remoteUri = ResolveOfflineCacheRemoteUri(file, out var remoteAuthorization);
            if ((string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) && remoteUri is null)
            {
                offlineUnavailableFileIds.Add(file.Id);
                unsupportedCount++;
                continue;
            }

            var fileSize = sourcePath is not null && File.Exists(sourcePath)
                ? new FileInfo(sourcePath).Length
                : await GetRemoteContentLengthAsync(remoteUri!, remoteAuthorization);
            plans.Add(new OfflineCacheFilePlan(file, sourcePath, remoteUri, remoteAuthorization, destinationPath, fileSize));
        }

        if (plans.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(posterGroupId) && unsupportedCount > 0)
            {
                offlineUnavailablePosterIds.Add(posterGroupId);
            }

            RefreshOfflineCachePresentation();
            var message = unsupportedCount > 0
                ? "远程源或不可访问文件暂不支持离线缓存。"
                : "所选内容已在本地缓存中。";
            StatusMessage = message;
            return;
        }

        var totalBytes = plans.Sum(static plan => Math.Max(0, plan.FileSize));
        var availableBytes = GetAvailableFreeBytes(cacheDirectory);
        if (availableBytes < totalBytes + MinimumFreeSpaceAfterOfflineCache)
        {
            ShowOfflineCacheAlert(
                "硬盘存储空间不够",
                $"《{groupTitle}》需要 {FormatBytes(totalBytes)}，当前缓存磁盘可用 {FormatBytes(availableBytes)}，空间不足，已取消离线缓存。");
            return;
        }

        if (!string.IsNullOrWhiteSpace(posterGroupId))
        {
            offlinePosterProgressByItemId[posterGroupId] = 0.01;
        }

        foreach (var plan in plans)
        {
            offlineCacheProgressByFileId[plan.File.Id] = 0.01;
        }

        RefreshOfflineCachePresentation();
        StatusMessage = $"正在离线缓存：{groupTitle}";

        var copiedBytesForGroup = 0L;
        try
        {
            foreach (var plan in plans)
            {
                await CopyOfflineCacheFileAsync(
                    plan,
                    totalBytes,
                    () => copiedBytesForGroup,
                    copied => copiedBytesForGroup += copied,
                    posterGroupId);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            foreach (var plan in plans)
            {
                offlineCacheProgressByFileId.Remove(plan.File.Id);
            }

            if (!string.IsNullOrWhiteSpace(posterGroupId))
            {
                offlinePosterProgressByItemId.Remove(posterGroupId);
            }

            RefreshOfflineCachePresentation();
            ShowOfflineCacheAlert("离线缓存失败", ex.Message);
            return;
        }

        if (!string.IsNullOrWhiteSpace(posterGroupId))
        {
            offlinePosterProgressByItemId.Remove(posterGroupId);
            offlineCachedPosterIds.Add(posterGroupId);
        }

        RefreshOfflineCachePresentation();
        StatusMessage = unsupportedCount > 0
            ? $"《{groupTitle}》已缓存完成，部分远程源或不可访问文件已跳过。"
            : $"《{groupTitle}》已缓存完成。";
    }

    private async Task CopyOfflineCacheFileAsync(
        OfflineCacheFilePlan plan,
        long groupTotalBytes,
        Func<long> groupCopiedBytes,
        Action<long> addGroupCopiedBytes,
        string? posterGroupId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(plan.DestinationPath) ?? ResolveOfflineCacheDirectory() ?? string.Empty);
        var tempPath = $"{plan.DestinationPath}.tmp";
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        var buffer = new byte[1024 * 1024];
        long copiedForFile = 0;
        await using var destination = File.Create(tempPath);
        if (plan.RemoteUri is not null)
        {
            await DownloadRemoteOfflineCacheFileAsync(plan, destination, groupTotalBytes, groupCopiedBytes, posterGroupId);
            copiedForFile = plan.FileSize;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(plan.SourcePath))
            {
                throw new FileNotFoundException("源文件不存在。");
            }

            await using var source = File.OpenRead(plan.SourcePath);
            while (true)
            {
                var read = await source.ReadAsync(buffer);
                if (read <= 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read));
                copiedForFile += read;
                var fileProgress = plan.FileSize > 0 ? Math.Clamp((double)copiedForFile / plan.FileSize, 0.01, 0.999) : 0.5;
                offlineCacheProgressByFileId[plan.File.Id] = fileProgress;

                if (!string.IsNullOrWhiteSpace(posterGroupId) && groupTotalBytes > 0)
                {
                    var groupProgress = Math.Clamp((double)(groupCopiedBytes() + copiedForFile) / groupTotalBytes, 0.01, 0.999);
                    offlinePosterProgressByItemId[posterGroupId] = groupProgress;
                }

                RefreshOfflineCachePresentation();
            }
        }

        await destination.FlushAsync();
        if (File.Exists(plan.DestinationPath))
        {
            File.Delete(plan.DestinationPath);
        }

        File.Move(tempPath, plan.DestinationPath);
        offlineCacheProgressByFileId.Remove(plan.File.Id);
        offlineCachedFileIds.Add(plan.File.Id);
        addGroupCopiedBytes(copiedForFile);
        RefreshOfflineCachePresentation();
    }

    private async Task DownloadRemoteOfflineCacheFileAsync(
        OfflineCacheFilePlan plan,
        FileStream destination,
        long groupTotalBytes,
        Func<long> groupCopiedBytes,
        string? posterGroupId)
    {
        if (plan.RemoteUri is null)
        {
            return;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromHours(6) };
        using var request = new HttpRequestMessage(HttpMethod.Get, plan.RemoteUri);
        if (!string.IsNullOrWhiteSpace(plan.RemoteAuthorization))
        {
            request.Headers.TryAddWithoutValidation("Authorization", plan.RemoteAuthorization);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var expectedBytes = response.Content.Headers.ContentLength ?? plan.FileSize;
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[1024 * 1024];
        long copiedForFile = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read <= 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read));
            copiedForFile += read;
            var fileProgress = expectedBytes > 0 ? Math.Clamp((double)copiedForFile / expectedBytes, 0.01, 0.999) : 0.5;
            offlineCacheProgressByFileId[plan.File.Id] = fileProgress;

            if (!string.IsNullOrWhiteSpace(posterGroupId) && groupTotalBytes > 0)
            {
                var groupProgress = Math.Clamp((double)(groupCopiedBytes() + copiedForFile) / groupTotalBytes, 0.01, 0.999);
                offlinePosterProgressByItemId[posterGroupId] = groupProgress;
            }

            RefreshOfflineCachePresentation();
        }
    }

    private string? ResolveOfflineCacheDirectory()
    {
        var configuredPath = Settings.OfflineCacheDirectory.Trim();
        return string.IsNullOrWhiteSpace(configuredPath) ? null : configuredPath;
    }

    private static string? ResolveOfflineCacheSourcePath(LibraryVideoItem file)
    {
        if (!string.IsNullOrWhiteSpace(file.AbsolutePath) && File.Exists(file.AbsolutePath))
        {
            return file.AbsolutePath;
        }

        if (!string.IsNullOrWhiteSpace(file.PlaybackPath) &&
            !Uri.TryCreate(file.PlaybackPath, UriKind.Absolute, out var playbackUri) &&
            File.Exists(file.PlaybackPath))
        {
            return file.PlaybackPath;
        }

        return null;
    }

    private static Uri? ResolveOfflineCacheRemoteUri(LibraryVideoItem file, out string? authorization)
    {
        authorization = null;
        if (!string.Equals(file.SourceProtocolType, "webdav", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var candidate = !string.IsNullOrWhiteSpace(file.PlaybackPath) ? file.PlaybackPath : file.AbsolutePath;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            var rawUserInfo = Uri.UnescapeDataString(uri.UserInfo);
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawUserInfo));
            authorization = $"Basic {encoded}";
            var builder = new UriBuilder(uri)
            {
                UserName = string.Empty,
                Password = string.Empty
            };
            uri = builder.Uri;
        }
        else if (MediaSourceAuthConfigSerializer.DeserializeWebDav(file.SourceAuthConfig) is { } auth &&
                 !string.IsNullOrWhiteSpace(auth.Username))
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{auth.Username}:{auth.Password}"));
            authorization = $"Basic {encoded}";
        }

        return uri;
    }

    private static async Task<long> GetRemoteContentLengthAsync(Uri remoteUri, string? authorization)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var request = new HttpRequestMessage(HttpMethod.Head, remoteUri);
            if (!string.IsNullOrWhiteSpace(authorization))
            {
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                return 0;
            }

            return response.Content.Headers.ContentLength ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private string? CurrentDetailOfflineGroupId()
    {
        if (currentDetailMovieId.HasValue)
        {
            return $"movie-{currentDetailMovieId.Value}";
        }

        return currentDetailTvShowId.HasValue ? $"tv-{currentDetailTvShowId.Value}" : null;
    }

    private string GetOfflineCachePath(LibraryVideoItem file, string cacheDirectory)
    {
        return Path.Combine(
            cacheDirectory,
            SanitizePathSegment(file.Id),
            SanitizePathSegment(string.IsNullOrWhiteSpace(file.FileName) ? $"{file.Id}.mp4" : file.FileName));
    }

    private bool IsOfflineCached(LibraryVideoItem file, out string? cachePath)
    {
        var cacheDirectory = ResolveOfflineCacheDirectory();
        if (string.IsNullOrWhiteSpace(cacheDirectory))
        {
            cachePath = null;
            return offlineCachedFileIds.Contains(file.Id);
        }

        cachePath = GetOfflineCachePath(file, cacheDirectory);
        var exists = File.Exists(cachePath);
        if (exists)
        {
            offlineCachedFileIds.Add(file.Id);
        }

        return exists || offlineCachedFileIds.Contains(file.Id);
    }

    private LibraryVideoItem ApplyOfflineCacheState(LibraryVideoItem file)
    {
        var isCached = IsOfflineCached(file, out var cachePath);
        var isDownloading = offlineCacheProgressByFileId.TryGetValue(file.Id, out var progress);
        return CopyVideoItem(
            file,
            isCached && cachePath is not null ? cachePath : file.OfflineCachePath,
            isDownloading,
            isCached,
            offlineUnavailableFileIds.Contains(file.Id),
            isDownloading ? progress : (isCached ? 1 : 0));
    }

    private LibraryPosterItem ApplyOfflineCacheState(LibraryPosterItem item)
    {
        var isDownloading = offlinePosterProgressByItemId.TryGetValue(item.Id, out var progress);
        return CopyPosterItem(
            item,
            item.WatchState,
            item.IsContinuing,
            isDownloading,
            offlineCachedPosterIds.Contains(item.Id),
            offlineUnavailablePosterIds.Contains(item.Id),
            isDownloading ? progress : (offlineCachedPosterIds.Contains(item.Id) ? 1 : 0));
    }

    private void RefreshOfflineCachePresentation()
    {
        ApplyFilters();
        if (IsDetailOpen)
        {
            var selectedId = DetailPrimaryFile?.Id;
            if (!string.IsNullOrWhiteSpace(selectedId))
            {
                preferredDetailVideoId = selectedId;
            }

            ApplyDetailFilter();
        }
        else
        {
            UpdateSelectedSeasonOfflineProgress();
        }
    }

    private void UpdateSelectedSeasonOfflineProgress()
    {
        var selectedFiles = allDetailFiles
            .Where(file => !IsDetailSeries || file.SeasonNumber == SelectedSeason)
            .ToList();
        if (selectedFiles.Count == 0 || !selectedFiles.Any(file => offlineCacheProgressByFileId.ContainsKey(file.Id)))
        {
            ShowSelectedSeasonOfflineCacheProgress = false;
            SelectedSeasonOfflineCacheProgress = 0;
            UpdateDetailPosterOfflineState();
            return;
        }

        var total = selectedFiles.Sum(file =>
        {
            if (IsOfflineCached(file, out _))
            {
                return 1.0;
            }

            return offlineCacheProgressByFileId.TryGetValue(file.Id, out var progress)
                ? Math.Clamp(progress, 0, 1)
                : 0;
        });
        SelectedSeasonOfflineCacheProgress = total / selectedFiles.Count;
        ShowSelectedSeasonOfflineCacheProgress = true;
        UpdateDetailPosterOfflineState();
    }

    private void UpdateDetailPosterOfflineState()
    {
        var groupId = CurrentDetailOfflineGroupId();
        if (string.IsNullOrWhiteSpace(groupId))
        {
            ShowDetailPosterOfflineCacheProgress = false;
            DetailPosterOfflineCacheProgress = 0;
            DetailPosterOfflineCacheGlyph = "\u2193";
            DetailPosterOfflineCacheTip = "\u79BB\u7EBF\u7F13\u5B58\u6574\u90E8\u5F71\u7247\u6216\u5267\u96C6";
            return;
        }

        if (offlinePosterProgressByItemId.TryGetValue(groupId, out var progress))
        {
            ShowDetailPosterOfflineCacheProgress = true;
            DetailPosterOfflineCacheProgress = Math.Clamp(progress, 0, 1);
            DetailPosterOfflineCacheTip = "\u6B63\u5728\u79BB\u7EBF\u7F13\u5B58";
            return;
        }

        ShowDetailPosterOfflineCacheProgress = false;
        DetailPosterOfflineCacheProgress = offlineCachedPosterIds.Contains(groupId) ? 1 : 0;
        DetailPosterOfflineCacheGlyph = offlineCachedPosterIds.Contains(groupId) ? "\u2713" : "\u2193";
        DetailPosterOfflineCacheTip = offlineCachedPosterIds.Contains(groupId)
            ? "\u5DF2\u7F13\u5B58\u5230\u672C\u5730"
            : "\u79BB\u7EBF\u7F13\u5B58\u6574\u90E8\u5F71\u7247\u6216\u5267\u96C6";
    }

    private void ShowOfflineCacheAlert(string title, string message)
    {
        OfflineCacheAlertTitle = title;
        OfflineCacheAlertMessage = message;
        IsOfflineCacheAlertOpen = true;
        StatusMessage = title;
        CloseOfflineCacheAlertCommand.NotifyCanExecuteChanged();
    }

    private void CloseOfflineCacheAlert()
    {
        IsOfflineCacheAlertOpen = false;
        OfflineCacheAlertTitle = string.Empty;
        OfflineCacheAlertMessage = string.Empty;
        CloseOfflineCacheAlertCommand.NotifyCanExecuteChanged();
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
        PosterDoubanSubject = string.Empty;
        PosterDoubanStatus = item.DoubanRating.HasValue
            ? $"已绑定豆瓣评分 {item.DoubanRating.Value:F1}"
            : "可填写豆瓣 subject ID 或链接进行手动绑定。";
        IsPosterScrapePanelOpen = false;
        ReplaceItems(PosterMetadataCandidates, []);
        IsPosterEditPanelOpen = true;
        OnCommandStateChanged();
    }

    private void ClosePosterEdit()
    {
        IsPosterEditPanelOpen = false;
        PosterSourceFileText = string.Empty;
        PosterDoubanSubject = string.Empty;
        PosterDoubanStatus = string.Empty;
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
            await RefreshDetailFilesAsync(SelectedSeason);
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
        if (MediaSourcePathResolver.IsMediaServerPlaybackEndpointPath(file.RelativePath))
        {
            return TryResolveDecodedDisplayPath(file.FileName)
                   ?? TryResolveDecodedDisplayPath(file.MetadataPath)
                   ?? file.FileName;
        }

        return TryResolveDecodedDisplayPath(file.RelativePath)
               ?? TryResolveDecodedDisplayPath(file.MetadataPath)
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

    private async Task BindPosterDoubanAsync()
    {
        if (posterMetadataTarget is null || doubanMetadataService is null)
        {
            PosterDoubanStatus = "当前版本未启用豆瓣绑定服务。";
            return;
        }

        var subject = PosterDoubanSubject.Trim();
        if (string.IsNullOrWhiteSpace(subject))
        {
            PosterDoubanStatus = "请填写豆瓣 subject ID 或链接。";
            return;
        }

        var isMovie = posterMetadataTarget.MovieId.HasValue;
        var entityId = isMovie ? posterMetadataTarget.MovieId!.Value : posterMetadataTarget.TvShowId!.Value;
        IsBusy = true;
        OnCommandStateChanged();
        try
        {
            PosterDoubanStatus = "正在绑定豆瓣条目...";
            var metadata = await doubanMetadataService.BindAsync(
                isMovie ? "movie" : "tv",
                entityId,
                subject,
                PosterEditTitle.Trim().Length == 0 ? posterMetadataTarget.Title : PosterEditTitle.Trim(),
                PosterEditDate);
            await ReloadLibraryAsync();
            await RefreshOpenDetailIfNeededAsync();
            var dockerSyncMessage = metadata is null
                ? null
                : await TrySyncDoubanMetadataToOmniPlayDockerAsync(posterMetadataTarget, metadata);
            PosterDoubanStatus = metadata?.Rating.HasValue == true
                ? $"豆瓣绑定成功：{metadata.Title} {metadata.Rating.Value:F1}"
                : "豆瓣条目已保存；本次未获取到评分。";
            if (!string.IsNullOrWhiteSpace(dockerSyncMessage))
            {
                PosterDoubanStatus = $"{PosterDoubanStatus} {dockerSyncMessage}";
            }
            StatusMessage = PosterDoubanStatus;
        }
        catch (ArgumentException ex)
        {
            PosterDoubanStatus = ex.Message;
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private async Task<string?> TrySyncDoubanMetadataToOmniPlayDockerAsync(
        LibraryPosterItem item,
        DoubanMetadata metadata)
    {
        if (omniPlayDockerClient is null)
        {
            return null;
        }

        var files = item.MovieId.HasValue
            ? await videoFileRepository.GetByMovieAsync(item.MovieId.Value)
            : item.TvShowId.HasValue
                ? await videoFileRepository.GetByTvShowAsync(item.TvShowId.Value)
                : [];
        var dockerFiles = files
            .Where(static file => string.Equals(file.SourceProtocolType, "omniplay-docker", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (dockerFiles.Count == 0)
        {
            return null;
        }

        var remoteFileIds = dockerFiles
            .Select(file => TryResolveOmniPlayDockerRemoteFileId(file.Id, file.RelativePath, out var remoteFileId) ? remoteFileId : null)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (remoteFileIds.Count == 0)
        {
            return null;
        }

        var firstFile = dockerFiles[0];
        var source = new MediaSource
        {
            ProtocolType = "omniplay-docker",
            BaseUrl = firstFile.SourceBasePath,
            AuthConfig = firstFile.SourceAuthConfig
        };

        try
        {
            var remoteItems = await omniPlayDockerClient.GetLibraryItemsAsync(source);
            foreach (var remoteItem in remoteItems)
            {
                var detail = await omniPlayDockerClient.GetLibraryDetailAsync(source, remoteItem.Id);
                if (detail is null)
                {
                    continue;
                }

                var detailFileIds = GetOmniPlayDockerRemoteFileIds(detail);
                if (detailFileIds.Overlaps(remoteFileIds))
                {
                    await omniPlayDockerClient.ImportDoubanMetadataAsync(source, remoteItem.Id, metadata);
                    return "已同步到 OmniPlay Docker。";
                }
            }

            return "未在 OmniPlay Docker 中找到对应条目，未同步到 Docker。";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return $"Docker 同步失败：{ex.Message}";
        }
    }

    private static HashSet<string> GetOmniPlayDockerRemoteFileIds(OmniPlayDockerLibraryDetail detail)
    {
        var fileIds = detail.VideoFiles.Select(static file => file.Id)
            .Concat(detail.Seasons.SelectMany(static season => season.Episodes)
                .SelectMany(static episode => episode.VideoFiles is { Count: > 0 }
                    ? episode.VideoFiles
                    : episode.VideoFile is { } file ? [file] : [])
                .Select(static file => file.Id));
        return fileIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
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
            return ExtractPreferredPosterTitle(candidate);
        }

        foreach (Match match in Regex.Matches(candidate, $@"(?<!\d){Regex.Escape(year)}(?!\d)"))
        {
            var prefix = NormalizePosterTitleCandidate(candidate[..match.Index]);
            var preferredPrefix = ExtractPreferredPosterTitle(prefix);
            if (!string.IsNullOrWhiteSpace(preferredPrefix))
            {
                return preferredPrefix;
            }
        }

        return ExtractPreferredPosterTitle(candidate);
    }

    private static string? ExtractPreferredPosterTitle(string value)
    {
        if (!HasPosterTitleText(value))
        {
            return null;
        }

        var chineseTitle = ExtractPosterChineseTitle(value);
        return string.IsNullOrWhiteSpace(chineseTitle) ? value.Trim() : chineseTitle;
    }

    private static string? ExtractPosterChineseTitle(string value)
    {
        List<string> current = [];
        var tokens = Regex.Split(value, @"\s+")
            .Select(static token => token.Trim(' ', '.', '-', '_', '/', '\\', '(', ')', '[', ']', '{', '}', '（', '）', '【', '】'))
            .Where(static token => token.Length > 0);

        foreach (var token in tokens)
        {
            if (token.Any(IsCjkCharacter))
            {
                current.Add(token);
                continue;
            }

            if (current.Count > 0 &&
                Regex.IsMatch(token, @"^\d{1,3}$") &&
                !Regex.IsMatch(token, @"^(19|20)\d{2}$"))
            {
                current.Add(token);
                continue;
            }

            if (current.Count > 0)
            {
                break;
            }
        }

        return current.Count == 0 ? null : string.Join(' ', current).Trim();
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
            var candidate = parts[bdmvIndex - 1];
            return IsPosterReleaseGroupWrapperFolder(candidate, bdmvIndex > 1 ? parts[bdmvIndex - 2] : string.Empty)
                ? parts[bdmvIndex - 2]
                : candidate;
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
               Regex.IsMatch(lower, @"^(4k|uhd|hdtv|uhdtv|hdr|hlg|dv|atmos|ddp\d*(\.\d+)?|aac\d*(\.\d+)?|ac3|eac3|dts|dts[- ]?hd|dts[- ]?x|dtsx|truehd|flac|lpcm|avc|hevc)$") ||
               Regex.IsMatch(lower, @"^(x|h)?26[45]$") ||
               Regex.IsMatch(lower, @"^(web|web-?dl|webdl|webrip|bluray|bdrip|remux|blu-ray)$") ||
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
        normalized = Regex.Replace(normalized, @"(?i)\b(?:1080p|2160p|720p|480p|4k|uhd|hdtv|uhdtv|blu[- ]?ray|bluray|web[- ]?dl|webdl|webrip|remux|x264|x265|h\.?264|h\.?265|hevc|hdr|hlg|dv|ddp\d*(?:\.\d+)?|dts(?:-hd|-x)?|dtsx|aac|atmos|avc|flac|lpcm|truehd|complete|nf|cmct|hdsky|adweb)\b", " ");
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

    private static bool IsPosterReleaseGroupWrapperFolder(string input, string ancestor)
    {
        if (string.IsNullOrWhiteSpace(input) ||
            string.IsNullOrWhiteSpace(ancestor) ||
            input.Any(IsCjkCharacter) ||
            ChoosePosterReleaseYear(input) is not null ||
            !HasLikelyPosterTitleSignal(ancestor))
        {
            return false;
        }

        var tokens = Regex
            .Split(input.Trim(), @"[\s._\-@#]+")
            .Where(static token => !string.IsNullOrWhiteSpace(token))
            .ToArray();
        return tokens.Length >= 2 &&
               tokens.All(static token => Regex.IsMatch(token, @"^[A-Z0-9]{2,10}$", RegexOptions.CultureInvariant));
    }

    private static bool HasLikelyPosterTitleSignal(string value)
    {
        return value.Any(IsCjkCharacter) || ChoosePosterReleaseYear(value) is not null;
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

    private static string FormatCombinedRatingText(double? tmdbRating, double? doubanRating)
    {
        var parts = new List<string>();
        if (tmdbRating.HasValue)
        {
            parts.Add($"TMDB {tmdbRating.Value:F1}");
        }

        if (doubanRating.HasValue)
        {
            parts.Add($"豆瓣 {doubanRating.Value:F1}");
        }

        return parts.Count == 0 ? "评分待补充" : string.Join("  ", parts);
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
        PendingPlaybackMediaFileName = string.Empty;
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
        NotifyPlayerTimelineChanged();
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

    private void ApplySavedLibraryViewPreferences()
    {
        suppressSortPreferencePersistence = true;
        try
        {
            var option = Settings.LibrarySortOption.Trim().ToLowerInvariant() switch
            {
                LibraryViewSettings.SortOptionYear => LibrarySortOption.Year,
                LibraryViewSettings.SortOptionRating => LibrarySortOption.Rating,
                _ => LibrarySortOption.Title
            };

            SelectedSortOption = option;
            SelectedSortOptionItem = SortOptions.FirstOrDefault(item => item.Value == option) ?? SortOptions[0];
            IsSortDescending = Settings.LibrarySortDescending;
            RefreshSortOptionSelectionStates();
        }
        finally
        {
            suppressSortPreferencePersistence = false;
        }
    }

    private void RefreshSortOptionSelectionStates()
    {
        foreach (var option in SortOptions)
        {
            option.IsSelected = option.Value == SelectedSortOption;
        }
    }

    private void PersistLibraryViewPreferencesIfNeeded()
    {
        if (suppressSortPreferencePersistence)
        {
            return;
        }

        _ = PersistLibraryViewPreferencesAsync();
    }

    private async Task PersistLibraryViewPreferencesAsync()
    {
        try
        {
            await Settings.SaveLibraryViewPreferencesAsync(ToLibrarySortSetting(SelectedSortOption), IsSortDescending);
        }
        catch
        {
            // Sorting should stay responsive even if the settings file cannot be written.
        }
    }

    private static string ToLibrarySortSetting(LibrarySortOption option)
    {
        return option switch
        {
            LibrarySortOption.Year => LibraryViewSettings.SortOptionYear,
            LibrarySortOption.Rating => LibraryViewSettings.SortOptionRating,
            _ => LibraryViewSettings.SortOptionTitle
        };
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

    private void NotifyNetworkShareFolderStateChanged()
    {
        OnPropertyChanged(nameof(HasNetworkShareFolders));
        OnPropertyChanged(nameof(IsNetworkCredentialFormVisible));
        OnPropertyChanged(nameof(StarredNetworkShareFolderCount));
        OnPropertyChanged(nameof(HasStarredNetworkShareFolders));
        OnPropertyChanged(nameof(MountStarredNetworkFoldersActionText));
        MountStarredNetworkFoldersCommand.NotifyCanExecuteChanged();
    }

    private void ToggleNetworkPasswordVisibility()
    {
        IsNetworkPasswordVisible = !IsNetworkPasswordVisible;
    }

    private void ToggleMediaServerTokenVisibility()
    {
        IsMediaServerTokenVisible = !IsMediaServerTokenVisible;
    }

    private void AddPendingExternalAddress()
    {
        PendingExternalAddresses.Add(new MediaSourceExternalAddress());
    }

    private void RemovePendingExternalAddress(MediaSourceExternalAddress? address)
    {
        if (address is not null)
        {
            PendingExternalAddresses.Remove(address);
        }
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
            NetworkSourceStatus = "正在预扫描 WebDAV、SMB、Plex、Emby 和 Jellyfin...";
            var sources = await networkShareDiscoveryService.DiscoverAsync();
            ReplaceItems(DiscoveredNetworkSources, sources);
            OnPropertyChanged(nameof(HasDiscoveredNetworkSources));
            NetworkSourceStatus = sources.Count == 0
                ? "未发现 SMB、WebDAV 或媒体服务器。"
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

        if (item.ProtocolKind is MediaSourceProtocol.Plex or MediaSourceProtocol.Emby or MediaSourceProtocol.Jellyfin or MediaSourceProtocol.OmniPlayDocker)
        {
            OpenDiscoveredMediaServer(item);
            return;
        }

        PendingNetworkProtocolType = item.ProtocolType;
        PendingNetworkBaseUrl = item.BaseUrl;
        PendingNetworkDisplayName = item.Name;
        PendingNetworkUsername = string.Empty;
        PendingNetworkPassword = string.Empty;
        PendingExternalAddresses.Clear();
        ApplySavedNetworkCredential(item.ProtocolKind, item.BaseUrl, allowBaseUrlPrefill: false);
        NetworkLoginTitle = $"登录 {item.ProtocolLabel}";
        IsSourcePopupOpen = false;
        IsMediaServerPanelOpen = false;
        ReplaceItems(NetworkShareFolders, []);
        NotifyNetworkShareFolderStateChanged();
        IsNetworkLoginPanelOpen = true;
        NetworkSourceStatus = $"正在准备登录：{item.Name}";
        OnCommandStateChanged();
    }

    private void OpenDiscoveredMediaServer(NetworkSourceDiscoveryItem item)
    {
        IsNetworkLoginPanelOpen = false;
        IsSourcePopupOpen = false;
        IsMediaServerPanelOpen = true;
        var option = MediaServerProtocolOptions.FirstOrDefault(option =>
            string.Equals(option.Value, item.ProtocolType, StringComparison.OrdinalIgnoreCase)) ?? MediaServerProtocolOptions[0];
        SelectedMediaServerProtocolOption = option;
        PendingMediaServerProtocolType = option.Value;
        PendingMediaServerName = item.Name;
        PendingMediaServerBaseUrl = item.BaseUrl;
        PendingMediaServerToken = string.Empty;
        PendingMediaServerUserId = string.Empty;
        MediaServerStatus = GetMediaServerConnectionHint(item.ProtocolType);
        NetworkSourceStatus = $"已选择 {item.ProtocolLabel}：{item.BaseUrl}";
        NotifyMediaServerProtocolDependentProperties();
        OnCommandStateChanged();
    }

    private void OpenManualNetworkLogin()
    {
        IsMediaServerPanelOpen = false;
        PendingNetworkProtocolType = "webdav";
        PendingNetworkBaseUrl = string.Empty;
        PendingNetworkDisplayName = string.Empty;
        PendingNetworkUsername = string.Empty;
        PendingNetworkPassword = string.Empty;
        PendingExternalAddresses.Clear();
        ApplyLatestSavedNetworkCredential();
        NetworkLoginTitle = "添加局域网媒体源";
        IsSourcePopupOpen = false;
        IsMediaServerPanelOpen = false;
        ReplaceItems(NetworkShareFolders, []);
        NotifyNetworkShareFolderStateChanged();
        IsNetworkLoginPanelOpen = true;
        NetworkSourceStatus = "输入 WebDAV 地址或 SMB 路径。";
        OnCommandStateChanged();
    }

    private void ApplySavedNetworkCredential(MediaSourceProtocol? protocol, string baseUrl, bool allowBaseUrlPrefill)
    {
        if (protocol is null ||
            protocol.Value != MediaSourceProtocol.WebDav && protocol.Value != MediaSourceProtocol.Smb)
        {
            return;
        }

        var credential = networkCredentialStore.FindBest(protocol.Value, baseUrl);
        if (credential is null)
        {
            return;
        }

        PendingNetworkUsername = credential.Username;
        PendingNetworkPassword = credential.Password;
        if (allowBaseUrlPrefill && string.IsNullOrWhiteSpace(PendingNetworkBaseUrl))
        {
            PendingNetworkProtocolType = credential.ProtocolType;
            PendingNetworkBaseUrl = credential.BaseUrl;
            PendingNetworkDisplayName = credential.BaseUrl;
        }
    }

    private void ApplyLatestSavedNetworkCredential()
    {
        var credential = networkCredentialStore.FindLatest();
        if (credential is null)
        {
            return;
        }

        PendingNetworkProtocolType = credential.ProtocolType;
        PendingNetworkBaseUrl = credential.BaseUrl;
        PendingNetworkDisplayName = credential.BaseUrl;
        PendingNetworkUsername = credential.Username;
        PendingNetworkPassword = credential.Password;
    }

    private void OpenMediaServerPanel()
    {
        IsNetworkLoginPanelOpen = false;
        IsSourcePopupOpen = false;
        IsMediaServerPanelOpen = true;
        if (SelectedMediaServerProtocolOption is null)
        {
            SelectedMediaServerProtocolOption = MediaServerProtocolOptions[0];
        }

        PendingMediaServerProtocolType = SelectedMediaServerProtocolOption.Value;
        ApplyMediaServerProtocolDefaults(PendingMediaServerProtocolType);
        MediaServerStatus = GetMediaServerConnectionHint(PendingMediaServerProtocolType);
        OnCommandStateChanged();
    }

    private void CloseMediaServerPanel()
    {
        ResetMediaServerForm(clearStatus: true);
        OnCommandStateChanged();
    }

    private async Task AddMediaServerSourceAsync()
    {
        var source = BuildPendingMediaServerSource();
        if (source is null)
        {
            return;
        }

        IsBusy = true;
        OnCommandStateChanged();

        try
        {
            if (source.ProtocolKind == MediaSourceProtocol.OmniPlayDocker)
            {
                var sourceId = await mediaSourceRepository.AddAsync(source);
                IsMediaServerPanelOpen = false;
                ResetMediaServerForm(clearStatus: true);
                await ReloadLibraryAsync();
                if (sourceId <= 0)
                {
                    StatusMessage = "已保存 OmniPlay Docker 媒体源。";
                    return;
                }

                var automationCancellationTokenSource = BeginLibraryAutomation();
                var cancellationToken = automationCancellationTokenSource.Token;
                IsBusy = false;
                OnCommandStateChanged();
                try
                {
                    StatusMessage = $"已添加 OmniPlay Docker：{source.Name}，正在同步媒体库...";
                    LastScanSummary = await RunLibraryScanAndArtworkAsync(
                        isExplicitRequest: true,
                        forceThumbnails: false,
                        cancellationToken,
                        [sourceId],
                        skipTmdbArtwork: true);
                    StatusMessage = $"已同步 OmniPlay Docker：{source.Name}";
                }
                finally
                {
                    CompleteLibraryAutomation(automationCancellationTokenSource);
                }

                return;
            }

            MediaServerStatus = $"正在预扫描 {source.ProtocolLabel} 共享文件夹...";
            StatusMessage = MediaServerStatus;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            IReadOnlyList<NetworkShareFolderItem> folders = mediaServerPreflightService is null
                ? []
                : await mediaServerPreflightService.ListLibrariesAsync(source, timeout.Token);

            ReplaceItems(NetworkShareFolders, folders);
            NotifyNetworkShareFolderStateChanged();
            PendingNetworkProtocolType = source.ProtocolType;
            PendingNetworkBaseUrl = source.BaseUrl;
            PendingNetworkDisplayName = source.Name;
            PendingNetworkUsername = string.Empty;
            PendingNetworkPassword = string.Empty;
            NetworkLoginTitle = $"选择 {source.ProtocolLabel} 共享文件夹";
            IsMediaServerPanelOpen = false;
            IsNetworkLoginPanelOpen = true;
            MediaServerStatus = folders.Count == 0
                ? "连接成功，但当前服务器没有可挂载的媒体库。"
                : $"预扫描成功：读取到 {folders.Count} 个媒体库，可给多个目标点星标，关闭列表时统一挂载。";
            NetworkSourceStatus = MediaServerStatus;
            StatusMessage = MediaServerStatus;
        }
        catch (OperationCanceledException)
        {
            MediaServerStatus = "媒体服务器预扫描超时。";
            StatusMessage = MediaServerStatus;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
        {
            MediaServerStatus = $"媒体服务器预扫描失败：{ex.Message}";
            StatusMessage = MediaServerStatus;
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private async Task AuthorizePlexAsync()
    {
        if (!PendingMediaServerUsesPlex || IsPlexAuthorizationInProgress)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(PendingMediaServerBaseUrl))
        {
            PendingMediaServerBaseUrl = DefaultMediaServerBaseUrl("plex");
        }

        IsPlexAuthorizationInProgress = true;
        MediaServerStatus = "正在创建 Plex 授权请求...";
        OnCommandStateChanged();

        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(130));
            var session = await PlexPinAuthClient.CreateAsync(cancellation.Token);
            Process.Start(new ProcessStartInfo
            {
                FileName = session.AuthorizationUrl,
                UseShellExecute = true
            });
            MediaServerStatus = $"已打开 Plex Link 页面，请确认 PIN：{session.Code}";
            var token = await PlexPinAuthClient.WaitForTokenAsync(session, cancellation.Token);
            PendingMediaServerToken = token;
            MediaServerStatus = "Plex 授权成功，已自动填入访问令牌。";
        }
        catch (OperationCanceledException)
        {
            MediaServerStatus = "Plex 授权超时，请重新点击登录。";
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            MediaServerStatus = $"Plex 授权失败：{ex.Message}";
        }
        finally
        {
            IsPlexAuthorizationInProgress = false;
            OnCommandStateChanged();
        }
    }

    private async Task PreScanMediaServerSourceAsync()
    {
        var source = BuildPendingMediaServerSource();
        if (source is null)
        {
            return;
        }

        IsBusy = true;
        OnCommandStateChanged();

        try
        {
            if (source.ProtocolKind == MediaSourceProtocol.OmniPlayDocker)
            {
                MediaServerStatus = "OmniPlay Docker 会在保存后直接同步服务端媒体库。";
                StatusMessage = MediaServerStatus;
                return;
            }

            MediaServerStatus = $"正在预扫描 {source.ProtocolLabel} 媒体列表...";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var result = await RunMediaServerPreflightAsync(source, timeout.Token);
            MediaServerStatus = result.Message;
            StatusMessage = result.Message;
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private void CloseNetworkLogin()
    {
        ClearNetworkLoginPanel(clearStatus: true);
        OnCommandStateChanged();
    }

    private void ClearNetworkLoginPanel(bool clearStatus)
    {
        IsNetworkLoginPanelOpen = false;
        IsNetworkPasswordVisible = false;
        PendingNetworkProtocolType = string.Empty;
        PendingNetworkBaseUrl = string.Empty;
        PendingNetworkDisplayName = string.Empty;
        PendingNetworkUsername = string.Empty;
        PendingNetworkPassword = string.Empty;
        PendingExternalAddresses.Clear();
        ReplaceItems(NetworkShareFolders, []);
        NotifyNetworkShareFolderStateChanged();
        if (clearStatus)
        {
            NetworkSourceStatus = string.Empty;
        }
    }

    private void ResetMediaServerForm(bool clearStatus)
    {
        IsMediaServerPanelOpen = false;
        PendingMediaServerName = string.Empty;
        PendingMediaServerProtocolType = "omniplay-docker";
        SelectedMediaServerProtocolOption = MediaServerProtocolOptions[0];
        PendingMediaServerBaseUrl = DefaultMediaServerBaseUrl(PendingMediaServerProtocolType);
        PendingMediaServerToken = string.Empty;
        IsMediaServerTokenVisible = false;
        PendingMediaServerUserId = string.Empty;
        if (clearStatus)
        {
            MediaServerStatus = string.Empty;
        }
    }

    private async Task SaveNetworkLoginAsync()
    {
        var normalizedBaseUrl = NormalizePendingNetworkBaseUrl(PendingNetworkBaseUrl.Trim());
        var protocolType = ResolvePendingNetworkProtocolType(PendingNetworkProtocolType, normalizedBaseUrl);
        if (string.Equals(protocolType, "webdav", StringComparison.OrdinalIgnoreCase))
        {
            normalizedBaseUrl = MediaSourceNormalizer.NormalizeBaseUrl(MediaSourceProtocol.WebDav, normalizedBaseUrl);
        }

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
            var sourceProtocol = source.ProtocolKind;
            if (sourceProtocol is MediaSourceProtocol.WebDav or MediaSourceProtocol.Smb)
            {
                networkCredentialStore.Save(
                    sourceProtocol.GetValueOrDefault(),
                    source.BaseUrl,
                    PendingNetworkUsername,
                    PendingNetworkPassword);
            }
            ReplaceItems(NetworkShareFolders, folders);
            NotifyNetworkShareFolderStateChanged();
            NetworkSourceStatus = folders.Count == 0
                ? "没有读取到可挂载的共享文件夹，请检查地址、用户名或密码。"
                : $"已读取到 {folders.Count} 个可挂载文件夹，可给多个目标点星标，关闭列表时统一挂载。";
        }
        catch (UnauthorizedAccessException)
        {
            ReplaceItems(NetworkShareFolders, []);
            NotifyNetworkShareFolderStateChanged();
            NetworkSourceStatus = "登录失败：用户名或密码无效。";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
        {
            ReplaceItems(NetworkShareFolders, []);
            NotifyNetworkShareFolderStateChanged();
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

        await MountNetworkFoldersAsync([folder]);
    }

    private void ToggleNetworkFolderStar(NetworkShareFolderItem? folder)
    {
        if (folder is null)
        {
            return;
        }

        var updated = NetworkShareFolders
            .Select(item => string.Equals(item.BaseUrl, folder.BaseUrl, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(item.ProtocolType, folder.ProtocolType, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(item.AuthConfig ?? string.Empty, folder.AuthConfig ?? string.Empty, StringComparison.Ordinal) &&
                            string.Equals(item.Name, folder.Name, StringComparison.OrdinalIgnoreCase)
                ? item.WithStarred(!item.IsStarred)
                : item)
            .ToList();
        ReplaceItems(NetworkShareFolders, updated);
        NotifyNetworkShareFolderStateChanged();
        OnCommandStateChanged();
    }

    private async Task MountStarredNetworkFoldersAsync()
    {
        var starredFolders = NetworkShareFolders
            .Where(static folder => folder.IsStarred)
            .ToList();
        if (starredFolders.Count == 0)
        {
            ClearNetworkLoginPanel(clearStatus: true);
            OnCommandStateChanged();
            return;
        }

        await MountNetworkFoldersAsync(starredFolders);
    }

    private async Task MountNetworkFoldersAsync(IReadOnlyList<NetworkShareFolderItem> folders)
    {
        if (folders.Count == 0)
        {
            return;
        }

        var automationCancellationTokenSource = BeginLibraryAutomation();
        var cancellationToken = automationCancellationTokenSource.Token;
        IsBusy = true;
        OnCommandStateChanged();

        try
        {
            var mountedNames = new List<string>(folders.Count);
            var mountedSourceIds = new List<long>(folders.Count);
            foreach (var folder in folders)
            {
                var source = folder.ToMediaSource();
                source.SetAddressConfiguration(BuildPendingAddressConfiguration(source.BaseUrl, source.ProtocolKind));
                var sourceId = await mediaSourceRepository.AddAsync(source);
                if (sourceId > 0)
                {
                    mountedSourceIds.Add(sourceId);
                }
                mountedNames.Add(source.Name);
            }

            ClearNetworkLoginPanel(clearStatus: false);
            await ReloadLibraryAsync();
            var folderSummary = folders.Count == 1
                ? mountedNames[0]
                : $"{folders.Count} 个文件夹";
            NetworkSourceStatus = $"已挂载媒体源：{folderSummary}，正在扫描并刮削...";
            StatusMessage = NetworkSourceStatus;
            IsBusy = false;
            OnCommandStateChanged();

            LastScanSummary = await RunLibraryScanAndArtworkAsync(
                isExplicitRequest: true,
                forceThumbnails: true,
                cancellationToken,
                mountedSourceIds);
            StatusMessage = string.IsNullOrWhiteSpace(StatusMessage)
                ? $"已挂载并扫描媒体源：{folderSummary}"
                : $"已挂载媒体源：{folderSummary}。{StatusMessage}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "\u5DF2\u505C\u6B62\u65B0\u6302\u8F7D\u5A92\u4F53\u6E90\u7684\u626B\u63CF\u548C\u522E\u524A\u4EFB\u52A1\u3002";
        }
        catch (Exception ex)
        {
            NetworkSourceStatus = $"挂载媒体源失败：{ex.Message}";
            StatusMessage = NetworkSourceStatus;
        }
        finally
        {
            IsBusy = false;
            CompleteLibraryAutomation(automationCancellationTokenSource);
            OnCommandStateChanged();
        }
    }

    private MediaSourceAddressConfiguration BuildPendingAddressConfiguration(
        string localAddress,
        MediaSourceProtocol? protocolKind)
    {
        var configuration = new MediaSourceAddressConfiguration
        {
            LocalAddress = localAddress.Trim(),
            LocalLabel = "局域网",
            ActiveAddress = localAddress.Trim(),
            ActiveLabel = "局域网"
        };

        foreach (var pending in PendingExternalAddresses)
        {
            var address = pending.Address.Trim();
            if (string.IsNullOrWhiteSpace(address))
            {
                continue;
            }

            if (protocolKind == MediaSourceProtocol.Smb)
            {
                address = NormalizePendingNetworkBaseUrl(address);
            }
            else if (protocolKind == MediaSourceProtocol.WebDav)
            {
                address = MediaSourceNormalizer.NormalizeBaseUrl(MediaSourceProtocol.WebDav, address);
            }

            var rule = Settings.LuckyStunRules.FirstOrDefault(item =>
                string.Equals(item.Id, pending.LuckyRuleId, StringComparison.OrdinalIgnoreCase));
            configuration.ExternalAddresses.Add(new MediaSourceExternalAddress
            {
                Address = address,
                Label = pending.Label.Trim(),
                UseLuckyStun = pending.UseLuckyStun,
                LuckyRuleId = pending.LuckyRuleId.Trim(),
                LuckyRuleName = rule?.Name ?? pending.LuckyRuleName.Trim()
            });
        }

        return configuration;
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
        OpenMediaServerPanelCommand.NotifyCanExecuteChanged();
        CloseMediaServerPanelCommand.NotifyCanExecuteChanged();
        PreScanMediaServerSourceCommand.NotifyCanExecuteChanged();
        AuthorizePlexCommand.NotifyCanExecuteChanged();
        AddMediaServerSourceCommand.NotifyCanExecuteChanged();
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
        ChooseOfflineCacheDirectoryCommand.NotifyCanExecuteChanged();
        CachePosterItemCommand.NotifyCanExecuteChanged();
        CacheDetailPosterCommand.NotifyCanExecuteChanged();
        CacheSelectedSeasonCommand.NotifyCanExecuteChanged();
        CacheDetailEpisodeCommand.NotifyCanExecuteChanged();
        CloseOfflineCacheAlertCommand.NotifyCanExecuteChanged();
        ContinueWithoutTmdbStartupScanCommand.NotifyCanExecuteChanged();
        OpenPosterWatchStateConfirmationCommand.NotifyCanExecuteChanged();
        CancelPosterWatchStateConfirmationCommand.NotifyCanExecuteChanged();
        ConfirmPosterWatchStateCommand.NotifyCanExecuteChanged();
        OpenPosterScrapeCommand.NotifyCanExecuteChanged();
        SearchPosterMetadataCandidatesCommand.NotifyCanExecuteChanged();
        ApplyPosterMetadataCandidateCommand.NotifyCanExecuteChanged();
        OpenPosterEditCommand.NotifyCanExecuteChanged();
        ChoosePosterImageCommand.NotifyCanExecuteChanged();
        BindPosterDoubanCommand.NotifyCanExecuteChanged();
        SavePosterMetadataEditCommand.NotifyCanExecuteChanged();
        OpenEpisodeEditCommand.NotifyCanExecuteChanged();
        SaveEpisodeEditCommand.NotifyCanExecuteChanged();
        ChooseEpisodeThumbnailImageCommand.NotifyCanExecuteChanged();
        EditSourceCommand.NotifyCanExecuteChanged();
        CancelWebDavEditCommand.NotifyCanExecuteChanged();
        RemoveSourceCommand.NotifyCanExecuteChanged();
        ToggleSourceEnabledCommand.NotifyCanExecuteChanged();
        CheckSourceConnectivityCommand.NotifyCanExecuteChanged();
        RefreshNetworkSourcesCommand.NotifyCanExecuteChanged();
        OpenNetworkLoginCommand.NotifyCanExecuteChanged();
        OpenManualNetworkLoginCommand.NotifyCanExecuteChanged();
        SaveNetworkLoginCommand.NotifyCanExecuteChanged();
        MountNetworkFolderCommand.NotifyCanExecuteChanged();
        ToggleNetworkFolderStarCommand.NotifyCanExecuteChanged();
        MountStarredNetworkFoldersCommand.NotifyCanExecuteChanged();
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
        if (cancellationTokenSource is null)
        {
            return;
        }

        if (!cancellationTokenSource.IsCancellationRequested)
        {
            cancellationTokenSource.Cancel();
        }

        libraryAutomationCancellationTokenSource = null;
        IsLibraryScanInProgress = false;
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
        var normalizedUrl = MediaSourceNormalizer.NormalizeBaseUrl(MediaSourceProtocol.WebDav, PendingWebDavUrl.Trim());
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

        source.SetAddressConfiguration(BuildPendingAddressConfiguration(source.BaseUrl, source.ProtocolKind));

        return source;
    }

    private MediaSource? BuildPendingMediaServerSource()
    {
        var protocolType = NormalizeMediaServerProtocolType(PendingMediaServerProtocolType);
        var protocolKind = ResolveMediaServerProtocolKind(protocolType);
        var normalizedUrl = MediaSourceNormalizer.NormalizeBaseUrl(protocolKind, PendingMediaServerBaseUrl.Trim());
        if (string.IsNullOrWhiteSpace(normalizedUrl))
        {
            MediaServerStatus = "请输入媒体服务器地址。";
            return null;
        }

        var authConfig = protocolKind == MediaSourceProtocol.OmniPlayDocker
            ? MediaSourceAuthConfigSerializer.SerializeOmniPlayDocker(new OmniPlayDockerAuthConfig(
                string.IsNullOrWhiteSpace(PendingMediaServerUserId) ? null : PendingMediaServerUserId.Trim(),
                string.IsNullOrWhiteSpace(PendingMediaServerToken) ? null : PendingMediaServerToken.Trim()))
            : MediaSourceAuthConfigSerializer.SerializeMediaServer(new MediaServerAuthConfig(
                PendingMediaServerToken,
                protocolKind == MediaSourceProtocol.Plex ? string.Empty : PendingMediaServerUserId.Trim()));

        var source = new MediaSource
        {
            Name = PendingMediaServerName.Trim(),
            ProtocolType = protocolType,
            BaseUrl = normalizedUrl,
            AuthConfig = authConfig
        };

        if (!source.IsValidConfiguration())
        {
            MediaServerStatus = "媒体服务器地址无效，请输入以 http:// 或 https:// 开头的地址。";
            return null;
        }

        if (protocolKind != MediaSourceProtocol.OmniPlayDocker && string.IsNullOrWhiteSpace(PendingMediaServerToken))
        {
            MediaServerStatus = protocolKind == MediaSourceProtocol.Plex
                ? "Plex 需要访问令牌（X-Plex-Token）才能读取媒体库。"
                : $"{source.ProtocolLabel} 需要密码、API Key 或访问令牌才能读取媒体列表和生成播放地址。";
            return null;
        }

        if (string.IsNullOrWhiteSpace(source.Name))
        {
            source.Name = ResolveMediaServerSourceName(source.ProtocolLabel, source.BaseUrl);
        }

        return source;
    }

    private Task<MediaServerPreflightResult> RunMediaServerPreflightAsync(MediaSource source, CancellationToken cancellationToken)
    {
        return mediaServerPreflightService is null
            ? Task.FromResult(new MediaServerPreflightResult(false, "当前版本未启用媒体服务器预扫描。"))
            : mediaServerPreflightService.PreScanAsync(source, cancellationToken);
    }

    private static string NormalizeMediaServerProtocolType(string protocolType)
    {
        return protocolType.Trim().ToLowerInvariant() switch
        {
            "omniplaydocker" or "omniplay-docker" or "omniplay_docker" => "omniplay-docker",
            "emby" => "emby",
            "jellyfin" => "jellyfin",
            _ => "plex"
        };
    }

    private static MediaSourceProtocol ResolveMediaServerProtocolKind(string protocolType)
    {
        return NormalizeMediaServerProtocolType(protocolType) switch
        {
            "omniplay-docker" => MediaSourceProtocol.OmniPlayDocker,
            "emby" => MediaSourceProtocol.Emby,
            "jellyfin" => MediaSourceProtocol.Jellyfin,
            _ => MediaSourceProtocol.Plex
        };
    }

    private static string DefaultMediaServerBaseUrl(string protocolType)
    {
        return NormalizeMediaServerProtocolType(protocolType) switch
        {
            "omniplay-docker" => "http://127.0.0.1:45722",
            "emby" or "jellyfin" => "http://127.0.0.1:8096",
            _ => "http://127.0.0.1:32400"
        };
    }

    private static bool IsKnownMediaServerDefaultBaseUrl(string value)
    {
        var trimmed = value.Trim();
        return string.Equals(trimmed, "http://127.0.0.1:32400", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "http://localhost:32400", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "http://127.0.0.1:8096", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "http://localhost:8096", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "http://127.0.0.1:45722", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "http://localhost:45722", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetMediaServerConnectionHint(string protocolType)
    {
        return NormalizeMediaServerProtocolType(protocolType) switch
        {
            "omniplay-docker" => "OmniPlay Docker 默认地址为 http://127.0.0.1:45722；未开启登录可留空 session cookie。",
            "plex" => "Plex 默认地址为 http://127.0.0.1:32400，点击“登录 Plex”会自动获取访问令牌。",
            "emby" => "Emby 默认地址为 http://127.0.0.1:8096，可填写用户名和密码，也可直接填写 API Key 或访问令牌。",
            "jellyfin" => "Jellyfin 默认地址为 http://127.0.0.1:8096，可填写用户名和密码，也可直接填写 API Key 或访问令牌。",
            _ => "保存后会先预扫描媒体库列表；可给多个库点星标，关闭列表时统一挂载并扫描刮削。"
        };
    }

    private void ApplyMediaServerProtocolDefaults(string protocolType)
    {
        var currentBaseUrl = PendingMediaServerBaseUrl.Trim();
        if (string.IsNullOrWhiteSpace(currentBaseUrl) || IsKnownMediaServerDefaultBaseUrl(currentBaseUrl))
        {
            PendingMediaServerBaseUrl = DefaultMediaServerBaseUrl(protocolType);
        }

        if (NormalizeMediaServerProtocolType(protocolType) is "plex" or "omniplay-docker")
        {
            PendingMediaServerUserId = string.Empty;
        }

        NotifyMediaServerProtocolDependentProperties();
    }

    private void NotifyMediaServerProtocolDependentProperties()
    {
        OnPropertyChanged(nameof(PendingMediaServerAddressWatermark));
        OnPropertyChanged(nameof(PendingMediaServerTokenWatermark));
        OnPropertyChanged(nameof(PendingMediaServerUserWatermark));
        OnPropertyChanged(nameof(PendingMediaServerUsesUserId));
        OnPropertyChanged(nameof(PendingMediaServerUsesPlex));
        OnPropertyChanged(nameof(PlexAuthorizeButtonText));
        AuthorizePlexCommand.NotifyCanExecuteChanged();
    }

    private sealed record OfflineCacheFilePlan(
        LibraryVideoItem File,
        string? SourcePath,
        Uri? RemoteUri,
        string? RemoteAuthorization,
        string DestinationPath,
        long FileSize);

    private sealed record TvShowHomeGroup(TvShow Representative, IReadOnlyList<TvShow> Shows);

    private sealed record PlexPinSession(string Id, string Code, string AuthorizationUrl);

    private static class PlexPinAuthClient
    {
        private static readonly HttpClient Client = new()
        {
            Timeout = TimeSpan.FromSeconds(12)
        };

        public static async Task<PlexPinSession> CreateAsync(CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://plex.tv/api/v2/pins");
            ApplyHeaders(request);

            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var id = ReadJsonString(root, "id");
            var code = ReadJsonString(root, "code");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(code))
            {
                throw new InvalidOperationException("Plex 授权响应无效。");
            }

            return new PlexPinSession(id, code, BuildAuthorizationUrl(code));
        }

        public static async Task<string> WaitForTokenAsync(PlexPinSession session, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                var token = await TryReadTokenAsync(session, cancellationToken);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    return token;
                }
            }

            throw new OperationCanceledException(cancellationToken);
        }

        private static async Task<string?> TryReadTokenAsync(PlexPinSession session, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://plex.tv/api/v2/pins/{Uri.EscapeDataString(session.Id)}");
            ApplyHeaders(request);

            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return ReadJsonString(document.RootElement, "authToken");
        }

        private static void ApplyHeaders(HttpRequestMessage request)
        {
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.TryAddWithoutValidation("X-Plex-Product", "OmniPlay");
            request.Headers.TryAddWithoutValidation("X-Plex-Version", "1.0");
            request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", ClientIdentifier);
            request.Headers.TryAddWithoutValidation("X-Plex-Device-Name", Environment.MachineName);
            request.Headers.TryAddWithoutValidation("X-Plex-Platform", "Windows");
        }

        private static string BuildAuthorizationUrl(string code)
        {
            return $"https://plex.tv/link/?pin={Uri.EscapeDataString(code)}";
        }

        private static string ClientIdentifier
        {
            get
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "OmniPlay");
                var path = Path.Combine(directory, "plex-client-id.txt");
                try
                {
                    if (File.Exists(path))
                    {
                        var existing = File.ReadAllText(path).Trim();
                        if (!string.IsNullOrWhiteSpace(existing))
                        {
                            return existing;
                        }
                    }

                    Directory.CreateDirectory(directory);
                    var created = $"omniplay-windows-{Guid.NewGuid():N}";
                    File.WriteAllText(path, created);
                    return created;
                }
                catch
                {
                    return "omniplay-windows";
                }
            }
        }

        private static string? ReadJsonString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                return null;
            }

            return property.ValueKind switch
            {
                JsonValueKind.String => property.GetString(),
                JsonValueKind.Number => property.GetRawText(),
                _ => null
            };
        }
    }

    private static string ResolveMediaServerSourceName(string protocolLabel, string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            return protocolLabel;
        }

        return $"{protocolLabel} · {uri.Host}";
    }

    private static MediaSource CreateLocalSource(string selectedPath, long? sourceId = null)
    {
        var normalizedPath = selectedPath.Trim();
        var displayName = Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = normalizedPath;
        }

        var source = new MediaSource
        {
            Id = sourceId,
            Name = displayName,
            ProtocolType = "local",
            BaseUrl = normalizedPath
        };
        source.SetAddressConfiguration(new MediaSourceAddressConfiguration
        {
            LocalAddress = normalizedPath,
            LocalLabel = "局域网",
            ActiveAddress = normalizedPath,
            ActiveLabel = "局域网"
        });
        return source;
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

    private async Task CheckSourceConnectivityAsync(MediaSource? source)
    {
        if (source?.Id is null)
        {
            return;
        }

        IsBusy = true;
        OnCommandStateChanged();
        try
        {
            var configuration = source.GetAddressConfiguration();
            var candidates = new List<(string Address, string Label)>
            {
                (configuration.LocalAddress.Trim(), string.IsNullOrWhiteSpace(configuration.LocalLabel) ? "局域网" : configuration.LocalLabel.Trim())
            };
            candidates.AddRange(configuration.ExternalAddresses
                .Where(static item => !string.IsNullOrWhiteSpace(item.Address))
                .Select(static item => (item.Address.Trim(), item.DisplayLabel)));

            foreach (var candidate in candidates)
            {
                if (await IsSourceAddressReachableAsync(source, candidate.Address))
                {
                    configuration.ActiveAddress = candidate.Address;
                    configuration.ActiveLabel = candidate.Label;
                    source.BaseUrl = candidate.Address;
                    source.SetAddressConfiguration(configuration);
                    var updated = await mediaSourceRepository.UpdateAsync(source);
                    await ReloadLibraryAsync();
                    StatusMessage = updated
                        ? $"媒体源“{source.Name}”已切换到{candidate.Label}入口。"
                        : $"媒体源“{source.Name}”已检测到可用入口：{candidate.Label}。";
                    return;
                }
            }

            StatusMessage = $"媒体源“{source.Name}”的局域网和外网入口均无法连接。";
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private async Task<bool> IsSourceAddressReachableAsync(MediaSource source, string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var probe = new MediaSource
        {
            Name = source.Name,
            ProtocolType = source.ProtocolType,
            BaseUrl = address,
            AuthConfig = source.AuthConfig,
            IsEnabled = source.IsEnabled
        };

        switch (source.ProtocolKind)
        {
            case MediaSourceProtocol.Local:
            case MediaSourceProtocol.Smb:
                return await Task.Run(() => Directory.Exists(address));
            case MediaSourceProtocol.WebDav:
                return (await webDavConnectionTester.TestConnectionAsync(probe)).Success;
            case MediaSourceProtocol.Plex:
            case MediaSourceProtocol.Emby:
            case MediaSourceProtocol.Jellyfin:
            case MediaSourceProtocol.OmniPlayDocker:
                return (await RunMediaServerPreflightAsync(probe, CancellationToken.None)).Success;
            case MediaSourceProtocol.Direct:
                return await Task.Run(() => File.Exists(address) || Directory.Exists(address));
            default:
                return false;
        }
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
            IsBusy = false;
            OnCommandStateChanged();
            try
            {
                LastScanSummary = await RunLibraryScanAndArtworkAsync(
                    isExplicitRequest: true,
                    forceThumbnails: true,
                    cancellationToken,
                    new[] { source.Id.Value });
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
                ? $"已移除媒体源：{source.Name}。已停止对应扫描、刮削和分集剧照刮削任务；首页已隐藏对应海报，扫描和刮削数据保留 30 天；重新挂载时会优先预填已保存的 SMB/WebDAV 登录信息。"
                : $"\u672A\u627E\u5230\u8981\u79FB\u9664\u7684\u5A92\u4F53\u6E90\uFF1A{source.Name}";
        }
        finally
        {
            OnCommandStateChanged();
        }
    }

    private static void SynchronizeLibraryPosterItems(
        ObservableCollection<LibraryPosterItem> target,
        IReadOnlyList<LibraryPosterItem> source)
    {
        for (var sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
        {
            var sourceItem = source[sourceIndex];
            var existingIndex = IndexOfPosterItem(target, sourceItem.Id);
            if (existingIndex < 0)
            {
                target.Insert(sourceIndex, sourceItem);
                continue;
            }

            if (existingIndex != sourceIndex)
            {
                target.Move(existingIndex, sourceIndex);
            }

            if (!LibraryPosterItemsEqual(target[sourceIndex], sourceItem))
            {
                target[sourceIndex] = sourceItem;
            }
        }

        while (target.Count > source.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private static int IndexOfPosterItem(IReadOnlyList<LibraryPosterItem> items, string id)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (string.Equals(items[index].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool LibraryPosterItemsEqual(LibraryPosterItem left, LibraryPosterItem right)
    {
        return string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
               string.Equals(left.Title, right.Title, StringComparison.Ordinal) &&
               string.Equals(left.Subtitle, right.Subtitle, StringComparison.Ordinal) &&
               string.Equals(left.PosterPath, right.PosterPath, StringComparison.Ordinal) &&
               left.VoteAverage == right.VoteAverage &&
               string.Equals(left.MediaKind, right.MediaKind, StringComparison.Ordinal) &&
               left.ContinueWatchingProgress == right.ContinueWatchingProgress &&
               left.LastPlayedAt == right.LastPlayedAt &&
               string.Equals(left.ContinueWatchingLabel, right.ContinueWatchingLabel, StringComparison.Ordinal) &&
               left.IsContinuing == right.IsContinuing &&
               left.WatchState == right.WatchState &&
               left.OfflineCacheIsDownloading == right.OfflineCacheIsDownloading &&
               left.OfflineCacheIsCached == right.OfflineCacheIsCached &&
               left.OfflineCacheUnavailable == right.OfflineCacheUnavailable &&
               left.OfflineCacheProgress == right.OfflineCacheProgress &&
               left.MovieId == right.MovieId &&
               left.TvShowId == right.TvShowId;
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
                _ = TryAutoAdvanceToNextMoviePartAsync();
                _ = TryAutoAdvanceToNextEpisodeAsync();
            }

            NotifyPlayerTimelineChanged();
            return;
        }

        if (e.PropertyName is nameof(PlayerViewModel.CurrentPositionSeconds)
            or nameof(PlayerViewModel.DurationSeconds)
            or nameof(PlayerViewModel.SeekPositionSeconds)
            or nameof(PlayerViewModel.IsPlaying)
            or nameof(PlayerViewModel.IsPaused))
        {
            NotifyPlayerTimelineChanged();
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

    private async Task TryAutoAdvanceToNextMoviePartAsync()
    {
        if (isAutoAdvancingToNextMoviePart ||
            !HasActivePlaybackSession() ||
            string.IsNullOrWhiteSpace(currentPlayingVideoId) ||
            string.Equals(autoAdvancedPlaybackVideoId, currentPlayingVideoId, StringComparison.Ordinal))
        {
            return;
        }

        var currentPart = ResolveCurrentPlaybackMoviePart();
        if (currentPart is null || FindNextMoviePart(currentPart) is null)
        {
            return;
        }

        autoAdvancedPlaybackVideoId = currentPlayingVideoId;
        isAutoAdvancingToNextMoviePart = true;
        try
        {
            await PlayNextMoviePartAsync();
        }
        finally
        {
            isAutoAdvancingToNextMoviePart = false;
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

        var normalizedProgress = NormalizePersistedProgress(progress, duration);
        await videoFileRepository.UpdatePlaybackStateAsync(videoId, normalizedProgress, duration);
        await SyncOmniPlayDockerPlaybackProgressIfNeededAsync(videoId, normalizedProgress, duration);
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

    private async Task SyncOmniPlayDockerPlaybackProgressIfNeededAsync(string videoId, double progress, double duration)
    {
        if (omniPlayDockerClient is null)
        {
            return;
        }

        var video = ResolveDetailVideo(videoId);
        if (video is null ||
            !string.Equals(video.SourceProtocolType, "omniplay-docker", StringComparison.OrdinalIgnoreCase) ||
            !TryResolveOmniPlayDockerRemoteFileId(video.Id, video.RelativePath, out var remoteFileId))
        {
            return;
        }

        try
        {
            await omniPlayDockerClient.UpdateProgressAsync(
                video.SourceBasePath,
                video.SourceAuthConfig,
                remoteFileId,
                progress,
                duration);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            StatusMessage = $"Docker 进度同步失败：{ex.Message}";
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilters();
    }

    partial void OnSelectedSortOptionChanged(LibrarySortOption value)
    {
        var selectedItem = SortOptions.FirstOrDefault(option => option.Value == value) ?? SortOptions[0];
        if (!ReferenceEquals(SelectedSortOptionItem, selectedItem))
        {
            SelectedSortOptionItem = selectedItem;
        }

        RefreshSortOptionSelectionStates();
        ApplyFilters();
        PersistLibraryViewPreferencesIfNeeded();
    }

    partial void OnIsSortDescendingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSortAscending));
        OnPropertyChanged(nameof(SortDirectionLabel));
        OnPropertyChanged(nameof(SortDirectionToolTip));
        ApplyFilters();
        PersistLibraryViewPreferencesIfNeeded();
    }

    partial void OnSelectedSortOptionItemChanged(LibrarySortOptionItem value)
    {
        if (value is not null)
        {
            SelectedSortOption = value.Value;
            RefreshSortOptionSelectionStates();
        }
    }

    partial void OnSelectedMediaServerProtocolOptionChanged(SettingsOptionItem? value)
    {
        if (value is not null)
        {
            PendingMediaServerProtocolType = value.Value;
            ApplyMediaServerProtocolDefaults(value.Value);
            MediaServerStatus = GetMediaServerConnectionHint(value.Value);
        }
    }

    partial void OnPendingMediaServerProtocolTypeChanged(string value)
    {
        NotifyMediaServerProtocolDependentProperties();
    }

    partial void OnIsPlexAuthorizationInProgressChanged(bool value)
    {
        OnPropertyChanged(nameof(PlexAuthorizeButtonText));
        AuthorizePlexCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsNetworkPasswordVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(NetworkPasswordVisibilityGlyph));
    }

    partial void OnIsMediaServerTokenVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(MediaServerTokenVisibilityGlyph));
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

        var sortedItems = SortLibraryItems(items)
            .Select(ApplyOfflineCacheState)
            .ToList();

        SynchronizeLibraryPosterItems(LibraryItems, ArrangeLibraryItemsForPresentation(sortedItems));
    }

    private IReadOnlyList<LibraryPosterItem> ArrangeLibraryItemsForPresentation(IReadOnlyList<LibraryPosterItem> sortedItems)
    {
        UpdateLibraryPosterTracking();
        if (newlyDiscoveredPosterOrder.Count == 0)
        {
            return sortedItems;
        }

        var pinnedItems = sortedItems
            .Where(item => newlyDiscoveredPosterOrder.ContainsKey(item.Id))
            .OrderByDescending(item => newlyDiscoveredPosterOrder[item.Id])
            .ToList();
        if (pinnedItems.Count == 0)
        {
            return sortedItems;
        }

        return pinnedItems
            .Concat(sortedItems.Where(item => !newlyDiscoveredPosterOrder.ContainsKey(item.Id)))
            .ToList();
    }

    private void UpdateLibraryPosterTracking()
    {
        var currentIds = allLibraryItems
            .Select(static item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!libraryPosterTrackingInitialized)
        {
            knownLibraryPosterIds.Clear();
            knownLibraryPosterIds.UnionWith(currentIds);
            libraryPosterTrackingInitialized = true;
            return;
        }

        foreach (var removedId in knownLibraryPosterIds.Except(currentIds).ToList())
        {
            knownLibraryPosterIds.Remove(removedId);
            newlyDiscoveredPosterOrder.Remove(removedId);
        }

        foreach (var item in allLibraryItems)
        {
            if (knownLibraryPosterIds.Add(item.Id) && IsLibraryScanInProgress)
            {
                newlyDiscoveredPosterOrder[item.Id] = ++nextNewlyDiscoveredPosterOrder;
            }
        }

        if (!IsLibraryScanInProgress)
        {
            newlyDiscoveredPosterOrder.Clear();
            return;
        }

        foreach (var item in allLibraryItems)
        {
            if (newlyDiscoveredPosterOrder.ContainsKey(item.Id) && HasSettledPosterMetadata(item))
            {
                newlyDiscoveredPosterOrder.Remove(item.Id);
            }
        }
    }

    private static bool HasSettledPosterMetadata(LibraryPosterItem item)
    {
        return !string.IsNullOrWhiteSpace(item.PosterPath) ||
               item.VoteAverage.HasValue ||
               SortYearValue(item).HasValue;
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

    private async Task RefreshDetailFilesAsync(int? preservedSelectedSeason = null)
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

        files = ApplyEpisodeDisplayDisambiguation(files);
        allDetailFiles.Clear();
        allDetailFiles.AddRange(files.Select(ApplyOfflineCacheState));
        BuildSeasonState(files, IsDetailSeries, preservedSelectedSeason);
        ApplyDetailFilter();
    }

    private async Task RefreshContinueWatchingAsync()
    {
        var continueWatching = await videoFileRepository.GetContinueWatchingAsync();
        ReplaceItems(ContinueWatchingItems, continueWatching.Select(ApplyOfflineCacheState).ToList());
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

    private static PlaybackWatchState ResolvePosterWatchState(
        IReadOnlyDictionary<string, PlaybackWatchState> playbackStates,
        IReadOnlyList<string> itemIds)
    {
        var hasWatched = false;
        foreach (var itemId in itemIds)
        {
            if (!playbackStates.TryGetValue(itemId, out var watchState))
            {
                continue;
            }

            if (watchState == PlaybackWatchState.InProgress)
            {
                return PlaybackWatchState.InProgress;
            }

            hasWatched |= watchState == PlaybackWatchState.Watched;
        }

        return hasWatched ? PlaybackWatchState.Watched : PlaybackWatchState.Unwatched;
    }

    private static IReadOnlyList<TvShowHomeGroup> GroupTvShowsForHome(IReadOnlyList<TvShow> shows)
    {
        return shows
            .GroupBy(static show => NormalizeTvShowHomeGroupKey(show.Title), StringComparer.OrdinalIgnoreCase)
            .Select(static group =>
            {
                var groupedShows = group.ToList();
                return new TvShowHomeGroup(SelectRepresentativeTvShow(groupedShows), groupedShows);
            })
            .OrderBy(static group => group.Representative.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static TvShow SelectRepresentativeTvShow(IReadOnlyList<TvShow> shows)
    {
        return shows
            .OrderByDescending(static show => show.IsLocked)
            .ThenByDescending(static show => !string.IsNullOrWhiteSpace(show.PosterPath))
            .ThenByDescending(static show => !string.IsNullOrWhiteSpace(show.Overview))
            .ThenByDescending(static show => show.VoteAverage.HasValue)
            .ThenBy(static show => show.Id)
            .First();
    }

    private static string NormalizeTvShowHomeGroupKey(string? title)
    {
        var normalized = Regex.Replace(title ?? string.Empty, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(normalized) ? Guid.NewGuid().ToString("N") : normalized;
    }

    private static LibraryPosterItem CopyPosterItem(
        LibraryPosterItem item,
        PlaybackWatchState watchState,
        bool isContinuing,
        bool? offlineCacheIsDownloading = null,
        bool? offlineCacheIsCached = null,
        bool? offlineCacheUnavailable = null,
        double? offlineCacheProgress = null)
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
            LastPlayedAt = item.LastPlayedAt,
            ContinueWatchingLabel = item.ContinueWatchingLabel,
            IsContinuing = isContinuing,
            WatchState = watchState,
            OfflineCacheIsDownloading = offlineCacheIsDownloading ?? item.OfflineCacheIsDownloading,
            OfflineCacheIsCached = offlineCacheIsCached ?? item.OfflineCacheIsCached,
            OfflineCacheUnavailable = offlineCacheUnavailable ?? item.OfflineCacheUnavailable,
            OfflineCacheProgress = offlineCacheProgress ?? item.OfflineCacheProgress,
            MovieId = item.MovieId,
            TvShowId = item.TvShowId
        };
    }

    private static IReadOnlyList<LibraryVideoItem> ApplyEpisodeDisplayDisambiguation(IReadOnlyList<LibraryVideoItem> files)
    {
        var duplicateEpisodeGroups = files
            .Where(static file => file.IsTvEpisode)
            .GroupBy(static file => (file.SeasonNumber, file.EpisodeNumber))
            .Where(static group => group.Count() > 1)
            .ToList();

        var duplicateEpisodeKeys = duplicateEpisodeGroups
            .Select(static group => group.Key)
            .ToHashSet();

        var prefixSuffixesByFileId = duplicateEpisodeGroups
            .SelectMany(static group => BuildEpisodePrefixDisambiguationSuffixes(
                group.Where(static file => string.IsNullOrWhiteSpace(file.CustomEpisodeSubtitle)).ToList()))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

        return files
            .Select(file =>
            {
                if (!file.IsTvEpisode || !string.IsNullOrWhiteSpace(file.CustomEpisodeSubtitle))
                {
                    return file;
                }

                string? suffix = null;
                if (duplicateEpisodeKeys.Contains((file.SeasonNumber, file.EpisodeNumber)))
                {
                    prefixSuffixesByFileId.TryGetValue(file.Id, out suffix);
                    if (string.IsNullOrWhiteSpace(suffix))
                    {
                        suffix = Path.GetFileNameWithoutExtension(file.FileName);
                    }
                }

                if (duplicateEpisodeKeys.Contains((file.SeasonNumber, file.EpisodeNumber)) &&
                    !string.IsNullOrWhiteSpace(suffix))
                {
                    return CopyVideoItemWithEpisodeSubtitle(file, suffix);
                }

                return CopyVideoItemWithEpisodeSubtitle(file, null);
            })
            .ToList();
    }

    private static IReadOnlyDictionary<string, string> BuildEpisodePrefixDisambiguationSuffixes(
        IReadOnlyList<LibraryVideoItem> files)
    {
        if (files.Count <= 1)
        {
            return new Dictionary<string, string>();
        }

        var tokenSets = files
            .Select(static file => new
            {
                file.Id,
                Tokens = EpisodePrefixTokens(file.FileName)
            })
            .ToList();
        if (tokenSets.All(static item => item.Tokens.Count == 0))
        {
            return new Dictionary<string, string>();
        }

        var allTokens = tokenSets.Select(static item => item.Tokens).ToList();
        var prefixCount = CommonPrefixTokenCount(allTokens);
        var suffixCount = CommonSuffixTokenCount(allTokens, prefixCount);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var item in tokenSets)
        {
            var end = Math.Max(prefixCount, item.Tokens.Count - suffixCount);
            if (prefixCount >= end)
            {
                continue;
            }

            var differenceTokens = item.Tokens.Skip(prefixCount).Take(end - prefixCount).ToList();
            var title = PreferredEpisodeDifferenceTitle(differenceTokens);
            if (!string.IsNullOrWhiteSpace(title))
            {
                result[item.Id] = title;
            }
        }

        return result;
    }

    private static List<string> EpisodePrefixTokens(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return [];
        }

        var marker = Regex.Match(
            stem,
            @"[sS]\d{1,2}[eE][pP]?\d{1,3}|[eE][pP]?\d{1,3}|第\s*\d{1,3}\s*[集话]");
        if (!marker.Success)
        {
            return [];
        }

        var prefix = stem[..marker.Index];
        var normalized = Regex.Replace(prefix, @"[\[\]\(\)\{\}【】（）《》「」『』〔〕〖〗,:：]+", " ");
        normalized = Regex.Replace(normalized, @"[._\-–—]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => !IsEpisodePrefixNoiseToken(token))
            .ToList();
    }

    private static int CommonPrefixTokenCount(IReadOnlyList<List<string>> tokenSets)
    {
        if (tokenSets.Count == 0 || tokenSets[0].Count == 0)
        {
            return 0;
        }

        var count = 0;
        while (count < tokenSets[0].Count)
        {
            var token = NormalizeEpisodeDifferenceToken(tokenSets[0][count]);
            if (tokenSets.Any(tokens => tokens.Count <= count || NormalizeEpisodeDifferenceToken(tokens[count]) != token))
            {
                break;
            }

            count++;
        }

        return count;
    }

    private static int CommonSuffixTokenCount(IReadOnlyList<List<string>> tokenSets, int prefixCount)
    {
        var count = 0;
        while (true)
        {
            string? candidate = null;
            foreach (var tokens in tokenSets)
            {
                var index = tokens.Count - count - 1;
                if (index < prefixCount)
                {
                    return count;
                }

                var token = NormalizeEpisodeDifferenceToken(tokens[index]);
                if (candidate is not null && candidate != token)
                {
                    return count;
                }

                candidate ??= token;
            }

            count++;
        }
    }

    private static string? PreferredEpisodeDifferenceTitle(IReadOnlyList<string> tokens)
    {
        var chineseTokens = tokens
            .Where(static token => Regex.IsMatch(token, @"\p{IsCJKUnifiedIdeographs}"))
            .ToList();
        if (chineseTokens.Count > 0)
        {
            var title = string.Concat(chineseTokens).Trim();
            return title.Length == 0 ? null : title;
        }

        var englishTokens = tokens
            .Where(static token => Regex.IsMatch(token, @"[A-Za-z]") && !IsEpisodePrefixNoiseToken(token))
            .ToList();
        var englishTitle = string.Join(' ', englishTokens).Trim();
        return englishTitle.Length == 0 ? null : englishTitle;
    }

    private static string NormalizeEpisodeDifferenceToken(string token)
    {
        return token.Trim().ToLowerInvariant();
    }

    private static bool IsEpisodePrefixNoiseToken(string token)
    {
        var lower = token.Trim().ToLowerInvariant();
        if (lower.Length == 0) return true;
        if (Regex.IsMatch(lower, @"^(19|20)\d{2}$")) return true;
        if (Regex.IsMatch(lower, @"^\d+$")) return true;
        if (Regex.IsMatch(lower, @"^\d+(\.\d+)?$")) return true;
        if (Regex.IsMatch(lower, @"^(\d{3,4}p|[48]k|uhd|fhd|hd|sd)$")) return true;
        if (Regex.IsMatch(lower, @"^(8|10|12)bit$")) return true;
        if (Regex.IsMatch(lower, @"^(x|h)?26[45]$")) return true;
        if (Regex.IsMatch(lower, @"^h\.?(264|265)$")) return true;
        if (Regex.IsMatch(lower, @"^(aac|ac3|eac3|ddp|dts|truehd|lpcm|flac|mp3|opus)\d*(\.\d+)?$")) return true;

        var releaseTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "blu", "ray", "bluray", "bdrip", "web", "dl", "webdl", "webrip", "hdtv", "uhdtv",
            "remux", "avc", "hevc", "hdr", "dv", "dovi", "sdr", "hdr10", "hdr10plus",
            "atmos", "nf", "netflix", "amzn", "amazon", "dsnp", "disney", "hulu", "atvp",
            "max", "proper", "repack", "internal"
        };
        return releaseTokens.Contains(lower);
    }

    private static LibraryVideoItem CopyVideoItemWithEpisodeSubtitle(LibraryVideoItem file, string? episodeSubtitle)
    {
        return CopyVideoItem(
            file,
            file.OfflineCachePath,
            file.OfflineCacheIsDownloading,
            file.OfflineCacheIsCached,
            file.OfflineCacheUnavailable,
            file.OfflineCacheProgress,
            episodeSubtitle,
            overrideEpisodeSubtitle: true);
    }

    private static LibraryVideoItem CopyVideoItem(
        LibraryVideoItem file,
        string? offlineCachePath,
        bool offlineCacheIsDownloading,
        bool offlineCacheIsCached,
        bool offlineCacheUnavailable,
        double offlineCacheProgress,
        string? episodeSubtitle = null,
        bool overrideEpisodeSubtitle = false)
    {
        return new LibraryVideoItem
        {
            Id = file.Id,
            FileName = file.FileName,
            RelativePath = file.RelativePath,
            MetadataPath = file.MetadataPath,
            AbsolutePath = file.AbsolutePath,
            PlaybackPath = file.PlaybackPath,
            SourceProtocolType = file.SourceProtocolType,
            SourceBasePath = file.SourceBasePath,
            SourceAuthConfig = file.SourceAuthConfig,
            LocalIsoPlaybackPath = file.LocalIsoPlaybackPath,
            OfflineCachePath = offlineCachePath,
            ThumbnailPath = file.ThumbnailPath,
            FallbackImagePath = file.FallbackImagePath,
            PlayProgress = file.PlayProgress,
            Duration = file.Duration,
            LastPlayedAt = file.LastPlayedAt,
            SeasonNumber = file.SeasonNumber,
            EpisodeNumber = file.EpisodeNumber,
            IsTvEpisode = file.IsTvEpisode,
            EpisodeLabel = file.EpisodeLabel,
            EpisodeSubtitle = overrideEpisodeSubtitle ? episodeSubtitle : file.EpisodeSubtitle,
            CustomEpisodeSubtitle = file.CustomEpisodeSubtitle,
            EpisodeYear = file.EpisodeYear,
            CustomThumbnailPath = file.CustomThumbnailPath,
            OfflineCacheIsDownloading = offlineCacheIsDownloading,
            OfflineCacheIsCached = offlineCacheIsCached,
            OfflineCacheUnavailable = offlineCacheUnavailable,
            OfflineCacheProgress = offlineCacheProgress
        };
    }

    private static long GetAvailableFreeBytes(string directory)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directory));
        if (string.IsNullOrWhiteSpace(root))
        {
            return 0;
        }

        return new DriveInfo(root).AvailableFreeSpace;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unitIndex = 0;
        var display = (double)value;
        while (display >= 1024 && unitIndex < units.Length - 1)
        {
            display /= 1024;
            unitIndex++;
        }

        return $"{display:0.##} {units[unitIndex]}";
    }

    private static string SanitizePathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalidChars.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "cache-item" : sanitized;
    }

    private void BuildSeasonState(IReadOnlyList<LibraryVideoItem> files, bool isSeries, int? preservedSelectedSeason = null)
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
        var recentUnfinished = files
            .Where(static file => file.HasProgress && !file.IsWatched)
            .OrderByDescending(static file => file.LastPlayedAt ?? 0)
            .ThenByDescending(static file => file.ProgressRatio)
            .FirstOrDefault();
        var nextUnwatched = files
            .Where(static file => !file.IsWatched)
            .OrderBy(static file => file.SeasonNumber == 0 ? int.MaxValue : file.SeasonNumber)
            .ThenBy(static file => file.EpisodeNumber)
            .ThenBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static file => file.FileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        var nextUp = recentUnfinished ?? nextUnwatched;

        var resolvedSeason = preservedSelectedSeason.HasValue && seasons.Contains(preservedSelectedSeason.Value)
            ? preservedSelectedSeason.Value
            : preferred?.SeasonNumber ?? nextUp?.SeasonNumber ?? seasons[0];

        SelectedSeason = resolvedSeason;
        selectedPlaybackNavigationSeason = preferred?.SeasonNumber ?? nextUp?.SeasonNumber ?? resolvedSeason;
        OnPropertyChanged(nameof(SelectedSeasonOption));
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
            filtered = OrderMovieFilesForPlayback(allDetailFiles);
        }

        filtered = filtered.Select(ApplyOfflineCacheState).ToList();
        ReplaceItems(DetailFiles, filtered);
        ChooseDetailPrimaryFile(filtered);
        RefreshDetailHeaderState();
        OnPropertyChanged(nameof(HasDetailFiles));
        UpdateSelectedSeasonOfflineProgress();
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
        var recentUnfinished = files
            .Where(static file => file.HasProgress && !file.IsWatched)
            .OrderByDescending(static file => file.LastPlayedAt ?? 0)
            .ThenByDescending(static file => file.ProgressRatio)
            .FirstOrDefault();
        var nextUnwatched = files
            .Where(static file => !file.IsWatched)
            .OrderBy(static file => file.SeasonNumber == 0 ? int.MaxValue : file.SeasonNumber)
            .ThenBy(static file => file.EpisodeNumber)
            .FirstOrDefault();

        DetailPrimaryFile = preferred ?? recentUnfinished ?? nextUnwatched ?? files.First();
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

        var currentMoviePart = ResolveCurrentPlaybackMoviePart();
        if (currentMoviePart is not null)
        {
            var previousPart = FindPreviousMoviePart(currentMoviePart);
            var nextPart = FindNextMoviePart(currentMoviePart);
            Player.ConfigurePreviousEpisodeAction(
                previousPart is null ? null : PlayPreviousMoviePartAsync,
                previousPart is null ? null : $"播放上一段 · {BuildMoviePartLabel(previousPart)}");
            Player.ConfigureNextEpisodeAction(
                nextPart is null ? null : PlayNextMoviePartAsync,
                nextPart is null ? null : $"播放下一段 · {BuildMoviePartLabel(nextPart)}");
            return;
        }

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

    private LibraryVideoItem? ResolveCurrentPlaybackMoviePart()
    {
        if (!currentDetailMovieId.HasValue ||
            IsDetailSeries ||
            string.IsNullOrWhiteSpace(currentPlayingVideoId))
        {
            return null;
        }

        var parts = GetOrderedMovieParts();
        return parts.Count > 1
            ? parts.FirstOrDefault(file => string.Equals(file.Id, currentPlayingVideoId, StringComparison.Ordinal))
            : null;
    }

    private List<LibraryVideoItem> GetOrderedMovieParts()
    {
        return OrderMovieFilesForPlayback(allDetailFiles.Where(static file => !file.IsTvEpisode));
    }

    private static List<LibraryVideoItem> OrderMovieFilesForPlayback(IEnumerable<LibraryVideoItem> files)
    {
        var ordered = files.ToList();
        ordered.Sort(CompareMovieFilesForPlayback);
        return ordered;
    }

    private static int CompareMovieFilesForPlayback(LibraryVideoItem left, LibraryVideoItem right)
    {
        var partComparison = ResolveMoviePartSortIndex(left).CompareTo(ResolveMoviePartSortIndex(right));
        if (partComparison != 0)
        {
            return partComparison;
        }

        var pathComparison = StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath);
        if (pathComparison != 0)
        {
            return pathComparison;
        }

        var nameComparison = StringComparer.OrdinalIgnoreCase.Compare(left.FileName, right.FileName);
        return nameComparison != 0
            ? nameComparison
            : StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id);
    }

    private static int ResolveMoviePartSortIndex(LibraryVideoItem file)
    {
        var raw = $"{file.RelativePath}/{file.FileName}";
        var tokens = Regex
            .Split(raw, @"[\\/\s._\-\[\]\(\)\{\}【】（）,:：]+")
            .Where(static token => !string.IsNullOrWhiteSpace(token))
            .ToArray();
        for (var index = 0; index < tokens.Length; index++)
        {
            var rawToken = tokens[index];
            if (TryParseChineseMoviePartToken(rawToken, out var chinesePart))
            {
                return chinesePart;
            }

            var token = rawToken.ToLowerInvariant();
            foreach (var prefix in MoviePartPrefixes)
            {
                if (string.Equals(token, prefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (index + 1 < tokens.Length && TryParseMoviePartToken(tokens[index + 1], out var separatedPart))
                    {
                        return separatedPart;
                    }
                }
                else if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                         TryParseMoviePartToken(token[prefix.Length..], out var joinedPart))
                {
                    return joinedPart;
                }
            }
        }

        return int.MaxValue;
    }

    private static readonly string[] MoviePartPrefixes =
    [
        "volume",
        "part",
        "disc",
        "disk",
        "dvd",
        "vol",
        "pt",
        "cd"
    ];

    private static bool TryParseMoviePartToken(string token, out int partIndex)
    {
        token = token.Trim();
        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out partIndex) && partIndex > 0)
        {
            return true;
        }

        if (TryParseChineseMoviePartToken(token, out partIndex))
        {
            return true;
        }

        return TryParseRomanNumber(token, out partIndex);
    }

    private static bool TryParseChineseMoviePartToken(string token, out int partIndex)
    {
        partIndex = token switch
        {
            "上" or "上半" or "上篇" or "前篇" => 1,
            "下" or "下半" or "下篇" or "后篇" or "後篇" => 2,
            _ => 0
        };
        return partIndex > 0;
    }

    private static bool TryParseRomanNumber(string token, out int value)
    {
        value = 0;
        token = token.Trim().ToUpperInvariant();
        if (token.Length == 0 || token.Any(static ch => ch is not ('I' or 'V' or 'X' or 'L' or 'C' or 'D' or 'M')))
        {
            return false;
        }

        for (var index = 0; index < token.Length; index++)
        {
            var current = RomanDigitValue(token[index]);
            var next = index + 1 < token.Length ? RomanDigitValue(token[index + 1]) : 0;
            value += current < next ? -current : current;
        }

        return value > 0;
    }

    private static int RomanDigitValue(char ch)
    {
        return ch switch
        {
            'I' => 1,
            'V' => 5,
            'X' => 10,
            'L' => 50,
            'C' => 100,
            'D' => 500,
            'M' => 1000,
            _ => 0
        };
    }

    private LibraryVideoItem? FindPreviousMoviePart(LibraryVideoItem currentPart)
    {
        var parts = GetOrderedMovieParts();
        var currentIndex = parts.FindIndex(file => string.Equals(file.Id, currentPart.Id, StringComparison.Ordinal));
        return currentIndex > 0 ? parts[currentIndex - 1] : null;
    }

    private LibraryVideoItem? FindNextMoviePart(LibraryVideoItem currentPart)
    {
        var parts = GetOrderedMovieParts();
        var currentIndex = parts.FindIndex(file => string.Equals(file.Id, currentPart.Id, StringComparison.Ordinal));
        return currentIndex >= 0 && currentIndex + 1 < parts.Count ? parts[currentIndex + 1] : null;
    }

    private static string BuildMoviePartLabel(LibraryVideoItem part)
    {
        return string.IsNullOrWhiteSpace(part.RelativePath)
            ? part.FileName
            : part.RelativePath;
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

    private async Task PlayPreviousMoviePartAsync()
    {
        var currentPart = ResolveCurrentPlaybackMoviePart();
        var previousPart = currentPart is null ? null : FindPreviousMoviePart(currentPart);
        if (currentPart is null || previousPart is null)
        {
            UpdatePlayerNextEpisodeAction();
            return;
        }

        if (!MediaSourcePathResolver.IsPlayableLocation(previousPart.EffectivePlaybackPath))
        {
            StatusMessage = $"上一段文件不可播放：{previousPart.FileName}";
            return;
        }

        await TransitionToMoviePartAsync(previousPart);
    }

    private async Task PlayNextMoviePartAsync()
    {
        var currentPart = ResolveCurrentPlaybackMoviePart();
        var nextPart = currentPart is null ? null : FindNextMoviePart(currentPart);
        if (currentPart is null || nextPart is null)
        {
            UpdatePlayerNextEpisodeAction();
            return;
        }

        if (!MediaSourcePathResolver.IsPlayableLocation(nextPart.EffectivePlaybackPath))
        {
            StatusMessage = $"下一段文件不可播放：{nextPart.FileName}";
            return;
        }

        await TransitionToMoviePartAsync(nextPart, markCurrentAsFinished: true);
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

        await TransitionToEpisodeAsync(nextEpisode, markCurrentAsFinished: true);
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

    private async Task TransitionToMoviePartAsync(
        LibraryVideoItem targetPart,
        bool markCurrentAsFinished = false,
        double? startPositionSeconds = null)
    {
        if (!MediaSourcePathResolver.IsPlayableLocation(targetPart.EffectivePlaybackPath))
        {
            StatusMessage = $"文件不可播放：{targetPart.FileName}";
            return;
        }

        if (string.Equals(currentPlayingVideoId, targetPart.Id, StringComparison.Ordinal))
        {
            ApplyPreferredDetailVideoSelection(targetPart);
            if (startPositionSeconds.HasValue)
            {
                await Player.CommitSeekAsync(startPositionSeconds.Value);
            }
            return;
        }

        var currentPart = ResolveCurrentPlaybackMoviePart();
        if (currentPart is not null)
        {
            StopPlaybackProgressSync();
            await PersistTransitionPlaybackStateAsync(currentPart, markAsFinished: markCurrentAsFinished);
            suppressedPlaybackPersistenceVideoId =
                currentPlaybackMode == PlaybackMode.Overlay
                    ? currentPart.Id
                    : null;
        }
        else
        {
            suppressedPlaybackPersistenceVideoId = null;
        }

        preferredDetailVideoId = targetPart.Id;
        await RefreshDetailFilesAsync();
        await RefreshContinueWatchingAsync();

        var resolvedTargetPart = ResolveDetailVideo(targetPart.Id) ?? targetPart;
        ApplyPreferredDetailVideoSelection(resolvedTargetPart);

        switch (currentPlaybackMode)
        {
            case PlaybackMode.Overlay:
                await PlayVideoWithStartPositionAsync(resolvedTargetPart, startPositionSeconds);
                break;
            case PlaybackMode.Standalone:
                await OpenStandalonePlayerInternalAsync(
                    resolvedTargetPart,
                    replaceCurrentSession: true,
                    persistCurrentPlaybackState: false);
                break;
            default:
                suppressedPlaybackPersistenceVideoId = null;
                UpdatePlayerNextEpisodeAction();
                break;
        }
    }

    private async Task TransitionToEpisodeAsync(LibraryVideoItem targetEpisode, bool markCurrentAsFinished = false)
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
            await PersistTransitionPlaybackStateAsync(currentEpisode, markAsFinished: markCurrentAsFinished);
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

    private async Task PersistTransitionPlaybackStateAsync(LibraryVideoItem episode, bool markAsFinished = false)
    {
        var duration = Math.Max(episode.Duration, Player.DurationSeconds);
        var progress = Math.Max(episode.PlayProgress, Player.CurrentPositionSeconds);
        var shouldMarkWatched = markAsFinished || episode.IsWatched || Player.PlaybackProgressRatio >= PlaybackProgressRules.CompletionRatio;
        if (shouldMarkWatched)
        {
            duration = Math.Max(duration, Math.Max(progress, 100));
        }

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
        isAutoAdvancingToNextMoviePart = false;
    }

    private bool IsDetailMultipartMovie => currentDetailMovieId.HasValue && !IsDetailSeries && GetOrderedMovieParts().Count > 1;

    private bool IsPlayingMultipartMovie => Player.IsPlaying && ResolveCurrentPlaybackMoviePart() is not null;

    private (double PositionSeconds, double DurationSeconds) ResolveMultipartSavedTimeline()
    {
        var position = 0d;
        var duration = 0d;
        foreach (var part in GetOrderedMovieParts())
        {
            if (part.Duration <= 0)
            {
                continue;
            }

            duration += part.Duration;
            position += part.IsWatched
                ? part.Duration
                : Math.Min(Math.Max(part.PlayProgress, 0), part.Duration);
        }

        return (position, duration);
    }

    private (double PositionSeconds, double DurationSeconds) ResolvePlayerTimeline()
    {
        if (!IsPlayingMultipartMovie)
        {
            return (Math.Max(Player.CurrentPositionSeconds, 0), Math.Max(Player.DurationSeconds, 0));
        }

        var parts = GetOrderedMovieParts();
        var currentIndex = parts.FindIndex(part => string.Equals(part.Id, currentPlayingVideoId, StringComparison.Ordinal));
        if (currentIndex < 0)
        {
            return (Math.Max(Player.CurrentPositionSeconds, 0), Math.Max(Player.DurationSeconds, 0));
        }

        var position = 0d;
        var duration = 0d;
        for (var index = 0; index < parts.Count; index++)
        {
            var partDuration = index == currentIndex
                ? Math.Max(parts[index].Duration, Math.Max(Player.DurationSeconds, Player.CurrentPositionSeconds))
                : parts[index].Duration;
            partDuration = Math.Max(partDuration, 0);
            if (index < currentIndex)
            {
                position += partDuration;
            }
            duration += partDuration;
        }

        position += Math.Max(Player.CurrentPositionSeconds, 0);
        return (Math.Min(position, Math.Max(duration, position)), duration);
    }

    private IReadOnlyList<double> ResolvePlayerTimelineSegmentBoundaries()
    {
        if (!IsPlayingMultipartMovie)
        {
            return [];
        }

        var parts = GetOrderedMovieParts();
        var currentIndex = parts.FindIndex(part => string.Equals(part.Id, currentPlayingVideoId, StringComparison.Ordinal));
        if (parts.Count <= 1 || currentIndex < 0)
        {
            return [];
        }

        var durations = parts
            .Select((part, index) => index == currentIndex
                ? Math.Max(part.Duration, Math.Max(Player.DurationSeconds, Player.CurrentPositionSeconds))
                : Math.Max(part.Duration, 0))
            .ToArray();
        var totalDuration = durations.Sum();
        if (totalDuration <= 0)
        {
            return [];
        }

        var boundaries = new List<double>(Math.Max(parts.Count - 1, 0));
        var cursor = 0d;
        for (var index = 0; index < durations.Length - 1; index++)
        {
            cursor += durations[index];
            if (cursor > 0 && cursor < totalDuration)
            {
                boundaries.Add(cursor / totalDuration);
            }
        }

        return boundaries;
    }

    private double ClampPlayerTimelinePosition(double value)
    {
        var duration = PlayerTimelineDurationSeconds;
        if (duration <= 0)
        {
            return Math.Max(value, 0);
        }

        return Math.Clamp(value, 0, duration);
    }

    private (LibraryVideoItem Part, double LocalPositionSeconds)? ResolveMultipartSeekTarget(double targetSeconds)
    {
        var parts = GetOrderedMovieParts();
        var currentIndex = parts.FindIndex(part => string.Equals(part.Id, currentPlayingVideoId, StringComparison.Ordinal));
        var cursor = 0d;
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            var duration = index == currentIndex
                ? Math.Max(part.Duration, Math.Max(Player.DurationSeconds, Player.CurrentPositionSeconds))
                : part.Duration;
            duration = Math.Max(duration, 0);
            if (duration <= 0)
            {
                continue;
            }

            if (targetSeconds <= cursor + duration || index == parts.Count - 1)
            {
                return (part, Math.Clamp(targetSeconds - cursor, 0, duration));
            }

            cursor += duration;
        }

        var fallback = parts.LastOrDefault();
        return fallback is null ? null : (fallback, 0);
    }

    private static string FormatPlaybackTime(double seconds)
    {
        var safeSeconds = Math.Max(0, (int)Math.Floor(double.IsFinite(seconds) ? seconds : 0));
        return TimeSpan.FromSeconds(safeSeconds).ToString(safeSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
    }

    private void NotifyPlayerTimelineChanged()
    {
        OnPropertyChanged(nameof(PlayerTimelineDurationSeconds));
        OnPropertyChanged(nameof(PlayerTimelineSeekPositionSeconds));
        OnPropertyChanged(nameof(PlayerTimelineSegmentBoundaries));
        OnPropertyChanged(nameof(PlayerTimelineCurrentPositionText));
        OnPropertyChanged(nameof(PlayerTimelineRemainingTimeText));
        PlayerTimelineSeekBackwardCommand.NotifyCanExecuteChanged();
        PlayerTimelineSeekForwardCommand.NotifyCanExecuteChanged();
    }

    public void BeginPlayerTimelineSeekInteraction()
    {
        if (!IsPlayingMultipartMovie)
        {
            Player.BeginSeekInteraction();
            return;
        }

        isMultipartTimelineSeekPreviewActive = true;
        multipartTimelineSeekPreviewSeconds = ResolvePlayerTimeline().PositionSeconds;
        NotifyPlayerTimelineChanged();
    }

    public void UpdatePlayerTimelineSeekPreview(double value)
    {
        if (!IsPlayingMultipartMovie)
        {
            Player.UpdateSeekPreview(value);
            return;
        }

        if (!isMultipartTimelineSeekPreviewActive)
        {
            return;
        }

        multipartTimelineSeekPreviewSeconds = ClampPlayerTimelinePosition(value);
        NotifyPlayerTimelineChanged();
    }

    public async Task CommitPlayerTimelineSeekAsync(double value)
    {
        if (!IsPlayingMultipartMovie)
        {
            await Player.CommitSeekAsync(value);
            return;
        }

        var target = ResolveMultipartSeekTarget(ClampPlayerTimelinePosition(value));
        isMultipartTimelineSeekPreviewActive = false;
        if (target is null)
        {
            NotifyPlayerTimelineChanged();
            return;
        }

        if (string.Equals(target.Value.Part.Id, currentPlayingVideoId, StringComparison.Ordinal))
        {
            await Player.CommitSeekAsync(target.Value.LocalPositionSeconds);
        }
        else
        {
            await TransitionToMoviePartAsync(
                target.Value.Part,
                markCurrentAsFinished: IsSeekTargetAfterCurrentPart(target.Value.Part),
                startPositionSeconds: target.Value.LocalPositionSeconds);
        }

        NotifyPlayerTimelineChanged();
    }

    private async Task SeekPlayerTimelineRelativeAsync(double deltaSeconds)
    {
        if (!IsPlayingMultipartMovie)
        {
            if (deltaSeconds < 0)
            {
                await Player.SeekBackwardCommand.ExecuteAsync(null);
            }
            else
            {
                await Player.SeekForwardCommand.ExecuteAsync(null);
            }
            return;
        }

        var timeline = ResolvePlayerTimeline();
        await CommitPlayerTimelineSeekAsync(Math.Clamp(timeline.PositionSeconds + deltaSeconds, 0, timeline.DurationSeconds));
    }

    private bool CanSeekPlayerTimeline()
    {
        return Player.IsPlaying && Player.IsAvailable && PlayerTimelineDurationSeconds > 0;
    }

    private bool IsSeekTargetAfterCurrentPart(LibraryVideoItem targetPart)
    {
        var parts = GetOrderedMovieParts();
        var currentIndex = parts.FindIndex(part => string.Equals(part.Id, currentPlayingVideoId, StringComparison.Ordinal));
        var targetIndex = parts.FindIndex(part => string.Equals(part.Id, targetPart.Id, StringComparison.Ordinal));
        return currentIndex >= 0 && targetIndex > currentIndex;
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
        var multipartTimeline = IsDetailMultipartMovie ? ResolveMultipartSavedTimeline() : default;

        DetailPrimaryProgressRatio = detailProgressRatio;
        DetailPlaybackProgressRatio = IsDetailMultipartMovie ? detailProgressRatio : DetailPrimaryFile.ProgressRatio;
        DetailPrimaryFileProgressText = IsDetailMultipartMovie
            ? FormatWatchStateSummary(detailWatchState, detailProgressRatio)
            : DetailPrimaryFile.ProgressText;
        DetailPrimaryTimeText = IsDetailMultipartMovie && multipartTimeline.DurationSeconds > 0
            ? $"{FormatPlaybackTime(multipartTimeline.PositionSeconds)} / {FormatPlaybackTime(multipartTimeline.DurationSeconds)}"
            : $"{DetailPrimaryFile.PositionText} / {DetailPrimaryFile.DurationText}";
        DetailPrimaryActionText = BuildPrimaryActionText(DetailPrimaryFile);
        DetailProgressSummary = FormatWatchStateSummary(detailWatchState, detailProgressRatio);

        DetailSelectionHint = DetailPrimaryFile.IsTvEpisode
            ? $"\u5F53\u524D\u4E3B\u96C6\uFF1A{FormatEpisodeTitle(DetailPrimaryFile)}"
            : IsDetailMultipartMovie
                ? $"多段电影 · 当前分段：{BuildMoviePartLabel(DetailPrimaryFile)}"
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

        if (IsDetailMultipartMovie)
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

        if (IsDetailMultipartMovie)
        {
            var timeline = ResolveMultipartSavedTimeline();
            return timeline.DurationSeconds > 0
                ? Math.Clamp(timeline.PositionSeconds / timeline.DurationSeconds, 0, 1)
                : files.Max(static file => file.ProgressRatio);
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
        return file.EpisodeDisplayTitle;
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

    private async Task RefreshIndexedScanItemArtworkAsync(
        LibraryScanIndexedItem item,
        CancellationToken cancellationToken)
    {
        var tmdbSettings = Settings.BuildTmdbSettings();
        await ReloadLibraryAsync();

        if (!tmdbSettings.EnableMetadataEnrichment && !tmdbSettings.EnablePosterDownloads)
        {
            return;
        }

        await libraryRefreshGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var summary = item.IsTvShow
                ? await libraryMetadataEnricher.EnrichMissingTvShowMetadataAsync(
                    item.Id,
                    tmdbSettings,
                    cancellationToken)
                : await libraryMetadataEnricher.EnrichMissingMovieMetadataAsync(
                    item.Id,
                    tmdbSettings,
                    cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            await ReloadLibraryAsync();
            await RefreshOpenDetailIfNeededAsync();

            if (summary.EncounteredNetworkError)
            {
                StatusMessage = string.IsNullOrWhiteSpace(summary.ErrorMessage)
                    ? "自动刮削失败：无法连接网络或 TMDB。"
                    : $"自动刮削失败：{summary.ErrorMessage}";
            }
        }
        finally
        {
            libraryRefreshGate.Release();
        }
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
            var tvShowMetadataQueue = allTvShows
                .Where(show => NeedsTvShowRefresh(show, tmdbSettings))
                .Select(show => (show.Id, show.Title))
                .ToList();
            var tvShowThumbnailQueue = shouldRefreshThumbnails
                ? BuildTvShowThumbnailQueueInHomeOrder()
                : new List<(long Id, string Title)>();
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

            foreach (var show in tvShowMetadataQueue)
            {
                cancellationToken.ThrowIfCancellationRequested();
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

            foreach (var show in tvShowThumbnailQueue)
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

    private sealed class LibraryScanAggregate
    {
        private readonly List<string> diagnostics = [];

        public int SourceCount { get; private set; }

        public int NewMovieCount { get; private set; }

        public int NewVideoFileCount { get; private set; }

        public int RemovedVideoFileCount { get; private set; }

        public int NewTvShowCount { get; private set; }

        public void Add(LibraryScanSummary summary)
        {
            SourceCount += summary.SourceCount;
            NewMovieCount += summary.NewMovieCount;
            NewVideoFileCount += summary.NewVideoFileCount;
            RemovedVideoFileCount += summary.RemovedVideoFileCount;
            NewTvShowCount += summary.NewTvShowCount;
            diagnostics.AddRange(summary.Diagnostics);
        }

        public LibraryScanSummary ToSummary()
        {
            return new LibraryScanSummary(
                SourceCount,
                NewMovieCount,
                NewVideoFileCount,
                RemovedVideoFileCount,
                NewTvShowCount,
                diagnostics.ToList());
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
        return settings.EnableEpisodeThumbnailDownloads && allTvShows.Count > 0 && forceThumbnails;
    }

    private List<(long Id, string Title)> BuildTvShowThumbnailQueueInHomeOrder()
    {
        var showsById = allTvShows.ToDictionary(static show => show.Id);
        var queuedIds = new HashSet<long>();
        var queue = new List<(long Id, string Title)>();

        foreach (var item in LibraryItems)
        {
            if (!item.TvShowId.HasValue ||
                !queuedIds.Add(item.TvShowId.Value) ||
                !showsById.TryGetValue(item.TvShowId.Value, out var show))
            {
                continue;
            }

            queue.Add((show.Id, show.Title));
        }

        foreach (var show in allTvShows.OrderBy(static show => show.Title, StringComparer.OrdinalIgnoreCase))
        {
            if (queuedIds.Add(show.Id))
            {
                queue.Add((show.Id, show.Title));
            }
        }

        return queue;
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

    private void OnLuckyStunAddressUpdated(object? sender, EventArgs e)
    {
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
