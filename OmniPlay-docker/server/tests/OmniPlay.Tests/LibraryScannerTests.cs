using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.FileSystem;
using OmniPlay.Infrastructure.Library;
using Xunit;

namespace OmniPlay.Tests;

public sealed class LibraryScannerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omniplay-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ScanAllIndexesMoviesAndTvEpisodes()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(mediaRoot, "Movies", "霸王别姬 (1993)", "Farewell.My.Concubine.1993.1080p.mkv"));
        Touch(Path.Combine(mediaRoot, "Shows", "绝命毒师", "Season 01", "Breaking.Bad.S01E01.mkv"));
        Touch(Path.Combine(mediaRoot, "Shows", "绝命毒师", "Season 01", "Breaking.Bad.S01E02.mkv"));

        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();

        var sources = new MediaSourceRepository(database);
        await sources.AddLocalAsync("测试媒体", mediaRoot);

        var scanner = new LibraryScanner(database);
        var summary = await scanner.ScanAllAsync();

        var repository = new LibraryRepository(database);
        var items = await repository.GetItemsAsync();

        Assert.Equal(1, summary.SourceCount);
        Assert.Equal(1, summary.NewMovieCount);
        Assert.Equal(1, summary.NewTvShowCount);
        Assert.Equal(3, summary.NewVideoFileCount);
        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.Title == "霸王别姬" && item.ItemKind == "movie" && item.VideoFileCount == 1);
        Assert.Contains(items, item => item.Title == "绝命毒师" && item.ItemKind == "tv" && item.VideoFileCount == 2);
    }

    [Fact]
    public async Task ScanAllStoresProbeMetadataWhenProbeServiceReturnsSnapshot()
    {
        var mediaRoot = Path.Combine(root, "media");
        var moviePath = Path.Combine(mediaRoot, "Movies", "Inception (2010)", "Inception.2010.2160p.mkv");
        Touch(moviePath);

        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        var probeService = new StubMediaProbeService(
            new MediaProbeSnapshot(
                moviePath,
                DurationSeconds: 8888.5,
                Container: "matroska,webm",
                VideoCodec: "hevc",
                AudioCodec: "truehd",
                SubtitleSummary: "subrip",
                RawJson: """
                    {
                      "streams": [
                        {
                          "index": 1,
                          "codec_type": "audio",
                          "codec_name": "truehd",
                          "channels": 8,
                          "channel_layout": "7.1",
                          "tags": { "language": "eng", "title": "English Atmos" },
                          "disposition": { "default": 1, "forced": 0 }
                        },
                        {
                          "index": 3,
                          "codec_type": "subtitle",
                          "codec_name": "subrip",
                          "tags": { "language": "chi", "title": "中文" },
                          "disposition": { "default": 0, "forced": 1 }
                        }
                      ]
                    }
                    """,
                Streams: []));
        var scanner = new LibraryScanner(database, probeService);

        var summary = await scanner.ScanAllAsync();

        var repository = new LibraryRepository(database);
        var item = (await repository.GetItemsAsync()).Single();
        var detail = await repository.GetItemDetailAsync(item.Id);
        Assert.NotNull(detail);
        var file = detail.VideoFiles.Single();

        Assert.Empty(summary.Diagnostics);
        Assert.Equal(8888.5, file.DurationSeconds);
        Assert.Equal("matroska,webm", file.Container);
        Assert.Equal("hevc", file.VideoCodec);
        Assert.Equal("truehd", file.AudioCodec);
        Assert.Equal("subrip", file.SubtitleSummary);
        Assert.Single(file.AudioTracks);
        Assert.Equal(1, file.AudioTracks[0].Index);
        Assert.Equal("eng", file.AudioTracks[0].Language);
        Assert.Equal("English Atmos", file.AudioTracks[0].Title);
        Assert.True(file.AudioTracks[0].IsDefault);
        Assert.Single(file.SubtitleStreams);
        Assert.Equal(3, file.SubtitleStreams[0].Index);
        Assert.Equal("chi", file.SubtitleStreams[0].Language);
        Assert.True(file.SubtitleStreams[0].IsForced);

        var playable = await repository.GetPlayableVideoFileAsync(file.Id);
        Assert.NotNull(playable);
        Assert.Equal(8888.5, playable.DurationSeconds);
        Assert.Equal("hevc", playable.VideoCodec);

        var unchangedSummary = await scanner.ScanAllAsync();
        Assert.Equal(0, unchangedSummary.NewVideoFileCount);
        Assert.Equal(1, probeService.ProbeCount);
    }

    [Fact]
    public async Task ScanAllStillProbesWhenNewItemsAreHiddenUntilScraped()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(mediaRoot, "Movies", "Inception (2010)", "Inception.2010.2160p.mkv"));

        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        var probeService = new CountingMediaProbeService();
        var scanner = new LibraryScanner(database, probeService);

        var summary = await scanner.ScanAllAsync(progress: null, hideNewItemsUntilScraped: true);
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COALESCE(MAX(duration_seconds), 0) FROM video_files WHERE missing_at IS NULL;";
        using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal(1, summary.NewVideoFileCount);
        Assert.Equal(1, probeService.ProbeCount);
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(0, reader.GetDouble(1));
    }

    [Fact]
    public async Task ScanAllMergesBluRayVolumesUnderSameMovieFolder()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(mediaRoot, "Movies", "SplitMovie", "VOL_1", "BDMV", "STREAM", "00000.m2ts"));
        Touch(Path.Combine(mediaRoot, "Movies", "SplitMovie", "VOL_2", "BDMV", "STREAM", "00000.m2ts"));

        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        var summary = await new LibraryScanner(database).ScanAllAsync();

        var item = Assert.Single(await new LibraryRepository(database).GetItemsAsync());
        var detail = await new LibraryRepository(database).GetItemDetailAsync(item.Id);

        Assert.Equal(1, summary.NewMovieCount);
        Assert.Equal(2, summary.NewVideoFileCount);
        Assert.NotNull(detail);
        Assert.Equal("movie", item.ItemKind);
        Assert.Equal(2, item.VideoFileCount);
        Assert.Equal(2, detail.VideoFiles.Count);
    }

    [Fact]
    public async Task ScanAllUsesBdmvStreamFilesInsteadOfPlaylistFiles()
    {
        var mediaRoot = Path.Combine(root, "media");
        var movieFolder = Path.Combine(
            mediaRoot,
            "Movies",
            "L.ultima.volta.che.siamo.stati.bambini.2023.1080p.Blu-ray.AVC.DTS-HD MA 5.1-nan@LuckDIY");
        Touch(Path.Combine(
            movieFolder,
            "BDMV",
            "PLAYLIST",
            "L.ultima.volta.che.siamo.stati.bambini.BD_00002.mkv"));
        Touch(Path.Combine(movieFolder, "BDMV", "STREAM", "00000.m2ts"), [0, 1, 2, 3, 4, 5, 6, 7]);

        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        var summary = await new LibraryScanner(database).ScanAllAsync();

        var item = Assert.Single(await new LibraryRepository(database).GetItemsAsync());
        var detail = await new LibraryRepository(database).GetItemDetailAsync(item.Id);
        var videoFile = Assert.Single(detail!.VideoFiles);

        Assert.Equal(1, summary.NewMovieCount);
        Assert.Equal(1, summary.NewVideoFileCount);
        Assert.Contains("/BDMV/STREAM/00000.m2ts", videoFile.RelativePath.Replace('\\', '/'));
    }

    [Fact]
    public async Task ScanAllMergesDifferentSeasonsOfSameTvShowIntoOneItem()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(mediaRoot, "南家三姐妹", "A.S04E01.2013.mkv"));
        Touch(Path.Combine(mediaRoot, "南家三姐妹", "Z.S01E01.2009.mkv"));

        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        var scanner = new LibraryScanner(database);

        await scanner.ScanAllAsync();

        var repository = new LibraryRepository(database);
        var item = Assert.Single(await repository.GetItemsAsync());
        var detail = await repository.GetItemDetailAsync(item.Id);

        Assert.NotNull(detail);
        Assert.Equal("南家三姐妹", item.Title);
        Assert.Equal("tv", item.ItemKind);
        Assert.Equal(2, item.VideoFileCount);
        Assert.Equal("2009-01-01", item.ReleaseDate);
        Assert.Contains(detail.Seasons, season => season.SeasonNumber == 1);
        Assert.Contains(detail.Seasons, season => season.SeasonNumber == 4);
    }

    [Fact]
    public async Task ScanAllMergesSameTvShowAcrossMediaSources()
    {
        var mediaRootA = Path.Combine(root, "disk-a");
        var mediaRootB = Path.Combine(root, "disk-b");
        Touch(Path.Combine(mediaRootA, "Shows", "绝命毒师", "Season 01", "Breaking.Bad.S01E01.mkv"));
        Touch(Path.Combine(mediaRootB, "TV", "绝命毒师", "Season 02", "Breaking.Bad.S02E01.mkv"));

        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();

        var sources = new MediaSourceRepository(database);
        await sources.AddLocalAsync("硬盘 A", mediaRootA);
        await sources.AddLocalAsync("硬盘 B", mediaRootB);
        var summary = await new LibraryScanner(database).ScanAllAsync();

        var repository = new LibraryRepository(database);
        var item = Assert.Single(await repository.GetItemsAsync());
        var detail = await repository.GetItemDetailAsync(item.Id);

        Assert.NotNull(detail);
        Assert.Equal(1, summary.NewTvShowCount);
        Assert.Equal("绝命毒师", item.Title);
        Assert.Equal(2, item.VideoFileCount);
        Assert.Contains(detail.Seasons, season => season.SeasonNumber == 1);
        Assert.Contains(detail.Seasons, season => season.SeasonNumber == 2);
        Assert.Equal(2, detail.VideoFiles.Select(file => file.SourceId).Distinct().Count());
    }

    [Fact]
    public async Task ScanAllMergesKnownEnglishAndChineseTvAliases()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(
            mediaRoot,
            "怪奇物语.Stranger.Things.S01.2016.2160p.WEB-DL.TrueHD5.1.H265.HDR.DV-Pure@HDSWEB",
            "怪奇物语.Stranger.Things.S01E01.2016.2160p.WEB-DL.TrueHD5.1.H265.HDR.DV-Pure@HDSWEB.mkv"));
        Touch(Path.Combine(
            mediaRoot,
            "Stranger.Things.S04.2160p.NF.WEB-DL.DDP.5.1.Atmos.HDR10.H.265-CHDWEB",
            "Stranger.Things.S04E01.Chapter.One.The.Hellfire.Club.2160p.NF.WEB-DL.DDP.5.1.Atmos.HDR10.H.265-CHDWEB.mkv"));

        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync();

        var repository = new LibraryRepository(database);
        var item = Assert.Single(await repository.GetItemsAsync());
        var detail = await repository.GetItemDetailAsync(item.Id);

        Assert.NotNull(detail);
        Assert.Equal("怪奇物语", item.Title);
        Assert.Equal("tv", item.ItemKind);
        Assert.Equal(2, item.VideoFileCount);
        Assert.Contains(detail.Seasons, season => season.SeasonNumber == 1);
        Assert.Contains(detail.Seasons, season => season.SeasonNumber == 4);
    }

    [Fact]
    public async Task ScanAllMergesKnownHayateSeasonAliases()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(
            mediaRoot,
            "Hayate no Gotoku Cuties S04 2013 1080p BluRay Remux AVC DTS-HD MA 2.0-LuckAni",
            "Hayate no Gotoku Cuties S04E01 2013 1080p BluRay Remux AVC DTS-HD MA 2.0-LuckAni.mkv"));
        Touch(Path.Combine(
            mediaRoot,
            "Hayate the Combat Butler Season 2 S02 2009 1080p BluRay Remux AVC DTS 2.0-LuckAni",
            "Hayate the Combat Butler Season 2 S02E01 2009 1080p BluRay Remux AVC DTS 2.0-LuckAni.mkv"));

        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync();

        var repository = new LibraryRepository(database);
        var item = Assert.Single(await repository.GetItemsAsync());
        var detail = await repository.GetItemDetailAsync(item.Id);

        Assert.NotNull(detail);
        Assert.Equal("旋风管家", item.Title);
        Assert.Equal("tv", item.ItemKind);
        Assert.Equal(2, item.VideoFileCount);
        Assert.Contains(detail.Seasons, season => season.SeasonNumber == 2);
        Assert.Contains(detail.Seasons, season => season.SeasonNumber == 4);
    }

    [Fact]
    public async Task ScanAllAddsSubtitlesOnlyForDuplicateEpisodeNumbers()
    {
        var mediaRoot = Path.Combine(root, "media");
        var showFolder = Path.Combine(mediaRoot, "Shows", "乘风破浪的姐姐.Sisters.Who.Make.Waves.S07.2026");
        Touch(Path.Combine(
            showFolder,
            "乘风破浪的姐姐.播客.Sisters.Who.Make.Waves.S07E01.Talk.2026.2160p.WEB-DL.H265.AAC-ADWeb.mkv"));
        Touch(Path.Combine(
            showFolder,
            "乘风破浪的姐姐.企划.Sisters.Who.Make.Waves.S07E01.Program.2026.2160p.WEB-DL.H265.AAC-ADWeb.mkv"));
        Touch(Path.Combine(
            showFolder,
            "乘风破浪的姐姐.Sisters.Who.Make.Waves.S07E02.2026.2160p.WEB-DL.H265.AAC-ADWeb.mkv"));

        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync();

        var repository = new LibraryRepository(database);
        var item = Assert.Single(await repository.GetItemsAsync());
        var detail = await repository.GetItemDetailAsync(item.Id);

        Assert.NotNull(detail);
        var season = Assert.Single(detail.Seasons);
        Assert.Equal(7, season.SeasonNumber);
        Assert.Equal(3, season.Episodes.Count);
        Assert.Contains(season.Episodes, episode => episode.EpisodeNumber == 1 && episode.Title == "第 7 季 第 1 集·播客");
        Assert.Contains(season.Episodes, episode => episode.EpisodeNumber == 1 && episode.Title == "第 7 季 第 1 集·企划");
        Assert.Contains(season.Episodes, episode => episode.EpisodeNumber == 2 && episode.Title == "乘风破浪的姐姐 第 7 季 第 2 集");
        Assert.All(season.Episodes, episode => Assert.NotNull(episode.VideoFile));
    }

    [Fact]
    public async Task ScanSourceIndexesOnlyRequestedSourceAndStoresLastScannedAt()
    {
        var mediaRootA = Path.Combine(root, "media-a");
        var mediaRootB = Path.Combine(root, "media-b");
        Touch(Path.Combine(mediaRootA, "Movie.A.2020.mkv"));
        Touch(Path.Combine(mediaRootB, "Movie.B.2021.mkv"));

        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();

        var mediaSources = new MediaSourceRepository(database);
        var sourceA = await mediaSources.AddLocalAsync("源 A", mediaRootA);
        var sourceB = await mediaSources.AddLocalAsync("源 B", mediaRootB);
        var scanner = new LibraryScanner(database);

        var summary = await scanner.ScanSourceAsync(sourceA.Id, progress: null);
        var items = await new LibraryRepository(database).GetItemsAsync();
        var sources = await mediaSources.GetAllAsync();

        Assert.Equal(1, summary.SourceCount);
        Assert.Equal(1, summary.NewVideoFileCount);
        Assert.Single(items);
        Assert.Contains(items, item => item.Title == "Movie A");
        Assert.NotNull(sources.Single(source => source.Id == sourceA.Id).LastScannedAt);
        Assert.Null(sources.Single(source => source.Id == sourceB.Id).LastScannedAt);
    }

    [Fact]
    public async Task RemovedSourceImmediatelyDisappearsFromLibrary()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(mediaRoot, "Movie.A.2020.mkv"));

        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();

        var mediaSources = new MediaSourceRepository(database);
        var source = await mediaSources.AddLocalAsync("源 A", mediaRoot);
        var scanner = new LibraryScanner(database);
        await scanner.ScanAllAsync();

        var repository = new LibraryRepository(database);
        Assert.Single(await repository.GetItemsAsync());

        Assert.True(await mediaSources.RemoveAsync(source.Id));

        Assert.Empty(await mediaSources.GetAllAsync());
        Assert.Empty(await repository.GetItemsAsync());
    }

    [Fact]
    public async Task DisabledSourceDisappearsFromLibraryWithoutDeletingIndexedMetadata()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(mediaRoot, "Movie.A.2020.mkv"));

        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();

        var mediaSources = new MediaSourceRepository(database);
        var source = await mediaSources.AddLocalAsync("源 A", mediaRoot);
        var scanner = new LibraryScanner(database);
        await scanner.ScanAllAsync();

        var repository = new LibraryRepository(database);
        var item = Assert.Single(await repository.GetItemsAsync());
        Assert.True(await repository.ApplyMetadataMatchAsync(new LibraryItemMetadataApplyRequest(
            item.Id,
            TmdbId: 100,
            MediaType: "movie",
            Title: "保留的元数据",
            Overview: "停用媒体源不应删除元数据。",
            ReleaseDate: "2020-01-01",
            PosterPath: null,
            VoteAverage: 8.1,
            PosterLocalPath: null,
            LockMetadata: true)));

        Assert.NotNull(await mediaSources.UpdateAsync(source.Id, new UpdateMediaSourceRequest(IsEnabled: false)));
        Assert.Empty(await repository.GetItemsAsync());

        Assert.NotNull(await mediaSources.UpdateAsync(source.Id, new UpdateMediaSourceRequest(IsEnabled: true)));
        var restored = Assert.Single(await repository.GetItemsAsync());
        Assert.Equal("保留的元数据", restored.Title);
        Assert.Equal(8.1, restored.VoteAverage);
    }

    [Fact]
    public async Task ScanSourceIndexesWebDavFilesWithoutMediaProbe()
    {
        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();

        var mediaSources = new MediaSourceRepository(database);
        var source = await mediaSources.AddWebDavAsync("远程媒体", "https://example.com/dav", "user", "secret");
        var probeService = new CountingMediaProbeService();
        var webDavEnumerator = new StubWebDavFileEnumerator(
            new WebDavFileEntry(
                "Inception.2010.mkv",
                "https://example.com/dav/Movies/Inception.2010.mkv",
                "Movies/Inception (2010)/Inception.2010.mkv",
                1024,
                DateTimeOffset.Parse("2026-05-03T08:00:00Z")),
            new WebDavFileEntry(
                "Breaking.Bad.S01E01.mkv",
                "https://example.com/dav/Shows/Breaking.Bad/Season 01/Breaking.Bad.S01E01.mkv",
                "Shows/绝命毒师/Season 01/Breaking.Bad.S01E01.mkv",
                2048,
                DateTimeOffset.Parse("2026-05-03T09:00:00Z")),
            new WebDavFileEntry(
                "readme.txt",
                "https://example.com/dav/readme.txt",
                "readme.txt",
                12,
                null));
        var scanner = new LibraryScanner(database, probeService, webDavEnumerator);

        var summary = await scanner.ScanSourceAsync(source.Id, progress: null);
        var items = await new LibraryRepository(database).GetItemsAsync();
        var sources = await mediaSources.GetAllAsync();

        Assert.Equal(1, summary.SourceCount);
        Assert.Equal(1, summary.NewMovieCount);
        Assert.Equal(1, summary.NewTvShowCount);
        Assert.Equal(2, summary.NewVideoFileCount);
        Assert.Equal(0, probeService.ProbeCount);
        Assert.Equal("https://example.com/dav", webDavEnumerator.RootUrl);
        Assert.Equal("user", webDavEnumerator.Username);
        Assert.Equal("secret", webDavEnumerator.Password);
        Assert.Contains(items, item => item.Title == "Inception" && item.ItemKind == "movie");
        Assert.Contains(items, item => item.Title == "绝命毒师" && item.ItemKind == "tv");
        Assert.NotNull(sources.Single(item => item.Id == source.Id).LastScannedAt);
    }

    private static void Touch(string path, byte[]? bytes = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes ?? [0, 1, 2, 3]);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StubMediaProbeService : IMediaProbeService
    {
        private readonly MediaProbeSnapshot snapshot;

        public StubMediaProbeService(MediaProbeSnapshot snapshot)
        {
            this.snapshot = snapshot;
        }

        public int ProbeCount { get; private set; }

        public Task<MediaProbeSnapshot?> ProbeAsync(string filePath, CancellationToken cancellationToken)
        {
            ProbeCount++;
            return Task.FromResult<MediaProbeSnapshot?>(snapshot);
        }
    }

    private sealed class CountingMediaProbeService : IMediaProbeService
    {
        public int ProbeCount { get; private set; }

        public Task<MediaProbeSnapshot?> ProbeAsync(string filePath, CancellationToken cancellationToken)
        {
            ProbeCount++;
            return Task.FromResult<MediaProbeSnapshot?>(null);
        }
    }

    private sealed class StubWebDavFileEnumerator : IWebDavFileEnumerator
    {
        private readonly IReadOnlyList<WebDavFileEntry> files;

        public StubWebDavFileEnumerator(params WebDavFileEntry[] files)
        {
            this.files = files;
        }

        public string? RootUrl { get; private set; }

        public string? Username { get; private set; }

        public string? Password { get; private set; }

        public Task<IReadOnlyList<WebDavFileEntry>> EnumerateFilesAsync(
            string rootUrl,
            string? username,
            string? password,
            CancellationToken cancellationToken = default)
        {
            RootUrl = rootUrl;
            Username = username;
            Password = password;
            return Task.FromResult(files);
        }
    }
}
