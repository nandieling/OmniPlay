using Dapper;
using Microsoft.Data.Sqlite;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models.Library;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.Library;

namespace OmniPlay.Tests;

public sealed class DoubanMetadataServiceTests : IDisposable
{
    private readonly string rootPath;
    private readonly SqliteDatabase database;
    private readonly TestStoragePaths storagePaths;
    private readonly DoubanMetadataService service;

    public DoubanMetadataServiceTests()
    {
        rootPath = Path.Combine(
            AppContext.BaseDirectory,
            "test-data",
            nameof(DoubanMetadataServiceTests),
            Guid.NewGuid().ToString("N"));

        storagePaths = new TestStoragePaths(rootPath);
        database = new SqliteDatabase(storagePaths);
        database.EnsureInitialized();
        service = new DoubanMetadataService(
            database,
            new HttpClient { Timeout = TimeSpan.FromSeconds(5) },
            storagePaths);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, true);
        }
    }

    [Fact]
    public void ExtractSubjectId_ParsesDigitsDesktopUrlAndMobileUrl()
    {
        Assert.Equal("1292052", DoubanMetadataService.ExtractSubjectId("1292052"));
        Assert.Equal("1292052", DoubanMetadataService.ExtractSubjectId("https://movie.douban.com/subject/1292052/"));
        Assert.Equal("1292052", DoubanMetadataService.ExtractSubjectId("https://m.douban.com/movie/subject/1292052"));
        Assert.Null(DoubanMetadataService.ExtractSubjectId("abc"));
        Assert.Null(DoubanMetadataService.ExtractSubjectId("12a"));
    }

    [Fact]
    public void PosterUrls_NormalizeLargeToRatioPoster_AndDistinguishUsable()
    {
        var normalized = DoubanPosterUrls.Normalize("//img1.doubanio.com/view/photo/large/public/p123.jpg?x=1");
        Assert.Equal("https://img1.doubanio.com/view/photo/s_ratio_poster/public/p123.jpg", normalized);

        Assert.True(DoubanPosterUrls.IsUsable("https://img1.doubanio.com/view/photo/s_ratio_poster/public/p123.jpg"));
        Assert.True(DoubanPosterUrls.IsUsable("https://img1.doubanio.com/view/photo/raw/public/p123.jpg"));
        Assert.False(DoubanPosterUrls.IsUsable("https://img1.doubanio.com/view/photo/l/public/p123.jpg@qnmob.webp"));
        Assert.False(DoubanPosterUrls.IsUsable("https://img1.doubanio.com/view/photo/s_ratio_poster/public/p123.jpg/q/60/"));
        Assert.False(DoubanPosterUrls.IsUsable("https://image.tmdb.org/t/p/w300/p123.jpg"));
        Assert.False(DoubanPosterUrls.IsUsable(null));

        Assert.True(DoubanPosterUrls.IsUnusableDouban("https://img1.doubanio.com/view/photo/l/public/p123.jpg@qnmob.webp"));
        Assert.True(DoubanPosterUrls.IsUnusableDouban("https://img1.doubanio.com/view/photo/w/300/p123.jpg"));
        Assert.False(DoubanPosterUrls.IsUnusableDouban("https://image.tmdb.org/t/p/w500/p123.jpg"));
        Assert.False(DoubanPosterUrls.IsUnusableDouban(null));
    }

    [Fact]
    public async Task GetAsync_ReturnsSavedBinding_AndUnbindDeletesIt()
    {
        using var connection = database.OpenConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO doubanMetadata (itemKind, entityId, subjectId, subjectUrl, title, rating, fetchedAt)
            VALUES ('movie', 7, '1292052', 'https://movie.douban.com/subject/1292052/', '测试影片', 8.6, @Now)
            """,
            new { Now = DateTimeOffset.UtcNow.ToString("O") });

        var metadata = await service.GetAsync("movie", 7);
        Assert.NotNull(metadata);
        Assert.Equal("1292052", metadata!.SubjectId);
        Assert.Equal("测试影片", metadata.Title);
        Assert.Equal(8.6, metadata.Rating);

        await service.UnbindAsync("movie", 7);
        Assert.Null(await service.GetAsync("movie", 7));
    }

    [Fact]
    public async Task BindAsync_ThrowsRateLimitWhenCooldownIsActive()
    {
        storagePaths.EnsureCreated();
        File.WriteAllText(
            Path.Combine(storagePaths.DataDirectory, "doubanCooldown.json"),
            DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString());

        await Assert.ThrowsAsync<DoubanRateLimitException>(
            () => service.BindAsync("movie", 9, "1292052", "测试影片", null));
    }
    [Fact]
    public void ParseSubjectHtml_RealMobilePage_ExtractsRatingCountAndPoster()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "test-data", "douban-fixture", "mobile-subject.html"));
        var parsed = DoubanMetadataService.ParseSubjectHtml(html);
        Assert.NotNull(parsed);
        Assert.Equal("肖申克的救赎", parsed!.Title);
        Assert.Equal(9.7, parsed.Rating);
        Assert.Equal(3337816, parsed.RatingCount);
        Assert.Equal("1994", parsed.Year);
        Assert.Contains("The Shawshank Redemption", parsed.OriginalTitle);
        Assert.NotNull(parsed.Summary);
        Assert.Contains("安迪", parsed.Summary);
        Assert.NotNull(parsed.PosterUrl);
        Assert.Contains("s_ratio_poster", parsed.PosterUrl);
    }

    [Fact]
    public void ParseSubjectHtml_ChallengePage_HasNoRating()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "test-data", "douban-fixture", "challenge.html"));
        Assert.True(DoubanMetadataService.IsChallengePage(html));
        var parsed = DoubanMetadataService.ParseSubjectHtml(html);
        Assert.NotNull(parsed);
        Assert.Null(parsed!.Rating);
    }

    [Fact]
    public void TrySolveChallenge_ChallengeFixture_FindsValidProofOfWork()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "test-data", "douban-fixture", "challenge.html"));
        Assert.True(DoubanMetadataService.TrySolveChallenge(html, out var result));
        Assert.False(string.IsNullOrEmpty(result.Tok));
        Assert.False(string.IsNullOrEmpty(result.Cha));
        Assert.False(string.IsNullOrEmpty(result.Sol));
        Assert.Equal("https://movie.douban.com/subject/1292052/", result.Red);

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(
            System.Text.Encoding.UTF8.GetBytes(result.Cha + result.Sol))).ToLowerInvariant();
        Assert.Equal("0000", hash[..4]);
    }
}
