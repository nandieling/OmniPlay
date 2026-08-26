using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using OmniPlay.Api;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Core.Runtime;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.Douban;
using OmniPlay.Infrastructure.FileSystem;
using OmniPlay.Infrastructure.Library;
using OmniPlay.Infrastructure.Maintenance;
using OmniPlay.Infrastructure.Network;
using OmniPlay.Infrastructure.SystemChecks;
using OmniPlay.Infrastructure.Tmdb;
using OmniPlay.Media;

const string SessionCookieName = "omniplay_session";

Console.WriteLine("[OmniPlay] Bootstrapping...");
var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
Console.WriteLine("[OmniPlay] Builder created.");

var listenUri = ResolveListenUri(
    Environment.GetEnvironmentVariable(AppRuntime.ListenUrlsEnvironmentVariable)
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));

builder.WebHost.UseKestrelCore();
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(ToListenAddress(listenUri.Host), listenUri.Port);
});
builder.Services.AddRoutingCore();
builder.Services.AddSingleton<IStoragePaths, StoragePaths>();
builder.Services.AddSingleton<SqliteDatabase>();
builder.Services.AddSingleton<IMediaSourceRepository, MediaSourceRepository>();
builder.Services.AddSingleton<ILocalDirectoryBrowser, LocalDirectoryBrowser>();
builder.Services.AddSingleton<IWebDavDirectoryBrowser, WebDavDirectoryBrowser>();
builder.Services.AddSingleton<IWebDavFileEnumerator, WebDavDirectoryBrowser>();
builder.Services.AddSingleton<IPlayableFileResolver, PlayableFileResolver>();
builder.Services.AddSingleton<IPlaybackCacheService, WebDavPlaybackCacheService>();
builder.Services.AddSingleton<IPlaybackSubtitleService, PlaybackSubtitleService>();
builder.Services.AddSingleton<IWebDavRangeStreamService, WebDavRangeStreamService>();
builder.Services.AddSingleton<ILibraryRepository, LibraryRepository>();
builder.Services.AddSingleton<ILibraryScanner, LibraryScanner>();
builder.Services.AddSingleton<IAppSettingsRepository, AppSettingsRepository>();
builder.Services.AddSingleton<IUserRepository, UserRepository>();
builder.Services.AddSingleton<IPosterAssetRepository, PosterAssetRepository>();
builder.Services.AddSingleton<IThumbnailAssetRepository, ThumbnailAssetRepository>();
builder.Services.AddSingleton<IAssetCacheCleanupService, AssetCacheCleanupService>();
builder.Services.AddSingleton<IMediaSourceCleanupService, MediaSourceCleanupService>();
builder.Services.AddSingleton<ICacheUsageService, CacheUsageService>();
builder.Services.AddSingleton<IWebDavCacheCleanupService, WebDavCacheCleanupService>();
builder.Services.AddSingleton<IBackgroundTaskCenter, InMemoryBackgroundTaskCenter>();
builder.Services.AddSingleton<CacheMaintenanceCoordinator>();
builder.Services.AddHostedService<CacheMaintenanceHostedService>();
builder.Services.AddSingleton<ILibraryMetadataEnricher, LibraryMetadataEnricher>();
builder.Services.AddSingleton<IScanStatusStore, InMemoryScanStatusStore>();
builder.Services.AddSingleton<ILibraryScanJobService, LibraryScanJobService>();
builder.Services.AddHostedService<LibraryRefreshSchedulerHostedService>();
builder.Services.AddSingleton<IMetadataEnrichmentStatusStore, InMemoryMetadataEnrichmentStatusStore>();
builder.Services.AddSingleton<ILibraryMetadataEnrichmentJobService, LibraryMetadataEnrichmentJobService>();
builder.Services.AddSingleton<IMediaProbeService, FfprobeMediaProbeService>();
builder.Services.AddSingleton<IHlsSessionService, FfmpegHlsSessionService>();
builder.Services.AddSingleton<ISubtitleCacheService, FfmpegSubtitleCacheService>();
builder.Services.AddSingleton<HttpClient>(serviceProvider =>
{
    var settingsRepository = serviceProvider.GetRequiredService<IAppSettingsRepository>();
    var httpClient = new HttpClient(new SettingsProxyHttpMessageHandler(settingsRepository), disposeHandler: true)
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("OmniPlay.NAS/0.1");
    return httpClient;
});
builder.Services.AddSingleton<ITmdbMetadataClient, TmdbMetadataClient>();
builder.Services.AddSingleton<IDoubanMetadataClient, DoubanMetadataClient>();
builder.Services.AddSingleton<IProxyConnectionTester, ProxyConnectionTester>();
builder.Services.AddSingleton<IRuntimeSelfCheckService>(serviceProvider => new RuntimeSelfCheckService(
    serviceProvider.GetRequiredService<IStoragePaths>(),
    serviceProvider.GetRequiredService<SqliteDatabase>(),
    serviceProvider.GetRequiredService<IHlsSessionService>(),
    serviceProvider.GetRequiredService<HttpClient>(),
    listenUri,
    serviceProvider.GetRequiredService<IAppSettingsRepository>()));

Console.WriteLine("[OmniPlay] Building app...");
var app = builder.Build();
Console.WriteLine("[OmniPlay] App built.");
var playbackTickets = new ConcurrentDictionary<string, PlaybackAccessTicket>(StringComparer.Ordinal);

Console.WriteLine("[OmniPlay] Preparing runtime directories...");
var storagePaths = app.Services.GetRequiredService<IStoragePaths>();
storagePaths.EnsureCreated();

Console.WriteLine("[OmniPlay] Initializing database...");
var database = app.Services.GetRequiredService<SqliteDatabase>();
database.EnsureInitialized();
Console.WriteLine("[OmniPlay] Database ready.");

var webRoot = ResolveWebRoot();
if (Directory.Exists(webRoot))
{
    var webFileProvider = new PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webFileProvider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = webFileProvider });
    Console.WriteLine($"[OmniPlay] Serving Web UI from {webRoot}");
}

app.Use(async (context, next) =>
{
    if (!RequiresAuthenticatedApi(context.Request.Path))
    {
        await next();
        return;
    }

    if (TryAuthorizePlaybackTicket(context, playbackTickets))
    {
        await next();
        return;
    }

    var users = context.RequestServices.GetRequiredService<IUserRepository>();
    if (!context.Request.Cookies.TryGetValue(SessionCookieName, out var token) ||
        await users.GetUserBySessionTokenAsync(token, context.RequestAborted) is not { } user)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "请先登录 OmniPlay。" }, context.RequestAborted);
        return;
    }

    context.Items["auth.user"] = user;
    await next();
});

app.MapGet("/api/auth/status", async (
    HttpContext context,
    IUserRepository users,
    CancellationToken cancellationToken) =>
{
    var hasUsers = await users.HasUsersAsync(cancellationToken);
    AuthUser? user = null;
    if (context.Request.Cookies.TryGetValue(SessionCookieName, out var token))
    {
        user = await users.GetUserBySessionTokenAsync(token, cancellationToken);
    }

    return Results.Ok(new AuthStatus(!hasUsers, user is not null, user?.Username, user?.Role));
});

app.MapPost("/api/auth/register", async (
    HttpContext context,
    HttpRequest request,
    IUserRepository users,
    CancellationToken cancellationToken) =>
{
    var authRequest = await ReadJsonBodyAsync<AuthRequest>(request, cancellationToken);
    if (authRequest is null)
    {
        return Results.BadRequest(new { error = "请提供用户名和密码。" });
    }

    try
    {
        var session = await users.RegisterFirstAdminAsync(authRequest, cancellationToken);
        SetSessionCookie(context, session);
        return Results.Ok(new AuthStatus(false, true, session.User.Username, session.User.Role));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
});

app.MapPost("/api/auth/login", async (
    HttpContext context,
    HttpRequest request,
    IUserRepository users,
    CancellationToken cancellationToken) =>
{
    var authRequest = await ReadJsonBodyAsync<AuthRequest>(request, cancellationToken);
    if (authRequest is null)
    {
        return Results.BadRequest(new { error = "请提供用户名和密码。" });
    }

    var session = await users.LoginAsync(authRequest, cancellationToken);
    if (session is null)
    {
        return Results.Json(new { error = "用户名或密码错误。" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    SetSessionCookie(context, session);
    return Results.Ok(new AuthStatus(false, true, session.User.Username, session.User.Role));
});

app.MapPost("/api/auth/logout", async (
    HttpContext context,
    IUserRepository users,
    CancellationToken cancellationToken) =>
{
    if (context.Request.Cookies.TryGetValue(SessionCookieName, out var token))
    {
        await users.RevokeSessionAsync(token, cancellationToken);
    }

    ClearSessionCookie(context);
    return Results.NoContent();
});

app.MapGet("/api/health", (
    IHostEnvironment environment,
    IStoragePaths paths,
    SqliteDatabase db) =>
{
    var dbStatus = db.GetStatus();
    return Results.Ok(new HealthStatus(
        AppRuntime.ServiceName,
        typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        environment.EnvironmentName,
        paths.RootDirectory,
        dbStatus.Path,
        dbStatus.Exists,
        DateTimeOffset.UtcNow));
});

app.MapGet("/api/runtime/self-check", async (
    IRuntimeSelfCheckService selfCheckService,
    CancellationToken cancellationToken) =>
{
    return Results.Ok(await selfCheckService.CheckAsync(cancellationToken));
});

app.MapGet("/api/settings", async (
    IAppSettingsRepository repository,
    CancellationToken cancellationToken) =>
{
    return Results.Ok(await repository.GetAsync(cancellationToken));
});

app.MapPut("/api/settings", async (
    HttpRequest httpRequest,
    IAppSettingsRepository repository,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<AppSettingsUpdateRequest>(httpRequest, cancellationToken);
    if (request is null)
    {
        return Results.BadRequest(new { error = "请提供设置内容。" });
    }

    return Results.Ok(await repository.UpdateAsync(request, cancellationToken));
});

app.MapPost("/api/settings/tmdb/test", async (
    HttpRequest httpRequest,
    IAppSettingsRepository settingsRepository,
    ITmdbMetadataClient tmdbClient,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<TmdbSettings>(httpRequest, cancellationToken);
    var settings = request ?? (await settingsRepository.GetAsync(cancellationToken)).Tmdb;
    return Results.Ok(await tmdbClient.TestConnectionAsync(settings, cancellationToken));
});

app.MapPost("/api/settings/proxy/test", async (
    HttpRequest httpRequest,
    IAppSettingsRepository settingsRepository,
    IProxyConnectionTester proxyTester,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<ProxySettings>(httpRequest, cancellationToken);
    var settings = request ?? (await settingsRepository.GetAsync(cancellationToken)).Proxy;
    return Results.Ok(await proxyTester.TestAsync(settings, cancellationToken));
});

app.MapGet("/api/sources", async (
    IMediaSourceRepository repository,
    CancellationToken cancellationToken) =>
{
    var sources = await repository.GetAllAsync(cancellationToken);
    return Results.Ok(sources);
});

app.MapGet("/api/sources/local/directories", async (
    string? path,
    ILocalDirectoryBrowser browser,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await browser.BrowseAsync(path, cancellationToken));
    }
    catch (DirectoryNotFoundException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/sources/local", async (
    HttpRequest httpRequest,
    IMediaSourceRepository repository,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<AddLocalMediaSourceRequest>(httpRequest, cancellationToken);
    if (request is null || string.IsNullOrWhiteSpace(request.Path))
    {
        return Results.BadRequest(new { error = "请提供本地媒体目录 path。" });
    }

    try
    {
        var source = await repository.AddLocalAsync(request.Name ?? string.Empty, request.Path, cancellationToken);
        return Results.Ok(source);
    }
    catch (DirectoryNotFoundException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/sources/webdav", async (
    HttpRequest httpRequest,
    IMediaSourceRepository repository,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<AddWebDavMediaSourceRequest>(httpRequest, cancellationToken);
    if (request is null || string.IsNullOrWhiteSpace(request.Url))
    {
        return Results.BadRequest(new { error = "请提供 WebDAV 地址 url。" });
    }

    try
    {
        var source = await repository.AddWebDavAsync(
            request.Name ?? string.Empty,
            request.Url,
            request.Username,
            request.Password,
            cancellationToken);
        return Results.Ok(source);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/sources/webdav/test", async (
    HttpRequest httpRequest,
    IWebDavDirectoryBrowser browser,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<WebDavConnectionRequest>(httpRequest, cancellationToken);
    if (request is null || string.IsNullOrWhiteSpace(request.Url))
    {
        return Results.BadRequest(new { error = "请提供 WebDAV 地址 url。" });
    }

    try
    {
        return Results.Ok(await browser.TestConnectionAsync(
            request.Url,
            request.Username,
            request.Password,
            cancellationToken));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (HttpRequestException ex)
    {
        return Results.BadRequest(new { error = $"WebDAV 连接失败：{ex.Message}" });
    }
});

app.MapPost("/api/sources/webdav/browse", async (
    HttpRequest httpRequest,
    IWebDavDirectoryBrowser browser,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<WebDavConnectionRequest>(httpRequest, cancellationToken);
    if (request is null || string.IsNullOrWhiteSpace(request.Url))
    {
        return Results.BadRequest(new { error = "请提供 WebDAV 地址 url。" });
    }

    try
    {
        return Results.Ok(await browser.BrowseAsync(
            request.Url,
            request.Username,
            request.Password,
            cancellationToken));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (HttpRequestException ex)
    {
        return Results.BadRequest(new { error = $"WebDAV 连接失败：{ex.Message}" });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/sources/{sourceId:long}/scan", async (
    long sourceId,
    HttpRequest httpRequest,
    ILibraryScanJobService scanJobService,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<LibraryRefreshRequest>(httpRequest, cancellationToken)
                  ?? new LibraryRefreshRequest();
    return scanJobService.TryStartSourceScan(sourceId, request, out var status)
        ? Results.Accepted("/api/library/scan/status", status)
        : Results.Conflict(new { error = "扫描已经在运行。", status });
});

app.MapPatch("/api/sources/{sourceId:long}", async (
    long sourceId,
    HttpRequest httpRequest,
    IMediaSourceRepository repository,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<UpdateMediaSourceRequest>(httpRequest, cancellationToken);
    if (request is null)
    {
        return Results.BadRequest(new { error = "请提供媒体源更新内容。" });
    }

    try
    {
        var source = await repository.UpdateAsync(sourceId, request, cancellationToken);
        return source is null ? Results.NotFound() : Results.Ok(source);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapDelete("/api/sources/{sourceId:long}", async (
    long sourceId,
    IMediaSourceRepository repository,
    IMediaSourceCleanupService cleanupService,
    IAssetCacheCleanupService assetCacheCleanupService,
    IAppSettingsRepository settingsRepository,
    IBackgroundTaskCenter taskCenter,
    CancellationToken cancellationToken) =>
{
    var removed = await repository.RemoveAsync(sourceId, cancellationToken);
    if (!removed)
    {
        return Results.NotFound();
    }

    return taskCenter.TryStartExclusive(
        "media-source-cleanup",
        $"清理媒体源 {sourceId}",
        async (_, progress, taskCancellationToken) =>
        {
            progress.Report(new BackgroundTaskProgress("cleanup-source", "正在清理媒体源索引", 5, sourceId.ToString()));
            var summary = await cleanupService.CleanupRemovedSourceAsync(
                sourceId,
                ScaleProgress(progress, 5, 65),
                taskCancellationToken);
            var settings = await settingsRepository.GetAsync(taskCancellationToken);
            var assetOptions = new AssetCacheCleanupOptions(
                IncludeUntrackedFiles: !string.Equals(
                    settings.Cache.ImageCleanupScope,
                    "orphans-only",
                    StringComparison.OrdinalIgnoreCase));
            var assetSummary = await assetCacheCleanupService.CleanupOrphansAsync(
                assetOptions,
                ScaleProgress(progress, 70, 28),
                taskCancellationToken);
            progress.Report(new BackgroundTaskProgress("completed", "媒体源和图片缓存清理完成", 100, sourceId.ToString()));
            return $"清理 {summary.RemovedVideoFileCount} 个视频文件，{summary.RemovedLibraryItemCount} 个影视条目，释放 {assetSummary.RemovedBytes} 字节图片缓存";
        },
        onAccepted: null,
        out var status)
        ? Results.Accepted("/api/tasks", status)
        : Results.Ok(CompletedMediaSourceRemovedStatus(sourceId, "媒体源已移除；当前有后台任务，索引清理稍后可再次触发。"));
});

app.MapGet("/api/library/items", async (
    ILibraryRepository repository,
    CancellationToken cancellationToken) =>
{
    var items = await repository.GetItemsAsync(cancellationToken);
    return Results.Ok(items);
});

app.MapGet("/api/library/items/{id}", async (
    string id,
    ILibraryRepository repository,
    CancellationToken cancellationToken) =>
{
    var detail = await repository.GetItemDetailAsync(id, cancellationToken);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

app.MapGet("/api/tasks", (IBackgroundTaskCenter taskCenter) =>
{
    return Results.Ok(taskCenter.GetSnapshot());
});

app.MapGet("/api/tasks/events", async (
    HttpContext context,
    IBackgroundTaskCenter taskCenter) =>
{
    await WriteTaskEventsAsync(context, taskCenter);
});

app.MapPost("/api/tasks/{taskId}/cancel", (
    string taskId,
    IBackgroundTaskCenter taskCenter) =>
{
    return taskCenter.TryCancel(taskId, out var status)
        ? Results.Ok(status)
        : Results.Conflict(new { error = "任务不存在或未运行。", status });
});

app.MapGet("/api/library/scan/status", (IScanStatusStore scanStatusStore) =>
{
    return Results.Ok(scanStatusStore.Get());
});

app.MapGet("/api/library/scan/events", async (
    HttpContext context,
    IScanStatusStore scanStatusStore) =>
{
    await WriteScanStatusEventsAsync(context, scanStatusStore);
});

app.MapPost("/api/library/scan", async (
    HttpRequest httpRequest,
    ILibraryScanJobService scanJobService,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<LibraryRefreshRequest>(httpRequest, cancellationToken)
                  ?? new LibraryRefreshRequest();
    return scanJobService.TryStartScan(request, out var status)
        ? Results.Accepted("/api/library/scan/status", status)
        : Results.Conflict(new { error = "扫描已经在运行。", status });
});

app.MapPost("/api/library/scan/cancel", (ILibraryScanJobService scanJobService) =>
{
    if (!scanJobService.RequestCancel(out var status))
    {
        return Results.Conflict(new { error = "当前没有正在运行的扫描。", status });
    }

    return Results.Ok(status);
});

app.MapGet("/api/library/scrape/status", (IMetadataEnrichmentStatusStore statusStore) =>
{
    return Results.Ok(statusStore.Get());
});

app.MapGet("/api/library/scrape/events", async (
    HttpContext context,
    IMetadataEnrichmentStatusStore statusStore) =>
{
    await WriteMetadataEnrichmentStatusEventsAsync(context, statusStore);
});

app.MapPost("/api/library/scrape", (ILibraryMetadataEnrichmentJobService enrichmentJobService) =>
{
    return enrichmentJobService.TryStartMissing(out var status)
        ? Results.Accepted("/api/library/scrape/status", status)
        : Results.Conflict(new { error = "刮削已经在运行。", status });
});

app.MapPost("/api/library/scrape/cancel", (ILibraryMetadataEnrichmentJobService enrichmentJobService) =>
{
    if (!enrichmentJobService.RequestCancel(out var status))
    {
        return Results.Conflict(new { error = "当前没有正在运行的刮削。", status });
    }

    return Results.Ok(status);
});

app.MapPost("/api/library/items/{id}/rescrape", (
    string id,
    ILibraryMetadataEnrichmentJobService enrichmentJobService) =>
{
    return enrichmentJobService.TryStartItem(id, out var status)
        ? Results.Accepted("/api/library/scrape/status", status)
        : Results.Conflict(new { error = "刮削已经在运行。", status });
});

app.MapGet("/api/library/items/{id}/metadata/search", async (
    string id,
    string? query,
    string? mediaType,
    string? year,
    ILibraryRepository repository,
    IAppSettingsRepository settingsRepository,
    ITmdbMetadataClient tmdbClient,
    CancellationToken cancellationToken) =>
{
    var item = await repository.GetItemDetailAsync(id, cancellationToken);
    if (item is null)
    {
        return Results.NotFound();
    }

    var sourceMetadata = ResolveSourceSearchMetadata(item.VideoFiles);
    var searchQuery = ResolveMetadataSearchQuery(item, sourceMetadata, query);
    var secondaryQuery = BuildMetadataSecondarySearchQuery(
        searchQuery,
        sourceMetadata?.ForeignTitle,
        sourceMetadata?.ChineseTitle,
        sourceMetadata?.ParentChineseTitle,
        sourceMetadata?.FullCleanTitle,
        item.Title);
    var searchTypes = ResolveMetadataSearchTypes(mediaType, item.ItemKind);
    var searchYear = string.IsNullOrWhiteSpace(year)
        ? NormalizeMetadataSearchYear(item.ReleaseDate) ?? NormalizeMetadataSearchYear(sourceMetadata?.Year)
        : year.Trim();
    var settings = (await settingsRepository.GetAsync(cancellationToken)).Tmdb;
    List<TmdbMetadataMatch> candidates = [];
    HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
    foreach (var searchType in searchTypes)
    {
        var matches = await tmdbClient.SearchCandidatesAsync(
            searchType,
            searchQuery,
            searchYear,
            settings,
            secondaryQuery,
            limit: 8,
            cancellationToken: cancellationToken);
        foreach (var match in matches)
        {
            var key = $"{match.MediaType}#{match.Id}";
            if (seen.Add(key))
            {
                candidates.Add(match);
            }
        }
    }

    return Results.Ok(candidates.Take(8).ToArray());
});

app.MapPost("/api/library/items/{id}/metadata/apply", async (
    string id,
    HttpRequest httpRequest,
    ILibraryRepository repository,
    IAppSettingsRepository settingsRepository,
    ITmdbMetadataClient tmdbClient,
    CancellationToken cancellationToken) =>
{
    var match = await ReadJsonBodyAsync<TmdbMetadataMatch>(httpRequest, cancellationToken);
    if (match is null || match.Id <= 0 || string.IsNullOrWhiteSpace(match.Title))
    {
        return Results.BadRequest(new { error = "请提供有效的 TMDB 匹配项。" });
    }

    var item = await repository.GetItemDetailAsync(id, cancellationToken);
    if (item is null)
    {
        return Results.NotFound();
    }

    var sourceMetadata = ResolveSourceSearchMetadata(item.VideoFiles);
    var settings = (await settingsRepository.GetAsync(cancellationToken)).Tmdb;
    var selectedMatch = await tmdbClient.GetDetailsAsync(
        match.MediaType,
        match.Id,
        settings,
        cancellationToken) ?? match;
    string? posterLocalPath = null;
    if (settings.EnablePosterDownloads && !string.IsNullOrWhiteSpace(selectedMatch.PosterPath))
    {
        posterLocalPath = await tmdbClient.DownloadPosterAsync(
            selectedMatch.PosterPath,
            selectedMatch.MediaType,
            selectedMatch.Id,
            cancellationToken);
    }

    var updated = await repository.ApplyMetadataMatchAsync(
        new LibraryItemMetadataApplyRequest(
            id,
            selectedMatch.Id,
            selectedMatch.MediaType,
            ResolveMetadataDisplayTitle(selectedMatch.Title, sourceMetadata),
            selectedMatch.Overview,
            selectedMatch.ReleaseDate,
            selectedMatch.PosterPath,
            selectedMatch.VoteAverage,
            posterLocalPath,
            LockMetadata: true),
        cancellationToken);
    if (!updated)
    {
        return Results.NotFound();
    }

    var detail = await repository.GetItemDetailAsync(id, cancellationToken);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

app.MapPost("/api/library/items/{id}/douban/bind", async (
    string id,
    HttpRequest httpRequest,
    ILibraryRepository repository,
    IDoubanMetadataClient doubanClient,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<DoubanBindRequest>(httpRequest, cancellationToken);
    if (request is null || string.IsNullOrWhiteSpace(request.Subject))
    {
        return Results.BadRequest(new { error = "请填写豆瓣影视链接或 subject ID。" });
    }

    var item = await repository.GetItemDetailAsync(id, cancellationToken);
    if (item is null)
    {
        return Results.NotFound();
    }

    try
    {
        var metadata = await doubanClient.FetchSubjectAsync(
            id,
            request.Subject,
            item.Title,
            item.ReleaseDate,
            cancellationToken);
        return await SaveDoubanMetadataAndReturnDetailAsync(id, metadata, repository, cancellationToken);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex) when (ex is HttpRequestException
                               or TaskCanceledException
                               or InvalidOperationException
                               or JsonException)
    {
        var metadata = doubanClient.CreateSubjectPlaceholder(
            id,
            request.Subject,
            item.Title,
            item.ReleaseDate);
        return await SaveDoubanMetadataAndReturnDetailAsync(id, metadata, repository, cancellationToken);
    }
});

app.MapPost("/api/library/items/{id}/douban/import", async (
    string id,
    HttpRequest httpRequest,
    ILibraryRepository repository,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<DoubanMetadataImportRequest>(httpRequest, cancellationToken);
    if (request is null
        || string.IsNullOrWhiteSpace(request.SubjectId)
        || string.IsNullOrWhiteSpace(request.SubjectUrl)
        || string.IsNullOrWhiteSpace(request.Title))
    {
        return Results.BadRequest(new { error = "豆瓣元数据缺少 subject、链接或标题。" });
    }

    if (request.Rating is < 0 or > 10)
    {
        return Results.BadRequest(new { error = "豆瓣评分需要在 0 到 10 之间。" });
    }

    var metadata = new DoubanMetadata(
        id,
        request.SubjectId,
        request.SubjectUrl,
        request.Title,
        request.OriginalTitle,
        request.Year,
        request.Rating,
        request.RatingCount,
        request.Summary,
        request.Genres,
        request.Countries,
        request.PosterUrl,
        request.FetchedAt ?? DateTimeOffset.UtcNow);

    return await SaveDoubanMetadataAndReturnDetailAsync(id, metadata, repository, cancellationToken);
});

app.MapPatch("/api/library/items/{id}/metadata/custom", async (
    string id,
    HttpRequest httpRequest,
    ILibraryRepository repository,
    IStoragePaths storagePaths,
    CancellationToken cancellationToken) =>
{
    LibraryItemCustomMetadataUpdateRequest? request;
    try
    {
        request = await ReadCustomMetadataRequestAsync(id, httpRequest, storagePaths, cancellationToken);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    if (request is null || string.IsNullOrWhiteSpace(request.Title))
    {
        return Results.BadRequest(new { error = "请填写影视名称。" });
    }

    if (request.VoteAverage is < 0 or > 10)
    {
        return Results.BadRequest(new { error = "TMDB评分需要在 0 到 10 之间。" });
    }

    if (request.DoubanRating is < 0 or > 10)
    {
        return Results.BadRequest(new { error = "豆瓣评分需要在 0 到 10 之间。" });
    }

    var updated = await repository.UpdateCustomMetadataAsync(request with { LibraryItemId = id }, cancellationToken);
    if (!updated)
    {
        return Results.NotFound();
    }

    var detail = await repository.GetItemDetailAsync(id, cancellationToken);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

app.MapPost("/api/library/items/{id}/metadata/lock", async (
    string id,
    HttpRequest httpRequest,
    ILibraryRepository repository,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<LibraryItemLockUpdateRequest>(httpRequest, cancellationToken);
    if (request is null)
    {
        return Results.BadRequest(new { error = "请提供锁定状态。" });
    }

    var updated = await repository.SetLibraryItemLockedAsync(
        new LibraryItemLockUpdateRequest(id, request.IsLocked),
        cancellationToken);
    if (!updated)
    {
        return Results.NotFound();
    }

    var detail = await repository.GetItemDetailAsync(id, cancellationToken);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

app.MapPost("/api/playback/progress", async (
    HttpRequest httpRequest,
    ILibraryRepository repository,
    ISubtitleCacheService subtitleCacheService,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<PlaybackProgressUpdateRequest>(httpRequest, cancellationToken);
    if (request is null || string.IsNullOrWhiteSpace(request.VideoFileId))
    {
        return Results.BadRequest(new { error = "请提供 videoFileId。" });
    }

    var updated = await repository.UpdatePlaybackProgressAsync(request, cancellationToken);
    if (updated && ShouldPrewarmNextEpisodeSubtitle(request))
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await subtitleCacheService.PrewarmNextEpisodeAsync(request.VideoFileId, CancellationToken.None);
            }
            catch
            {
                // Playback progress must stay non-blocking; the next scan/scrape will retry subtitle prewarming.
            }
        });
    }

    return updated ? Results.NoContent() : Results.NotFound();
});

app.MapGet("/api/playback/files/{fileId}/progress", async (
    string fileId,
    ILibraryRepository repository,
    CancellationToken cancellationToken) =>
{
    var progress = await repository.GetPlaybackProgressAsync(fileId, cancellationToken);
    return progress is null ? Results.NotFound() : Results.Ok(progress);
});

app.MapPost("/api/playback/watched", async (
    HttpRequest httpRequest,
    ILibraryRepository repository,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<WatchedStatusUpdateRequest>(httpRequest, cancellationToken);
    if (request is null || string.IsNullOrWhiteSpace(request.VideoFileId))
    {
        return Results.BadRequest(new { error = "请提供 videoFileId。" });
    }

    var updated = await repository.SetWatchedAsync(request, cancellationToken);
    return updated ? Results.NoContent() : Results.NotFound();
});

app.MapPost("/api/library/items/{id}/watched", async (
    string id,
    HttpRequest httpRequest,
    ILibraryRepository repository,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<LibraryItemWatchedStatusUpdateRequest>(httpRequest, cancellationToken);
    if (request is null)
    {
        return Results.BadRequest(new { error = "请提供已看状态。" });
    }

    var updated = await repository.SetLibraryItemWatchedAsync(
        new LibraryItemWatchedStatusUpdateRequest(id, request.IsWatched, request.UserId),
        cancellationToken);
    if (!updated)
    {
        return Results.NotFound();
    }

    var detail = await repository.GetItemDetailAsync(id, cancellationToken);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

app.MapGet("/api/playback/capabilities", async (
    IHlsSessionService hlsSessionService,
    CancellationToken cancellationToken) =>
{
    return Results.Ok(await hlsSessionService.GetCapabilitiesAsync(cancellationToken));
});

app.MapGet("/api/playback/decision/{fileId}", async (
    string fileId,
    string? quality,
    int? audioTrackIndex,
    string? subtitleMode,
    string? subtitleId,
    bool? hardware,
    ILibraryRepository repository,
    IPlayableFileResolver playableFileResolver,
    IPlaybackSubtitleService playbackSubtitleService,
    IWebDavRangeStreamService webDavRangeStreamService,
    IPlaybackCacheService cacheService,
    IAppSettingsRepository settingsRepository,
    IHlsSessionService hlsSessionService,
    IMediaProbeService mediaProbeService,
    CancellationToken cancellationToken) =>
{
    var playbackSettings = (await settingsRepository.GetAsync(cancellationToken)).Playback;
    var effectiveQuality = ResolveEffectiveQuality(quality, playbackSettings.PlaybackQualityPreference);
    var remoteFile = await webDavRangeStreamService.GetFileInfoAsync(fileId, cancellationToken);
    if (remoteFile is not null)
    {
        var remoteProbe = CreateProbeFromStoredMetadata(remoteFile);
        var remoteSourceVideoCodec = ResolveSourceVideoCodec(remoteFile.FileName, remoteProbe);
        var remoteDynamicRange = AnalyzeSourceDynamicRange(remoteFile.FileName, remoteProbe);
        var remoteToneMapToSdr = remoteDynamicRange.ShouldToneMapToSdr;
        var remotePlan = ResolvePlaybackPlan(remoteFile.FileName, remoteProbe);
        var remoteDurationSeconds = ResolvePlaybackDurationSeconds(remoteProbe, remoteFile.DurationSeconds);
        var remoteEmbeddedSubtitleStreamIndex = ResolveEmbeddedSubtitleStreamIndex(subtitleId);
        var remoteEmbeddedSubtitleCodec = ResolveEmbeddedSubtitleCodec(remoteProbe, remoteEmbeddedSubtitleStreamIndex);
        var remoteSubtitlePath = remoteEmbeddedSubtitleStreamIndex.HasValue
            ? null
            : await playbackSubtitleService.ResolveSubtitlePathAsync(remoteFile.Id, subtitleId, cancellationToken);
        var remoteRequestedSubtitleMode = NormalizeSubtitleMode(subtitleMode, remoteSubtitlePath, remoteEmbeddedSubtitleStreamIndex);
        var remoteProfile = await ResolveRequestedProfileAsync(
            remotePlan.Profile,
            effectiveQuality,
            audioTrackIndex,
            remoteRequestedSubtitleMode,
            remoteSubtitlePath,
            remoteEmbeddedSubtitleStreamIndex,
            remoteEmbeddedSubtitleCodec,
            remoteSourceVideoCodec,
            hardware,
            remoteToneMapToSdr,
            remoteDynamicRange.ToneMapMode,
            hlsSessionService,
            cancellationToken);
        var remoteMode = ResolveRequestedMode(remotePlan.Mode, remoteProfile, effectiveQuality, audioTrackIndex, remoteRequestedSubtitleMode);
        var remotePolicy = await ApplyPlaybackSettingsAsync(
            remoteFile.Id,
            remotePlan,
            remoteProfile,
            remoteMode,
            playbackSettings,
            effectiveQuality,
            audioTrackIndex,
            remoteRequestedSubtitleMode,
            remoteSubtitlePath,
            remoteEmbeddedSubtitleStreamIndex,
            remoteEmbeddedSubtitleCodec,
            remoteSourceVideoCodec,
            hardware,
            hlsSessionService,
            cancellationToken);
        if (remotePolicy.Decision is not null)
        {
            return Results.Ok(remotePolicy.Decision);
        }

        if (remotePolicy.Mode == "direct")
        {
            var remoteCacheStatus = await cacheService.GetStatusAsync(remoteFile.Id, cancellationToken);
            if (remoteCacheStatus?.IsReady == true)
            {
                var completedCachedPath = await cacheService.EnsureCachedAsync(remoteFile.Id, cancellationToken);
                if (!string.IsNullOrWhiteSpace(completedCachedPath))
                {
                    var cachedRemoteFile = remoteFile with { AbsolutePath = completedCachedPath };
                    var cachedHlsProfile = HlsPlaybackProfile.CreateRemux(audioTrackIndex);
                    if (hlsSessionService.GetCompletedSession(cachedRemoteFile, cachedHlsProfile) is { } cachedHlsSession)
                    {
                        return Results.Ok(new PlaybackDecision(
                            remoteFile.Id,
                            "hls-remux",
                            null,
                            $"/api/playback/hls/{Uri.EscapeDataString(cachedHlsSession.SessionId)}/index.m3u8",
                            cachedHlsSession.SessionId,
                            true,
                            "已命中预生成 HLS 缓存。",
                            remoteDurationSeconds));
                    }
                }
            }

            return Results.Ok(new PlaybackDecision(
                remoteFile.Id,
                "direct",
                $"/api/playback/files/{Uri.EscapeDataString(remoteFile.Id)}/stream",
                null,
                null,
                true,
                $"{remotePolicy.Reason} WebDAV 使用 Range 分段代理。",
                remoteDurationSeconds));
        }

        var cachedPath = await cacheService.EnsureCachedAsync(remoteFile.Id, cancellationToken);
        if (string.IsNullOrWhiteSpace(cachedPath))
        {
            return Results.Ok(new PlaybackDecision(
                remoteFile.Id,
                "unavailable",
                null,
                null,
                null,
                false,
                "WebDAV 文件需要完整缓存后才能进入 HLS 播放，请等待缓存完成后重试。",
                remoteDurationSeconds));
        }

        var remoteCachedFile = remoteFile with { AbsolutePath = cachedPath };
        var remoteSession = await hlsSessionService.EnsureSessionAsync(remoteCachedFile, remotePolicy.Profile, cancellationToken);
        if (!remoteSession.IsReady && !string.IsNullOrWhiteSpace(remoteSession.ErrorMessage))
        {
            return Results.Ok(new PlaybackDecision(
                remoteFile.Id,
                "unavailable",
                null,
                null,
                remoteSession.SessionId,
                false,
                remoteSession.ErrorMessage,
                remoteDurationSeconds));
        }

        return Results.Ok(new PlaybackDecision(
            remoteFile.Id,
            remotePolicy.Mode,
            null,
            $"/api/playback/hls/{Uri.EscapeDataString(remoteSession.SessionId)}/index.m3u8",
            remoteSession.SessionId,
            remoteSession.IsReady,
            remoteSession.IsReady ? ResolveProfileReason(remotePolicy.Reason, remotePolicy.Profile) : "HLS 正在准备。",
            remoteDurationSeconds));
    }

    var file = await playableFileResolver.ResolveAsync(fileId, cancellationToken);
    if (file is null)
    {
        return Results.NotFound();
    }

    var probe = await mediaProbeService.ProbeAsync(file.AbsolutePath, cancellationToken);
    if (probe is not null)
    {
        await repository.UpdateVideoFileProbeAsync(
            new VideoFileProbeUpdate(
                file.Id,
                probe.DurationSeconds,
                probe.Container,
                probe.VideoCodec,
                probe.AudioCodec,
                probe.SubtitleSummary,
                probe.RawJson),
            cancellationToken);
    }
    else
    {
        probe = CreateProbeFromStoredMetadata(file);
    }

    var sourceVideoCodec = ResolveSourceVideoCodec(file.FileName, probe);
    var dynamicRange = AnalyzeSourceDynamicRange(file.FileName, probe);
    var toneMapToSdr = dynamicRange.ShouldToneMapToSdr;
    var plan = ResolvePlaybackPlan(file.FileName, probe);
    var durationSeconds = ResolvePlaybackDurationSeconds(probe, file.DurationSeconds);
    var embeddedSubtitleStreamIndex = ResolveEmbeddedSubtitleStreamIndex(subtitleId);
    var embeddedSubtitleCodec = ResolveEmbeddedSubtitleCodec(probe, embeddedSubtitleStreamIndex);
    var subtitlePath = embeddedSubtitleStreamIndex.HasValue
        ? null
        : await playbackSubtitleService.ResolveSubtitlePathAsync(file.Id, subtitleId, cancellationToken);
    var requestedSubtitleMode = NormalizeSubtitleMode(subtitleMode, subtitlePath, embeddedSubtitleStreamIndex);
    var profile = await ResolveRequestedProfileAsync(
        plan.Profile,
        effectiveQuality,
        audioTrackIndex,
        requestedSubtitleMode,
        subtitlePath,
        embeddedSubtitleStreamIndex,
        embeddedSubtitleCodec,
        sourceVideoCodec,
        hardware,
        toneMapToSdr,
        dynamicRange.ToneMapMode,
        hlsSessionService,
        cancellationToken);
    var mode = ResolveRequestedMode(plan.Mode, profile, effectiveQuality, audioTrackIndex, requestedSubtitleMode);
    var policy = await ApplyPlaybackSettingsAsync(
        file.Id,
        plan,
        profile,
        mode,
        playbackSettings,
        effectiveQuality,
        audioTrackIndex,
        requestedSubtitleMode,
        subtitlePath,
        embeddedSubtitleStreamIndex,
        embeddedSubtitleCodec,
        sourceVideoCodec,
        hardware,
        hlsSessionService,
        cancellationToken);
    if (policy.Decision is not null)
    {
        return Results.Ok(policy.Decision);
    }

    mode = policy.Mode;
    profile = policy.Profile;
    if (mode == "direct")
    {
        var cachedHlsProfile = HlsPlaybackProfile.CreateRemux(audioTrackIndex);
        if (hlsSessionService.GetCompletedSession(file, cachedHlsProfile) is { } cachedHlsSession)
        {
            return Results.Ok(new PlaybackDecision(
                file.Id,
                "hls-remux",
                null,
                $"/api/playback/hls/{Uri.EscapeDataString(cachedHlsSession.SessionId)}/index.m3u8",
                cachedHlsSession.SessionId,
                true,
                "已命中预生成 HLS 缓存。",
                durationSeconds));
        }

        return Results.Ok(new PlaybackDecision(
            file.Id,
            "direct",
            $"/api/playback/files/{Uri.EscapeDataString(file.Id)}/stream",
            null,
            null,
            true,
            policy.Reason,
            durationSeconds));
    }

    var session = await hlsSessionService.EnsureSessionAsync(file, profile, cancellationToken);
    if (!session.IsReady && !string.IsNullOrWhiteSpace(session.ErrorMessage))
    {
        return Results.Ok(new PlaybackDecision(
            file.Id,
            "unavailable",
            null,
            null,
            session.SessionId,
            false,
            session.ErrorMessage,
            durationSeconds));
    }

    return Results.Ok(new PlaybackDecision(
        file.Id,
        mode,
        null,
        $"/api/playback/hls/{Uri.EscapeDataString(session.SessionId)}/index.m3u8",
        session.SessionId,
        session.IsReady,
        session.IsReady ? ResolveProfileReason(policy.Reason, profile) : "HLS 正在准备。",
        durationSeconds));
});

app.MapGet("/api/playback/diagnostics/{fileId}", async (
    string fileId,
    string? quality,
    int? audioTrackIndex,
    string? subtitleMode,
    string? subtitleId,
    bool? hardware,
    IPlayableFileResolver playableFileResolver,
    IPlaybackSubtitleService playbackSubtitleService,
    IWebDavRangeStreamService webDavRangeStreamService,
    IPlaybackCacheService cacheService,
    IAppSettingsRepository settingsRepository,
    IHlsSessionService hlsSessionService,
    IMediaProbeService mediaProbeService,
    CancellationToken cancellationToken) =>
{
    var diagnostics = await BuildPlaybackDiagnosticsAsync(
        fileId,
        quality,
        audioTrackIndex,
        subtitleMode,
        subtitleId,
        hardware,
        playableFileResolver,
        playbackSubtitleService,
        webDavRangeStreamService,
        cacheService,
        settingsRepository,
        hlsSessionService,
        mediaProbeService,
        cancellationToken);
    return diagnostics is null ? Results.NotFound() : Results.Ok(diagnostics);
});

app.MapGet("/api/playback/files/{fileId}/subtitles", async (
    string fileId,
    IPlaybackSubtitleService playbackSubtitleService,
    CancellationToken cancellationToken) =>
{
    var subtitles = await playbackSubtitleService.DiscoverAsync(fileId, cancellationToken);
    if (subtitles is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(subtitles);
});

app.MapGet("/api/playback/files/{fileId}/subtitle-cache/status", async (
    string fileId,
    ISubtitleCacheService subtitleCacheService,
    IPlayableFileResolver playableFileResolver,
    IWebDavRangeStreamService webDavRangeStreamService,
    IPlaybackCacheService cacheService,
    CancellationToken cancellationToken) =>
{
    var file = await playableFileResolver.ResolveAsync(fileId, cancellationToken);
    var inputPath = file?.AbsolutePath;
    if (string.IsNullOrWhiteSpace(inputPath))
    {
        var remoteFile = await webDavRangeStreamService.GetFileInfoAsync(fileId, cancellationToken);
        inputPath = remoteFile is null
            ? null
            : await cacheService.GetCompletedCachedPathAsync(fileId, cancellationToken);
    }

    return Results.Ok(await subtitleCacheService.GetPgsCacheStatusAsync(fileId, inputPath, cancellationToken));
});

app.MapGet("/api/playback/files/{fileId}/streams", async (
    string fileId,
    ILibraryRepository repository,
    IPlayableFileResolver playableFileResolver,
    IWebDavRangeStreamService webDavRangeStreamService,
    IMediaProbeService mediaProbeService,
    CancellationToken cancellationToken) =>
{
    var file = await playableFileResolver.ResolveAsync(fileId, cancellationToken);
    MediaProbeSnapshot? probe = null;
    if (file is not null)
    {
        probe = await mediaProbeService.ProbeAsync(file.AbsolutePath, cancellationToken);
        if (probe is not null)
        {
            await repository.UpdateVideoFileProbeAsync(
                new VideoFileProbeUpdate(
                    file.Id,
                    probe.DurationSeconds,
                    probe.Container,
                    probe.VideoCodec,
                    probe.AudioCodec,
                    probe.SubtitleSummary,
                    probe.RawJson),
                cancellationToken);
        }
    }
    else
    {
        file = await webDavRangeStreamService.GetFileInfoAsync(fileId, cancellationToken);
        probe = file is null ? null : CreateProbeFromStoredMetadata(file);
    }

    if (file is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(new PlaybackFileStreams(
        BuildVideoFileStreamSummaries(probe, "audio"),
        BuildVideoFileStreamSummaries(probe, "subtitle")));
});

app.MapGet("/api/playback/files/{fileId}/cache", async (
    string fileId,
    IPlaybackCacheService cacheService,
    CancellationToken cancellationToken) =>
{
    var status = await cacheService.GetStatusAsync(fileId, cancellationToken);
    return status is null ? Results.NotFound() : Results.Ok(status);
});

app.MapPost("/api/playback/files/{fileId}/cache/prepare", async (
    string fileId,
    IPlaybackCacheService cacheService,
    CancellationToken cancellationToken) =>
{
    var status = await cacheService.StartAsync(fileId, cancellationToken);
    return status is null ? Results.NotFound() : Results.Accepted($"/api/playback/files/{Uri.EscapeDataString(fileId)}/cache", status);
});

app.MapPost("/api/playback/files/{fileId}/cache/cancel", async (
    string fileId,
    IPlaybackCacheService cacheService,
    CancellationToken cancellationToken) =>
{
    var status = await cacheService.CancelAsync(fileId, cancellationToken);
    return status is null ? Results.NotFound() : Results.Ok(status);
});

app.MapPost("/api/playback/files/{fileId}/ticket", async (
    string fileId,
    ILibraryRepository repository,
    CancellationToken cancellationToken) =>
{
    CleanupExpiredPlaybackTickets(playbackTickets);
    var token = RandomNumberGenerator.GetHexString(32);
    var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
    playbackTickets[token] = new PlaybackAccessTicket(fileId, expiresAt);
    var progress = await repository.GetPlaybackProgressAsync(fileId, cancellationToken)
        ?? new PlaybackProgressState(0, 0, false);

    return Results.Ok(new
    {
        token,
        expiresAt,
        positionSeconds = progress.PositionSeconds,
        durationSeconds = progress.DurationSeconds,
        isWatched = progress.IsWatched,
        streamUrl = $"/api/playback/files/{Uri.EscapeDataString(fileId)}/stream?ticket={Uri.EscapeDataString(token)}"
    });
});

app.MapGet("/api/playback/files/{fileId}/stream", async (
    string fileId,
    HttpContext httpContext,
    IWebDavRangeStreamService webDavRangeStreamService,
    IPlayableFileResolver playableFileResolver,
    CancellationToken cancellationToken) =>
{
    var remoteStream = await webDavRangeStreamService.OpenReadAsync(
        fileId,
        httpContext.Request.Headers.Range.ToString(),
        cancellationToken);
    if (remoteStream is not null)
    {
        await WriteWebDavRangeStreamAsync(httpContext, remoteStream, cancellationToken);
        return;
    }

    var file = await playableFileResolver.ResolveAsync(fileId, cancellationToken);
    if (file is null)
    {
        httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await Results.File(
            file.AbsolutePath,
            ResolveContentType(file.AbsolutePath),
            enableRangeProcessing: true)
        .ExecuteAsync(httpContext);
});

app.MapGet("/api/playback/hls/{sessionId}/{assetName}", (
    string sessionId,
    string assetName,
    HttpContext httpContext,
    IHlsSessionService hlsSessionService) =>
{
    var asset = hlsSessionService.GetAsset(sessionId, assetName);
    if (asset is not null)
    {
        httpContext.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        httpContext.Response.Headers.Pragma = "no-cache";
        httpContext.Response.Headers.Expires = "0";
    }

    return asset is null
        ? Results.NotFound()
        : Results.File(asset.FullPath, asset.ContentType, enableRangeProcessing: asset.EnableRangeProcessing);
});

app.MapPost("/api/playback/hls/{sessionId}/stop", (
    string sessionId,
    IHlsSessionService hlsSessionService) =>
{
    return hlsSessionService.StopSession(sessionId) ? Results.NoContent() : Results.NotFound();
});

app.MapPost("/api/playback/hls/prewarm", (
    HlsCachePrepareRequest request,
    ILibraryRepository repository,
    IPlayableFileResolver playableFileResolver,
    IPlaybackSubtitleService playbackSubtitleService,
    IWebDavRangeStreamService webDavRangeStreamService,
    IPlaybackCacheService cacheService,
    IAppSettingsRepository settingsRepository,
    IHlsSessionService hlsSessionService,
    IMediaProbeService mediaProbeService,
    IBackgroundTaskCenter taskCenter) =>
{
    var libraryItemIds = NormalizeRequestIds(request.LibraryItemIds);
    var videoFileIds = NormalizeRequestIds(request.VideoFileIds);
    if (libraryItemIds.Count == 0 && videoFileIds.Count == 0)
    {
        return Results.BadRequest(new { error = "请选择要生成 HLS 缓存的影视或分集。" });
    }

    return taskCenter.TryStartExclusive(
        "hls-cache-prewarm",
        "生成 HLS 缓存",
        async (_, progress, cancellationToken) =>
        {
            var targetFileIds = await ResolveHlsPrewarmFileIdsAsync(
                libraryItemIds,
                videoFileIds,
                repository,
                cancellationToken);
            if (targetFileIds.Count == 0)
            {
                return "没有找到可生成 HLS 缓存的视频文件。";
            }

            var completed = 0;
            var failed = 0;
            for (var index = 0; index < targetFileIds.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileId = targetFileIds[index];
                var offset = index * 100d / targetFileIds.Count;
                var span = 100d / targetFileIds.Count;
                progress.Report(new BackgroundTaskProgress(
                    "hls-cache",
                    $"准备 HLS 缓存 {index + 1}/{targetFileIds.Count}",
                    offset,
                    fileId));

                var target = await ResolveHlsPrewarmTargetAsync(
                    fileId,
                    repository,
                    playableFileResolver,
                    playbackSubtitleService,
                    webDavRangeStreamService,
                    cacheService,
                    settingsRepository,
                    hlsSessionService,
                    mediaProbeService,
                    ScaleProgress(progress, offset, span * 0.2),
                    cancellationToken);
                if (target is null)
                {
                    failed++;
                    continue;
                }

                var session = await hlsSessionService.EnsureCompletedSessionAsync(
                    target.File,
                    target.Profile,
                    ScaleProgress(progress, offset + span * 0.2, span * 0.8),
                    cancellationToken);
                if (session.IsReady && !session.IsRunning && string.IsNullOrWhiteSpace(session.ErrorMessage))
                {
                    completed++;
                }
                else
                {
                    failed++;
                }
            }

            progress.Report(new BackgroundTaskProgress(
                "completed",
                $"HLS 缓存完成：{completed}/{targetFileIds.Count}",
                100,
                null));
            return failed > 0
                ? $"HLS 缓存完成：成功 {completed} 个，失败 {failed} 个"
                : $"HLS 缓存完成：成功 {completed} 个";
        },
        onAccepted: null,
        out var status)
        ? Results.Accepted("/api/tasks", status)
        : Results.Conflict(new { error = "已有后台任务正在运行，请稍后再试。", status });
});

app.MapPost("/api/playback/hls/cleanup", (
    int? maxAgeHours,
    int? maxGb,
    bool? clearAll,
    IHlsSessionService hlsSessionService,
    IAppSettingsRepository settingsRepository,
    IBackgroundTaskCenter taskCenter) =>
{
    return taskCenter.TryStartExclusive(
        "hls-cache-cleanup",
        "清理转码缓存",
        async (_, progress, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settings = await settingsRepository.GetAsync(cancellationToken);
            var shouldClearAll = clearAll == true;
            var retentionHours = Math.Clamp(maxAgeHours ?? settings.Cache.HlsRetentionHours, 1, 24 * 30);
            var maxSizeGb = Math.Clamp(maxGb ?? settings.Cache.HlsMaxGb, 1, 1024);
            var maxAge = shouldClearAll ? TimeSpan.Zero : TimeSpan.FromHours(retentionHours);
            var maxBytes = shouldClearAll ? 0L : maxSizeGb * 1024L * 1024L * 1024L;
            progress.Report(new BackgroundTaskProgress("cleanup", "正在清理转码缓存", null, null));
            var summary = hlsSessionService.CleanupCache(maxAge, maxBytes);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            return shouldClearAll
                ? $"清除 HLS 缓存：{summary.RemovedSessionCount} 个会话，释放 {summary.RemovedBytes} 字节"
                : $"清理 {summary.RemovedSessionCount} 个会话，释放 {summary.RemovedBytes} 字节，HLS 上限 {maxSizeGb} GB";
        },
        onAccepted: null,
        out var status)
        ? Results.Accepted("/api/tasks", status)
        : Results.Conflict(new { error = "已有后台任务正在运行，请稍后再试。", status });
});

app.MapPost("/api/playback/subtitles/cache/cleanup", (
    int? maxGb,
    bool? clearAll,
    ISubtitleCacheService subtitleCacheService,
    IAppSettingsRepository settingsRepository,
    IBackgroundTaskCenter taskCenter) =>
{
    return taskCenter.TryStartExclusive(
        "subtitle-cache-cleanup",
        "清理字幕缓存",
        async (_, progress, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settings = await settingsRepository.GetAsync(cancellationToken);
            var shouldClearAll = clearAll == true;
            var maxSizeGb = Math.Clamp(maxGb ?? settings.Cache.SubtitleMaxGb, 1, 1024);
            var maxBytes = shouldClearAll ? 0L : maxSizeGb * 1024L * 1024L * 1024L;
            progress.Report(new BackgroundTaskProgress("cleanup", "正在清理字幕缓存", null, null));
            var summary = await subtitleCacheService.CleanupAsync(maxBytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new BackgroundTaskProgress("completed", "字幕缓存清理完成", 100, null));
            return shouldClearAll
                ? $"清除字幕缓存：{summary.RemovedFileCount} 个文件，释放 {summary.RemovedBytes} 字节"
                : $"清理 {summary.RemovedFileCount} 个文件，释放 {summary.RemovedBytes} 字节，字幕上限 {maxSizeGb} GB";
        },
        onAccepted: null,
        out var status)
        ? Results.Accepted("/api/tasks", status)
        : Results.Conflict(new { error = "已有后台任务正在运行，请稍后再试。", status });
});

app.MapPost("/api/playback/webdav/cache/cleanup", (
    int? maxAgeHours,
    IWebDavCacheCleanupService cleanupService,
    IAppSettingsRepository settingsRepository,
    IBackgroundTaskCenter taskCenter) =>
{
    return taskCenter.TryStartExclusive(
        "webdav-cache-cleanup",
        "清理 WebDAV 缓存",
        async (_, progress, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settings = await settingsRepository.GetAsync(cancellationToken);
            var retentionHours = Math.Clamp(maxAgeHours ?? settings.Cache.WebDavRetentionHours, 1, 24 * 30);
            var maxAge = TimeSpan.FromHours(retentionHours);
            var maxBytes = settings.Cache.WebDavMaxGb * 1024L * 1024L * 1024L;
            progress.Report(new BackgroundTaskProgress("cleanup", "正在清理 WebDAV 缓存", null, null));
            var summary = await cleanupService.CleanupAsync(maxAge, progress, cancellationToken, maxBytes);
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new BackgroundTaskProgress("completed", "WebDAV 缓存清理完成", 100, null));
            return $"清理 {summary.RemovedFileCount} 个文件，释放 {summary.RemovedBytes} 字节";
        },
        onAccepted: null,
        out var status)
        ? Results.Accepted("/api/tasks", status)
        : Results.Conflict(new { error = "已有后台任务正在运行，请稍后再试。", status });
});

app.MapPost("/api/assets/cache/cleanup", (
    IAssetCacheCleanupService cleanupService,
    IAppSettingsRepository settingsRepository,
    IBackgroundTaskCenter taskCenter) =>
{
    return taskCenter.TryStartExclusive(
        "asset-cache-cleanup",
        "清理图片缓存",
        async (_, progress, cancellationToken) =>
        {
            var settings = await settingsRepository.GetAsync(cancellationToken);
            var options = new AssetCacheCleanupOptions(
                IncludeUntrackedFiles: !string.Equals(
                    settings.Cache.ImageCleanupScope,
                    "orphans-only",
                    StringComparison.OrdinalIgnoreCase));
            var summary = await cleanupService.CleanupOrphansAsync(options, progress, cancellationToken);
            return $"清理 {summary.RemovedFileCount} 个文件，{summary.RemovedAssetRecordCount} 条记录，释放 {summary.RemovedBytes} 字节";
        },
        onAccepted: null,
        out var status)
        ? Results.Accepted("/api/tasks", status)
        : Results.Conflict(new { error = "已有后台任务正在运行，请稍后再试。", status });
});

app.MapGet("/api/cache/status", (ICacheUsageService cacheUsageService) =>
{
    return Results.Ok(cacheUsageService.GetUsage());
});

app.MapGet("/api/playback/files/{fileId}/subtitles/{subtitleId}.vtt", async (
    string fileId,
    string subtitleId,
    IPlaybackSubtitleService playbackSubtitleService,
    ISubtitleCacheService subtitleCacheService,
    CancellationToken cancellationToken) =>
{
    var subtitlePath = await playbackSubtitleService.ResolveSubtitlePathAsync(fileId, subtitleId, cancellationToken);
    if (subtitlePath is null)
    {
        return Results.NotFound();
    }

    var webVtt = await subtitleCacheService.ReadExternalSubtitleAsWebVttAsync(subtitlePath, cancellationToken);
    return webVtt is null
        ? Results.NotFound()
        : Results.Text(webVtt, "text/vtt; charset=utf-8");
});

app.MapGet("/api/playback/files/{fileId}/embedded-subtitles/{subtitleOrdinal:int}.vtt", async (
    string fileId,
    int subtitleOrdinal,
    IPlayableFileResolver playableFileResolver,
    IWebDavRangeStreamService webDavRangeStreamService,
    IPlaybackCacheService cacheService,
    ISubtitleCacheService subtitleCacheService,
    CancellationToken cancellationToken) =>
{
    if (subtitleOrdinal < 0)
    {
        return Results.NotFound();
    }

    var file = await playableFileResolver.ResolveAsync(fileId, cancellationToken);
    var inputPath = file?.AbsolutePath;
    if (string.IsNullOrWhiteSpace(inputPath))
    {
        var remoteFile = await webDavRangeStreamService.GetFileInfoAsync(fileId, cancellationToken);
        inputPath = remoteFile is null
            ? null
            : await cacheService.EnsureCachedAsync(fileId, cancellationToken);
    }

    if (string.IsNullOrWhiteSpace(inputPath) || !System.IO.File.Exists(inputPath))
    {
        return Results.NotFound();
    }

    var webVtt = await subtitleCacheService.ReadEmbeddedSubtitleAsWebVttAsync(inputPath, subtitleOrdinal, cancellationToken);
    return webVtt is null
        ? Results.NotFound()
        : Results.Text(webVtt, "text/vtt; charset=utf-8");
});

app.MapGet("/api/playback/files/{fileId}/embedded-subtitles/{subtitleOrdinal:int}.sup", async (
    string fileId,
    int subtitleOrdinal,
    IPlayableFileResolver playableFileResolver,
    IWebDavRangeStreamService webDavRangeStreamService,
    IPlaybackCacheService cacheService,
    ISubtitleCacheService subtitleCacheService,
    CancellationToken cancellationToken) =>
{
    if (subtitleOrdinal < 0)
    {
        return Results.NotFound();
    }

    var file = await playableFileResolver.ResolveAsync(fileId, cancellationToken);
    var inputPath = file?.AbsolutePath;
    if (string.IsNullOrWhiteSpace(inputPath))
    {
        var remoteFile = await webDavRangeStreamService.GetFileInfoAsync(fileId, cancellationToken);
        inputPath = remoteFile is null
            ? null
            : await cacheService.EnsureCachedAsync(fileId, cancellationToken);
    }

    if (string.IsNullOrWhiteSpace(inputPath) || !System.IO.File.Exists(inputPath))
    {
        return Results.NotFound();
    }

    var supPath = await subtitleCacheService.ExtractEmbeddedSubtitleAsSupAsync(inputPath, subtitleOrdinal, cancellationToken);
    return string.IsNullOrWhiteSpace(supPath)
        ? Results.NotFound()
        : Results.File(
            supPath,
            "application/octet-stream",
            fileDownloadName: $"{Path.GetFileNameWithoutExtension(inputPath)}.s{subtitleOrdinal}.sup",
            enableRangeProcessing: true);
});

app.MapGet("/api/assets/posters/{id}", async (
    string id,
    IPosterAssetRepository repository,
    CancellationToken cancellationToken) =>
{
    var asset = await repository.GetAsync(id, cancellationToken);
    if (asset is null || !System.IO.File.Exists(asset.LocalPath))
    {
        return Results.NotFound();
    }

    return Results.File(
        asset.LocalPath,
        ResolveContentType(asset.LocalPath),
        enableRangeProcessing: true);
});

app.MapGet("/api/assets/thumbnails/{id}", async (
    string id,
    IThumbnailAssetRepository repository,
    CancellationToken cancellationToken) =>
{
    var asset = await repository.GetAsync(id, cancellationToken);
    if (asset is null || !System.IO.File.Exists(asset.LocalPath))
    {
        return Results.NotFound();
    }

    return Results.File(
        asset.LocalPath,
        ResolveContentType(asset.LocalPath),
        enableRangeProcessing: true);
});

if (Directory.Exists(webRoot))
{
    app.MapFallback((HttpContext context) =>
    {
        return context.Request.Path.StartsWithSegments("/api")
            ? Results.NotFound()
            : Results.File(Path.Combine(webRoot, "index.html"), "text/html; charset=utf-8");
    });
}
else
{
    app.MapGet("/", () => Results.Redirect("/api/health"));
}

Console.WriteLine("[OmniPlay] Starting HTTP server...");
Console.WriteLine($"[OmniPlay] Listening on http://{listenUri.Host}:{listenUri.Port}");
app.Run();

static bool RequiresAuthenticatedApi(PathString path)
{
    if (!path.StartsWithSegments("/api"))
    {
        return false;
    }

    return !path.StartsWithSegments("/api/auth");
}

static bool TryAuthorizePlaybackTicket(
    HttpContext context,
    ConcurrentDictionary<string, PlaybackAccessTicket> playbackTickets)
{
    if (!TryResolvePlaybackTicketFileId(context.Request.Path, out var fileId))
    {
        return false;
    }

    var token = context.Request.Query["ticket"].ToString();
    if (string.IsNullOrWhiteSpace(token)
        || !playbackTickets.TryGetValue(token, out var ticket))
    {
        return false;
    }

    var now = DateTimeOffset.UtcNow;
    if (ticket.ExpiresAt <= now)
    {
        playbackTickets.TryRemove(token, out _);
        return false;
    }

    if (!string.Equals(ticket.FileId, fileId, StringComparison.Ordinal))
    {
        return false;
    }

    // A long movie can issue Range requests for longer than the original
    // 30-minute ticket lifetime. Refresh only active tickets that are close to
    // expiry so the same stream URL remains usable without weakening the
    // authentication check above.
    if (ticket.ExpiresAt - now < TimeSpan.FromMinutes(10))
    {
        playbackTickets[token] = ticket with { ExpiresAt = now.AddMinutes(30) };
    }

    return true;
}

static bool TryResolvePlaybackTicketFileId(PathString path, out string fileId)
{
    fileId = "";
    var value = path.Value;
    const string prefix = "/api/playback/files/";
    if (string.IsNullOrWhiteSpace(value)
        || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var remaining = value[prefix.Length..];
    var separator = remaining.IndexOf('/');
    if (separator <= 0)
    {
        return false;
    }

    var suffix = remaining[separator..];
    if (!string.Equals(suffix, "/stream", StringComparison.OrdinalIgnoreCase)
        && !suffix.StartsWith("/subtitles/", StringComparison.OrdinalIgnoreCase)
        && !suffix.StartsWith("/embedded-subtitles/", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    fileId = Uri.UnescapeDataString(remaining[..separator]);
    return !string.IsNullOrWhiteSpace(fileId);
}

static void CleanupExpiredPlaybackTickets(ConcurrentDictionary<string, PlaybackAccessTicket> playbackTickets)
{
    var now = DateTimeOffset.UtcNow;
    foreach (var entry in playbackTickets)
    {
        if (entry.Value.ExpiresAt <= now)
        {
            playbackTickets.TryRemove(entry.Key, out _);
        }
    }
}

static void SetSessionCookie(HttpContext context, AuthSession session)
{
    context.Response.Cookies.Append(
        SessionCookieName,
        session.Token,
        new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Expires = session.ExpiresAt,
            Path = "/"
        });
}

static void ClearSessionCookie(HttpContext context)
{
    context.Response.Cookies.Delete(
        SessionCookieName,
        new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Path = "/"
        });
}

static Uri ResolveListenUri(string? configuredUrls)
{
    var firstUrl = configuredUrls?
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault();

    if (Uri.TryCreate(firstUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp)
    {
        return uri;
    }

    return new Uri("http://0.0.0.0:8096");
}

static string ResolveWebRoot()
{
    var configuredRoot = Environment.GetEnvironmentVariable("OMNIPLAY_WEB_ROOT");
    if (!string.IsNullOrWhiteSpace(configuredRoot))
    {
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredRoot));
    }

    return Path.Combine(AppContext.BaseDirectory, "wwwroot");
}

static IPAddress ToListenAddress(string host)
{
    if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
    {
        return IPAddress.Loopback;
    }

    if (string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "*", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "+", StringComparison.OrdinalIgnoreCase))
    {
        return IPAddress.Any;
    }

    return IPAddress.TryParse(host, out var address) ? address : IPAddress.Any;
}

static async Task<T?> ReadJsonBodyAsync<T>(HttpRequest request, CancellationToken cancellationToken)
{
    if (request.ContentLength == 0)
    {
        return default;
    }

    return await JsonSerializer.DeserializeAsync<T>(
        request.Body,
        new JsonSerializerOptions(JsonSerializerDefaults.Web),
        cancellationToken);
}

static async Task<LibraryItemCustomMetadataUpdateRequest?> ReadCustomMetadataRequestAsync(
    string libraryItemId,
    HttpRequest request,
    IStoragePaths storagePaths,
    CancellationToken cancellationToken)
{
    if (!request.HasFormContentType)
    {
        var json = await ReadJsonBodyAsync<LibraryItemCustomMetadataUpdateRequest>(request, cancellationToken);
        return json is null ? null : json with { LibraryItemId = libraryItemId };
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var posterFile = form.Files.GetFile("poster");
    var posterLocalPath = posterFile is { Length: > 0 }
        ? await SaveCustomPosterAsync(libraryItemId, posterFile, storagePaths, cancellationToken)
        : null;

    return new LibraryItemCustomMetadataUpdateRequest(
        libraryItemId,
        ReadFormString(form, "title") ?? string.Empty,
        ReadFormString(form, "releaseDate"),
        ReadFormString(form, "overview"),
        ReadFormDouble(form, "voteAverage"),
        ReadFormDouble(form, "doubanRating"),
        posterLocalPath,
        posterFile is { Length: > 0 } ? posterFile.FileName : null,
        LockMetadata: true,
        EpisodeId: ReadFormString(form, "episodeId"),
        EpisodeSubtitle: ReadFormString(form, "episodeSubtitle"));
}

static async Task<string> SaveCustomPosterAsync(
    string libraryItemId,
    IFormFile posterFile,
    IStoragePaths storagePaths,
    CancellationToken cancellationToken)
{
    const long maxPosterBytes = 20 * 1024 * 1024;
    if (posterFile.Length > maxPosterBytes)
    {
        throw new InvalidOperationException("海报图片不能超过 20 MB。");
    }

    storagePaths.EnsureCreated();
    var extension = NormalizePosterExtension(Path.GetExtension(posterFile.FileName));
    var hashInput = $"{libraryItemId}:{posterFile.FileName}:{Guid.NewGuid():N}";
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput)))[..16].ToLowerInvariant();
    var destination = Path.Combine(storagePaths.PostersDirectory, $"custom-{hash}{extension}");
    await using var output = System.IO.File.Create(destination);
    await posterFile.CopyToAsync(output, cancellationToken);
    return destination;
}

static string NormalizePosterExtension(string? extension)
{
    return extension?.ToLowerInvariant() switch
    {
        ".png" => ".png",
        ".webp" => ".webp",
        ".jpg" or ".jpeg" => ".jpg",
        _ => ".jpg"
    };
}

static string? ReadFormString(IFormCollection form, string key)
{
    if (!form.TryGetValue(key, out var values))
    {
        return null;
    }

    var value = values.ToString().Trim();
    return string.IsNullOrWhiteSpace(value) ? null : value;
}

static double? ReadFormDouble(IFormCollection form, string key)
{
    var value = ReadFormString(form, key);
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    if (double.TryParse(
            value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var invariantValue))
    {
        return invariantValue;
    }

    return double.TryParse(value, out var localValue) ? localValue : null;
}

static async Task WriteWebDavRangeStreamAsync(
    HttpContext httpContext,
    WebDavRangeStreamResult result,
    CancellationToken cancellationToken)
{
    await using (result)
    {
        httpContext.Response.StatusCode = result.StatusCode;
        httpContext.Response.ContentType = result.ContentType;
        httpContext.Response.Headers["Accept-Ranges"] = "bytes";
        if (result.ContentRange is not null)
        {
            httpContext.Response.Headers["Content-Range"] = result.ContentRange;
        }

        if (result.ContentLength.HasValue)
        {
            httpContext.Response.ContentLength = result.ContentLength.Value;
        }

        if (result.Content is null)
        {
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                await httpContext.Response.WriteAsync(result.ErrorMessage, cancellationToken);
            }

            return;
        }

        await result.Content.CopyToAsync(httpContext.Response.Body, cancellationToken);
    }
}

static string ResolveContentType(string path)
{
    return Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        ".ts" or ".m2ts" or ".m2t" => "video/mp2t",
        ".avi" => "video/x-msvideo",
        _ => "application/octet-stream"
    };
}

static async Task WriteScanStatusEventsAsync(HttpContext context, IScanStatusStore scanStatusStore)
{
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream; charset=utf-8";

    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var lastPayload = string.Empty;
    var cancellationToken = context.RequestAborted;
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var status = scanStatusStore.Get();
            var payload = JsonSerializer.Serialize(status, jsonOptions);
            if (!string.Equals(payload, lastPayload, StringComparison.Ordinal))
            {
                await context.Response.WriteAsync("event: status\n", cancellationToken);
                await context.Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
                lastPayload = payload;
            }

            await Task.Delay(status.IsRunning ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(3), cancellationToken);
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // Client closed the EventSource connection.
    }
}

static async Task WriteTaskEventsAsync(HttpContext context, IBackgroundTaskCenter taskCenter)
{
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream; charset=utf-8";

    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var lastPayload = string.Empty;
    var cancellationToken = context.RequestAborted;
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var snapshot = taskCenter.GetSnapshot();
            var payload = JsonSerializer.Serialize(snapshot, jsonOptions);
            if (!string.Equals(payload, lastPayload, StringComparison.Ordinal))
            {
                await context.Response.WriteAsync("event: status\n", cancellationToken);
                await context.Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
                lastPayload = payload;
            }

            await Task.Delay(snapshot.ActiveTask is not null ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(3), cancellationToken);
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // Client closed the EventSource connection.
    }
}

static async Task WriteMetadataEnrichmentStatusEventsAsync(
    HttpContext context,
    IMetadataEnrichmentStatusStore statusStore)
{
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream; charset=utf-8";

    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var lastPayload = string.Empty;
    var cancellationToken = context.RequestAborted;
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var status = statusStore.Get();
            var payload = JsonSerializer.Serialize(status, jsonOptions);
            if (!string.Equals(payload, lastPayload, StringComparison.Ordinal))
            {
                await context.Response.WriteAsync("event: status\n", cancellationToken);
                await context.Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
                lastPayload = payload;
            }

            await Task.Delay(status.IsRunning ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(3), cancellationToken);
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // Client closed the EventSource connection.
    }
}

static BackgroundTaskStatus CompletedMediaSourceRemovedStatus(long sourceId, string message)
{
    var now = DateTimeOffset.UtcNow;
    return new BackgroundTaskStatus(
        $"media-source-remove_{sourceId}_{now:yyyyMMddHHmmssfff}",
        "media-source-remove",
        $"移除媒体源 {sourceId}",
        "completed",
        IsRunning: false,
        IsCancellationRequested: false,
        CanCancel: false,
        CreatedAt: now,
        StartedAt: now,
        CompletedAt: now,
        Phase: "completed",
        ProgressText: message,
        ProgressPercent: 100,
        CurrentItem: sourceId.ToString(),
        ResultText: message,
        ErrorMessage: null);
}

static async Task<PlaybackDiagnostics?> BuildPlaybackDiagnosticsAsync(
    string fileId,
    string? quality,
    int? audioTrackIndex,
    string? subtitleMode,
    string? subtitleId,
    bool? hardware,
    IPlayableFileResolver playableFileResolver,
    IPlaybackSubtitleService playbackSubtitleService,
    IWebDavRangeStreamService webDavRangeStreamService,
    IPlaybackCacheService cacheService,
    IAppSettingsRepository settingsRepository,
    IHlsSessionService hlsSessionService,
    IMediaProbeService mediaProbeService,
    CancellationToken cancellationToken)
{
    var settings = await settingsRepository.GetAsync(cancellationToken);
    var effectiveQuality = ResolveEffectiveQuality(quality, settings.Playback.PlaybackQualityPreference);
    var cacheStatus = await cacheService.GetStatusAsync(fileId, cancellationToken);
    var remoteFile = await webDavRangeStreamService.GetFileInfoAsync(fileId, cancellationToken);
    var isRemote = remoteFile is not null;
    var file = remoteFile;
    MediaProbeSnapshot? probe = null;
    if (file is null)
    {
        file = await playableFileResolver.ResolveAsync(fileId, cancellationToken);
        if (file is null)
        {
            return null;
        }

        probe = await mediaProbeService.ProbeAsync(file.AbsolutePath, cancellationToken);
    }

    probe ??= CreateProbeFromStoredMetadata(file);
    var sourceVideoCodec = ResolveSourceVideoCodec(file.FileName, probe);
    var dynamicRange = AnalyzeSourceDynamicRange(file.FileName, probe);
    var toneMapToSdr = dynamicRange.ShouldToneMapToSdr;
    var plan = ResolvePlaybackPlan(file.FileName, probe);
    var embeddedSubtitleStreamIndex = ResolveEmbeddedSubtitleStreamIndex(subtitleId);
    var embeddedSubtitleCodec = ResolveEmbeddedSubtitleCodec(probe, embeddedSubtitleStreamIndex);
    var subtitlePath = embeddedSubtitleStreamIndex.HasValue
        ? null
        : await playbackSubtitleService.ResolveSubtitlePathAsync(file.Id, subtitleId, cancellationToken);
    var requestedSubtitleMode = NormalizeSubtitleMode(subtitleMode, subtitlePath, embeddedSubtitleStreamIndex);
    var profile = await ResolveRequestedProfileAsync(
        plan.Profile,
        effectiveQuality,
        audioTrackIndex,
        requestedSubtitleMode,
        subtitlePath,
        embeddedSubtitleStreamIndex,
        embeddedSubtitleCodec,
        sourceVideoCodec,
        hardware,
        toneMapToSdr,
        dynamicRange.ToneMapMode,
        hlsSessionService,
        cancellationToken);
    var requestedMode = ResolveRequestedMode(plan.Mode, profile, effectiveQuality, audioTrackIndex, requestedSubtitleMode);
    var policy = await ApplyPlaybackSettingsAsync(
        file.Id,
        plan,
        profile,
        requestedMode,
        settings.Playback,
        effectiveQuality,
        audioTrackIndex,
        requestedSubtitleMode,
        subtitlePath,
        embeddedSubtitleStreamIndex,
        embeddedSubtitleCodec,
        sourceVideoCodec,
        hardware,
        hlsSessionService,
        cancellationToken);

    var effectiveMode = policy.Decision?.Mode ?? policy.Mode;
    profile = policy.Profile;
    var usesDirect = string.Equals(effectiveMode, "direct", StringComparison.OrdinalIgnoreCase);
    var usesHls = effectiveMode.StartsWith("hls", StringComparison.OrdinalIgnoreCase);
    var usesTranscode = string.Equals(effectiveMode, "hls-transcode", StringComparison.OrdinalIgnoreCase)
                        || profile.TranscodeVideo;
    var burnsSubtitle = IsSubtitleBurnMode(requestedSubtitleMode)
                        && (subtitlePath is not null || embeddedSubtitleStreamIndex.HasValue);
    var usesRangeProxy = isRemote && usesDirect;
    var requiresFullCache = isRemote && !usesRangeProxy;
    var capabilities = usesHls || hardware == true
        ? await hlsSessionService.GetCapabilitiesAsync(cancellationToken)
        : null;
    var previewFile = requiresFullCache
        ? file with { AbsolutePath = "<webdav-cache-file>" }
        : file;
    var ffmpegCommandPreview = usesHls && policy.Decision is null
        ? hlsSessionService.PreviewCommand(previewFile, profile)
        : null;
    var reason = policy.Decision?.Reason
                 ?? (usesRangeProxy ? $"{policy.Reason} WebDAV 使用 Range 分段代理。" : policy.Reason);

    return new PlaybackDiagnostics(
        file.Id,
        file.FileName,
        isRemote,
        isRemote ? "webdav" : "local",
        NormalizeQuality(effectiveQuality),
        audioTrackIndex,
        requestedSubtitleMode,
        subtitleId,
        hardware == true,
        plan.Mode,
        effectiveMode,
        reason,
        requiresFullCache,
        usesRangeProxy,
        usesDirect,
        usesHls,
        usesTranscode,
        burnsSubtitle,
        usesDirect ? $"/api/playback/files/{Uri.EscapeDataString(file.Id)}/stream" : null,
        null,
        usesHls ? profile : null,
        ffmpegCommandPreview,
        cacheStatus,
        capabilities,
        BuildDiagnosticSteps(
            file,
            isRemote,
            probe,
            plan,
            requestedMode,
            policy,
            requestedSubtitleMode,
            subtitlePath,
            embeddedSubtitleStreamIndex,
            cacheStatus,
            usesRangeProxy,
            requiresFullCache,
            usesHls,
            usesTranscode,
            burnsSubtitle,
            ffmpegCommandPreview,
            capabilities));
}

static IReadOnlyList<PlaybackDiagnosticStep> BuildDiagnosticSteps(
    PlayableVideoFile file,
    bool isRemote,
    MediaProbeSnapshot? probe,
    PlaybackPlan plan,
    string requestedMode,
    PlaybackPolicyResult policy,
    string requestedSubtitleMode,
    string? subtitlePath,
    int? embeddedSubtitleStreamIndex,
    PlaybackCacheStatus? cacheStatus,
    bool usesRangeProxy,
    bool requiresFullCache,
    bool usesHls,
    bool usesTranscode,
    bool burnsSubtitle,
    string? ffmpegCommandPreview,
    FfmpegTranscodeCapabilities? capabilities)
{
    List<PlaybackDiagnosticStep> steps = [];
    steps.Add(new PlaybackDiagnosticStep(
        "source",
        "媒体源",
        "ok",
        isRemote ? "WebDAV 媒体源。" : "NAS 本地媒体源。"));
    steps.Add(new PlaybackDiagnosticStep(
        "probe",
        "媒体探测",
        probe is null ? "warn" : "ok",
        probe is null
            ? "没有可用探测信息，将按扩展名选择保守播放策略。"
            : $"容器 {probe.Container ?? "未知"}，视频 {probe.VideoCodec ?? "未知"}，音频 {probe.AudioCodec ?? "未知"}。"));
    steps.Add(new PlaybackDiagnosticStep(
        "base-plan",
        "基础策略",
        "ok",
        $"{plan.Mode}：{plan.Reason}"));
    steps.Add(new PlaybackDiagnosticStep(
        "request",
        "请求参数",
        "ok",
        $"档位 {policy.Profile.QualityId}，音轨 {(policy.Profile.AudioTrackIndex?.ToString() ?? "默认")}，字幕 {requestedSubtitleMode}。"));
    if (isRemote)
    {
        steps.Add(new PlaybackDiagnosticStep(
            "webdav-cache",
            "WebDAV 缓存",
            requiresFullCache ? "warn" : "ok",
            usesRangeProxy
                ? "可使用 WebDAV Range 分段代理，不需要完整缓存。"
                : cacheStatus is null
                    ? "需要完整缓存后进入 HLS/烧录链路。"
                    : $"需要完整缓存，当前状态 {cacheStatus.State}，{cacheStatus.Percent?.ToString("0.#") ?? "未知"}%。"));
    }

    steps.Add(new PlaybackDiagnosticStep(
        "policy",
        "策略裁决",
        policy.Decision?.Mode == "unavailable" ? "error" : "ok",
        policy.Decision?.Reason ?? $"{policy.Mode}：{policy.Reason}"));
    if (burnsSubtitle)
    {
        steps.Add(new PlaybackDiagnosticStep(
            "subtitle-burn",
            "字幕烧录",
            "warn",
            subtitlePath is not null
                ? $"外挂字幕烧录：{Path.GetFileName(subtitlePath)}。"
                : $"内嵌字幕烧录：stream {embeddedSubtitleStreamIndex}."));
    }

    if (usesHls)
    {
        steps.Add(new PlaybackDiagnosticStep(
            "ffmpeg",
            usesTranscode ? "FFmpeg 转码" : "FFmpeg 转封装",
            capabilities?.IsAvailable == false ? "error" : "ok",
            ffmpegCommandPreview is null
                ? capabilities?.ErrorMessage ?? "FFmpeg 命令将在启动 HLS 会话时生成。"
                : ffmpegCommandPreview));
    }

    if (usesTranscode && string.IsNullOrWhiteSpace(policy.Profile.HardwareEncoder))
    {
        steps.Add(new PlaybackDiagnosticStep(
            "hardware-encoder",
            "硬件编码器",
            "error",
            "FFmpeg 没有检测到可用硬件编码器，转码不会使用软件编码兜底。请检查 /dev/dri 权限、VAAPI 驱动和 FFmpeg 硬件编码能力。"));
    }

    if (usesTranscode && policy.Profile.ToneMapToSdr)
    {
        var toneMapDetail = policy.Profile.ToneMapMode switch
        {
            var mode when string.Equals(mode, "hardware", StringComparison.OrdinalIgnoreCase) =>
                "已启用 VAAPI SDR 色彩转换，并交给硬件编码器输出。",
            var mode when string.Equals(mode, "dolby-vision", StringComparison.OrdinalIgnoreCase) =>
                "已启用 Dolby Vision-only 兼容色彩转换，并交给硬件编码器输出。",
            _ =>
                "已启用 SDR 色彩转换，并交给硬件编码器输出；当前片源缺少安全硬件 tone map 所需的元数据时会使用兼容滤镜。"
        };
        steps.Add(new PlaybackDiagnosticStep(
            "tone-map",
            "HDR/DV 转 SDR",
            "ok",
            toneMapDetail));
    }

    if (!usesHls && usesRangeProxy)
    {
        steps.Add(new PlaybackDiagnosticStep(
            "range-proxy",
            "Range 代理",
            "ok",
            "浏览器请求 Range 时，服务端会按固定块读取 WebDAV 并写入 Range 缓存。"));
    }

    if (!usesHls && !usesRangeProxy)
    {
        steps.Add(new PlaybackDiagnosticStep(
            "direct",
            "Range 直出",
            "ok",
            $"直接读取 {file.FileName}。"));
    }

    return steps;
}

static IReadOnlyList<string> NormalizeRequestIds(IEnumerable<string>? values)
{
    return values?
        .Select(static value => value.Trim())
        .Where(static value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.Ordinal)
        .ToArray() ?? [];
}

static async Task<IReadOnlyList<string>> ResolveHlsPrewarmFileIdsAsync(
    IReadOnlyList<string> libraryItemIds,
    IReadOnlyList<string> videoFileIds,
    ILibraryRepository repository,
    CancellationToken cancellationToken)
{
    List<string> resolved = [];
    foreach (var libraryItemId in libraryItemIds)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var detail = await repository.GetItemDetailAsync(libraryItemId, cancellationToken);
        if (detail is null)
        {
            continue;
        }

        resolved.AddRange(SelectDefaultHlsPrewarmFiles(detail).Select(static file => file.Id));
    }

    resolved.AddRange(videoFileIds);
    return resolved
        .Where(static id => !string.IsNullOrWhiteSpace(id))
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}

static IReadOnlyList<VideoFileSummary> SelectDefaultHlsPrewarmFiles(LibraryItemDetail detail)
{
    if (!string.Equals(detail.ItemKind, "tv", StringComparison.OrdinalIgnoreCase))
    {
        return detail.VideoFiles;
    }

    var orderedEpisodes = detail.Seasons
        .OrderBy(static season => season.SeasonNumber)
        .SelectMany(static season => season.Episodes.OrderBy(static episode => episode.EpisodeNumber))
        .Select(static episode => episode.VideoFile)
        .Where(static file => file is not null)
        .Cast<VideoFileSummary>()
        .ToArray();
    var target = orderedEpisodes.FirstOrDefault(IsUnfinishedForHlsPrewarm)
                 ?? orderedEpisodes.FirstOrDefault(static file => !file.IsWatched)
                 ?? orderedEpisodes.FirstOrDefault();
    return target is null ? [] : [target];
}

static bool IsUnfinishedForHlsPrewarm(VideoFileSummary file)
{
    if (file.IsWatched || file.PositionSeconds <= 5)
    {
        return false;
    }

    return file.DurationSeconds <= 0 || file.PositionSeconds < file.DurationSeconds * 0.95;
}

static async Task<HlsPrewarmTarget?> ResolveHlsPrewarmTargetAsync(
    string fileId,
    ILibraryRepository repository,
    IPlayableFileResolver playableFileResolver,
    IPlaybackSubtitleService playbackSubtitleService,
    IWebDavRangeStreamService webDavRangeStreamService,
    IPlaybackCacheService cacheService,
    IAppSettingsRepository settingsRepository,
    IHlsSessionService hlsSessionService,
    IMediaProbeService mediaProbeService,
    IProgress<BackgroundTaskProgress> progress,
    CancellationToken cancellationToken)
{
    _ = playbackSubtitleService;
    var settings = await settingsRepository.GetAsync(cancellationToken);
    var effectiveQuality = ResolveEffectiveQuality(null, settings.Playback.PlaybackQualityPreference);
    var remoteFile = await webDavRangeStreamService.GetFileInfoAsync(fileId, cancellationToken);
    if (remoteFile is not null)
    {
        progress.Report(new BackgroundTaskProgress("hls-cache", "正在准备 WebDAV 原文件缓存", 10, remoteFile.FileName));
        var cachedPath = await cacheService.EnsureCachedAsync(remoteFile.Id, cancellationToken);
        if (string.IsNullOrWhiteSpace(cachedPath))
        {
            return null;
        }

        var remoteProbe = CreateProbeFromStoredMetadata(remoteFile);
        var target = await BuildHlsPrewarmTargetAsync(
            remoteFile with { AbsolutePath = cachedPath },
            remoteProbe,
            effectiveQuality,
            settings.Playback,
            hlsSessionService,
            cancellationToken);
        progress.Report(new BackgroundTaskProgress("hls-cache", "WebDAV 原文件缓存已就绪", 100, remoteFile.FileName));
        return target;
    }

    var file = await playableFileResolver.ResolveAsync(fileId, cancellationToken);
    if (file is null)
    {
        return null;
    }

    var probe = await mediaProbeService.ProbeAsync(file.AbsolutePath, cancellationToken);
    if (probe is not null)
    {
        await repository.UpdateVideoFileProbeAsync(
            new VideoFileProbeUpdate(
                file.Id,
                probe.DurationSeconds,
                probe.Container,
                probe.VideoCodec,
                probe.AudioCodec,
                probe.SubtitleSummary,
                probe.RawJson),
            cancellationToken);
    }
    else
    {
        probe = CreateProbeFromStoredMetadata(file);
    }

    return await BuildHlsPrewarmTargetAsync(
        file,
        probe,
        effectiveQuality,
        settings.Playback,
        hlsSessionService,
        cancellationToken);
}

static async Task<HlsPrewarmTarget?> BuildHlsPrewarmTargetAsync(
    PlayableVideoFile file,
    MediaProbeSnapshot? probe,
    string effectiveQuality,
    PlaybackSettings playbackSettings,
    IHlsSessionService hlsSessionService,
    CancellationToken cancellationToken)
{
    var sourceVideoCodec = ResolveSourceVideoCodec(file.FileName, probe);
    var dynamicRange = AnalyzeSourceDynamicRange(file.FileName, probe);
    var plan = ResolvePlaybackPlan(file.FileName, probe);
    var profile = await ResolveRequestedProfileAsync(
        plan.Profile,
        effectiveQuality,
        audioTrackIndex: null,
        subtitleMode: "off",
        externalSubtitlePath: null,
        embeddedSubtitleStreamIndex: null,
        embeddedSubtitleCodec: null,
        sourceVideoCodec: sourceVideoCodec,
        hardware: null,
        toneMapToSdr: dynamicRange.ShouldToneMapToSdr,
        toneMapMode: dynamicRange.ToneMapMode,
        hlsSessionService: hlsSessionService,
        cancellationToken: cancellationToken);
    var mode = ResolveRequestedMode(plan.Mode, profile, effectiveQuality, audioTrackIndex: null, subtitleMode: "off");
    var policy = await ApplyPlaybackSettingsAsync(
        file.Id,
        plan,
        profile,
        mode,
        playbackSettings,
        effectiveQuality,
        audioTrackIndex: null,
        subtitleMode: "off",
        externalSubtitlePath: null,
        embeddedSubtitleStreamIndex: null,
        embeddedSubtitleCodec: null,
        sourceVideoCodec: sourceVideoCodec,
        hardware: null,
        hlsSessionService: hlsSessionService,
        cancellationToken: cancellationToken);
    if (policy.Decision is not null)
    {
        return null;
    }

    mode = policy.Mode;
    profile = policy.Profile;
    if (string.Equals(mode, "direct", StringComparison.OrdinalIgnoreCase))
    {
        mode = "hls-remux";
        profile = HlsPlaybackProfile.CreateRemux();
    }

    return string.Equals(mode, "unavailable", StringComparison.OrdinalIgnoreCase)
        ? null
        : new HlsPrewarmTarget(file, profile, mode);
}

static PlaybackPlan ResolvePlaybackPlan(string fileName, MediaProbeSnapshot? probe)
{
    var extension = Path.GetExtension(fileName).ToLowerInvariant();
    var videoCodec = ResolveSourceVideoCodec(fileName, probe);
    var dynamicRange = AnalyzeSourceDynamicRange(fileName, probe);
    var toneMapToSdr = dynamicRange.ShouldToneMapToSdr;
    if (dynamicRange.IsDolbyVisionOnly)
    {
        return new PlaybackPlan(
            "hls-transcode",
            HlsPlaybackProfile.Transcode,
            "该文件是 Dolby Vision-only，按兼容 SDR 路径转为 H.264/AAC HLS 播放。");
    }

    if (probe is null)
    {
        if (toneMapToSdr)
        {
            return new PlaybackPlan("hls-transcode", HlsPlaybackProfile.Transcode, "文件名显示 HDR/DV 视频，转为 H.264 SDR HLS 播放。");
        }

        if (!string.IsNullOrWhiteSpace(videoCodec) && !IsH264(videoCodec))
        {
            return new PlaybackPlan("hls-transcode", HlsPlaybackProfile.Transcode, $"文件名显示视频编码 {videoCodec}，转为 H.264/AAC HLS 播放。");
        }

        return extension is ".mp4" or ".m4v" or ".mov" or ".webm"
            ? new PlaybackPlan("direct", HlsPlaybackProfile.Remux, "媒体探测不可用，兼容容器先使用 Range 直出。")
            : new PlaybackPlan("hls-remux", HlsPlaybackProfile.Remux, "媒体探测不可用，先使用 FFmpeg HLS 转封装。");
    }

    var audioCodec = NormalizeCodec(probe.AudioCodec);

    if (toneMapToSdr)
    {
        return new PlaybackPlan("hls-transcode", HlsPlaybackProfile.Transcode, "HDR/DV 视频转为 H.264 SDR HLS 播放，避免浏览器色彩异常。");
    }

    if (extension is ".webm"
        && videoCodec is "vp8" or "vp9" or "av1"
        && IsWebAudioCompatible(audioCodec))
    {
        return new PlaybackPlan("direct", HlsPlaybackProfile.Remux, "WebM 编码兼容，使用 Range 直出。");
    }

    if (extension is ".mp4" or ".m4v" or ".mov"
        && IsH264(videoCodec)
        && IsMp4AudioCompatible(audioCodec))
    {
        return new PlaybackPlan("direct", HlsPlaybackProfile.Remux, "MP4/MOV 编码兼容，使用 Range 直出。");
    }

    if (IsH264(videoCodec) || string.IsNullOrWhiteSpace(videoCodec))
    {
        return new PlaybackPlan("hls-remux", HlsPlaybackProfile.Remux, "视频编码兼容，使用 FFmpeg HLS 转封装并转 AAC 音频。");
    }

    return new PlaybackPlan("hls-transcode", HlsPlaybackProfile.Transcode, $"当前 HLS 播放需要将视频编码 {videoCodec} 转为 H.264/AAC。");
}

static MediaProbeSnapshot? CreateProbeFromStoredMetadata(PlayableVideoFile file)
{
    if (string.IsNullOrWhiteSpace(file.Container)
        && string.IsNullOrWhiteSpace(file.VideoCodec)
        && string.IsNullOrWhiteSpace(file.AudioCodec)
        && string.IsNullOrWhiteSpace(file.SubtitleSummary)
        && file.DurationSeconds <= 0)
    {
        return null;
    }

    return new MediaProbeSnapshot(
        file.AbsolutePath,
        file.DurationSeconds,
        file.Container,
        file.VideoCodec,
        file.AudioCodec,
        file.SubtitleSummary,
        null,
        []);
}

static IReadOnlyList<VideoFileStreamSummary> BuildVideoFileStreamSummaries(MediaProbeSnapshot? probe, string kind)
{
    return probe?.Streams
        .Where(stream => string.Equals(stream.Kind, kind, StringComparison.OrdinalIgnoreCase))
        .Select(stream => new VideoFileStreamSummary(
            stream.Index,
            stream.Kind,
            stream.Codec,
            stream.Language,
            stream.Title,
            stream.Channels,
            stream.ChannelLayout,
            stream.IsDefault,
            stream.IsForced))
        .ToArray() ?? [];
}

static double ResolvePlaybackDurationSeconds(MediaProbeSnapshot? probe, double storedDurationSeconds)
{
    if (probe?.DurationSeconds > 0)
    {
        return probe.DurationSeconds;
    }

    return storedDurationSeconds > 0 ? storedDurationSeconds : 0;
}

static bool ShouldPrewarmNextEpisodeSubtitle(PlaybackProgressUpdateRequest request)
{
    if (request.DurationSeconds <= 0 || request.PositionSeconds <= 0)
    {
        return false;
    }

    return request.PositionSeconds >= request.DurationSeconds * 0.65;
}

static async Task<HlsPlaybackProfile> ResolveRequestedProfileAsync(
    HlsPlaybackProfile baseProfile,
    string? quality,
    int? audioTrackIndex,
    string subtitleMode,
    string? externalSubtitlePath,
    int? embeddedSubtitleStreamIndex,
    string? embeddedSubtitleCodec,
    string? sourceVideoCodec,
    bool? hardware,
    bool toneMapToSdr,
    string toneMapMode,
    IHlsSessionService hlsSessionService,
    CancellationToken cancellationToken)
{
    var normalizedQuality = NormalizeQuality(quality);
    var shouldBurnSubtitle = IsSubtitleBurnMode(subtitleMode)
                             && (externalSubtitlePath is not null || embeddedSubtitleStreamIndex.HasValue);
    var requiresQualityTranscode = normalizedQuality is not "original" and not "auto";
    var requiresTranscode = baseProfile.TranscodeVideo
                            || requiresQualityTranscode
                            || shouldBurnSubtitle
                            || toneMapToSdr;

    if (!requiresTranscode)
    {
        return HlsPlaybackProfile.CreateRemux(audioTrackIndex);
    }

    var capabilities = await hlsSessionService.GetCapabilitiesAsync(cancellationToken);
    var canUseHardware = hardware != false;
    var hardwareEncoder = canUseHardware ? capabilities.PreferredHardwareEncoder : null;
    var hardwareDecoder = canUseHardware ? SelectHardwareDecoder(capabilities, sourceVideoCodec) : null;
    var hardwareAcceleration = canUseHardware ? SelectHardwareAcceleration(capabilities, hardwareDecoder) : null;
    var effectiveToneMapMode =
        !toneMapToSdr
            ? "off"
            : string.Equals(toneMapMode, "dolby-vision", StringComparison.OrdinalIgnoreCase)
                ? "dolby-vision"
                : string.Equals(toneMapMode, "hardware", StringComparison.OrdinalIgnoreCase)
                  && string.Equals(ResolveHardwareKind(hardwareEncoder), "vaapi", StringComparison.OrdinalIgnoreCase)
                  && string.Equals(ResolveHardwareKind(hardwareDecoder), "vaapi", StringComparison.OrdinalIgnoreCase)
                  && string.Equals(hardwareAcceleration, "vaapi", StringComparison.OrdinalIgnoreCase)
                    ? "hardware"
                    : "software";

    var effectiveQuality = normalizedQuality switch
    {
        "original" => "auto",
        "auto" when toneMapToSdr => "1080p",
        "auto" => "auto",
        _ => normalizedQuality
    };

    return HlsPlaybackProfile.CreateTranscode(
        effectiveQuality,
        audioTrackIndex: audioTrackIndex,
        subtitleMode: shouldBurnSubtitle ? subtitleMode : "off",
        externalSubtitlePath: shouldBurnSubtitle ? externalSubtitlePath : null,
        embeddedSubtitleStreamIndex: shouldBurnSubtitle ? embeddedSubtitleStreamIndex : null,
        embeddedSubtitleCodec: shouldBurnSubtitle ? embeddedSubtitleCodec : null,
        hardwareEncoder: hardwareEncoder,
        hardwareDecoder: hardwareDecoder,
        hardwareAcceleration: hardwareAcceleration,
        toneMapToSdr: toneMapToSdr,
        toneMapMode: effectiveToneMapMode);
}

static string ResolveRequestedMode(
    string baseMode,
    HlsPlaybackProfile profile,
    string? quality,
    int? audioTrackIndex,
    string subtitleMode)
{
    if (baseMode == "direct"
        && !profile.TranscodeVideo
        && NormalizeQuality(quality) is "original" or "auto"
        && !audioTrackIndex.HasValue
        && !IsSubtitleBurnMode(subtitleMode))
    {
        return "direct";
    }

    if (baseMode == "unavailable")
    {
        return "unavailable";
    }

    return profile.TranscodeVideo ? "hls-transcode" : "hls-remux";
}

static async Task<PlaybackPolicyResult> ApplyPlaybackSettingsAsync(
    string fileId,
    PlaybackPlan plan,
    HlsPlaybackProfile profile,
    string mode,
    PlaybackSettings settings,
    string? quality,
    int? audioTrackIndex,
    string subtitleMode,
    string? externalSubtitlePath,
    int? embeddedSubtitleStreamIndex,
    string? embeddedSubtitleCodec,
    string? sourceVideoCodec,
    bool? hardware,
    IHlsSessionService hlsSessionService,
    CancellationToken cancellationToken)
{
    if (mode == "unavailable")
    {
        return Unavailable(fileId, plan.Reason);
    }

    if (RejectMissingHardwareEncoder(fileId, profile) is { } rejection)
    {
        return rejection;
    }

    if (mode == "direct")
    {
        if (settings.DirectStream)
        {
            return new PlaybackPolicyResult(mode, profile, plan.Reason, null);
        }

        if (settings.HlsRemux)
        {
            return new PlaybackPolicyResult(
                "hls-remux",
                HlsPlaybackProfile.CreateRemux(audioTrackIndex),
                "Range 直出策略已关闭，改用 HLS 转封装。",
                null);
        }

        if (settings.Transcode)
        {
            var transcodeProfile = await ResolveRequestedProfileAsync(
                HlsPlaybackProfile.Transcode,
                quality,
                audioTrackIndex,
                subtitleMode,
                externalSubtitlePath,
                embeddedSubtitleStreamIndex,
                embeddedSubtitleCodec,
                sourceVideoCodec,
                hardware,
                profile.ToneMapToSdr,
                profile.ToneMapMode,
                hlsSessionService,
                cancellationToken);
            if (RejectMissingHardwareEncoder(fileId, transcodeProfile) is { } transcodeRejection)
            {
                return transcodeRejection;
            }

            return new PlaybackPolicyResult(
                "hls-transcode",
                transcodeProfile,
                "Range 直出策略已关闭，改用 HLS 转码。",
                null);
        }

        return Unavailable(fileId, "没有启用可用的播放策略。");
    }

    if (mode == "hls-remux")
    {
        if (settings.HlsRemux)
        {
            return new PlaybackPolicyResult(mode, profile, plan.Reason, null);
        }

        if (settings.Transcode)
        {
            var transcodeProfile = await ResolveRequestedProfileAsync(
                HlsPlaybackProfile.Transcode,
                quality,
                audioTrackIndex,
                subtitleMode,
                externalSubtitlePath,
                embeddedSubtitleStreamIndex,
                embeddedSubtitleCodec,
                sourceVideoCodec,
                hardware,
                profile.ToneMapToSdr,
                profile.ToneMapMode,
                hlsSessionService,
                cancellationToken);
            if (RejectMissingHardwareEncoder(fileId, transcodeProfile) is { } transcodeRejection)
            {
                return transcodeRejection;
            }

            return new PlaybackPolicyResult(
                "hls-transcode",
                transcodeProfile,
                "HLS 转封装策略已关闭，改用 HLS 转码。",
                null);
        }

        if (settings.DirectStream)
        {
            return new PlaybackPolicyResult(
                "direct",
                profile,
                "HLS 转封装策略已关闭，尝试 Range 直出。",
                null);
        }

        return Unavailable(fileId, "当前文件需要 HLS 转封装或转码，但相关播放策略已关闭。");
    }

    if (mode == "hls-transcode")
    {
        if (RejectMissingHardwareEncoder(fileId, profile) is { } transcodeRejection)
        {
            return transcodeRejection;
        }

        if (settings.Transcode)
        {
            return new PlaybackPolicyResult(mode, profile, plan.Reason, null);
        }

        if (settings.DirectStream)
        {
            return new PlaybackPolicyResult(
                "direct",
                profile,
                "HLS 转码策略已关闭，尝试 Range 直出。",
                null);
        }

        return Unavailable(fileId, "当前请求需要 HLS 转码，但转码策略已关闭。");
    }

    return new PlaybackPolicyResult(mode, profile, plan.Reason, null);
}

static PlaybackPolicyResult? RejectMissingHardwareEncoder(string fileId, HlsPlaybackProfile profile)
{
    if (!profile.TranscodeVideo || !string.IsNullOrWhiteSpace(profile.HardwareEncoder))
    {
        return null;
    }

    return Unavailable(
        fileId,
        "当前请求需要视频转码，但 FFmpeg 没有检测到可用硬件编码器。请检查 /dev/dri 权限、VAAPI 驱动和 FFmpeg 硬件编码能力。");
}

static PlaybackPolicyResult Unavailable(string fileId, string reason)
{
    return new PlaybackPolicyResult(
        "unavailable",
        HlsPlaybackProfile.Remux,
        reason,
        new PlaybackDecision(fileId, "unavailable", null, null, null, false, reason));
}

static IProgress<BackgroundTaskProgress> ScaleProgress(
    IProgress<BackgroundTaskProgress> progress,
    double offset,
    double span)
{
    return new InlineProgress<BackgroundTaskProgress>(value =>
    {
        double? percent = value.Percent is null
            ? null
            : offset + Math.Clamp(value.Percent.Value, 0, 100) / 100 * span;
        progress.Report(value with { Percent = percent });
    });
}

static string ResolveProfileReason(string baseReason, HlsPlaybackProfile profile)
{
    if (!profile.TranscodeVideo)
    {
        return baseReason;
    }

    if (profile.ToneMapToSdr)
    {
        var toneMapMode = profile.ToneMapMode switch
        {
            var mode when string.Equals(mode, "hardware", StringComparison.OrdinalIgnoreCase) => "VAAPI 硬件色彩转换",
            var mode when string.Equals(mode, "dolby-vision", StringComparison.OrdinalIgnoreCase) => "Dolby Vision-only 兼容色彩转换",
            _ => "兼容色彩转换"
        };
        return $"{baseReason} 使用硬件编码 {profile.HardwareEncoder}，并将 HDR/DV 转为 SDR（{toneMapMode}），档位 {profile.QualityId}。";
    }

    if (string.IsNullOrWhiteSpace(profile.HardwareEncoder))
    {
        return $"{baseReason} 未检测到可用硬件编码器，无法转码。";
    }

    var decoder = string.IsNullOrWhiteSpace(profile.HardwareDecoder)
        ? "自动"
        : profile.HardwareDecoder;
    return $"{baseReason} 使用硬件编码 {profile.HardwareEncoder}，硬件解码 {decoder}，档位 {profile.QualityId}。";
}

static string NormalizeQuality(string? quality)
{
    var normalized = quality?.Trim().ToLowerInvariant();
    return string.IsNullOrWhiteSpace(normalized) ? "original" : normalized;
}

static string ResolveEffectiveQuality(string? quality, string? preference)
{
    if (!string.IsNullOrWhiteSpace(quality))
    {
        return NormalizeQuality(quality);
    }

    return preference?.Trim().ToLowerInvariant() switch
    {
        "original-priority" => "original",
        "compatibility" => "1080p",
        _ => "auto"
    };
}

static string ResolveSourceVideoCodec(string fileName, MediaProbeSnapshot? probe)
{
    var probedCodec = NormalizeCodec(probe?.VideoCodec);
    return string.IsNullOrWhiteSpace(probedCodec)
        ? DetectVideoCodecFromFileName(fileName)
        : NormalizeHardwareDecodeCodec(probedCodec);
}

static string DetectVideoCodecFromFileName(string fileName)
{
    var name = Path.GetFileName(fileName).ToLowerInvariant();
    var compact = CompactMediaTokenText(name);
    if (compact.Contains("h265", StringComparison.Ordinal)
        || compact.Contains("x265", StringComparison.Ordinal)
        || name.Contains("hevc", StringComparison.Ordinal))
    {
        return "hevc";
    }

    if (compact.Contains("h264", StringComparison.Ordinal)
        || compact.Contains("x264", StringComparison.Ordinal)
        || TokenizeMediaName(name).Contains("avc", StringComparer.Ordinal))
    {
        return "h264";
    }

    if (TokenizeMediaName(name).Contains("av1", StringComparer.Ordinal))
    {
        return "av1";
    }

    if (TokenizeMediaName(name).Contains("vp9", StringComparer.Ordinal))
    {
        return "vp9";
    }

    return string.Empty;
}

static SourceDynamicRange AnalyzeSourceDynamicRange(string fileName, MediaProbeSnapshot? probe)
{
    var name = Path.GetFileName(fileName).ToLowerInvariant();
    var tokens = TokenizeMediaName(name);
    var compact = CompactMediaTokenText(name);
    var rawJson = probe?.RawJson?.ToLowerInvariant();
    var hasDolbyVisionSignal =
        tokens.Any(static token => token is "dv" or "dovi")
        || compact.Contains("dolbyvision", StringComparison.Ordinal)
        || rawJson is not null
           && (rawJson.Contains("dv_profile", StringComparison.Ordinal)
               || rawJson.Contains("dolby vision", StringComparison.Ordinal)
               || rawJson.Contains("dovi", StringComparison.Ordinal));
    var hasHdrSignal =
        tokens.Any(static token => token is "hdr" or "hdr10" or "hdr10plus" or "hlg" or "edr")
        || rawJson is not null
           && (rawJson.Contains("smpte2084", StringComparison.Ordinal)
               || rawJson.Contains("arib-std-b67", StringComparison.Ordinal)
               || rawJson.Contains("bt2020", StringComparison.Ordinal));

    var compatibilityId = ResolveDolbyVisionCompatibilityId(probe?.RawJson);
    var dolbyVisionProfile = ResolveDolbyVisionProfile(probe?.RawJson);
    var hasExplicitDolbyVisionCompatibility = compatibilityId.HasValue || dolbyVisionProfile.HasValue;
    var hasDolbyVisionOnlySignal =
        compatibilityId == 0
        || dolbyVisionProfile == 5
        || (!hasExplicitDolbyVisionCompatibility && HasDolbyVisionOnlyNameHint(tokens))
        || HasIctcpDolbyVisionSignal(rawJson);
    if (hasDolbyVisionSignal && hasDolbyVisionOnlySignal)
    {
        return new SourceDynamicRange(true, true, "dolby-vision");
    }

    if (hasHdrSignal || hasDolbyVisionSignal)
    {
        return new SourceDynamicRange(true, false, hasDolbyVisionSignal ? "software" : ResolveToneMapMode(probe));
    }

    return new SourceDynamicRange(false, false, "off");
}

static bool HasDolbyVisionOnlyNameHint(string[] tokens)
{
    var hasDolbyVisionToken = tokens.Any(static token => token is "dv" or "dovi");
    var hasHdrFallbackToken = tokens.Any(static token => token is "hdr" or "hdr10" or "hdr10plus" or "hlg");
    return hasDolbyVisionToken && !hasHdrFallbackToken;
}

static bool HasIctcpDolbyVisionSignal(string? rawJson)
{
    return rawJson is not null
           && (rawJson.Contains("ictcp", StringComparison.Ordinal)
               || rawJson.Contains("ipt-pq-c2", StringComparison.Ordinal)
               || rawJson.Contains("iptpqc2", StringComparison.Ordinal));
}

static string ResolveToneMapMode(MediaProbeSnapshot? probe)
{
    return HasMasteringDisplayData(probe) ? "hardware" : "software";
}

static bool HasMasteringDisplayData(MediaProbeSnapshot? probe)
{
    var rawJson = probe?.RawJson;
    return rawJson is not null
           && rawJson.Contains("Mastering display metadata", StringComparison.OrdinalIgnoreCase);
}

static int? ResolveDolbyVisionCompatibilityId(string? rawJson)
{
    return FindIntPropertyInJson(rawJson, "dv_bl_signal_compatibility_id");
}

static int? ResolveDolbyVisionProfile(string? rawJson)
{
    return FindIntPropertyInJson(rawJson, "dv_profile");
}

static int? FindIntPropertyInJson(string? rawJson, string propertyName)
{
    if (string.IsNullOrWhiteSpace(rawJson))
    {
        return null;
    }

    try
    {
        using var document = JsonDocument.Parse(rawJson);
        return FindIntPropertyInElement(document.RootElement, propertyName);
    }
    catch (JsonException)
    {
        return null;
    }
}

static int? FindIntPropertyInElement(JsonElement element, string propertyName)
{
    switch (element.ValueKind)
    {
        case JsonValueKind.Object:
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(propertyName) && property.Value.TryGetInt32(out var value))
                {
                    return value;
                }

                var nested = FindIntPropertyInElement(property.Value, propertyName);
                if (nested.HasValue)
                {
                    return nested;
                }
            }

            return null;
        case JsonValueKind.Array:
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindIntPropertyInElement(item, propertyName);
                if (nested.HasValue)
                {
                    return nested;
                }
            }

            return null;
        default:
            return null;
    }
}

static string CompactMediaTokenText(string value)
{
    var builder = new StringBuilder(value.Length);
    foreach (var character in value)
    {
        if (char.IsLetterOrDigit(character))
        {
            builder.Append(character);
        }
    }

    return builder.ToString();
}

static string[] TokenizeMediaName(string value)
{
    return value
        .Split(['.', '_', '-', ' ', '[', ']', '(', ')', '+'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(static token => token.ToLowerInvariant())
        .ToArray();
}

static string NormalizeSubtitleMode(string? subtitleMode, string? externalSubtitlePath, int? embeddedSubtitleStreamIndex)
{
    var hasSelectedSubtitle = externalSubtitlePath is not null || embeddedSubtitleStreamIndex.HasValue;
    if (!hasSelectedSubtitle)
    {
        return "off";
    }

    return subtitleMode?.Trim().ToLowerInvariant() switch
    {
        "web" => "web",
        "burn" => "burn",
        "burn-bitmap" => "burn-bitmap",
        _ => "off"
    };
}

static bool IsSubtitleBurnMode(string? subtitleMode)
{
    return subtitleMode?.Trim().StartsWith("burn", StringComparison.OrdinalIgnoreCase) == true;
}

static string NormalizeCodec(string? codec)
{
    return codec?.Trim().ToLowerInvariant() ?? string.Empty;
}

static string NormalizeHardwareDecodeCodec(string? codec)
{
    return NormalizeCodec(codec) switch
    {
        "avc1" => "h264",
        "h265" => "hevc",
        "mpeg2video" => "mpeg2",
        "mpeg2_videotoolbox" => "mpeg2",
        "vp09" => "vp9",
        var value => value
    };
}

static string? SelectHardwareDecoder(FfmpegTranscodeCapabilities capabilities, string? sourceVideoCodec)
{
    if (!capabilities.IsAvailable
        || (capabilities.HardwareDecoders.Count == 0 && capabilities.HardwareAccelerators.Count == 0))
    {
        return null;
    }

    var normalizedCodec = NormalizeHardwareDecodeCodec(sourceVideoCodec);
    var preferredHardwareKind = ResolveHardwareKind(capabilities.PreferredHardwareEncoder);
    var candidates = normalizedCodec switch
    {
        "h264" => new[]
        {
            "h264_vaapi",
            "h264_qsv",
            "h264_cuvid",
            "h264_v4l2m2m",
            "h264_videotoolbox"
        },
        "hevc" => new[]
        {
            "hevc_vaapi",
            "hevc_qsv",
            "hevc_cuvid",
            "hevc_v4l2m2m",
            "hevc_videotoolbox"
        },
        "vp9" => new[]
        {
            "vp9_vaapi",
            "vp9_qsv",
            "vp9_cuvid"
        },
        "av1" => new[]
        {
            "av1_vaapi",
            "av1_qsv",
            "av1_cuvid"
        },
        "mpeg2" => new[]
        {
            "mpeg2_vaapi",
            "mpeg2_qsv",
            "mpeg2_cuvid",
            "mpeg2_v4l2m2m"
        },
        _ => Array.Empty<string>()
    };
    candidates = PrioritizeHardwareDecoderCandidates(candidates, preferredHardwareKind);

    var decoder = candidates.FirstOrDefault(candidate => capabilities.HardwareDecoders.Contains(candidate, StringComparer.OrdinalIgnoreCase));
    if (!string.IsNullOrWhiteSpace(decoder))
    {
        return decoder;
    }

    if (string.IsNullOrWhiteSpace(normalizedCodec))
    {
        return null;
    }

    foreach (var hardwareKind in PrioritizeHardwareKinds(["vaapi", "qsv", "cuda", "videotoolbox"], preferredHardwareKind))
    {
        if (!capabilities.HardwareAccelerators.Contains(hardwareKind, StringComparer.OrdinalIgnoreCase))
        {
            continue;
        }

        return hardwareKind switch
        {
            "cuda" => normalizedCodec == "hevc" ? "hevc_cuvid" : $"{normalizedCodec}_cuvid",
            _ => $"{normalizedCodec}_{hardwareKind}"
        };
    }

    return null;
}

static string[] PrioritizeHardwareDecoderCandidates(string[] candidates, string? preferredHardwareKind)
{
    if (string.IsNullOrWhiteSpace(preferredHardwareKind))
    {
        return candidates;
    }

    return candidates
        .OrderBy(candidate => string.Equals(ResolveHardwareKind(candidate), preferredHardwareKind, StringComparison.OrdinalIgnoreCase)
            ? 0
            : 1)
        .ToArray();
}

static IEnumerable<string> PrioritizeHardwareKinds(IEnumerable<string> hardwareKinds, string? preferredHardwareKind)
{
    if (string.IsNullOrWhiteSpace(preferredHardwareKind))
    {
        return hardwareKinds;
    }

    return hardwareKinds
        .OrderBy(kind => string.Equals(kind, preferredHardwareKind, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
}

static string? SelectHardwareAcceleration(
    FfmpegTranscodeCapabilities capabilities,
    string? hardwareDecoder)
{
    var decoderKind = ResolveHardwareKind(hardwareDecoder);
    if (IsHardwareKindAvailable(capabilities, decoderKind))
    {
        return decoderKind;
    }

    var encoderKind = ResolveHardwareKind(capabilities.PreferredHardwareEncoder);
    if (IsHardwareKindAvailable(capabilities, encoderKind))
    {
        return encoderKind;
    }

    return capabilities.HardwareAccelerators.FirstOrDefault();
}

static bool IsHardwareKindAvailable(FfmpegTranscodeCapabilities capabilities, string? kind)
{
    if (string.IsNullOrWhiteSpace(kind))
    {
        return false;
    }

    return capabilities.HardwareAccelerators.Contains(kind, StringComparer.OrdinalIgnoreCase)
           || capabilities.HardwareDecoders.Any(decoder => string.Equals(ResolveHardwareKind(decoder), kind, StringComparison.OrdinalIgnoreCase))
           || capabilities.HardwareEncoders.Any(encoder => string.Equals(ResolveHardwareKind(encoder), kind, StringComparison.OrdinalIgnoreCase));
}

static string? ResolveHardwareKind(string? codecName)
{
    if (string.IsNullOrWhiteSpace(codecName))
    {
        return null;
    }

    var normalized = codecName.Trim().ToLowerInvariant();
    if (normalized.EndsWith("_vaapi", StringComparison.Ordinal))
    {
        return "vaapi";
    }

    if (normalized.EndsWith("_qsv", StringComparison.Ordinal))
    {
        return "qsv";
    }

    if (normalized.EndsWith("_cuvid", StringComparison.Ordinal)
        || normalized.EndsWith("_nvenc", StringComparison.Ordinal))
    {
        return "cuda";
    }

    if (normalized.EndsWith("_v4l2m2m", StringComparison.Ordinal))
    {
        return "v4l2m2m";
    }

    if (normalized.EndsWith("_videotoolbox", StringComparison.Ordinal))
    {
        return "videotoolbox";
    }

    return null;
}

static bool IsH264(string codec)
{
    return codec is "h264" or "avc1";
}

static bool IsMp4AudioCompatible(string codec)
{
    return string.IsNullOrWhiteSpace(codec) || codec is "aac" or "mp3";
}

static bool IsWebAudioCompatible(string codec)
{
    return string.IsNullOrWhiteSpace(codec) || codec is "opus" or "vorbis";
}

static int? ResolveEmbeddedSubtitleStreamIndex(string? subtitleId)
{
    const string prefix = "embedded_";
    if (string.IsNullOrWhiteSpace(subtitleId)
        || !subtitleId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    var value = subtitleId[prefix.Length..];
    var subtitleOrdinalMarker = value.LastIndexOf("_si_", StringComparison.OrdinalIgnoreCase);
    if (subtitleOrdinalMarker >= 0)
    {
        value = value[(subtitleOrdinalMarker + 4)..];
    }

    return int.TryParse(value, out var streamIndex) && streamIndex >= 0
        ? streamIndex
        : null;
}

static string? ResolveEmbeddedSubtitleCodec(MediaProbeSnapshot? probe, int? embeddedSubtitleStreamIndex)
{
    if (!embeddedSubtitleStreamIndex.HasValue)
    {
        return null;
    }

    return probe?.Streams
        .Where(static stream => string.Equals(stream.Kind, "subtitle", StringComparison.OrdinalIgnoreCase))
        .Skip(embeddedSubtitleStreamIndex.Value)
        .FirstOrDefault()
        ?.Codec;
}

static MediaNameParser.CombinedSearchMetadataResult? ResolveSourceSearchMetadata(
    IReadOnlyList<VideoFileSummary> videoFiles)
{
    var sourceFile = videoFiles
        .OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();
    if (sourceFile is null)
    {
        return null;
    }

    return MediaNameParser.CombinedSearchMetadata(sourceFile.RelativePath, sourceFile.FileName);
}

static string ResolveMetadataSearchQuery(
    LibraryItemDetail item,
    MediaNameParser.CombinedSearchMetadataResult? sourceMetadata,
    string? manualQuery)
{
    if (!string.IsNullOrWhiteSpace(manualQuery))
    {
        return manualQuery.Trim();
    }

    return sourceMetadata?.ChineseTitle
           ?? sourceMetadata?.ParentChineseTitle
           ?? sourceMetadata?.ForeignTitle
           ?? sourceMetadata?.FullCleanTitle
           ?? item.Title;
}

static string ResolveMetadataDisplayTitle(
    string title,
    MediaNameParser.CombinedSearchMetadataResult? sourceMetadata)
{
    var normalizedTitle = ChineseTextNormalizer.NormalizeTitle(title);
    if (ContainsHan(normalizedTitle))
    {
        return normalizedTitle;
    }

    var sourceChineseTitle = sourceMetadata?.ChineseTitle ?? sourceMetadata?.ParentChineseTitle;
    return string.IsNullOrWhiteSpace(sourceChineseTitle)
        ? normalizedTitle
        : ChineseTextNormalizer.NormalizeTitle(sourceChineseTitle);
}

static bool ContainsHan(string? input)
{
    return !string.IsNullOrWhiteSpace(input) && input.Any(static character => character is >= '\u4e00' and <= '\u9fff');
}

static string? BuildMetadataSecondarySearchQuery(string primaryQuery, params string?[] candidates)
{
    var normalizedPrimary = NormalizeMetadataSearchKey(primaryQuery);
    foreach (var candidate in candidates)
    {
        var trimmed = candidate?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            continue;
        }

        if (!string.Equals(NormalizeMetadataSearchKey(trimmed), normalizedPrimary, StringComparison.Ordinal))
        {
            return trimmed;
        }
    }

    return null;
}

static string? NormalizeMetadataSearchYear(string? value)
{
    var trimmed = value?.Trim();
    if (string.IsNullOrWhiteSpace(trimmed))
    {
        return null;
    }

    return trimmed.Length >= 4 ? trimmed[..4] : trimmed;
}

static string NormalizeMetadataSearchKey(string value)
{
    var builder = new StringBuilder(value.Length);
    foreach (var character in value.Trim())
    {
        if (char.IsLetterOrDigit(character))
        {
            builder.Append(char.ToLowerInvariant(character));
        }
    }

    return builder.ToString();
}

static IReadOnlyList<string> ResolveMetadataSearchTypes(string? requestedMediaType, string itemKind)
{
    var normalizedItemKind = string.Equals(itemKind, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
    var requested = requestedMediaType?.Trim().ToLowerInvariant();
    if (requested is "movie" or "tv")
    {
        return [requested];
    }

    if (requested is "all" or "multi" or "any" or "*")
    {
        return normalizedItemKind == "tv" ? ["tv", "movie"] : ["movie", "tv"];
    }

    return [normalizedItemKind];
}

static async Task<IResult> SaveDoubanMetadataAndReturnDetailAsync(
    string libraryItemId,
    DoubanMetadata metadata,
    ILibraryRepository repository,
    CancellationToken cancellationToken)
{
    var updated = await repository.SaveDoubanMetadataAsync(metadata, cancellationToken);
    if (!updated)
    {
        return Results.NotFound();
    }

    var detail = await repository.GetItemDetailAsync(libraryItemId, cancellationToken);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
}

sealed record PlaybackPlan(string Mode, HlsPlaybackProfile Profile, string Reason);

sealed record PlaybackAccessTicket(string FileId, DateTimeOffset ExpiresAt);

sealed record PlaybackFileStreams(
    IReadOnlyList<VideoFileStreamSummary> AudioTracks,
    IReadOnlyList<VideoFileStreamSummary> SubtitleStreams);

sealed record SourceDynamicRange(bool ShouldToneMapToSdr, bool IsDolbyVisionOnly, string ToneMapMode);

sealed record PlaybackPolicyResult(
    string Mode,
    HlsPlaybackProfile Profile,
    string Reason,
    PlaybackDecision? Decision);

sealed record HlsCachePrepareRequest(
    IReadOnlyList<string>? LibraryItemIds,
    IReadOnlyList<string>? VideoFileIds);

sealed record HlsPrewarmTarget(
    PlayableVideoFile File,
    HlsPlaybackProfile Profile,
    string Mode);

sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> report;

    public InlineProgress(Action<T> report)
    {
        this.report = report;
    }

    public void Report(T value)
    {
        report(value);
    }
}

public partial class Program;
