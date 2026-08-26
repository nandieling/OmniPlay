using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.FileSystem;
using OmniPlay.Infrastructure.Library;
using Xunit;

namespace OmniPlay.Tests;

public sealed class LibraryMetadataEnricherTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omniplay-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EnrichMissingUpdatesMetadataAndPosterAsset()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(mediaRoot, "Movies", "Farewell.My.Concubine.1993.1080p.mkv"));

        var paths = new StoragePaths(Path.Combine(root, "app"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync();

        var fakeTmdb = new FakeTmdbMetadataClient(paths);
        var enricher = new LibraryMetadataEnricher(
            database,
            new AppSettingsRepository(database),
            fakeTmdb);

        var summary = await enricher.EnrichMissingAsync();
        var items = await new LibraryRepository(database).GetItemsAsync();

        Assert.Equal(1, summary.ScannedItems);
        Assert.Equal(1, summary.MatchedItems);
        Assert.Equal(1, summary.UpdatedItems);
        Assert.Equal(1, summary.DownloadedPosters);
        Assert.Single(items);
        Assert.Equal("霸王别姬", items[0].Title);
        Assert.Equal("一段横跨时代的故事。", items[0].Overview);
        Assert.Equal(8.2, items[0].VoteAverage);
        Assert.False(string.IsNullOrWhiteSpace(items[0].PosterAssetId));
        Assert.True(items[0].IsLocked);

        var detail = await new LibraryRepository(database).GetItemDetailAsync(items[0].Id);
        Assert.NotNull(detail);
        Assert.Equal(1, detail.TmdbId);
    }

    [Fact]
    public async Task EnrichMissingMergesMovieItemsWithSameTmdbId()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(mediaRoot, "Movies", "Disc1", "Farewell.My.Concubine.1993.1080p.mkv"));
        Touch(Path.Combine(mediaRoot, "Movies", "Disc2", "Farewell.My.Concubine.1993.1080p.mkv"));

        var paths = new StoragePaths(Path.Combine(root, "app"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync();
        Assert.Equal(2, (await new LibraryRepository(database).GetItemsAsync()).Count);

        var fakeTmdb = new FakeTmdbMetadataClient(paths);
        var enricher = new LibraryMetadataEnricher(
            database,
            new AppSettingsRepository(database),
            fakeTmdb);

        await enricher.EnrichMissingAsync();

        var repository = new LibraryRepository(database);
        var item = Assert.Single(await repository.GetItemsAsync());
        var detail = await repository.GetItemDetailAsync(item.Id);

        Assert.NotNull(detail);
        Assert.Equal("霸王别姬", item.Title);
        Assert.Equal(2, item.VideoFileCount);
        Assert.Equal(2, detail.VideoFiles.Count);
        Assert.Equal(1, detail.TmdbId);
    }

    [Fact]
    public async Task EnrichMissingMergesTvItemsWithSameTmdbIdAndPreservesSeasons()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(mediaRoot, "示例剧上篇", "Example.Show.S01E01.mkv"));
        Touch(Path.Combine(mediaRoot, "示例剧下篇", "Example.Show.S02E01.mkv"));

        var paths = new StoragePaths(Path.Combine(root, "app"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync();
        Assert.Equal(2, (await new LibraryRepository(database).GetItemsAsync()).Count);

        var match = new TmdbMetadataMatch(
            2026,
            "tv",
            "示例剧",
            "同一部剧分布在不同目录。",
            "2026-01-01",
            "/example-show.jpg",
            8.0,
            20);
        var fakeTmdb = new FakeTmdbMetadataClient(paths);
        fakeTmdb.SearchMatchesByTitle["示例剧上篇"] = match;
        fakeTmdb.SearchMatchesByTitle["示例剧下篇"] = match;
        var enricher = new LibraryMetadataEnricher(
            database,
            new AppSettingsRepository(database),
            fakeTmdb);

        await enricher.EnrichMissingAsync();

        var repository = new LibraryRepository(database);
        var item = Assert.Single(await repository.GetItemsAsync());
        var detail = await repository.GetItemDetailAsync(item.Id);

        Assert.NotNull(detail);
        Assert.Equal("示例剧", item.Title);
        Assert.Equal(2, item.VideoFileCount);
        Assert.Equal(2026, detail.TmdbId);
        Assert.Contains(detail.Seasons, season => season.SeasonNumber == 1);
        Assert.Contains(detail.Seasons, season => season.SeasonNumber == 2);
        Assert.Equal(2, detail.VideoFiles.Count);
    }

    [Fact]
    public async Task EnrichMissingSkipsItemsLockedByPreviousScrape()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(mediaRoot, "Movies", "Farewell.My.Concubine.1993.1080p.mkv"));

        var paths = new StoragePaths(Path.Combine(root, "app"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync();

        var fakeTmdb = new FakeTmdbMetadataClient(paths);
        var enricher = new LibraryMetadataEnricher(
            database,
            new AppSettingsRepository(database),
            fakeTmdb);

        await enricher.EnrichMissingAsync();
        fakeTmdb.ResetCounters();
        fakeTmdb.SearchMatch = new TmdbMetadataMatch(
            2,
            "movie",
            "不应覆盖",
            "已锁定条目不应再次批量刮削。",
            "2000-01-01",
            "/other.jpg",
            1.1,
            10);

        var summary = await enricher.EnrichMissingAsync();
        var item = (await new LibraryRepository(database).GetItemsAsync()).Single();

        Assert.Equal(0, summary.ScannedItems);
        Assert.Equal(0, fakeTmdb.SearchRequestCount);
        Assert.Equal("霸王别姬", item.Title);
        Assert.True(item.IsLocked);
    }

    [Fact]
    public async Task EnrichMissingContinuesAfterPosterFailureAndRevealsUnmatchedAtEnd()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(mediaRoot, "失败影片.2020.mkv"));
        Touch(Path.Combine(mediaRoot, "成功影片.2021.mkv"));

        var paths = new StoragePaths(Path.Combine(root, "app"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync(null, hideNewItemsUntilScraped: true);

        var fakeTmdb = new FakeTmdbMetadataClient(paths);
        fakeTmdb.SearchMatchesByTitle["失败影片"] = new TmdbMetadataMatch(
            101,
            "movie",
            "失败影片",
            "海报下载会失败。",
            "2020-01-01",
            "/broken.jpg",
            6.1,
            10);
        fakeTmdb.SearchMatchesByTitle["成功影片"] = new TmdbMetadataMatch(
            102,
            "movie",
            "成功影片",
            "海报下载正常。",
            "2021-01-01",
            "/good.jpg",
            7.2,
            20);
        fakeTmdb.FailingPosterPaths.Add("/broken.jpg");
        var enricher = new LibraryMetadataEnricher(
            database,
            new AppSettingsRepository(database),
            fakeTmdb);

        var summary = await enricher.EnrichMissingAsync();
        var items = await new LibraryRepository(database).GetItemsAsync();

        Assert.Equal(2, summary.ScannedItems);
        Assert.Equal(2, summary.MatchedItems);
        Assert.Equal(1, summary.UpdatedItems);
        Assert.Equal(1, summary.DownloadedPosters);
        Assert.Contains(summary.Diagnostics, item => item.Contains("主海报下载失败", StringComparison.Ordinal));
        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.Title == "成功影片" && !string.IsNullOrWhiteSpace(item.PosterAssetId));
        Assert.Contains(items, item => item.Title == "失败影片" && string.IsNullOrWhiteSpace(item.PosterAssetId));
    }

    [Fact]
    public async Task EnrichMissingRevealsFailedItemsBeforeRefreshingEpisodeStills()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(mediaRoot, "失败影片.2020.mkv"));
        Touch(Path.Combine(
            mediaRoot,
            "Shows",
            "示例剧.Example.Show.2022.S01",
            "示例剧.Example.Show.2022.S01E01.mkv"));

        var paths = new StoragePaths(Path.Combine(root, "app"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync(null, hideNewItemsUntilScraped: true);

        var fakeTmdb = new FakeTmdbMetadataClient(paths);
        fakeTmdb.SearchMatchesByTitle["失败影片"] = new TmdbMetadataMatch(
            201,
            "movie",
            "失败影片",
            "海报下载会失败。",
            "2020-01-01",
            "/broken-order.jpg",
            6.1,
            10);
        fakeTmdb.SearchMatchesByTitle["示例剧"] = new TmdbMetadataMatch(
            202,
            "tv",
            "示例剧",
            "剧集简介。",
            "2022-01-01",
            "/show-order.jpg",
            7.5,
            20);
        fakeTmdb.FailingPosterPaths.Add("/broken-order.jpg");
        fakeTmdb.SeasonDetails[1] = new TmdbSeasonDetail(
            1,
            "第 1 季",
            "第一季简介。",
            "2022-01-01",
            "/season-order.jpg",
            [
                new TmdbEpisodeDetail(
                    1,
                    "第 1 集",
                    "分集简介。",
                    "2022-01-01",
                    "/still-order.jpg")
            ]);
        fakeTmdb.BeforeSeasonRequestAsync = async () =>
        {
            var visibleItems = await new LibraryRepository(database).GetItemsAsync();
            Assert.Contains(visibleItems, item => item.Title == "失败影片");
        };
        var enricher = new LibraryMetadataEnricher(
            database,
            new AppSettingsRepository(database),
            fakeTmdb);

        var summary = await enricher.EnrichMissingAsync();

        Assert.Equal(2, summary.ScannedItems);
        Assert.Equal(1, fakeTmdb.SeasonRequestCount);
    }

    [Fact]
    public async Task EnrichItemUsesSavedTmdbIdForPreciseRefresh()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(mediaRoot, "Movies", "Farewell.My.Concubine.1993.1080p.mkv"));

        var paths = new StoragePaths(Path.Combine(root, "app"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync();

        var fakeTmdb = new FakeTmdbMetadataClient(paths);
        var enricher = new LibraryMetadataEnricher(
            database,
            new AppSettingsRepository(database),
            fakeTmdb);

        await enricher.EnrichMissingAsync();
        var item = (await new LibraryRepository(database).GetItemsAsync()).Single();

        fakeTmdb.ResetCounters();
        fakeTmdb.SearchMatch = new TmdbMetadataMatch(
            999,
            "movie",
            "错误搜索结果",
            "标题搜索不应被使用。",
            "1999-01-01",
            "/wrong.jpg",
            3.1,
            1);
        fakeTmdb.DetailMatch = new TmdbMetadataMatch(
            1,
            "movie",
            "霸王别姬 4K 修复版",
            "按已保存 TMDB ID 精确刷新的简介。",
            "1993-01-01",
            "/poster-4k.jpg",
            8.9,
            20);

        var summary = await enricher.EnrichItemAsync(item.Id);
        var detail = await new LibraryRepository(database).GetItemDetailAsync(item.Id);

        Assert.Equal(1, summary.MatchedItems);
        Assert.Equal(1, summary.UpdatedItems);
        Assert.Equal(0, fakeTmdb.SearchRequestCount);
        Assert.Equal(1, fakeTmdb.DetailRequestCount);
        Assert.Equal(1, fakeTmdb.LastDetailTmdbId);
        Assert.NotNull(detail);
        Assert.Equal("霸王别姬 4K 修复版", detail.Title);
        Assert.Equal("按已保存 TMDB ID 精确刷新的简介。", detail.Overview);
        Assert.Equal(8.9, detail.VoteAverage);
        Assert.Equal(1, detail.TmdbId);
    }

    [Fact]
    public async Task EnrichItemRefreshesExistingTvEpisodeMetadata()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(mediaRoot, "Shows", "Breaking Bad", "Season 01", "Breaking.Bad.S01E01.mkv"));
        Touch(Path.Combine(mediaRoot, "Shows", "Breaking Bad", "Season 01", "Breaking.Bad.S01E02.mkv"));

        var paths = new StoragePaths(Path.Combine(root, "app"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync();

        var fakeTmdb = new FakeTmdbMetadataClient(paths)
        {
            SearchMatch = new TmdbMetadataMatch(
                1396,
                "tv",
                "绝命毒师",
                "一名化学老师的故事。",
                "2008-01-20",
                "/show.jpg",
                8.9,
                100)
        };
        fakeTmdb.SeasonDetails[1] = new TmdbSeasonDetail(
            1,
            "第 1 季",
            "第一季简介。",
            "2008-01-20",
            "/season.jpg",
            [
                new TmdbEpisodeDetail(
                    1,
                    "试播集",
                    "沃尔特面临人生转折。",
                    "2008-01-20",
                    "/still-1.jpg"),
                new TmdbEpisodeDetail(
                    2,
                    "猫在袋里",
                    "杰西和沃尔特处理后果。",
                    "2008-01-27",
                    "/still-2.jpg"),
                new TmdbEpisodeDetail(
                    3,
                    "本地没有的集",
                    "不应创建新分集。",
                    "2008-02-10",
                    "/still-3.jpg")
            ]);

        var enricher = new LibraryMetadataEnricher(
            database,
            new AppSettingsRepository(database),
            fakeTmdb);

        var item = (await new LibraryRepository(database).GetItemsAsync()).Single();
        var progressReports = new List<LibraryMetadataEnrichmentProgress>();
        var summary = await enricher.EnrichItemAsync(item.Id, new CapturingMetadataProgress(progressReports));
        var detail = await new LibraryRepository(database).GetItemDetailAsync(item.Id);
        var episodeProgress = progressReports
            .Last(report => report.Phase == "fetching-episodes" && report.PhaseTargetCount.HasValue);

        Assert.Equal(1, summary.MatchedItems);
        Assert.Equal(4, summary.DownloadedPosters);
        Assert.Equal(1, fakeTmdb.SeasonRequestCount);
        Assert.Equal(new[] { 1 }, fakeTmdb.LastSeasonNumbers);
        Assert.Equal(2, episodeProgress.PhaseTargetCount);
        Assert.Equal(2, episodeProgress.PhaseProcessedCount);
        Assert.NotNull(detail);
        Assert.Equal(1396, detail.TmdbId);
        Assert.Single(detail.Seasons);
        Assert.Equal("第 1 季", detail.Seasons[0].Title);
        Assert.False(string.IsNullOrWhiteSpace(detail.Seasons[0].PosterAssetId));
        Assert.Equal(2, detail.Seasons[0].Episodes.Count);
        Assert.Equal("试播集", detail.Seasons[0].Episodes[0].Title);
        Assert.Equal("沃尔特面临人生转折。", detail.Seasons[0].Episodes[0].Overview);
        Assert.Equal("2008-01-20", detail.Seasons[0].Episodes[0].AirDate);
        Assert.False(string.IsNullOrWhiteSpace(detail.Seasons[0].Episodes[0].StillAssetId));
        Assert.Equal("猫在袋里", detail.Seasons[0].Episodes[1].Title);
        Assert.Equal("杰西和沃尔特处理后果。", detail.Seasons[0].Episodes[1].Overview);
        Assert.Equal("2008-01-27", detail.Seasons[0].Episodes[1].AirDate);
        Assert.False(string.IsNullOrWhiteSpace(detail.Seasons[0].Episodes[1].StillAssetId));
    }

    [Fact]
    public async Task EnrichItemPreservesLocalEpisodeSubtitleWhenRefreshingDuplicateEpisodeNumbers()
    {
        var mediaRoot = Path.Combine(root, "media");
        var showFolder = Path.Combine(mediaRoot, "Shows", "乘风破浪的姐姐.Sisters.Who.Make.Waves.S07.2026");
        Touch(Path.Combine(
            showFolder,
            "乘风破浪的姐姐.播客.Sisters.Who.Make.Waves.S07E01.Talk.2026.2160p.WEB-DL.H265.AAC-ADWeb.mkv"));
        Touch(Path.Combine(
            showFolder,
            "乘风破浪的姐姐.企划.Sisters.Who.Make.Waves.S07E01.Program.2026.2160p.WEB-DL.H265.AAC-ADWeb.mkv"));

        var paths = new StoragePaths(Path.Combine(root, "app"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync();

        var fakeTmdb = new FakeTmdbMetadataClient(paths)
        {
            SearchMatch = new TmdbMetadataMatch(
                300,
                "tv",
                "乘风破浪的姐姐",
                "综艺节目。",
                "2026-01-01",
                "/sisters.jpg",
                7.1,
                10)
        };
        fakeTmdb.SeasonDetails[7] = new TmdbSeasonDetail(
            7,
            "乘风破浪的姐姐 第七季",
            "第七季简介。",
            "2026-01-01",
            "/sisters-season.jpg",
            [
                new TmdbEpisodeDetail(
                    1,
                    "正片",
                    "第一期。",
                    "2026-01-01",
                    "/sisters-still.jpg")
            ]);

        var item = Assert.Single(await new LibraryRepository(database).GetItemsAsync());
        var enricher = new LibraryMetadataEnricher(
            database,
            new AppSettingsRepository(database),
            fakeTmdb);

        await enricher.EnrichItemAsync(item.Id);
        var detail = await new LibraryRepository(database).GetItemDetailAsync(item.Id);

        Assert.NotNull(detail);
        var season = Assert.Single(detail.Seasons);
        Assert.Equal("第 7 季", season.Title);
        Assert.Contains(season.Episodes, episode => episode.Title == "正片·播客");
        Assert.Contains(season.Episodes, episode => episode.Title == "正片·企划");
    }

    [Fact]
    public async Task EnrichMissingUsesSourceForeignTitleAsSecondaryQuery()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(
            mediaRoot,
            "Shows",
            "足球教练.Ted.Lasso.2020.S01.2160p.ATVP.WEB-DL.H265",
            "Ted.Lasso.2020.S01E01.2160p.ATVP.WEB-DL.H265.mkv"));

        var paths = new StoragePaths(Path.Combine(root, "app"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync();

        var fakeTmdb = new FakeTmdbMetadataClient(paths)
        {
            SearchMatch = new TmdbMetadataMatch(
                97546,
                "tv",
                "足球教练",
                "美式足球教练执教英超球队。",
                "2020-08-14",
                "/ted-lasso.jpg",
                8.4,
                80)
        };
        var enricher = new LibraryMetadataEnricher(
            database,
            new AppSettingsRepository(database),
            fakeTmdb);

        await enricher.EnrichMissingAsync();

        Assert.Equal("足球教练", fakeTmdb.LastSearchTitle);
        Assert.Equal("Ted Lasso", fakeTmdb.LastSearchSecondaryQuery);
        Assert.Equal("2020", fakeTmdb.LastSearchYear);
    }

    [Fact]
    public async Task EnrichMissingKeepsSourceChineseTitleWhenTmdbTitleIsEnglish()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(
            mediaRoot,
            "Shows",
            "操控游戏.The.Manipulated.S01.2025",
            "操控游戏.The.Manipulated.S01E01.2025.2160p.DSNP.WEB-DL.DDP5.1.H265.HDR.DV-Pure@HDSWEB.mkv"));

        var paths = new StoragePaths(Path.Combine(root, "app"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync();

        var fakeTmdb = new FakeTmdbMetadataClient(paths)
        {
            SearchMatch = new TmdbMetadataMatch(
                247718,
                "tv",
                "The Manipulated",
                "A revenge story.",
                "2025-11-05",
                "/manipulated.jpg",
                8.1,
                50)
        };
        var enricher = new LibraryMetadataEnricher(
            database,
            new AppSettingsRepository(database),
            fakeTmdb);

        await enricher.EnrichMissingAsync();
        var item = (await new LibraryRepository(database).GetItemsAsync()).Single();

        Assert.Equal("操控游戏", item.Title);
        Assert.Equal("操控游戏", fakeTmdb.LastSearchTitle);
        Assert.Equal("The Manipulated", fakeTmdb.LastSearchSecondaryQuery);
    }

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0, 1, 2, 3]);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeTmdbMetadataClient : OmniPlay.Core.Interfaces.ITmdbMetadataClient
    {
        private readonly StoragePaths paths;

        public FakeTmdbMetadataClient(StoragePaths paths)
        {
            this.paths = paths;
        }

        public int SearchRequestCount { get; private set; }

        public int DetailRequestCount { get; private set; }

        public int SeasonRequestCount { get; private set; }

        public int? LastDetailTmdbId { get; private set; }

        public string? LastSearchTitle { get; private set; }

        public string? LastSearchYear { get; private set; }

        public string? LastSearchSecondaryQuery { get; private set; }

        public List<int> LastSeasonNumbers { get; } = [];

        public Dictionary<string, TmdbMetadataMatch> SearchMatchesByTitle { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> FailingPosterPaths { get; } = new(StringComparer.Ordinal);

        public HashSet<string> FailingStillPaths { get; } = new(StringComparer.Ordinal);

        public Func<Task>? BeforeSeasonRequestAsync { get; set; }

        public TmdbMetadataMatch SearchMatch { get; set; } = new(
            1,
            "movie",
            "霸王别姬",
            "一段横跨时代的故事。",
            "1993-01-01",
            "/poster.jpg",
            8.2,
            10);

        public TmdbMetadataMatch? DetailMatch { get; set; }

        public Dictionary<int, TmdbSeasonDetail> SeasonDetails { get; } = [];

        public void ResetCounters()
        {
            SearchRequestCount = 0;
            DetailRequestCount = 0;
            SeasonRequestCount = 0;
            LastDetailTmdbId = null;
            LastSearchTitle = null;
            LastSearchYear = null;
            LastSearchSecondaryQuery = null;
            LastSeasonNumbers.Clear();
        }

        public Task<TmdbConnectionTestResult> TestConnectionAsync(
            TmdbSettings settings,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TmdbConnectionTestResult(true, "fake", 200, "ok"));
        }

        public Task<TmdbMetadataMatch?> SearchAsync(
            string mediaType,
            string title,
            string? year,
            TmdbSettings settings,
            string? secondaryQuery = null,
            CancellationToken cancellationToken = default)
        {
            SearchRequestCount++;
            LastSearchTitle = title;
            LastSearchYear = year;
            LastSearchSecondaryQuery = secondaryQuery;
            var match = SearchMatchesByTitle.TryGetValue(title, out var titleMatch)
                ? titleMatch
                : SearchMatch;
            return Task.FromResult<TmdbMetadataMatch?>(match with { MediaType = mediaType });
        }

        public Task<IReadOnlyList<TmdbMetadataMatch>> SearchCandidatesAsync(
            string mediaType,
            string title,
            string? year,
            TmdbSettings settings,
            string? secondaryQuery = null,
            int limit = 8,
            CancellationToken cancellationToken = default)
        {
            LastSearchTitle = title;
            LastSearchYear = year;
            LastSearchSecondaryQuery = secondaryQuery;
            IReadOnlyList<TmdbMetadataMatch> matches =
            [
                SearchMatch with { MediaType = mediaType }
            ];
            return Task.FromResult(matches);
        }

        public Task<TmdbMetadataMatch?> GetDetailsAsync(
            string mediaType,
            int tmdbId,
            TmdbSettings settings,
            CancellationToken cancellationToken = default)
        {
            DetailRequestCount++;
            LastDetailTmdbId = tmdbId;
            return Task.FromResult<TmdbMetadataMatch?>(
                (DetailMatch ?? SearchMatch) with { Id = tmdbId, MediaType = mediaType });
        }

        public async Task<TmdbSeasonDetail?> GetSeasonAsync(
            int tvTmdbId,
            int seasonNumber,
            TmdbSettings settings,
            CancellationToken cancellationToken = default)
        {
            if (BeforeSeasonRequestAsync is not null)
            {
                await BeforeSeasonRequestAsync();
            }

            SeasonRequestCount++;
            LastSeasonNumbers.Add(seasonNumber);
            SeasonDetails.TryGetValue(seasonNumber, out var season);
            return season;
        }

        public Task<string?> DownloadPosterAsync(
            string posterPath,
            string mediaType,
            int tmdbId,
            CancellationToken cancellationToken = default)
        {
            if (FailingPosterPaths.Contains(posterPath))
            {
                throw new HttpRequestException("poster unavailable");
            }

            paths.EnsureCreated();
            var poster = Path.Combine(paths.PostersDirectory, $"poster-{tmdbId}.jpg");
            File.WriteAllBytes(poster, [1, 2, 3]);
            return Task.FromResult<string?>(poster);
        }

        public Task<string?> DownloadStillAsync(
            string stillPath,
            int tvTmdbId,
            int seasonNumber,
            int episodeNumber,
            CancellationToken cancellationToken = default)
        {
            if (FailingStillPaths.Contains(stillPath))
            {
                throw new HttpRequestException("still unavailable");
            }

            paths.EnsureCreated();
            var still = Path.Combine(paths.ThumbnailsDirectory, $"s{seasonNumber:00}e{episodeNumber:00}.jpg");
            File.WriteAllBytes(still, [4, 5, 6]);
            return Task.FromResult<string?>(still);
        }
    }

    private sealed class CapturingMetadataProgress : IProgress<LibraryMetadataEnrichmentProgress>
    {
        private readonly List<LibraryMetadataEnrichmentProgress> reports;

        public CapturingMetadataProgress(List<LibraryMetadataEnrichmentProgress> reports)
        {
            this.reports = reports;
        }

        public void Report(LibraryMetadataEnrichmentProgress value)
        {
            reports.Add(value);
        }
    }
}
