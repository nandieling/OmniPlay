using Dapper;
using Microsoft.Data.Sqlite;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Infrastructure.Data;

namespace OmniPlay.Tests;

public sealed class MediaSourceRepositoryTests : IDisposable
{
    private readonly string rootPath;
    private readonly SqliteDatabase database;
    private readonly MediaSourceRepository repository;

    public MediaSourceRepositoryTests()
    {
        rootPath = Path.Combine(
            AppContext.BaseDirectory,
            "test-data",
            nameof(MediaSourceRepositoryTests),
            Guid.NewGuid().ToString("N"));

        var storagePaths = new TestStoragePaths(rootPath);
        database = new SqliteDatabase(storagePaths);
        database.EnsureInitialized();
        repository = new MediaSourceRepository(database);
    }

    [Fact]
    public async Task AddAsync_ProtectsWebDavCredentialsAtRest_AndRestoresOnRead()
    {
        var plainAuthConfig = MediaSourceAuthConfigSerializer.SerializeWebDav(new WebDavAuthConfig("demo", "secret"));

        var sourceId = await repository.AddAsync(new MediaSource
        {
            Name = "Remote",
            ProtocolType = "webdav",
            BaseUrl = "https://demo.example/library",
            AuthConfig = plainAuthConfig
        });

        using var connection = database.OpenConnection();
        var rawAuthConfig = await connection.ExecuteScalarAsync<string>(
            "SELECT authConfig FROM mediaSource WHERE id = @Id",
            new { Id = sourceId });

        Assert.False(string.IsNullOrWhiteSpace(rawAuthConfig));
        Assert.NotEqual(plainAuthConfig, rawAuthConfig);
        Assert.DoesNotContain("secret", rawAuthConfig, StringComparison.Ordinal);

        var storedSource = Assert.Single(await repository.GetAllAsync());
        var auth = MediaSourceAuthConfigSerializer.DeserializeWebDav(storedSource.AuthConfig);

        Assert.NotNull(auth);
        Assert.Equal("demo", auth!.Username);
        Assert.Equal("secret", auth.Password);
    }

    [Fact]
    public async Task UpdateAsync_RewritesProtectedCredentialsAndBaseUrl()
    {
        var sourceId = await repository.AddAsync(new MediaSource
        {
            Name = "Remote",
            ProtocolType = "webdav",
            BaseUrl = "https://demo.example/library",
            AuthConfig = MediaSourceAuthConfigSerializer.SerializeWebDav(new WebDavAuthConfig("demo", "old-secret"))
        });

        var updated = await repository.UpdateAsync(new MediaSource
        {
            Id = sourceId,
            Name = "Remote Updated",
            ProtocolType = "webdav",
            BaseUrl = "https://demo.example/library-2",
            AuthConfig = MediaSourceAuthConfigSerializer.SerializeWebDav(new WebDavAuthConfig("demo2", "new-secret"))
        });

        Assert.True(updated);

        using var connection = database.OpenConnection();
        var row = await connection.QuerySingleAsync<MediaSourceRow>(
            "SELECT name, baseUrl, authConfig FROM mediaSource WHERE id = @Id",
            new { Id = sourceId });

        Assert.Equal("Remote Updated", row.Name);
        Assert.Equal("https://demo.example/library-2", row.BaseUrl);
        Assert.DoesNotContain("new-secret", row.AuthConfig, StringComparison.Ordinal);

        var storedSource = Assert.Single(await repository.GetAllAsync());
        var auth = MediaSourceAuthConfigSerializer.DeserializeWebDav(storedSource.AuthConfig);

        Assert.NotNull(auth);
        Assert.Equal("demo2", auth!.Username);
        Assert.Equal("new-secret", auth.Password);
    }

    [Fact]
    public async Task SetEnabledAsync_DisablesSourceWithoutRemovingIt()
    {
        var sourceId = await repository.AddAsync(new MediaSource
        {
            Name = "Local",
            ProtocolType = "local",
            BaseUrl = @"D:\Movies"
        });

        var updated = await repository.SetEnabledAsync(sourceId, false, DateTimeOffset.UtcNow);

        Assert.True(updated);

        var storedSource = Assert.Single(await repository.GetAllAsync());
        Assert.False(storedSource.IsEnabled);
        Assert.Null(storedSource.RemovedAt);
    }

    [Fact]
    public async Task SoftRemoveAsync_HidesSourceUntilItIsAddedAgain()
    {
        var sourceId = await repository.AddAsync(new MediaSource
        {
            Name = "Remote",
            ProtocolType = "webdav",
            BaseUrl = "https://demo.example/library",
            AuthConfig = MediaSourceAuthConfigSerializer.SerializeWebDav(new WebDavAuthConfig("demo", "secret"))
        });

        var removed = await repository.SoftRemoveAsync(sourceId, DateTimeOffset.UtcNow);

        Assert.True(removed);
        Assert.Empty(await repository.GetAllAsync());

        using (var connection = database.OpenConnection())
        {
            var rowCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM mediaSource WHERE id = @Id AND removedAt IS NOT NULL",
                new { Id = sourceId });
            Assert.Equal(1, rowCount);
        }

        var reactivatedId = await repository.AddAsync(new MediaSource
        {
            Name = "Remote Again",
            ProtocolType = "webdav",
            BaseUrl = "https://demo.example/library",
            AuthConfig = MediaSourceAuthConfigSerializer.SerializeWebDav(new WebDavAuthConfig("demo2", "new-secret"))
        });

        Assert.Equal(sourceId, reactivatedId);
        var storedSource = Assert.Single(await repository.GetAllAsync());
        Assert.True(storedSource.IsEnabled);
        Assert.Null(storedSource.RemovedAt);
        Assert.Equal("Remote Again", storedSource.Name);
    }

    [Fact]
    public async Task PurgeExpiredInactiveAsync_RemovesSourcesAfterRetentionWindow()
    {
        var sourceId = await repository.AddAsync(new MediaSource
        {
            Name = "Old",
            ProtocolType = "local",
            BaseUrl = @"D:\Old"
        });
        var removedAt = DateTimeOffset.UtcNow.AddDays(-31);
        await repository.SoftRemoveAsync(sourceId, removedAt);

        var purged = await repository.PurgeExpiredInactiveAsync(DateTimeOffset.UtcNow);

        Assert.Equal(1, purged);
        using var connection = database.OpenConnection();
        var rowCount = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM mediaSource WHERE id = @Id",
            new { Id = sourceId });
        Assert.Equal(0, rowCount);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private sealed record MediaSourceRow(
        string Name,
        string BaseUrl,
        string AuthConfig);
}
