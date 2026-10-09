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
using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace OmniPlay.Core.ViewModels.Library;

public partial class PosterWallViewModel : ObservableObject
{

    private static readonly string[] MoviePartPrefixes = new[] { "volume", "part", "disc", "disk", "dvd", "vol", "pt", "cd" };

    private bool IsDetailMultipartMovie => currentDetailMovieId.HasValue && !IsDetailSeries && GetOrderedMovieParts().Count > 1;

    private bool IsPlayingMultipartMovie => Player.IsPlaying && ResolveCurrentPlaybackMoviePart() != null;

    private sealed record OfflineCacheFilePlan(LibraryVideoItem File, string? SourcePath, Uri? RemoteUri, string? RemoteAuthorization, string DestinationPath, long FileSize);

    private sealed record TvShowHomeGroup(TvShow Representative, IReadOnlyList<TvShow> Shows);

    private sealed record PlexPinSession(string Id, string Code, string AuthorizationUrl);

    private static class PlexPinAuthClient
    {
        private static readonly HttpClient Client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12L)
        };

        private static string ClientIdentifier
        {
            get
            {
                string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmniPlay");
                string path = Path.Combine(text, "plex-client-id.txt");
                try
                {
                    if (File.Exists(path))
                    {
                        string text2 = File.ReadAllText(path).Trim();
                        if (!string.IsNullOrWhiteSpace(text2))
                        {
                            return text2;
                        }
                    }
                    Directory.CreateDirectory(text);
                    string text3 = $"omniplay-windows-{Guid.NewGuid():N}";
                    File.WriteAllText(path, text3);
                    return text3;
                }
                catch
                {
                    return "omniplay-windows";
                }
            }
        }

        public static async Task<PlexPinSession> CreateAsync(CancellationToken cancellationToken)
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "https://plex.tv/api/v2/pins");
            ApplyHeaders(request);
            using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            PlexPinSession result;
            await using (Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                using JsonDocument document = await JsonDocument.ParseAsync(stream, default(JsonDocumentOptions), cancellationToken);
                JsonElement root = document.RootElement;
                string id = ReadJsonString(root, "id");
                string code = ReadJsonString(root, "code");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(code))
                {
                    throw new InvalidOperationException("Plex 授权响应无效。");
                }
                result = new PlexPinSession(id, code, BuildAuthorizationUrl(code));
            }
            return result;
        }

        public static async Task<string> WaitForTokenAsync(PlexPinSession session, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1L), cancellationToken);
                string token = await TryReadTokenAsync(session, cancellationToken);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    return token;
                }
            }
            throw new OperationCanceledException(cancellationToken);
        }

        private static async Task<string?> TryReadTokenAsync(PlexPinSession session, CancellationToken cancellationToken)
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "https://plex.tv/api/v2/pins/" + Uri.EscapeDataString(session.Id));
            ApplyHeaders(request);
            using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            string result;
            await using (Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                using JsonDocument document = await JsonDocument.ParseAsync(stream, default(JsonDocumentOptions), cancellationToken);
                result = ReadJsonString(document.RootElement, "authToken");
            }
            return result;
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
            return "https://plex.tv/link/?pin=" + Uri.EscapeDataString(code);
        }

        private static string? ReadJsonString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                return null;
            }
            JsonValueKind valueKind = value.ValueKind;
            if (1 == 0)
            {
            }
            string result = valueKind switch
            {
                JsonValueKind.String => value.GetString(), 
                JsonValueKind.Number => value.GetRawText(), 
                _ => null, 
            };
            if (1 == 0)
            {
            }
            return result;
        }
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

        public bool HasChanges => UpdatedMovieCount > 0 || UpdatedTvShowCount > 0 || DownloadedPosterCount > 0 || DownloadedThumbnailCount > 0;

        public void AddMetadata(LibraryMetadataEnrichmentSummary summary)
        {
            UpdatedMovieCount += summary.UpdatedMovieCount;
            UpdatedTvShowCount += summary.UpdatedTvShowCount;
            DownloadedPosterCount += summary.DownloadedPosterCount;
            MetadataNetworkError |= summary.EncounteredNetworkError;
            if (MetadataErrorMessage == null)
            {
                string text = (MetadataErrorMessage = summary.ErrorMessage);
            }
        }

        public void AddThumbnails(LibraryThumbnailEnrichmentSummary summary)
        {
            DownloadedThumbnailCount += summary.DownloadedThumbnailCount;
            ThumbnailNetworkError |= summary.EncounteredNetworkError;
            if (ThumbnailErrorMessage == null)
            {
                string text = (ThumbnailErrorMessage = summary.ErrorMessage);
            }
        }
    }

    private sealed class LibraryScanAggregate
    {
        private readonly List<string> diagnostics = new List<string>();

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
            return new LibraryScanSummary(SourceCount, NewMovieCount, NewVideoFileCount, RemovedVideoFileCount, NewTvShowCount, diagnostics.ToList());
        }
    }

    private enum PlaybackMode
    {
        None,
        Overlay,
        Standalone
    }

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
    private readonly ILuckyStunClient? luckyStunClient;
    private readonly IDoubanMetadataService? doubanMetadataService;
    private readonly IExternalLinkOpener? externalLinkOpener;
    private readonly IPlaybackLauncher playbackLauncher;
    private readonly ICategoryFolderStore? categoryFolderStore;
    private Dictionary<string, CategoryFolder> homeCategoryFolders = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, PersonProfile> homePersonProfiles = new(StringComparer.OrdinalIgnoreCase);
    private bool homeFolderPosterMigrationAttempted;
    /// <summary>没有导演也没有主演的条目归入该分组（对齐 mac 版「未标注」）。</summary>
    private const string UnknownFolderName = "unknown";

    private const string UnknownFolderDisplayName = "\u672A\u6807\u6CE8";

    /// <summary>本轮会话里每个分类文件夹选中的随机封面，避免每次刷新都换一张。</summary>
    private readonly Dictionary<string, string> homeFolderRandomPosters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>切换或关闭文件夹时递增，用于丢弃过期的作品列表回填结果。</summary>
    private int openFolderBioGeneration;
    private readonly List<Movie> allMovies = [];
    private readonly List<TvShow> allTvShows = [];
    private readonly List<LibraryPosterItem> allLibraryItems = [];
    private readonly List<LibraryVideoItem> allDetailFiles = [];
    private readonly HashSet<string> knownLibraryPosterIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> newlyDiscoveredPosterOrder = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim libraryRefreshGate = new(1, 1);
    private CancellationTokenSource? libraryAutomationCancellationTokenSource;
    private CancellationTokenSource? playbackProgressSyncCancellationTokenSource;
    private CancellationTokenSource? offlineCacheCancellationTokenSource;
    private bool isOfflineCacheCancellationRequested;
    private int offlineCacheBatchTotalCount;
    private int offlineCacheBatchCompletedCount;
    private string? offlineCacheBatchCurrentFileId;
    private string? offlineCacheBatchCurrentFileName;
    private string? offlineCacheActiveTitle;
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
        IDoubanMetadataService? doubanMetadataService = null,
        ILuckyStunClient? luckyStunClient = null,
        IExternalLinkOpener? externalLinkOpener = null,
        ICategoryFolderStore? categoryFolderStore = null)
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
        this.doubanMetadataService = doubanMetadataService;
        this.luckyStunClient = luckyStunClient;
        this.externalLinkOpener = externalLinkOpener;
        this.playbackLauncher = playbackLauncher;
        this.categoryFolderStore = categoryFolderStore;

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
        OpenLuckyStunSourcePanelCommand = new RelayCommand(OpenLuckyStunSourcePanel, () => !IsBusy || IsLibraryScanInProgress);
        CloseLuckyStunSourcePanelCommand = new RelayCommand(CloseLuckyStunSourcePanel, () => !IsBusy);
        PreScanMediaServerSourceCommand = new AsyncRelayCommand(PreScanMediaServerSourceAsync, () => IsMediaServerPanelOpen && !IsBusy);
        AuthorizePlexCommand = new AsyncRelayCommand(AuthorizePlexAsync, () => IsMediaServerPanelOpen && !IsBusy && !IsPlexAuthorizationInProgress && PendingMediaServerUsesPlex);
        TestPendingLuckyStunLoginCommand = new AsyncRelayCommand(TestPendingLuckyStunLoginAsync, () => (IsMediaServerPanelOpen || IsLuckyStunSourcePanelOpen) && !IsBusy && !IsPendingLuckyStunLoginInProgress);
        SavePendingLuckyStunSourceCommand = new AsyncRelayCommand(SavePendingLuckyStunSourceAsync, () => IsMediaServerPanelOpen && !IsBusy);
        SaveLuckyStunSourceCommand = new AsyncRelayCommand(SaveLuckyStunSourceAsync, () => IsLuckyStunSourcePanelOpen && !IsBusy && LuckyStunSourceSaveEnabled);
        TestLuckyStunWebDavSourceCommand = new AsyncRelayCommand(TestLuckyStunWebDavSourceAsync, () => IsLuckyStunSourcePanelOpen && LuckyStunSourceUsesWebDav && !IsBusy && !string.IsNullOrWhiteSpace(PendingLuckyStunRuleId));
        RefreshLuckyStunSourceCommand = new AsyncRelayCommand<MediaSource?>(RefreshLuckyStunSourceAsync, source => source is not null && !IsBusy);
        EditLuckyStunSourceCommand = new RelayCommand<MediaSource?>(EditLuckyStunSource, source => source is not null && !IsBusy);
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
        KeepOfflineCacheCommand = new RelayCommand(KeepOfflineCache, () => IsCancelOfflineCacheConfirmationOpen);
        ConfirmCancelOfflineCacheCommand = new RelayCommand(ConfirmCancelOfflineCache, () => IsCancelOfflineCacheConfirmationOpen);
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
        OpenPosterDoubanSearchCommand = new RelayCommand(OpenPosterDoubanSearch, () => posterMetadataTarget is not null && !IsBusy);
        UnbindPosterDoubanCommand = new AsyncRelayCommand(UnbindPosterDoubanAsync, () => hasPosterDoubanBinding && !IsBusy);
        SavePosterMetadataEditCommand = new AsyncRelayCommand(SavePosterMetadataEditAsync, () => posterMetadataTarget is not null && !IsBusy);
        OpenEpisodeEditCommand = new RelayCommand<LibraryVideoItem?>(OpenEpisodeEdit, video => video is not null && IsDetailSeries);
        CloseEpisodeEditCommand = new RelayCommand(CloseEpisodeEdit);
        SaveEpisodeEditCommand = new AsyncRelayCommand(SaveEpisodeEditAsync, () => episodeEditTarget is not null && !IsBusy);
        ChooseEpisodeThumbnailImageCommand = new AsyncRelayCommand(ChooseEpisodeThumbnailImageAsync, () => isEpisodeEditPanelOpen && !IsBusy);
        EditSourceCommand = new AsyncRelayCommand<MediaSource?>(EditSourceAsync, source => source is not null && !IsBusy);
        CancelWebDavEditCommand = new RelayCommand(CancelWebDavEdit, () => IsEditingWebDavSource && !IsBusy);
        RemoveSourceCommand = new AsyncRelayCommand<MediaSource?>(RemoveSourceAsync, source => source?.Id is not null);
        OpenRemoveSourceConfirmationCommand = new RelayCommand<MediaSource?>(OpenRemoveSourceConfirmation, source => source?.Id is not null && !IsRemoveSourceConfirmationOpen);
        ConfirmRemoveSourceCommand = new AsyncRelayCommand(ConfirmRemoveSourceAsync, () => IsRemoveSourceConfirmationOpen);
        CancelRemoveSourceConfirmationCommand = new RelayCommand(CancelRemoveSourceConfirmation, () => IsRemoveSourceConfirmationOpen);
        ToggleSourceEnabledCommand = new AsyncRelayCommand<MediaSource?>(
            ToggleSourceEnabledAsync,
            source => source?.Id is not null && (!IsBusy || source.IsEnabled));
        CheckSourceConnectivityCommand = new AsyncRelayCommand<MediaSource?>(
            CheckSourceConnectivityAsync,
            source => source?.Id is not null && !IsBusy);
        RefreshAndCheckLuckyStunSourceCommand = new AsyncRelayCommand<MediaSource?>(
            RefreshAndCheckLuckyStunSourceAsync,
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
        ToggleLuckyStunPasswordVisibilityCommand = new RelayCommand(ToggleLuckyStunPasswordVisibility);
        TogglePendingLuckyStunPasswordVisibilityCommand = new RelayCommand(TogglePendingLuckyStunPasswordVisibility);
        MountNetworkFolderCommand = new AsyncRelayCommand<NetworkShareFolderItem?>(MountNetworkFolderAsync, folder => folder is not null && !IsBusy);
        ToggleNetworkFolderStarCommand = new RelayCommand<NetworkShareFolderItem?>(ToggleNetworkFolderStar, folder => folder is not null && !IsBusy);
        MountStarredNetworkFoldersCommand = new AsyncRelayCommand(MountStarredNetworkFoldersAsync, () => HasStarredNetworkShareFolders && !IsBusy);
        SelectSortOptionCommand = new RelayCommand<LibrarySortOptionItem?>(SelectSortOption);
        ToggleSortDirectionCommand = new RelayCommand(ToggleSortDirection);
        ToggleHomeCategoryMenuCommand = new RelayCommand(ToggleHomeCategoryMenu);
        SelectHomeCategoryCommand = new AsyncRelayCommand<string?>(SelectHomeCategoryAsync, mode => mode is not null);
        CloseHomeFolderCommand = new RelayCommand(CloseHomeFolder);
        EditHomeFolderPosterCommand = new AsyncRelayCommand<LibraryPosterItem?>(EditHomeFolderPosterAsync, item => item is { IsFolder: true } && !IsBusy);
        CloseFolderPosterEditorCommand = new RelayCommand(CloseFolderPosterEditor);
        RenameFolderPersonCommand = new AsyncRelayCommand(RenameFolderPersonAsync, () => !IsBusy && IsFolderPersonRenameVisible);
        ChooseFolderLocalPosterCommand = new AsyncRelayCommand(ChooseFolderLocalPosterAsync, () => !IsBusy && !string.IsNullOrEmpty(FolderPosterEditorFolderKey));
        SelectFolderPosterCommand = new AsyncRelayCommand<FolderPosterCandidate?>(SelectFolderPosterAsync, candidate => candidate is not null && !IsBusy);
        ResetFolderPosterCommand = new AsyncRelayCommand(ResetFolderPosterAsync, () => !IsBusy && !string.IsNullOrEmpty(FolderPosterEditorFolderKey));
        SelectSeasonCommand = new RelayCommand<int?>(SelectSeason);
        ToggleSearchPopupCommand = new RelayCommand(ToggleSearchPopup);
        ToggleSortPopupCommand = new RelayCommand(ToggleSortPopup);
        ToggleSourcePopupCommand = new RelayCommand(ToggleSourcePopup);
        ToggleSettingsPopupCommand = new RelayCommand(ToggleSettingsPopup);
        CloseDetailCommand = new RelayCommand(CloseDetail);
        ClosePlayerOverlayCommand = new AsyncRelayCommand(ClosePlayerOverlayAsync, () => IsPlayerOverlayOpen);

        Settings.SettingsSaved += OnSettingsSaved;
        Settings.PropertyChanged += OnSettingsPropertyChanged;
        HomeCategoryMode = NormalizeHomeCategoryModeChoice(Settings.HomeCategoryMode);
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

    public ObservableCollection<DetailVersionItem> DetailVersions { get; } = [];

    public ObservableCollection<LibraryMetadataSearchCandidate> DetailMetadataCandidates { get; } = [];

    public ObservableCollection<LibraryMetadataSearchCandidate> PosterMetadataCandidates { get; } = [];

    public ObservableCollection<int> AvailableSeasons { get; } = [];


    public ObservableCollection<ToastItem> Toasts { get; } = [];


    public IReadOnlyList<int> SkeletonItems { get; } = Enumerable.Range(0, 12).ToArray();

    public IReadOnlyList<LibrarySortOptionItem> SortOptions { get; } =
    [
        new(LibrarySortOption.Title, "\u6309\u6807\u9898"),
        new(LibrarySortOption.Year, "\u6309\u5E74\u4EFD"),
        new(LibrarySortOption.Rating, "\u6309\u8BC4\u5206")
    ];


        public bool IsHomeFolderOpen => !string.IsNullOrEmpty(OpenFolderKey);

        public string OpenFolderDisplayName
        {
            get
            {
                if (string.IsNullOrEmpty(OpenFolderKey))
                {
                    return string.Empty;
                }

                (string mode, string name) = SplitFolderKey(OpenFolderKey);
                return name == UnknownFolderName ? UnknownFolderDisplayName : name;
            }
        }

        public bool IsHomeCategoryNoneSelected => HomeCategoryMode == "none";

        public bool IsHomeCategoryDirectorSelected => HomeCategoryMode == "director";

        public bool IsHomeCategoryActorSelected => HomeCategoryMode == "actor";

        public bool IsHomeCategoryModeActive => HomeCategoryMode is "director" or "actor";


    public IReadOnlyList<SettingsOptionItem> MediaServerProtocolOptions { get; } =
    [
        new("webdav", "WebDAV"),
        new("plex", "Plex"),
        new("emby", "Emby"),
        new("jellyfin", "Jellyfin")
    ];

    public string PendingMediaServerAddressWatermark => NormalizeMediaServerProtocolType(PendingMediaServerProtocolType) switch
    {
        "plex" => "Plex 地址，例如 http://127.0.0.1:32400",
        "emby" => "Emby 地址，例如 http://127.0.0.1:8096",
        "jellyfin" => "Jellyfin 地址，例如 http://127.0.0.1:8096",
        "webdav" => "WebDAV 目录地址，例如 http://192.168.1.10:8080/media",
        _ => "服务器地址，例如 http://127.0.0.1:8096"
    };

    public string PendingMediaServerUserWatermark => NormalizeMediaServerProtocolType(PendingMediaServerProtocolType) == "webdav" ? "用户名（可选）" : "用户名 / 用户 ID（可选）";

    public string PendingMediaServerTokenWatermark => NormalizeMediaServerProtocolType(PendingMediaServerProtocolType) switch
    {
        "plex" => "Plex 访问令牌（X-Plex-Token）",
        "webdav" => "密码（可选）",
        _ => "密码 / API Key / 访问令牌"
    };

    private static string LuckyStunProtocolType(SettingsOptionItem? option) => option?.Value ?? string.Empty;

    public bool LuckyStunSourceUsesWebDav => LuckyStunProtocolType(LuckyStunSourceProtocolOption) == "webdav";

    public bool LuckyStunSourceUsesPlex => LuckyStunProtocolType(LuckyStunSourceProtocolOption) == "plex";

    public string LuckyStunSourceUserWatermark => LuckyStunSourceUsesWebDav ? "用户名（可选）" : "用户名 / 用户 ID（可选）";

    public string LuckyStunSourceCredentialWatermark => LuckyStunSourceUsesWebDav
        ? "密码（可选）"
        : (LuckyStunSourceUsesPlex ? "Plex 访问令牌（X-Plex-Token）" : "密码 / API Key / 访问令牌");

    public string LuckyStunSourceCredentialLabel => LuckyStunSourceProtocolOption?.Label ?? "媒体服务器" + " 登录";

    public string LuckyStunPanelTitle => pendingLuckyStunEditSourceId.HasValue
        ? "编辑 Lucky STUN 穿透媒体源"
        : "添加 Lucky STUN 穿透媒体源";

    public bool LuckyStunSourceSaveEnabled => !IsPendingLuckyStunLoginInProgress
        && !string.IsNullOrWhiteSpace(PendingLuckyStunRuleId)
        && (LuckyStunSourceUsesWebDav || !string.IsNullOrWhiteSpace(PendingLuckyStunServerPassword));

    public bool PendingMediaServerUsesUserId => NormalizeMediaServerProtocolType(PendingMediaServerProtocolType) is not "plex";

    public bool PendingMediaServerUsesPlex => NormalizeMediaServerProtocolType(PendingMediaServerProtocolType) == "plex";

    public string PlexAuthorizeButtonText => IsPlexAuthorizationInProgress ? "等待 Plex 授权..." : "登录 Plex";

    public SettingsViewModel Settings { get; }

    public PlayerViewModel Player { get; }

    public IAsyncRelayCommand ScanCommand { get; }

    public IAsyncRelayCommand AddFolderSourceCommand { get; }

    public IAsyncRelayCommand AddWebDavSourceCommand { get; }

    public IAsyncRelayCommand TestWebDavSourceCommand { get; }

    public IRelayCommand OpenMediaServerPanelCommand { get; }
    public IRelayCommand OpenLuckyStunSourcePanelCommand { get; }
    public IRelayCommand CloseLuckyStunSourcePanelCommand { get; }
    public IAsyncRelayCommand SaveLuckyStunSourceCommand { get; }
    public IAsyncRelayCommand TestLuckyStunWebDavSourceCommand { get; }

    public IRelayCommand CloseMediaServerPanelCommand { get; }

    public IAsyncRelayCommand PreScanMediaServerSourceCommand { get; }

    public IAsyncRelayCommand AuthorizePlexCommand { get; }
    public IAsyncRelayCommand TestPendingLuckyStunLoginCommand { get; }
    public IAsyncRelayCommand SavePendingLuckyStunSourceCommand { get; }
    public IAsyncRelayCommand<MediaSource?> RefreshLuckyStunSourceCommand { get; }

    public IRelayCommand<MediaSource?> EditLuckyStunSourceCommand { get; }

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

    public IRelayCommand KeepOfflineCacheCommand { get; }

    public IRelayCommand ConfirmCancelOfflineCacheCommand { get; }

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
    public IRelayCommand OpenPosterDoubanSearchCommand { get; }
    public IAsyncRelayCommand UnbindPosterDoubanCommand { get; }

    public IAsyncRelayCommand SavePosterMetadataEditCommand { get; }

    public IRelayCommand<LibraryVideoItem?> OpenEpisodeEditCommand { get; }

    public IRelayCommand CloseEpisodeEditCommand { get; }

    public IAsyncRelayCommand SaveEpisodeEditCommand { get; }

    public IAsyncRelayCommand ChooseEpisodeThumbnailImageCommand { get; }

    public IAsyncRelayCommand<MediaSource?> EditSourceCommand { get; }

    public IRelayCommand CancelWebDavEditCommand { get; }

    public IAsyncRelayCommand<MediaSource?> RemoveSourceCommand { get; }

    public IRelayCommand<MediaSource?> OpenRemoveSourceConfirmationCommand { get; }

    public IAsyncRelayCommand ConfirmRemoveSourceCommand { get; }

    public IRelayCommand CancelRemoveSourceConfirmationCommand { get; }

    public string RemoveSourceConfirmationBody
    {
        get { return "即将移除媒体源「" + RemoveSourceConfirmationText + "」。移除后首页将隐藏对应海报，扫描和刮削数据保留 30 天。"; }
    }

    public IAsyncRelayCommand<MediaSource?> ToggleSourceEnabledCommand { get; }

    public IAsyncRelayCommand<MediaSource?> CheckSourceConnectivityCommand { get; }

    public IAsyncRelayCommand<MediaSource?> RefreshAndCheckLuckyStunSourceCommand { get; }

    public IRelayCommand AddPendingExternalAddressCommand { get; }

    public IRelayCommand<MediaSourceExternalAddress?> RemovePendingExternalAddressCommand { get; }

    public IAsyncRelayCommand RefreshNetworkSourcesCommand { get; }

    public IRelayCommand<NetworkSourceDiscoveryItem?> OpenNetworkLoginCommand { get; }

    public IRelayCommand OpenManualNetworkLoginCommand { get; }

    public IRelayCommand CloseNetworkLoginCommand { get; }

    public IAsyncRelayCommand SaveNetworkLoginCommand { get; }

    public IRelayCommand ToggleNetworkPasswordVisibilityCommand { get; }

    public IRelayCommand ToggleMediaServerTokenVisibilityCommand { get; }
    public IRelayCommand ToggleLuckyStunPasswordVisibilityCommand { get; }
    public IRelayCommand TogglePendingLuckyStunPasswordVisibilityCommand { get; }

    public IAsyncRelayCommand<NetworkShareFolderItem?> MountNetworkFolderCommand { get; }

    public IRelayCommand<NetworkShareFolderItem?> ToggleNetworkFolderStarCommand { get; }

    public IAsyncRelayCommand MountStarredNetworkFoldersCommand { get; }

    public IRelayCommand<LibrarySortOptionItem?> SelectSortOptionCommand { get; }

    public IRelayCommand ToggleHomeCategoryMenuCommand { get; }

    public IAsyncRelayCommand<string?> SelectHomeCategoryCommand { get; }

    public IRelayCommand CloseHomeFolderCommand { get; }

    public IAsyncRelayCommand<LibraryPosterItem?> EditHomeFolderPosterCommand { get; }

    public IRelayCommand CloseFolderPosterEditorCommand { get; }

    public IAsyncRelayCommand RenameFolderPersonCommand { get; }

    public IAsyncRelayCommand ChooseFolderLocalPosterCommand { get; }

    public IAsyncRelayCommand<FolderPosterCandidate?> SelectFolderPosterCommand { get; }

    public IAsyncRelayCommand ResetFolderPosterCommand { get; }

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
    private bool hasMultipleDetailVersions;

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
    private SettingsOptionItem? luckyStunSourceProtocolOption;

    [ObservableProperty]
    private string pendingMediaServerBaseUrl = string.Empty;

    [ObservableProperty]
    private string pendingMediaServerToken = string.Empty;

    [ObservableProperty]
    private bool isMediaServerTokenVisible;

    [ObservableProperty]
    private bool isLuckyStunPasswordVisible;

    [ObservableProperty]
    private bool isPendingLuckyStunPasswordVisible;

    [ObservableProperty]
    private bool isLuckyStunSourcePanelOpen;

    [ObservableProperty]
    private string luckyStunSourceStatus = string.Empty;

    [ObservableProperty]
    private string pendingMediaServerUserId = string.Empty;

    [ObservableProperty]
    private string pendingLuckyStunManagementUrl = string.Empty;

    [ObservableProperty]
    private string pendingLuckyStunUsername = string.Empty;

    [ObservableProperty]
    private string pendingLuckyStunPassword = string.Empty;

    [ObservableProperty]
    private string pendingLuckyStunServerUsername = string.Empty;

    [ObservableProperty]
    private string pendingLuckyStunServerPassword = string.Empty;

    [ObservableProperty]
    private string pendingLuckyStunRuleId = string.Empty;

    [ObservableProperty]
    private string pendingLuckyStunPathSuffix = string.Empty;

    [ObservableProperty]
    private bool pendingLuckyStunAutoUpdate = true;

    [ObservableProperty]
    private int pendingLuckyStunUpdateIntervalMinutes = 30;

    [ObservableProperty]
    private bool isPendingLuckyStunLoginInProgress;

    [ObservableProperty]
    private string pendingLuckyStunStatus = string.Empty;

    private long? pendingLuckyStunEditSourceId;

    private LuckyStunSourceConfiguration? pendingLuckyStunMountConfiguration;

    private string pendingLuckyStunMountRootAddress = string.Empty;

    public string PendingLuckyStunLoginButtonText => IsPendingLuckyStunLoginInProgress ? "检测中..." : "登录检测";

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
    private bool isCancelOfflineCacheConfirmationOpen;

    [ObservableProperty]
    private bool isRemoveSourceConfirmationOpen;

    [ObservableProperty]
    private string removeSourceConfirmationText = string.Empty;

    private MediaSource? pendingRemoveSource;

    [ObservableProperty]
    private bool showOfflineCacheTopBar;

    [ObservableProperty]
    private string offlineCacheTopBarTitle = string.Empty;

    [ObservableProperty]
    private string offlineCacheTopBarDetail = string.Empty;

    [ObservableProperty]
    private double offlineCacheTopBarProgress;

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
    private bool isHomeCategoryMenuOpen;

    [ObservableProperty]
    private string homeCategoryMode = "none";

    [ObservableProperty]
    private string? openFolderKey;

    [ObservableProperty]
    private string openFolderBioHeader = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<IReadOnlyList<FolderBioRow>> openFolderBioColumns = EmptyBioColumns;

    [ObservableProperty]
    private bool hasOpenFolderBioRows;

    [ObservableProperty]
    private bool isOpenFolderFilmographyLoading;

    [ObservableProperty]
    private string? openFolderCoverPath;

    [ObservableProperty]
    private string openFolderCoverTip = string.Empty;

    [ObservableProperty]
    private bool isFolderPosterEditorOpen;

    [ObservableProperty]
    private string? folderPosterEditorFolderKey;

    [ObservableProperty]
    private string folderPosterEditorTitle = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<FolderPosterCandidate> folderPosterCandidates = [];

    [ObservableProperty]
    private string? folderPosterEditorCurrentPath;

    [ObservableProperty]
    private string folderPosterEditorDraftName = string.Empty;

    [ObservableProperty]
    private string folderPosterEditorNameLabel = string.Empty;

    [ObservableProperty]
    private bool isFolderPersonRenameVisible;

    /// <summary>编辑器正在编辑的人物档案类型（director/cast），改名时要用。</summary>
    private string folderPosterEditorKind = string.Empty;

    public bool HasFolderPosterCandidates => FolderPosterCandidates.Count > 0;

    public bool HasFolderPosterEditorCurrent => !string.IsNullOrWhiteSpace(FolderPosterEditorCurrentPath);

    [ObservableProperty]
    private bool hasDetailDirector;

    [ObservableProperty]
    private string detailDirectorLine = string.Empty;

    [ObservableProperty]
    private bool hasDetailCast;

    [ObservableProperty]
    private string detailCastLine = string.Empty;

    [ObservableProperty]
    private string posterEditDirector = string.Empty;

    [ObservableProperty]
    private string posterEditCast = string.Empty;

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
    private bool hasDetailTmdbRating;

    [ObservableProperty]
    private string detailTmdbRatingText = string.Empty;

    [ObservableProperty]
    private bool hasDetailDoubanRating;

    [ObservableProperty]
    private string detailDoubanRatingText = string.Empty;

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

    [ObservableProperty]
    private bool hasPosterDoubanBinding;

    [ObservableProperty]
    private string posterDoubanBindingSummary = string.Empty;

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
    private string detailPrimaryFileProgressText = "\u672A\u64AD";

    [ObservableProperty]
    private string detailPrimaryTimeText = "00:00 / \u672A\u77E5\u65F6\u957F";

    [ObservableProperty]
    private string detailPrimaryActionText = "\u5F00\u59CB\u64AD\u653E";

    [ObservableProperty]
    private string detailSelectionHint = "\u70B9\u51FB\u4E0B\u65B9\u6761\u76EE\u53EF\u5207\u6362\u4E3B\u6587\u4EF6\u3002";

    [ObservableProperty]
    private string detailWatchedActionText = "\u672A\u64AD";

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

    /// <summary>分类文件夹详情页占满整页，此时首页的继续播放区块要让位。</summary>
    public bool ShowContinueWatching => HasContinueWatching && !IsHomeFolderOpen;

    public bool HasLibraryItems => LibraryItems.Count > 0;

    public bool ShowLibrarySkeleton => IsBusy && !HasLibraryItems;

    public bool ShowLibraryEmptyState => !ShowLibrarySkeleton && !HasLibraryItems;

    public string LibraryEmptyTitleText => string.IsNullOrWhiteSpace(SearchText) ? "还没有影视内容" : "没有匹配的影视";

    public string LibraryEmptyHintText => string.IsNullOrWhiteSpace(SearchText)
        ? "打开右上角媒体库面板，添加本地文件夹、SMB、WebDAV 或 Lucky STUN 穿透媒体源，然后执行扫描。"
        : "没有找到与「" + SearchText + "」匹配的标题，试试其他关键词。";

    private void NotifyLibraryViewFlags()
    {
        OnPropertyChanged("HasLibraryItems");
        OnPropertyChanged("ShowLibrarySkeleton");
        OnPropertyChanged("ShowLibraryEmptyState");
        OnPropertyChanged("LibraryEmptyTitleText");
        OnPropertyChanged("LibraryEmptyHintText");
    }

    public void ShowToast(string text, ToastKind kind = ToastKind.Info)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        ToastItem item = new(text.Trim(), kind);
        while (Toasts.Count >= 3)
        {
            Toasts.RemoveAt(0);
        }
        Toasts.Add(item);
        _ = DismissToastAsync(item);
    }

    private async Task DismissToastAsync(ToastItem item)
    {
        try
        {
            await Task.Delay(4200);
        }
        catch (TaskCanceledException)
        {
            return;
        }
        Toasts.Remove(item);
    }

    public bool HasSeasonTabs => IsDetailSeries && AvailableSeasons.Count > 1;

    public bool CanShowDetailVersions => !IsDetailSeries && HasMultipleDetailVersions;

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

    private static string BuildDiagnosticSuffix(LibraryScanSummary? summary)
    {
        if (summary is not { HasDiagnostics: true })
        {
            return string.Empty;
        }

        string diagnosticText = string.Join(" ", summary.Diagnostics.Take(2));
        return " 另有 " + summary.Diagnostics.Count + " 条扫描诊断：" + diagnosticText;
    }

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

    public string LuckyStunPasswordVisibilityGlyph => IsLuckyStunPasswordVisible ? "🙈" : "👁";

    public string PendingLuckyStunPasswordVisibilityGlyph => IsPendingLuckyStunPasswordVisible ? "🙈" : "👁";

    public bool IsEditingWebDavSource => EditingWebDavSourceId.HasValue;

    public bool IsSortAscending => !IsSortDescending;

    public string SortDirectionLabel => IsSortDescending ? "\u964D\u5E8F" : "\u5347\u5E8F";

    public string SortDirectionToolTip => IsHomeCategoryModeActive
        ? IsSortDescending
            ? "\u5206\u7C7B\u6587\u4EF6\u5939\u6309\u540D\u79F0\u964D\u5E8F"
            : "\u5206\u7C7B\u6587\u4EF6\u5939\u6309\u540D\u79F0\u5347\u5E8F"
        : IsSortDescending
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
            ShowToast("扫描完成：新增 " + LastScanSummary.NewMovieCount + " 部电影、" + LastScanSummary.NewTvShowCount + " 部剧集", ToastKind.Success);
            _ = BackfillPersonChineseNamesAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "\u5DF2\u505C\u6B62\u626B\u63CF\u548C\u522E\u524A\u4EFB\u52A1\u3002";
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[OmniPlay] 扫描媒体库失败：" + ex.Message);
            StatusMessage = $"扫描失败：{ex.Message}";
            ShowToast("扫描失败：" + ex.Message, ToastKind.Error);
        }
        finally
        {
            startupScanInProgress = false;
            CompleteLibraryAutomation(automationCancellationTokenSource);
            OnCommandStateChanged();
        }
    }

    /// <summary>扫描后台补齐历史数据里的外文人名/片名，不阻塞界面。</summary>
    private async Task BackfillPersonChineseNamesAsync()
    {
        if (categoryFolderStore is null)
        {
            return;
        }

        try
        {
            int changed = await categoryFolderStore.BackfillChineseNamesAsync();
            if (changed > 0)
            {
                await ReloadLibraryAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[OmniPlay] 中文译名补全失败：" + ex.Message);
        }
    }

    private async Task<LibraryScanSummary> RunLibraryScanAndArtworkAsync(bool isExplicitRequest, bool forceThumbnails, CancellationToken cancellationToken, IReadOnlyCollection<long>? sourceIds = null, bool skipTmdbArtwork = false)
    {
        HashSet<long> targetSourceIds = sourceIds?.ToHashSet();
        List<MediaSource> sources = (from mediaSource in await mediaSourceRepository.GetAllAsync(cancellationToken)
            where mediaSource != null && mediaSource.Id.HasValue && mediaSource.IsActive && (targetSourceIds == null || targetSourceIds.Contains(mediaSource.Id.Value))
            orderby mediaSource.Id
            select mediaSource).ToList();
        LibraryScanAggregate aggregate = new LibraryScanAggregate();
        libraryScanner.ClearDeferredUnidentifiedScanGroups();
        if (sources.Count == 0)
        {
            return FinishScanWithDiagnostics(aggregate.ToSummary());
        }
        try
        {
            foreach (MediaSource source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StatusMessage = "正在扫描媒体源：" + source.Name + "...";
                aggregate.Add(await libraryScanner.ScanSourceAsync(source.Id.Value, cancellationToken, null, deferUnidentifiedGroups: true));
                LastScanSummary = aggregate.ToSummary();
                await ReloadLibraryAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            cancellationToken.ThrowIfCancellationRequested();
            LibraryScanSummary deferredSummary = await libraryScanner.CommitDeferredUnidentifiedScanGroupsAsync(cancellationToken);
            if (deferredSummary.NewMovieCount > 0 || deferredSummary.NewTvShowCount > 0 || deferredSummary.NewVideoFileCount > 0 || deferredSummary.RemovedVideoFileCount > 0 || deferredSummary.HasDiagnostics)
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
                if (!(await EnsureTmdbAvailableForPendingArtworkAsync(isExplicitRequest, forceThumbnails: true, cancellationToken)))
                {
                    return FinishScanWithDiagnostics(aggregate.ToSummary());
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
        return FinishScanWithDiagnostics(aggregate.ToSummary());
    }

    private LibraryScanSummary FinishScanWithDiagnostics(LibraryScanSummary summary)
    {
        if (summary.HasDiagnostics)
        {
            string diagnosticText = string.Join(" ", summary.Diagnostics.Take(2));
            StatusMessage += " 另有 " + summary.Diagnostics.Count + " 条扫描诊断：" + diagnosticText;
        }

        return summary;
    }

    private async Task<bool> EnsureTmdbAvailableForPendingArtworkAsync(bool isExplicitRequest, bool forceThumbnails, CancellationToken cancellationToken)
    {
        TmdbSettings tmdbSettings = Settings.BuildTmdbSettings();
        bool needsMetadata = NeedsMetadataRefresh(tmdbSettings);
        bool needsThumbnails = ShouldRefreshThumbnails(tmdbSettings, forceThumbnails);
        if (!needsMetadata && !needsThumbnails)
        {
            StatusMessage = "当前媒体库已无需刮削的内容。";
            return false;
        }
        if (tmdbConnectionTester == null)
        {
            return true;
        }
        StatusMessage = "正在检测 TMDB API 连通性...";
        TmdbConnectionTestResult connection = await tmdbConnectionTester.TestConnectionAsync(tmdbSettings, cancellationToken);
        if (connection.Success)
        {
            return true;
        }
        if (isExplicitRequest)
        {
            StatusMessage = "TMDB API 无法连接，已保留未刮削条目：" + connection.Message;
        }
        else
        {
            StartupTmdbAlertMessage = "建议添加自己的 TMDB API 后再刮削海报与影视信息。\n" + connection.Message;
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
        string selectedPath = await folderPickerService.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            StatusMessage = "已取消选择文件夹。";
            return;
        }
        if (!Directory.Exists(selectedPath))
        {
            StatusMessage = "文件夹不存在：" + selectedPath;
            return;
        }
        IsBusy = true;
        OnCommandStateChanged();
        try
        {
            MediaSource source = CreateLocalSource(selectedPath);
            long sourceId = await mediaSourceRepository.AddAsync(source);
            if (sourceId <= 0)
            {
                await ReloadLibraryAsync();
                StatusMessage = "已保存本地媒体源。";
                return;
            }
            CancellationTokenSource automationCancellationTokenSource = BeginLibraryAutomation();
            CancellationToken cancellationToken = automationCancellationTokenSource.Token;
            IsBusy = false;
            OnCommandStateChanged();
            try
            {
                StatusMessage = "已添加本地媒体源：" + source.Name + "，正在扫描并刮削...";
                await ReloadLibraryAsync();
                LastScanSummary = await RunLibraryScanAndArtworkAsync(isExplicitRequest: true, forceThumbnails: true, cancellationToken, new long[1] { sourceId });
                StatusMessage = (string.IsNullOrWhiteSpace(StatusMessage) ? ("已添加并扫描本地媒体源：" + source.Name) : ("已添加本地媒体源：" + source.Name + "。" + StatusMessage));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                StatusMessage = "已停止新媒体源的扫描和刮削任务。";
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[OmniPlay] 扫描新媒体源失败：" + ex.Message);
                StatusMessage = "已添加本地媒体源：" + source.Name + "，但扫描失败：" + ex.Message;
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
        MediaSource source = BuildPendingWebDavSource();
        if (source == null)
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
                if (!(await mediaSourceRepository.UpdateAsync(source)))
                {
                    StatusMessage = "保存 WebDAV 失败，可能是媒体源不存在，或与现有地址重复。";
                    return;
                }
                await ReloadLibraryAsync();
                ResetWebDavForm();
                IsSourcePopupOpen = false;
                StatusMessage = "已保存 WebDAV 媒体源：" + source.Name;
                return;
            }
            long sourceId = await mediaSourceRepository.AddAsync(source);
            ResetWebDavForm();
            IsSourcePopupOpen = false;
            if (sourceId <= 0)
            {
                await ReloadLibraryAsync();
                StatusMessage = "已保存 WebDAV 媒体源。";
                return;
            }
            CancellationTokenSource automationCancellationTokenSource = BeginLibraryAutomation();
            CancellationToken cancellationToken = automationCancellationTokenSource.Token;
            IsBusy = false;
            OnCommandStateChanged();
            try
            {
                StatusMessage = "已添加 WebDAV 媒体源：" + source.Name + "，正在扫描并刮削...";
                await ReloadLibraryAsync();
                LastScanSummary = await RunLibraryScanAndArtworkAsync(isExplicitRequest: true, forceThumbnails: true, cancellationToken, new long[1] { sourceId });
                StatusMessage = (string.IsNullOrWhiteSpace(StatusMessage) ? ("已添加并扫描 WebDAV 媒体源：" + source.Name) : ("已添加 WebDAV 媒体源：" + source.Name + "。" + StatusMessage));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                StatusMessage = "已停止新媒体源的扫描和刮削任务。";
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[OmniPlay] 扫描新媒体源失败：" + ex.Message);
                StatusMessage = "已添加 WebDAV 媒体源：" + source.Name + "，但扫描失败：" + ex.Message;
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
        MediaSource source = BuildPendingWebDavSource();
        if (source == null)
        {
            return;
        }
        OnCommandStateChanged();
        try
        {
            StatusMessage = (await webDavConnectionTester.TestConnectionAsync(source)).Message;
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
            StatusMessage = (string.IsNullOrWhiteSpace(filePath) ? "未提供用于覆盖层诊断的媒体文件。" : ("覆盖层诊断文件不存在：" + filePath));
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
        PendingPlaybackStartPositionSeconds = 0.0;
        currentPlaybackMode = PlaybackMode.Overlay;
        currentPlayingVideoId = string.Empty;
        selectedPlaybackNavigationSeason = null;
        suppressedPlaybackPersistenceVideoId = null;
        ResetAutoAdvanceState();
        UpdatePlayerNextEpisodeAction();
        StatusMessage = "正在准备覆盖层诊断播放：" + Path.GetFileName(filePath);
        OnCommandStateChanged();
    }

    public Task CloseOverlayPlaybackDiagnosticAsync()
    {
        return ClosePlayerOverlayAsync();
    }

    private async Task ReloadLibraryAsync()
    {
        IReadOnlyList<Movie> movies = await movieRepository.GetAllAsync();
        IReadOnlyList<TvShow> tvShows = await tvShowRepository.GetAllAsync();
        IReadOnlyList<MediaSource> sources = await mediaSourceRepository.GetAllAsync();
        IReadOnlyList<LibraryPosterItem> continueWatching = await videoFileRepository.GetContinueWatchingAsync();
        IReadOnlyDictionary<string, PlaybackWatchState> playbackStates = await videoFileRepository.GetLibraryPlaybackStatesAsync();
        allMovies.Clear();
        allMovies.AddRange(movies);
        allTvShows.Clear();
        allTvShows.AddRange(tvShows);
        ReplaceItems(MediaSources, sources);
        OnPropertyChanged("HasMediaSources");
        ReplaceItems(ContinueWatchingItems, continueWatching.Select(ApplyOfflineCacheState).ToList());
        OnPropertyChanged("HasContinueWatching");
        OnPropertyChanged("ShowContinueWatching");
        HashSet<string> continuingIds = continueWatching.Select((LibraryPosterItem item) => item.Id).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
        allLibraryItems.Clear();
        allLibraryItems.AddRange(movies.Select(delegate(Movie movie)
        {
            string text = $"movie-{movie.Id}";
            PlaybackWatchState watchState = ResolvePosterWatchState(playbackStates, text);
            return new LibraryPosterItem
            {
                Id = text,
                Title = movie.Title,
                Subtitle = FormatMovieSubtitle(movie),
                PosterPath = movie.PosterPath,
                VoteAverage = movie.VoteAverage,
                DoubanRating = movie.DoubanRating,
                Director = movie.Director,
                MainCast = movie.MainCast,
                Directors = movie.Directors,
                Casts = movie.Casts,
                MediaKind = "电影",
                IsContinuing = continuingIds.Contains(text),
                WatchState = watchState,
                MovieId = movie.Id
            };
        }));
        allLibraryItems.AddRange(GroupTvShowsForHome(tvShows).Select(delegate(TvShowHomeGroup group)
        {
            TvShow representative = group.Representative;
            string id = $"tv-{representative.Id}";
            List<string> list = group.Shows.Select((TvShow groupedShow) => $"tv-{groupedShow.Id}").ToList();
            PlaybackWatchState watchState = ResolvePosterWatchState(playbackStates, list);
            return new LibraryPosterItem
            {
                Id = id,
                Title = representative.Title,
                Subtitle = FormatTvShowSubtitle(representative),
                PosterPath = representative.PosterPath,
                VoteAverage = representative.VoteAverage,
                DoubanRating = representative.DoubanRating,
                Director = representative.Director,
                MainCast = representative.MainCast,
                Directors = representative.Directors,
                Casts = representative.Casts,
                MediaKind = "剧集",
                IsContinuing = list.Any(continuingIds.Contains),
                WatchState = watchState,
                TvShowId = representative.Id
            };
        }));
        await RefreshHomeCategoryCacheAsync();
        ApplyFilters();
    }

    private async Task OpenDetailAsync(LibraryPosterItem? item)
    {
        if (item == null)
        {
            return;
        }
        preferredDetailVideoId = null;
        if (item.IsFolder)
        {
            await RefreshHomeCategoryCacheAsync();
            OpenFolderKey = item.FolderKey;
            ApplyFilters();
            RefreshOpenFolderBio();
            _ = RefreshOpenFolderFilmographyAsync();
            return;
        }
        if (item.MovieId.HasValue)
        {
            Movie movie = allMovies.FirstOrDefault((Movie x) => x.Id == item.MovieId);
            if (movie != null)
            {
                currentDetailMovieId = movie.Id;
                currentDetailTvShowId = null;
                await OpenDetailInternalAsync(movie.Title, movie.ReleaseDate ?? "电影", movie.Overview ?? "暂无简介。", movie.PosterPath, movie.VoteAverage, "电影", movie.IsLocked, isSeries: false, preserveMetadataCandidates: false, () => videoFileRepository.GetByMovieAsync(movie.Id.GetValueOrDefault()));
            }
        }
        else
        {
            if (!item.TvShowId.HasValue)
            {
                return;
            }
            TvShow show = allTvShows.FirstOrDefault((TvShow x) => x.Id == item.TvShowId.Value);
            if (show != null)
            {
                currentDetailMovieId = null;
                currentDetailTvShowId = show.Id;
                await OpenDetailInternalAsync(show.Title, FormatTvShowSubtitle(show.FirstAirDate), show.Overview ?? "这里会按季整理所有分集，便于还原 mac 版的主集选择和继续观看逻辑。", show.PosterPath, show.VoteAverage, "剧集", show.IsLocked, isSeries: true, preserveMetadataCandidates: false, () => videoFileRepository.GetByTvShowAsync(show.Id));
            }
        }
    }

    private async Task OpenDetailInternalAsync(string title, string meta, string overview, string? posterPath, double? voteAverage, string kind, bool isLocked, bool isSeries, bool preserveMetadataCandidates, Func<Task<IReadOnlyList<LibraryVideoItem>>> fileLoader)
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
        DetailPosterPath = (string.IsNullOrWhiteSpace(posterPath) ? null : posterPath);
        DetailKindText = kind;
        IsDetailLocked = isLocked;
        DetailLockStateText = (isLocked ? "已锁定元数据" : "未锁定，后续扫描可自动更新");
        DetailLockActionText = (isLocked ? "解除锁定" : "锁定元数据");
        DetailScrapeQuery = title;
        if (!preserveMetadataCandidates)
        {
            ClearDetailMetadataCandidates();
        }
        double? doubanRating = ((!currentDetailMovieId.HasValue) ? ((!currentDetailTvShowId.HasValue) ? ((double?)null) : allTvShows.FirstOrDefault((TvShow show) => show.Id == currentDetailTvShowId.Value)?.DoubanRating) : allMovies.FirstOrDefault((Movie movie) => movie.Id == currentDetailMovieId.Value)?.DoubanRating);
        HasDetailTmdbRating = voteAverage.HasValue && voteAverage.Value > 0;
        DetailTmdbRatingText = FormatRatingText(voteAverage);
        HasDetailDoubanRating = doubanRating.HasValue && doubanRating.Value > 0;
        DetailDoubanRatingText = FormatRatingText(doubanRating);
        DetailCollectionHeading = (isSeries ? "分集列表" : "文件列表");
        DetailFileSummary = "正在读取文件...";
        allDetailFiles.Clear();
        ReplaceItems(DetailFiles, Array.Empty<LibraryVideoItem>());
        ReplaceItems(AvailableSeasons, Array.Empty<int>());
        selectedPlaybackNavigationSeason = null;
        DetailPrimaryFile = null;
        RefreshDetailHeaderState();
        UpdatePlayerNextEpisodeAction();
        IReadOnlyList<LibraryVideoItem> files = ApplyEpisodeDisplayDisambiguation(await fileLoader());
        allDetailFiles.AddRange(files.Select(ApplyOfflineCacheState));
        BuildSeasonState(files, isSeries);
        ApplyDetailFilter();
        if (!IsLibraryScanInProgress)
        {
            StatusMessage = "已打开《" + title + "》详情。";
        }
        OnPropertyChanged("HasDetailFiles");
        OnCommandStateChanged();
        SetDetailPeople();
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
            string query = (string.IsNullOrWhiteSpace(DetailScrapeQuery) ? DetailTitle : DetailScrapeQuery.Trim());
            long? tvShowIdToRefresh = currentDetailTvShowId;
            StatusMessage = "正在重新刮削当前条目...";
            LibraryMetadataRefreshResult libraryMetadataRefreshResult = ((!currentDetailMovieId.HasValue) ? (await libraryMetadataEditor.RefreshTvShowAsync(currentDetailTvShowId.Value, query)) : (await libraryMetadataEditor.RefreshMovieAsync(currentDetailMovieId.Value, query)));
            LibraryMetadataRefreshResult result = libraryMetadataRefreshResult;
            if (result.Updated)
            {
                await ReloadLibraryAsync();
                await RefreshOpenDetailIfNeededAsync();
            }
            LibraryThumbnailEnrichmentSummary libraryThumbnailEnrichmentSummary = ((!tvShowIdToRefresh.HasValue) ? new LibraryThumbnailEnrichmentSummary() : (await RefreshTvShowThumbnailsAndDetailFilesAsync(tvShowIdToRefresh.Value)));
            LibraryThumbnailEnrichmentSummary thumbnailSummary = libraryThumbnailEnrichmentSummary;
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
        CancellationTokenSource automationCancellationTokenSource = BeginLibraryAutomation();
        CancellationToken cancellationToken = automationCancellationTokenSource.Token;
        startupScanInProgress = true;
        OnCommandStateChanged();
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(750L), cancellationToken);
            StatusMessage = "正在后台扫描媒体源...";
            LastScanSummary = await RunLibraryScanAndArtworkAsync(isExplicitRequest: false, forceThumbnails: true, cancellationToken);
            string scanPrefix = $"后台扫描完成：新增 {LastScanSummary.NewMovieCount} 部电影、{LastScanSummary.NewTvShowCount} 部剧集，移除 {LastScanSummary.RemovedVideoFileCount} 个视频文件。";
            StatusMessage = (string.IsNullOrWhiteSpace(StatusMessage) ? scanPrefix : (scanPrefix + " " + StatusMessage));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "已停止后台扫描和刮削任务。";
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
        CancellationTokenSource automationCancellationTokenSource = BeginLibraryAutomation();
        CancellationToken cancellationToken = automationCancellationTokenSource.Token;
        startupScanInProgress = true;
        OnCommandStateChanged();
        try
        {
            StatusMessage = "正在本地扫描媒体源...";
            LastScanSummary = await RunLibraryScanAndArtworkAsync(isExplicitRequest: false, forceThumbnails: false, cancellationToken, null, skipTmdbArtwork: true);
            StatusMessage = "本地扫描完成，未刮削条目已显示；TMDB API 连通后可重新同步刮削。" + BuildDiagnosticSuffix(LastScanSummary);
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
            string query = (string.IsNullOrWhiteSpace(DetailScrapeQuery) ? DetailTitle : DetailScrapeQuery.Trim());
            StatusMessage = "正在搜索 TMDB 候选结果...";
            IReadOnlyList<LibraryMetadataSearchCandidate> readOnlyList = ((!currentDetailMovieId.HasValue) ? (await libraryMetadataEditor.SearchTvShowMatchesAsync(currentDetailTvShowId.Value, query)) : (await libraryMetadataEditor.SearchMovieMatchesAsync(currentDetailMovieId.Value, query)));
            IReadOnlyList<LibraryMetadataSearchCandidate> candidates = readOnlyList;
            ReplaceItems(DetailMetadataCandidates, candidates);
            IsDetailMetadataCandidatePanelOpen = candidates.Count > 0;
            OnDetailMetadataCandidatesStateChanged();
            StatusMessage = ((candidates.Count > 0) ? BuildDetailMetadataCandidatesMessage(candidates) : ("未找到与「" + query + "」匹配的 TMDB 候选结果，已自动尝试回退查询。"));
        }
        catch (Exception ex) when (((ex is HttpRequestException || ex is TaskCanceledException) ? 1 : 0) != 0)
        {
            HideDetailMetadataCandidatesPanel();
            StatusMessage = "搜索 TMDB 候选结果失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private async Task ApplyDetailMetadataCandidateAsync(LibraryMetadataSearchCandidate? candidate)
    {
        if ((object)candidate == null || (!currentDetailMovieId.HasValue && !currentDetailTvShowId.HasValue))
        {
            return;
        }
        IsBusy = true;
        OnCommandStateChanged();
        try
        {
            long? tvShowIdToRefresh = currentDetailTvShowId;
            StatusMessage = "正在应用所选 TMDB 匹配...";
            LibraryMetadataRefreshResult libraryMetadataRefreshResult = ((!currentDetailMovieId.HasValue) ? (await libraryMetadataEditor.ApplyTvShowMatchAsync(currentDetailTvShowId.Value, candidate)) : (await libraryMetadataEditor.ApplyMovieMatchAsync(currentDetailMovieId.Value, candidate)));
            LibraryMetadataRefreshResult result = libraryMetadataRefreshResult;
            if (result.Updated)
            {
                await ReloadLibraryAsync();
                await RefreshOpenDetailIfNeededAsync();
            }
            LibraryThumbnailEnrichmentSummary libraryThumbnailEnrichmentSummary = ((!tvShowIdToRefresh.HasValue) ? new LibraryThumbnailEnrichmentSummary() : (await RefreshTvShowThumbnailsAndDetailFilesAsync(tvShowIdToRefresh.Value)));
            LibraryThumbnailEnrichmentSummary thumbnailSummary = libraryThumbnailEnrichmentSummary;
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
        LibraryMetadataSearchCandidate libraryMetadataSearchCandidate = candidates[0];
        return libraryMetadataSearchCandidate.HasMatchedQuery ? $"已通过{libraryMetadataSearchCandidate.MatchedQueryText}找到 {candidates.Count} 个 TMDB 候选结果。" : $"已找到 {candidates.Count} 个 TMDB 候选结果。";
    }

    private async Task<LibraryThumbnailEnrichmentSummary> RefreshTvShowThumbnailsAndDetailFilesAsync(long tvShowId)
    {
        TmdbSettings tmdbSettings = Settings.BuildTmdbSettings();
        if (!tmdbSettings.EnableEpisodeThumbnailDownloads)
        {
            return new LibraryThumbnailEnrichmentSummary();
        }
        await libraryRefreshGate.WaitAsync();
        LibraryThumbnailEnrichmentSummary summary;
        try
        {
            StatusMessage = "正在刮削当前剧集的分集剧照...";
            summary = await libraryThumbnailEnricher.EnrichMissingThumbnailsForTvShowAsync(tvShowId, tmdbSettings);
        }
        finally
        {
            libraryRefreshGate.Release();
        }
        if (summary.HasChanges && IsDetailOpen && currentDetailTvShowId.HasValue && currentDetailTvShowId.Value == tvShowId)
        {
            await RefreshDetailFilesAsync();
        }
        return summary;
    }

    private static string AppendThumbnailRefreshStatusMessage(string? message, LibraryThumbnailEnrichmentSummary summary)
    {
        string text = ComposeAutomaticThumbnailRefreshStatusMessage(summary);
        if (string.IsNullOrWhiteSpace(text))
        {
            return message ?? string.Empty;
        }
        return string.IsNullOrWhiteSpace(message) ? text : (message + " " + text);
    }

    private static string ComposeAutomaticThumbnailRefreshStatusMessage(LibraryThumbnailEnrichmentSummary summary)
    {
        if (summary.EncounteredNetworkError)
        {
            return string.IsNullOrWhiteSpace(summary.ErrorMessage) ? "分集剧照下载失败：无法连接网络或 TMDB。" : ("分集剧照下载失败：" + summary.ErrorMessage);
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
            bool nextLocked = !IsDetailLocked;
            if (!currentDetailMovieId.HasValue)
            {
                await libraryMetadataEditor.SetTvShowLockedAsync(currentDetailTvShowId.Value, nextLocked);
            }
            else
            {
                await libraryMetadataEditor.SetMovieLockedAsync(currentDetailMovieId.Value, nextLocked);
            }
            HideDetailMetadataCandidatesPanel();
            await ReloadLibraryAsync();
            await RefreshOpenDetailIfNeededAsync();
            StatusMessage = (nextLocked ? "当前条目已锁定，后续自动刮削不会覆盖它。" : "当前条目已解除锁定，后续可继续自动补全。");
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private void ShowDetailMetadataCandidatesPanel()
    {
        if (HasDetailMetadataCandidates)
        {
            IsDetailMetadataCandidatePanelOpen = true;
            OnDetailMetadataCandidatesStateChanged();
        }
    }

    private void HideDetailMetadataCandidatesPanel()
    {
        if (IsDetailMetadataCandidatePanelOpen)
        {
            IsDetailMetadataCandidatePanelOpen = false;
            OnDetailMetadataCandidatesStateChanged();
        }
    }

    private void ClearDetailMetadataCandidates()
    {
        IsDetailMetadataCandidatePanelOpen = false;
        ReplaceItems(DetailMetadataCandidates, Array.Empty<LibraryMetadataSearchCandidate>());
        OnDetailMetadataCandidatesStateChanged();
    }

    private void OnDetailMetadataCandidatesStateChanged()
    {
        OnPropertyChanged("HasDetailMetadataCandidates");
        OnPropertyChanged("CanShowDetailMetadataCandidatesPanel");
        ShowDetailMetadataCandidatesPanelCommand.NotifyCanExecuteChanged();
        HideDetailMetadataCandidatesPanelCommand.NotifyCanExecuteChanged();
    }

    private async Task PlayVideoAsync(LibraryVideoItem? video)
    {
        if (video == null || !MediaSourcePathResolver.IsPlayableLocation(video.EffectivePlaybackPath))
        {
            StatusMessage = ((video == null) ? "没有可播放的文件。" : ("文件不存在：" + video.FileName));
            return;
        }
        string effectivePlaybackPath = video.EffectivePlaybackPath;
        if (string.IsNullOrWhiteSpace(effectivePlaybackPath))
        {
            StatusMessage = "无法获取播放地址：" + video.FileName;
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
        double? playbackStartPosition = nextPlaybackStartPositionOverride ?? GetPlaybackStartPosition(video);
        nextPlaybackStartPositionOverride = null;
        ApplyPlaybackPreferencesToPlayer();
        IsPlayerOverlayOpen = true;
        PendingPlaybackFilePath = string.Empty;
        PendingPlaybackDisplayPath = string.Empty;
        PendingPlaybackMediaFileName = string.Empty;
        PendingPlaybackStartPositionSeconds = playbackStartPosition.GetValueOrDefault();
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
        StatusMessage = (playbackStartPosition.HasValue ? ("正在继续播放：" + video.FileName) : ("正在准备播放：" + video.FileName));
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

    private Task OpenStandalonePlayerAsync(LibraryVideoItem? video)
    {
        return OpenStandalonePlayerInternalAsync(video, replaceCurrentSession: false, persistCurrentPlaybackState: true);
    }

    private async Task OpenStandalonePlayerInternalAsync(LibraryVideoItem? video, bool replaceCurrentSession, bool persistCurrentPlaybackState)
    {
        if (video == null || !MediaSourcePathResolver.IsPlayableLocation(video.EffectivePlaybackPath))
        {
            StatusMessage = ((video == null) ? "没有可独立播放的文件。" : ("文件不存在：" + video.FileName));
            return;
        }
        if (currentPlaybackMode == PlaybackMode.Overlay && IsPlayerOverlayOpen)
        {
            await ClosePlayerOverlayAsync();
        }
        bool isReplacingStandaloneSession = currentPlaybackMode == PlaybackMode.Standalone && !string.IsNullOrWhiteSpace(currentPlayingVideoId) && !string.Equals(currentPlayingVideoId, video.Id, StringComparison.Ordinal);
        if (persistCurrentPlaybackState & isReplacingStandaloneSession)
        {
            LibraryVideoItem currentVideo = ResolveCurrentPlaybackVideo();
            if (currentVideo != null)
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
        double? playbackStartPosition = GetPlaybackStartPosition(video);
        string effectivePlaybackPath = video.EffectivePlaybackPath;
        if (string.IsNullOrWhiteSpace(effectivePlaybackPath))
        {
            StatusMessage = "无法获取独立播放地址：" + video.FileName;
            return;
        }
        ApplyPlaybackPreferencesToPlayer();
        if (!(await playbackLauncher.OpenAsync(new PlaybackOpenRequest(effectivePlaybackPath, video.AbsolutePath, video.FileName), (PlaybackCloseResult result) => HandleStandalonePlaybackClosedAsync(video.Id, video.FileName, result), playbackStartPosition, replaceCurrentSession | isReplacingStandaloneSession)))
        {
            StatusMessage = "无法在独立窗口打开：" + video.FileName;
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
        StatusMessage = (playbackStartPosition.HasValue ? ("已在独立窗口继续播放：" + video.FileName) : ("已在独立窗口打开：" + video.FileName));
        OnCommandStateChanged();
    }

    private void SelectDetailFile(LibraryVideoItem? video)
    {
        if (video != null)
        {
            ApplyPreferredDetailVideoSelection(video);
        }
    }

    private async Task ToggleDetailWatchedAsync(LibraryVideoItem? video)
    {
        if (video != null)
        {
            double nextDuration = ((video.Duration > 0.0) ? video.Duration : 100.0);
            double nextProgress = (video.IsWatched ? 0.0 : nextDuration);
            await videoFileRepository.UpdatePlaybackStateAsync(video.Id, nextProgress, video.IsWatched ? ((double?)null) : new double?(nextDuration));
            preferredDetailVideoId = video.Id;
            await RefreshDetailFilesAsync();
            await RefreshContinueWatchingAsync();
            StatusMessage = (video.IsWatched ? ("已将 " + video.FileName + " 标记为未播。") : ("已将 " + video.FileName + " 标记为已播。"));
        }
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
        StatusMessage = (IsHomeOfflineCacheModeActive ? "首页已进入离线缓存模式。" : "首页已退出离线缓存模式。");
    }

    private void ToggleDetailOfflineCacheMode()
    {
        IsDetailOfflineCacheModeActive = !IsDetailOfflineCacheModeActive;
        IsSearchPopupOpen = false;
        IsSortPopupOpen = false;
        IsSourcePopupOpen = false;
        IsSettingsPopupOpen = false;
        RefreshOfflineCachePresentation();
        StatusMessage = (IsDetailOfflineCacheModeActive ? "详情页已进入离线缓存模式。" : "详情页已退出离线缓存模式。");
    }

    private async Task ChooseOfflineCacheDirectoryAsync()
    {
        string selectedPath = await folderPickerService.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            StatusMessage = "已取消选择离线缓存目录。";
            return;
        }
        Directory.CreateDirectory(selectedPath);
        Settings.OfflineCacheDirectory = selectedPath;
        await Settings.SaveCommand.ExecuteAsync(null);
        RefreshOfflineCachePresentation();
        StatusMessage = "已设置离线缓存目录：" + selectedPath;
    }

    private async Task CachePosterItemAsync(LibraryPosterItem? item)
    {
        if (item != null)
        {
            IReadOnlyList<LibraryVideoItem> files = await LoadPosterTargetFilesAsync(item);
            if (IsOfflineCacheInProgressForFiles(files))
            {
                RequestCancelOfflineCache();
            }
            else
            {
                await StartOfflineCacheAsync(files, item.Id, item.Title);
            }
        }
    }

    private Task CacheDetailPosterAsync()
    {
        if (IsOfflineCacheInProgressForFiles(allDetailFiles))
        {
            RequestCancelOfflineCache();
            return Task.CompletedTask;
        }
        return StartOfflineCacheAsync(allDetailFiles, CurrentDetailOfflineGroupId(), DetailTitle);
    }

    private Task CacheSelectedSeasonAsync()
    {
        List<LibraryVideoItem> list = allDetailFiles.Where((LibraryVideoItem file) => !file.IsTvEpisode || file.SeasonNumber == SelectedSeason).ToList();
        string groupTitle = ((SelectedSeason == 0) ? (DetailTitle + " 特别篇") : $"{DetailTitle} 第 {SelectedSeason} 季");
        if (IsOfflineCacheInProgressForFiles(list))
        {
            RequestCancelOfflineCache();
            return Task.CompletedTask;
        }
        return StartOfflineCacheAsync(list, null, groupTitle);
    }

    private Task CacheDetailEpisodeAsync(LibraryVideoItem? video)
    {
        if (video == null)
        {
            return Task.CompletedTask;
        }
        if (offlineCacheProgressByFileId.ContainsKey(video.Id))
        {
            RequestCancelOfflineCache();
            return Task.CompletedTask;
        }
        return StartOfflineCacheAsync(new LibraryVideoItem[1] { video }, null, video.FileName);
    }

    private async Task StartOfflineCacheAsync(IEnumerable<LibraryVideoItem> requestedFiles, string? posterGroupId, string groupTitle)
    {
        string cacheDirectory = ResolveOfflineCacheDirectory();
        if (string.IsNullOrWhiteSpace(cacheDirectory))
        {
            ShowOfflineCacheAlert("离线缓存", "请先在设置中选择离线缓存保存位置。");
            return;
        }
        Directory.CreateDirectory(cacheDirectory);
        List<LibraryVideoItem> uniqueFiles = (from @group in requestedFiles.Where((LibraryVideoItem libraryVideoItem) => !string.IsNullOrWhiteSpace(libraryVideoItem.Id)).GroupBy<LibraryVideoItem, string>((LibraryVideoItem libraryVideoItem) => libraryVideoItem.Id, StringComparer.OrdinalIgnoreCase)
            select @group.First()).ToList();
        if (uniqueFiles.Count == 0)
        {
            StatusMessage = "所选内容没有可缓存的视频文件。";
            return;
        }
        List<OfflineCacheFilePlan> plans = new List<OfflineCacheFilePlan>();
        int unsupportedCount = 0;
        foreach (LibraryVideoItem file in uniqueFiles)
        {
            string destinationPath = GetOfflineCachePath(file, cacheDirectory);
            if (File.Exists(destinationPath))
            {
                offlineCachedFileIds.Add(file.Id);
                continue;
            }
            string sourcePath = ResolveOfflineCacheSourcePath(file);
            Uri remoteUri = ResolveOfflineCacheRemoteUri(file, out string remoteAuthorization);
            if ((string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) && (object)remoteUri == null)
            {
                offlineUnavailableFileIds.Add(file.Id);
                unsupportedCount++;
                continue;
            }
            long num = ((sourcePath == null || !File.Exists(sourcePath)) ? (await GetRemoteContentLengthAsync(remoteUri, remoteAuthorization)) : new FileInfo(sourcePath).Length);
            long fileSize = num;
            plans.Add(new OfflineCacheFilePlan(file, sourcePath, remoteUri, remoteAuthorization, destinationPath, fileSize));
            remoteAuthorization = null;
        }
        if (plans.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(posterGroupId) && unsupportedCount > 0)
            {
                offlineUnavailablePosterIds.Add(posterGroupId);
            }
            RefreshOfflineCachePresentation();
            string message = ((unsupportedCount > 0) ? "远程源或不可访问文件暂不支持离线缓存。" : "所选内容已在本地缓存中。");
            StatusMessage = message;
            return;
        }
        long totalBytes = plans.Sum((OfflineCacheFilePlan offlineCacheFilePlan) => Math.Max(0L, offlineCacheFilePlan.FileSize));
        long availableBytes = GetAvailableFreeBytes(cacheDirectory);
        if (availableBytes < totalBytes + 268435456)
        {
            ShowOfflineCacheAlert("硬盘存储空间不够", $"《{groupTitle}》需要 {FormatBytes(totalBytes)}，当前缓存磁盘可用 {FormatBytes(availableBytes)}，空间不足，已取消离线缓存。");
            return;
        }
        if (!string.IsNullOrWhiteSpace(posterGroupId))
        {
            offlinePosterProgressByItemId[posterGroupId] = 0.01;
        }
        foreach (OfflineCacheFilePlan plan in plans)
        {
            offlineCacheProgressByFileId[plan.File.Id] = 0.01;
        }
        RefreshOfflineCachePresentation();
        StatusMessage = "正在离线缓存：" + groupTitle;
        long copiedBytesForGroup = 0L;
        offlineCacheCancellationTokenSource = new CancellationTokenSource();
        CancellationToken cancellationToken = offlineCacheCancellationTokenSource.Token;
        BeginOfflineCacheBatch(groupTitle, plans.Count);
        try
        {
            foreach (OfflineCacheFilePlan plan2 in plans)
            {
                offlineCacheBatchCurrentFileId = plan2.File.Id;
                offlineCacheBatchCurrentFileName = plan2.File.FileName;
                await CopyOfflineCacheFileAsync(plan2, totalBytes, () => copiedBytesForGroup, delegate(long copied)
                {
                    copiedBytesForGroup += copied;
                }, posterGroupId, cancellationToken);
                offlineCacheBatchCompletedCount++;
            }
        }
        catch (OperationCanceledException) when (isOfflineCacheCancellationRequested)
        {
            isOfflineCacheCancellationRequested = false;
            FinishOfflineCacheBatch();
            foreach (OfflineCacheFilePlan plan3 in plans)
            {
                string tempPath = plan3.DestinationPath + ".tmp";
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
                offlineCacheProgressByFileId.Remove(plan3.File.Id);
            }
            if (!string.IsNullOrWhiteSpace(posterGroupId))
            {
                offlinePosterProgressByItemId.Remove(posterGroupId);
            }
            RefreshOfflineCachePresentation();
            StatusMessage = "已取消离线缓存，已缓存的内容会保留。";
            return;
        }
        catch (Exception ex2) when (((ex2 is IOException || ex2 is UnauthorizedAccessException || ex2 is HttpRequestException) ? 1 : 0) != 0)
        {
            isOfflineCacheCancellationRequested = false;
            FinishOfflineCacheBatch();
            foreach (OfflineCacheFilePlan plan4 in plans)
            {
                offlineCacheProgressByFileId.Remove(plan4.File.Id);
            }
            if (!string.IsNullOrWhiteSpace(posterGroupId))
            {
                offlinePosterProgressByItemId.Remove(posterGroupId);
            }
            RefreshOfflineCachePresentation();
            ShowOfflineCacheAlert("离线缓存失败", ex2.Message);
            return;
        }
        if (!string.IsNullOrWhiteSpace(posterGroupId))
        {
            offlinePosterProgressByItemId.Remove(posterGroupId);
            offlineCachedPosterIds.Add(posterGroupId);
        }
        FinishOfflineCacheBatch();
        RefreshOfflineCachePresentation();
        StatusMessage = ((unsupportedCount > 0) ? ("《" + groupTitle + "》已缓存完成，部分远程源或不可访问文件已跳过。") : ("《" + groupTitle + "》已缓存完成。"));
    }

    private async Task CopyOfflineCacheFileAsync(OfflineCacheFilePlan plan, long groupTotalBytes, Func<long> groupCopiedBytes, Action<long> addGroupCopiedBytes, string? posterGroupId, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(plan.DestinationPath) ?? ResolveOfflineCacheDirectory() ?? string.Empty);
        string tempPath = plan.DestinationPath + ".tmp";
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }
        byte[] buffer = new byte[1048576];
        long copiedForFile = 0L;
        await using FileStream destination = File.Create(tempPath);
        if ((object)plan.RemoteUri != null)
        {
            await DownloadRemoteOfflineCacheFileAsync(plan, destination, groupTotalBytes, groupCopiedBytes, posterGroupId, cancellationToken);
            copiedForFile = plan.FileSize;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(plan.SourcePath))
            {
                throw new FileNotFoundException("源文件不存在。");
            }
            await using FileStream source = File.OpenRead(plan.SourcePath);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = await source.ReadAsync(buffer);
                if (read <= 0)
                {
                    break;
                }
                await destination.WriteAsync(buffer.AsMemory(0, read));
                copiedForFile += read;
                double fileProgress = ((plan.FileSize > 0) ? Math.Clamp((double)copiedForFile / (double)plan.FileSize, 0.01, 0.999) : 0.5);
                offlineCacheProgressByFileId[plan.File.Id] = fileProgress;
                if (!string.IsNullOrWhiteSpace(posterGroupId) && groupTotalBytes > 0)
                {
                    double groupProgress = Math.Clamp((double)(groupCopiedBytes() + copiedForFile) / (double)groupTotalBytes, 0.01, 0.999);
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

    private async Task DownloadRemoteOfflineCacheFileAsync(OfflineCacheFilePlan plan, FileStream destination, long groupTotalBytes, Func<long> groupCopiedBytes, string? posterGroupId, CancellationToken cancellationToken)
    {
        if ((object)plan.RemoteUri == null)
        {
            return;
        }
        using HttpClient client = new HttpClient
        {
            Timeout = TimeSpan.FromHours(6)
        };
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, plan.RemoteUri);
        if (!string.IsNullOrWhiteSpace(plan.RemoteAuthorization))
        {
            request.Headers.TryAddWithoutValidation("Authorization", plan.RemoteAuthorization);
        }
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        long expectedBytes = response.Content.Headers.ContentLength ?? plan.FileSize;
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        byte[] buffer = new byte[1048576];
        long copiedForFile = 0L;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
            {
                break;
            }
            await destination.WriteAsync(buffer.AsMemory(0, read));
            copiedForFile += read;
            double fileProgress = ((expectedBytes > 0) ? Math.Clamp((double)copiedForFile / (double)expectedBytes, 0.01, 0.999) : 0.5);
            offlineCacheProgressByFileId[plan.File.Id] = fileProgress;
            if (!string.IsNullOrWhiteSpace(posterGroupId) && groupTotalBytes > 0)
            {
                double groupProgress = Math.Clamp((double)(groupCopiedBytes() + copiedForFile) / (double)groupTotalBytes, 0.01, 0.999);
                offlinePosterProgressByItemId[posterGroupId] = groupProgress;
            }
            RefreshOfflineCachePresentation();
        }
    }

    private string? ResolveOfflineCacheDirectory()
    {
        string text = Settings.OfflineCacheDirectory.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? ResolveOfflineCacheSourcePath(LibraryVideoItem file)
    {
        if (!string.IsNullOrWhiteSpace(file.AbsolutePath) && File.Exists(file.AbsolutePath))
        {
            return file.AbsolutePath;
        }
        if (!string.IsNullOrWhiteSpace(file.PlaybackPath) && !Uri.TryCreate(file.PlaybackPath, UriKind.Absolute, out Uri _) && File.Exists(file.PlaybackPath))
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
        string uriString = ((!string.IsNullOrWhiteSpace(file.PlaybackPath)) ? file.PlaybackPath : file.AbsolutePath);
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out Uri result) || (result.Scheme != Uri.UriSchemeHttp && result.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(result.UserInfo))
        {
            string s = Uri.UnescapeDataString(result.UserInfo);
            string text = Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
            authorization = "Basic " + text;
            UriBuilder uriBuilder = new UriBuilder(result)
            {
                UserName = string.Empty,
                Password = string.Empty
            };
            result = uriBuilder.Uri;
        }
        else
        {
            WebDavAuthConfig webDavAuthConfig = MediaSourceAuthConfigSerializer.DeserializeWebDav(file.SourceAuthConfig);
            if ((object)webDavAuthConfig != null && !string.IsNullOrWhiteSpace(webDavAuthConfig.Username))
            {
                string text2 = Convert.ToBase64String(Encoding.UTF8.GetBytes(webDavAuthConfig.Username + ":" + webDavAuthConfig.Password));
                authorization = "Basic " + text2;
            }
        }
        return result;
    }

    private static async Task<long> GetRemoteContentLengthAsync(Uri remoteUri, string? authorization)
    {
        try
        {
            using HttpClient client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20L)
            };
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Head, remoteUri);
            if (!string.IsNullOrWhiteSpace(authorization))
            {
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            }
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                return 0L;
            }
            return response.Content.Headers.ContentLength.GetValueOrDefault();
        }
        catch
        {
            return 0L;
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
        return Path.Combine(cacheDirectory, SanitizePathSegment(file.Id), SanitizePathSegment(string.IsNullOrWhiteSpace(file.FileName) ? (file.Id + ".mp4") : file.FileName));
    }

    private bool IsOfflineCached(LibraryVideoItem file, out string? cachePath)
    {
        string text = ResolveOfflineCacheDirectory();
        if (string.IsNullOrWhiteSpace(text))
        {
            cachePath = null;
            return offlineCachedFileIds.Contains(file.Id);
        }
        cachePath = GetOfflineCachePath(file, text);
        bool flag = File.Exists(cachePath);
        if (flag)
        {
            offlineCachedFileIds.Add(file.Id);
        }
        return flag || offlineCachedFileIds.Contains(file.Id);
    }

    private LibraryVideoItem ApplyOfflineCacheState(LibraryVideoItem file)
    {
        bool flag = IsOfflineCached(file, out string cachePath);
        bool flag2 = offlineCacheProgressByFileId.TryGetValue(file.Id, out var value);
        return CopyVideoItem(file, (flag && cachePath != null) ? cachePath : file.OfflineCachePath, flag2, flag, offlineUnavailableFileIds.Contains(file.Id), flag2 ? value : ((double)(flag ? 1 : 0)));
    }

    private LibraryPosterItem ApplyOfflineCacheState(LibraryPosterItem item)
    {
        bool flag = offlinePosterProgressByItemId.TryGetValue(item.Id, out var value);
        return CopyPosterItem(item, item.WatchState, item.IsContinuing, flag, offlineCachedPosterIds.Contains(item.Id), offlineUnavailablePosterIds.Contains(item.Id), flag ? value : ((double)(offlineCachedPosterIds.Contains(item.Id) ? 1 : 0)));
    }

    private void RefreshOfflineCachePresentation()
    {
        ApplyFilters();
        if (IsDetailOpen)
        {
            string value = DetailPrimaryFile?.Id;
            if (!string.IsNullOrWhiteSpace(value))
            {
                preferredDetailVideoId = value;
            }
            ApplyDetailFilter();
        }
        else
        {
            UpdateSelectedSeasonOfflineProgress();
        }
        UpdateOfflineCacheTopBarPresentation();
    }

    private void UpdateOfflineCacheTopBarPresentation()
    {
        if (offlineCacheBatchTotalCount > 0)
        {
            string text = offlineCacheBatchCurrentFileId;
            if (text != null && offlineCacheProgressByFileId.TryGetValue(text, out var value))
            {
                double num = Math.Clamp(((double)offlineCacheBatchCompletedCount + value) / (double)offlineCacheBatchTotalCount, 0.0, 1.0);
                ShowOfflineCacheTopBar = true;
                OfflineCacheTopBarTitle = offlineCacheActiveTitle ?? "离线缓存";
                OfflineCacheTopBarDetail = $"{offlineCacheBatchCurrentFileName ?? "等待缓存"} · 第 {Math.Min(offlineCacheBatchCompletedCount + 1, offlineCacheBatchTotalCount)}/{offlineCacheBatchTotalCount} 集 · {num * 100.0:F0}%";
                OfflineCacheTopBarProgress = num;
                return;
            }
        }
        ShowOfflineCacheTopBar = false;
        OfflineCacheTopBarTitle = string.Empty;
        OfflineCacheTopBarDetail = string.Empty;
        OfflineCacheTopBarProgress = 0.0;
    }

    private void UpdateSelectedSeasonOfflineProgress()
    {
        List<LibraryVideoItem> list = allDetailFiles.Where((LibraryVideoItem file) => !IsDetailSeries || file.SeasonNumber == SelectedSeason).ToList();
        if (list.Count == 0 || !list.Any((LibraryVideoItem file) => offlineCacheProgressByFileId.ContainsKey(file.Id)))
        {
            ShowSelectedSeasonOfflineCacheProgress = false;
            SelectedSeasonOfflineCacheProgress = 0.0;
            UpdateDetailPosterOfflineState();
            return;
        }
        double num = list.Sum((LibraryVideoItem file) => IsOfflineCached(file, out string _) ? 1.0 : (offlineCacheProgressByFileId.TryGetValue(file.Id, out var value) ? Math.Clamp(value, 0.0, 1.0) : 0.0));
        SelectedSeasonOfflineCacheProgress = num / (double)list.Count;
        ShowSelectedSeasonOfflineCacheProgress = true;
        UpdateDetailPosterOfflineState();
    }

    private void UpdateDetailPosterOfflineState()
    {
        string text = CurrentDetailOfflineGroupId();
        double value;
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowDetailPosterOfflineCacheProgress = false;
            DetailPosterOfflineCacheProgress = 0.0;
            DetailPosterOfflineCacheGlyph = "↓";
            DetailPosterOfflineCacheTip = "离线缓存整部影片或剧集";
        }
        else if (offlinePosterProgressByItemId.TryGetValue(text, out value))
        {
            ShowDetailPosterOfflineCacheProgress = true;
            DetailPosterOfflineCacheProgress = Math.Clamp(value, 0.0, 1.0);
            DetailPosterOfflineCacheTip = "正在离线缓存";
        }
        else
        {
            ShowDetailPosterOfflineCacheProgress = false;
            DetailPosterOfflineCacheProgress = (offlineCachedPosterIds.Contains(text) ? 1 : 0);
            DetailPosterOfflineCacheGlyph = (offlineCachedPosterIds.Contains(text) ? "✓" : "↓");
            DetailPosterOfflineCacheTip = (offlineCachedPosterIds.Contains(text) ? "已缓存到本地" : "离线缓存整部影片或剧集");
        }
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

    private bool IsOfflineCacheInProgressForFiles(IEnumerable<LibraryVideoItem> files)
    {
        return files?.Any((LibraryVideoItem file) => !string.IsNullOrWhiteSpace(file.Id) && offlineCacheProgressByFileId.ContainsKey(file.Id)) ?? false;
    }

    private void BeginOfflineCacheBatch(string? groupTitle, int planCount)
    {
        offlineCacheActiveTitle = (string.IsNullOrWhiteSpace(groupTitle) ? "离线缓存" : groupTitle.Trim());
        offlineCacheBatchTotalCount = planCount;
        offlineCacheBatchCompletedCount = 0;
        offlineCacheBatchCurrentFileId = null;
        offlineCacheBatchCurrentFileName = null;
    }

    private void FinishOfflineCacheBatch()
    {
        offlineCacheBatchTotalCount = 0;
        offlineCacheBatchCompletedCount = 0;
        offlineCacheBatchCurrentFileId = null;
        offlineCacheBatchCurrentFileName = null;
        offlineCacheActiveTitle = null;
    }

    private void RequestCancelOfflineCache()
    {
        IsCancelOfflineCacheConfirmationOpen = true;
        OnCommandStateChanged();
    }

    private void KeepOfflineCache()
    {
        IsCancelOfflineCacheConfirmationOpen = false;
        OnCommandStateChanged();
    }

    private void ConfirmCancelOfflineCache()
    {
        IsCancelOfflineCacheConfirmationOpen = false;
        isOfflineCacheCancellationRequested = true;
        offlineCacheCancellationTokenSource?.Cancel();
        StatusMessage = "正在取消离线缓存…";
        OnCommandStateChanged();
    }

    private void OpenPosterWatchStateConfirmation(LibraryPosterItem? item)
    {
        if (item != null)
        {
            pendingPosterWatchStateMarkWatched = item.WatchState != PlaybackWatchState.Watched;
            posterWatchStateTarget = item;
            IsSearchPopupOpen = false;
            IsSortPopupOpen = false;
            IsSourcePopupOpen = false;
            IsSettingsPopupOpen = false;
            IsPosterScrapePanelOpen = false;
            IsPosterEditPanelOpen = false;
            IsEpisodeEditPanelOpen = false;
            string value = (pendingPosterWatchStateMarkWatched ? "已播" : "未播");
            string value2 = (string.IsNullOrWhiteSpace(item.MediaKind) ? "条目" : item.MediaKind);
            PosterWatchStateConfirmationTitle = (pendingPosterWatchStateMarkWatched ? "标记为已播" : "标记为未播");
            PosterWatchStateConfirmationMessage = $"确认将《{item.Title}》整部{value2}标记为{value}？这会更新其所有视频文件的播放进度。";
            PosterWatchStateConfirmationActionText = (pendingPosterWatchStateMarkWatched ? "标记为已播" : "标记为未播");
            IsPosterWatchStateConfirmationOpen = true;
            OnCommandStateChanged();
        }
    }

    private void ClosePosterWatchStateConfirmation()
    {
        ResetPosterWatchStateConfirmation();
        OnCommandStateChanged();
    }

    private async Task ConfirmPosterWatchStateAsync()
    {
        LibraryPosterItem target = posterWatchStateTarget;
        if (target == null)
        {
            return;
        }
        IsBusy = true;
        OnCommandStateChanged();
        try
        {
            IReadOnlyList<LibraryVideoItem> files = await LoadPosterTargetFilesAsync(target);
            if (files.Count == 0)
            {
                StatusMessage = "《" + target.Title + "》没有可更新的视频文件。";
                return;
            }
            foreach (LibraryVideoItem file in files)
            {
                double duration = ((file.Duration > 0.0) ? file.Duration : 100.0);
                await videoFileRepository.UpdatePlaybackStateAsync(file.Id, pendingPosterWatchStateMarkWatched ? duration : 0.0, pendingPosterWatchStateMarkWatched ? new double?(duration) : ((double?)null));
            }
            await RefreshContinueWatchingAsync();
            if (IsPosterTargetCurrentDetail(target))
            {
                await RefreshDetailFilesAsync();
            }
            string actionText = (pendingPosterWatchStateMarkWatched ? "已播" : "未播");
            StatusMessage = $"已将《{target.Title}》整部标记为{actionText}。";
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
        return Array.Empty<LibraryVideoItem>();
    }

    private bool IsPosterTargetCurrentDetail(LibraryPosterItem item)
    {
        return IsDetailOpen && ((item.MovieId.HasValue && currentDetailMovieId == item.MovieId.Value) || (item.TvShowId.HasValue && currentDetailTvShowId == item.TvShowId.Value));
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
        if (item != null)
        {
            await PreparePosterMetadataTargetAsync(item);
            PosterPanelTitle = "重新匹配 TMDB · " + item.Title;
            PosterScrapeQuery = ResolveInitialPosterScrapeQuery(item, PosterSourceFileText);
            PosterScrapeYear = ExtractFourDigitYear(item.Subtitle) ?? ExtractFourDigitYear(PosterSourceFileText) ?? string.Empty;
            PosterScrapeStatus = "正在按当前条目自动搜索 TMDB 候选结果...";
            StatusMessage = PosterScrapeStatus;
            ReplaceItems(PosterMetadataCandidates, Array.Empty<LibraryMetadataSearchCandidate>());
            IsPosterEditPanelOpen = false;
            IsPosterScrapePanelOpen = true;
            await LoadPosterDoubanBindingAsync();
            OnCommandStateChanged();
            await SearchPosterMetadataCandidatesAsync();
        }
    }

    private void ClosePosterScrape()
    {
        IsPosterScrapePanelOpen = false;
        ReplaceItems(PosterMetadataCandidates, Array.Empty<LibraryMetadataSearchCandidate>());
        PosterScrapeStatus = string.Empty;
        PosterScrapeYear = string.Empty;
        PosterSourceFileText = string.Empty;
        PosterDoubanSubject = string.Empty;
        PosterDoubanStatus = string.Empty;
        HasPosterDoubanBinding = false;
        PosterDoubanBindingSummary = string.Empty;
        posterMetadataTarget = null;
        OnCommandStateChanged();
    }

    private async Task SearchPosterMetadataCandidatesAsync()
    {
        LibraryPosterItem target = posterMetadataTarget;
        if (target == null)
        {
            return;
        }
        IsBusy = true;
        OnCommandStateChanged();
        try
        {
            string query = NormalizePosterQuery(target);
            string year = NormalizePosterYear();
            PosterScrapeStatus = "正在搜索 TMDB 候选结果...";
            StatusMessage = PosterScrapeStatus;
            IReadOnlyList<LibraryMetadataSearchCandidate> readOnlyList = ((!target.MovieId.HasValue) ? (await libraryMetadataEditor.SearchTvShowMatchesAsync(target.TvShowId.Value, query, year)) : (await libraryMetadataEditor.SearchMovieMatchesAsync(target.MovieId.Value, query, year)));
            IReadOnlyList<LibraryMetadataSearchCandidate> candidates = readOnlyList;
            ReplaceItems(PosterMetadataCandidates, candidates);
            PosterScrapeStatus = ((candidates.Count > 0) ? BuildDetailMetadataCandidatesMessage(candidates) : ("未找到与「" + BuildPosterSearchLabel(query, year) + "」匹配的 TMDB 候选结果。"));
            StatusMessage = PosterScrapeStatus;
        }
        catch (Exception ex) when (((ex is HttpRequestException || ex is TaskCanceledException) ? 1 : 0) != 0)
        {
            PosterScrapeStatus = "搜索 TMDB 候选结果失败：" + ex.Message;
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
        LibraryPosterItem target = posterMetadataTarget;
        if (target == null || (object)candidate == null)
        {
            return;
        }
        IsBusy = true;
        OnCommandStateChanged();
        try
        {
            long? tvShowIdToRefresh = target.TvShowId;
            PosterScrapeStatus = "正在应用所选 TMDB 匹配...";
            StatusMessage = PosterScrapeStatus;
            LibraryMetadataRefreshResult libraryMetadataRefreshResult = ((!target.MovieId.HasValue) ? (await libraryMetadataEditor.ApplyTvShowMatchAsync(target.TvShowId.Value, candidate)) : (await libraryMetadataEditor.ApplyMovieMatchAsync(target.MovieId.Value, candidate)));
            LibraryMetadataRefreshResult result = libraryMetadataRefreshResult;
            if (result.Updated)
            {
                await ReloadLibraryAsync();
                ClosePosterScrape();
            }
            LibraryThumbnailEnrichmentSummary libraryThumbnailEnrichmentSummary = ((!tvShowIdToRefresh.HasValue) ? new LibraryThumbnailEnrichmentSummary() : (await RefreshTvShowThumbnailsAndDetailFilesAsync(tvShowIdToRefresh.Value)));
            LibraryThumbnailEnrichmentSummary thumbnailSummary = libraryThumbnailEnrichmentSummary;
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
        if (item != null)
        {
            await PreparePosterMetadataTargetAsync(item);
            PosterPanelTitle = "手动编辑资料 · " + item.Title;
            PosterEditTitle = item.Title;
            PosterEditDate = ((!item.MovieId.HasValue) ? (allTvShows.FirstOrDefault((TvShow show) => show.Id == item.TvShowId.Value)?.FirstAirDate ?? string.Empty) : (allMovies.FirstOrDefault((Movie movie) => movie.Id == item.MovieId.Value)?.ReleaseDate ?? string.Empty));
            PosterEditVoteAverage = (item.VoteAverage.HasValue ? item.VoteAverage.Value.ToString("0.0", CultureInfo.InvariantCulture) : string.Empty);
            PosterEditOverview = ((!item.MovieId.HasValue) ? (allTvShows.FirstOrDefault((TvShow show) => show.Id == item.TvShowId.Value)?.Overview ?? string.Empty) : (allMovies.FirstOrDefault((Movie movie) => movie.Id == item.MovieId.Value)?.Overview ?? string.Empty));
            PosterEditPosterPath = item.PosterPath ?? string.Empty;
            PosterEditDirector = ((!item.MovieId.HasValue) ? (allTvShows.FirstOrDefault((TvShow show) => show.Id == item.TvShowId.Value)?.Director ?? string.Empty) : (allMovies.FirstOrDefault((Movie movie) => movie.Id == item.MovieId.Value)?.Director ?? string.Empty));
            PosterEditCast = ((!item.MovieId.HasValue) ? (allTvShows.FirstOrDefault((TvShow show) => show.Id == item.TvShowId.Value)?.MainCast ?? string.Empty) : (allMovies.FirstOrDefault((Movie movie) => movie.Id == item.MovieId.Value)?.MainCast ?? string.Empty));
            IsPosterScrapePanelOpen = false;
            ReplaceItems(PosterMetadataCandidates, Array.Empty<LibraryMetadataSearchCandidate>());
            await LoadPosterDoubanBindingAsync();
            IsPosterEditPanelOpen = true;
            OnCommandStateChanged();
        }
    }

    private void ClosePosterEdit()
    {
        IsPosterEditPanelOpen = false;
        PosterSourceFileText = string.Empty;
        PosterDoubanSubject = string.Empty;
        PosterDoubanStatus = string.Empty;
        HasPosterDoubanBinding = false;
        PosterDoubanBindingSummary = string.Empty;
        posterMetadataTarget = null;
        OnCommandStateChanged();
    }

    private void OpenEpisodeEdit(LibraryVideoItem? video)
    {
        if (video != null && IsDetailSeries)
        {
            episodeEditTarget = video;
            EpisodeEditPanelTitle = "编辑分集 · " + video.SeasonEpisodeText;
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
        LibraryVideoItem target = episodeEditTarget;
        if (target == null)
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
        string year = NormalizeEpisodeYear(EpisodeEditYear);
        if (year == null && !string.IsNullOrWhiteSpace(EpisodeEditYear))
        {
            StatusMessage = "年份需要是 4 位数字。";
            return;
        }
        IsBusy = true;
        OnCommandStateChanged();
        try
        {
            preferredDetailVideoId = target.Id;
            await videoFileRepository.UpdateEpisodeMetadataAsync(target.Id, new LibraryEpisodeEditRequest(seasonNumber, episodeNumber, year, EpisodeEditSubtitle, EpisodeEditThumbnailPath));
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
        string imagePath = await posterImagePickerService.PickPosterImageAsync();
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
        IReadOnlyList<LibraryVideoItem> readOnlyList;
        if (item.MovieId.HasValue)
        {
            readOnlyList = await videoFileRepository.GetByMovieAsync(item.MovieId.Value);
        }
        else
        {
            IReadOnlyList<LibraryVideoItem> readOnlyList2 = ((!item.TvShowId.HasValue) ? Array.Empty<LibraryVideoItem>() : (await videoFileRepository.GetByTvShowAsync(item.TvShowId.Value)));
            readOnlyList = readOnlyList2;
        }
        IReadOnlyList<LibraryVideoItem> files = readOnlyList;
        LibraryVideoItem file = files.FirstOrDefault();
        if (file == null)
        {
            return "未找到关联的视频文件";
        }
        return FormatSourceFileDisplayName(file);
    }

    private static string FormatSourceFileDisplayName(LibraryVideoItem file)
    {
        if (MediaSourcePathResolver.IsMediaServerPlaybackEndpointPath(file.RelativePath))
        {
            return TryResolveDecodedDisplayPath(file.FileName) ?? TryResolveDecodedDisplayPath(file.MetadataPath) ?? file.FileName;
        }
        return TryResolveDecodedDisplayPath(file.RelativePath) ?? TryResolveDecodedDisplayPath(file.MetadataPath) ?? TryResolveDecodedDisplayPath(file.AbsolutePath) ?? TryResolveDecodedDisplayPath(file.PlaybackPath) ?? TryResolveDecodedDisplayPath(file.FileName) ?? file.FileName;
    }

    private static string? TryResolveDecodedDisplayPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        string text = value.Trim();
        string text2 = null;
        text2 = ((MediaSourcePathResolver.IsRemoteHttpUrl(text) && Uri.TryCreate(text, UriKind.Absolute, out Uri result)) ? result.AbsolutePath.Trim('/') : ((!IsUncPath(text)) ? text : RemoveUncServerAndShare(text)));
        text2 = DecodePercentEncodedText(NormalizeDisplayPath(text2 ?? text)).Trim();
        return string.IsNullOrWhiteSpace(text2) ? null : text2;
    }

    private static bool IsUncPath(string value)
    {
        return value.StartsWith("\\\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal);
    }

    private static string RemoveUncServerAndShare(string value)
    {
        string text = value.Replace('\\', '/').TrimStart('/');
        string[] array = text.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (array.Length > 2) ? string.Join('/', array.Skip(2)) : text;
    }

    private static string NormalizeDisplayPath(string value)
    {
        string text = value.Replace('\\', '/');
        while (text.Contains("//", StringComparison.Ordinal))
        {
            text = text.Replace("//", "/", StringComparison.Ordinal);
        }
        return text.Trim('/');
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
        string posterPath = await posterImagePickerService.PickPosterImageAsync();
        if (!string.IsNullOrWhiteSpace(posterPath))
        {
            PosterEditPosterPath = posterPath;
        }
    }

    private async Task BindPosterDoubanAsync()
    {
        if (posterMetadataTarget == null || doubanMetadataService == null)
        {
            PosterDoubanStatus = "当前版本未启用豆瓣绑定服务。";
            return;
        }
        string subject = PosterDoubanSubject.Trim();
        if (string.IsNullOrWhiteSpace(subject))
        {
            PosterDoubanStatus = "请填写豆瓣 subject ID 或链接。";
            return;
        }
        LibraryPosterItem target = posterMetadataTarget;
        bool isMovie = target.MovieId.HasValue;
        long entityId = (isMovie ? target.MovieId.Value : target.TvShowId.Value);
        IsBusy = true;
        OnCommandStateChanged();
        try
        {
            PosterDoubanStatus = "正在抓取豆瓣条目...";
            DoubanMetadata metadata = await doubanMetadataService.BindAsync(isMovie ? "movie" : "tv", entityId, subject, (PosterEditTitle.Trim().Length == 0) ? target.Title : PosterEditTitle.Trim(), PosterEditDate);
            string? appliedPoster = null;
            if (metadata is not null)
            {
                appliedPoster = await ApplyDoubanPosterToTargetAsync(isMovie, entityId, metadata);
            }
            await ReloadLibraryAsync();
            await RefreshOpenDetailIfNeededAsync();
            UpdatePosterDoubanPresentation(metadata);
            PosterDoubanStatus = ((metadata?.Rating.HasValue ?? false) ? $"豆瓣绑定成功：{metadata!.Title} {metadata.Rating.Value:F1}" : "豆瓣条目已保存；本次未获取到评分。");
            ShowToast(metadata?.Rating.HasValue ?? false ? $"豆瓣绑定成功：{metadata!.Title} {metadata.Rating.Value:F1}" : "豆瓣绑定成功", ToastKind.Success);
            if (appliedPoster is not null)
            {
                PosterDoubanStatus += " 已应用豆瓣海报。";
            }
            StatusMessage = PosterDoubanStatus;
        }
        catch (DoubanRateLimitException ex)
        {
            PosterDoubanStatus = ex.Message;
        }
        catch (ArgumentException ex)
        {
            PosterDoubanStatus = ex.Message;
        }
        catch (Exception ex)
        {
            PosterDoubanStatus = "豆瓣绑定失败：" + ex.Message;
            ShowToast("豆瓣绑定失败：" + ex.Message, ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private async Task LoadPosterDoubanBindingAsync()
    {
        LibraryPosterItem? target = posterMetadataTarget;
        if (target == null || doubanMetadataService == null)
        {
            HasPosterDoubanBinding = false;
            PosterDoubanBindingSummary = string.Empty;
            if (string.IsNullOrWhiteSpace(PosterDoubanSubject))
            {
                PosterDoubanStatus = "可填写豆瓣 subject ID 或链接进行手动绑定。";
            }
            return;
        }
        try
        {
            bool isMovie = target.MovieId.HasValue;
            long entityId = (isMovie ? target.MovieId.Value : target.TvShowId.Value);
            DoubanMetadata? metadata = await doubanMetadataService.GetAsync(isMovie ? "movie" : "tv", entityId);
            if (metadata is null || IsInvalidDoubanPlaceholder(metadata))
            {
                HasPosterDoubanBinding = false;
                PosterDoubanBindingSummary = string.Empty;
                PosterDoubanStatus = "可填写豆瓣 subject ID 或链接进行手动绑定。";
                return;
            }
            HasPosterDoubanBinding = true;
            PosterDoubanBindingSummary = (metadata.Rating.HasValue ? $"{metadata.Title} · {metadata.Rating.Value:F1}" : metadata.Title);
            PosterDoubanSubject = metadata.SubjectUrl;
            PosterDoubanStatus = "豆瓣仅支持手动绑定单个条目；不会自动搜索豆瓣，也不会参与批量扫描。";
        }
        catch
        {
            HasPosterDoubanBinding = false;
            PosterDoubanBindingSummary = string.Empty;
        }
    }

    private static bool IsInvalidDoubanPlaceholder(DoubanMetadata metadata)
    {
        var title = metadata.Title.Trim();
        return metadata.Rating is null && (title == "豆瓣" || title == "豆瓣电影");
    }

    private void OpenPosterDoubanSearch()
    {
        LibraryPosterItem? target = posterMetadataTarget;
        string query = (target?.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            PosterDoubanStatus = "当前条目缺少片名，无法生成豆瓣搜索链接。";
            return;
        }
        if (externalLinkOpener is null)
        {
            PosterDoubanStatus = "当前版本未启用浏览器打开服务。";
            return;
        }
        var searchUrl = "https://search.douban.com/movie/subject_search?search_text=" + Uri.EscapeDataString(query);
        if (!externalLinkOpener.TryOpen(searchUrl, out string? error))
        {
            PosterDoubanStatus = error ?? "无法打开浏览器搜索豆瓣。";
        }
    }

    private async Task UnbindPosterDoubanAsync()
    {
        LibraryPosterItem? target = posterMetadataTarget;
        if (target == null || doubanMetadataService == null)
        {
            PosterDoubanStatus = "当前版本未启用豆瓣绑定服务。";
            return;
        }
        bool isMovie = target.MovieId.HasValue;
        long entityId = (isMovie ? target.MovieId.Value : target.TvShowId.Value);
        IsBusy = true;
        OnCommandStateChanged();
        try
        {
            PosterDoubanStatus = "正在解除豆瓣绑定...";
            await doubanMetadataService.UnbindAsync(isMovie ? "movie" : "tv", entityId);
            string? currentPoster = (isMovie ? allMovies.FirstOrDefault((Movie movie) => movie.Id == entityId)?.PosterPath : allTvShows.FirstOrDefault((TvShow show) => show.Id == entityId)?.PosterPath);
            if (DoubanPosterUrls.IsUnusableDouban(currentPoster))
            {
                if (isMovie)
                {
                    await movieRepository.UpdatePosterPathAsync(entityId, null);
                }
                else
                {
                    await tvShowRepository.UpdatePosterPathAsync(entityId, null);
                }
            }
            await ReloadLibraryAsync();
            await RefreshOpenDetailIfNeededAsync();
            HasPosterDoubanBinding = false;
            PosterDoubanBindingSummary = string.Empty;
            PosterDoubanSubject = string.Empty;
            PosterDoubanStatus = "已解除豆瓣绑定。";
            StatusMessage = PosterDoubanStatus;
        }
        catch (Exception ex)
        {
            PosterDoubanStatus = "解除豆瓣绑定失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private async Task<string?> ApplyDoubanPosterToTargetAsync(bool isMovie, long entityId, DoubanMetadata metadata)
    {
        string? currentPoster = (isMovie ? allMovies.FirstOrDefault((Movie movie) => movie.Id == entityId)?.PosterPath : allTvShows.FirstOrDefault((TvShow show) => show.Id == entityId)?.PosterPath);
        // 对齐 mac 版 applyPosterToMovie：已有可用海报（本地或 TMDB 远程）时不覆盖。
        if (!string.IsNullOrWhiteSpace(currentPoster) && !DoubanPosterUrls.IsUnusableDouban(currentPoster))
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(metadata.PosterLocalPath))
        {
            if (isMovie)
            {
                await movieRepository.UpdatePosterPathAsync(entityId, metadata.PosterLocalPath);
            }
            else
            {
                await tvShowRepository.UpdatePosterPathAsync(entityId, metadata.PosterLocalPath);
            }
            return metadata.PosterLocalPath;
        }
        // 海报抓取失败且当前海报是失效豆瓣图时，清理掉避免显示破损海报。
        if (DoubanPosterUrls.IsUnusableDouban(currentPoster))
        {
            if (isMovie)
            {
                await movieRepository.UpdatePosterPathAsync(entityId, null);
            }
            else
            {
                await tvShowRepository.UpdatePosterPathAsync(entityId, null);
            }
        }
        return null;
    }

    private void UpdatePosterDoubanPresentation(DoubanMetadata? metadata)
    {
        if (metadata is null || IsInvalidDoubanPlaceholder(metadata))
        {
            HasPosterDoubanBinding = false;
            PosterDoubanBindingSummary = string.Empty;
            return;
        }
        HasPosterDoubanBinding = true;
        PosterDoubanBindingSummary = (metadata.Rating.HasValue ? $"{metadata.Title} · {metadata.Rating.Value:F1}" : metadata.Title);
        PosterDoubanSubject = metadata.SubjectUrl;
    }

    private async Task SavePosterMetadataEditAsync()
    {
        LibraryPosterItem target = posterMetadataTarget;
        if (target == null)
        {
            return;
        }
        string title = PosterEditTitle.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            StatusMessage = "名称不能为空。";
            return;
        }
        if (!TryParsePosterVoteAverage(out var voteAverage))
        {
            StatusMessage = "评分需要是 0 到 10 之间的数字。";
            return;
        }
        IsBusy = true;
        OnCommandStateChanged();
        try
        {
            LibraryMetadataEditRequest request = new LibraryMetadataEditRequest(title, PosterEditDate, PosterEditOverview, PosterEditPosterPath, voteAverage, Director: NormalizePeopleText(PosterEditDirector), MainCast: NormalizePeopleText(PosterEditCast));
            LibraryMetadataRefreshResult libraryMetadataRefreshResult = ((!target.MovieId.HasValue) ? (await libraryMetadataEditor.UpdateTvShowMetadataAsync(target.TvShowId.Value, request)) : (await libraryMetadataEditor.UpdateMovieMetadataAsync(target.MovieId.Value, request)));
            LibraryMetadataRefreshResult result = libraryMetadataRefreshResult;
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
        return PosterScrapeQuery = (string.IsNullOrWhiteSpace(PosterScrapeQuery) ? target.Title : PosterScrapeQuery.Trim());
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
        string text = sourceFileText.Replace('\\', '/');
        string text2 = ResolvePosterTitleSource(text);
        string input = (string.IsNullOrWhiteSpace(text2) ? text : text2);
        input = Regex.Replace(input, "[._]+", " ");
        input = Regex.Replace(input, "[\\[\\]\\(\\)\\{\\}]+", " ");
        input = Regex.Replace(input, "\\s+", " ").Trim();
        input = NormalizePosterTitleCandidate(input);
        string text3 = ChoosePosterReleaseYear(input);
        if (string.IsNullOrWhiteSpace(text3))
        {
            return ExtractPreferredPosterTitle(input);
        }
        foreach (Match item in Regex.Matches(input, "(?<!\\d)" + Regex.Escape(text3) + "(?!\\d)"))
        {
            string value = NormalizePosterTitleCandidate(input.Substring(0, item.Index));
            string text4 = ExtractPreferredPosterTitle(value);
            if (!string.IsNullOrWhiteSpace(text4))
            {
                return text4;
            }
        }
        return ExtractPreferredPosterTitle(input);
    }

    private static string? ExtractPreferredPosterTitle(string value)
    {
        if (!HasPosterTitleText(value))
        {
            return null;
        }
        string text = ExtractPosterChineseTitle(value);
        return string.IsNullOrWhiteSpace(text) ? value.Trim() : text;
    }

    private static string? ExtractPosterChineseTitle(string value)
    {
        List<string> list = new List<string>();
        IEnumerable<string> enumerable = from token in Regex.Split(value, "\\s+")
            select token.Trim(new char[16]
            {
                ' ', '.', '-', '_', '/', '\\', '(', ')', '[', ']',
                '{', '}', '（', '）', '【', '】'
            }) into token
            where token.Length > 0
            select token;
        foreach (string item in enumerable)
        {
            if (item.Any(IsCjkCharacter))
            {
                list.Add(item);
            }
            else if (list.Count > 0 && Regex.IsMatch(item, "^\\d{1,3}$") && !Regex.IsMatch(item, "^(19|20)\\d{2}$"))
            {
                list.Add(item);
            }
            else if (list.Count > 0)
            {
                break;
            }
        }
        return (list.Count == 0) ? null : string.Join(' ', list).Trim();
    }

    private static bool HasPosterTitleText(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Any((char character) => char.IsLetter(character) || IsCjkCharacter(character));
    }

    private static bool IsCjkCharacter(char character)
    {
        return character >= '一' && character <= '鿿';
    }

    private static string ResolvePosterTitleSource(string normalizedPath)
    {
        string[] array = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int num = Array.FindIndex(array, (string part) => part.Equals("BDMV", StringComparison.OrdinalIgnoreCase));
        if (num > 0)
        {
            string text = array[num - 1];
            return IsPosterReleaseGroupWrapperFolder(text, (num > 1) ? array[num - 2] : string.Empty) ? array[num - 2] : text;
        }
        return Path.GetFileNameWithoutExtension(normalizedPath);
    }

    private static string? ChoosePosterReleaseYear(string text)
    {
        List<string> list = (from match in Regex.Matches(text, "(?<!\\d)(19\\d{2}|20\\d{2})(?!\\d)")
            select match.Value).Distinct<string>(StringComparer.Ordinal).ToList();
        if (list.Count == 0)
        {
            return null;
        }
        if (list.Count >= 2)
        {
            string text2 = Regex.Replace(text.Substring(0, Math.Min(text.Length, 12)), "[.\\-_/\\s]+", string.Empty);
            if (text2.StartsWith(list[0], StringComparison.Ordinal))
            {
                return list[1];
            }
        }
        return list[0];
    }

    private static string NormalizePosterTitleCandidate(string value)
    {
        string input = Regex.Replace(value, "[._]+", " ");
        input = Regex.Replace(input, "\\s+", " ").Trim();
        input = TruncatePosterReleaseTail(input);
        return RemovePosterReleaseTitleNoise(input.Trim(new char[12]
        {
            ' ', '.', '-', '_', '/', '\\', '(', ')', '[', ']',
            '{', '}'
        }));
    }

    private static string TruncatePosterReleaseTail(string value)
    {
        List<string> list = (from token in Regex.Split(value, "\\s+")
            where !string.IsNullOrWhiteSpace(token)
            select token).ToList();
        for (int num = 0; num < list.Count; num++)
        {
            if (IsPosterReleaseToken(list[num]))
            {
                string text = string.Join(' ', list.Take(num)).Trim();
                if (HasPosterTitleText(text))
                {
                    return text;
                }
            }
        }
        return value;
    }

    private static bool IsPosterReleaseToken(string token)
    {
        string input = token.Trim().ToLowerInvariant();
        return Regex.IsMatch(input, "^s\\d{1,2}e[p]?\\d{1,3}$") || Regex.IsMatch(input, "^s\\d{1,2}$") || Regex.IsMatch(input, "^ep?\\d{1,3}$") || Regex.IsMatch(input, "^episode\\d{1,3}$") || Regex.IsMatch(input, "^\\d{3,4}p$") || Regex.IsMatch(input, "^(4k|uhd|hdtv|uhdtv|hdr|hlg|dv|atmos|ddp\\d*(\\.\\d+)?|aac\\d*(\\.\\d+)?|ac3|eac3|dts|dts[- ]?hd|dts[- ]?x|dtsx|truehd|flac|lpcm|avc|hevc)$") || Regex.IsMatch(input, "^(x|h)?26[45]$") || Regex.IsMatch(input, "^(web|web-?dl|webdl|webrip|bluray|bdrip|remux|blu-ray)$") || Regex.IsMatch(input, "^(nf|amzn|netflix|dsnp|disney|hmax|max|atvp|hulu|cr)$") || Regex.IsMatch(input, "^(bonus|extra|extras|featurette|trailer|sample|complete)$");
    }

    private static string RemovePosterReleaseTitleNoise(string value)
    {
        string input = Regex.Replace(value, "[._]+", " ");
        input = Regex.Replace(input, "\\s+", " ").Trim();
        input = RemovePosterDecorativeBracketSegments(input);
        input = Regex.Replace(input, "(?i)^(?:disc|disk|cd|dvd|vol|volume)\\s*\\d{0,2}\\s*[-–—:]+\\s*", string.Empty);
        input = Regex.Replace(input, "^[\\s._\\-–—:]+", string.Empty);
        input = Regex.Replace(input, "(?i)\\b(?:cctv4k|cctv)\\b", " ");
        input = Regex.Replace(input, "(?i)\\b[s]\\d{1,2}[e][p]?\\d{1,3}\\b", " ");
        input = Regex.Replace(input, "(?i)\\b[s]\\d{1,2}\\b", " ");
        input = Regex.Replace(input, "(?i)\\bepisode\\s*\\d{1,3}\\b", " ");
        input = Regex.Replace(input, "(?i)\\b(?:1080p|2160p|720p|480p|4k|uhd|hdtv|uhdtv|blu[- ]?ray|bluray|web[- ]?dl|webdl|webrip|remux|x264|x265|h\\.?264|h\\.?265|hevc|hdr|hlg|dv|ddp\\d*(?:\\.\\d+)?|dts(?:-hd|-x)?|dtsx|aac|atmos|avc|flac|lpcm|truehd|complete|nf|cmct|hdsky|adweb)\\b", " ");
        input = Regex.Replace(input, "(?:剧场版|纪念版|花絮|特典|番外|幕后花絮)", " ");
        string text = Regex.Replace(input, "(?i)(?:\\s*[-–—:]\\s*|\\s+)the\\s+movie\\s*$", string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            input = text;
        }
        return Regex.Replace(input, "\\s+", " ").Trim();
    }

    private static string RemovePosterDecorativeBracketSegments(string value)
    {
        return Regex.Replace(value, "[\\[\\(\\{（【]\\s*[^]\\)\\}）】]*(?:剧场版|纪念版|花絮|特典|番外|幕后花絮|anniversary|edition|featurette|bonus|extra|extras|trailer)[^]\\)\\}）】]*[\\]\\)\\}）】]", " ", RegexOptions.IgnoreCase);
    }

    private static bool IsPosterReleaseGroupWrapperFolder(string input, string ancestor)
    {
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(ancestor) || input.Any(IsCjkCharacter) || ChoosePosterReleaseYear(input) != null || !HasLikelyPosterTitleSignal(ancestor))
        {
            return false;
        }
        string[] array = (from token in Regex.Split(input.Trim(), "[\\s._\\-@#]+")
            where !string.IsNullOrWhiteSpace(token)
            select token).ToArray();
        return array.Length >= 2 && array.All((string token) => Regex.IsMatch(token, "^[A-Z0-9]{2,10}$", RegexOptions.CultureInvariant));
    }

    private static bool HasLikelyPosterTitleSignal(string value)
    {
        return value.Any(IsCjkCharacter) || ChoosePosterReleaseYear(value) != null;
    }

    private string NormalizePosterYear()
    {
        string text = ExtractFourDigitYear(PosterScrapeYear);
        if (text != null)
        {
            PosterScrapeYear = text;
            return text;
        }
        return PosterScrapeYear = PosterScrapeYear.Trim();
    }

    private static string BuildPosterSearchLabel(string query, string year)
    {
        return string.IsNullOrWhiteSpace(year) ? query : (query + " / " + year);
    }

    private static string? ExtractFourDigitYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        Match match = Regex.Match(value, "(?<!\\d)(19\\d{2}|20\\d{2})(?!\\d)");
        return match.Success ? match.Value : null;
    }

    private bool TryParsePosterVoteAverage(out double? value)
    {
        string text = PosterEditVoteAverage.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            value = null;
            return true;
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out result))
        {
            value = Math.Clamp(result, 0.0, 10.0);
            return true;
        }
        value = null;
        return false;
    }

    private static bool TryParseEpisodeNumber(string value, bool allowZero, out int number)
    {
        number = 0;
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            return false;
        }
        if (allowZero ? (result < 0) : (result <= 0))
        {
            return false;
        }
        number = result;
        return true;
    }

    private static string? NormalizeEpisodeYear(string? value)
    {
        string text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }
        return (text.Length == 4 && text.All(char.IsDigit)) ? text : null;
    }

    private static string FormatRatingText(double? rating)
    {
        return (rating.HasValue) ? rating.Value.ToString("0.0", CultureInfo.InvariantCulture) : string.Empty;
    }

    private async Task ClosePlayerOverlayAsync()
    {
        StopPlaybackProgressSync();
        double progress = Math.Max(Player.CurrentPositionSeconds, 0.0);
        double duration = Math.Max(Player.DurationSeconds, 0.0);
        string currentVideoId = currentPlayingVideoId;
        bool shouldPersistPlaybackState = !string.IsNullOrWhiteSpace(currentVideoId) && !ShouldSuppressPlaybackPersistence(currentVideoId);
        PendingPlaybackStartPositionSeconds = 0.0;
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
        StatusMessage = "已关闭播放器覆盖层。";
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
            RefreshNetworkSourcesAsync();
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
        ReplaceItems(DetailFiles, Array.Empty<LibraryVideoItem>());
        ReplaceItems(AvailableSeasons, Array.Empty<int>());
        selectedPlaybackNavigationSeason = null;
        DetailPrimaryFile = null;
        IsDetailLocked = false;
        DetailLockStateText = "未锁定";
        DetailLockActionText = "锁定元数据";
        DetailScrapeQuery = string.Empty;
        HasDetailDirector = false;
        DetailDirectorLine = string.Empty;
        HasDetailCast = false;
        DetailCastLine = string.Empty;
        ClearDetailMetadataCandidates();
        RefreshDetailHeaderState();
        ResetAutoAdvanceState();
        UpdatePlayerNextEpisodeAction();
        OnPropertyChanged("HasSeasonTabs");
        OnCommandStateChanged();
    }

    private void SelectSortOption(LibrarySortOptionItem? option)
    {
        if (option != null)
        {
            SelectedSortOptionItem = option;
            IsSortPopupOpen = false;
        }
    }

    private void ToggleSortDirection()
    {
        IsSortDescending = !IsSortDescending;
    }

    private void ToggleHomeCategoryMenu()
    {
        IsSearchPopupOpen = false;
        IsSortPopupOpen = false;
        IsSourcePopupOpen = false;
        IsSettingsPopupOpen = false;
        IsHomeCategoryMenuOpen = !IsHomeCategoryMenuOpen;
    }

    private async Task SelectHomeCategoryAsync(string? mode)
    {
        string normalized = NormalizeHomeCategoryModeChoice(mode);
        IsHomeCategoryMenuOpen = false;
        if (normalized == HomeCategoryMode)
        {
            return;
        }

        HomeCategoryMode = normalized;
        OpenFolderKey = null;
        ClearOpenFolderBio();
        homeFolderRandomPosters.Clear();
        await RefreshHomeCategoryCacheAsync();
        ApplyFilters();
        try
        {
            await Settings.SaveHomeCategoryPreferenceAsync(normalized);
        }
        catch (Exception)
        {
            // Categorization still applies for this session if persistence fails.
        }
    }

    private void CloseHomeFolder()
    {
        if (string.IsNullOrEmpty(OpenFolderKey))
        {
            return;
        }

        OpenFolderKey = null;
        ApplyFilters();
        ClearOpenFolderBio();
    }

    /// <summary>卡片右上角的"自定义编辑"按钮：打开该分类文件夹的封面编辑器。</summary>
    private Task EditHomeFolderPosterAsync(LibraryPosterItem? item) =>
        OpenFolderPosterEditorAsync(item is { IsFolder: true } ? item.FolderKey : null, item?.Title);

    private async Task OpenFolderPosterEditorAsync(string? folderKey, string? title)
    {
        if (string.IsNullOrEmpty(folderKey) || IsBusy)
        {
            return;
        }

        await RefreshHomeCategoryCacheAsync();
        (string mode, string name) = SplitFolderKey(folderKey);
        bool isPerson = !string.Equals(name, UnknownFolderName, StringComparison.OrdinalIgnoreCase);
        FolderPosterEditorFolderKey = folderKey;
        FolderPosterEditorTitle = title ?? string.Empty;
        folderPosterEditorKind = HomeCategoryStoreKind(mode);
        FolderPosterEditorNameLabel = (isPerson ? CategoryFolderKindLabel(folderPosterEditorKind) : string.Empty) + "\u540D\u79F0";
        FolderPosterEditorDraftName = name;
        IsFolderPersonRenameVisible = isPerson && categoryFolderStore is not null;
        BuildFolderPosterCandidates();
        IsFolderPosterEditorOpen = true;
        NotifyFolderPosterCommands();
    }

    private static string CategoryFolderKindLabel(string kind) => kind == "cast" ? "\u4E3B\u6F14" : "\u5BFC\u6F14";

    partial void OnFolderPosterEditorDraftNameChanged(string value)
    {
        RenameFolderPersonCommand.NotifyCanExecuteChanged();
    }

    /// <summary>手动改导演/主演名：库内名单、文件夹、人物档案一起迁移，并记住这个名字。</summary>
    private async Task RenameFolderPersonAsync()
    {
        string folderKey = FolderPosterEditorFolderKey ?? string.Empty;
        string newName = (FolderPosterEditorDraftName ?? string.Empty).Trim();
        if (categoryFolderStore is null || IsBusy || string.IsNullOrEmpty(folderKey) || newName.Length == 0)
        {
            return;
        }

        (string mode, string name) = SplitFolderKey(folderKey);
        if (string.Equals(newName, name, StringComparison.Ordinal))
        {
            return;
        }

        IsBusy = true;
        OnCommandStateChanged();
        string newKey = mode + "|" + newName;
        bool wasOpen = string.Equals(OpenFolderKey, folderKey, StringComparison.OrdinalIgnoreCase);
        try
        {
            bool renamed = await categoryFolderStore.RenamePersonAsync(folderPosterEditorKind, name, newName);
            homeFolderRandomPosters.Remove(folderKey);
            homeFolderRandomPosters.Remove(newKey);
            if (wasOpen)
            {
                OpenFolderKey = newKey;
            }

            await ReloadLibraryAsync();
            if (renamed)
            {
                FolderPosterEditorFolderKey = newKey;
                FolderPosterEditorTitle = newName;
                FolderPosterEditorDraftName = newName;
                StatusMessage = $"\u5DF2\u5C06{FolderPosterEditorNameLabel}\u300C{name}\u300D\u6539\u4E3A\u300C{newName}\u300D\u3002";
            }
            else
            {
                StatusMessage = $"\u300C{name}\u300D\u6CA1\u6709\u6539\u52A8\u3002";
            }

            BuildFolderPosterCandidates();
            if (wasOpen)
            {
                RefreshOpenFolderBio();
                _ = RefreshOpenFolderFilmographyAsync();
            }
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
            NotifyFolderPosterCommands();
        }
    }

    private void CloseFolderPosterEditor()
    {
        IsFolderPosterEditorOpen = false;
        FolderPosterEditorFolderKey = null;
        FolderPosterCandidates = [];
        FolderPosterEditorCurrentPath = null;
        IsFolderPersonRenameVisible = false;
        FolderPosterEditorDraftName = string.Empty;
        FolderPosterEditorNameLabel = string.Empty;
        folderPosterEditorKind = string.Empty;
        NotifyFolderPosterCommands();
    }

    private void NotifyFolderPosterCommands()
    {
        ChooseFolderLocalPosterCommand.NotifyCanExecuteChanged();
        ResetFolderPosterCommand.NotifyCanExecuteChanged();
        RenameFolderPersonCommand.NotifyCanExecuteChanged();
    }

    /// <summary>候选海报：该分类下已刮削到的海报去重后按片名排序，当前封面置顶并标记。</summary>
    private void BuildFolderPosterCandidates()
    {
        string folderKey = FolderPosterEditorFolderKey ?? string.Empty;
        (string mode, string name) = SplitFolderKey(folderKey);
        bool isPerson = !string.Equals(name, UnknownFolderName, StringComparison.OrdinalIgnoreCase);
        List<LibraryPosterItem> items = FolderItemsFor(mode, name);
        string? current = ResolveHomeFolderPoster(isPerson ? name : null, folderKey, items);

        List<FolderPosterCandidate> candidates = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (LibraryPosterItem item in items.OrderBy((LibraryPosterItem x) => x.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!IsUsablePosterPath(item.PosterPath) || !seen.Add(item.PosterPath!))
            {
                continue;
            }

            candidates.Add(new FolderPosterCandidate
            {
                PosterPath = item.PosterPath!,
                Title = item.Title,
                MediaKind = item.MediaKind,
                IsCurrent = string.Equals(item.PosterPath, current, StringComparison.Ordinal)
            });
        }

        // 当前封面可能是本地图片或旧设置里的路径，不在本分类内，单独补一条置顶展示。
        if (IsUsablePosterPath(current) && !candidates.Any((FolderPosterCandidate c) => c.IsCurrent))
        {
            candidates.Insert(0, new FolderPosterCandidate
            {
                PosterPath = current!,
                Title = string.IsNullOrWhiteSpace(FolderPosterEditorTitle) ? "\u6587\u4EF6\u5939\u5C01\u9762" : FolderPosterEditorTitle,
                MediaKind = "\u81EA\u5B9A\u4E49",
                IsCurrent = true
            });
        }
        else
        {
            candidates = [.. candidates.OrderByDescending((FolderPosterCandidate c) => c.IsCurrent)];
        }

        FolderPosterCandidates = candidates;
        FolderPosterEditorCurrentPath = current;
        OnPropertyChanged(nameof(HasFolderPosterCandidates));
        OnPropertyChanged(nameof(HasFolderPosterEditorCurrent));
    }

    /// <summary>某个分类文件夹里的全部影视；封面候选不受搜索词影响。</summary>
    private List<LibraryPosterItem> FolderItemsFor(string mode, string folderName)
    {
        return [.. allLibraryItems.Where((LibraryPosterItem x) => !x.IsFolder && MatchesHomeFolder(x, mode, folderName))];
    }

    private Task SelectFolderPosterAsync(FolderPosterCandidate? candidate) =>
        ApplyFolderPosterAsync(candidate?.PosterPath);

    private async Task ChooseFolderLocalPosterAsync()
    {
        string? picked = await posterImagePickerService.PickPosterImageAsync();
        if (!string.IsNullOrWhiteSpace(picked))
        {
            await ApplyFolderPosterAsync(picked);
        }
    }

    private Task ResetFolderPosterAsync() => ApplyFolderPosterAsync(null);

    /// <summary>写入或清除文件夹封面：档案与旧设置各存一份，然后刷新卡片、详情页和编辑器。</summary>
    private async Task ApplyFolderPosterAsync(string? posterPath)
    {
        string folderKey = FolderPosterEditorFolderKey ?? string.Empty;
        if (string.IsNullOrEmpty(folderKey) || IsBusy)
        {
            return;
        }

        IsBusy = true;
        OnCommandStateChanged();
        try
        {
            (string mode, string name) = SplitFolderKey(folderKey);
            await Settings.SaveHomeFolderPosterAsync(folderKey, posterPath);
            if (categoryFolderStore is not null && !string.Equals(name, UnknownFolderName, StringComparison.OrdinalIgnoreCase))
            {
                await categoryFolderStore.SetFolderPosterAsync(HomeCategoryStoreKind(mode), name, posterPath);
            }

            homeFolderRandomPosters.Remove(folderKey);
            await RefreshHomeCategoryCacheAsync();
            ApplyFilters();
            if (string.Equals(OpenFolderKey, folderKey, StringComparison.OrdinalIgnoreCase))
            {
                RefreshOpenFolderBio();
            }

            StatusMessage = string.IsNullOrWhiteSpace(posterPath)
                ? $"\u5DF2\u6062\u590D\u300C{FolderPosterEditorTitle}\u300D\u7684\u9ED8\u8BA4\u5C01\u9762\u3002"
                : $"\u5DF2\u4E3A\u300C{FolderPosterEditorTitle}\u300D\u8BBE\u7F6E\u5C01\u9762\u3002";
            BuildFolderPosterCandidates();
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private IReadOnlyList<LibraryPosterItem> ApplyHomeCategorization(IReadOnlyList<LibraryPosterItem> items)
    {
        if (!string.IsNullOrEmpty(OpenFolderKey) || HomeCategoryMode is not ("director" or "actor"))
        {
            return items;
        }

        List<string> orderedKeys = [];
        Dictionary<string, List<LibraryPosterItem>> groups = new(StringComparer.OrdinalIgnoreCase);
        foreach (LibraryPosterItem item in items)
        {
            if (item.IsFolder)
            {
                continue;
            }

            // 一部影视会同时出现在每位导演/主演的文件夹里。
            List<string> keys = PersonValues(item, HomeCategoryMode);
            if (keys.Count == 0)
            {
                keys = [UnknownFolderName];
            }

            foreach (string key in keys)
            {
                if (!groups.TryGetValue(key, out List<LibraryPosterItem>? bucket))
                {
                    bucket = [];
                    groups[key] = bucket;
                    orderedKeys.Add(key);
                }

                bucket.Add(item);
            }
        }

        List<LibraryPosterItem> result = [];
        foreach (string key in orderedKeys)
        {
            List<LibraryPosterItem> bucket = groups[key];
            string folderKey = HomeCategoryMode + "|" + key;
            bool isPerson = !string.Equals(key, UnknownFolderName, StringComparison.OrdinalIgnoreCase);
            result.Add(new LibraryPosterItem
            {
                Id = "folder-" + Uri.EscapeDataString(folderKey),
                Title = isPerson ? key : UnknownFolderDisplayName,
                PosterPath = ResolveHomeFolderPoster(isPerson ? key : null, folderKey, bucket),
                MediaKind = "\u6587\u4EF6\u5939",
                IsFolder = true,
                FolderKey = folderKey,
                FolderItemCount = bucket.Count
            });
        }

        // 分类文件夹一律按名称排序，升降序由首页箭头按钮切换，「未标注」固定排在最后。
        List<LibraryPosterItem> folders = [.. result.Where((LibraryPosterItem item) => !IsUnknownFolderKey(item.FolderKey))];
        folders.Sort((LibraryPosterItem left, LibraryPosterItem right) =>
            string.Compare(left.Title, right.Title, StringComparison.CurrentCultureIgnoreCase));
        if (IsSortDescending)
        {
            folders.Reverse();
        }

        folders.AddRange(result.Where((LibraryPosterItem item) => IsUnknownFolderKey(item.FolderKey)));
        return folders;
    }

    private bool IsUnknownFolderKey(string? folderKey)
    {
        return folderKey is not null &&
               string.Equals(SplitFolderKey(folderKey).Name, UnknownFolderName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>卡片封面优先级：用户指定封面 > 分类内随机影视海报（人物照片只在详情页用）。</summary>
    private string? ResolveHomeFolderPoster(string? personName, string folderKey, List<LibraryPosterItem> bucket)
    {
        return ResolveCustomFolderPoster(personName, folderKey) ?? PickRandomFolderPoster(folderKey, bucket);
    }

    /// <summary>用户为该文件夹指定的封面：档案里的优先，其次旧设置中搬过来的记录。</summary>
    private string? ResolveCustomFolderPoster(string? personName, string folderKey)
    {
        if (personName is not null &&
            homeCategoryFolders.TryGetValue(personName, out CategoryFolder? folder) &&
            IsUsablePosterPath(folder.PosterPath))
        {
            return folder.PosterPath;
        }

        return Settings.HomeFolderPosters.TryGetValue(folderKey, out string? legacyPoster) && IsUsablePosterPath(legacyPoster)
            ? legacyPoster
            : null;
    }

    private string? PickRandomFolderPoster(string folderKey, List<LibraryPosterItem> bucket)
    {
        List<string> candidates = [];
        foreach (LibraryPosterItem item in bucket)
        {
            if (IsUsablePosterPath(item.PosterPath))
            {
                candidates.Add(item.PosterPath!);
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // 同一轮会话里固定一张，否则每次刷新封面都会跳。
        if (homeFolderRandomPosters.TryGetValue(folderKey, out string? cached) && candidates.Contains(cached, StringComparer.Ordinal))
        {
            return cached;
        }

        string picked = candidates[Random.Shared.Next(candidates.Count)];
        homeFolderRandomPosters[folderKey] = picked;
        return picked;
    }

    private static bool IsUsablePosterPath(string? path) => !string.IsNullOrWhiteSpace(path);

    /// <summary>人物照片必须已经落到本地磁盘，TMDB 相对路径在这边显示不出来。</summary>
    private static bool IsUsablePhotoPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && File.Exists(path);

    private static string HomeCategoryStoreKind(string mode) => mode == "actor" ? "cast" : "director";

    private static List<string> PersonValues(LibraryPosterItem item, string mode)
    {
        return mode == "actor" ? item.CastList : item.DirectorList;
    }

    private static bool MatchesHomeFolder(LibraryPosterItem item, string mode, string folderName)
    {
        if (string.Equals(folderName, UnknownFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return PersonValues(item, mode).Count == 0;
        }

        return PersonValues(item, mode).Any((string name) => string.Equals(name, folderName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>刷新导演/主演文件夹封面与人物档案缓存，并把旧设置里的封面搬进数据库。</summary>
    private async Task RefreshHomeCategoryCacheAsync()
    {
        if (categoryFolderStore is null)
        {
            return;
        }

        try
        {
            string kind = HomeCategoryStoreKind(HomeCategoryMode);
            IReadOnlyList<CategoryFolder> folders = await categoryFolderStore.LoadFoldersAsync(kind);
            IReadOnlyList<PersonProfile> profiles = await categoryFolderStore.LoadProfilesAsync(kind);
            Dictionary<string, CategoryFolder> folderByName = new(StringComparer.OrdinalIgnoreCase);
            foreach (CategoryFolder folder in folders)
            {
                folderByName[folder.Name] = folder;
            }

            Dictionary<string, PersonProfile> profileByName = new(StringComparer.OrdinalIgnoreCase);
            foreach (PersonProfile profile in profiles)
            {
                profileByName[profile.Name] = profile;
            }

            homeCategoryFolders = folderByName;
            homePersonProfiles = profileByName;

            if (!homeFolderPosterMigrationAttempted)
            {
                homeFolderPosterMigrationAttempted = true;
                await MigrateLegacyHomeFolderPostersAsync(kind);
            }
        }
        catch (Exception)
        {
            // 缓存刷新失败只影响封面展示，分类本身仍可用。
        }
    }

    private async Task MigrateLegacyHomeFolderPostersAsync(string kind)
    {
        bool migrated = false;
        foreach (KeyValuePair<string, string> entry in Settings.HomeFolderPosters)
        {
            (string mode, string name) = SplitFolderKey(entry.Key);
            if (HomeCategoryStoreKind(mode) != kind || string.IsNullOrWhiteSpace(entry.Value))
            {
                continue;
            }

            if (homeCategoryFolders.TryGetValue(name, out CategoryFolder? existing) && IsUsablePosterPath(existing.PosterPath))
            {
                continue;
            }

            await categoryFolderStore!.SetFolderPosterAsync(kind, name, entry.Value);
            migrated = true;
        }

        if (migrated)
        {
            IReadOnlyList<CategoryFolder> reloaded = await categoryFolderStore!.LoadFoldersAsync(kind);
            Dictionary<string, CategoryFolder> rebuilt = new(StringComparer.OrdinalIgnoreCase);
            foreach (CategoryFolder folder in reloaded)
            {
                rebuilt[folder.Name] = folder;
            }

            homeCategoryFolders = rebuilt;
        }
    }

    /// <summary>
    /// 详情页进入后补齐人物资料：头像下载到本地海报目录、作品列表写进人物档案，
    /// 两者本地已有就不再打网络，只在首次打开时显示"正在获取"。
    /// </summary>
    private async Task RefreshOpenFolderFilmographyAsync()
    {
        if (categoryFolderStore is null || string.IsNullOrEmpty(OpenFolderKey))
        {
            return;
        }

        (string mode, string name) = SplitFolderKey(OpenFolderKey);
        if (string.Equals(name, UnknownFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int generation = openFolderBioGeneration;
        string kind = HomeCategoryStoreKind(mode);
        IsOpenFolderFilmographyLoading = !HasCachedPersonData(name);
        try
        {
            // 头像必须先落到本地磁盘，详情页左侧才显示得出人物照片。
            string? photoPath = await categoryFolderStore.EnsureProfilePhotoAsync(kind, name);
            IReadOnlyList<TmdbFilmographyItem>? items = await categoryFolderStore.FilmographyAsync(kind, name);
            if (generation != openFolderBioGeneration)
            {
                return;
            }

            if (items is { Count: > 0 } || !string.IsNullOrWhiteSpace(photoPath))
            {
                await RefreshHomeCategoryCacheAsync();
            }

            RefreshOpenFolderBio();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[OmniPlay] 人物作品列表获取失败：" + ex.Message);
        }
        finally
        {
            if (generation == openFolderBioGeneration)
            {
                IsOpenFolderFilmographyLoading = false;
            }
        }
    }

    /// <summary>本地档案里是否已有该人物的作品列表（决定要不要显示"正在获取"）。</summary>
    private bool HasCachedPersonData(string name) =>
        homePersonProfiles.TryGetValue(name, out PersonProfile? profile) && profile.Filmography.Count > 0;

    /// <summary>关闭文件夹详情页时清掉头部封面与年表。</summary>
    private void ClearOpenFolderBio()
    {
        openFolderBioGeneration++;
        OpenFolderBioHeader = string.Empty;
        OpenFolderBioColumns = EmptyBioColumns;
        HasOpenFolderBioRows = false;
        IsOpenFolderFilmographyLoading = false;
        OpenFolderCoverPath = null;
        OpenFolderCoverTip = string.Empty;
    }

    /// <summary>详情页头部封面：用户指定封面 > 导演/主演照片 > 分类内影视海报（兜底）。</summary>
    private void RefreshOpenFolderCover(string? personName)
    {
        string folderKey = OpenFolderKey ?? string.Empty;
        string? custom = ResolveCustomFolderPoster(personName, folderKey);
        if (custom is not null)
        {
            OpenFolderCoverPath = custom;
            OpenFolderCoverTip = "\u7528\u6237\u6307\u5b9a\u5c01\u9762";
            return;
        }

        if (personName is not null &&
            homePersonProfiles.TryGetValue(personName, out PersonProfile? profile) &&
            IsUsablePhotoPath(profile.PhotoPath))
        {
            OpenFolderCoverPath = profile.PhotoPath;
            OpenFolderCoverTip = "\u4EBA\u7269\u7167\u7247";
            return;
        }

        OpenFolderCoverPath = PickRandomFolderPoster(folderKey, [.. LibraryItems]);
        OpenFolderCoverTip = "\u5F71\u89C6\u6D77\u62A5";
    }

    /// <summary>年表里的一部作品（本地库内的或只有 TMDB 记录的）。</summary>
    private readonly record struct FolderBioEntry(string Title, double? Rating, int? Year, string? Date, bool IsLocal);

    /// <summary>
    /// 文件夹详情页简介：一行标题 + 三列年表，本地库里的作品加粗高亮，
    /// 库里还没有、只在人物作品记录里出现的排在其后（对齐 mac 版 folderBio）。
    /// </summary>
    private void RefreshOpenFolderBio()
    {
        if (string.IsNullOrEmpty(OpenFolderKey))
        {
            ClearOpenFolderBio();
            return;
        }

        (string mode, string name) = SplitFolderKey(OpenFolderKey);
        bool isPerson = !string.Equals(name, UnknownFolderName, StringComparison.OrdinalIgnoreCase);
        RefreshOpenFolderCover(isPerson ? name : null);

        if (!isPerson)
        {
            OpenFolderBioHeader = $"\u8FD9\u4E9B {LibraryItems.Count} \u90E8\u5F71\u89C6\u5C1A\u672A\u8865\u5145\u5BFC\u6F14/\u4E3B\u6F14\u4FE1\u606F\uFF0C\u8865\u5145\u540E\u5C06\u81EA\u52A8\u5F52\u5165\u5BF9\u5E94\u6587\u4EF6\u5939\u3002";
            SetOpenFolderBioRows([]);
            return;
        }

        PersonProfile? profile;
        homePersonProfiles.TryGetValue(name, out profile);
        List<TmdbFilmographyItem> filmography = profile?.Filmography ?? [];
        Dictionary<int, TmdbFilmographyItem> remoteByTmdbId = [];
        Dictionary<string, TmdbFilmographyItem> remoteByTitle = new(StringComparer.OrdinalIgnoreCase);
        foreach (TmdbFilmographyItem item in filmography)
        {
            if (item.TmdbId is int tmdbId)
            {
                remoteByTmdbId[tmdbId] = item;
            }

            if (!string.IsNullOrWhiteSpace(item.Title))
            {
                remoteByTitle.TryAdd(item.Title, item);
            }
        }

        string verb = mode == "actor" ? "\u53C2\u6F14" : "\u6267\u5BFC";
        HashSet<TmdbFilmographyItem> represented = new(ReferenceEqualityComparer.Instance);
        List<FolderBioEntry> entries = [];
        foreach (LibraryPosterItem item in LibraryItems)
        {
            // 同一作品以 TMDB 记录为准：片名可能已被补成中文，日期也更完整。
            TmdbFilmographyItem? remote = FindFilmographyMatch(item, remoteByTmdbId, remoteByTitle);
            if (remote is not null)
            {
                represented.Add(remote);
            }

            string? localDate = LocalReleaseDate(item);
            string? date = !string.IsNullOrWhiteSpace(remote?.Date) ? remote!.Date : localDate;
            entries.Add(new FolderBioEntry(
                !string.IsNullOrWhiteSpace(remote?.Title) ? remote!.Title : item.Title,
                item.VoteAverage ?? remote?.Rating,
                ParseReleaseYear(date) ?? remote?.Year,
                date,
                true));
        }

        foreach (TmdbFilmographyItem item in filmography)
        {
            if (represented.Contains(item) || string.IsNullOrWhiteSpace(item.Title))
            {
                continue;
            }

            entries.Add(new FolderBioEntry(item.Title, item.Rating, item.Year, item.Date, false));
        }

        OpenFolderBioHeader = entries.Count == 0
            ? $"{name}\u6682\u65E0\u4F5C\u54C1\u8BB0\u5F55\u3002"
            : $"{name}\u5171{verb}\u8FC7 {entries.Count} \u90E8\u5F71\u89C6";
        if (entries.Count == 0)
        {
            SetOpenFolderBioRows([]);
            return;
        }

        // 按上映时间正序，缺日期的排最后。
        List<FolderBioEntry> sorted = [.. entries
            .OrderBy((FolderBioEntry entry) => entry.Date ?? "9999-12-31", StringComparer.Ordinal)
            .ThenBy((FolderBioEntry entry) => entry.Year ?? 9999)];

        List<FolderBioRow> rows = [];
        int? lastLabelledYear = null;
        bool labelledNoYear = false;
        foreach (FolderBioEntry entry in sorted)
        {
            string yearLabel;
            if (entry.Year is int year)
            {
                if (lastLabelledYear != year)
                {
                    yearLabel = $"{year}\uFF1A";
                    lastLabelledYear = year;
                    labelledNoYear = false;
                }
                else
                {
                    yearLabel = string.Empty;
                }
            }
            else if (!labelledNoYear)
            {
                yearLabel = "\u5176\u4ED6\uFF1A";
                labelledNoYear = true;
            }
            else
            {
                yearLabel = string.Empty;
            }

            string rating = entry.Rating.HasValue
                ? entry.Rating.Value.ToString("0.0", CultureInfo.InvariantCulture)
                : "\u6682\u65E0\u8BC4\u5206";
            rows.Add(new FolderBioRow
            {
                YearLabel = yearLabel,
                Text = $"\u300A{entry.Title}\u300B{rating}",
                IsLocal = entry.IsLocal
            });
        }

        SetOpenFolderBioRows(rows);
    }

    /// <summary>年表固定六列，作品多的时候也不会把头部顶得太高。</summary>
    private void SetOpenFolderBioRows(List<FolderBioRow> rows)
    {
        int chunk = Math.Max(1, (rows.Count + BioColumnCount - 1) / BioColumnCount);
        List<IReadOnlyList<FolderBioRow>> columns = [];
        for (int index = 0; index < BioColumnCount; index++)
        {
            columns.Add(TakeBioRows(rows, index * chunk, chunk));
        }

        OpenFolderBioColumns = columns;
        HasOpenFolderBioRows = rows.Count > 0;
    }

    private const int BioColumnCount = 6;

    private static readonly IReadOnlyList<IReadOnlyList<FolderBioRow>> EmptyBioColumns =
        Enumerable.Repeat((IReadOnlyList<FolderBioRow>)[], BioColumnCount).ToList();

    private static IReadOnlyList<FolderBioRow> TakeBioRows(List<FolderBioRow> rows, int start, int count)
    {
        if (start >= rows.Count || count <= 0)
        {
            return [];
        }

        return rows.GetRange(start, Math.Min(count, rows.Count - start));
    }

    private TmdbFilmographyItem? FindFilmographyMatch(
        LibraryPosterItem item,
        Dictionary<int, TmdbFilmographyItem> byTmdbId,
        Dictionary<string, TmdbFilmographyItem> byTitle)
    {
        // 电影存了 TMDB id，按 id 对齐最准；剧集没有该列，只能退回片名比对。
        if (item.MovieId is long movieId &&
            allMovies.FirstOrDefault((Movie movie) => movie.Id == movieId)?.TmdbId is int tmdbId &&
            byTmdbId.TryGetValue(tmdbId, out TmdbFilmographyItem? byId))
        {
            return byId;
        }

        return byTitle.TryGetValue(item.Title, out TmdbFilmographyItem? byName) ? byName : null;
    }

    private string? LocalReleaseDate(LibraryPosterItem item)
    {
        if (item.MovieId is long movieId)
        {
            return allMovies.FirstOrDefault((Movie movie) => movie.Id == movieId)?.ReleaseDate;
        }

        if (item.TvShowId is long tvShowId)
        {
            return allTvShows.FirstOrDefault((TvShow show) => show.Id == tvShowId)?.FirstAirDate;
        }

        return null;
    }

    private static int? ParseReleaseYear(string? releaseDate)
    {
        if (!string.IsNullOrWhiteSpace(releaseDate) &&
            releaseDate.Length >= 4 &&
            int.TryParse(releaseDate[..4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int year))
        {
            return year;
        }

        return null;
    }

    private static (string Mode, string Name) SplitFolderKey(string folderKey)
    {
        int separator = folderKey.IndexOf('|');
        if (separator <= 0 || separator == folderKey.Length - 1)
        {
            return (string.Empty, folderKey);
        }

        return (folderKey[..separator], folderKey[(separator + 1)..]);
    }

    private static string NormalizeHomeCategoryModeChoice(string? mode)
    {
        string value = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return value is "director" or "actor" ? value : "none";
    }

    private void SetDetailPeople()
    {
        string? director = null;
        string? mainCast = null;
        if (currentDetailMovieId.HasValue)
        {
            director = allMovies.FirstOrDefault((Movie movie) => movie.Id == currentDetailMovieId.Value)?.Director;
            mainCast = allMovies.FirstOrDefault((Movie movie) => movie.Id == currentDetailMovieId.Value)?.MainCast;
        }
        else if (currentDetailTvShowId.HasValue)
        {
            director = allTvShows.FirstOrDefault((TvShow show) => show.Id == currentDetailTvShowId.Value)?.Director;
            mainCast = allTvShows.FirstOrDefault((TvShow show) => show.Id == currentDetailTvShowId.Value)?.MainCast;
        }

        HasDetailDirector = !string.IsNullOrWhiteSpace(director);
        DetailDirectorLine = HasDetailDirector ? "\u5BFC\u6F14\uFF1A" + director!.Trim() : string.Empty;
        HasDetailCast = !string.IsNullOrWhiteSpace(mainCast);
        DetailCastLine = HasDetailCast ? "\u4E3B\u6F14\uFF1A" + mainCast!.Trim() : string.Empty;
    }

    private static string NormalizePeopleText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Replace(',', '\u3001').Replace('\uFF0C', '\u3001').Trim();
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.HomeCategoryMode))
        {
            if (HomeCategoryMode != Settings.HomeCategoryMode)
            {
                HomeCategoryMode = NormalizeHomeCategoryModeChoice(Settings.HomeCategoryMode);
            }
            ApplyFilters();
        }
    }

    private void ApplySavedLibraryViewPreferences()
    {
        suppressSortPreferencePersistence = true;
        try
        {
            string text = Settings.LibrarySortOption.Trim().ToLowerInvariant();
            if (1 == 0)
            {
            }
            LibrarySortOption librarySortOption = ((text == "year") ? LibrarySortOption.Year : ((text == "rating") ? LibrarySortOption.Rating : LibrarySortOption.Title));
            if (1 == 0)
            {
            }
            LibrarySortOption option = librarySortOption;
            SelectedSortOption = option;
            SelectedSortOptionItem = SortOptions.FirstOrDefault((LibrarySortOptionItem item) => item.Value == option) ?? SortOptions[0];
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
        foreach (LibrarySortOptionItem sortOption in SortOptions)
        {
            sortOption.IsSelected = sortOption.Value == SelectedSortOption;
        }
    }

    private void PersistLibraryViewPreferencesIfNeeded()
    {
        if (!suppressSortPreferencePersistence)
        {
            _ = PersistLibraryViewPreferencesAsync();
        }
    }

    private async Task PersistLibraryViewPreferencesAsync()
    {
        try
        {
            await Settings.SaveLibraryViewPreferencesAsync(ToLibrarySortSetting(SelectedSortOption), IsSortDescending);
        }
        catch
        {
        }
    }

    private static string ToLibrarySortSetting(LibrarySortOption option)
    {
        if (1 == 0)
        {
        }
        string result = option switch
        {
            LibrarySortOption.Year => "year", 
            LibrarySortOption.Rating => "rating", 
            _ => "title", 
        };
        if (1 == 0)
        {
        }
        return result;
    }

    private void SelectSeason(int? season)
    {
        if (season.HasValue && SelectedSeason != season.Value)
        {
            SelectedSeason = season.Value;
        }
    }

    private Task RefreshNetworkSourcesAsync()
    {
        return RefreshNetworkSourcesAsync(updateBusyState: true);
    }

    private void NotifyNetworkShareFolderStateChanged()
    {
        OnPropertyChanged("HasNetworkShareFolders");
        OnPropertyChanged("IsNetworkCredentialFormVisible");
        OnPropertyChanged("StarredNetworkShareFolderCount");
        OnPropertyChanged("HasStarredNetworkShareFolders");
        OnPropertyChanged("MountStarredNetworkFoldersActionText");
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

    private void ToggleLuckyStunPasswordVisibility()
    {
        IsLuckyStunPasswordVisible = !IsLuckyStunPasswordVisible;
    }

    private void TogglePendingLuckyStunPasswordVisibility()
    {
        IsPendingLuckyStunPasswordVisible = !IsPendingLuckyStunPasswordVisible;
    }

    private void AddPendingExternalAddress()
    {
        PendingExternalAddresses.Add(new MediaSourceExternalAddress());
    }

    private void RemovePendingExternalAddress(MediaSourceExternalAddress? address)
    {
        if (address != null)
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
            IReadOnlyList<NetworkSourceDiscoveryItem> sources = await networkShareDiscoveryService.DiscoverAsync();
            ReplaceItems(DiscoveredNetworkSources, sources);
            OnPropertyChanged("HasDiscoveredNetworkSources");
            NetworkSourceStatus = ((sources.Count == 0) ? "未发现 SMB、WebDAV 或媒体服务器。" : $"已预扫描到 {sources.Count} 个网络入口。");
        }
        catch
        {
            ReplaceItems(DiscoveredNetworkSources, Array.Empty<NetworkSourceDiscoveryItem>());
            OnPropertyChanged("HasDiscoveredNetworkSources");
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
        if (item == null)
        {
            return;
        }
        MediaSourceProtocol? protocolKind = item.ProtocolKind;
        bool flag;
        if (protocolKind.HasValue)
        {
            MediaSourceProtocol valueOrDefault = protocolKind.GetValueOrDefault();
            if ((uint)(valueOrDefault - 4) <= 2u)
            {
                flag = true;
                goto IL_0035;
            }
        }
        flag = false;
        goto IL_0035;
        IL_0035:
        if (flag)
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
        NetworkLoginTitle = "登录 " + item.ProtocolLabel;
        IsSourcePopupOpen = false;
        IsMediaServerPanelOpen = false;
        ReplaceItems(NetworkShareFolders, Array.Empty<NetworkShareFolderItem>());
        NotifyNetworkShareFolderStateChanged();
        IsNetworkLoginPanelOpen = true;
        NetworkSourceStatus = "正在准备登录：" + item.Name;
        OnCommandStateChanged();
    }

    private void OpenDiscoveredMediaServer(NetworkSourceDiscoveryItem item)
    {
        IsNetworkLoginPanelOpen = false;
        IsSourcePopupOpen = false;
        IsMediaServerPanelOpen = true;
        SettingsOptionItem settingsOptionItem = (SelectedMediaServerProtocolOption = MediaServerProtocolOptions.FirstOrDefault((SettingsOptionItem option) => string.Equals(option.Value, item.ProtocolType, StringComparison.OrdinalIgnoreCase)) ?? MediaServerProtocolOptions[0]);
        PendingMediaServerProtocolType = settingsOptionItem.Value;
        PendingMediaServerName = item.Name;
        PendingMediaServerBaseUrl = item.BaseUrl;
        PendingMediaServerToken = string.Empty;
        PendingMediaServerUserId = string.Empty;
        MediaServerStatus = GetMediaServerConnectionHint(item.ProtocolType);
        NetworkSourceStatus = "已选择 " + item.ProtocolLabel + "：" + item.BaseUrl;
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
        ReplaceItems(NetworkShareFolders, Array.Empty<NetworkShareFolderItem>());
        NotifyNetworkShareFolderStateChanged();
        IsNetworkLoginPanelOpen = true;
        NetworkSourceStatus = "输入 WebDAV 地址或 SMB 路径。";
        OnCommandStateChanged();
    }

    private void ApplySavedNetworkCredential(MediaSourceProtocol? protocol, string baseUrl, bool allowBaseUrlPrefill)
    {
        if (!protocol.HasValue || (protocol.Value != MediaSourceProtocol.WebDav && protocol.Value != MediaSourceProtocol.Smb))
        {
            return;
        }
        NetworkCredentialEntry networkCredentialEntry = networkCredentialStore.FindBest(protocol.Value, baseUrl);
        if ((object)networkCredentialEntry != null)
        {
            PendingNetworkUsername = networkCredentialEntry.Username;
            PendingNetworkPassword = networkCredentialEntry.Password;
            if (allowBaseUrlPrefill && string.IsNullOrWhiteSpace(PendingNetworkBaseUrl))
            {
                PendingNetworkProtocolType = networkCredentialEntry.ProtocolType;
                PendingNetworkBaseUrl = networkCredentialEntry.BaseUrl;
                PendingNetworkDisplayName = networkCredentialEntry.BaseUrl;
            }
        }
    }

    private void ApplyLatestSavedNetworkCredential()
    {
        NetworkCredentialEntry networkCredentialEntry = networkCredentialStore.FindLatest();
        if ((object)networkCredentialEntry != null)
        {
            PendingNetworkProtocolType = networkCredentialEntry.ProtocolType;
            PendingNetworkBaseUrl = networkCredentialEntry.BaseUrl;
            PendingNetworkDisplayName = networkCredentialEntry.BaseUrl;
            PendingNetworkUsername = networkCredentialEntry.Username;
            PendingNetworkPassword = networkCredentialEntry.Password;
        }
    }

    private void OpenMediaServerPanel()
    {
        IsNetworkLoginPanelOpen = false;
        IsSourcePopupOpen = false;
        IsMediaServerPanelOpen = true;
        if ((object)SelectedMediaServerProtocolOption == null)
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
        MediaSource source = BuildPendingMediaServerSource();
        if (source == null)
        {
            return;
        }
        IsBusy = true;
        OnCommandStateChanged();
        try
        {
            if (source.ProtocolKind == MediaSourceProtocol.WebDav)
            {
                string webDavUser = PendingMediaServerUserId.Trim();
                string webDavPassword = PendingMediaServerToken;
                MediaServerStatus = "正在连接 WebDAV 并读取共享文件夹...";
                StatusMessage = MediaServerStatus;
                IReadOnlyList<NetworkShareFolderItem> webdavFolders;
                try
                {
                    webdavFolders = await networkShareDiscoveryService.ListFoldersAsync(new NetworkSourceDiscoveryItem
                    {
                        Name = source.Name,
                        ProtocolType = "webdav",
                        BaseUrl = source.BaseUrl,
                        Description = source.BaseUrl
                    }, webDavUser, webDavPassword);
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is InvalidOperationException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    bool isUnauthorized = ex is UnauthorizedAccessException
                        || ex.Message.Contains("401", StringComparison.Ordinal)
                        || ex.Message.Contains("403", StringComparison.Ordinal);
                    MediaServerStatus = isUnauthorized ? "WebDAV 连接失败：用户名或密码无效。" : "WebDAV 连接失败：" + ex.Message;
                    StatusMessage = MediaServerStatus;
                    ShowToast(MediaServerStatus, ToastKind.Error);
                    return;
                }
                if (!string.IsNullOrWhiteSpace(webDavUser) || !string.IsNullOrWhiteSpace(webDavPassword))
                {
                    networkCredentialStore.Save(MediaSourceProtocol.WebDav, source.BaseUrl, webDavUser, webDavPassword);
                }
                if (webdavFolders.Count > 0)
                {
                    // 列表第一项是 WebDAV 根目录，默认选中；用户可以改为只挂载部分子文件夹。
                    List<NetworkShareFolderItem> selectableFolders = webdavFolders
                        .Select((folder, index) => index == 0 ? folder.WithStarred(true) : folder)
                        .ToList();
                    ReplaceItems(NetworkShareFolders, selectableFolders);
                    NotifyNetworkShareFolderStateChanged();
                    PendingNetworkProtocolType = "webdav";
                    PendingNetworkBaseUrl = source.BaseUrl;
                    PendingNetworkDisplayName = source.Name;
                    PendingNetworkUsername = webDavUser;
                    PendingNetworkPassword = webDavPassword;
                    NetworkLoginTitle = "选择 WebDAV 共享文件夹";
                    ResetMediaServerForm(clearStatus: false);
                    IsNetworkLoginPanelOpen = true;
                    MediaServerStatus = "连接成功：读取到 " + selectableFolders.Count + " 个可挂载文件夹，默认已选根目录，可点星标调整，确认后统一挂载扫描。";
                    NetworkSourceStatus = MediaServerStatus;
                    StatusMessage = MediaServerStatus;
                    return;
                }
                MediaServerStatus = "正在保存 WebDAV 媒体源并扫描...";
                StatusMessage = MediaServerStatus;
                long webdavSourceId = await mediaSourceRepository.AddAsync(source);
                ResetMediaServerForm(clearStatus: false);
                IsMediaServerPanelOpen = false;
                if (webdavSourceId <= 0)
                {
                    await ReloadLibraryAsync();
                    MediaServerStatus = "已保存 WebDAV 媒体源：" + source.Name;
                    StatusMessage = MediaServerStatus;
                    return;
                }
                CancellationTokenSource webdavAutomation = BeginLibraryAutomation();
                try
                {
                    StatusMessage = "已添加 WebDAV 媒体源：" + source.Name + "，正在扫描并刮削...";
                    await ReloadLibraryAsync();
                    LastScanSummary = await RunLibraryScanAndArtworkAsync(isExplicitRequest: true, forceThumbnails: true, webdavAutomation.Token, new long[1] { webdavSourceId });
                    StatusMessage = (string.IsNullOrWhiteSpace(StatusMessage) ? ("已添加并扫描 WebDAV 媒体源：" + source.Name) : ("已添加 WebDAV 媒体源：" + source.Name + "。" + StatusMessage));
                }
                catch (OperationCanceledException) when (webdavAutomation.IsCancellationRequested)
                {
                    StatusMessage = "已停止新媒体源的扫描和刮削任务。";
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[OmniPlay] 扫描新媒体源失败：" + ex.Message);
                    StatusMessage = "已添加 WebDAV 媒体源：" + source.Name + "，但扫描失败：" + ex.Message;
                }
                finally
                {
                    CompleteLibraryAutomation(webdavAutomation);
                }
                return;
            }
            MediaServerStatus = "正在预扫描 " + source.ProtocolLabel + " 共享文件夹...";
            StatusMessage = MediaServerStatus;
            using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90L));
            IReadOnlyList<NetworkShareFolderItem> readOnlyList = ((mediaServerPreflightService != null) ? (await mediaServerPreflightService.ListLibrariesAsync(source, timeout.Token)) : Array.Empty<NetworkShareFolderItem>());
            IReadOnlyList<NetworkShareFolderItem> folders = readOnlyList;
            ReplaceItems(NetworkShareFolders, folders);
            NotifyNetworkShareFolderStateChanged();
            if (!string.IsNullOrWhiteSpace(PendingMediaServerToken) || !string.IsNullOrWhiteSpace(PendingMediaServerUserId))
            {
                if (source.ProtocolKind.HasValue)
                {
                    networkCredentialStore.Save(source.ProtocolKind.Value, source.BaseUrl, PendingMediaServerUserId.Trim(), PendingMediaServerToken);
                }
            }
            PendingNetworkProtocolType = source.ProtocolType;
            PendingNetworkBaseUrl = source.BaseUrl;
            PendingNetworkDisplayName = source.Name;
            PendingNetworkUsername = string.Empty;
            PendingNetworkPassword = string.Empty;
            NetworkLoginTitle = "选择 " + source.ProtocolLabel + " 共享文件夹";
            IsMediaServerPanelOpen = false;
            IsNetworkLoginPanelOpen = true;
            MediaServerStatus = ((folders.Count == 0) ? "连接成功，但当前服务器没有可挂载的媒体库。" : $"预扫描成功：读取到 {folders.Count} 个媒体库，可给多个目标点星标，关闭列表时统一挂载。");
            NetworkSourceStatus = MediaServerStatus;
            StatusMessage = MediaServerStatus;
        }
        catch (OperationCanceledException)
        {
            MediaServerStatus = "媒体服务器预扫描超时。";
            StatusMessage = MediaServerStatus;
        }
        catch (Exception ex2) when (((ex2 is HttpRequestException || ex2 is InvalidOperationException || ex2 is IOException) ? 1 : 0) != 0)
        {
            MediaServerStatus = "媒体服务器预扫描失败：" + ex2.Message;
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
            using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(130L));
            PlexPinSession session = await PlexPinAuthClient.CreateAsync(cancellation.Token);
            Process.Start(new ProcessStartInfo
            {
                FileName = session.AuthorizationUrl,
                UseShellExecute = true
            });
            MediaServerStatus = "已打开 Plex Link 页面，请确认 PIN：" + session.Code;
            PendingMediaServerToken = await PlexPinAuthClient.WaitForTokenAsync(session, cancellation.Token);
            MediaServerStatus = "Plex 授权成功，已自动填入访问令牌。";
        }
        catch (OperationCanceledException)
        {
            MediaServerStatus = "Plex 授权超时，请重新点击登录。";
        }
        catch (Exception ex2) when (((ex2 is HttpRequestException || ex2 is JsonException || ex2 is InvalidOperationException) ? 1 : 0) != 0)
        {
            MediaServerStatus = "Plex 授权失败：" + ex2.Message;
        }
        finally
        {
            IsPlexAuthorizationInProgress = false;
            OnCommandStateChanged();
        }
    }

    private LuckyStunSourceConfiguration? BuildPendingLuckyStunConfiguration()
    {
        string text = PendingLuckyStunManagementUrl.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        int updateIntervalMinutes = Math.Clamp(PendingLuckyStunUpdateIntervalMinutes, 5, 1440);
        string text2 = Settings.LuckyStunRules.FirstOrDefault((LuckyStunRule rule) => string.Equals(rule.Id, PendingLuckyStunRuleId, StringComparison.OrdinalIgnoreCase))?.Name ?? string.Empty;
        return new LuckyStunSourceConfiguration
        {
            ManagementUrl = text,
            Username = PendingLuckyStunUsername.Trim(),
            Password = PendingLuckyStunPassword,
            RuleId = PendingLuckyStunRuleId.Trim(),
            RuleName = text2.Trim(),
            DisplayName = string.Empty,
            AutoUpdate = PendingLuckyStunAutoUpdate,
            UpdateIntervalMinutes = updateIntervalMinutes,
            LastUpdatedAt = null,
            PathSuffix = PendingLuckyStunPathSuffix.Trim().Trim('/')
        };
    }

    private void SetLuckyStunPanelStatus(string status)
    {
        PendingLuckyStunStatus = status;
        LuckyStunSourceStatus = status;
    }

    private async Task TestPendingLuckyStunLoginAsync()
    {
        if (luckyStunClient == null)
        {
            SetLuckyStunPanelStatus("Lucky STUN 服务尚未初始化。");
            return;
        }
        if (string.IsNullOrWhiteSpace(PendingLuckyStunManagementUrl))
        {
            SetLuckyStunPanelStatus("请先填写 Lucky 管理地址。");
            return;
        }
        IsPendingLuckyStunLoginInProgress = true;
        OnPropertyChanged("PendingLuckyStunLoginButtonText");
        SetLuckyStunPanelStatus("正在登录并读取穿透规则...");
        OnCommandStateChanged();
        try
        {
            LuckyStunSettings settings = new LuckyStunSettings
            {
                ManagementUrl = PendingLuckyStunManagementUrl.Trim(),
                Username = PendingLuckyStunUsername.Trim(),
                Password = PendingLuckyStunPassword,
                SelectedRuleId = PendingLuckyStunRuleId
            };
            LuckyStunSnapshot snapshot = await luckyStunClient.FetchAsync(settings, CancellationToken.None);
            Settings.LuckyStunRules = snapshot.Rules;
            if (snapshot.Rules.Count == 0)
            {
                PendingLuckyStunRuleId = string.Empty;
            }
            else if ((object)snapshot.SelectedRule != null)
            {
                PendingLuckyStunRuleId = snapshot.SelectedRule.Id;
            }
            SetLuckyStunPanelStatus(snapshot.Message);
            try
            {
                string persistedRuleName = Settings.LuckyStunRules.FirstOrDefault(rule => string.Equals(rule.Id, PendingLuckyStunRuleId, StringComparison.OrdinalIgnoreCase))?.Name ?? string.Empty;
                await Settings.SaveLuckyStunCredentialsAsync(PendingLuckyStunManagementUrl.Trim(), PendingLuckyStunUsername.Trim(), PendingLuckyStunPassword, PendingLuckyStunAutoUpdate, PendingLuckyStunUpdateIntervalMinutes, PendingLuckyStunRuleId, persistedRuleName);
            }
            catch
            {
                // 全局登录信息落盘失败不影响本次登录检测结果。
            }
        }
        catch (Exception ex)
        {
            SetLuckyStunPanelStatus("Lucky STUN 登录检测失败：" + ex.Message);
        }
        finally
        {
            IsPendingLuckyStunLoginInProgress = false;
            OnPropertyChanged("PendingLuckyStunLoginButtonText");
            OnCommandStateChanged();
        }
    }

    private async Task SavePendingLuckyStunSourceAsync()
    {
        if (luckyStunClient == null)
        {
            MediaServerStatus = "Lucky STUN 服务尚未初始化。";
            return;
        }
        LuckyStunSourceConfiguration configuration = BuildPendingLuckyStunConfiguration();
        if (configuration == null || string.IsNullOrWhiteSpace(configuration.ManagementUrl))
        {
            MediaServerStatus = "请填写 Lucky STUN 管理地址。";
            return;
        }
        IsBusy = true;
        OnCommandStateChanged();
        try
        {
            MediaServerStatus = "正在登录 Lucky STUN 并读取穿透规则...";
            LuckyStunSettings settings = new LuckyStunSettings
            {
                ManagementUrl = configuration.ManagementUrl,
                Username = configuration.Username,
                Password = configuration.Password,
                SelectedRuleId = configuration.RuleId,
                SelectedRuleName = configuration.RuleName
            };
            LuckyStunSnapshot snapshot = await luckyStunClient.FetchAsync(settings, CancellationToken.None);
            LuckyStunRule rule = snapshot.Rules.FirstOrDefault((LuckyStunRule item) => string.Equals(item.Id, configuration.RuleId, StringComparison.OrdinalIgnoreCase)) ?? snapshot.SelectedRule;
            if ((object)rule == null)
            {
                MediaServerStatus = "请先登录检测并选择有效的穿透规则。";
                return;
            }
            string normalizedAddress = LuckyStunAddressHelper.NormalizeAddress(rule.Address);
            if (string.IsNullOrWhiteSpace(normalizedAddress))
            {
                MediaServerStatus = "穿透规则没有返回有效地址。";
                return;
            }
            string updatedAddress = LuckyStunAddressHelper.AppendPathSuffix(configuration.PathSuffix, normalizedAddress);
            string ruleName = rule.Name;
            string displayName = (string.IsNullOrWhiteSpace(PendingMediaServerName) ? ("Lucky STUN · " + ruleName) : PendingMediaServerName.Trim());
            string protocolBackup = PendingMediaServerProtocolType;
            string baseUrlBackup = PendingMediaServerBaseUrl;
            try
            {
                PendingMediaServerBaseUrl = updatedAddress;
                MediaSource source = BuildPendingMediaServerSource();
                if (source == null)
                {
                    MediaServerStatus = (string.IsNullOrWhiteSpace(MediaServerStatus) ? "媒体服务器地址无效。" : MediaServerStatus);
                    return;
                }
                source.Name = displayName;
                source.BaseUrl = updatedAddress;
                MediaSourceAddressConfiguration addressConfiguration = source.GetAddressConfiguration();
                addressConfiguration.LocalAddress = updatedAddress;
                addressConfiguration.LocalLabel = string.Empty;
                addressConfiguration.ActiveAddress = updatedAddress;
                addressConfiguration.ActiveLabel = string.Empty;
                configuration.RuleId = rule.Id;
                configuration.RuleName = ruleName;
                addressConfiguration.LuckyStun = configuration;
                source.SetAddressConfiguration(addressConfiguration);
                MediaSource existing = (await mediaSourceRepository.GetAllAsync()).FirstOrDefault(delegate(MediaSource item)
                {
                    int result;
                    if (item.IsActive)
                    {
                        LuckyStunSourceConfiguration luckyStun = item.GetAddressConfiguration().LuckyStun;
                        if (luckyStun != null && string.Equals(luckyStun.ManagementUrl, configuration.ManagementUrl, StringComparison.OrdinalIgnoreCase))
                        {
                            result = (string.Equals(luckyStun.RuleId, rule.Id, StringComparison.OrdinalIgnoreCase) ? 1 : 0);
                            goto IL_004f;
                        }
                    }
                    result = 0;
                    goto IL_004f;
                    IL_004f:
                    return (byte)result != 0;
                });
                long num;
                if (existing != null)
                {
                    existing.Name = source.Name;
                    existing.ProtocolType = source.ProtocolType;
                    existing.BaseUrl = source.BaseUrl;
                    existing.AuthConfig = source.AuthConfig;
                    existing.SetAddressConfiguration(addressConfiguration);
                    num = await mediaSourceRepository.UpdateAsync(existing) ? existing.Id.GetValueOrDefault() : 0;
                }
                else
                {
                    num = await mediaSourceRepository.AddAsync(source);
                }
                long sourceId = num;
                IsMediaServerPanelOpen = false;
                ResetMediaServerForm(clearStatus: true);
                await ReloadLibraryAsync();
                Settings.StartLuckyStunAutoUpdateLoop();
                StatusMessage = ((sourceId > 0) ? $"已保存 Lucky STUN 穿透媒体源：{displayName}（{updatedAddress}）" : $"Lucky STUN 穿透媒体源已保存：{displayName}（{updatedAddress}）");
            }
            finally
            {
                PendingMediaServerProtocolType = protocolBackup;
                PendingMediaServerBaseUrl = baseUrlBackup;
            }
        }
        catch (Exception ex)
        {
            MediaServerStatus = "Lucky STUN 穿透媒体源保存失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private async Task RefreshLuckyStunSourceAsync(MediaSource? source)
    {
        if (source == null)
        {
            return;
        }
        IsBusy = true;
        OnCommandStateChanged();
        try
        {
            StatusMessage = await RefreshLuckyStunSourceCoreAsync(source);
        }
        catch (Exception ex) when (((ex is HttpRequestException || ex is InvalidOperationException || ex is TaskCanceledException) ? 1 : 0) != 0)
        {
            StatusMessage = "刷新 " + source.Name + " 的穿透地址失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private async Task<string> RefreshLuckyStunSourceCoreAsync(MediaSource source)
    {
        if (luckyStunClient == null)
        {
            return "Lucky STUN 服务尚未初始化。";
        }
        MediaSourceAddressConfiguration configuration = source.GetAddressConfiguration();
        LuckyStunSourceConfiguration? lucky = configuration.LuckyStun;
        if (lucky == null || string.IsNullOrWhiteSpace(lucky.ManagementUrl))
        {
            return "该媒体源未配置 Lucky STUN 穿透。";
        }
        StatusMessage = "正在刷新 " + source.Name + " 的穿透地址...";
        LuckyStunSnapshot snapshot = await luckyStunClient.FetchAsync(new LuckyStunSettings
        {
            ManagementUrl = lucky.ManagementUrl,
            Username = lucky.Username,
            Password = lucky.Password,
            SelectedRuleId = lucky.RuleId,
            SelectedRuleName = lucky.RuleName
        }, CancellationToken.None);
        if ((object)snapshot.SelectedRule == null)
        {
            return snapshot.Message;
        }
        string updatedAddress = LuckyStunAddressHelper.AppendPathSuffix(address: LuckyStunAddressHelper.NormalizeAddress(snapshot.SelectedRule.Address), suffix: lucky.PathSuffix);
        if (string.IsNullOrWhiteSpace(updatedAddress))
        {
            return "穿透规则没有返回有效地址。";
        }
        bool addressChanged = !string.Equals(source.BaseUrl, updatedAddress, StringComparison.OrdinalIgnoreCase);
        lucky.RuleId = snapshot.SelectedRule.Id;
        lucky.RuleName = snapshot.SelectedRule.Name;
        lucky.LastUpdatedAt = DateTimeOffset.UtcNow;
        configuration.LocalAddress = updatedAddress;
        configuration.LocalLabel = string.Empty;
        configuration.ActiveAddress = updatedAddress;
        configuration.ActiveLabel = string.Empty;
        source.BaseUrl = updatedAddress;
        source.SetAddressConfiguration(configuration);
        bool saved = await mediaSourceRepository.UpdateAsync(source);
        Settings.StartLuckyStunAutoUpdateLoop();
        await ReloadLibraryAsync();
        string refreshedStatus = (addressChanged ? ("已更新 " + source.Name + " 的穿透地址：" + updatedAddress) : ("规则“" + snapshot.SelectedRule.Name + "”检测成功，地址未变化：" + updatedAddress));
        if (!saved)
        {
            refreshedStatus += "；但地址未保存成功（可能与其它媒体源地址冲突），请检查媒体源列表。";
        }
        else
        {
            refreshedStatus += "；" + (await TestSourceAddressMessageAsync(source, updatedAddress));
        }
        return refreshedStatus;
    }

    private async Task RefreshAndCheckLuckyStunSourceAsync(MediaSource? source)
    {
        if (source == null || source.Id == null)
        {
            return;
        }
        IsBusy = true;
        OnCommandStateChanged();
        try
        {
            string refreshSummary = await RefreshLuckyStunSourceCoreAsync(source);
            StatusMessage = refreshSummary + "，正在检测连通性...";
            string checkResult = await CheckSourceConnectivityCoreAsync(source);
            StatusMessage = refreshSummary + " " + checkResult;
            ShowToast(checkResult, checkResult.Contains("无法连接", StringComparison.Ordinal) || checkResult.Contains("失败", StringComparison.Ordinal) ? ToastKind.Error : ToastKind.Success);
        }
        catch (Exception ex)
        {
            StatusMessage = "刷新穿透并检测连通性失败：" + ex.Message;
            ShowToast(StatusMessage, ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private async Task PreScanMediaServerSourceAsync()
    {
        MediaSource source = BuildPendingMediaServerSource();
        if (source == null)
        {
            return;
        }
        IsBusy = true;
        OnCommandStateChanged();
        try
        {
            MediaServerStatus = "正在预扫描 " + source.ProtocolLabel + " 媒体列表...";
            using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90L));
            MediaServerPreflightResult result = await RunMediaServerPreflightAsync(source, timeout.Token);
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
        pendingLuckyStunMountConfiguration = null;
        pendingLuckyStunMountRootAddress = string.Empty;
        ReplaceItems(NetworkShareFolders, Array.Empty<NetworkShareFolderItem>());
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
        PendingMediaServerProtocolType = MediaServerProtocolOptions[0].Value;
        SelectedMediaServerProtocolOption = MediaServerProtocolOptions[0];
        PendingMediaServerBaseUrl = DefaultMediaServerBaseUrl(PendingMediaServerProtocolType);
        PendingMediaServerToken = string.Empty;
        IsMediaServerTokenVisible = false;
        PendingMediaServerUserId = string.Empty;
        ResetPendingLuckyStunForm();
        if (clearStatus)
        {
            MediaServerStatus = string.Empty;
        }
    }

    private void ResetPendingLuckyStunForm()
    {
        PendingLuckyStunManagementUrl = string.Empty;
        PendingLuckyStunUsername = string.Empty;
        PendingLuckyStunPassword = string.Empty;
        PendingLuckyStunServerUsername = string.Empty;
        PendingLuckyStunServerPassword = string.Empty;
        PendingLuckyStunRuleId = string.Empty;
        PendingLuckyStunPathSuffix = string.Empty;
        PendingLuckyStunAutoUpdate = true;
        PendingLuckyStunUpdateIntervalMinutes = 30;
        PendingLuckyStunStatus = string.Empty;
        IsPendingLuckyStunPasswordVisible = false;
    }

    private void OpenLuckyStunSourcePanel()
    {
        IsNetworkLoginPanelOpen = false;
        IsSourcePopupOpen = false;
        IsMediaServerPanelOpen = false;
        ResetLuckyStunSourceForm();
        pendingLuckyStunEditSourceId = null;
        OnPropertyChanged("LuckyStunPanelTitle");
        LuckyStunSourceProtocolOption = MediaServerProtocolOptions[0];
        PreloadLuckyStunFormFromSettings();
        LuckyStunSourceStatus = "先点击“登录检测”读取 Lucky STUN 穿透规则，再选择要保存的媒体服务器类型与登录信息。";
        IsLuckyStunSourcePanelOpen = true;
        OnCommandStateChanged();
    }

    private void EditLuckyStunSource(MediaSource? item)
    {
        if (item == null)
        {
            return;
        }
        LuckyStunSourceConfiguration? lucky = item.GetAddressConfiguration().LuckyStun;
        if (lucky == null)
        {
            return;
        }
        IsNetworkLoginPanelOpen = false;
        IsSourcePopupOpen = false;
        IsMediaServerPanelOpen = false;
        ResetLuckyStunSourceForm();
        pendingLuckyStunEditSourceId = item.Id;
        LuckyStunSourceProtocolOption = MediaServerProtocolOptions.FirstOrDefault(option => string.Equals(option.Value, NormalizeMediaServerProtocolType(item.ProtocolType), StringComparison.OrdinalIgnoreCase)) ?? MediaServerProtocolOptions[0];
        PendingLuckyStunManagementUrl = lucky.ManagementUrl ?? string.Empty;
        PendingLuckyStunUsername = lucky.Username ?? string.Empty;
        PendingLuckyStunPassword = lucky.Password ?? string.Empty;
        PendingLuckyStunRuleId = lucky.RuleId ?? string.Empty;
        PendingLuckyStunPathSuffix = lucky.PathSuffix ?? string.Empty;
        PendingLuckyStunAutoUpdate = lucky.AutoUpdate;
        PendingLuckyStunUpdateIntervalMinutes = lucky.UpdateIntervalMinutes;
        if (LuckyStunSourceUsesWebDav)
        {
            WebDavAuthConfig? webDavAuth = MediaSourceAuthConfigSerializer.DeserializeWebDav(item.AuthConfig);
            PendingLuckyStunServerUsername = webDavAuth?.Username ?? string.Empty;
            PendingLuckyStunServerPassword = webDavAuth?.Password ?? string.Empty;
        }
        else
        {
            MediaServerAuthConfig? serverAuth = MediaSourceAuthConfigSerializer.DeserializeMediaServer(item.AuthConfig);
            PendingLuckyStunServerUsername = (LuckyStunSourceUsesPlex ? string.Empty : serverAuth?.UserId) ?? string.Empty;
            PendingLuckyStunServerPassword = serverAuth?.Token ?? string.Empty;
        }
        OnPropertyChanged("LuckyStunPanelTitle");
        LuckyStunSourceStatus = "正在编辑：" + item.Name + "。修改后点击“保存并同步”写回该媒体源。";
        IsLuckyStunSourcePanelOpen = true;
        OnCommandStateChanged();
    }

    private void CloseLuckyStunSourcePanel()
    {
        ResetLuckyStunSourceForm();
        IsLuckyStunSourcePanelOpen = false;
        OnCommandStateChanged();
    }

    private void ResetLuckyStunSourceForm()
    {
        LuckyStunSourceProtocolOption = null;
        LuckyStunSourceStatus = string.Empty;
        ResetPendingLuckyStunForm();
    }

    /// <summary>
    /// Prefills the Lucky STUN panel with the persisted management login so the
    /// form is not empty after reopening it or restarting the app.
    /// </summary>
    private void PreloadLuckyStunFormFromSettings()
    {
        if (string.IsNullOrWhiteSpace(Settings.LuckyStunManagementUrl)
            && string.IsNullOrWhiteSpace(Settings.LuckyStunUsername)
            && string.IsNullOrWhiteSpace(Settings.LuckyStunPassword))
        {
            return;
        }

        PendingLuckyStunManagementUrl = Settings.LuckyStunManagementUrl;
        PendingLuckyStunUsername = Settings.LuckyStunUsername;
        PendingLuckyStunPassword = Settings.LuckyStunPassword;
        PendingLuckyStunAutoUpdate = Settings.LuckyStunAutoUpdate;
        PendingLuckyStunUpdateIntervalMinutes = Settings.LuckyStunUpdateIntervalMinutes;
        PendingLuckyStunRuleId = Settings.LuckyStunSelectedRuleId;
    }

    private async Task TestLuckyStunWebDavSourceAsync()
    {
        LuckyStunRule? rule = Settings.LuckyStunRules.FirstOrDefault(item => string.Equals(item.Id, PendingLuckyStunRuleId, StringComparison.OrdinalIgnoreCase));
        if (rule == null)
        {
            SetLuckyStunPanelStatus("请先点击“登录检测”并选择穿透规则。");
            return;
        }
        IsBusy = true;
        OnCommandStateChanged();
        try
        {
            SetLuckyStunPanelStatus("正在登录 WebDAV 穿透地址...");
            string normalizedAddress = LuckyStunAddressHelper.NormalizeAddress(rule.Address);
            string updatedAddress = LuckyStunAddressHelper.AppendPathSuffix(PendingLuckyStunPathSuffix.Trim().Trim('/'), normalizedAddress);
            string baseUrl = MediaSourceNormalizer.NormalizeBaseUrl(MediaSourceProtocol.WebDav, updatedAddress);
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                SetLuckyStunPanelStatus("穿透地址不是有效的 WebDAV 地址。");
                return;
            }
            MediaSource probe = new MediaSource
            {
                Name = "Lucky STUN WebDAV",
                ProtocolType = "webdav",
                BaseUrl = baseUrl,
                AuthConfig = MediaSourceAuthConfigSerializer.SerializeWebDav(new WebDavAuthConfig(PendingLuckyStunServerUsername, PendingLuckyStunServerPassword))
            };
            WebDavConnectionTestResult result = await webDavConnectionTester.TestConnectionAsync(probe);
            SetLuckyStunPanelStatus(result.Message);
        }
        catch (Exception ex)
        {
            SetLuckyStunPanelStatus("WebDAV 登录测试失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    partial void OnLuckyStunSourceProtocolOptionChanged(SettingsOptionItem? value)
    {
        OnPropertyChanged("LuckyStunSourceUsesWebDav");
        OnPropertyChanged("LuckyStunSourceUsesPlex");
        OnPropertyChanged("LuckyStunSourceUserWatermark");
        OnPropertyChanged("LuckyStunSourceCredentialWatermark");
        OnPropertyChanged("LuckyStunSourceCredentialLabel");
        OnPropertyChanged("LuckyStunSourceSaveEnabled");
        SaveLuckyStunSourceCommand.NotifyCanExecuteChanged();
        PrefillLuckyStunServerCredential();
    }

    private void PrefillLuckyStunServerCredential()
    {
        string luckyProtocol = LuckyStunProtocolType(LuckyStunSourceProtocolOption);
        if (luckyProtocol is not ("webdav" or "emby" or "jellyfin" or "plex"))
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(PendingLuckyStunServerUsername) || !string.IsNullOrWhiteSpace(PendingLuckyStunServerPassword))
        {
            return;
        }
        NetworkCredentialEntry? entry = networkCredentialStore.FindBest(ResolveMediaServerProtocolKind(luckyProtocol), string.Empty);
        if (entry == null)
        {
            NetworkCredentialEntry? latest = networkCredentialStore.FindLatest();
            if (latest != null && string.Equals(latest.ProtocolType, luckyProtocol, StringComparison.OrdinalIgnoreCase))
            {
                entry = latest;
            }
        }
        if (entry == null)
        {
            return;
        }
        PendingLuckyStunServerUsername = entry.Username;
        PendingLuckyStunServerPassword = entry.Password;
    }

    partial void OnIsPendingLuckyStunLoginInProgressChanged(bool value)
    {
        OnPropertyChanged("LuckyStunSourceSaveEnabled");
        SaveLuckyStunSourceCommand.NotifyCanExecuteChanged();
    }

    partial void OnPendingLuckyStunRuleIdChanged(string value)
    {
        OnPropertyChanged("LuckyStunSourceSaveEnabled");
        SaveLuckyStunSourceCommand.NotifyCanExecuteChanged();
    }

    partial void OnPendingLuckyStunPasswordChanged(string value)
    {
        OnPropertyChanged("LuckyStunSourceSaveEnabled");
        SaveLuckyStunSourceCommand.NotifyCanExecuteChanged();
    }

    partial void OnPendingLuckyStunServerPasswordChanged(string value)
    {
        OnPropertyChanged("LuckyStunSourceSaveEnabled");
        SaveLuckyStunSourceCommand.NotifyCanExecuteChanged();
    }

    private async Task SaveLuckyStunSourceAsync()
    {
        if (luckyStunClient == null)
        {
            LuckyStunSourceStatus = "Lucky STUN 服务尚未初始化。";
            return;
        }
        string protocolType = LuckyStunProtocolType(LuckyStunSourceProtocolOption);
        if (string.IsNullOrWhiteSpace(protocolType))
        {
            LuckyStunSourceStatus = "请选择媒体服务器类型。";
            return;
        }
        LuckyStunSourceConfiguration configuration = BuildPendingLuckyStunConfiguration();
        if (configuration == null || string.IsNullOrWhiteSpace(configuration.ManagementUrl))
        {
            LuckyStunSourceStatus = "请填写 Lucky STUN 管理地址。";
            return;
        }
        IsBusy = true;
        OnCommandStateChanged();
        try
        {
            LuckyStunSourceStatus = "正在登录 Lucky STUN 并读取穿透规则...";
            LuckyStunSettings settings = new LuckyStunSettings
            {
                ManagementUrl = configuration.ManagementUrl,
                Username = configuration.Username,
                Password = configuration.Password,
                SelectedRuleId = configuration.RuleId,
                SelectedRuleName = configuration.RuleName
            };
            LuckyStunSnapshot snapshot = await luckyStunClient.FetchAsync(settings, CancellationToken.None);
            LuckyStunRule rule = snapshot.Rules.FirstOrDefault(item => string.Equals(item.Id, configuration.RuleId, StringComparison.OrdinalIgnoreCase)) ?? snapshot.SelectedRule;
            if (rule == null)
            {
                LuckyStunSourceStatus = "请先登录检测并选择有效的穿透规则。";
                return;
            }
            string normalizedAddress = LuckyStunAddressHelper.NormalizeAddress(rule.Address);
            if (string.IsNullOrWhiteSpace(normalizedAddress))
            {
                LuckyStunSourceStatus = "穿透规则没有返回有效地址。";
                return;
            }
            string updatedAddress = LuckyStunAddressHelper.AppendPathSuffix(configuration.PathSuffix, normalizedAddress);
            string ruleName = rule.Name;
            string protocolLabel = LuckyStunSourceProtocolOption?.Label ?? protocolType;
            string hostLabel = Uri.TryCreate(updatedAddress, UriKind.Absolute, out Uri luckyUri) && !string.IsNullOrWhiteSpace(luckyUri.Host) ? luckyUri.Host : updatedAddress;
            string displayName = string.IsNullOrWhiteSpace(PendingLuckyStunManagementUrl) ? string.Empty : string.Empty;
            displayName = "Lucky STUN · " + ruleName;
            if (Uri.TryCreate(updatedAddress, UriKind.Absolute, out Uri nameUri) && !string.IsNullOrWhiteSpace(nameUri.Host))
            {
                displayName = "Lucky STUN · " + nameUri.Host;
            }

            MediaSource source;
            MediaSourceProtocol protocolKind;
            if (protocolType == "webdav")
            {
                string webdavBaseUrl = MediaSourceNormalizer.NormalizeBaseUrl(MediaSourceProtocol.WebDav, updatedAddress);
                if (string.IsNullOrWhiteSpace(webdavBaseUrl))
                {
                    LuckyStunSourceStatus = "穿透地址不是有效的 WebDAV 地址。";
                    return;
                }
                source = new MediaSource
                {
                    Name = displayName,
                    ProtocolType = "webdav",
                    BaseUrl = webdavBaseUrl,
                    AuthConfig = MediaSourceAuthConfigSerializer.SerializeWebDav(new WebDavAuthConfig(PendingLuckyStunServerUsername, PendingLuckyStunServerPassword))
                };
                protocolKind = MediaSourceProtocol.WebDav;
            }
            else
            {
                protocolKind = protocolType switch
                {
                    "emby" => MediaSourceProtocol.Emby,
                    "jellyfin" => MediaSourceProtocol.Jellyfin,
                    _ => MediaSourceProtocol.Plex
                };
                string mediaServerBaseUrl = MediaSourceNormalizer.NormalizeBaseUrl(protocolKind, updatedAddress);
                if (string.IsNullOrWhiteSpace(mediaServerBaseUrl))
                {
                    LuckyStunSourceStatus = "穿透地址不是有效的媒体服务器地址。";
                    return;
                }
                string token = PendingLuckyStunServerPassword;
                string userId = protocolType == "plex" ? string.Empty : PendingLuckyStunServerUsername.Trim();
                source = new MediaSource
                {
                    Name = displayName,
                    ProtocolType = protocolType,
                    BaseUrl = mediaServerBaseUrl,
                    AuthConfig = MediaSourceAuthConfigSerializer.SerializeMediaServer(new MediaServerAuthConfig(token, userId))
                };
            }
            source.Name = displayName;
            if (!string.IsNullOrWhiteSpace(PendingLuckyStunServerUsername) || !string.IsNullOrWhiteSpace(PendingLuckyStunServerPassword))
            {
                networkCredentialStore.Save(protocolKind, source.BaseUrl, PendingLuckyStunServerUsername.Trim(), PendingLuckyStunServerPassword);
            }
            if (!source.IsValidConfiguration())
            {
                LuckyStunSourceStatus = "穿透地址无效，请输入以 http:// 或 https:// 开头的地址。";
                return;
            }

            MediaSourceAddressConfiguration addressConfiguration = source.GetAddressConfiguration();
            addressConfiguration.LocalAddress = updatedAddress;
            addressConfiguration.LocalLabel = string.Empty;
            addressConfiguration.ActiveAddress = updatedAddress;
            addressConfiguration.ActiveLabel = string.Empty;
            configuration.RuleId = rule.Id;
            configuration.RuleName = ruleName;
            addressConfiguration.LuckyStun = configuration;
            source.SetAddressConfiguration(addressConfiguration);

            bool wasEditing = pendingLuckyStunEditSourceId.HasValue;
            List<MediaSource> allSources = (await mediaSourceRepository.GetAllAsync()).ToList();
            MediaSource existing = null;
            if (wasEditing)
            {
                existing = allSources.FirstOrDefault(item => item.Id == pendingLuckyStunEditSourceId);
            }
            if (existing == null)
            {
                existing = allSources.FirstOrDefault(item =>
                {
                    if (!item.IsActive)
                    {
                        return false;
                    }
                    LuckyStunSourceConfiguration existingLucky = item.GetAddressConfiguration().LuckyStun;
                    return existingLucky != null
                        && string.Equals(existingLucky.ManagementUrl, configuration.ManagementUrl, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(existingLucky.RuleId, rule.Id, StringComparison.OrdinalIgnoreCase);
                });
            }
            if (wasEditing && existing != null && !string.IsNullOrWhiteSpace(existing.Name))
            {
                source.Name = existing.Name;
                displayName = existing.Name;
            }
            if (existing == null)
            {
                // 新增穿透源时先读取共享文件夹，由用户标星选择要挂载的目录，避免整站挂载扫描。
                IReadOnlyList<NetworkShareFolderItem> luckyMountFolders = Array.Empty<NetworkShareFolderItem>();
                if (protocolKind == MediaSourceProtocol.WebDav)
                {
                    LuckyStunSourceStatus = "正在读取穿透地址的 WebDAV 共享文件夹...";
                    try
                    {
                        luckyMountFolders = await networkShareDiscoveryService.ListFoldersAsync(new NetworkSourceDiscoveryItem
                        {
                            Name = displayName,
                            ProtocolType = "webdav",
                            BaseUrl = source.BaseUrl,
                            Description = source.BaseUrl
                        }, PendingLuckyStunServerUsername, PendingLuckyStunServerPassword);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException || ex is HttpRequestException || ex is InvalidOperationException || ex is IOException)
                    {
                        bool isUnauthorized = ex is UnauthorizedAccessException
                            || ex.Message.Contains("401", StringComparison.Ordinal)
                            || ex.Message.Contains("403", StringComparison.Ordinal);
                        LuckyStunSourceStatus = isUnauthorized ? "WebDAV 连接失败：用户名或密码无效。" : "读取 WebDAV 共享文件夹失败：" + ex.Message;
                        ShowToast(LuckyStunSourceStatus, ToastKind.Error);
                        return;
                    }
                }
                else
                {
                    LuckyStunSourceStatus = "正在预扫描穿透 " + protocolLabel + " 媒体库...";
                    try
                    {
                        using CancellationTokenSource luckyPreflightTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90L));
                        luckyMountFolders = ((mediaServerPreflightService != null) ? (await mediaServerPreflightService.ListLibrariesAsync(source, luckyPreflightTimeout.Token)) : Array.Empty<NetworkShareFolderItem>());
                    }
                    catch (OperationCanceledException)
                    {
                        LuckyStunSourceStatus = "媒体服务器预扫描超时。";
                        ShowToast(LuckyStunSourceStatus, ToastKind.Error);
                        return;
                    }
                    catch (Exception ex) when (((ex is HttpRequestException || ex is InvalidOperationException || ex is IOException) ? 1 : 0) != 0)
                    {
                        LuckyStunSourceStatus = "媒体服务器预扫描失败：" + ex.Message;
                        ShowToast(LuckyStunSourceStatus, ToastKind.Error);
                        return;
                    }
                }
                if (luckyMountFolders.Count > 0)
                {
                    List<NetworkShareFolderItem> luckySelectableFolders = luckyMountFolders
                        .Select((folder, index) => index == 0 ? folder.WithStarred(true) : folder)
                        .ToList();
                    ReplaceItems(NetworkShareFolders, luckySelectableFolders);
                    NotifyNetworkShareFolderStateChanged();
                    PendingNetworkProtocolType = source.ProtocolType;
                    PendingNetworkBaseUrl = source.BaseUrl;
                    PendingNetworkDisplayName = displayName;
                    PendingNetworkUsername = protocolKind == MediaSourceProtocol.WebDav ? PendingLuckyStunServerUsername : string.Empty;
                    PendingNetworkPassword = protocolKind == MediaSourceProtocol.WebDav ? PendingLuckyStunServerPassword : string.Empty;
                    pendingLuckyStunMountConfiguration = configuration;
                    pendingLuckyStunMountRootAddress = source.GetNormalizedBaseUrl();
                    NetworkLoginTitle = "选择 " + protocolLabel + " 穿透共享文件夹";
                    ResetLuckyStunSourceForm();
                    IsLuckyStunSourcePanelOpen = false;
                    IsNetworkLoginPanelOpen = true;
                    LuckyStunSourceStatus = "连接成功：读取到 " + luckySelectableFolders.Count + " 个可挂载文件夹，默认已选第一项，可点星标调整，确认后统一挂载扫描。";
                    NetworkSourceStatus = LuckyStunSourceStatus;
                    StatusMessage = LuckyStunSourceStatus;
                    try
                    {
                        await Settings.SaveLuckyStunCredentialsAsync(configuration.ManagementUrl, configuration.Username, configuration.Password, configuration.AutoUpdate, configuration.UpdateIntervalMinutes, rule.Id, ruleName);
                    }
                    catch
                    {
                        // 全局登录信息落盘失败不影响本次保存结果，源上的穿透配置已经持久化。
                    }
                    Settings.StartLuckyStunAutoUpdateLoop();
                    return;
                }
            }
            long savedId;
            if (existing != null)
            {
                existing.Name = source.Name;
                existing.ProtocolType = source.ProtocolType;
                existing.BaseUrl = source.BaseUrl;
                existing.AuthConfig = source.AuthConfig;
                existing.SetAddressConfiguration(addressConfiguration);
                savedId = await mediaSourceRepository.UpdateAsync(existing) ? existing.Id.GetValueOrDefault() : 0;
            }
            else
            {
                savedId = await mediaSourceRepository.AddAsync(source);
            }

            pendingLuckyStunEditSourceId = null;
            OnPropertyChanged("LuckyStunPanelTitle");
            IsLuckyStunSourcePanelOpen = false;
            ResetLuckyStunSourceForm();
            await ReloadLibraryAsync();
            try
            {
                await Settings.SaveLuckyStunCredentialsAsync(configuration.ManagementUrl, configuration.Username, configuration.Password, configuration.AutoUpdate, configuration.UpdateIntervalMinutes, rule.Id, ruleName);
            }
            catch
            {
                // 全局登录信息落盘失败不影响本次保存结果，源上的穿透配置已经持久化。
            }
            Settings.StartLuckyStunAutoUpdateLoop();
            StatusMessage = savedId > 0
                ? (wasEditing ? "已更新 Lucky STUN 穿透媒体源：" : "已保存 Lucky STUN 穿透媒体源：") + displayName + "（" + updatedAddress + "）"
                : "Lucky STUN 穿透媒体源已保存：" + displayName + "（" + updatedAddress + "）";
            ShowToast((wasEditing ? "已更新 Lucky STUN 穿透媒体源：" : "已保存 Lucky STUN 穿透媒体源：") + displayName, ToastKind.Success);
        }
        catch (Exception ex)
        {
            LuckyStunSourceStatus = "Lucky STUN 穿透媒体源保存失败：" + ex.Message;
            ShowToast("Lucky STUN 穿透媒体源保存失败：" + ex.Message, ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private async Task SaveNetworkLoginAsync()
    {
        string normalizedBaseUrl = NormalizePendingNetworkBaseUrl(PendingNetworkBaseUrl.Trim());
        string protocolType = ResolvePendingNetworkProtocolType(PendingNetworkProtocolType, normalizedBaseUrl);
        if (string.Equals(protocolType, "webdav", StringComparison.OrdinalIgnoreCase))
        {
            normalizedBaseUrl = MediaSourceNormalizer.NormalizeBaseUrl(MediaSourceProtocol.WebDav, normalizedBaseUrl);
        }
        PendingNetworkBaseUrl = normalizedBaseUrl;
        NetworkSourceDiscoveryItem source = new NetworkSourceDiscoveryItem
        {
            Name = normalizedBaseUrl,
            ProtocolType = protocolType,
            BaseUrl = normalizedBaseUrl,
            Description = normalizedBaseUrl
        };
        if (!source.ProtocolKind.HasValue || string.IsNullOrWhiteSpace(source.BaseUrl))
        {
            NetworkSourceStatus = "网络媒体源地址无效。";
            return;
        }
        IsBusy = true;
        OnCommandStateChanged();
        try
        {
            NetworkSourceStatus = "正在读取共享文件夹...";
            IReadOnlyList<NetworkShareFolderItem> folders = await networkShareDiscoveryService.ListFoldersAsync(source, PendingNetworkUsername, PendingNetworkPassword);
            MediaSourceProtocol? sourceProtocol = source.ProtocolKind;
            bool flag;
            if (sourceProtocol.HasValue)
            {
                MediaSourceProtocol valueOrDefault = sourceProtocol.GetValueOrDefault();
                if ((uint)(valueOrDefault - 1) <= 1u)
                {
                    flag = true;
                    goto IL_0229;
                }
            }
            flag = false;
            goto IL_0229;
            IL_0229:
            if (flag)
            {
                networkCredentialStore.Save(sourceProtocol.GetValueOrDefault(), source.BaseUrl, PendingNetworkUsername, PendingNetworkPassword);
            }
            ReplaceItems(NetworkShareFolders, folders);
            NotifyNetworkShareFolderStateChanged();
            NetworkSourceStatus = ((folders.Count == 0) ? "没有读取到可挂载的共享文件夹，请检查地址、用户名或密码。" : $"已读取到 {folders.Count} 个可挂载文件夹，可给多个目标点星标，关闭列表时统一挂载。");
        }
        catch (UnauthorizedAccessException)
        {
            ReplaceItems(NetworkShareFolders, Array.Empty<NetworkShareFolderItem>());
            NotifyNetworkShareFolderStateChanged();
            NetworkSourceStatus = "登录失败：用户名或密码无效。";
            ShowToast("登录失败：用户名或密码无效", ToastKind.Error);
        }
        catch (Exception ex2) when (((ex2 is HttpRequestException || ex2 is InvalidOperationException || ex2 is IOException) ? 1 : 0) != 0)
        {
            ReplaceItems(NetworkShareFolders, Array.Empty<NetworkShareFolderItem>());
            NotifyNetworkShareFolderStateChanged();
            NetworkSourceStatus = "读取共享文件夹失败：" + ex2.Message;
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private static string NormalizePendingNetworkBaseUrl(string baseUrl)
    {
        if (!baseUrl.StartsWith("smb://", StringComparison.OrdinalIgnoreCase) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri result) || string.IsNullOrWhiteSpace(result.Host))
        {
            return baseUrl;
        }
        string text = Uri.UnescapeDataString(result.AbsolutePath).Trim('/').Replace('/', '\\');
        return string.IsNullOrWhiteSpace(text) ? ("\\\\" + result.Host) : ("\\\\" + result.Host + "\\" + text);
    }

    private static string ResolvePendingNetworkProtocolType(string protocolType, string baseUrl)
    {
        if (baseUrl.StartsWith("\\\\", StringComparison.Ordinal) || baseUrl.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
        {
            return "smb";
        }
        if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "webdav";
        }
        return string.IsNullOrWhiteSpace(protocolType) ? "webdav" : protocolType.Trim().ToLowerInvariant();
    }

    private async Task MountNetworkFolderAsync(NetworkShareFolderItem? folder)
    {
        if (folder != null)
        {
            await MountNetworkFoldersAsync(new[] { folder });
        }
    }

    private void ToggleNetworkFolderStar(NetworkShareFolderItem? folder)
    {
        if (folder != null)
        {
            List<NetworkShareFolderItem> source = NetworkShareFolders.Select((NetworkShareFolderItem item) => (string.Equals(item.BaseUrl, folder.BaseUrl, StringComparison.OrdinalIgnoreCase) && string.Equals(item.ProtocolType, folder.ProtocolType, StringComparison.OrdinalIgnoreCase) && string.Equals(item.AuthConfig ?? string.Empty, folder.AuthConfig ?? string.Empty, StringComparison.Ordinal) && string.Equals(item.Name, folder.Name, StringComparison.OrdinalIgnoreCase)) ? item.WithStarred(!item.IsStarred) : item).ToList();
            ReplaceItems(NetworkShareFolders, source);
            NotifyNetworkShareFolderStateChanged();
            OnCommandStateChanged();
        }
    }

    private async Task MountStarredNetworkFoldersAsync()
    {
        List<NetworkShareFolderItem> starredFolders = NetworkShareFolders.Where((NetworkShareFolderItem folder) => folder.IsStarred).ToList();
        if (starredFolders.Count == 0)
        {
            ClearNetworkLoginPanel(clearStatus: true);
            OnCommandStateChanged();
        }
        else
        {
            await MountNetworkFoldersAsync(starredFolders);
        }
    }

    private async Task MountNetworkFoldersAsync(IReadOnlyList<NetworkShareFolderItem> folders)
    {
        if (folders.Count == 0)
        {
            return;
        }
        CancellationTokenSource automationCancellationTokenSource = BeginLibraryAutomation();
        CancellationToken cancellationToken = automationCancellationTokenSource.Token;
        IsBusy = true;
        OnCommandStateChanged();
        try
        {
            List<string> mountedNames = new List<string>(folders.Count);
            List<long> mountedSourceIds = new List<long>(folders.Count);
            foreach (NetworkShareFolderItem folder in folders)
            {
                MediaSource source = folder.ToMediaSource();
                source.SetAddressConfiguration(BuildPendingAddressConfiguration(source.BaseUrl, source.ProtocolKind));
                long sourceId = await mediaSourceRepository.AddAsync(source);
                if (sourceId > 0)
                {
                    mountedSourceIds.Add(sourceId);
                }
                mountedNames.Add(source.Name);
            }
            ClearNetworkLoginPanel(clearStatus: false);
            await ReloadLibraryAsync();
            string folderSummary = ((folders.Count == 1) ? mountedNames[0] : $"{folders.Count} 个文件夹");
            NetworkSourceStatus = "已挂载媒体源：" + folderSummary + "，正在扫描并刮削...";
            StatusMessage = NetworkSourceStatus;
            IsBusy = false;
            OnCommandStateChanged();
            LastScanSummary = await RunLibraryScanAndArtworkAsync(isExplicitRequest: true, forceThumbnails: true, cancellationToken, mountedSourceIds);
            StatusMessage = (string.IsNullOrWhiteSpace(StatusMessage) ? ("已挂载并扫描媒体源：" + folderSummary) : ("已挂载媒体源：" + folderSummary + "。" + StatusMessage));
            ShowToast("已挂载并扫描完成：" + folderSummary, ToastKind.Success);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "已停止新挂载媒体源的扫描和刮削任务。";
        }
        catch (Exception ex2)
        {
            Exception ex3 = ex2;
            NetworkSourceStatus = "挂载媒体源失败：" + ex3.Message;
            StatusMessage = NetworkSourceStatus;
            ShowToast("挂载媒体源失败：" + ex3.Message, ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
            CompleteLibraryAutomation(automationCancellationTokenSource);
            OnCommandStateChanged();
        }
    }

    private MediaSourceAddressConfiguration BuildPendingAddressConfiguration(string localAddress, MediaSourceProtocol? protocolKind)
    {
        MediaSourceAddressConfiguration mediaSourceAddressConfiguration = new MediaSourceAddressConfiguration
        {
            LocalAddress = localAddress.Trim(),
            LocalLabel = "局域网",
            ActiveAddress = localAddress.Trim(),
            ActiveLabel = "局域网"
        };
        foreach (MediaSourceExternalAddress pending in PendingExternalAddresses)
        {
            string text = pending.Address.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (protocolKind == MediaSourceProtocol.Smb)
                {
                    text = NormalizePendingNetworkBaseUrl(text);
                }
                else if (protocolKind == MediaSourceProtocol.WebDav)
                {
                    text = MediaSourceNormalizer.NormalizeBaseUrl(MediaSourceProtocol.WebDav, text);
                }
                LuckyStunRule luckyStunRule = Settings.LuckyStunRules.FirstOrDefault((LuckyStunRule item) => string.Equals(item.Id, pending.LuckyRuleId, StringComparison.OrdinalIgnoreCase));
                mediaSourceAddressConfiguration.ExternalAddresses.Add(new MediaSourceExternalAddress
                {
                    Address = text,
                    Label = pending.Label.Trim(),
                    UseLuckyStun = pending.UseLuckyStun,
                    LuckyRuleId = pending.LuckyRuleId.Trim(),
                    LuckyRuleName = (luckyStunRule?.Name ?? pending.LuckyRuleName.Trim())
                });
            }
        }
        if (pendingLuckyStunMountConfiguration != null)
        {
            LuckyStunSourceConfiguration luckyMount = pendingLuckyStunMountConfiguration;
            string luckyRootAddress = pendingLuckyStunMountRootAddress.TrimEnd('/');
            string mountedAddress = mediaSourceAddressConfiguration.LocalAddress.TrimEnd('/');
            string luckyFolderPath = string.Empty;
            if (luckyRootAddress.Length > 0
                && mountedAddress.Length > luckyRootAddress.Length
                && mountedAddress.StartsWith(luckyRootAddress, StringComparison.OrdinalIgnoreCase))
            {
                luckyFolderPath = mountedAddress.Substring(luckyRootAddress.Length).Trim('/');
            }
            if (luckyFolderPath.Length > 0)
            {
                string existingSuffix = luckyMount.PathSuffix.Trim('/');
                luckyMount = new LuckyStunSourceConfiguration
                {
                    ManagementUrl = luckyMount.ManagementUrl,
                    Username = luckyMount.Username,
                    Password = luckyMount.Password,
                    RuleId = luckyMount.RuleId,
                    RuleName = luckyMount.RuleName,
                    DisplayName = luckyMount.DisplayName,
                    AutoUpdate = luckyMount.AutoUpdate,
                    UpdateIntervalMinutes = luckyMount.UpdateIntervalMinutes,
                    LastUpdatedAt = luckyMount.LastUpdatedAt,
                    PathSuffix = (existingSuffix.Length == 0) ? luckyFolderPath : (existingSuffix + "/" + luckyFolderPath)
                };
            }
            mediaSourceAddressConfiguration.LuckyStun = luckyMount;
            mediaSourceAddressConfiguration.LocalLabel = "穿透";
            mediaSourceAddressConfiguration.ActiveLabel = "穿透";
        }
        return mediaSourceAddressConfiguration;
    }

    private async Task EditSourceAsync(MediaSource? source)
    {
        if (source != null)
        {
            if (!source.Id.HasValue)
            {
                StatusMessage = "当前暂不支持编辑此媒体源：" + source.Name;
            }
            else if (source.SupportsPickerEditing)
            {
                await EditLocalSourceAsync(source);
            }
            else if (!source.SupportsInlineEditing)
            {
                StatusMessage = "当前暂不支持编辑此媒体源：" + source.Name;
            }
            else
            {
                BeginEditWebDavSource(source);
            }
        }
    }

    private void BeginEditWebDavSource(MediaSource source)
    {
        long? id = source.Id;
        if (id.HasValue)
        {
            long valueOrDefault = id.GetValueOrDefault();
            if (true)
            {
                WebDavAuthConfig webDavAuthConfig = MediaSourceAuthConfigSerializer.DeserializeWebDav(source.AuthConfig);
                EditingWebDavSourceId = valueOrDefault;
                PendingWebDavName = source.Name;
                PendingWebDavUrl = source.BaseUrl;
                PendingWebDavUsername = webDavAuthConfig?.Username ?? string.Empty;
                PendingWebDavPassword = webDavAuthConfig?.Password ?? string.Empty;
                IsSourcePopupOpen = true;
                IsSearchPopupOpen = false;
                IsSortPopupOpen = false;
                IsSettingsPopupOpen = false;
                StatusMessage = "正在编辑媒体源：" + source.Name;
                NotifyWebDavFormStateChanged();
                OnCommandStateChanged();
                return;
            }
        }
        StatusMessage = "当前暂不支持编辑此媒体源：" + source.Name;
    }

    private async Task EditLocalSourceAsync(MediaSource source)
    {
        IsSourcePopupOpen = false;
        IsSettingsPopupOpen = false;
        ResetWebDavForm();
        string selectedPath = await folderPickerService.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            StatusMessage = "已取消更换目录：" + source.Name;
            return;
        }
        if (!Directory.Exists(selectedPath))
        {
            StatusMessage = "文件夹不存在：" + selectedPath;
            return;
        }
        IsBusy = true;
        OnCommandStateChanged();
        try
        {
            MediaSource updatedSource = CreateLocalSource(selectedPath, source.Id);
            if (!(await mediaSourceRepository.UpdateAsync(updatedSource)))
            {
                StatusMessage = "保存本地媒体源失败，可能是媒体源不存在，或与现有目录重复。";
                return;
            }
            await ReloadLibraryAsync();
            StatusMessage = "已更新本地媒体源：" + updatedSource.Name;
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
        OpenLuckyStunSourcePanelCommand.NotifyCanExecuteChanged();
        CloseLuckyStunSourcePanelCommand.NotifyCanExecuteChanged();
        TestPendingLuckyStunLoginCommand.NotifyCanExecuteChanged();
        SaveLuckyStunSourceCommand.NotifyCanExecuteChanged();
        TestLuckyStunWebDavSourceCommand.NotifyCanExecuteChanged();
        EditLuckyStunSourceCommand.NotifyCanExecuteChanged();
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
        KeepOfflineCacheCommand.NotifyCanExecuteChanged();
        ConfirmCancelOfflineCacheCommand.NotifyCanExecuteChanged();
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
        OpenPosterDoubanSearchCommand.NotifyCanExecuteChanged();
        UnbindPosterDoubanCommand.NotifyCanExecuteChanged();
        SavePosterMetadataEditCommand.NotifyCanExecuteChanged();
        EditHomeFolderPosterCommand.NotifyCanExecuteChanged();
        RenameFolderPersonCommand.NotifyCanExecuteChanged();
        ChooseFolderLocalPosterCommand.NotifyCanExecuteChanged();
        SelectFolderPosterCommand.NotifyCanExecuteChanged();
        ResetFolderPosterCommand.NotifyCanExecuteChanged();
        OpenEpisodeEditCommand.NotifyCanExecuteChanged();
        SaveEpisodeEditCommand.NotifyCanExecuteChanged();
        ChooseEpisodeThumbnailImageCommand.NotifyCanExecuteChanged();
        EditSourceCommand.NotifyCanExecuteChanged();
        CancelWebDavEditCommand.NotifyCanExecuteChanged();
        RemoveSourceCommand.NotifyCanExecuteChanged();
        OpenRemoveSourceConfirmationCommand.NotifyCanExecuteChanged();
        ConfirmRemoveSourceCommand.NotifyCanExecuteChanged();
        CancelRemoveSourceConfirmationCommand.NotifyCanExecuteChanged();
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
        CancellationTokenSource result = (libraryAutomationCancellationTokenSource = new CancellationTokenSource());
        IsLibraryScanInProgress = true;
        return result;
    }

    private void CancelActiveLibraryAutomation()
    {
        forceRefreshQueued = false;
        CancellationTokenSource cancellationTokenSource = libraryAutomationCancellationTokenSource;
        if (cancellationTokenSource != null)
        {
            if (!cancellationTokenSource.IsCancellationRequested)
            {
                cancellationTokenSource.Cancel();
            }
            libraryAutomationCancellationTokenSource = null;
            IsLibraryScanInProgress = false;
        }
    }

    private void CompleteLibraryAutomation(CancellationTokenSource cancellationTokenSource)
    {
        if (libraryAutomationCancellationTokenSource == cancellationTokenSource)
        {
            libraryAutomationCancellationTokenSource = null;
            IsLibraryScanInProgress = false;
        }
        cancellationTokenSource.Dispose();
    }

    private MediaSource? BuildPendingWebDavSource()
    {
        string text = MediaSourceNormalizer.NormalizeBaseUrl(MediaSourceProtocol.WebDav, PendingWebDavUrl.Trim());
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusMessage = "请输入 WebDAV 地址。";
            return null;
        }
        MediaSource mediaSource = new MediaSource
        {
            Name = PendingWebDavName.Trim(),
            ProtocolType = "webdav",
            BaseUrl = text,
            AuthConfig = MediaSourceAuthConfigSerializer.SerializeWebDav(new WebDavAuthConfig(PendingWebDavUsername, PendingWebDavPassword))
        };
        if (!mediaSource.IsValidConfiguration())
        {
            StatusMessage = "WebDAV 地址无效，请输入以 http:// 或 https:// 开头的目录地址。";
            return null;
        }
        if (string.IsNullOrWhiteSpace(mediaSource.Name))
        {
            mediaSource.Name = ResolveWebDavSourceName(mediaSource.BaseUrl);
        }
        mediaSource.SetAddressConfiguration(BuildPendingAddressConfiguration(mediaSource.BaseUrl, mediaSource.ProtocolKind));
        return mediaSource;
    }

    private MediaSource? BuildPendingMediaServerSource()
    {
        string protocolType = NormalizeMediaServerProtocolType(PendingMediaServerProtocolType);
        MediaSourceProtocol mediaSourceProtocol = ResolveMediaServerProtocolKind(protocolType);
        string text = MediaSourceNormalizer.NormalizeBaseUrl(mediaSourceProtocol, PendingMediaServerBaseUrl.Trim());
        if (string.IsNullOrWhiteSpace(text))
        {
            MediaServerStatus = protocolType == "webdav" ? "请输入 WebDAV 目录地址。" : "请输入媒体服务器地址。";
            return null;
        }
        string authConfig = protocolType == "webdav"
            ? MediaSourceAuthConfigSerializer.SerializeWebDav(new WebDavAuthConfig(PendingMediaServerUserId.Trim(), PendingMediaServerToken))
            : MediaSourceAuthConfigSerializer.SerializeMediaServer(new MediaServerAuthConfig(PendingMediaServerToken, (mediaSourceProtocol == MediaSourceProtocol.Plex) ? string.Empty : PendingMediaServerUserId.Trim()));
        MediaSource mediaSource = new MediaSource
        {
            Name = PendingMediaServerName.Trim(),
            ProtocolType = protocolType,
            BaseUrl = text,
            AuthConfig = authConfig
        };
        LuckyStunSourceConfiguration luckyStunSourceConfiguration = BuildPendingLuckyStunConfiguration();
        if (luckyStunSourceConfiguration != null)
        {
            MediaSourceAddressConfiguration addressConfiguration = mediaSource.GetAddressConfiguration();
            addressConfiguration.LuckyStun = luckyStunSourceConfiguration;
            mediaSource.SetAddressConfiguration(addressConfiguration);
        }
        if (!mediaSource.IsValidConfiguration())
        {
            MediaServerStatus = "媒体服务器地址无效，请输入以 http:// 或 https:// 开头的地址。";
            return null;
        }
        if (protocolType != "webdav" && string.IsNullOrWhiteSpace(PendingMediaServerToken))
        {
            MediaServerStatus = ((mediaSourceProtocol == MediaSourceProtocol.Plex) ? "Plex 需要访问令牌（X-Plex-Token）才能读取媒体库。" : (mediaSource.ProtocolLabel + " 需要密码、API Key 或访问令牌才能读取媒体列表和生成播放地址。"));
            return null;
        }
        if (string.IsNullOrWhiteSpace(mediaSource.Name))
        {
            mediaSource.Name = ResolveMediaServerSourceName(mediaSource.ProtocolLabel, mediaSource.BaseUrl);
        }
        return mediaSource;
    }

    private Task<MediaServerPreflightResult> RunMediaServerPreflightAsync(MediaSource source, CancellationToken cancellationToken)
    {
        return (Task<MediaServerPreflightResult>)((mediaServerPreflightService == null) ? ((Task)Task.FromResult(new MediaServerPreflightResult(Success: false, "当前版本未启用媒体服务器预扫描。"))) : ((Task)mediaServerPreflightService.PreScanAsync(source, cancellationToken)));
    }

    private static string NormalizeMediaServerProtocolType(string protocolType)
    {
        string text = protocolType.Trim().ToLowerInvariant();
        if (1 == 0)
        {
        }
        string result = text switch
        {
            "emby" => "emby",
            "jellyfin" => "jellyfin",
            "webdav" => "webdav",
            _ => "plex"
        };
        if (1 == 0)
        {
        }
        return result;
    }

    private static MediaSourceProtocol ResolveMediaServerProtocolKind(string protocolType)
    {
        string text = NormalizeMediaServerProtocolType(protocolType);
        if (1 == 0)
        {
        }
        MediaSourceProtocol result = text switch
        {
            "emby" => MediaSourceProtocol.Emby,
            "jellyfin" => MediaSourceProtocol.Jellyfin,
            "webdav" => MediaSourceProtocol.WebDav,
            _ => MediaSourceProtocol.Plex
        };
        if (1 == 0)
        {
        }
        return result;
    }

    private static string DefaultMediaServerBaseUrl(string protocolType)
    {
        string text = NormalizeMediaServerProtocolType(protocolType);
        if (1 == 0)
        {
        }
        string result = text switch
        {
            "emby" => "http://127.0.0.1:8096",
            "jellyfin" => "http://127.0.0.1:8096",
            _ => "http://127.0.0.1:32400"
        };
        if (text == "webdav")
        {
            result = string.Empty;
        }
        if (1 == 0)
        {
        }
        return result;
    }

    private static bool IsKnownMediaServerDefaultBaseUrl(string value)
    {
        string a = value.Trim();
        return string.Equals(a, "http://127.0.0.1:32400", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "http://localhost:32400", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "http://127.0.0.1:8096", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "http://localhost:8096", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetMediaServerConnectionHint(string protocolType)
    {
        string text = NormalizeMediaServerProtocolType(protocolType);
        if (1 == 0)
        {
        }
        string result = text switch
        {
            "plex" => "Plex 默认地址为 http://127.0.0.1:32400，点击“登录 Plex”会自动获取访问令牌。",
            "emby" => "Emby 默认地址为 http://127.0.0.1:8096，可填写用户名和密码，也可直接填写 API Key 或访问令牌。",
            "jellyfin" => "Jellyfin 默认地址为 http://127.0.0.1:8096，可填写用户名和密码，也可直接填写 API Key 或访问令牌。",
            "webdav" => "WebDAV 保存后直接扫描该地址下的媒体文件，用户名和密码可选。",
            _ => "保存后会先预扫描媒体库列表；可给多个库点星标，关闭列表时统一挂载并扫描刮削。",
        };
        if (1 == 0)
        {
        }
        return result;
    }

    private void ApplyMediaServerProtocolDefaults(string protocolType)
    {
        string value = PendingMediaServerBaseUrl.Trim();
        if (string.IsNullOrWhiteSpace(value) || IsKnownMediaServerDefaultBaseUrl(value))
        {
            PendingMediaServerBaseUrl = DefaultMediaServerBaseUrl(protocolType);
        }
        if (NormalizeMediaServerProtocolType(protocolType) == "plex")
        {
            PendingMediaServerUserId = string.Empty;
        }
        if (NormalizeMediaServerProtocolType(protocolType) is "webdav" or "emby" or "jellyfin" or "plex")
        {
            NetworkCredentialEntry? savedCredential = networkCredentialStore.FindBest(ResolveMediaServerProtocolKind(protocolType), PendingMediaServerBaseUrl.Trim());
            if (savedCredential == null)
            {
                NetworkCredentialEntry? latestCredential = networkCredentialStore.FindLatest();
                if (latestCredential != null && string.Equals(latestCredential.ProtocolType, NormalizeMediaServerProtocolType(protocolType), StringComparison.OrdinalIgnoreCase))
                {
                    savedCredential = latestCredential;
                }
            }
            if (savedCredential != null)
            {
                if (string.IsNullOrWhiteSpace(PendingMediaServerUserId))
                {
                    PendingMediaServerUserId = savedCredential.Username;
                }
                if (string.IsNullOrWhiteSpace(PendingMediaServerToken))
                {
                    PendingMediaServerToken = savedCredential.Password;
                }
            }
        }
        NotifyMediaServerProtocolDependentProperties();
    }

    private void NotifyMediaServerProtocolDependentProperties()
    {
        OnPropertyChanged("PendingMediaServerAddressWatermark");
        OnPropertyChanged("PendingMediaServerTokenWatermark");
        OnPropertyChanged("PendingMediaServerUserWatermark");
        OnPropertyChanged("PendingMediaServerUsesUserId");
        OnPropertyChanged("PendingMediaServerUsesPlex");
        OnPropertyChanged("PlexAuthorizeButtonText");
        AuthorizePlexCommand.NotifyCanExecuteChanged();
    }

    private static string ResolveMediaServerSourceName(string protocolLabel, string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri result) || string.IsNullOrWhiteSpace(result.Host))
        {
            return protocolLabel;
        }
        return protocolLabel + " · " + result.Host;
    }

    private static MediaSource CreateLocalSource(string selectedPath, long? sourceId = null)
    {
        string text = selectedPath.Trim();
        string text2 = Path.GetFileName(text.TrimEnd(new char[2]
        {
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        }));
        if (string.IsNullOrWhiteSpace(text2))
        {
            text2 = text;
        }
        MediaSource mediaSource = new MediaSource
        {
            Id = sourceId,
            Name = text2,
            ProtocolType = "local",
            BaseUrl = text
        };
        mediaSource.SetAddressConfiguration(new MediaSourceAddressConfiguration
        {
            LocalAddress = text,
            LocalLabel = "局域网",
            ActiveAddress = text,
            ActiveLabel = "局域网"
        });
        return mediaSource;
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
        OnPropertyChanged("IsEditingWebDavSource");
        OnPropertyChanged("WebDavFormTitle");
        OnPropertyChanged("WebDavSubmitActionText");
    }

    private static string ResolveWebDavSourceName(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri result))
        {
            return "WebDAV";
        }
        string text = result.Segments.LastOrDefault()?.Trim('/');
        if (!string.IsNullOrWhiteSpace(text))
        {
            return Uri.UnescapeDataString(text);
        }
        return result.Host;
    }

    private async Task CheckSourceConnectivityAsync(MediaSource? source)
    {
        if (!(source?.Id).HasValue)
        {
            return;
        }
        IsBusy = true;
        OnCommandStateChanged();
        try
        {
            StatusMessage = await CheckSourceConnectivityCoreAsync(source);
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private async Task<string> CheckSourceConnectivityCoreAsync(MediaSource source)
    {
        MediaSourceAddressConfiguration configuration = source.GetAddressConfiguration();
        List<(string Address, string Label)> candidates = new List<(string, string)>();
        AddConnectivityCandidate(candidates, source.BaseUrl, string.IsNullOrWhiteSpace(configuration.ActiveLabel) ? "当前地址" : configuration.ActiveLabel.Trim());
        AddConnectivityCandidate(candidates, configuration.LocalAddress, string.IsNullOrWhiteSpace(configuration.LocalLabel) ? "局域网" : configuration.LocalLabel.Trim());
        foreach (var item in configuration.ExternalAddresses)
        {
            AddConnectivityCandidate(candidates, item.Address, item.DisplayLabel);
        }
        foreach (var candidate in candidates)
        {
            if (await IsSourceAddressReachableAsync(source, candidate.Address))
            {
                (configuration.ActiveAddress, configuration.ActiveLabel) = candidate;
                (source.BaseUrl, _) = candidate;
                source.SetAddressConfiguration(configuration);
                bool updated = await mediaSourceRepository.UpdateAsync(source);
                await ReloadLibraryAsync();
                return (updated ? $"媒体源“{source.Name}”已切换到{candidate.Label}入口。" : $"媒体源“{source.Name}”已检测到可用入口：{candidate.Label}。");
            }
        }
        return "媒体源“" + source.Name + "”的局域网和外网入口均无法连接。";
    }

    private static void AddConnectivityCandidate(List<(string Address, string Label)> candidates, string address, string label)
    {
        string trimmed = address?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return;
        }
        for (int i = 0; i < candidates.Count; i++)
        {
            if (string.Equals(candidates[i].Address, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        candidates.Add((trimmed, string.IsNullOrWhiteSpace(label) ? "局域网" : label.Trim()));
    }

    private async Task<bool> IsSourceAddressReachableAsync(MediaSource source, string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }
        MediaSource probe = new MediaSource
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
            return (await RunMediaServerPreflightAsync(probe, CancellationToken.None)).Success;
        case MediaSourceProtocol.Direct:
            return await Task.Run(() => File.Exists(address) || Directory.Exists(address));
        default:
            return false;
        }
    }

    private async Task<string> TestSourceAddressMessageAsync(MediaSource source, string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return "地址为空，已跳过连接测试。";
        }
        try
        {
            MediaSource probe = new MediaSource
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
                return (await Task.Run(() => Directory.Exists(address))) ? "地址连接测试成功。" : "地址连接测试失败：目录不存在。";
            case MediaSourceProtocol.WebDav:
                WebDavConnectionTestResult webDavResult = await webDavConnectionTester.TestConnectionAsync(probe);
                return webDavResult.Success ? "地址连接测试成功。" : ("地址连接测试失败：" + webDavResult.Message);
            case MediaSourceProtocol.Plex:
            case MediaSourceProtocol.Emby:
            case MediaSourceProtocol.Jellyfin:
                MediaServerPreflightResult preflightResult = await RunMediaServerPreflightAsync(probe, CancellationToken.None);
                return preflightResult.Success ? "地址连接测试成功。" : ("地址连接测试失败：" + preflightResult.Message);
            case MediaSourceProtocol.Direct:
                return (await Task.Run(() => File.Exists(address) || Directory.Exists(address))) ? "地址连接测试成功。" : "地址连接测试失败：地址不存在。";
            default:
                return "媒体源协议不受支持，已跳过连接测试。";
            }
        }
        catch (Exception ex)
        {
            return "地址连接测试失败：" + ex.Message;
        }
    }

    private async Task ToggleSourceEnabledAsync(MediaSource? source)
    {
        if (!(source?.Id).HasValue)
        {
            return;
        }
        bool shouldEnable = !source.IsEnabled;
        if (!shouldEnable)
        {
            CancelActiveLibraryAutomation();
        }
        IsBusy = true;
        OnCommandStateChanged();
        // 乐观更新行状态，点击后立即给用户视觉反馈
        var optimisticIndex = MediaSources.IndexOf(source);
        if (optimisticIndex >= 0)
        {
            var optimistic = source.Clone();
            optimistic.IsEnabled = shouldEnable;
            optimistic.DisabledAt = shouldEnable ? null : DateTimeOffset.UtcNow.ToString("o");
            MediaSources[optimisticIndex] = optimistic;
        }
        try
        {
            if (!(await mediaSourceRepository.SetEnabledAsync(source.Id.Value, shouldEnable, DateTimeOffset.UtcNow)))
            {
                StatusMessage = "未找到媒体源：" + source.Name;
                return;
            }
            await ReloadLibraryAsync();
            if (currentDetailMovieId.HasValue || currentDetailTvShowId.HasValue)
            {
                await RefreshDetailFilesAsync();
            }
            if (!shouldEnable)
            {
                StatusMessage = "已关闭媒体源：" + source.Name + "。首页已隐藏对应海报，扫描和刮削数据保留 30 天。";
                ShowToast("已关闭媒体源：" + source.Name, ToastKind.Success);
                return;
            }
            StatusMessage = "已开启媒体源：" + source.Name + "，正在扫描并刮削...";
            ShowToast("已开启媒体源：" + source.Name + "，正在扫描并刮削", ToastKind.Success);
            CancellationTokenSource automationCancellationTokenSource = BeginLibraryAutomation();
            CancellationToken cancellationToken = automationCancellationTokenSource.Token;
            IsBusy = false;
            OnCommandStateChanged();
            try
            {
                LastScanSummary = await RunLibraryScanAndArtworkAsync(isExplicitRequest: true, forceThumbnails: true, cancellationToken, new long[1] { source.Id.Value });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                StatusMessage = "已停止媒体源的扫描和刮削任务。";
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[OmniPlay] 扫描媒体源失败：" + ex.Message);
                StatusMessage = "已开启媒体源：" + source.Name + "，但扫描失败：" + ex.Message;
                return;
            }
            finally
            {
                CompleteLibraryAutomation(automationCancellationTokenSource);
            }
            StatusMessage = (string.IsNullOrWhiteSpace(StatusMessage) ? ("已开启并扫描媒体源：" + source.Name) : ("已开启媒体源：" + source.Name + "。" + StatusMessage));
        }
        finally
        {
            IsBusy = false;
            OnCommandStateChanged();
        }
    }

    private async Task RemoveSourceAsync(MediaSource? source)
    {
        if (!(source?.Id).HasValue)
        {
            return;
        }
        CancelActiveLibraryAutomation();
        OnCommandStateChanged();
        try
        {
            bool removed = await mediaSourceRepository.SoftRemoveAsync(source.Id.Value, DateTimeOffset.UtcNow);
            if (removed && EditingWebDavSourceId == source.Id.Value)
            {
                ResetWebDavForm();
            }
            await ReloadLibraryAsync();
            if (currentDetailMovieId.HasValue || currentDetailTvShowId.HasValue)
            {
                await RefreshDetailFilesAsync();
            }
            if (removed)
            {
                ShowToast("已移除媒体源：" + source.Name, ToastKind.Success);
            }
            StatusMessage = (removed ? ("已移除媒体源：" + source.Name + "。已停止对应扫描、刮削和分集剧照刮削任务；首页已隐藏对应海报，扫描和刮削数据保留 30 天；重新挂载时会优先预填已保存的 SMB/WebDAV 登录信息。") : ("未找到要移除的媒体源：" + source.Name));
        }
        finally
        {
            OnCommandStateChanged();
        }
    }

    private void OpenRemoveSourceConfirmation(MediaSource? source)
    {
        if (!(source?.Id).HasValue)
        {
            return;
        }
        pendingRemoveSource = source;
        RemoveSourceConfirmationText = source.Name;
        OnPropertyChanged(nameof(RemoveSourceConfirmationBody));
        IsSourcePopupOpen = false;
        IsRemoveSourceConfirmationOpen = true;
    }

    private void CancelRemoveSourceConfirmation()
    {
        IsRemoveSourceConfirmationOpen = false;
        pendingRemoveSource = null;
    }

    private async Task ConfirmRemoveSourceAsync()
    {
        MediaSource? source = pendingRemoveSource;
        IsRemoveSourceConfirmationOpen = false;
        pendingRemoveSource = null;
        if (!(source?.Id).HasValue)
        {
            return;
        }
        await RemoveSourceAsync(source);
    }

    private static void SynchronizeLibraryPosterItems(ObservableCollection<LibraryPosterItem> target, IReadOnlyList<LibraryPosterItem> source)
    {
        for (int i = 0; i < source.Count; i++)
        {
            LibraryPosterItem libraryPosterItem = source[i];
            int num = IndexOfPosterItem(target, libraryPosterItem.Id);
            if (num < 0)
            {
                target.Insert(i, libraryPosterItem);
                continue;
            }
            if (num != i)
            {
                target.Move(num, i);
            }
            if (!LibraryPosterItemsEqual(target[i], libraryPosterItem))
            {
                target[i] = libraryPosterItem;
            }
        }
        while (target.Count > source.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private static int IndexOfPosterItem(IReadOnlyList<LibraryPosterItem> items, string id)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private static bool LibraryPosterItemsEqual(LibraryPosterItem left, LibraryPosterItem right)
    {
        return string.Equals(left.Id, right.Id, StringComparison.Ordinal) && string.Equals(left.Title, right.Title, StringComparison.Ordinal) && string.Equals(left.Subtitle, right.Subtitle, StringComparison.Ordinal) && string.Equals(left.PosterPath, right.PosterPath, StringComparison.Ordinal) && left.VoteAverage == right.VoteAverage && left.DoubanRating == right.DoubanRating && string.Equals(left.MediaKind, right.MediaKind, StringComparison.Ordinal) && left.ContinueWatchingProgress == right.ContinueWatchingProgress && left.LastPlayedAt == right.LastPlayedAt && string.Equals(left.ContinueWatchingLabel, right.ContinueWatchingLabel, StringComparison.Ordinal) && left.IsContinuing == right.IsContinuing && left.WatchState == right.WatchState && left.OfflineCacheIsDownloading == right.OfflineCacheIsDownloading && left.OfflineCacheIsCached == right.OfflineCacheIsCached && left.OfflineCacheUnavailable == right.OfflineCacheUnavailable && left.OfflineCacheProgress == right.OfflineCacheProgress && left.MovieId == right.MovieId && left.TvShowId == right.TvShowId;
    }

    private static void ReplaceItems<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        if (target.SequenceEqual(source))
        {
            return;
        }
        target.Clear();
        foreach (T item in source)
        {
            target.Add(item);
        }
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "IsPlaybackCompleted")
        {
            if (Player.IsPlaybackCompleted)
            {
                _ = TryAutoAdvanceToNextMoviePartAsync();
                _ = TryAutoAdvanceToNextEpisodeAsync();
            }
            NotifyPlayerTimelineChanged();
            return;
        }
        bool flag;
        switch (e.PropertyName)
        {
        case "CurrentPositionSeconds":
        case "DurationSeconds":
        case "SeekPositionSeconds":
        case "IsPlaying":
        case "IsPaused":
            flag = true;
            break;
        default:
            flag = false;
            break;
        }
        if (flag)
        {
            NotifyPlayerTimelineChanged();
        }
        if (!(e.PropertyName != "IsPlaying"))
        {
            if (Player.IsPlaying && HasActivePlaybackSession())
            {
                StartPlaybackProgressSync();
            }
            else
            {
                StopPlaybackProgressSync();
            }
        }
    }

    private async Task TryAutoAdvanceToNextEpisodeAsync()
    {
        if (isAutoAdvancingToNextEpisode || !HasActivePlaybackSession() || string.IsNullOrWhiteSpace(currentPlayingVideoId) || string.Equals(autoAdvancedPlaybackVideoId, currentPlayingVideoId, StringComparison.Ordinal))
        {
            return;
        }
        LibraryVideoItem currentEpisode = ResolveCurrentPlaybackEpisode();
        if (currentEpisode == null || FindNextEpisode(currentEpisode) == null)
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
        if (isAutoAdvancingToNextMoviePart || !HasActivePlaybackSession() || string.IsNullOrWhiteSpace(currentPlayingVideoId) || string.Equals(autoAdvancedPlaybackVideoId, currentPlayingVideoId, StringComparison.Ordinal))
        {
            return;
        }
        LibraryVideoItem currentPart = ResolveCurrentPlaybackMoviePart();
        if (currentPart == null || FindNextMoviePart(currentPart) == null)
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

    private async Task HandleStandalonePlaybackClosedAsync(string videoId, string fileName, PlaybackCloseResult result)
    {
        StopPlaybackProgressSync();
        bool shouldPersistPlaybackState = !ShouldSuppressPlaybackPersistence(videoId);
        if (shouldPersistPlaybackState)
        {
            await PersistPlaybackStateAsync(videoId, result.PositionSeconds, result.DurationSeconds);
        }
        if (currentPlaybackMode == PlaybackMode.Standalone && string.Equals(currentPlayingVideoId, videoId, StringComparison.Ordinal))
        {
            currentPlaybackMode = PlaybackMode.None;
            currentPlayingVideoId = string.Empty;
            selectedPlaybackNavigationSeason = null;
            ResetAutoAdvanceState();
            UpdatePlayerNextEpisodeAction();
            if (shouldPersistPlaybackState)
            {
                StatusMessage = "已记录独立窗口播放进度：" + fileName;
            }
            OnCommandStateChanged();
        }
    }

    private async Task PersistPlaybackStateAsync(string videoId, double progress, double duration, bool refreshCollections = true)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return;
        }
        double normalizedProgress = NormalizePersistedProgress(progress, duration);
        await videoFileRepository.UpdatePlaybackStateAsync(videoId, normalizedProgress, duration);
        preferredDetailVideoId = videoId;
        if (refreshCollections)
        {
            if (IsDetailOpen && (currentDetailMovieId.HasValue || currentDetailTvShowId.HasValue))
            {
                await RefreshDetailFilesAsync();
            }
            await RefreshContinueWatchingAsync();
        }
    }

    private static bool ShouldShowHomeStatusMessage(string? statusMessage)
    {
        if (string.IsNullOrWhiteSpace(statusMessage))
        {
            return false;
        }
        string text = statusMessage.Trim();
        // 扫描/刮削进行中和结果（完成、失败、停止、异常）都需要在首页状态栏可见。
        return text.Contains("扫描", StringComparison.Ordinal) || text.Contains("刮削", StringComparison.Ordinal) || text.Contains("分集剧照", StringComparison.Ordinal);
    }

    private void ApplyFilters()
    {
        IEnumerable<LibraryPosterItem> enumerable = allLibraryItems;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            enumerable = enumerable.Where((LibraryPosterItem x) => x.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrEmpty(OpenFolderKey))
        {
            (string openMode, string openName) = SplitFolderKey(OpenFolderKey);
            enumerable = enumerable.Where((LibraryPosterItem x) => !x.IsFolder && MatchesHomeFolder(x, openMode, openName));
        }
        List<LibraryPosterItem> sortedItems = SortLibraryItems(enumerable).Select(ApplyOfflineCacheState).ToList();
        SynchronizeLibraryPosterItems(LibraryItems, ApplyHomeCategorization(ArrangeLibraryItemsForPresentation(sortedItems)));
        NotifyLibraryViewFlags();
    }

    private IReadOnlyList<LibraryPosterItem> ArrangeLibraryItemsForPresentation(IReadOnlyList<LibraryPosterItem> sortedItems)
    {
        UpdateLibraryPosterTracking();
        if (newlyDiscoveredPosterOrder.Count == 0)
        {
            return sortedItems;
        }
        List<LibraryPosterItem> list = (from item in sortedItems
            where newlyDiscoveredPosterOrder.ContainsKey(item.Id)
            orderby newlyDiscoveredPosterOrder[item.Id] descending
            select item).ToList();
        if (list.Count == 0)
        {
            return sortedItems;
        }
        return list.Concat(sortedItems.Where((LibraryPosterItem item) => !newlyDiscoveredPosterOrder.ContainsKey(item.Id))).ToList();
    }

    private void UpdateLibraryPosterTracking()
    {
        HashSet<string> hashSet = allLibraryItems.Select((LibraryPosterItem item) => item.Id).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!libraryPosterTrackingInitialized)
        {
            knownLibraryPosterIds.Clear();
            knownLibraryPosterIds.UnionWith(hashSet);
            libraryPosterTrackingInitialized = true;
            return;
        }
        foreach (string item in knownLibraryPosterIds.Except(hashSet).ToList())
        {
            knownLibraryPosterIds.Remove(item);
            newlyDiscoveredPosterOrder.Remove(item);
        }
        foreach (LibraryPosterItem allLibraryItem in allLibraryItems)
        {
            if (knownLibraryPosterIds.Add(allLibraryItem.Id) && IsLibraryScanInProgress)
            {
                newlyDiscoveredPosterOrder[allLibraryItem.Id] = ++nextNewlyDiscoveredPosterOrder;
            }
        }
        if (!IsLibraryScanInProgress)
        {
            newlyDiscoveredPosterOrder.Clear();
            return;
        }
        foreach (LibraryPosterItem allLibraryItem2 in allLibraryItems)
        {
            if (newlyDiscoveredPosterOrder.ContainsKey(allLibraryItem2.Id) && HasSettledPosterMetadata(allLibraryItem2))
            {
                newlyDiscoveredPosterOrder.Remove(allLibraryItem2.Id);
            }
        }
    }

    private static bool HasSettledPosterMetadata(LibraryPosterItem item)
    {
        return !string.IsNullOrWhiteSpace(item.PosterPath) || item.VoteAverage.HasValue || SortYearValue(item).HasValue;
    }

    private IEnumerable<LibraryPosterItem> SortLibraryItems(IEnumerable<LibraryPosterItem> items)
    {
        LibrarySortOption librarySortOption = SelectedSortOption;
        if (1 == 0)
        {
        }
        IOrderedEnumerable<LibraryPosterItem> result = librarySortOption switch
        {
            LibrarySortOption.Year => IsSortDescending ? (from item in items
                orderby SortYearMissingRank(item), SortYearValue(item) descending
                select item).ThenBy<LibraryPosterItem, string>((LibraryPosterItem item) => item.Title, StringComparer.OrdinalIgnoreCase) : (from item in items
                orderby SortYearMissingRank(item), SortYearValue(item)
                select item).ThenBy<LibraryPosterItem, string>((LibraryPosterItem item) => item.Title, StringComparer.OrdinalIgnoreCase), 
            LibrarySortOption.Rating => IsSortDescending ? (from item in items
                orderby SortRatingMissingRank(item), item.VoteAverage descending
                select item).ThenBy<LibraryPosterItem, string>((LibraryPosterItem item) => item.Title, StringComparer.OrdinalIgnoreCase) : (from item in items
                orderby SortRatingMissingRank(item), item.VoteAverage
                select item).ThenBy<LibraryPosterItem, string>((LibraryPosterItem item) => item.Title, StringComparer.OrdinalIgnoreCase), 
            _ => IsSortDescending ? items.OrderByDescending<LibraryPosterItem, string>((LibraryPosterItem item) => item.Title, StringComparer.OrdinalIgnoreCase) : items.OrderBy<LibraryPosterItem, string>((LibraryPosterItem item) => item.Title, StringComparer.OrdinalIgnoreCase), 
        };
        if (1 == 0)
        {
        }
        return result;
    }

    private static int SortYearMissingRank(LibraryPosterItem item)
    {
        return (!SortYearValue(item).HasValue) ? 1 : 0;
    }

    private static int? SortYearValue(LibraryPosterItem item)
    {
        int result;
        return int.TryParse(FormatMediaYear(item.Subtitle), out result) ? new int?(result) : ((int?)null);
    }

    private static int SortRatingMissingRank(LibraryPosterItem item)
    {
        return (!item.VoteAverage.HasValue) ? 1 : 0;
    }

    private async Task RefreshDetailFilesAsync(int? preservedSelectedSeason = null)
    {
        IReadOnlyList<LibraryVideoItem> files = Array.Empty<LibraryVideoItem>();
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
        ReplaceItems(source: (await videoFileRepository.GetContinueWatchingAsync()).Select(ApplyOfflineCacheState).ToList(), target: ContinueWatchingItems);
        OnPropertyChanged("HasContinueWatching");
        OnPropertyChanged("ShowContinueWatching");
        await RefreshLibraryPlaybackStatesAsync();
    }

    private async Task RefreshLibraryPlaybackStatesAsync()
    {
        if (allLibraryItems.Count == 0)
        {
            return;
        }
        IReadOnlyDictionary<string, PlaybackWatchState> playbackStates = await videoFileRepository.GetLibraryPlaybackStatesAsync();
        HashSet<string> continuingIds = ContinueWatchingItems.Select((LibraryPosterItem libraryPosterItem) => libraryPosterItem.Id).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool libraryStateChanged = false;
        for (int index = 0; index < allLibraryItems.Count; index++)
        {
            LibraryPosterItem item = allLibraryItems[index];
            PlaybackWatchState watchState = ResolvePosterWatchState(playbackStates, item.Id);
            bool isContinuing = continuingIds.Contains(item.Id);
            if (item.WatchState != watchState || item.IsContinuing != isContinuing)
            {
                libraryStateChanged = true;
                allLibraryItems[index] = CopyPosterItem(item, watchState, isContinuing);
            }
        }
        if (libraryStateChanged)
        {
            ApplyFilters();
        }
    }

    private static PlaybackWatchState ResolvePosterWatchState(IReadOnlyDictionary<string, PlaybackWatchState> playbackStates, string itemId)
    {
        PlaybackWatchState value;
        return playbackStates.TryGetValue(itemId, out value) ? value : PlaybackWatchState.Unwatched;
    }

    private static PlaybackWatchState ResolvePosterWatchState(IReadOnlyDictionary<string, PlaybackWatchState> playbackStates, IReadOnlyList<string> itemIds)
    {
        bool flag = false;
        foreach (string itemId in itemIds)
        {
            if (playbackStates.TryGetValue(itemId, out var value))
            {
                if (value == PlaybackWatchState.InProgress)
                {
                    return PlaybackWatchState.InProgress;
                }
                flag |= value == PlaybackWatchState.Watched;
            }
        }
        return flag ? PlaybackWatchState.Watched : PlaybackWatchState.Unwatched;
    }

    private static IReadOnlyList<TvShowHomeGroup> GroupTvShowsForHome(IReadOnlyList<TvShow> shows)
    {
        return shows.GroupBy<TvShow, string>((TvShow show) => NormalizeTvShowHomeGroupKey(show.Title), StringComparer.OrdinalIgnoreCase).Select(delegate(IGrouping<string, TvShow> group)
        {
            List<TvShow> shows2 = group.ToList();
            return new TvShowHomeGroup(SelectRepresentativeTvShow(shows2), shows2);
        }).OrderBy<TvShowHomeGroup, string>((TvShowHomeGroup group) => group.Representative.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static TvShow SelectRepresentativeTvShow(IReadOnlyList<TvShow> shows)
    {
        return (from show in shows
            orderby show.IsLocked descending, !string.IsNullOrWhiteSpace(show.PosterPath) descending, !string.IsNullOrWhiteSpace(show.Overview) descending, show.VoteAverage.HasValue descending, show.Id
            select show).First();
    }

    private static string NormalizeTvShowHomeGroupKey(string? title)
    {
        string text = Regex.Replace(title ?? string.Empty, "\\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(text) ? Guid.NewGuid().ToString("N") : text;
    }

    private static LibraryPosterItem CopyPosterItem(LibraryPosterItem item, PlaybackWatchState watchState, bool isContinuing, bool? offlineCacheIsDownloading = null, bool? offlineCacheIsCached = null, bool? offlineCacheUnavailable = null, double? offlineCacheProgress = null)
    {
        return new LibraryPosterItem
        {
            Id = item.Id,
            Title = item.Title,
            Subtitle = item.Subtitle,
            PosterPath = item.PosterPath,
            VoteAverage = item.VoteAverage,
            DoubanRating = item.DoubanRating,
            Director = item.Director,
            MainCast = item.MainCast,
            MediaKind = item.MediaKind,
            ContinueWatchingProgress = item.ContinueWatchingProgress,
            LastPlayedAt = item.LastPlayedAt,
            ContinueWatchingLabel = item.ContinueWatchingLabel,
            IsContinuing = isContinuing,
            WatchState = watchState,
            OfflineCacheIsDownloading = (offlineCacheIsDownloading ?? item.OfflineCacheIsDownloading),
            OfflineCacheIsCached = (offlineCacheIsCached ?? item.OfflineCacheIsCached),
            OfflineCacheUnavailable = (offlineCacheUnavailable ?? item.OfflineCacheUnavailable),
            OfflineCacheProgress = (offlineCacheProgress ?? item.OfflineCacheProgress),
            MovieId = item.MovieId,
            TvShowId = item.TvShowId,
            IsFolder = item.IsFolder,
            FolderKey = item.FolderKey
        };
    }

    private static IReadOnlyList<LibraryVideoItem> ApplyEpisodeDisplayDisambiguation(IReadOnlyList<LibraryVideoItem> files)
    {
        List<IGrouping<(int, int), LibraryVideoItem>> source = (from file in files
            where file.IsTvEpisode
            group file by (SeasonNumber: file.SeasonNumber, EpisodeNumber: file.EpisodeNumber) into @group
            where @group.Count() > 1
            select @group).ToList();
        HashSet<(int SeasonNumber, int EpisodeNumber)> duplicateEpisodeKeys = source.Select((IGrouping<(int SeasonNumber, int EpisodeNumber), LibraryVideoItem> group) => group.Key).ToHashSet();
        Dictionary<string, string> prefixSuffixesByFileId = source.SelectMany((IGrouping<(int SeasonNumber, int EpisodeNumber), LibraryVideoItem> group) => BuildEpisodePrefixDisambiguationSuffixes(group.Where((LibraryVideoItem file) => string.IsNullOrWhiteSpace(file.CustomEpisodeSubtitle)).ToList())).ToDictionary<KeyValuePair<string, string>, string, string>((KeyValuePair<string, string> pair) => pair.Key, (KeyValuePair<string, string> pair) => pair.Value, StringComparer.Ordinal);
        return files.Select(delegate(LibraryVideoItem file)
        {
            if (!file.IsTvEpisode || !string.IsNullOrWhiteSpace(file.CustomEpisodeSubtitle))
            {
                return file;
            }
            string value = null;
            if (duplicateEpisodeKeys.Contains((file.SeasonNumber, file.EpisodeNumber)))
            {
                prefixSuffixesByFileId.TryGetValue(file.Id, out value);
                if (string.IsNullOrWhiteSpace(value))
                {
                    value = Path.GetFileNameWithoutExtension(file.FileName);
                }
            }
            return (duplicateEpisodeKeys.Contains((file.SeasonNumber, file.EpisodeNumber)) && !string.IsNullOrWhiteSpace(value)) ? CopyVideoItemWithEpisodeSubtitle(file, value) : CopyVideoItemWithEpisodeSubtitle(file, null);
        }).ToList();
    }

    private static IReadOnlyDictionary<string, string> BuildEpisodePrefixDisambiguationSuffixes(IReadOnlyList<LibraryVideoItem> files)
    {
        if (files.Count <= 1)
        {
            return new Dictionary<string, string>();
        }
        var list = files.Select((LibraryVideoItem file) => new
        {
            Id = file.Id,
            Tokens = EpisodePrefixTokens(file.FileName)
        }).ToList();
        if (list.All(item => item.Tokens.Count == 0))
        {
            return new Dictionary<string, string>();
        }
        List<List<string>> tokenSets = list.Select(item => item.Tokens).ToList();
        int num = CommonPrefixTokenCount(tokenSets);
        int num2 = CommonSuffixTokenCount(tokenSets, num);
        Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in list)
        {
            int num3 = Math.Max(num, item.Tokens.Count - num2);
            if (num < num3)
            {
                List<string> tokens = item.Tokens.Skip(num).Take(num3 - num).ToList();
                string value = PreferredEpisodeDifferenceTitle(tokens);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    dictionary[item.Id] = value;
                }
            }
        }
        return dictionary;
    }

    private static List<string> EpisodePrefixTokens(string fileName)
    {
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
        {
            return new List<string>();
        }
        Match match = Regex.Match(fileNameWithoutExtension, "[sS]\\d{1,2}[eE][pP]?\\d{1,3}|[eE][pP]?\\d{1,3}|第\\s*\\d{1,3}\\s*[集话]");
        if (!match.Success)
        {
            return new List<string>();
        }
        string input = fileNameWithoutExtension.Substring(0, match.Index);
        string input2 = Regex.Replace(input, "[\\[\\]\\(\\)\\{\\}【】（）《》「」『』〔〕〖〗,:：]+", " ");
        input2 = Regex.Replace(input2, "[._\\-–—]+", " ");
        input2 = Regex.Replace(input2, "\\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(input2))
        {
            return new List<string>();
        }
        return (from token in input2.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            where !IsEpisodePrefixNoiseToken(token)
            select token).ToList();
    }

    private static int CommonPrefixTokenCount(IReadOnlyList<List<string>> tokenSets)
    {
        if (tokenSets.Count == 0 || tokenSets[0].Count == 0)
        {
            return 0;
        }
        int count = 0;
        while (count < tokenSets[0].Count)
        {
            string token = NormalizeEpisodeDifferenceToken(tokenSets[0][count]);
            if (tokenSets.Any((List<string> tokens) => tokens.Count <= count || NormalizeEpisodeDifferenceToken(tokens[count]) != token))
            {
                break;
            }
            int num = count;
            count = num + 1;
        }
        return count;
    }

    private static int CommonSuffixTokenCount(IReadOnlyList<List<string>> tokenSets, int prefixCount)
    {
        int num = 0;
        while (true)
        {
            string text = null;
            foreach (List<string> tokenSet in tokenSets)
            {
                int num2 = tokenSet.Count - num - 1;
                if (num2 < prefixCount)
                {
                    return num;
                }
                string text2 = NormalizeEpisodeDifferenceToken(tokenSet[num2]);
                if (text != null && text != text2)
                {
                    return num;
                }
                if (text == null)
                {
                    text = text2;
                }
            }
            num++;
        }
    }

    private static string? PreferredEpisodeDifferenceTitle(IReadOnlyList<string> tokens)
    {
        List<string> list = tokens.Where((string token) => Regex.IsMatch(token, "\\p{IsCJKUnifiedIdeographs}")).ToList();
        if (list.Count > 0)
        {
            string text = string.Concat(list).Trim();
            return (text.Length == 0) ? null : text;
        }
        List<string> values = tokens.Where((string token) => Regex.IsMatch(token, "[A-Za-z]") && !IsEpisodePrefixNoiseToken(token)).ToList();
        string text2 = string.Join(' ', values).Trim();
        return (text2.Length == 0) ? null : text2;
    }

    private static string NormalizeEpisodeDifferenceToken(string token)
    {
        return token.Trim().ToLowerInvariant();
    }

    private static bool IsEpisodePrefixNoiseToken(string token)
    {
        string text = token.Trim().ToLowerInvariant();
        if (text.Length == 0)
        {
            return true;
        }
        if (Regex.IsMatch(text, "^(19|20)\\d{2}$"))
        {
            return true;
        }
        if (Regex.IsMatch(text, "^\\d+$"))
        {
            return true;
        }
        if (Regex.IsMatch(text, "^\\d+(\\.\\d+)?$"))
        {
            return true;
        }
        if (Regex.IsMatch(text, "^(\\d{3,4}p|[48]k|uhd|fhd|hd|sd)$"))
        {
            return true;
        }
        if (Regex.IsMatch(text, "^(8|10|12)bit$"))
        {
            return true;
        }
        if (Regex.IsMatch(text, "^(x|h)?26[45]$"))
        {
            return true;
        }
        if (Regex.IsMatch(text, "^h\\.?(264|265)$"))
        {
            return true;
        }
        if (Regex.IsMatch(text, "^(aac|ac3|eac3|ddp|dts|truehd|lpcm|flac|mp3|opus)\\d*(\\.\\d+)?$"))
        {
            return true;
        }
        HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "blu", "ray", "bluray", "bdrip", "web", "dl", "webdl", "webrip", "hdtv", "uhdtv",
            "remux", "avc", "hevc", "hdr", "dv", "dovi", "sdr", "hdr10", "hdr10plus", "atmos",
            "nf", "netflix", "amzn", "amazon", "dsnp", "disney", "hulu", "atvp", "max", "proper",
            "repack", "internal"
        };
        return hashSet.Contains(text);
    }

    private static LibraryVideoItem CopyVideoItemWithEpisodeSubtitle(LibraryVideoItem file, string? episodeSubtitle)
    {
        return CopyVideoItem(file, file.OfflineCachePath, file.OfflineCacheIsDownloading, file.OfflineCacheIsCached, file.OfflineCacheUnavailable, file.OfflineCacheProgress, episodeSubtitle, overrideEpisodeSubtitle: true);
    }

    private static LibraryVideoItem CopyVideoItem(LibraryVideoItem file, string? offlineCachePath, bool offlineCacheIsDownloading, bool offlineCacheIsCached, bool offlineCacheUnavailable, double offlineCacheProgress, string? episodeSubtitle = null, bool overrideEpisodeSubtitle = false)
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
            EpisodeSubtitle = (overrideEpisodeSubtitle ? episodeSubtitle : file.EpisodeSubtitle),
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
        string pathRoot = Path.GetPathRoot(Path.GetFullPath(directory));
        if (string.IsNullOrWhiteSpace(pathRoot))
        {
            return 0L;
        }
        return new DriveInfo(pathRoot).AvailableFreeSpace;
    }

    private static string FormatBytes(long bytes)
    {
        string[] array = new string[5] { "B", "KB", "MB", "GB", "TB" };
        long num = Math.Max(0L, bytes);
        int num2 = 0;
        double num3 = num;
        while (num3 >= 1024.0 && num2 < array.Length - 1)
        {
            num3 /= 1024.0;
            num2++;
        }
        return $"{num3:0.##} {array[num2]}";
    }

    private static string SanitizePathSegment(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        string text = new string(value.Select((char character) => invalidChars.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(text) ? "cache-item" : text;
    }

    private void BuildSeasonState(IReadOnlyList<LibraryVideoItem> files, bool isSeries, int? preservedSelectedSeason = null)
    {
        if (!isSeries)
        {
            ReplaceItems(AvailableSeasons, Array.Empty<int>());
            SelectedSeason = 1;
            selectedPlaybackNavigationSeason = null;
            OnPropertyChanged("HasSeasonTabs");
            return;
        }
        List<int> list = (from season in (from file in files
                where file.IsTvEpisode
                select file.SeasonNumber).Distinct()
            orderby (season == 0) ? int.MaxValue : season
            select season).ToList();
        if (list.Count == 0)
        {
            int num = 1;
            List<int> list2 = new List<int>(num);
            CollectionsMarshal.SetCount(list2, num);
            CollectionsMarshal.AsSpan(list2)[0] = 1;
            list = list2;
        }
        ReplaceItems(AvailableSeasons, list);
        LibraryVideoItem libraryVideoItem = files.FirstOrDefault((LibraryVideoItem file) => file.Id == preferredDetailVideoId);
        LibraryVideoItem libraryVideoItem2 = (from file in files
            where file.HasProgress && !file.IsWatched
            orderby file.LastPlayedAt.GetValueOrDefault() descending, file.ProgressRatio descending
            select file).FirstOrDefault();
        LibraryVideoItem libraryVideoItem3 = (from file in files
            where !file.IsWatched
            orderby (file.SeasonNumber == 0) ? int.MaxValue : file.SeasonNumber, file.EpisodeNumber
            select file).ThenBy<LibraryVideoItem, string>((LibraryVideoItem file) => file.RelativePath, StringComparer.OrdinalIgnoreCase).ThenBy<LibraryVideoItem, string>((LibraryVideoItem file) => file.FileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        LibraryVideoItem libraryVideoItem4 = libraryVideoItem2 ?? libraryVideoItem3;
        int num2 = (SelectedSeason = ((preservedSelectedSeason.HasValue && list.Contains(preservedSelectedSeason.Value)) ? preservedSelectedSeason.Value : (libraryVideoItem?.SeasonNumber ?? libraryVideoItem4?.SeasonNumber ?? list[0])));
        selectedPlaybackNavigationSeason = libraryVideoItem?.SeasonNumber ?? libraryVideoItem4?.SeasonNumber ?? num2;
        OnPropertyChanged("SelectedSeasonOption");
        OnPropertyChanged("HasSeasonTabs");
    }

    private void ApplyDetailFilter()
    {
        IReadOnlyList<LibraryVideoItem> source = ((!IsDetailSeries) ? ((IReadOnlyList<LibraryVideoItem>)OrderMovieFilesForPlayback(allDetailFiles)) : ((IReadOnlyList<LibraryVideoItem>)(from file in allDetailFiles
            where file.SeasonNumber == SelectedSeason
            orderby file.EpisodeNumber
            select file).ThenBy<LibraryVideoItem, string>((LibraryVideoItem file) => file.FileName, StringComparer.OrdinalIgnoreCase).ToList()));
        source = source.Select(ApplyOfflineCacheState).ToList();
        UpdateDetailVersions(source);
        ReplaceItems(DetailFiles, source);
        ChooseDetailPrimaryFile(source);
        RefreshDetailHeaderState();
        OnPropertyChanged("HasDetailFiles");
        UpdateSelectedSeasonOfflineProgress();
        UpdatePlayerNextEpisodeAction();
    }
    private void UpdateDetailVersions(IReadOnlyList<LibraryVideoItem> source)
    {
        if (IsDetailSeries)
        {
            ReplaceItems(DetailVersions, Array.Empty<DetailVersionItem>());
            HasMultipleDetailVersions = false;
            return;
        }

        var versions = BuildMovieVersions(source);
        ReplaceItems(DetailVersions, versions);
        HasMultipleDetailVersions = versions.Count > 1;
    }

    private static List<DetailVersionItem> BuildMovieVersions(IReadOnlyList<LibraryVideoItem> files)
    {
        var groups = new Dictionary<string, List<LibraryVideoItem>>();
        var groupMeta = new Dictionary<string, (string KeyPath, string Label, bool IsBdmv)>();
        foreach (var file in files)
        {
            var path = (file.RelativePath + "/" + file.FileName).Replace('\\', '/').Trim('/');
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var bdmvIndex = Array.FindIndex(segments, (string part) => string.Equals(part, "BDMV", StringComparison.OrdinalIgnoreCase));
            string keyPath;
            string label;
            var isBdmv = false;
            if (bdmvIndex >= 2)
            {
                // BDMV 碟目录：以包含 BDMV 的目录为一个版本。
                keyPath = string.Join('/', segments[..bdmvIndex]);
                label = segments[bdmvIndex - 1];
                isBdmv = true;
            }
            else if (IsDiscImageFileName(file.FileName))
            {
                // ISO 等镜像文件：文件本身即一个版本。
                keyPath = path;
                label = file.FileName;
            }
            else if (segments.Length >= 2)
            {
                // 松散视频文件：以所在目录为一个版本（同目录的多分段属于同一版本）。
                keyPath = string.Join('/', segments[..^1]);
                label = segments[^2];
            }
            else
            {
                keyPath = path;
                label = file.FileName;
            }

            if (!groups.TryGetValue(keyPath, out var groupFiles))
            {
                groups[keyPath] = [file];
                groupMeta[keyPath] = (keyPath, label, isBdmv);
            }
            else
            {
                groupFiles.Add(file);
            }
        }

        return groups.Select((group, index) =>
        {
            var meta = groupMeta[group.Key];
            var primary = meta.IsBdmv
                ? group.Value.OrderByDescending((LibraryVideoItem item) => item.Duration).First()
                : group.Value.First();
            return new DetailVersionItem
            {
                Index = index + 1,
                Label = meta.Label,
                KeyPath = meta.KeyPath,
                FileCount = group.Value.Count,
                PrimaryFile = primary,
            };
        }).ToList();
    }

    private static bool IsDiscImageFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension is ".iso" or ".img" or ".nrg" or ".mds" or ".bvd";
    }

    private void ChooseDetailPrimaryFile(IReadOnlyList<LibraryVideoItem> files)
    {
        if (files.Count == 0)
        {
            DetailPrimaryFile = null;
            return;
        }
        LibraryVideoItem libraryVideoItem = ((preferredDetailVideoId != null) ? files.FirstOrDefault((LibraryVideoItem file) => file.Id == preferredDetailVideoId) : null);
        LibraryVideoItem libraryVideoItem2 = (from file in files
            where file.HasProgress && !file.IsWatched
            orderby file.LastPlayedAt.GetValueOrDefault() descending, file.ProgressRatio descending
            select file).FirstOrDefault();
        LibraryVideoItem libraryVideoItem3 = (from file in files
            where !file.IsWatched
            orderby (file.SeasonNumber == 0) ? int.MaxValue : file.SeasonNumber, file.EpisodeNumber
            select file).FirstOrDefault();
        DetailPrimaryFile = libraryVideoItem ?? libraryVideoItem2 ?? libraryVideoItem3 ?? files.First();
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
        LibraryVideoItem libraryVideoItem = ResolveCurrentPlaybackMoviePart();
        if (libraryVideoItem != null)
        {
            LibraryVideoItem libraryVideoItem2 = FindPreviousMoviePart(libraryVideoItem);
            LibraryVideoItem libraryVideoItem3 = FindNextMoviePart(libraryVideoItem);
            Player.ConfigurePreviousEpisodeAction((libraryVideoItem2 == null) ? null : new Func<Task>(PlayPreviousMoviePartAsync), (libraryVideoItem2 == null) ? null : ("播放上一段 · " + BuildMoviePartLabel(libraryVideoItem2)));
            Player.ConfigureNextEpisodeAction((libraryVideoItem3 == null) ? null : new Func<Task>(PlayNextMoviePartAsync), (libraryVideoItem3 == null) ? null : ("播放下一段 · " + BuildMoviePartLabel(libraryVideoItem3)));
            return;
        }
        LibraryVideoItem libraryVideoItem4 = ResolveCurrentPlaybackEpisode();
        LibraryVideoItem libraryVideoItem5 = ((libraryVideoItem4 == null) ? null : FindPreviousEpisode(libraryVideoItem4));
        LibraryVideoItem libraryVideoItem6 = ((libraryVideoItem4 == null) ? null : FindNextEpisode(libraryVideoItem4));
        if (libraryVideoItem5 == null)
        {
            Player.ConfigurePreviousEpisodeAction(null);
        }
        else
        {
            string text = (string.IsNullOrWhiteSpace(libraryVideoItem5.EpisodeLabel) ? FormatEpisodeTitle(libraryVideoItem5) : libraryVideoItem5.EpisodeLabel);
            Player.ConfigurePreviousEpisodeAction(PlayPreviousEpisodeAsync, "播放上一集 · " + text);
        }
        if (libraryVideoItem6 == null)
        {
            Player.ConfigureNextEpisodeAction(null);
            return;
        }
        string text2 = (string.IsNullOrWhiteSpace(libraryVideoItem6.EpisodeLabel) ? FormatEpisodeTitle(libraryVideoItem6) : libraryVideoItem6.EpisodeLabel);
        Player.ConfigureNextEpisodeAction(PlayNextEpisodeAsync, "播放下一集 · " + text2);
    }

    private void UpdatePlayerPlaybackNavigation()
    {
        if (!currentDetailTvShowId.HasValue)
        {
            Player.ConfigurePlaybackNavigation(Array.Empty<PlayerNavigationItem>(), null);
            return;
        }
        List<LibraryVideoItem> orderedEpisodes = GetOrderedEpisodes();
        if (orderedEpisodes.Count == 0)
        {
            Player.ConfigurePlaybackNavigation(Array.Empty<PlayerNavigationItem>(), null);
            return;
        }
        List<int> seasons = (from season in orderedEpisodes.Select((LibraryVideoItem file) => file.SeasonNumber).Distinct()
            orderby (season == 0) ? int.MaxValue : season
            select season).ToList();
        int selectedSeason = ResolvePlaybackNavigationSeason(orderedEpisodes);
        string currentEpisodeId = ((!string.IsNullOrWhiteSpace(currentPlayingVideoId)) ? currentPlayingVideoId : DetailPrimaryFile?.Id);
        List<PlayerNavigationItem> items = (from file in orderedEpisodes
            where file.SeasonNumber == selectedSeason
            select new PlayerNavigationItem(file.Id, BuildPlaybackNavigationItemTitle(file), BuildPlaybackNavigationItemSubtitle(file), BuildPlaybackNavigationItemStatus(file, currentEpisodeId), string.Equals(file.Id, currentEpisodeId, StringComparison.Ordinal), file.IsWatched)).ToList();
        Player.ConfigurePlaybackNavigation(items, PlayNavigationItemAsync, "剧集导航", seasons, selectedSeason, SelectPlaybackNavigationSeasonAsync);
    }

    private LibraryVideoItem? ResolveCurrentPlaybackEpisode()
    {
        if (!currentDetailTvShowId.HasValue || string.IsNullOrWhiteSpace(currentPlayingVideoId) || allDetailFiles.Count == 0)
        {
            return null;
        }
        return allDetailFiles.FirstOrDefault((LibraryVideoItem file) => file.IsTvEpisode && string.Equals(file.Id, currentPlayingVideoId, StringComparison.Ordinal));
    }

    private LibraryVideoItem? ResolveCurrentPlaybackVideo()
    {
        if (string.IsNullOrWhiteSpace(currentPlayingVideoId) || allDetailFiles.Count == 0)
        {
            return null;
        }
        return allDetailFiles.FirstOrDefault((LibraryVideoItem file) => string.Equals(file.Id, currentPlayingVideoId, StringComparison.Ordinal));
    }

    private LibraryVideoItem? ResolveCurrentPlaybackMoviePart()
    {
        if (!currentDetailMovieId.HasValue || IsDetailSeries || string.IsNullOrWhiteSpace(currentPlayingVideoId))
        {
            return null;
        }
        List<LibraryVideoItem> orderedMovieParts = GetOrderedMovieParts();
        return (orderedMovieParts.Count > 1) ? orderedMovieParts.FirstOrDefault((LibraryVideoItem file) => string.Equals(file.Id, currentPlayingVideoId, StringComparison.Ordinal)) : null;
    }

    private List<LibraryVideoItem> GetOrderedMovieParts()
    {
        return OrderMovieFilesForPlayback(allDetailFiles.Where((LibraryVideoItem file) => !file.IsTvEpisode));
    }

    private static List<LibraryVideoItem> OrderMovieFilesForPlayback(IEnumerable<LibraryVideoItem> files)
    {
        List<LibraryVideoItem> list = files.ToList();
        list.Sort(CompareMovieFilesForPlayback);
        return list;
    }

    private static int CompareMovieFilesForPlayback(LibraryVideoItem left, LibraryVideoItem right)
    {
        int num = ResolveMoviePartSortIndex(left).CompareTo(ResolveMoviePartSortIndex(right));
        if (num != 0)
        {
            return num;
        }
        int num2 = StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath);
        if (num2 != 0)
        {
            return num2;
        }
        int num3 = StringComparer.OrdinalIgnoreCase.Compare(left.FileName, right.FileName);
        return (num3 != 0) ? num3 : StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id);
    }

    private static int ResolveMoviePartSortIndex(LibraryVideoItem file)
    {
        string input = file.RelativePath + "/" + file.FileName;
        string[] array = (from token in Regex.Split(input, "[\\\\/\\s._\\-\\[\\]\\(\\)\\{\\}【】（）,:：]+")
            where !string.IsNullOrWhiteSpace(token)
            select token).ToArray();
        for (int num = 0; num < array.Length; num++)
        {
            string text = array[num];
            if (TryParseChineseMoviePartToken(text, out var partIndex))
            {
                return partIndex;
            }
            string text2 = text.ToLowerInvariant();
            string[] moviePartPrefixes = MoviePartPrefixes;
            foreach (string text3 in moviePartPrefixes)
            {
                if (string.Equals(text2, text3, StringComparison.OrdinalIgnoreCase))
                {
                    if (num + 1 < array.Length && TryParseMoviePartToken(array[num + 1], out var partIndex2))
                    {
                        return partIndex2;
                    }
                }
                else if (text2.StartsWith(text3, StringComparison.OrdinalIgnoreCase))
                {
                    string text4 = text2;
                    int length = text3.Length;
                    if (TryParseMoviePartToken(text4.Substring(length, text4.Length - length), out var partIndex3))
                    {
                        return partIndex3;
                    }
                }
            }
        }
        return int.MaxValue;
    }

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
        if (1 == 0)
        {
        }
        int num;
        switch (token)
        {
        case "上":
        case "上半":
        case "上篇":
        case "前篇":
            num = 1;
            break;
        case "下":
        case "下半":
        case "下篇":
        case "后篇":
        case "後篇":
            num = 2;
            break;
        default:
            num = 0;
            break;
        }
        if (1 == 0)
        {
        }
        partIndex = num;
        return partIndex > 0;
    }

    private static bool TryParseRomanNumber(string token, out int value)
    {
        value = 0;
        token = token.Trim().ToUpperInvariant();
        if (token.Length == 0 || token.Any(delegate(char ch)
        {
            bool flag;
            switch (ch)
            {
            case 'C':
            case 'D':
            case 'I':
            case 'L':
            case 'M':
            case 'V':
            case 'X':
                flag = true;
                break;
            default:
                flag = false;
                break;
            }
            return !flag;
        }))
        {
            return false;
        }
        for (int num = 0; num < token.Length; num++)
        {
            int num2 = RomanDigitValue(token[num]);
            int num3 = ((num + 1 < token.Length) ? RomanDigitValue(token[num + 1]) : 0);
            value += ((num2 < num3) ? (-num2) : num2);
        }
        return value > 0;
    }

    private static int RomanDigitValue(char ch)
    {
        if (1 == 0)
        {
        }
        int result = ch switch
        {
            'I' => 1, 
            'V' => 5, 
            'X' => 10, 
            'L' => 50, 
            'C' => 100, 
            'D' => 500, 
            'M' => 1000, 
            _ => 0, 
        };
        if (1 == 0)
        {
        }
        return result;
    }

    private LibraryVideoItem? FindPreviousMoviePart(LibraryVideoItem currentPart)
    {
        List<LibraryVideoItem> orderedMovieParts = GetOrderedMovieParts();
        int num = orderedMovieParts.FindIndex((LibraryVideoItem file) => string.Equals(file.Id, currentPart.Id, StringComparison.Ordinal));
        return (num > 0) ? orderedMovieParts[num - 1] : null;
    }

    private LibraryVideoItem? FindNextMoviePart(LibraryVideoItem currentPart)
    {
        List<LibraryVideoItem> orderedMovieParts = GetOrderedMovieParts();
        int num = orderedMovieParts.FindIndex((LibraryVideoItem file) => string.Equals(file.Id, currentPart.Id, StringComparison.Ordinal));
        return (num >= 0 && num + 1 < orderedMovieParts.Count) ? orderedMovieParts[num + 1] : null;
    }

    private static string BuildMoviePartLabel(LibraryVideoItem part)
    {
        return string.IsNullOrWhiteSpace(part.RelativePath) ? part.FileName : part.RelativePath;
    }

    private List<LibraryVideoItem> GetOrderedEpisodes()
    {
        return (from file in allDetailFiles
            where file.IsTvEpisode
            orderby (file.SeasonNumber == 0) ? int.MaxValue : file.SeasonNumber, file.EpisodeNumber
            select file).ThenBy<LibraryVideoItem, string>((LibraryVideoItem file) => file.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private int ResolvePlaybackNavigationSeason(IReadOnlyList<LibraryVideoItem> orderedEpisodes)
    {
        LibraryVideoItem libraryVideoItem = ResolveCurrentPlaybackEpisode();
        if (libraryVideoItem != null)
        {
            selectedPlaybackNavigationSeason = libraryVideoItem.SeasonNumber;
            return libraryVideoItem.SeasonNumber;
        }
        if (selectedPlaybackNavigationSeason.HasValue && orderedEpisodes.Any((LibraryVideoItem file) => file.SeasonNumber == selectedPlaybackNavigationSeason.Value))
        {
            return selectedPlaybackNavigationSeason.Value;
        }
        LibraryVideoItem? libraryVideoItem2 = DetailPrimaryFile;
        int? num = ((libraryVideoItem2 != null) ? new int?(libraryVideoItem2.SeasonNumber) : orderedEpisodes.FirstOrDefault((LibraryVideoItem file) => file.Id == preferredDetailVideoId)?.SeasonNumber);
        if (num.HasValue)
        {
            selectedPlaybackNavigationSeason = num.Value;
            return num.Value;
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
        List<LibraryVideoItem> orderedEpisodes = GetOrderedEpisodes();
        int num = orderedEpisodes.FindIndex((LibraryVideoItem file) => string.Equals(file.Id, currentEpisode.Id, StringComparison.Ordinal));
        if (num <= 0)
        {
            return null;
        }
        return orderedEpisodes[num - 1];
    }

    private LibraryVideoItem? FindNextEpisode(LibraryVideoItem currentEpisode)
    {
        if (!currentEpisode.IsTvEpisode)
        {
            return null;
        }
        List<LibraryVideoItem> orderedEpisodes = GetOrderedEpisodes();
        int num = orderedEpisodes.FindIndex((LibraryVideoItem file) => string.Equals(file.Id, currentEpisode.Id, StringComparison.Ordinal));
        if (num < 0 || num + 1 >= orderedEpisodes.Count)
        {
            return null;
        }
        return orderedEpisodes[num + 1];
    }

    private async Task PlayPreviousMoviePartAsync()
    {
        LibraryVideoItem currentPart = ResolveCurrentPlaybackMoviePart();
        LibraryVideoItem previousPart = ((currentPart == null) ? null : FindPreviousMoviePart(currentPart));
        if (currentPart == null || previousPart == null)
        {
            UpdatePlayerNextEpisodeAction();
        }
        else if (!MediaSourcePathResolver.IsPlayableLocation(previousPart.EffectivePlaybackPath))
        {
            StatusMessage = "上一段文件不可播放：" + previousPart.FileName;
        }
        else
        {
            await TransitionToMoviePartAsync(previousPart);
        }
    }

    private async Task PlayNextMoviePartAsync()
    {
        LibraryVideoItem currentPart = ResolveCurrentPlaybackMoviePart();
        LibraryVideoItem nextPart = ((currentPart == null) ? null : FindNextMoviePart(currentPart));
        if (currentPart == null || nextPart == null)
        {
            UpdatePlayerNextEpisodeAction();
        }
        else if (!MediaSourcePathResolver.IsPlayableLocation(nextPart.EffectivePlaybackPath))
        {
            StatusMessage = "下一段文件不可播放：" + nextPart.FileName;
        }
        else
        {
            await TransitionToMoviePartAsync(nextPart, markCurrentAsFinished: true);
        }
    }

    private async Task PlayPreviousEpisodeAsync()
    {
        LibraryVideoItem currentEpisode = ResolveCurrentPlaybackEpisode();
        LibraryVideoItem previousEpisode = ((currentEpisode == null) ? null : FindPreviousEpisode(currentEpisode));
        if (currentEpisode == null || previousEpisode == null)
        {
            UpdatePlayerNextEpisodeAction();
        }
        else if (!MediaSourcePathResolver.IsPlayableLocation(previousEpisode.EffectivePlaybackPath))
        {
            StatusMessage = "上一集文件不可播放：" + previousEpisode.FileName;
        }
        else
        {
            await TransitionToEpisodeAsync(previousEpisode);
        }
    }

    private async Task PlayNextEpisodeAsync()
    {
        LibraryVideoItem currentEpisode = ResolveCurrentPlaybackEpisode();
        LibraryVideoItem nextEpisode = ((currentEpisode == null) ? null : FindNextEpisode(currentEpisode));
        if (currentEpisode == null || nextEpisode == null)
        {
            UpdatePlayerNextEpisodeAction();
        }
        else if (!MediaSourcePathResolver.IsPlayableLocation(nextEpisode.EffectivePlaybackPath))
        {
            StatusMessage = "下一集文件不可播放：" + nextEpisode.FileName;
        }
        else
        {
            await TransitionToEpisodeAsync(nextEpisode, markCurrentAsFinished: true);
        }
    }

    private Task SelectPlaybackNavigationSeasonAsync(int season)
    {
        if (!currentDetailTvShowId.HasValue)
        {
            return Task.CompletedTask;
        }
        List<LibraryVideoItem> orderedEpisodes = GetOrderedEpisodes();
        if (orderedEpisodes.Count == 0 || !orderedEpisodes.Any((LibraryVideoItem file) => file.SeasonNumber == season))
        {
            return Task.CompletedTask;
        }
        selectedPlaybackNavigationSeason = season;
        UpdatePlayerPlaybackNavigation();
        return Task.CompletedTask;
    }

    private async Task PlayNavigationItemAsync(string videoId)
    {
        LibraryVideoItem targetEpisode = ResolveDetailVideo(videoId);
        if (targetEpisode != null)
        {
            await TransitionToEpisodeAsync(targetEpisode);
        }
    }

    private async Task TransitionToMoviePartAsync(LibraryVideoItem targetPart, bool markCurrentAsFinished = false, double? startPositionSeconds = null)
    {
        if (!MediaSourcePathResolver.IsPlayableLocation(targetPart.EffectivePlaybackPath))
        {
            StatusMessage = "文件不可播放：" + targetPart.FileName;
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
        LibraryVideoItem currentPart = ResolveCurrentPlaybackMoviePart();
        if (currentPart != null)
        {
            StopPlaybackProgressSync();
            await PersistTransitionPlaybackStateAsync(currentPart, markCurrentAsFinished);
            suppressedPlaybackPersistenceVideoId = ((currentPlaybackMode == PlaybackMode.Overlay) ? currentPart.Id : null);
        }
        else
        {
            suppressedPlaybackPersistenceVideoId = null;
        }
        preferredDetailVideoId = targetPart.Id;
        await RefreshDetailFilesAsync();
        await RefreshContinueWatchingAsync();
        LibraryVideoItem resolvedTargetPart = ResolveDetailVideo(targetPart.Id) ?? targetPart;
        ApplyPreferredDetailVideoSelection(resolvedTargetPart);
        switch (currentPlaybackMode)
        {
        case PlaybackMode.Overlay:
            await PlayVideoWithStartPositionAsync(resolvedTargetPart, startPositionSeconds);
            break;
        case PlaybackMode.Standalone:
            await OpenStandalonePlayerInternalAsync(resolvedTargetPart, replaceCurrentSession: true, persistCurrentPlaybackState: false);
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
            StatusMessage = "文件不可播放：" + targetEpisode.FileName;
            return;
        }
        if (string.Equals(currentPlayingVideoId, targetEpisode.Id, StringComparison.Ordinal))
        {
            ApplyPreferredDetailVideoSelection(targetEpisode);
            return;
        }
        LibraryVideoItem currentEpisode = ResolveCurrentPlaybackEpisode();
        if (currentEpisode != null)
        {
            StopPlaybackProgressSync();
            await PersistTransitionPlaybackStateAsync(currentEpisode, markCurrentAsFinished);
            suppressedPlaybackPersistenceVideoId = ((currentPlaybackMode == PlaybackMode.Overlay) ? currentEpisode.Id : null);
        }
        else
        {
            suppressedPlaybackPersistenceVideoId = null;
        }
        selectedPlaybackNavigationSeason = targetEpisode.SeasonNumber;
        preferredDetailVideoId = targetEpisode.Id;
        await RefreshDetailFilesAsync();
        await RefreshContinueWatchingAsync();
        LibraryVideoItem resolvedTargetEpisode = ResolveDetailVideo(targetEpisode.Id) ?? targetEpisode;
        ApplyPreferredDetailVideoSelection(resolvedTargetEpisode);
        switch (currentPlaybackMode)
        {
        case PlaybackMode.Overlay:
            await PlayVideoAsync(resolvedTargetEpisode);
            break;
        case PlaybackMode.Standalone:
            await OpenStandalonePlayerInternalAsync(resolvedTargetEpisode, replaceCurrentSession: true, persistCurrentPlaybackState: false);
            break;
        default:
            suppressedPlaybackPersistenceVideoId = null;
            UpdatePlayerNextEpisodeAction();
            break;
        }
    }

    private static string BuildPlaybackNavigationItemTitle(LibraryVideoItem episode)
    {
        return episode.IsTvEpisode ? $"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}" : episode.FileName;
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
        if (!string.IsNullOrWhiteSpace(currentEpisodeId) && string.Equals(episode.Id, currentEpisodeId, StringComparison.Ordinal))
        {
            return "当前播放";
        }
        return episode.ProgressText;
    }

    private LibraryVideoItem? ResolveDetailVideo(string videoId)
    {
        return allDetailFiles.FirstOrDefault((LibraryVideoItem file) => string.Equals(file.Id, videoId, StringComparison.Ordinal));
    }

    private async Task PersistTransitionPlaybackStateAsync(LibraryVideoItem episode, bool markAsFinished = false)
    {
        double duration = Math.Max(episode.Duration, Player.DurationSeconds);
        double progress = Math.Max(episode.PlayProgress, Player.CurrentPositionSeconds);
        bool shouldMarkWatched = markAsFinished || episode.IsWatched || Player.PlaybackProgressRatio >= 0.95;
        if (shouldMarkWatched)
        {
            duration = Math.Max(duration, Math.Max(progress, 100.0));
        }
        double persistedProgress = ((shouldMarkWatched && duration > 0.0) ? duration : NormalizePersistedProgress(progress, duration));
        await videoFileRepository.UpdatePlaybackStateAsync(episode.Id, persistedProgress, (duration > 0.0) ? new double?(duration) : ((double?)null));
    }

    private bool ShouldSuppressPlaybackPersistence(string videoId)
    {
        if (string.IsNullOrWhiteSpace(suppressedPlaybackPersistenceVideoId) || !string.Equals(suppressedPlaybackPersistenceVideoId, videoId, StringComparison.Ordinal))
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

    private (double PositionSeconds, double DurationSeconds) ResolveMultipartSavedTimeline()
    {
        double num = 0.0;
        double num2 = 0.0;
        foreach (LibraryVideoItem orderedMoviePart in GetOrderedMovieParts())
        {
            if (!(orderedMoviePart.Duration <= 0.0))
            {
                num2 += orderedMoviePart.Duration;
                num += (orderedMoviePart.IsWatched ? orderedMoviePart.Duration : Math.Min(Math.Max(orderedMoviePart.PlayProgress, 0.0), orderedMoviePart.Duration));
            }
        }
        return (PositionSeconds: num, DurationSeconds: num2);
    }

    private (double PositionSeconds, double DurationSeconds) ResolvePlayerTimeline()
    {
        if (!IsPlayingMultipartMovie)
        {
            return (PositionSeconds: Math.Max(Player.CurrentPositionSeconds, 0.0), DurationSeconds: Math.Max(Player.DurationSeconds, 0.0));
        }
        List<LibraryVideoItem> orderedMovieParts = GetOrderedMovieParts();
        int num = orderedMovieParts.FindIndex((LibraryVideoItem part) => string.Equals(part.Id, currentPlayingVideoId, StringComparison.Ordinal));
        if (num < 0)
        {
            return (PositionSeconds: Math.Max(Player.CurrentPositionSeconds, 0.0), DurationSeconds: Math.Max(Player.DurationSeconds, 0.0));
        }
        double num2 = 0.0;
        double num3 = 0.0;
        for (int num4 = 0; num4 < orderedMovieParts.Count; num4++)
        {
            double val = ((num4 == num) ? Math.Max(orderedMovieParts[num4].Duration, Math.Max(Player.DurationSeconds, Player.CurrentPositionSeconds)) : orderedMovieParts[num4].Duration);
            val = Math.Max(val, 0.0);
            if (num4 < num)
            {
                num2 += val;
            }
            num3 += val;
        }
        num2 += Math.Max(Player.CurrentPositionSeconds, 0.0);
        return (PositionSeconds: Math.Min(num2, Math.Max(num3, num2)), DurationSeconds: num3);
    }

    private IReadOnlyList<double> ResolvePlayerTimelineSegmentBoundaries()
    {
        if (!IsPlayingMultipartMovie)
        {
            return Array.Empty<double>();
        }
        List<LibraryVideoItem> orderedMovieParts = GetOrderedMovieParts();
        int currentIndex = orderedMovieParts.FindIndex((LibraryVideoItem part) => string.Equals(part.Id, currentPlayingVideoId, StringComparison.Ordinal));
        if (orderedMovieParts.Count <= 1 || currentIndex < 0)
        {
            return Array.Empty<double>();
        }
        double[] array = orderedMovieParts.Select((LibraryVideoItem part, int index) => (index == currentIndex) ? Math.Max(part.Duration, Math.Max(Player.DurationSeconds, Player.CurrentPositionSeconds)) : Math.Max(part.Duration, 0.0)).ToArray();
        double num = array.Sum();
        if (num <= 0.0)
        {
            return Array.Empty<double>();
        }
        List<double> list = new List<double>(Math.Max(orderedMovieParts.Count - 1, 0));
        double num2 = 0.0;
        for (int num3 = 0; num3 < array.Length - 1; num3++)
        {
            num2 += array[num3];
            if (num2 > 0.0 && num2 < num)
            {
                list.Add(num2 / num);
            }
        }
        return list;
    }

    private double ClampPlayerTimelinePosition(double value)
    {
        double playerTimelineDurationSeconds = PlayerTimelineDurationSeconds;
        if (playerTimelineDurationSeconds <= 0.0)
        {
            return Math.Max(value, 0.0);
        }
        return Math.Clamp(value, 0.0, playerTimelineDurationSeconds);
    }

    private (LibraryVideoItem Part, double LocalPositionSeconds)? ResolveMultipartSeekTarget(double targetSeconds)
    {
        List<LibraryVideoItem> orderedMovieParts = GetOrderedMovieParts();
        int num = orderedMovieParts.FindIndex((LibraryVideoItem part) => string.Equals(part.Id, currentPlayingVideoId, StringComparison.Ordinal));
        double num2 = 0.0;
        for (int num3 = 0; num3 < orderedMovieParts.Count; num3++)
        {
            LibraryVideoItem libraryVideoItem = orderedMovieParts[num3];
            double val = ((num3 == num) ? Math.Max(libraryVideoItem.Duration, Math.Max(Player.DurationSeconds, Player.CurrentPositionSeconds)) : libraryVideoItem.Duration);
            val = Math.Max(val, 0.0);
            if (!(val <= 0.0))
            {
                if (targetSeconds <= num2 + val || num3 == orderedMovieParts.Count - 1)
                {
                    return (libraryVideoItem, Math.Clamp(targetSeconds - num2, 0.0, val));
                }
                num2 += val;
            }
        }
        LibraryVideoItem libraryVideoItem2 = orderedMovieParts.LastOrDefault();
        return (libraryVideoItem2 == null) ? (((LibraryVideoItem, double)?)null) : new(LibraryVideoItem, double)?((libraryVideoItem2, 0.0));
    }

    private static string FormatPlaybackTime(double seconds)
    {
        int num = Math.Max(0, (int)Math.Floor(double.IsFinite(seconds) ? seconds : 0.0));
        return TimeSpan.FromSeconds(num).ToString((num >= 3600) ? "h\\:mm\\:ss" : "m\\:ss", CultureInfo.InvariantCulture);
    }

    private void NotifyPlayerTimelineChanged()
    {
        OnPropertyChanged("PlayerTimelineDurationSeconds");
        OnPropertyChanged("PlayerTimelineSeekPositionSeconds");
        OnPropertyChanged("PlayerTimelineSegmentBoundaries");
        OnPropertyChanged("PlayerTimelineCurrentPositionText");
        OnPropertyChanged("PlayerTimelineRemainingTimeText");
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
        }
        else if (isMultipartTimelineSeekPreviewActive)
        {
            multipartTimelineSeekPreviewSeconds = ClampPlayerTimelinePosition(value);
            NotifyPlayerTimelineChanged();
        }
    }

    public async Task CommitPlayerTimelineSeekAsync(double value)
    {
        if (!IsPlayingMultipartMovie)
        {
            await Player.CommitSeekAsync(value);
            return;
        }
        (LibraryVideoItem Part, double LocalPositionSeconds)? target = ResolveMultipartSeekTarget(ClampPlayerTimelinePosition(value));
        isMultipartTimelineSeekPreviewActive = false;
        if (!target.HasValue)
        {
            NotifyPlayerTimelineChanged();
            return;
        }
        if (!string.Equals(target.Value.Part.Id, currentPlayingVideoId, StringComparison.Ordinal))
        {
            await TransitionToMoviePartAsync(target.Value.Part, IsSeekTargetAfterCurrentPart(target.Value.Part), target.Value.LocalPositionSeconds);
        }
        else
        {
            await Player.CommitSeekAsync(target.Value.LocalPositionSeconds);
        }
        NotifyPlayerTimelineChanged();
    }

    private async Task SeekPlayerTimelineRelativeAsync(double deltaSeconds)
    {
        if (!IsPlayingMultipartMovie)
        {
            if (deltaSeconds < 0.0)
            {
                await Player.SeekBackwardCommand.ExecuteAsync(null);
            }
            else
            {
                await Player.SeekForwardCommand.ExecuteAsync(null);
            }
        }
        else
        {
            (double PositionSeconds, double DurationSeconds) timeline = ResolvePlayerTimeline();
            await CommitPlayerTimelineSeekAsync(Math.Clamp(timeline.PositionSeconds + deltaSeconds, 0.0, timeline.DurationSeconds));
        }
    }

    private bool CanSeekPlayerTimeline()
    {
        return Player.IsPlaying && Player.IsAvailable && PlayerTimelineDurationSeconds > 0.0;
    }

    private bool IsSeekTargetAfterCurrentPart(LibraryVideoItem targetPart)
    {
        List<LibraryVideoItem> orderedMovieParts = GetOrderedMovieParts();
        int num = orderedMovieParts.FindIndex((LibraryVideoItem part) => string.Equals(part.Id, currentPlayingVideoId, StringComparison.Ordinal));
        int num2 = orderedMovieParts.FindIndex((LibraryVideoItem part) => string.Equals(part.Id, targetPart.Id, StringComparison.Ordinal));
        return num >= 0 && num2 > num;
    }

    private void RefreshDetailHeaderState()
    {
        if (DetailFiles.Count == 0)
        {
            DetailFileSummary = "正在读取文件...";
            DetailPrimaryFileProgressText = "未播";
            DetailPrimaryTimeText = "00:00 / 未知时长";
            DetailPrimaryActionText = "开始播放";
            DetailPrimaryProgressRatio = 0.0;
            DetailPlaybackProgressRatio = 0.0;
            DetailSelectionHint = "先添加目录并执行扫描，随后这里会显示文件。";
            DetailWatchedActionText = "未播";
            OnPropertyChanged("CanShowDetailPrimaryProgress");
            return;
        }
        DetailFileSummary = (IsDetailSeries ? $"当前季共 {DetailFiles.Count} 集" : $"共 {DetailFiles.Count} 个文件");
        if (DetailPrimaryFile == null)
        {
            DetailProgressSummary = "请选择文件";
            DetailPrimaryFileProgressText = "未播";
            DetailPrimaryTimeText = "00:00 / 未知时长";
            DetailPrimaryActionText = "开始播放";
            DetailPrimaryProgressRatio = 0.0;
            DetailPlaybackProgressRatio = 0.0;
            DetailSelectionHint = "点击下方条目可切换主文件。";
            DetailWatchedActionText = "未播";
            OnPropertyChanged("CanShowDetailPrimaryProgress");
        }
        else
        {
            PlaybackWatchState watchState = ResolveDetailWatchState();
            double num = ResolveDetailProgressRatio(watchState);
            (double, double) tuple = (IsDetailMultipartMovie ? ResolveMultipartSavedTimeline() : default((double, double)));
            DetailPrimaryProgressRatio = num;
            DetailPlaybackProgressRatio = (IsDetailMultipartMovie ? num : DetailPrimaryFile.ProgressRatio);
            DetailPrimaryFileProgressText = (IsDetailMultipartMovie ? FormatWatchStateSummary(watchState, num) : DetailPrimaryFile.ProgressText);
            DetailPrimaryTimeText = ((IsDetailMultipartMovie && tuple.Item2 > 0.0) ? (FormatPlaybackTime(tuple.Item1) + " / " + FormatPlaybackTime(tuple.Item2)) : (DetailPrimaryFile.PositionText + " / " + DetailPrimaryFile.DurationText));
            DetailPrimaryActionText = BuildPrimaryActionText(DetailPrimaryFile);
            DetailProgressSummary = FormatWatchStateSummary(watchState, num);
            DetailSelectionHint = (DetailPrimaryFile.IsTvEpisode ? ("当前主集：" + FormatEpisodeTitle(DetailPrimaryFile)) : (IsDetailMultipartMovie ? ("多段电影 · 当前分段：" + BuildMoviePartLabel(DetailPrimaryFile)) : "当前主文件已选中。"));
            DetailWatchedActionText = DetailPrimaryFile.WatchStateText;
            OnPropertyChanged("CanShowDetailPrimaryProgress");
        }
    }

    private PlaybackWatchState ResolveDetailWatchState()
    {
        List<LibraryVideoItem> list = ((allDetailFiles.Count > 0) ? allDetailFiles : DetailFiles.ToList());
        if (list.Count == 0 || list.All((LibraryVideoItem file) => file.WatchState == PlaybackWatchState.Unwatched))
        {
            return PlaybackWatchState.Unwatched;
        }
        if (IsDetailSeries)
        {
            return (!list.All((LibraryVideoItem file) => file.WatchState == PlaybackWatchState.Watched)) ? PlaybackWatchState.InProgress : PlaybackWatchState.Watched;
        }
        if (IsDetailMultipartMovie)
        {
            return (!list.All((LibraryVideoItem file) => file.WatchState == PlaybackWatchState.Watched)) ? PlaybackWatchState.InProgress : PlaybackWatchState.Watched;
        }
        return (!list.Any((LibraryVideoItem file) => file.WatchState == PlaybackWatchState.Watched)) ? PlaybackWatchState.InProgress : PlaybackWatchState.Watched;
    }

    private double ResolveDetailProgressRatio(PlaybackWatchState watchState)
    {
        if (watchState == PlaybackWatchState.Watched)
        {
            return 1.0;
        }
        List<LibraryVideoItem> list = ((allDetailFiles.Count > 0) ? allDetailFiles : DetailFiles.ToList());
        if (list.Count == 0)
        {
            return 0.0;
        }
        if (IsDetailSeries)
        {
            return list.Average((LibraryVideoItem file) => (file.WatchState == PlaybackWatchState.Watched) ? 1.0 : file.ProgressRatio);
        }
        if (IsDetailMultipartMovie)
        {
            (double, double) tuple = ResolveMultipartSavedTimeline();
            return (tuple.Item2 > 0.0) ? Math.Clamp(tuple.Item1 / tuple.Item2, 0.0, 1.0) : list.Max((LibraryVideoItem file) => file.ProgressRatio);
        }
        return list.Max((LibraryVideoItem file) => file.ProgressRatio);
    }

    private static string FormatWatchStateSummary(PlaybackWatchState watchState, double progressRatio)
    {
        if (1 == 0)
        {
        }
        string result = watchState switch
        {
            PlaybackWatchState.Watched => "已播", 
            PlaybackWatchState.InProgress => (!(progressRatio > 0.0)) ? "未播完" : $"未播完 · {Math.Clamp(progressRatio, 0.0, 1.0) * 100.0:F0}%", 
            _ => "未播", 
        };
        if (1 == 0)
        {
        }
        return result;
    }

    private static string BuildPrimaryActionText(LibraryVideoItem file)
    {
        string text = ((file.HasProgress && !file.IsWatched) ? "继续播放" : "开始播放");
        return file.IsTvEpisode ? (text + " " + FormatEpisodeTitle(file)) : text;
    }

    private static string FormatEpisodeTitle(LibraryVideoItem file)
    {
        return file.EpisodeDisplayTitle;
    }

    private static string FormatMovieSubtitle(Movie movie)
    {
        return HasVisibleMetadataYear(movie) ? FormatMediaYear(movie.ReleaseDate) : string.Empty;
    }

    private static string FormatTvShowSubtitle(TvShow show)
    {
        return HasVisibleMetadataYear(show) ? FormatMediaYear(show.FirstAirDate) : string.Empty;
    }

    private static string FormatTvShowSubtitle(string? firstAirDate)
    {
        return FormatMediaYear(firstAirDate);
    }

    private static string FormatMediaYear(string? value)
    {
        string text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }
        return (text.Length >= 4) ? text.Substring(0, 4) : text;
    }

    private static bool HasVisibleMetadataYear(Movie movie)
    {
        return !string.IsNullOrWhiteSpace(movie.ReleaseDate) && (movie.IsLocked || !string.IsNullOrWhiteSpace(movie.Overview) || !string.IsNullOrWhiteSpace(movie.PosterPath) || movie.VoteAverage.HasValue || !string.IsNullOrWhiteSpace(movie.ProductionCountryCodes) || !string.IsNullOrWhiteSpace(movie.OriginalLanguage));
    }

    private static bool HasVisibleMetadataYear(TvShow show)
    {
        return !string.IsNullOrWhiteSpace(show.FirstAirDate) && (show.IsLocked || !string.IsNullOrWhiteSpace(show.Overview) || !string.IsNullOrWhiteSpace(show.PosterPath) || show.VoteAverage.HasValue || !string.IsNullOrWhiteSpace(show.ProductionCountryCodes) || !string.IsNullOrWhiteSpace(show.OriginalLanguage));
    }

    private void QueueLibraryRefreshIfNeeded(bool force = false)
    {
        TmdbSettings settings = Settings.BuildTmdbSettings();
        Task task = libraryRefreshTask;
        if (task != null && !task.IsCompleted)
        {
            if (!force || forceRefreshQueued)
            {
                return;
            }
            forceRefreshQueued = true;
            libraryRefreshTask = libraryRefreshTask.ContinueWith((Func<Task, Task>)async delegate
            {
                if (forceRefreshQueued)
                {
                    forceRefreshQueued = false;
                    await RunLibraryArtworkRefreshAsync(isExplicitRequest: false, forceThumbnails: true);
                }
            }, TaskScheduler.Default).Unwrap();
        }
        else if (force || NeedsMetadataRefresh(settings) || ShouldRefreshThumbnails(settings, forceThumbnails: false))
        {
            libraryRefreshTask = RunLibraryArtworkRefreshAsync(isExplicitRequest: false, force);
        }
    }

    private async Task RunLibraryArtworkRefreshAsync(bool isExplicitRequest, bool forceThumbnails)
    {
        CancellationTokenSource automationCancellationTokenSource = BeginLibraryAutomation();
        CancellationToken cancellationToken = automationCancellationTokenSource.Token;
        try
        {
            await RefreshLibraryArtworkAsync(isExplicitRequest, forceThumbnails, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "已停止自动刮削和分集剧照刮削任务。";
        }
        finally
        {
            CompleteLibraryAutomation(automationCancellationTokenSource);
        }
    }

    private bool NeedsMetadataRefresh(TmdbSettings settings)
    {
        return allMovies.Any((Movie movie) => NeedsMovieRefresh(movie, settings)) || allTvShows.Any((TvShow show) => NeedsTvShowRefresh(show, settings));
    }

    private async Task RefreshIndexedScanItemArtworkAsync(LibraryScanIndexedItem item, CancellationToken cancellationToken)
    {
        TmdbSettings tmdbSettings = Settings.BuildTmdbSettings();
        await ReloadLibraryAsync();
        if (!tmdbSettings.EnableMetadataEnrichment && !tmdbSettings.EnablePosterDownloads)
        {
            return;
        }
        await libraryRefreshGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            LibraryMetadataEnrichmentSummary libraryMetadataEnrichmentSummary = ((!item.IsTvShow) ? (await libraryMetadataEnricher.EnrichMissingMovieMetadataAsync(item.Id, tmdbSettings, cancellationToken)) : (await libraryMetadataEnricher.EnrichMissingTvShowMetadataAsync(item.Id, tmdbSettings, cancellationToken)));
            LibraryMetadataEnrichmentSummary summary = libraryMetadataEnrichmentSummary;
            cancellationToken.ThrowIfCancellationRequested();
            await ReloadLibraryAsync();
            await RefreshOpenDetailIfNeededAsync();
            if (summary.EncounteredNetworkError)
            {
                StatusMessage = (string.IsNullOrWhiteSpace(summary.ErrorMessage) ? "自动刮削失败：无法连接网络或 TMDB。" : ("自动刮削失败：" + summary.ErrorMessage));
            }
        }
        finally
        {
            libraryRefreshGate.Release();
        }
    }

    private async Task RefreshLibraryArtworkAsync(bool isExplicitRequest, bool forceThumbnails, CancellationToken cancellationToken)
    {
        await libraryRefreshGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            TmdbSettings tmdbSettings = Settings.BuildTmdbSettings();
            bool shouldRefreshMetadata = NeedsMetadataRefresh(tmdbSettings);
            bool shouldRefreshThumbnails = ShouldRefreshThumbnails(tmdbSettings, forceThumbnails);
            if (!shouldRefreshMetadata && !shouldRefreshThumbnails)
            {
                if (isExplicitRequest)
                {
                    StatusMessage = (IsTmdbAutomationDisabled(tmdbSettings) ? "当前已关闭自动刮削和分集剧照下载。" : "当前媒体库已无需要刮削的内容。");
                }
                return;
            }
            StatusMessage = BuildArtworkRefreshProgressMessage(isExplicitRequest, shouldRefreshMetadata, shouldRefreshThumbnails);
            // 刮削顺序跟随首页当前排列顺序（排序选项 + 搜索筛选后的展示顺序），未在当前视图中的条目按当前排序补齐。
            List<(long Id, string Title)> movieQueue = BuildMovieMetadataQueueInHomeOrder(tmdbSettings);
            List<(long Id, string Title)> tvShowMetadataQueue = BuildTvShowMetadataQueueInHomeOrder(tmdbSettings);
            List<(long Id, string Title)> tvShowThumbnailQueue = (List<(long Id, string Title)>)(shouldRefreshThumbnails ? ((IList)BuildTvShowThumbnailQueueInHomeOrder()) : ((IList)new List<(long, string)>()));
            LibraryArtworkRefreshTotals totals = new LibraryArtworkRefreshTotals();
            foreach (var movie in movieQueue)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StatusMessage = "正在刮削电影元数据：" + movie.Title + "...";
                LibraryMetadataEnrichmentSummary metadataSummary = await libraryMetadataEnricher.EnrichMissingMovieMetadataAsync(movie.Id, tmdbSettings, cancellationToken);
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
                StatusMessage = "正在刮削剧集元数据：" + show.Title + "...";
                LibraryMetadataEnrichmentSummary metadataSummary2 = await libraryMetadataEnricher.EnrichMissingTvShowMetadataAsync(show.Id, tmdbSettings, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                totals.AddMetadata(metadataSummary2);
                if (metadataSummary2.HasChanges)
                {
                    await ReloadLibraryAsync();
                    await RefreshOpenDetailIfNeededAsync();
                }
                if (metadataSummary2.EncounteredNetworkError)
                {
                    StatusMessage = ComposeArtworkRefreshStatusMessage(isExplicitRequest, tmdbSettings, totals);
                    return;
                }
            }
            foreach (var show2 in tvShowThumbnailQueue)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StatusMessage = "正在刮削分集剧照：" + show2.Title + "...";
                LibraryThumbnailEnrichmentSummary thumbnailSummary = await libraryThumbnailEnricher.EnrichMissingThumbnailsForTvShowAsync(show2.Id, tmdbSettings, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                totals.AddThumbnails(thumbnailSummary);
                if (thumbnailSummary.HasChanges && IsDetailOpen && currentDetailTvShowId.HasValue && currentDetailTvShowId.Value == show2.Id)
                {
                    await RefreshDetailFilesAsync();
                }
                if (thumbnailSummary.EncounteredNetworkError)
                {
                    StatusMessage = ComposeArtworkRefreshStatusMessage(isExplicitRequest, tmdbSettings, totals);
                    return;
                }
            }
            string finalMessage = ComposeArtworkRefreshStatusMessage(isExplicitRequest, tmdbSettings, totals);
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
            StatusMessage = "自动刮削时发生异常。";
        }
        finally
        {
            libraryRefreshGate.Release();
        }
    }

    private static string ComposeArtworkRefreshStatusMessage(bool isExplicitRequest, TmdbSettings settings, LibraryArtworkRefreshTotals totals)
    {
        if (totals.MetadataNetworkError)
        {
            return string.IsNullOrWhiteSpace(totals.MetadataErrorMessage) ? "自动刮削失败：无法连接网络或 TMDB。" : ("自动刮削失败：" + totals.MetadataErrorMessage);
        }
        if (totals.ThumbnailNetworkError)
        {
            return string.IsNullOrWhiteSpace(totals.ThumbnailErrorMessage) ? "分集剧照刮削失败：无法连接网络或 TMDB。" : ("分集剧照刮削失败：" + totals.ThumbnailErrorMessage);
        }
        if (totals.HasChanges)
        {
            return $"已完成自动刮削：{totals.UpdatedMovieCount} 部电影、{totals.UpdatedTvShowCount} 部剧集，下载 {totals.DownloadedPosterCount} 张海报、{totals.DownloadedThumbnailCount} 张分集剧照。";
        }
        return (!isExplicitRequest) ? string.Empty : (IsTmdbAutomationDisabled(settings) ? "当前已关闭自动刮削和分集剧照下载。" : "扫描完成，但当前没有新的内容需要刮削。");
    }

    private static string BuildArtworkRefreshProgressMessage(bool isExplicitRequest, bool refreshMetadata, bool refreshThumbnails)
    {
        string text = (isExplicitRequest ? "正在执行" : "正在后台执行");
        if (refreshMetadata & refreshThumbnails)
        {
            return text + "元数据、海报和分集剧照刮削...";
        }
        if (refreshMetadata)
        {
            return text + "元数据和海报刮削...";
        }
        return text + "分集剧照刮削...";
    }

    private static bool NeedsMovieRefresh(Movie movie, TmdbSettings settings)
    {
        if (movie.IsLocked)
        {
            return false;
        }
        bool flag = settings.EnablePosterDownloads && !IsUsableLocalPoster(movie.PosterPath);
        bool flag2 = settings.EnableMetadataEnrichment && (NeedsMetadataLanguageRefresh(movie.MetadataLanguage, settings) || string.IsNullOrWhiteSpace(movie.ReleaseDate) || string.IsNullOrWhiteSpace(movie.Overview) || string.IsNullOrWhiteSpace(movie.ProductionCountryCodes) || !movie.VoteAverage.HasValue || (movie.TmdbId.HasValue && (string.IsNullOrWhiteSpace(movie.Director) || string.IsNullOrWhiteSpace(movie.MainCast))));
        return flag | flag2;
    }

    private static bool NeedsTvShowRefresh(TvShow show, TmdbSettings settings)
    {
        if (show.IsLocked)
        {
            return false;
        }
        bool flag = settings.EnablePosterDownloads && !IsUsableLocalPoster(show.PosterPath);
        bool flag2 = settings.EnableMetadataEnrichment && (NeedsMetadataLanguageRefresh(show.MetadataLanguage, settings) || string.IsNullOrWhiteSpace(show.FirstAirDate) || string.IsNullOrWhiteSpace(show.Overview) || string.IsNullOrWhiteSpace(show.ProductionCountryCodes) || !show.VoteAverage.HasValue || (!string.IsNullOrWhiteSpace(show.MetadataLanguage) && (string.IsNullOrWhiteSpace(show.Director) || string.IsNullOrWhiteSpace(show.MainCast))));
        return flag | flag2;
    }

    private static bool NeedsMetadataLanguageRefresh(string? metadataLanguage, TmdbSettings settings)
    {
        string text = ResolveDesiredMetadataLanguage(settings.Language);
        return !string.IsNullOrWhiteSpace(text) && !string.Equals(NormalizeMetadataLanguage(metadataLanguage), text, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDesiredMetadataLanguage(string? language)
    {
        string text = language?.Trim();
        return string.IsNullOrWhiteSpace(text) ? "zh-CN" : text;
    }

    private static string? NormalizeMetadataLanguage(string? language)
    {
        string text = language?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private bool ShouldRefreshThumbnails(TmdbSettings settings, bool forceThumbnails)
    {
        return (settings.EnableEpisodeThumbnailDownloads && allTvShows.Count > 0) & forceThumbnails;
    }

    private List<(long Id, string Title)> BuildTvShowThumbnailQueueInHomeOrder()
    {
        Dictionary<long, TvShow> dictionary = allTvShows.ToDictionary((TvShow show) => show.Id);
        HashSet<long> hashSet = new HashSet<long>();
        List<(long, string)> list = new List<(long, string)>();
        foreach (LibraryPosterItem libraryItem in LibraryItems)
        {
            if (libraryItem.TvShowId.HasValue && hashSet.Add(libraryItem.TvShowId.Value) && dictionary.TryGetValue(libraryItem.TvShowId.Value, out var value))
            {
                list.Add((value.Id, value.Title));
            }
        }
        foreach (TvShow item in allTvShows.OrderBy<TvShow, string>((TvShow show) => show.Title, StringComparer.OrdinalIgnoreCase))
        {
            if (hashSet.Add(item.Id))
            {
                list.Add((item.Id, item.Title));
            }
        }
        return list;
    }

    private List<(long Id, string Title)> BuildMovieMetadataQueueInHomeOrder(TmdbSettings settings)
    {
        Dictionary<long, Movie> dictionary = allMovies
            .Where((Movie movie) => movie.Id.HasValue)
            .ToDictionary((Movie movie) => movie.Id!.Value);
        HashSet<long> seen = new HashSet<long>();
        List<(long, string)> list = new List<(long, string)>();
        foreach (LibraryPosterItem libraryItem in LibraryItems)
        {
            long? movieId = libraryItem.MovieId;
            if (movieId.HasValue
                && seen.Add(movieId.Value)
                && dictionary.TryGetValue(movieId.Value, out Movie? value)
                && NeedsMovieRefresh(value, settings))
            {
                list.Add((value.Id!.Value, value.Title));
            }
        }
        foreach (LibraryPosterItem orderedItem in SortLibraryItems(allLibraryItems))
        {
            long? movieId = orderedItem.MovieId;
            if (movieId.HasValue
                && seen.Add(movieId.Value)
                && dictionary.TryGetValue(movieId.Value, out Movie? value)
                && NeedsMovieRefresh(value, settings))
            {
                list.Add((value.Id!.Value, value.Title));
            }
        }
        return list;
    }

    private List<(long Id, string Title)> BuildTvShowMetadataQueueInHomeOrder(TmdbSettings settings)
    {
        Dictionary<long, TvShow> dictionary = allTvShows.ToDictionary((TvShow show) => show.Id);
        HashSet<long> seen = new HashSet<long>();
        List<(long, string)> list = new List<(long, string)>();
        foreach (LibraryPosterItem libraryItem in LibraryItems)
        {
            long? tvShowId = libraryItem.TvShowId;
            if (tvShowId.HasValue
                && seen.Add(tvShowId.Value)
                && dictionary.TryGetValue(tvShowId.Value, out TvShow value)
                && NeedsTvShowRefresh(value, settings))
            {
                list.Add((value.Id, value.Title));
            }
        }
        foreach (LibraryPosterItem orderedItem in SortLibraryItems(allLibraryItems))
        {
            long? tvShowId = orderedItem.TvShowId;
            if (tvShowId.HasValue
                && seen.Add(tvShowId.Value)
                && dictionary.TryGetValue(tvShowId.Value, out TvShow value)
                && NeedsTvShowRefresh(value, settings))
            {
                list.Add((value.Id, value.Title));
            }
        }
        return list;
    }

    private static bool IsUsableLocalPoster(string? posterPath)
    {
        return !string.IsNullOrWhiteSpace(posterPath) && Path.IsPathRooted(posterPath) && File.Exists(posterPath);
    }

    private static bool IsTmdbAutomationDisabled(TmdbSettings settings)
    {
        return !settings.EnableMetadataEnrichment && !settings.EnablePosterDownloads && !settings.EnableEpisodeThumbnailDownloads;
    }

    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        ApplyPlaybackPreferencesToPlayer();
        OnPropertyChanged("ShowMediaSourceRealPath");
        QueueLibraryRefreshIfNeeded(force: true);
    }

    private void OnLuckyStunAddressUpdated(object? sender, EventArgs e)
    {
        QueueLibraryRefreshIfNeeded(force: true);
    }

    private void ApplyPlaybackPreferencesToPlayer()
    {
        Player.ConfigureDefaultTracks(Settings.DefaultAudioTrackMode, Settings.DefaultSubtitleTrack, ResolveSmartAudioTrackMode());
    }

    private string? ResolveSmartAudioTrackMode()
    {
        if (!string.Equals(Settings.DefaultAudioTrackMode, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string productionCountryCodes = ((!currentDetailMovieId.HasValue) ? ((!currentDetailTvShowId.HasValue) ? null : allTvShows.FirstOrDefault((TvShow show) => show.Id == currentDetailTvShowId.Value)?.ProductionCountryCodes) : allMovies.FirstOrDefault((Movie movie) => movie.Id == currentDetailMovieId.Value)?.ProductionCountryCodes);
        string originalLanguage = ((!currentDetailMovieId.HasValue) ? ((!currentDetailTvShowId.HasValue) ? null : allTvShows.FirstOrDefault((TvShow show) => show.Id == currentDetailTvShowId.Value)?.OriginalLanguage) : allMovies.FirstOrDefault((Movie movie) => movie.Id == currentDetailMovieId.Value)?.OriginalLanguage);
        return ResolveAudioTrackModeFromTmdbMetadata(productionCountryCodes, originalLanguage);
    }

    private static string? ResolveAudioTrackModeFromTmdbMetadata(string? productionCountryCodes, string? originalLanguage)
    {
        foreach (string item in SplitMetadataCodes(productionCountryCodes))
        {
            string text = ResolveAudioTrackModeFromCountryCode(item);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }
        return ResolveAudioTrackModeFromLanguage(originalLanguage);
    }

    private static string? ResolveAudioTrackModeFromCountryCode(string countryCode)
    {
        string text = countryCode.Trim().ToUpperInvariant();
        if (1 == 0)
        {
        }
        string result;
        switch (text)
        {
        case "CN":
        case "HK":
        case "MO":
        case "TW":
        case "SG":
            result = "chi";
            break;
        case "US":
        case "GB":
        case "AU":
        case "CA":
        case "NZ":
        case "IE":
            result = "eng";
            break;
        case "JP":
            result = "jpn";
            break;
        default:
            result = null;
            break;
        }
        if (1 == 0)
        {
        }
        return result;
    }

    private static string? ResolveAudioTrackModeFromLanguage(string? language)
    {
        string text = language?.Trim().ToLowerInvariant();
        if (1 == 0)
        {
        }
        string result;
        switch (text)
        {
        case "zh":
        case "cn":
        case "chi":
        case "zho":
        case "cmn":
        case "yue":
            result = "chi";
            break;
        case "en":
        case "eng":
            result = "eng";
            break;
        case "ja":
        case "jpn":
            result = "jpn";
            break;
        default:
            result = null;
            break;
        }
        if (1 == 0)
        {
        }
        return result;
    }

    private static IEnumerable<string> SplitMetadataCodes(string? codes)
    {
        return string.IsNullOrWhiteSpace(codes) ? ((IEnumerable<string>)Array.Empty<string>()) : ((IEnumerable<string>)codes.Split(new char[4] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private async Task RefreshOpenDetailIfNeededAsync()
    {
        if (!IsDetailOpen)
        {
            return;
        }
        if (currentDetailMovieId.HasValue)
        {
            Movie movie = allMovies.FirstOrDefault((Movie x) => x.Id == currentDetailMovieId);
            if (movie != null)
            {
                await OpenDetailInternalAsync(movie.Title, movie.ReleaseDate ?? "电影", movie.Overview ?? "暂无简介。", movie.PosterPath, movie.VoteAverage, "电影", movie.IsLocked, isSeries: false, preserveMetadataCandidates: true, () => videoFileRepository.GetByMovieAsync(movie.Id.GetValueOrDefault()));
                return;
            }
        }
        if (!currentDetailTvShowId.HasValue)
        {
            return;
        }
        TvShow show = allTvShows.FirstOrDefault((TvShow x) => x.Id == currentDetailTvShowId.Value);
        if (show != null)
        {
            await OpenDetailInternalAsync(show.Title, FormatTvShowSubtitle(show.FirstAirDate), show.Overview ?? "这里会按季整理所有分集，便于还原 mac 版的主集选择和继续观看逻辑。", show.PosterPath, show.VoteAverage, "剧集", show.IsLocked, isSeries: true, preserveMetadataCandidates: true, () => videoFileRepository.GetByTvShowAsync(show.Id));
        }
    }

    private void StartPlaybackProgressSync()
    {
        if (playbackProgressSyncCancellationTokenSource == null && HasActivePlaybackSession())
        {
            _ = RunPlaybackProgressSyncAsync((playbackProgressSyncCancellationTokenSource = new CancellationTokenSource()).Token);
        }
    }

    private async Task RunPlaybackProgressSyncAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromSeconds(5L));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (HasActivePlaybackSession() && !string.IsNullOrWhiteSpace(currentPlayingVideoId) && Player.IsPlaying)
                {
                    double progress = Math.Max(Player.CurrentPositionSeconds, 0.0);
                    double duration = Math.Max(Player.DurationSeconds, 0.0);
                    if (ShouldSyncPlaybackProgress(progress, duration))
                    {
                        await PersistPlaybackStateAsync(currentPlayingVideoId, progress, duration, refreshCollections: false);
                        lastSyncedPlaybackPositionSeconds = progress;
                        lastSyncedPlaybackDurationSeconds = duration;
                    }
                }
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
        return currentPlaybackMode != PlaybackMode.None && !string.IsNullOrWhiteSpace(currentPlayingVideoId);
    }

    private bool ShouldSyncPlaybackProgress(double progress, double duration)
    {
        if (progress <= 5.0 && duration <= 0.0)
        {
            return false;
        }
        if (double.IsNaN(lastSyncedPlaybackPositionSeconds))
        {
            return true;
        }
        if (duration > 0.0 && progress >= duration * 0.95 && lastSyncedPlaybackPositionSeconds < duration * 0.95)
        {
            return true;
        }
        return Math.Abs(progress - lastSyncedPlaybackPositionSeconds) >= 10.0 || Math.Abs(duration - lastSyncedPlaybackDurationSeconds) >= 1.0;
    }

    private static double NormalizePersistedProgress(double progress, double duration)
    {
        double num = Math.Max(progress, 0.0);
        if (duration > 0.0 && num >= duration - 2.0)
        {
            return duration;
        }
        return num;
    }

    private static double? GetPlaybackStartPosition(LibraryVideoItem video)
    {
        if (!video.HasProgress || video.IsWatched)
        {
            return null;
        }
        double num = Math.Max(video.PlayProgress, 0.0);
        if (video.Duration > 0.0)
        {
            double num2 = video.Duration - num;
            if (num2 <= 5.0)
            {
                return null;
            }
            num = Math.Min(num, Math.Max(video.Duration - 5.0, 0.0));
        }
        return (num > 0.0) ? new double?(num) : ((double?)null);
    }

    partial void OnIsNetworkPasswordVisibleChanged(bool value)
    {
        OnPropertyChanged("NetworkPasswordVisibilityGlyph");
    }

    partial void OnPendingMediaServerProtocolTypeChanged(string value)
    {
        NotifyMediaServerProtocolDependentProperties();
    }

    partial void OnSelectedMediaServerProtocolOptionChanged(SettingsOptionItem? value)
    {
        if ((object)value != null)
        {
            PendingMediaServerProtocolType = value.Value;
            ApplyMediaServerProtocolDefaults(value.Value);
            MediaServerStatus = GetMediaServerConnectionHint(value.Value);
        }
    }

    partial void OnIsMediaServerTokenVisibleChanged(bool value)
    {
        OnPropertyChanged("MediaServerTokenVisibilityGlyph");
    }

    partial void OnIsLuckyStunPasswordVisibleChanged(bool value)
    {
        OnPropertyChanged("LuckyStunPasswordVisibilityGlyph");
    }

    partial void OnIsPendingLuckyStunPasswordVisibleChanged(bool value)
    {
        OnPropertyChanged("PendingLuckyStunPasswordVisibilityGlyph");
    }

    partial void OnIsPlexAuthorizationInProgressChanged(bool value)
    {
        OnPropertyChanged("PlexAuthorizeButtonText");
        AuthorizePlexCommand.NotifyCanExecuteChanged();
    }

    partial void OnOpenFolderKeyChanged(string? value)
    {
        openFolderBioGeneration++;
        OnPropertyChanged("IsHomeFolderOpen");
        OnPropertyChanged("OpenFolderDisplayName");
        OnPropertyChanged("ShowContinueWatching");
    }

    partial void OnHomeCategoryModeChanged(string value)
    {
        OnPropertyChanged("IsHomeCategoryNoneSelected");
        OnPropertyChanged("IsHomeCategoryDirectorSelected");
        OnPropertyChanged("IsHomeCategoryActorSelected");
        OnPropertyChanged("IsHomeCategoryModeActive");
        OnPropertyChanged("SortDirectionToolTip");
    }

    partial void OnIsBusyChanged(bool value)
    {
        NotifyLibraryViewFlags();
    }

    partial void OnIsLibraryScanInProgressChanged(bool value)
    {
        OnPropertyChanged("HasStatusMessage");
        OnPropertyChanged("HomeStatusMessage");
        NotifyLibraryViewFlags();
    }

    partial void OnStatusMessageChanged(string value)
    {
        OnPropertyChanged("HasStatusMessage");
        OnPropertyChanged("HomeStatusMessage");
    }

    partial void OnLastScanSummaryChanged(LibraryScanSummary? value)
    {
        OnPropertyChanged("HasLastScanSummary");
        OnPropertyChanged("HasLastScanDiagnostics");
        OnPropertyChanged("LastScanDiagnostics");
        OnPropertyChanged("LastScanOverviewText");
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilters();
    }

    partial void OnSelectedSortOptionChanged(LibrarySortOption value)
    {
        LibrarySortOptionItem librarySortOptionItem = SortOptions.FirstOrDefault((LibrarySortOptionItem option) => option.Value == value) ?? SortOptions[0];
        if (SelectedSortOptionItem != librarySortOptionItem)
        {
            SelectedSortOptionItem = librarySortOptionItem;
        }
        RefreshSortOptionSelectionStates();
        ApplyFilters();
        PersistLibraryViewPreferencesIfNeeded();
    }

    partial void OnSelectedSortOptionItemChanged(LibrarySortOptionItem value)
    {
        if (value != null)
        {
            SelectedSortOption = value.Value;
            RefreshSortOptionSelectionStates();
        }
    }

    partial void OnIsSortDescendingChanged(bool value)
    {
        OnPropertyChanged("IsSortAscending");
        OnPropertyChanged("SortDirectionLabel");
        OnPropertyChanged("SortDirectionToolTip");
        ApplyFilters();
        PersistLibraryViewPreferencesIfNeeded();
    }

    partial void OnIsDetailSeriesChanged(bool value)
    {
        OnPropertyChanged("HasSeasonTabs");
        OnPropertyChanged("CanShowDetailVersions");
        OpenEpisodeEditCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasMultipleDetailVersionsChanged(bool value)
    {
        OnPropertyChanged("CanShowDetailVersions");
    }

    partial void OnDetailPrimaryFileChanged(LibraryVideoItem? value)
    {
        RefreshDetailHeaderState();
        OnCommandStateChanged();
    }

    partial void OnSelectedSeasonChanged(int value)
    {
        OnPropertyChanged("SelectedSeasonOption");
        ApplyDetailFilter();
    }
}
