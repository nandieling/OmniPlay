using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Settings;

namespace OmniPlay.Core.ViewModels.Settings;

public partial class SettingsViewModel : ObservableObject
{
    private const string DefaultTmdbLanguage = TmdbSettings.DefaultLanguage;
    private const string TmdbWebsiteUrl = "https://www.themoviedb.org";
    private const string TmdbFaqUrl = "https://developer.themoviedb.org/docs/faq";
    private const string TmdbApiTermsUrl = "https://www.themoviedb.org/api-terms-of-use";

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
    private readonly string appVersionText;
    private readonly string runtimeText;
    private readonly string platformText;
    private readonly string thirdPartyNoticesPath;
    private bool loaded;
    private bool suppressOptionSelectionChange;

    public SettingsViewModel(
        ISettingsService settingsService,
        ITmdbConnectionTester tmdbConnectionTester,
        IExternalLinkOpener? externalLinkOpener = null)
    {
        this.settingsService = settingsService;
        this.tmdbConnectionTester = tmdbConnectionTester;
        this.externalLinkOpener = externalLinkOpener ?? NullExternalLinkOpener.Instance;

        appVersionText = ResolveAppVersion();
        runtimeText = RuntimeInformation.FrameworkDescription;
        platformText = $"{RuntimeInformation.OSDescription.Trim()} / {RuntimeInformation.ProcessArchitecture}";
        thirdPartyNoticesPath = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.md");

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
        TestTmdbConnectionCommand = new AsyncRelayCommand(TestTmdbConnectionAsync, () => !IsBusy);
        OpenAboutCommand = new RelayCommand(OpenAbout);
        CloseAboutCommand = new RelayCommand(CloseAbout);
        OpenTmdbWebsiteCommand = new RelayCommand(OpenTmdbWebsite);
        OpenTmdbFaqCommand = new RelayCommand(OpenTmdbFaq);
        OpenTmdbApiTermsCommand = new RelayCommand(OpenTmdbApiTerms);
        OpenCreditLinkCommand = new RelayCommand<AppCreditLinkItem?>(OpenCreditLink);
        OpenThirdPartyNoticesCommand = new RelayCommand(OpenThirdPartyNotices);
        OpenSettingsDirectoryCommand = new RelayCommand(OpenSettingsDirectory);

        SyncSelectedOptions();
    }

    public IAsyncRelayCommand SaveCommand { get; }

    public IAsyncRelayCommand TestTmdbConnectionCommand { get; }

    public IRelayCommand OpenAboutCommand { get; }

    public IRelayCommand CloseAboutCommand { get; }

    public IRelayCommand OpenTmdbWebsiteCommand { get; }

    public IRelayCommand OpenTmdbFaqCommand { get; }

    public IRelayCommand OpenTmdbApiTermsCommand { get; }

    public IRelayCommand<AppCreditLinkItem?> OpenCreditLinkCommand { get; }

    public IRelayCommand OpenThirdPartyNoticesCommand { get; }

    public IRelayCommand OpenSettingsDirectoryCommand { get; }

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

    [ObservableProperty]
    private bool autoScanOnStartup = true;

    [ObservableProperty]
    private bool showMediaSourceRealPath = true;

    [ObservableProperty]
    private bool enableBuiltInPublicTmdbSource = true;

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
    }

    public AppSettings BuildAppSettings()
    {
        return new AppSettings
        {
            AutoScanOnStartup = AutoScanOnStartup,
            ShowMediaSourceRealPath = ShowMediaSourceRealPath,
            Tmdb = BuildTmdbSettings(),
            Playback = BuildPlaybackPreferenceSettings()
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
            EnableBuiltInPublicSource = EnableBuiltInPublicTmdbSource,
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
            await settingsService.SaveAsync(BuildAppSettings());

            loaded = true;
            StatusMessage = "设置已保存。";
            SettingsSaved?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
            NotifyCommandStateChanged();
        }
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
        AutoScanOnStartup = settings.AutoScanOnStartup;
        ShowMediaSourceRealPath = settings.ShowMediaSourceRealPath;
        EnableTmdbMetadataEnrichment = true;
        EnableTmdbPosterDownloads = true;
        EnableTmdbEpisodeThumbnailDownloads = true;
        EnableBuiltInPublicTmdbSource = settings.Tmdb.EnableBuiltInPublicSource;
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
    }

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
