using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Core.Settings;
using OmniPlay.Core.ViewModels.Settings;

namespace OmniPlay.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void OpenTmdbFaqCommand_UpdatesStatusWhenBrowserLaunchSucceeds()
    {
        var viewModel = CreateViewModel(new FakeExternalLinkOpener(shouldSucceed: true));

        viewModel.OpenTmdbFaqCommand.Execute(null);

        Assert.Equal("已打开 TMDB FAQ。", viewModel.StatusMessage);
    }

    [Fact]
    public void OpenTmdbApiTermsCommand_UpdatesStatusWhenBrowserLaunchFails()
    {
        var viewModel = CreateViewModel(new FakeExternalLinkOpener(shouldSucceed: false, errorMessage: "boom"));

        viewModel.OpenTmdbApiTermsCommand.Execute(null);

        Assert.Equal("打开 TMDB API 条款 失败：boom", viewModel.StatusMessage);
    }

    [Fact]
    public void OpenCreditLinkCommand_UsesSelectedCreditItem()
    {
        var opener = new FakeExternalLinkOpener(shouldSucceed: true);
        var viewModel = CreateViewModel(opener);

        var creditItem = viewModel.CreditLinks.First(item => item.Title.Contains("Avalonia", StringComparison.Ordinal));
        viewModel.OpenCreditLinkCommand.Execute(creditItem);

        Assert.Equal("已打开 Avalonia UI。", viewModel.StatusMessage);
        Assert.Equal(creditItem.Url, opener.LastTarget);
    }

    [Fact]
    public void CreditLinks_ExposeExpectedBuiltInCreditsAndLicenses()
    {
        var viewModel = CreateViewModel(new FakeExternalLinkOpener(shouldSucceed: true));

        Assert.True(viewModel.CreditLinks.Count >= 7);
        Assert.Contains(viewModel.CreditLinks, item => item.Title.Contains("TMDB", StringComparison.Ordinal));
        Assert.Contains(viewModel.CreditLinks, item => item.Title.Contains("libmpv", StringComparison.Ordinal));
        Assert.All(viewModel.CreditLinks, item => Assert.False(string.IsNullOrWhiteSpace(item.License)));
    }

    [Fact]
    public void OpenAboutCommand_OpensAndClosesAboutPopup()
    {
        var viewModel = CreateViewModel(new FakeExternalLinkOpener(shouldSucceed: true));

        viewModel.OpenAboutCommand.Execute(null);
        Assert.True(viewModel.IsAboutPopupOpen);

        viewModel.CloseAboutCommand.Execute(null);
        Assert.False(viewModel.IsAboutPopupOpen);
    }

    [Fact]
    public void AboutMetadata_IsPopulated()
    {
        var viewModel = CreateViewModel(new FakeExternalLinkOpener(shouldSucceed: true));

        Assert.False(string.IsNullOrWhiteSpace(viewModel.AppVersionText));
        Assert.False(string.IsNullOrWhiteSpace(viewModel.RuntimeText));
        Assert.False(string.IsNullOrWhiteSpace(viewModel.PlatformText));
        Assert.Equal(Path.GetTempPath(), viewModel.SettingsDirectoryPath);
        Assert.EndsWith("THIRD_PARTY_NOTICES.md", viewModel.ThirdPartyNoticesPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpenThirdPartyNoticesCommand_UsesLocalNoticePath()
    {
        var opener = new FakeExternalLinkOpener(shouldSucceed: true);
        var viewModel = CreateViewModel(opener);

        viewModel.OpenThirdPartyNoticesCommand.Execute(null);

        Assert.Equal("已打开 THIRD_PARTY_NOTICES。", viewModel.StatusMessage);
        Assert.Equal(viewModel.ThirdPartyNoticesPath, opener.LastTarget);
    }

    [Fact]
    public void OpenSettingsDirectoryCommand_UsesSettingsDirectory()
    {
        var opener = new FakeExternalLinkOpener(shouldSucceed: true);
        var viewModel = CreateViewModel(opener);

        viewModel.OpenSettingsDirectoryCommand.Execute(null);

        Assert.Equal("已打开 设置目录。", viewModel.StatusMessage);
        Assert.Equal(Path.GetTempPath(), opener.LastTarget);
    }

    [Fact]
    public async Task LoadAsync_AppliesPersistedTmdbLanguage()
    {
        var settingsService = new FakeSettingsService(new AppSettings
        {
            AutoScanOnStartup = false,
            ShowMediaSourceRealPath = false,
            Tmdb = new TmdbSettings
            {
                EnableMetadataEnrichment = false,
                EnablePosterDownloads = false,
                EnableEpisodeThumbnailDownloads = false,
                EnableBuiltInPublicSource = false,
                CustomApiKey = "custom-key",
                CustomAccessToken = "custom-token",
                Language = " en-US "
            }
        });
        var viewModel = CreateViewModel(settingsService: settingsService);

        await viewModel.LoadAsync();

        Assert.False(viewModel.AutoScanOnStartup);
        Assert.False(viewModel.ShowMediaSourceRealPath);
        Assert.True(viewModel.EnableTmdbMetadataEnrichment);
        Assert.True(viewModel.EnableTmdbPosterDownloads);
        Assert.True(viewModel.EnableTmdbEpisodeThumbnailDownloads);
        Assert.False(viewModel.EnableBuiltInPublicTmdbSource);
        Assert.Equal("custom-key", viewModel.CustomTmdbApiKey);
        Assert.Equal("custom-token", viewModel.CustomTmdbAccessToken);
        Assert.Equal("custom-token", viewModel.CustomTmdbCredential);
        Assert.Equal("en-US", viewModel.TmdbLanguage);
        Assert.Equal(PlaybackPreferenceSettings.DefaultAudioSmart, viewModel.DefaultAudioTrackMode);
        Assert.Equal(PlaybackPreferenceSettings.DefaultSubtitleChinese, viewModel.DefaultSubtitleTrack);
    }

    [Fact]
    public async Task SaveCommand_PersistsNormalizedTmdbLanguageAndCredential()
    {
        var settingsService = new FakeSettingsService();
        var viewModel = CreateViewModel(settingsService: settingsService);
        viewModel.EnableTmdbMetadataEnrichment = false;
        viewModel.EnableTmdbPosterDownloads = false;
        viewModel.EnableTmdbEpisodeThumbnailDownloads = false;
        viewModel.CustomTmdbCredential = "  custom-key  ";
        viewModel.TmdbLanguage = "  ja-JP  ";

        await viewModel.SaveCommand.ExecuteAsync(null);

        var savedSettings = Assert.IsType<AppSettings>(settingsService.SavedSettings);
        Assert.True(savedSettings.Tmdb.EnableMetadataEnrichment);
        Assert.True(savedSettings.Tmdb.EnablePosterDownloads);
        Assert.True(savedSettings.Tmdb.EnableEpisodeThumbnailDownloads);
        Assert.Equal("custom-key", savedSettings.Tmdb.CustomApiKey);
        Assert.Equal(string.Empty, savedSettings.Tmdb.CustomAccessToken);
        Assert.Equal("zh-CN", savedSettings.Tmdb.Language);
        Assert.Equal(PlaybackPreferenceSettings.DefaultAudioSmart, savedSettings.Playback.DefaultAudioTrack);
        Assert.Equal(PlaybackPreferenceSettings.DefaultSubtitleChinese, savedSettings.Playback.DefaultSubtitleTrack);
        Assert.Equal("custom-key", viewModel.CustomTmdbCredential);
        Assert.Equal("custom-key", viewModel.CustomTmdbApiKey);
        Assert.Equal(string.Empty, viewModel.CustomTmdbAccessToken);
        Assert.Equal("zh-CN", viewModel.TmdbLanguage);
    }

    [Fact]
    public void BuildTmdbSettings_UsesDefaultLanguageWhenBlank()
    {
        var viewModel = CreateViewModel();
        viewModel.TmdbLanguage = " ";

        var settings = viewModel.BuildTmdbSettings();

        Assert.Equal(TmdbSettings.DefaultLanguage, settings.Language);
        Assert.Equal(TmdbSettings.DefaultLanguage, viewModel.TmdbLanguage);
    }

    [Fact]
    public void BuildTmdbSettings_AcceptsReadAccessTokenInMergedCredential()
    {
        var viewModel = CreateViewModel();
        viewModel.CustomTmdbCredential = "Bearer eyJhbGciOiJIUzI1NiJ9.demo.signature";

        var settings = viewModel.BuildTmdbSettings();

        Assert.Equal(string.Empty, settings.CustomApiKey);
        Assert.Equal("eyJhbGciOiJIUzI1NiJ9.demo.signature", settings.CustomAccessToken);
    }

    [Fact]
    public void BuildPlaybackPreferenceSettings_NormalizesSupportedOptions()
    {
        var viewModel = CreateViewModel();
        viewModel.DefaultAudioTrackMode = "jpn";
        viewModel.DefaultSubtitleTrack = "eng";

        var settings = viewModel.BuildPlaybackPreferenceSettings();

        Assert.Equal(PlaybackPreferenceSettings.AudioJapanese, settings.DefaultAudioTrack);
        Assert.Equal(PlaybackPreferenceSettings.SubtitleEnglish, settings.DefaultSubtitleTrack);
    }

    private static SettingsViewModel CreateViewModel(
        FakeExternalLinkOpener? externalLinkOpener = null,
        FakeSettingsService? settingsService = null,
        FakeTmdbConnectionTester? tmdbConnectionTester = null)
    {
        return new SettingsViewModel(
            settingsService ?? new FakeSettingsService(),
            tmdbConnectionTester ?? new FakeTmdbConnectionTester(),
            externalLinkOpener ?? new FakeExternalLinkOpener(shouldSucceed: true));
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public FakeSettingsService(AppSettings? initialSettings = null)
        {
            LoadedSettings = initialSettings ?? new AppSettings();
        }

        public string SettingsDirectory => Path.GetTempPath();

        public AppSettings LoadedSettings { get; private set; }

        public AppSettings? SavedSettings { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LoadedSettings);
        }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            SavedSettings = settings;
            LoadedSettings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTmdbConnectionTester : ITmdbConnectionTester
    {
        public Task<TmdbConnectionTestResult> TestConnectionAsync(TmdbSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TmdbConnectionTestResult(true, "ok"));
        }
    }

    private sealed class FakeExternalLinkOpener : IExternalLinkOpener
    {
        private readonly bool shouldSucceed;
        private readonly string? errorMessage;

        public FakeExternalLinkOpener(bool shouldSucceed, string? errorMessage = null)
        {
            this.shouldSucceed = shouldSucceed;
            this.errorMessage = errorMessage;
        }

        public string? LastTarget { get; private set; }

        public bool TryOpen(string target, out string? errorMessage)
        {
            LastTarget = target;
            errorMessage = this.errorMessage;
            return shouldSucceed;
        }
    }
}
