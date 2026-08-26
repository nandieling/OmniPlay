using System.Reflection;
using System.Runtime.InteropServices;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Core.Settings;

namespace OmniPlay.Core.ViewModels.Settings;

public partial class SettingsViewModel : ObservableObject
{
    private const string DefaultTmdbLanguage = TmdbSettings.DefaultLanguage;
    private const string TmdbWebsiteUrl = "https://www.themoviedb.org";
    private const string TmdbFaqUrl = "https://developer.themoviedb.org/docs/faq";
    private const string TmdbApiTermsUrl = "https://www.themoviedb.org/api-terms-of-use";
    private const string GitHubRepositoryUrl = "https://github.com/nandieling/OmniPlay";
    private const string GitHubLatestReleaseApiUrl = "https://api.github.com/repos/nandieling/OmniPlay/releases/latest";
    private const string GitHubLatestReleaseUrl = "https://github.com/nandieling/OmniPlay/releases/latest";
    private static readonly TimeSpan AutomaticUpdateCheckInterval = TimeSpan.FromHours(24);

    private static readonly IReadOnlyList<AppCreditLinkItem> BuiltInCreditLinks =
    [
        new("The Movie Database (TMDB)", "影视元数据、海报和归属信息来源。", "服务条款 / Attribution", "https://www.themoviedb.org", "打开官网"),
        new("Avalonia UI", "Windows 桌面界面框架。", "MIT", "https://avaloniaui.net", "打开官网"),
        new("CommunityToolkit.Mvvm", "MVVM 命令、状态和属性通知。", "MIT", "https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/", "打开文档"),
        new("Dapper", "轻量数据访问层。", "Apache-2.0", "https://github.com/DapperLib/Dapper", "打开项目"),
        new("Microsoft.Data.Sqlite", "本地媒体库数据库访问。", "MIT", "https://learn.microsoft.com/dotnet/standard/data/sqlite/", "打开文档"),
        new("Microsoft.Extensions", "依赖注入和日志抽象。", "MIT", "https://github.com/dotnet/runtime", "打开项目"),
        new("SQLitePCLRaw / SQLite", "SQLite 原生运行时封装。", "Apache-2.0 / SQLite public domain", "https://github.com/ericsink/SQLitePCL.raw", "打开项目"),
        new("SkiaSharp / HarfBuzzSharp", "Avalonia 图形和字体渲染依赖。", "MIT", "https://github.com/mono/SkiaSharp", "打开项目"),
        new("MicroCom.Runtime", "Avalonia COM 互操作运行时。", "MIT", "https://github.com/AvaloniaUI/Avalonia", "打开项目"),
        new("Tmds.DBus.Protocol", "桌面平台集成依赖。", "MIT", "https://github.com/tmds/Tmds.DBus", "打开项目"),
        new("libmpv / mpv", "视频播放内核。", "见 THIRD_PARTY_NOTICES", "https://mpv.io", "打开官网"),
        new("Serilog", "日志记录组件。", "Apache-2.0", "https://serilog.net", "打开官网")
    ];

    private static readonly IReadOnlyList<SettingsOptionItem> TmdbLanguageOptionItems =
    [
        new("zh-CN", "简体中文"),
        new("en-US", "English")
    ];

    private static readonly IReadOnlyList<SettingsOptionItem> DefaultAudioTrackOptionItems =
    [
        new(PlaybackPreferenceSettings.DefaultAudioSmart, "智能匹配（制片国家语言）"),
        new(PlaybackPreferenceSettings.AudioChinese, "中文"),
        new(PlaybackPreferenceSettings.AudioEnglish, "英语"),
        new(PlaybackPreferenceSettings.AudioJapanese, "日语")
    ];

    private static readonly IReadOnlyList<SettingsOptionItem> DefaultSubtitleTrackOptionItems =
    [
        new(PlaybackPreferenceSettings.DefaultSubtitleChinese, "中文"),
        new(PlaybackPreferenceSettings.SubtitleEnglish, "英语")
    ];

    private readonly ISettingsService settingsService;
    private readonly ITmdbConnectionTester tmdbConnectionTester;
    private readonly IExternalLinkOpener externalLinkOpener;
    private readonly ILuckyStunClient? luckyStunClient;
    private readonly IMediaSourceRepository? mediaSourceRepository;
    private readonly string appVersionText;
    private readonly string runtimeText;
    private readonly string platformText;
    private readonly string thirdPartyNoticesPath;
    private bool loaded;
    private bool suppressOptionSelectionChange;
    private DateTimeOffset? lastUpdateCheckAt;
    private DateTimeOffset? lastLuckyStunUpdatedAt;
    private string luckyStunSelectedRuleId = string.Empty;
    private string luckyStunSelectedRuleName = string.Empty;
    private CancellationTokenSource? luckyStunAutoUpdateCancellationTokenSource;
    private Task? luckyStunAutoUpdateTask;
    private readonly SemaphoreSlim luckyStunUpdateGate = new(1, 1);

    public SettingsViewModel(
        ISettingsService settingsService,
        ITmdbConnectionTester tmdbConnectionTester,
        IExternalLinkOpener? externalLinkOpener = null,
        ILuckyStunClient? luckyStunClient = null,
        IMediaSourceRepository? mediaSourceRepository = null)
    {
        this.settingsService = settingsService;
        this.tmdbConnectionTester = tmdbConnectionTester;
        this.externalLinkOpener = externalLinkOpener ?? NullExternalLinkOpener.Instance;
        this.luckyStunClient = luckyStunClient;
        this.mediaSourceRepository = mediaSourceRepository;

        appVersionText = ResolveAppVersion();
        runtimeText = RuntimeInformation.FrameworkDescription;
        platformText = $"{RuntimeInformation.OSDescription.Trim()} / {RuntimeInformation.ProcessArchitecture}";
        thirdPartyNoticesPath = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.md");

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
        TestTmdbConnectionCommand = new AsyncRelayCommand(TestTmdbConnectionAsync, () => !IsBusy);
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync, () => !IsBusy);
        InstallLatestUpdateCommand = new AsyncRelayCommand(InstallLatestUpdateAsync, () => !IsBusy);
        RefreshLuckyStunCommand = new AsyncRelayCommand(RefreshLuckyStunAsync, () => !IsBusy);
        OpenAboutCommand = new RelayCommand(OpenAbout);
        CloseAboutCommand = new RelayCommand(CloseAbout);
        OpenTmdbWebsiteCommand = new RelayCommand(OpenTmdbWebsite);
        OpenTmdbFaqCommand = new RelayCommand(OpenTmdbFaq);
        OpenTmdbApiTermsCommand = new RelayCommand(OpenTmdbApiTerms);
        OpenCreditLinkCommand = new RelayCommand<AppCreditLinkItem?>(OpenCreditLink);
        OpenThirdPartyNoticesCommand = new RelayCommand(OpenThirdPartyNotices);
        OpenSettingsDirectoryCommand = new RelayCommand(OpenSettingsDirectory);
        OpenGitHubRepositoryCommand = new RelayCommand(OpenGitHubRepository);

        SyncSelectedOptions();
    }

    public IAsyncRelayCommand SaveCommand { get; }

    public IAsyncRelayCommand TestTmdbConnectionCommand { get; }

    public IAsyncRelayCommand CheckForUpdatesCommand { get; }

    public IAsyncRelayCommand InstallLatestUpdateCommand { get; }

    public IAsyncRelayCommand RefreshLuckyStunCommand { get; }

    public IRelayCommand OpenAboutCommand { get; }

    public IRelayCommand CloseAboutCommand { get; }

    public IRelayCommand OpenTmdbWebsiteCommand { get; }

    public IRelayCommand OpenTmdbFaqCommand { get; }

    public IRelayCommand OpenTmdbApiTermsCommand { get; }

    public IRelayCommand<AppCreditLinkItem?> OpenCreditLinkCommand { get; }

    public IRelayCommand OpenThirdPartyNoticesCommand { get; }

    public IRelayCommand OpenSettingsDirectoryCommand { get; }

    public IRelayCommand OpenGitHubRepositoryCommand { get; }

    public IReadOnlyList<AppCreditLinkItem> CreditLinks => BuiltInCreditLinks;

    public IReadOnlyList<SettingsOptionItem> TmdbLanguageOptions => TmdbLanguageOptionItems;

    public IReadOnlyList<SettingsOptionItem> DefaultAudioTrackOptions => DefaultAudioTrackOptionItems;

    public IReadOnlyList<SettingsOptionItem> DefaultSubtitleTrackOptions => DefaultSubtitleTrackOptionItems;

    public string AppVersionText => appVersionText;

    public string RuntimeText => runtimeText;

    public string PlatformText => platformText;

    public string SettingsDirectoryPath => settingsService.SettingsDirectory;

    public string ThirdPartyNoticesPath => thirdPartyNoticesPath;

    public event EventHandler? SettingsSaved;

    public event EventHandler? LuckyStunAddressUpdated;

    [ObservableProperty]
    private bool autoScanOnStartup = true;

    [ObservableProperty]
    private bool autoCheckUpdatesOnStartup = true;

    [ObservableProperty]
    private bool showMediaSourceRealPath = true;

    [ObservableProperty]
    private string librarySortOption = LibraryViewSettings.SortOptionTitle;

    [ObservableProperty]
    private bool librarySortDescending;

    [ObservableProperty]
    private string offlineCacheDirectory = string.Empty;

    [ObservableProperty]
    private bool enableLocalMetadataImport;

    [ObservableProperty]
    private bool enableLocalMetadataExport;

    [ObservableProperty]
    private bool enableTmdbMetadataEnrichment = true;

    [ObservableProperty]
    private bool enableTmdbPosterDownloads = true;

    [ObservableProperty]
    private bool enableTmdbEpisodeThumbnailDownloads = true;

    [ObservableProperty]
    private string customTmdbCredential = string.Empty;

    [ObservableProperty]
    private string customTmdbApiKey = string.Empty;

    [ObservableProperty]
    private string customTmdbAccessToken = string.Empty;

    [ObservableProperty]
    private string tmdbLanguage = DefaultTmdbLanguage;

    [ObservableProperty]
    private string defaultAudioTrackMode = PlaybackPreferenceSettings.DefaultAudioSmart;

    [ObservableProperty]
    private string defaultSubtitleTrack = PlaybackPreferenceSettings.DefaultSubtitleChinese;

    [ObservableProperty]
    private string luckyStunManagementUrl = string.Empty;

    [ObservableProperty]
    private string luckyStunUsername = string.Empty;

    [ObservableProperty]
    private string luckyStunPassword = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<LuckyStunRule> luckyStunRules = [];

    [ObservableProperty]
    private LuckyStunRule? selectedLuckyStunRule;

    [ObservableProperty]
    private bool luckyStunAutoUpdate;

    [ObservableProperty]
    private int luckyStunUpdateIntervalMinutes = 30;

    [ObservableProperty]
    private string luckyStunStatusMessage = "尚未检测 Lucky STUN。";

    [ObservableProperty]
    private SettingsOptionItem? selectedTmdbLanguageOption;

    [ObservableProperty]
    private SettingsOptionItem? selectedDefaultAudioTrackOption;

    [ObservableProperty]
    private SettingsOptionItem? selectedDefaultSubtitleTrackOption;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private string tmdbConnectionStatusMessage = "尚未测试 TMDB 连通性。";

    [ObservableProperty]
    private string updateStatusMessage = "尚未检查更新。";

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isAboutPopupOpen;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (loaded)
        {
            return;
        }

        var settings = await settingsService.LoadAsync(cancellationToken);
        Apply(settings);
        loaded = true;
        StartLuckyStunAutoUpdateLoop();
    }

    public AppSettings BuildAppSettings()
    {
        return new AppSettings
        {
            AutoScanOnStartup = AutoScanOnStartup,
            AutoCheckUpdatesOnStartup = AutoCheckUpdatesOnStartup,
            LastUpdateCheckAt = lastUpdateCheckAt,
            ShowMediaSourceRealPath = ShowMediaSourceRealPath,
            LibraryView = new LibraryViewSettings
            {
                SortOption = NormalizeLibrarySortOption(LibrarySortOption),
                SortDescending = LibrarySortDescending
            },
            OfflineCacheDirectory = OfflineCacheDirectory.Trim(),
            Tmdb = BuildTmdbSettings(),
            LocalMetadata = BuildLocalMetadataSettings(),
            Playback = BuildPlaybackPreferenceSettings()
            ,LuckyStun = BuildLuckyStunSettings()
        };
    }

    public LuckyStunSettings BuildLuckyStunSettings()
    {
        var interval = Math.Clamp(LuckyStunUpdateIntervalMinutes, 5, 1440);
        LuckyStunUpdateIntervalMinutes = interval;
        var selected = SelectedLuckyStunRule;
        return new LuckyStunSettings
        {
            ManagementUrl = LuckyStunManagementUrl.Trim(),
            Username = LuckyStunUsername.Trim(),
            Password = LuckyStunPassword,
            SelectedRuleId = selected?.Id ?? luckyStunSelectedRuleId,
            SelectedRuleName = selected?.Name ?? luckyStunSelectedRuleName,
            AutoUpdate = LuckyStunAutoUpdate,
            UpdateIntervalMinutes = interval,
            LastUpdatedAt = lastLuckyStunUpdatedAt
        };
    }

    public LocalMetadataSettings BuildLocalMetadataSettings()
    {
        return new LocalMetadataSettings
        {
            EnableLocalMetadataImport = EnableLocalMetadataImport,
            EnableLocalMetadataExport = EnableLocalMetadataExport
        };
    }

    public TmdbSettings BuildTmdbSettings()
    {
        var (normalizedCustomApiKey, normalizedCustomAccessToken) = ResolveCustomTmdbCredentials();
        var normalizedLanguage = NormalizeTmdbLanguage(TmdbLanguage);

        ApplyNormalizedText(CustomTmdbApiKey, normalizedCustomApiKey, value => CustomTmdbApiKey = value);
        ApplyNormalizedText(CustomTmdbAccessToken, normalizedCustomAccessToken, value => CustomTmdbAccessToken = value);
        ApplyNormalizedText(CustomTmdbCredential, FormatCustomTmdbCredential(normalizedCustomApiKey, normalizedCustomAccessToken), value => CustomTmdbCredential = value);
        ApplyNormalizedText(TmdbLanguage, normalizedLanguage, value => TmdbLanguage = value);
        SyncSelectedOptions();

        return new TmdbSettings
        {
            EnableMetadataEnrichment = true,
            EnablePosterDownloads = true,
            EnableEpisodeThumbnailDownloads = true,
            CustomApiKey = normalizedCustomApiKey,
            CustomAccessToken = normalizedCustomAccessToken,
            Language = normalizedLanguage
        };
    }

    public PlaybackPreferenceSettings BuildPlaybackPreferenceSettings()
    {
        var normalizedAudioMode = NormalizeDefaultAudioTrack(DefaultAudioTrackMode);
        var normalizedSubtitleTrack = NormalizeDefaultSubtitleTrack(DefaultSubtitleTrack);

        ApplyNormalizedText(DefaultAudioTrackMode, normalizedAudioMode, value => DefaultAudioTrackMode = value);
        ApplyNormalizedText(DefaultSubtitleTrack, normalizedSubtitleTrack, value => DefaultSubtitleTrack = value);
        SyncSelectedOptions();

        return new PlaybackPreferenceSettings
        {
            DefaultAudioTrack = normalizedAudioMode,
            DefaultSubtitleTrack = normalizedSubtitleTrack
        };
    }

    private async Task SaveAsync()
    {
        IsBusy = true;
        NotifyCommandStateChanged();

        try
        {
            var settings = BuildAppSettings();
            await settingsService.SaveAsync(settings);
            Apply(settings);
            StartLuckyStunAutoUpdateLoop();

            loaded = true;
            StatusMessage = "设置已保存。";
            SettingsSaved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存设置失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyCommandStateChanged();
        }
    }

    public async Task SaveLibraryViewPreferencesAsync(
        string sortOption,
        bool sortDescending,
        CancellationToken cancellationToken = default)
    {
        LibrarySortOption = NormalizeLibrarySortOption(sortOption);
        LibrarySortDescending = sortDescending;
        await settingsService.SaveAsync(BuildAppSettings(), cancellationToken);
        loaded = true;
    }

    private async Task TestTmdbConnectionAsync()
    {
        IsBusy = true;
        NotifyCommandStateChanged();

        try
        {
            TmdbConnectionStatusMessage = "正在连接 TMDB...";
            var result = await tmdbConnectionTester.TestConnectionAsync(BuildTmdbSettings());
            TmdbConnectionStatusMessage = result.Message;
        }
        finally
        {
            IsBusy = false;
            NotifyCommandStateChanged();
        }
    }

    private async Task RefreshLuckyStunAsync()
    {
        await RefreshLuckyStunCoreAsync(isAutomatic: false, CancellationToken.None);
    }

    public void StartLuckyStunAutoUpdateLoop()
    {
        luckyStunAutoUpdateCancellationTokenSource?.Cancel();
        luckyStunAutoUpdateCancellationTokenSource = null;
        luckyStunAutoUpdateTask = null;

        if (!LuckyStunAutoUpdate || string.IsNullOrWhiteSpace(LuckyStunManagementUrl))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        luckyStunAutoUpdateCancellationTokenSource = cancellation;
        luckyStunAutoUpdateTask = RunLuckyStunAutoUpdateLoopAsync(cancellation.Token);
    }

    private async Task RunLuckyStunAutoUpdateLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var due = !lastLuckyStunUpdatedAt.HasValue ||
                          DateTimeOffset.UtcNow - lastLuckyStunUpdatedAt.Value >= TimeSpan.FromMinutes(Math.Clamp(LuckyStunUpdateIntervalMinutes, 5, 1440));
                if (due)
                {
                    await RefreshLuckyStunCoreAsync(isAutomatic: true, cancellationToken);
                }

                await Task.Delay(TimeSpan.FromMinutes(Math.Clamp(LuckyStunUpdateIntervalMinutes, 5, 1440)), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task RefreshLuckyStunCoreAsync(bool isAutomatic, CancellationToken cancellationToken)
    {
        if (luckyStunClient is null || mediaSourceRepository is null || string.IsNullOrWhiteSpace(LuckyStunManagementUrl))
        {
            if (!isAutomatic)
            {
                LuckyStunStatusMessage = "Lucky STUN 服务尚未初始化，或管理地址为空。";
            }

            return;
        }

        if (!await luckyStunUpdateGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        IsBusy = true;
        NotifyCommandStateChanged();
        try
        {
            LuckyStunStatusMessage = isAutomatic ? "正在自动更新 Lucky STUN 地址..." : "正在登录并读取 Lucky STUN 规则...";
            var snapshot = await luckyStunClient.FetchAsync(BuildLuckyStunSettings(), cancellationToken);
            LuckyStunRules = snapshot.Rules;
            SelectedLuckyStunRule = snapshot.SelectedRule;
            if (snapshot.SelectedRule is null)
            {
                LuckyStunStatusMessage = snapshot.Message;
                return;
            }

            luckyStunSelectedRuleId = snapshot.SelectedRule.Id;
            luckyStunSelectedRuleName = snapshot.SelectedRule.Name;

            var normalizedAddress = NormalizeDockerBaseUrl(snapshot.SelectedRule.Address);
            var updatedCount = 0;
            var allSources = await mediaSourceRepository.GetAllAsync(cancellationToken);
            foreach (var source in allSources)
            {
                if (source.Id is null)
                {
                    continue;
                }

                if (source.ProtocolKind == MediaSourceProtocol.OmniPlayDocker)
                {
                    if (string.Equals(source.GetNormalizedBaseUrl(), normalizedAddress, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    source.BaseUrl = normalizedAddress;
                    if (await mediaSourceRepository.UpdateAsync(source, cancellationToken))
                    {
                        updatedCount++;
                    }
                    continue;
                }

                var addressConfiguration = source.GetAddressConfiguration();
                var changed = false;
                foreach (var external in addressConfiguration.ExternalAddresses)
                {
                    var ruleMatches = string.IsNullOrWhiteSpace(external.LuckyRuleId)
                        ? string.IsNullOrWhiteSpace(external.LuckyRuleName) ||
                          string.Equals(external.LuckyRuleName, snapshot.SelectedRule.Name, StringComparison.OrdinalIgnoreCase)
                        : string.Equals(external.LuckyRuleId, snapshot.SelectedRule.Id, StringComparison.OrdinalIgnoreCase);
                    if (!external.UseLuckyStun || !ruleMatches)
                    {
                        continue;
                    }

                    var oldAddress = external.Address;
                    if (!string.Equals(oldAddress, normalizedAddress, StringComparison.OrdinalIgnoreCase))
                    {
                        external.Address = normalizedAddress;
                        changed = true;
                    }

                    if (string.Equals(source.BaseUrl, oldAddress, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(addressConfiguration.ActiveAddress, oldAddress, StringComparison.OrdinalIgnoreCase))
                    {
                        source.BaseUrl = normalizedAddress;
                        addressConfiguration.ActiveAddress = normalizedAddress;
                        addressConfiguration.ActiveLabel = external.DisplayLabel;
                        changed = true;
                    }
                }

                if (changed)
                {
                    source.SetAddressConfiguration(addressConfiguration);
                    if (await mediaSourceRepository.UpdateAsync(source, cancellationToken))
                    {
                        updatedCount++;
                    }
                }
            }

            lastLuckyStunUpdatedAt = DateTimeOffset.UtcNow;
            await settingsService.SaveAsync(BuildAppSettings(), cancellationToken);
            LuckyStunStatusMessage = updatedCount > 0
                ? $"已更新规则“{snapshot.SelectedRule.Name}”，同步 {updatedCount} 个媒体源：{normalizedAddress}"
                : $"规则“{snapshot.SelectedRule.Name}”已检测成功，地址未变化：{normalizedAddress}";
            if (updatedCount > 0)
            {
                LuckyStunAddressUpdated?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!isAutomatic)
            {
                LuckyStunStatusMessage = "Lucky STUN 更新已取消。";
            }
        }
        catch (Exception ex)
        {
            LuckyStunStatusMessage = isAutomatic
                ? $"自动更新 Lucky STUN 失败：{ex.Message}"
                : $"Lucky STUN 检测失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            luckyStunUpdateGate.Release();
            NotifyCommandStateChanged();
        }
    }

    private static string NormalizeDockerBaseUrl(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = $"http://{trimmed}";
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return value.Trim();
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4].TrimEnd('/');
        }

        return new UriBuilder(uri) { Path = path, Query = string.Empty, Fragment = string.Empty }
            .Uri.AbsoluteUri.TrimEnd('/');
    }

    public bool HasLuckyStunRules => LuckyStunRules.Count > 0;

    partial void OnLuckyStunRulesChanged(IReadOnlyList<LuckyStunRule> value)
    {
        OnPropertyChanged(nameof(HasLuckyStunRules));
    }

    private async Task CheckForUpdatesAsync()
    {
        await CheckForUpdatesCoreAsync(install: false);
    }

    private async Task InstallLatestUpdateAsync()
    {
        await CheckForUpdatesCoreAsync(install: true);
    }

    public async Task CheckForUpdatesOnStartupIfNeededAsync(CancellationToken cancellationToken = default)
    {
        if (!AutoCheckUpdatesOnStartup)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (lastUpdateCheckAt.HasValue &&
            now >= lastUpdateCheckAt.Value &&
            now - lastUpdateCheckAt.Value < AutomaticUpdateCheckInterval)
        {
            return;
        }

        try
        {
            lastUpdateCheckAt = now;
            await settingsService.SaveAsync(BuildAppSettings(), cancellationToken);

            var release = await FetchLatestGitHubReleaseAsync(cancellationToken);
            if (release is null)
            {
                return;
            }

            var isNewer = IsVersionNewer(release.TagName, AppVersionText);
            UpdateStatusMessage = isNewer
                ? $"后台自动检查发现新版本 {release.TagName}，可点击“直接更新”。"
                : $"最近自动检查：当前已是最新版本（{AppVersionText}）。";
        }
        catch
        {
            // 启动时检查只做静默提示，网络失败不打扰用户。
        }
    }

    private async Task CheckForUpdatesCoreAsync(bool install)
    {
        IsBusy = true;
        NotifyCommandStateChanged();

        try
        {
            UpdateStatusMessage = install ? "正在获取 GitHub 最新版本..." : "正在检查 GitHub 最新版本...";
            var release = await FetchLatestGitHubReleaseAsync();
            if (release is null)
            {
                UpdateStatusMessage = "GitHub 仓库暂未发布 Release，已打开仓库页面。";
                OpenExternalTarget(GitHubRepositoryUrl, "GitHub 仓库");
                return;
            }

            var isNewer = IsVersionNewer(release.TagName, AppVersionText);
            if (!install)
            {
                UpdateStatusMessage = isNewer
                    ? $"发现新版本 {release.TagName}，可点击“直接更新”。"
                    : $"当前已是最新版本（{AppVersionText}）。";
                return;
            }

            if (!isNewer)
            {
                UpdateStatusMessage = $"当前已是最新版本（{AppVersionText}）。";
                return;
            }

            var asset = PickWindowsReleaseAsset(release.Assets);
            if (asset is null)
            {
                UpdateStatusMessage = "未找到适合 Windows 的安装包，已打开 GitHub Release 页面。";
                OpenExternalTarget(string.IsNullOrWhiteSpace(release.HtmlUrl) ? GitHubLatestReleaseUrl : release.HtmlUrl, "GitHub Release");
                return;
            }

            var downloadedPath = await DownloadReleaseAssetAsync(asset.Value);
            UpdateStatusMessage = $"已下载 {Path.GetFileName(downloadedPath)}，正在打开安装包。";
            OpenExternalTarget(downloadedPath, "更新包");
        }
        catch (Exception ex)
        {
            UpdateStatusMessage = $"检查更新失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyCommandStateChanged();
        }
    }

    private void OpenAbout()
    {
        IsAboutPopupOpen = true;
    }

    private void CloseAbout()
    {
        IsAboutPopupOpen = false;
    }

    private void OpenTmdbWebsite()
    {
        OpenExternalTarget(TmdbWebsiteUrl, "TMDB 官网");
    }

    private void OpenTmdbFaq()
    {
        OpenExternalTarget(TmdbFaqUrl, "TMDB FAQ");
    }

    private void OpenTmdbApiTerms()
    {
        OpenExternalTarget(TmdbApiTermsUrl, "TMDB API 条款");
    }

    private void OpenCreditLink(AppCreditLinkItem? creditItem)
    {
        if (creditItem is null)
        {
            return;
        }

        OpenExternalTarget(creditItem.Url, creditItem.Title);
    }

    private void OpenThirdPartyNotices()
    {
        OpenExternalTarget(ThirdPartyNoticesPath, "THIRD_PARTY_NOTICES");
    }

    private void OpenSettingsDirectory()
    {
        OpenExternalTarget(SettingsDirectoryPath, "设置目录");
    }

    private void OpenGitHubRepository()
    {
        OpenExternalTarget(GitHubRepositoryUrl, "GitHub 仓库");
    }

    private void OpenExternalTarget(string target, string label)
    {
        if (externalLinkOpener.TryOpen(target, out var errorMessage))
        {
            StatusMessage = $"已打开 {label}。";
            return;
        }

        var detail = string.IsNullOrWhiteSpace(errorMessage)
            ? "无法启动系统浏览器或文件查看器。"
            : errorMessage;
        StatusMessage = $"打开 {label} 失败：{detail}";
    }

    private void Apply(AppSettings settings)
    {
        var libraryView = settings.LibraryView ?? new LibraryViewSettings();
        var luckyStun = settings.LuckyStun ?? new LuckyStunSettings();
        AutoScanOnStartup = settings.AutoScanOnStartup;
        AutoCheckUpdatesOnStartup = settings.AutoCheckUpdatesOnStartup;
        lastUpdateCheckAt = settings.LastUpdateCheckAt;
        ShowMediaSourceRealPath = settings.ShowMediaSourceRealPath;
        lastLuckyStunUpdatedAt = luckyStun.LastUpdatedAt;
        luckyStunSelectedRuleId = luckyStun.SelectedRuleId ?? string.Empty;
        luckyStunSelectedRuleName = luckyStun.SelectedRuleName ?? string.Empty;
        LuckyStunManagementUrl = luckyStun.ManagementUrl ?? string.Empty;
        LuckyStunUsername = luckyStun.Username ?? string.Empty;
        LuckyStunPassword = luckyStun.Password ?? string.Empty;
        LuckyStunAutoUpdate = luckyStun.AutoUpdate;
        LuckyStunUpdateIntervalMinutes = Math.Clamp(luckyStun.UpdateIntervalMinutes, 5, 1440);
        LuckyStunRules = [];
        SelectedLuckyStunRule = null;
        LuckyStunStatusMessage = "尚未检测 Lucky STUN。";
        LibrarySortOption = NormalizeLibrarySortOption(libraryView.SortOption);
        LibrarySortDescending = libraryView.SortDescending;
        OfflineCacheDirectory = settings.OfflineCacheDirectory ?? string.Empty;
        EnableLocalMetadataImport = settings.LocalMetadata.EnableLocalMetadataImport;
        EnableLocalMetadataExport = settings.LocalMetadata.EnableLocalMetadataExport;
        EnableTmdbMetadataEnrichment = true;
        EnableTmdbPosterDownloads = true;
        EnableTmdbEpisodeThumbnailDownloads = true;
        CustomTmdbApiKey = settings.Tmdb.CustomApiKey ?? string.Empty;
        CustomTmdbAccessToken = settings.Tmdb.CustomAccessToken ?? string.Empty;
        CustomTmdbCredential = FormatCustomTmdbCredential(CustomTmdbApiKey, CustomTmdbAccessToken);
        TmdbLanguage = NormalizeTmdbLanguage(settings.Tmdb.Language);
        DefaultAudioTrackMode = NormalizeDefaultAudioTrack(settings.Playback.DefaultAudioTrack);
        DefaultSubtitleTrack = NormalizeDefaultSubtitleTrack(settings.Playback.DefaultSubtitleTrack);
        StatusMessage = string.Empty;
        TmdbConnectionStatusMessage = "尚未测试 TMDB 连通性。";
        SyncSelectedOptions();
    }

    private (string ApiKey, string AccessToken) ResolveCustomTmdbCredentials()
    {
        var credential = CustomTmdbCredential.Trim();
        if (string.IsNullOrWhiteSpace(credential))
        {
            return (CustomTmdbApiKey.Trim(), CustomTmdbAccessToken.Trim());
        }

        if (credential.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            credential = credential["Bearer ".Length..].Trim();
        }

        return LooksLikeTmdbAccessToken(credential)
            ? (string.Empty, credential)
            : (credential, string.Empty);
    }

    private static bool LooksLikeTmdbAccessToken(string credential)
    {
        return credential.Length > 64 ||
               credential.StartsWith("eyJ", StringComparison.OrdinalIgnoreCase) ||
               credential.Count(static character => character == '.') >= 2;
    }

    private static string FormatCustomTmdbCredential(string? apiKey, string? accessToken)
    {
        return !string.IsNullOrWhiteSpace(accessToken)
            ? accessToken.Trim()
            : apiKey?.Trim() ?? string.Empty;
    }

    private static string NormalizeTmdbLanguage(string? language)
    {
        var normalized = language?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return DefaultTmdbLanguage;
        }

        return normalized.ToLowerInvariant() switch
        {
            "en" or "en-us" => "en-US",
            "zh" or "zh-cn" or "zh-hans" or "zh-hans-cn" => "zh-CN",
            _ => DefaultTmdbLanguage
        };
    }

    private static string NormalizeLibrarySortOption(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return LibraryViewSettings.SortOptionTitle;
        }

        return normalized.ToLowerInvariant() switch
        {
            "year" or "年份" => LibraryViewSettings.SortOptionYear,
            "rating" or "score" or "评分" => LibraryViewSettings.SortOptionRating,
            _ => LibraryViewSettings.SortOptionTitle
        };
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
            "auto" or "smart" => PlaybackPreferenceSettings.DefaultAudioSmart,
            "chi" or "zho" or "zh" or "zh-cn" or "cn" or "中文" => PlaybackPreferenceSettings.AudioChinese,
            "eng" or "en" or "en-us" or "english" or "英语" => PlaybackPreferenceSettings.AudioEnglish,
            "jpn" or "ja" or "ja-jp" or "japanese" or "日语" => PlaybackPreferenceSettings.AudioJapanese,
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
            "eng" or "en" or "en-us" or "english" or "英语" => PlaybackPreferenceSettings.SubtitleEnglish,
            _ => PlaybackPreferenceSettings.DefaultSubtitleChinese
        };
    }

    private void SyncSelectedOptions()
    {
        suppressOptionSelectionChange = true;
        try
        {
            SelectedTmdbLanguageOption = FindOption(TmdbLanguageOptionItems, NormalizeTmdbLanguage(TmdbLanguage));
            SelectedDefaultAudioTrackOption = FindOption(DefaultAudioTrackOptionItems, NormalizeDefaultAudioTrack(DefaultAudioTrackMode));
            SelectedDefaultSubtitleTrackOption = FindOption(DefaultSubtitleTrackOptionItems, NormalizeDefaultSubtitleTrack(DefaultSubtitleTrack));
        }
        finally
        {
            suppressOptionSelectionChange = false;
        }
    }

    private static SettingsOptionItem FindOption(IReadOnlyList<SettingsOptionItem> options, string value)
    {
        return options.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.Ordinal))
            ?? options[0];
    }

    private static void ApplyNormalizedText(
        string currentValue,
        string normalizedValue,
        Action<string> assign)
    {
        if (!string.Equals(currentValue, normalizedValue, StringComparison.Ordinal))
        {
            assign(normalizedValue);
        }
    }

    private void NotifyCommandStateChanged()
    {
        SaveCommand.NotifyCanExecuteChanged();
        TestTmdbConnectionCommand.NotifyCanExecuteChanged();
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        InstallLatestUpdateCommand.NotifyCanExecuteChanged();
        RefreshLuckyStunCommand.NotifyCanExecuteChanged();
    }

    private static async Task<GitHubRelease?> FetchLatestGitHubReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OmniPlay-Windows");
        using var response = await client.GetAsync(GitHubLatestReleaseApiUrl, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var assets = new List<GitHubAsset>();
        if (root.TryGetProperty("assets", out var assetsElement) &&
            assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsElement.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
                {
                    assets.Add(new GitHubAsset(name!, url!));
                }
            }
        }

        return new GitHubRelease(
            root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("html_url", out var htmlElement) ? htmlElement.GetString() ?? GitHubLatestReleaseUrl : GitHubLatestReleaseUrl,
            assets);
    }

    private static GitHubAsset? PickWindowsReleaseAsset(IReadOnlyList<GitHubAsset> assets)
    {
        return assets
            .Select(asset => (Asset: asset, Score: ScoreWindowsReleaseAsset(asset.Name)))
            .Where(item => item.Score > 0)
            .OrderByDescending(static item => item.Score)
            .Select(static item => item.Asset)
            .FirstOrDefault();
    }

    private static int ScoreWindowsReleaseAsset(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("symbols") || lower.EndsWith(".pdb", StringComparison.Ordinal))
        {
            return 0;
        }

        var score = lower.EndsWith(".exe", StringComparison.Ordinal) || lower.EndsWith(".msi", StringComparison.Ordinal)
            ? 5
            : lower.EndsWith(".zip", StringComparison.Ordinal) || lower.EndsWith(".7z", StringComparison.Ordinal)
                ? 3
                : 0;
        if (score == 0)
        {
            return 0;
        }

        if (lower.Contains("win") || lower.Contains("windows"))
        {
            score += 4;
        }

        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            if (lower.Contains("arm64") || lower.Contains("aarch64")) score += 5;
            if (lower.Contains("x64") || lower.Contains("x86_64")) score -= 2;
        }
        else
        {
            if (lower.Contains("x64") || lower.Contains("x86_64")) score += 5;
            if (lower.Contains("arm64") || lower.Contains("aarch64")) score -= 2;
        }

        return score;
    }

    private static async Task<string> DownloadReleaseAssetAsync(GitHubAsset asset)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OmniPlay-Windows");
        var updateDirectory = Path.Combine(Path.GetTempPath(), "OmniPlayUpdates");
        Directory.CreateDirectory(updateDirectory);
        var destinationPath = Path.Combine(updateDirectory, SanitizeFileName(asset.Name));

        await using var source = await client.GetStreamAsync(asset.DownloadUrl);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination);
        return destinationPath;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(character => invalid.Contains(character) ? '_' : character).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "OmniPlay-update" : sanitized;
    }

    private static bool IsVersionNewer(string candidate, string current)
    {
        var left = VersionParts(candidate);
        var right = VersionParts(current);
        if (left.Count == 0)
        {
            return false;
        }

        var count = Math.Max(left.Count, right.Count);
        for (var index = 0; index < count; index++)
        {
            var lhs = index < left.Count ? left[index] : 0;
            var rhs = index < right.Count ? right[index] : 0;
            if (lhs != rhs)
            {
                return lhs > rhs;
            }
        }

        return false;
    }

    private static List<int> VersionParts(string value)
    {
        return Regex.Matches(value.Trim().TrimStart('v', 'V'), @"\d+")
            .Select(static match => int.TryParse(match.Value, out var number) ? number : 0)
            .ToList();
    }

    private sealed record GitHubRelease(string TagName, string HtmlUrl, IReadOnlyList<GitHubAsset> Assets);

    private readonly record struct GitHubAsset(string Name, string DownloadUrl);

    private static string ResolveAppVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+', 2, StringSplitOptions.TrimEntries)[0];
        }

        var version = assembly.GetName().Version;
        if (version is null)
        {
            return "dev";
        }

        return version.Revision == 0
            ? $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}"
            : version.ToString();
    }

    partial void OnTmdbLanguageChanged(string value)
    {
        if (!suppressOptionSelectionChange)
        {
            SyncSelectedOptions();
        }
    }

    partial void OnDefaultAudioTrackModeChanged(string value)
    {
        if (!suppressOptionSelectionChange)
        {
            SyncSelectedOptions();
        }
    }

    partial void OnDefaultSubtitleTrackChanged(string value)
    {
        if (!suppressOptionSelectionChange)
        {
            SyncSelectedOptions();
        }
    }

    partial void OnSelectedTmdbLanguageOptionChanged(SettingsOptionItem? value)
    {
        if (!suppressOptionSelectionChange && value is not null)
        {
            TmdbLanguage = value.Value;
        }
    }

    partial void OnSelectedDefaultAudioTrackOptionChanged(SettingsOptionItem? value)
    {
        if (!suppressOptionSelectionChange && value is not null)
        {
            DefaultAudioTrackMode = value.Value;
        }
    }

    partial void OnSelectedDefaultSubtitleTrackOptionChanged(SettingsOptionItem? value)
    {
        if (!suppressOptionSelectionChange && value is not null)
        {
            DefaultSubtitleTrack = value.Value;
        }
    }

    private sealed class NullExternalLinkOpener : IExternalLinkOpener
    {
        public static readonly NullExternalLinkOpener Instance = new();

        public bool TryOpen(string target, out string? errorMessage)
        {
            errorMessage = "当前环境未提供外部链接或文件打开能力。";
            return false;
        }
    }
}
