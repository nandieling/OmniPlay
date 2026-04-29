using Microsoft.Data.Sqlite;
using OmniPlay.Core.Settings;
using OmniPlay.Infrastructure.FileSystem;

namespace OmniPlay.Tests;

public sealed class JsonSettingsServiceTests : IDisposable
{
    private readonly string rootPath;
    private readonly TestStoragePaths storagePaths;

    public JsonSettingsServiceTests()
    {
        rootPath = Path.Combine(
            AppContext.BaseDirectory,
            "test-data",
            nameof(JsonSettingsServiceTests),
            Guid.NewGuid().ToString("N"));
        storagePaths = new TestStoragePaths(rootPath);
    }

    [Fact]
    public async Task LoadAndSaveAsync_PersistsTmdbSettings()
    {
        var service = new JsonSettingsService(storagePaths);

        var defaults = await service.LoadAsync();
        Assert.True(defaults.AutoScanOnStartup);
        Assert.True(defaults.ShowMediaSourceRealPath);
        Assert.True(defaults.Tmdb.EnableMetadataEnrichment);
        Assert.True(defaults.Tmdb.EnablePosterDownloads);
        Assert.True(defaults.Tmdb.EnableEpisodeThumbnailDownloads);
        Assert.True(defaults.Tmdb.EnableBuiltInPublicSource);
        Assert.Equal(string.Empty, defaults.Tmdb.CustomApiKey);
        Assert.Equal(string.Empty, defaults.Tmdb.CustomAccessToken);
        Assert.Equal(string.Empty, defaults.Tmdb.Language);
        Assert.Equal(PlaybackPreferenceSettings.DefaultAudioSmart, defaults.Playback.DefaultAudioTrack);
        Assert.Equal(PlaybackPreferenceSettings.DefaultSubtitleChinese, defaults.Playback.DefaultSubtitleTrack);

        await service.SaveAsync(new AppSettings
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
                Language = "en-US"
            },
            Playback = new PlaybackPreferenceSettings
            {
                DefaultAudioTrack = PlaybackPreferenceSettings.AudioJapanese,
                DefaultSubtitleTrack = PlaybackPreferenceSettings.SubtitleEnglish
            }
        });

        var reloaded = await new JsonSettingsService(storagePaths).LoadAsync();
        Assert.False(reloaded.AutoScanOnStartup);
        Assert.False(reloaded.ShowMediaSourceRealPath);
        Assert.False(reloaded.Tmdb.EnableMetadataEnrichment);
        Assert.False(reloaded.Tmdb.EnablePosterDownloads);
        Assert.False(reloaded.Tmdb.EnableEpisodeThumbnailDownloads);
        Assert.False(reloaded.Tmdb.EnableBuiltInPublicSource);
        Assert.Equal("custom-key", reloaded.Tmdb.CustomApiKey);
        Assert.Equal("custom-token", reloaded.Tmdb.CustomAccessToken);
        Assert.Equal("en-US", reloaded.Tmdb.Language);
        Assert.Equal(PlaybackPreferenceSettings.AudioJapanese, reloaded.Playback.DefaultAudioTrack);
        Assert.Equal(PlaybackPreferenceSettings.SubtitleEnglish, reloaded.Playback.DefaultSubtitleTrack);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }
}
