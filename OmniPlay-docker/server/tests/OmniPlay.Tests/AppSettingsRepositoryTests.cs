using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.FileSystem;
using Xunit;

namespace OmniPlay.Tests;

public sealed class AppSettingsRepositoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omniplay-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UpdatePersistsAndNormalizesCacheSettings()
    {
        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();
        var repository = new AppSettingsRepository(database);

        var updated = await repository.UpdateAsync(new AppSettingsUpdateRequest(
            Cache: new CacheSettings(
                HlsRetentionHours: 999,
                HlsMaxGb: 0,
                HlsCachePath: "  /cache/hls  ",
                ImageCleanupScope: "orphans-only",
                WebDavRetentionHours: 0,
                WebDavMaxGb: 0,
                SubtitleCachePath: "  /cache/subtitles  ",
                SubtitleMaxGb: 0,
                SubtitleCacheStrategy: "full")));
        var snapshot = await repository.GetAsync();

        Assert.Equal(24 * 30, updated.Cache.HlsRetentionHours);
        Assert.Equal(30, updated.Cache.HlsMaxGb);
        Assert.Equal("/cache/hls", updated.Cache.HlsCachePath);
        Assert.Equal("orphans-only", updated.Cache.ImageCleanupScope);
        Assert.Equal(72, updated.Cache.WebDavRetentionHours);
        Assert.Equal(20, updated.Cache.WebDavMaxGb);
        Assert.Equal("/cache/subtitles", updated.Cache.SubtitleCachePath);
        Assert.Equal(20, updated.Cache.SubtitleMaxGb);
        Assert.Equal("full", updated.Cache.SubtitleCacheStrategy);
        Assert.Equal(24 * 30, snapshot.Cache.HlsRetentionHours);
        Assert.Equal(30, snapshot.Cache.HlsMaxGb);
        Assert.Equal("/cache/hls", snapshot.Cache.HlsCachePath);
        Assert.Equal("orphans-only", snapshot.Cache.ImageCleanupScope);
        Assert.Equal(72, snapshot.Cache.WebDavRetentionHours);
        Assert.Equal(20, snapshot.Cache.WebDavMaxGb);
        Assert.Equal("/cache/subtitles", snapshot.Cache.SubtitleCachePath);
        Assert.Equal(20, snapshot.Cache.SubtitleMaxGb);
        Assert.Equal("full", snapshot.Cache.SubtitleCacheStrategy);
    }

    [Fact]
    public async Task UpdateKeepsPlaybackStrategiesEnabled()
    {
        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "playback")));
        database.EnsureInitialized();
        var repository = new AppSettingsRepository(database);

        var updated = await repository.UpdateAsync(new AppSettingsUpdateRequest(
            Playback: new PlaybackSettings(
                DirectStream: false,
                HlsRemux: true,
                Transcode: false)));
        var snapshot = await repository.GetAsync();

        Assert.True(updated.Playback.DirectStream);
        Assert.True(updated.Playback.HlsRemux);
        Assert.True(updated.Playback.Transcode);
        Assert.True(snapshot.Playback.DirectStream);
        Assert.True(snapshot.Playback.HlsRemux);
        Assert.True(snapshot.Playback.Transcode);
    }

    [Fact]
    public async Task UpdateAlwaysKeepsTmdbScrapingAndPostersEnabled()
    {
        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "tmdb-defaults")));
        database.EnsureInitialized();
        var repository = new AppSettingsRepository(database);

        var updated = await repository.UpdateAsync(new AppSettingsUpdateRequest(
            Tmdb: new TmdbSettings(
                EnableMetadataEnrichment: false,
                EnablePosterDownloads: false,
                CustomApiKey: "api-key")));

        Assert.True(updated.Tmdb.EnableMetadataEnrichment);
        Assert.True(updated.Tmdb.EnablePosterDownloads);
        Assert.Equal("api-key", updated.Tmdb.CustomApiKey);
    }

    [Fact]
    public async Task UpdateNormalizesEmptyPlaybackPolicyToDefault()
    {
        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "playback-default")));
        database.EnsureInitialized();
        var repository = new AppSettingsRepository(database);

        var updated = await repository.UpdateAsync(new AppSettingsUpdateRequest(
            Playback: new PlaybackSettings(
                DirectStream: false,
                HlsRemux: false,
                Transcode: false)));

        Assert.True(updated.Playback.DirectStream);
        Assert.True(updated.Playback.HlsRemux);
        Assert.True(updated.Playback.Transcode);
    }

    [Fact]
    public async Task UpdateCacheOnlyPreservesPlaybackSettings()
    {
        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "partial-update")));
        database.EnsureInitialized();
        var repository = new AppSettingsRepository(database);

        await repository.UpdateAsync(new AppSettingsUpdateRequest(
            Playback: new PlaybackSettings(
                DirectStream: false,
                HlsRemux: true,
                Transcode: false)));
        var updated = await repository.UpdateAsync(new AppSettingsUpdateRequest(
            Cache: new CacheSettings(
                HlsRetentionHours: 12,
                HlsMaxGb: 16,
                ImageCleanupScope: "orphans-and-untracked",
                WebDavRetentionHours: 48,
                WebDavMaxGb: 64,
                SubtitleMaxGb: 8,
                SubtitleCacheStrategy: "invalid")));

        Assert.True(updated.Playback.DirectStream);
        Assert.True(updated.Playback.HlsRemux);
        Assert.True(updated.Playback.Transcode);
        Assert.Equal(16, updated.Cache.HlsMaxGb);
        Assert.Equal(48, updated.Cache.WebDavRetentionHours);
        Assert.Equal(64, updated.Cache.WebDavMaxGb);
        Assert.Equal(8, updated.Cache.SubtitleMaxGb);
        Assert.Equal("disabled", updated.Cache.SubtitleCacheStrategy);
    }

    [Fact]
    public async Task ExistingCacheSettingsWithoutHlsMaxGbUseDefaultLimit()
    {
        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "legacy-cache")));
        database.EnsureInitialized();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings (key, value_json, updated_at)
            VALUES ('cache', '{"hlsRetentionHours":12,"imageCleanupScope":"orphans-only","webDavRetentionHours":48,"webDavMaxGb":64}', $updatedAt);
            """;
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
        var repository = new AppSettingsRepository(database);

        var snapshot = await repository.GetAsync();

        Assert.Equal(12, snapshot.Cache.HlsRetentionHours);
        Assert.Equal(30, snapshot.Cache.HlsMaxGb);
        Assert.Equal(string.Empty, snapshot.Cache.HlsCachePath);
        Assert.Equal("orphans-only", snapshot.Cache.ImageCleanupScope);
        Assert.Equal(48, snapshot.Cache.WebDavRetentionHours);
        Assert.Equal(64, snapshot.Cache.WebDavMaxGb);
        Assert.Equal(string.Empty, snapshot.Cache.SubtitleCachePath);
        Assert.Equal(20, snapshot.Cache.SubtitleMaxGb);
        Assert.Equal("disabled", snapshot.Cache.SubtitleCacheStrategy);
    }

    [Fact]
    public async Task UpdatePersistsAndNormalizesAutomationSettings()
    {
        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "automation")));
        database.EnsureInitialized();
        var repository = new AppSettingsRepository(database);

        var updated = await repository.UpdateAsync(new AppSettingsUpdateRequest(
            Automation: new AutomationSettings(
                ScheduledLibraryRefreshEnabled: true,
                ScheduledLibraryRefreshIntervalHours: 999)));
        var snapshot = await repository.GetAsync();

        Assert.True(updated.Automation.ScheduledLibraryRefreshEnabled);
        Assert.Equal(24 * 30, updated.Automation.ScheduledLibraryRefreshIntervalHours);
        Assert.True(snapshot.Automation.ScheduledLibraryRefreshEnabled);
        Assert.Equal(24 * 30, snapshot.Automation.ScheduledLibraryRefreshIntervalHours);
    }

    [Fact]
    public async Task UpdatePersistsAndNormalizesProxySettings()
    {
        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "proxy")));
        database.EnsureInitialized();
        var repository = new AppSettingsRepository(database);

        var updated = await repository.UpdateAsync(new AppSettingsUpdateRequest(
            Proxy: new ProxySettings(
                IsEnabled: true,
                Url: "192.168.1.10:7890",
                Username: " user ",
                Password: " pass ",
                BypassList: " localhost ; 192.168.*\n.local ")));
        var snapshot = await repository.GetAsync();

        Assert.True(updated.Proxy.IsEnabled);
        Assert.Equal("http://192.168.1.10:7890", updated.Proxy.Url);
        Assert.Equal("", updated.Proxy.Username);
        Assert.Equal("", updated.Proxy.Password);
        Assert.Equal("", updated.Proxy.BypassList);
        Assert.Equal("http://192.168.1.10:7890", snapshot.Proxy.Url);
        Assert.Equal("", snapshot.Proxy.BypassList);
    }

    [Fact]
    public async Task UpdateNormalizesProxyAliasAndEmbeddedCredentials()
    {
        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "proxy-credentials")));
        database.EnsureInitialized();
        var repository = new AppSettingsRepository(database);

        var updated = await repository.UpdateAsync(new AppSettingsUpdateRequest(
            Proxy: new ProxySettings(
                IsEnabled: true,
                Url: "socks://user%20name:pass%23word@127.0.0.1:1080")));

        Assert.True(updated.Proxy.IsEnabled);
        Assert.Equal("socks5://127.0.0.1:1080", updated.Proxy.Url);
        Assert.Equal("", updated.Proxy.Username);
        Assert.Equal("", updated.Proxy.Password);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
