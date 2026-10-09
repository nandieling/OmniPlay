using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Dapper;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Core.Models.Library;
using OmniPlay.Core.Runtime;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.Library;
using OmniPlay.Infrastructure.Tmdb;

namespace OmniPlay.Tests;

/// <summary>
/// 导演/主演名单、中文译名与人物档案的行为约束（对齐 mac 版
/// PeopleTests / ChineseNameStoreTests）。
/// </summary>
[Collection("Locale")]
public sealed class PeopleChineseNameTests : IDisposable
{
    private readonly TestStoragePaths storagePaths;
    private readonly SqliteDatabase database;

    public PeopleChineseNameTests()
    {
        var rootPath = Path.Combine(
            AppContext.BaseDirectory,
            "test-data",
            nameof(PeopleChineseNameTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        storagePaths = new TestStoragePaths(rootPath);
        database = new SqliteDatabase(storagePaths);
        database.EnsureInitialized();
        Locale.Set(Locale.Chinese);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void PersonNames_DecodesJsonAndLegacyFlatText()
    {
        var encoded = PersonNames.Encode(["克里斯托弗·诺兰", "艾玛·托马斯", "克里斯托弗·诺兰"]);
        Assert.Equal("""["克里斯托弗·诺兰","艾玛·托马斯"]""", encoded);

        // 历史扁平列：顿号/逗号/分号都能拆，顺序保留且去重。
        Assert.Equal(
            ["Christopher Nolan", "Emma Thomas"],
            PersonNames.Decode("Christopher Nolan, Emma Thomas；Emma Thomas"));
        Assert.Null(PersonNames.Encode([]));
    }

    [Fact]
    public void PeopleColumns_PrefersCreditListsButKeepsFlatProjection()
    {
        var credits = new TmdbCredits(
            [new TmdbPersonInfo(108, "克里斯托弗·诺兰")],
            [new TmdbPersonInfo(7, "莉塔"), new TmdbPersonInfo(8, "约翰")]
        );

        var columns = PeopleColumns.FromCredits(credits, "旧导演", "旧主演");

        Assert.Equal("""["克里斯托弗·诺兰"]""", columns.Directors);
        Assert.Equal("""["莉塔","约翰"]""", columns.Casts);
        Assert.Equal("克里斯托弗·诺兰", columns.DirectorText);
        Assert.Equal("莉塔、约翰", columns.MainCastText);
    }

    [Fact]
    public void PeopleColumns_FallsBackToLegacyTextWhenNoCredits()
    {
        var columns = PeopleColumns.FromLegacyText("诺兰, 盖伊", null);

        Assert.Equal("""["诺兰","盖伊"]""", columns.Directors);
        Assert.Null(columns.Casts);
        Assert.Equal("诺兰、盖伊", columns.DirectorText);
    }

    [Fact]
    public async Task LocalizeCreditsAsync_TranslatesForeignNameOnceAndKeepsFailuresUnfinished()
    {
        var tmdb = new FakeTmdbClient();
        tmdb.PersonResults[108] = TmdbChineseNameResult.Found("克里斯托弗·诺兰");
        tmdb.PersonResults[7] = TmdbChineseNameResult.RequestFailed;
        var store = CreateStore(tmdb);

        var credits = new TmdbCredits(
            [new TmdbPersonInfo(108, "Christopher Nolan")],
            [new TmdbPersonInfo(7, "Guy Pearce")]
        );

        var first = await store.LocalizeCreditsAsync(credits);
        Assert.Equal("克里斯托弗·诺兰", first!.Directors[0].Name);
        // 请求失败时保留原名，不能因为一次断网把外文名永久写死。
        Assert.Equal("Guy Pearce", first.Casts[0].Name);
        Assert.Equal(2, tmdb.PersonRequestCount);

        var second = await store.LocalizeCreditsAsync(first);
        Assert.Equal("克里斯托弗·诺兰", second!.Directors[0].Name);
        // 已有中文名的条目不再请求；失败的条目会重试。
        Assert.Equal(3, tmdb.PersonRequestCount);
    }

    [Fact]
    public async Task LocalizeCreditsAsync_SkipsRequestsInEnglishInterface()
    {
        var tmdb = new FakeTmdbClient();
        tmdb.PersonResults[108] = TmdbChineseNameResult.Found("克里斯托弗·诺兰");
        var store = CreateStore(tmdb);
        Locale.Set(Locale.English);
        try
        {
            var credits = new TmdbCredits([new TmdbPersonInfo(108, "Christopher Nolan")], []);
            var localized = await store.LocalizeCreditsAsync(credits);

            Assert.Equal("Christopher Nolan", localized!.Directors[0].Name);
            Assert.Equal(0, tmdb.PersonRequestCount);
        }
        finally
        {
            Locale.Set(Locale.Chinese);
        }
    }

    [Fact]
    public async Task RenamePersonAsync_RewritesCreditsFoldersAndRemembersAlias()
    {
        var tmdb = new FakeTmdbClient();
        var store = CreateStore(tmdb);
        await InsertMovieAsync(-201, """["Christopher Nolan"]""", "Christopher Nolan");

        await store.RegisterCreditsAsync(new TmdbCredits([new TmdbPersonInfo(108, "Christopher Nolan")], []));
        var renamed = await store.RenamePersonAsync("director", "Christopher Nolan", "克里斯托弗·诺兰");

        Assert.True(renamed);
        using (var connection = database.OpenConnection())
        {
            var directors = await connection.ExecuteScalarAsync<string?>(
                "SELECT directors FROM movie WHERE id = -201");
            var legacy = await connection.ExecuteScalarAsync<string?>(
                "SELECT director FROM movie WHERE id = -201");
            Assert.Contains("克里斯托弗·诺兰", directors);
            Assert.DoesNotContain("Christopher Nolan", directors);
            Assert.Equal("克里斯托弗·诺兰", legacy);

            var folderNames = await connection.QueryAsync<string>(
                "SELECT name FROM categoryFolder WHERE kind = 'director'");
            Assert.Equal(["克里斯托弗·诺兰"], folderNames);
        }

        // 后续刮削仍会带回 TMDB 的外文名，手改的名字必须顶住。
        tmdb.PersonResults[108] = TmdbChineseNameResult.NotTranslated;
        var localized = await store.LocalizeCreditsAsync(
            new TmdbCredits([new TmdbPersonInfo(108, "Christopher Nolan")], []));
        Assert.Equal("克里斯托弗·诺兰", localized!.Directors[0].Name);
    }

    [Fact]
    public async Task RegisterCreditsAsync_RecordsProfilesWithTmdbPersonIds()
    {
        var store = CreateStore(new FakeTmdbClient());

        await store.RegisterCreditsAsync(new TmdbCredits(
            [new TmdbPersonInfo(108, "诺兰", "/profile.jpg")],
            [new TmdbPersonInfo(7, "盖伊")]
        ));

        var profiles = await store.LoadProfilesAsync("director");
        var profile = Assert.Single(profiles);
        Assert.Equal("诺兰", profile.Name);
        Assert.Equal(108, profile.TmdbPersonId);
    }

    [Fact]
    public async Task EnsureInitialized_BackfillsLegacyPeopleColumns()
    {
        await InsertMovieAsync(-301, null, "诺兰, 盖伊");
        await SetSchemaVersionAsync(2);
        await ClearPeopleColumnsAsync(-301);

        database.EnsureInitialized();

        using var connection = database.OpenConnection();
        var directors = await connection.ExecuteScalarAsync<string?>(
            "SELECT directors FROM movie WHERE id = -301");
        Assert.Equal("""["诺兰","盖伊"]""", directors);
    }

    [Fact]
    public async Task TmdbRequestLimiter_KeepsMinimumSpacingBetweenRequests()
    {
        var limiter = new TmdbRequestLimiter(requestsPerSecond: 20);
        var stopwatch = Stopwatch.StartNew();
        for (var index = 0; index < 5; index++)
        {
            await limiter.AcquireAsync(CancellationToken.None);
        }

        stopwatch.Stop();
        Assert.True(
            stopwatch.ElapsedMilliseconds >= 150,
            $"限速未生效，5 次请求只用了 {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task SidecarNfo_RoundTripsDirectorsAndActors()
    {
        var service = new LocalMetadataSidecarService();
        var directory = Path.Combine(storagePaths.AppRoot, "nfo");
        Directory.CreateDirectory(directory);
        var videoPath = Path.Combine(directory, "Memento.2000.mkv");
        await File.WriteAllTextAsync(videoPath, "video");

        await service.ExportMovieAsync(
            "local",
            directory,
            "Memento.2000.mkv",
            new LocalSidecarMetadata
            {
                Title = "记忆碎片",
                Directors = ["克里斯托弗·诺兰"],
                Casts = ["盖·皮尔斯", "凯瑞-安·莫斯"]
            });

        var read = service.ReadMovieMetadata(videoPath);
        Assert.NotNull(read);
        Assert.Equal(["克里斯托弗·诺兰"], read!.Directors);
        Assert.Equal(["盖·皮尔斯", "凯瑞-安·莫斯"], read.Casts);
        Assert.Equal("盖·皮尔斯、凯瑞-安·莫斯", read.CastText);
    }

    private CategoryFolderStore CreateStore(FakeTmdbClient tmdb) =>
        new(new CategoryFolderRepository(database), new ChineseNameCacheRepository(database), tmdb);

    private async Task InsertMovieAsync(long id, string? directors, string? legacyDirector)
    {
        using var connection = database.OpenConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO movie (id, title, directors, director)
            VALUES (@Id, 'Memento', @Directors, @LegacyDirector)
            """,
            new { Id = id, Directors = directors, LegacyDirector = legacyDirector });
    }

    private async Task SetSchemaVersionAsync(int version)
    {
        using var connection = database.OpenConnection();
        await connection.ExecuteAsync($"PRAGMA user_version = {version}");
    }

    private async Task ClearPeopleColumnsAsync(long id)
    {
        using var connection = database.OpenConnection();
        await connection.ExecuteAsync(
            "UPDATE movie SET directors = NULL WHERE id = @Id",
            new { Id = id });
    }

    private sealed class FakeTmdbClient : ITmdbMetadataClient
    {
        public Dictionary<int, TmdbChineseNameResult> PersonResults { get; } = [];

        public int PersonRequestCount { get; private set; }

        public Task<TmdbChineseNameResult> FetchChinesePersonNameAsync(
            int personId,
            CancellationToken cancellationToken = default)
        {
            PersonRequestCount++;
            return Task.FromResult(
                PersonResults.TryGetValue(personId, out var result)
                    ? result
                    : TmdbChineseNameResult.NotTranslated);
        }

        public Task<TmdbChineseNameResult> FetchChineseTitleAsync(
            string? mediaType,
            int id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(TmdbChineseNameResult.NotTranslated);

        public Task<IReadOnlyList<TmdbFilmographyItem>?> FetchPersonFilmographyAsync(
            int personId,
            string kind,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TmdbFilmographyItem>?>(null);

        public Task<TmdbPersonDetail?> FetchPersonDetailAsync(
            int personId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TmdbPersonDetail?>(null);

        public Task<string?> DownloadProfileImageAsync(
            string profilePath,
            int personId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyList<TmdbMetadataMatch>> SearchMovieCandidatesAsync(
            IReadOnlyList<string> candidateTitles,
            string? year,
            CancellationToken cancellationToken = default,
            TmdbSearchOptions? options = null) =>
            Task.FromResult<IReadOnlyList<TmdbMetadataMatch>>([]);

        public Task<TmdbMetadataMatch?> SearchMovieAsync(
            IReadOnlyList<string> candidateTitles,
            string? year,
            CancellationToken cancellationToken = default,
            TmdbSearchOptions? options = null) =>
            Task.FromResult<TmdbMetadataMatch?>(null);

        public Task<IReadOnlyList<TmdbMetadataMatch>> SearchTvShowCandidatesAsync(
            IReadOnlyList<string> candidateTitles,
            string? year,
            CancellationToken cancellationToken = default,
            TmdbSearchOptions? options = null) =>
            Task.FromResult<IReadOnlyList<TmdbMetadataMatch>>([]);

        public Task<TmdbMetadataMatch?> SearchTvShowAsync(
            IReadOnlyList<string> candidateTitles,
            string? year,
            CancellationToken cancellationToken = default,
            TmdbSearchOptions? options = null) =>
            Task.FromResult<TmdbMetadataMatch?>(null);

        public Task<string?> DownloadPosterAsync(
            string posterPath,
            string mediaType,
            int tmdbId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> DownloadEpisodeStillAsync(
            int tmdbShowId,
            int seasonNumber,
            int episodeNumber,
            string videoFileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<TmdbCredits?> FetchCreditsAsync(
            int tmdbId,
            string mediaType,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TmdbCredits?>(null);
    }
}
