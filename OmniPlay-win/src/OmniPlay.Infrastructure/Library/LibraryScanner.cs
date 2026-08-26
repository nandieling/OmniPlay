using Dapper;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Core.Models.Playback;
using OmniPlay.Infrastructure.Data;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OmniPlay.Infrastructure.Library;

public sealed class LibraryScanner : ILibraryScanner
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m2ts", ".m2t", ".iso", ".ts",
        ".rmvb", ".flv", ".webm", ".m4v"
    };

    private readonly SqliteDatabase database;
    private readonly IMediaSourceRepository mediaSourceRepository;
    private readonly ISettingsService? settingsService;
    private readonly IWebDavDiscoveryClient? webDavDiscoveryClient;
    private readonly ILocalMetadataSidecarService? localMetadataSidecarService;
    private readonly IMediaServerDiscoveryClient? mediaServerDiscoveryClient;
    private readonly IOmniPlayDockerClient? omniPlayDockerClient;
    private readonly IStoragePaths? storagePaths;
    private readonly object deferredUnidentifiedScanLock = new();
    private readonly List<DeferredPendingScanGroup> deferredUnidentifiedScanGroups = [];

    public LibraryScanner(
        SqliteDatabase database,
        IMediaSourceRepository mediaSourceRepository,
        ILibraryMetadataEnricher? metadataEnricher = null,
        ISettingsService? settingsService = null,
        IWebDavDiscoveryClient? webDavDiscoveryClient = null,
        ILocalMetadataSidecarService? localMetadataSidecarService = null,
        IMediaServerDiscoveryClient? mediaServerDiscoveryClient = null,
        IOmniPlayDockerClient? omniPlayDockerClient = null,
        IStoragePaths? storagePaths = null)
    {
        this.database = database;
        this.mediaSourceRepository = mediaSourceRepository;
        this.settingsService = settingsService;
        this.webDavDiscoveryClient = webDavDiscoveryClient;
        this.localMetadataSidecarService = localMetadataSidecarService;
        this.mediaServerDiscoveryClient = mediaServerDiscoveryClient;
        this.omniPlayDockerClient = omniPlayDockerClient;
        this.storagePaths = storagePaths;
    }

    public async Task<LibraryScanSummary> ScanAllAsync(CancellationToken cancellationToken = default)
    {
        await mediaSourceRepository.PurgeExpiredInactiveAsync(DateTimeOffset.UtcNow, cancellationToken);
        var sources = (await mediaSourceRepository.GetAllAsync(cancellationToken))
            .Where(static source => source.IsActive)
            .ToList();
        var newMovies = 0;
        var newVideoFiles = 0;
        var removedVideoFiles = 0;
        var newTvShows = 0;
        List<string> diagnostics = [];

        foreach (var source in sources.Where(static x => x.Id.HasValue))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await ScanSourceWithDiagnosticsAsync(source, cancellationToken);
            newMovies += result.NewMovieCount;
            newVideoFiles += result.NewVideoFileCount;
            removedVideoFiles += result.RemovedVideoFileCount;
            newTvShows += result.NewTvShowCount;
            diagnostics.AddRange(result.Diagnostics);
        }

        return new LibraryScanSummary(sources.Count, newMovies, newVideoFiles, removedVideoFiles, newTvShows, diagnostics);
    }

    public async Task<LibraryScanSummary> ScanSourceAsync(
        long sourceId,
        CancellationToken cancellationToken = default,
        Func<LibraryScanIndexedItem, CancellationToken, Task>? afterItemIndexed = null,
        bool deferUnidentifiedGroups = false)
    {
        await mediaSourceRepository.PurgeExpiredInactiveAsync(DateTimeOffset.UtcNow, cancellationToken);
        var source = (await mediaSourceRepository.GetAllAsync(cancellationToken))
            .FirstOrDefault(source => source.Id == sourceId && source.IsActive);
        if (source is null)
        {
            return new LibraryScanSummary(0, 0, 0);
        }

        return await ScanSourceWithDiagnosticsAsync(source, cancellationToken, afterItemIndexed, deferUnidentifiedGroups);
    }

    public void ClearDeferredUnidentifiedScanGroups()
    {
        lock (deferredUnidentifiedScanLock)
        {
            deferredUnidentifiedScanGroups.Clear();
        }
    }

    public async Task<LibraryScanSummary> CommitDeferredUnidentifiedScanGroupsAsync(CancellationToken cancellationToken = default)
    {
        List<DeferredPendingScanGroup> pendingGroups;
        lock (deferredUnidentifiedScanLock)
        {
            pendingGroups = deferredUnidentifiedScanGroups.ToList();
            deferredUnidentifiedScanGroups.Clear();
        }

        if (pendingGroups.Count == 0)
        {
            return new LibraryScanSummary(0, 0, 0);
        }

        var newMovies = 0;
        var newVideoFiles = 0;
        var newTvShows = 0;
        using var connection = database.OpenConnection();
        foreach (var pending in pendingGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var commitResult = await UpsertPendingScanGroupAsync(
                connection,
                pending.Source,
                pending.Group,
                cancellationToken);
            newMovies += commitResult.NewMovieCount;
            newTvShows += commitResult.NewTvShowCount;
            newVideoFiles += commitResult.NewVideoFileCount;
        }

        using (var transaction = connection.BeginTransaction())
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "DELETE FROM movie WHERE id NOT IN (SELECT DISTINCT movieId FROM videoFile WHERE movieId IS NOT NULL)",
                    transaction: transaction,
                    cancellationToken: cancellationToken));
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "DELETE FROM tvShow WHERE id NOT IN (SELECT DISTINCT episodeId FROM videoFile WHERE episodeId IS NOT NULL)",
                    transaction: transaction,
                    cancellationToken: cancellationToken));
            transaction.Commit();
        }

        return new LibraryScanSummary(0, newMovies, newVideoFiles, newTvShowCount: newTvShows);
    }

    private async Task<LibraryScanSummary> ScanLocalSourceAsync(
        MediaSource source,
        CancellationToken cancellationToken,
        Func<LibraryScanIndexedItem, CancellationToken, Task>? afterItemIndexed,
        bool deferUnidentifiedGroups)
    {
        if (!Directory.Exists(source.BaseUrl))
        {
            return CreateDiagnosticSummary(source, $"已跳过本地媒体源“{ResolveSourceDisplayName(source)}”：目录不存在。");
        }

        var scannedFiles = EnumerateLocalVideoFiles(source.BaseUrl, cancellationToken);
        return await ScanSourceAsync(source, scannedFiles, cancellationToken, afterItemIndexed, deferUnidentifiedGroups);
    }

    private async Task<LibraryScanSummary> ScanSmbSourceAsync(
        MediaSource source,
        CancellationToken cancellationToken,
        Func<LibraryScanIndexedItem, CancellationToken, Task>? afterItemIndexed,
        bool deferUnidentifiedGroups)
    {
        if (!source.IsValidConfiguration())
        {
            return CreateDiagnosticSummary(source, $"已跳过 SMB 媒体源“{ResolveSourceDisplayName(source)}”：共享路径无效。");
        }

        try
        {
            var auth = MediaSourceAuthConfigSerializer.DeserializeWebDav(source.AuthConfig);
            using var connection = SmbNetworkConnection.Connect(source.BaseUrl, auth?.Username, auth?.Password);
            if (!Directory.Exists(source.BaseUrl))
            {
                return CreateDiagnosticSummary(source, $"已跳过 SMB 媒体源“{ResolveSourceDisplayName(source)}”：共享文件夹不可访问。");
            }

            var scannedFiles = EnumerateLocalVideoFiles(source.BaseUrl, cancellationToken);
            return await ScanSourceAsync(source, scannedFiles, cancellationToken, afterItemIndexed, deferUnidentifiedGroups);
        }
        catch (InvalidOperationException ex)
        {
            return CreateDiagnosticSummary(source, $"扫描 SMB 媒体源“{ResolveSourceDisplayName(source)}”失败：{ex.Message}");
        }
    }

    private async Task<LibraryScanSummary> ScanWebDavSourceAsync(
        MediaSource source,
        CancellationToken cancellationToken,
        Func<LibraryScanIndexedItem, CancellationToken, Task>? afterItemIndexed,
        bool deferUnidentifiedGroups)
    {
        if (webDavDiscoveryClient is null)
        {
            return CreateDiagnosticSummary(source, $"已跳过 WebDAV 媒体源“{ResolveSourceDisplayName(source)}”：当前版本未启用 WebDAV 扫描。");
        }

        if (!source.IsValidConfiguration())
        {
            return CreateDiagnosticSummary(source, $"已跳过 WebDAV 媒体源“{ResolveSourceDisplayName(source)}”：地址配置无效。");
        }

        IReadOnlyList<WebDavFileEntry> discoveredFiles;
        try
        {
            discoveredFiles = await webDavDiscoveryClient.EnumerateFilesAsync(source, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateDiagnosticSummary(source, $"扫描 WebDAV 媒体源“{ResolveSourceDisplayName(source)}”失败：连接超时。");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return CreateDiagnosticSummary(source, $"扫描 WebDAV 媒体源“{ResolveSourceDisplayName(source)}”失败：{ex.Message}");
        }

        var scannedFiles = ResolvePrimaryVideoFiles(discoveredFiles
            .Where(file => VideoExtensions.Contains(Path.GetExtension(file.FileName)))
            .Select(file => new ScannedVideoFile(
                MediaSourcePathResolver.ResolveMetadataPath(
                    source.ProtocolKind,
                    source.BaseUrl,
                    file.RelativePath),
                NormalizeRelativePath(file.RelativePath),
                file.FileName,
                file.ContentLength))
            .ToList());

        return await ScanSourceAsync(source, scannedFiles, cancellationToken, afterItemIndexed, deferUnidentifiedGroups);
    }

    private async Task<LibraryScanSummary> ScanMediaServerSourceAsync(
        MediaSource source,
        CancellationToken cancellationToken,
        Func<LibraryScanIndexedItem, CancellationToken, Task>? afterItemIndexed,
        bool deferUnidentifiedGroups)
    {
        if (mediaServerDiscoveryClient is null)
        {
            return CreateDiagnosticSummary(source, $"已跳过媒体源“{ResolveSourceDisplayName(source)}”：当前版本未启用媒体服务器扫描。");
        }

        if (!source.IsValidConfiguration())
        {
            return CreateDiagnosticSummary(source, $"已跳过媒体源“{ResolveSourceDisplayName(source)}”：服务器地址配置无效。");
        }

        IReadOnlyList<MediaServerFileEntry> discoveredFiles;
        try
        {
            discoveredFiles = await mediaServerDiscoveryClient.EnumerateFilesAsync(source, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateDiagnosticSummary(source, $"扫描媒体源“{ResolveSourceDisplayName(source)}”失败：连接超时。");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return CreateDiagnosticSummary(source, $"扫描媒体源“{ResolveSourceDisplayName(source)}”失败：{ex.Message}");
        }

        var scannedFiles = ResolvePrimaryVideoFiles(discoveredFiles
            .Select(file => new ScannedVideoFile(
                file.MetadataPath,
                NormalizeRelativePath(file.RelativePath),
                file.FileName,
                file.ContentLength,
                file.MediaType))
            .ToList());

        return await ScanSourceAsync(source, scannedFiles, cancellationToken, afterItemIndexed, deferUnidentifiedGroups);
    }

    private async Task<LibraryScanSummary> ScanOmniPlayDockerSourceAsync(
        MediaSource source,
        CancellationToken cancellationToken)
    {
        if (omniPlayDockerClient is null || source.Id is null)
        {
            return CreateDiagnosticSummary(source, "扫描 OmniPlay Docker 失败：缺少 Docker API 客户端。");
        }

        try
        {
            var items = await omniPlayDockerClient.GetLibraryItemsAsync(source, cancellationToken);
            var details = new List<OmniPlayDockerLibraryDetail>();
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var detail = await omniPlayDockerClient.GetLibraryDetailAsync(source, item.Id, cancellationToken);
                if (detail is not null)
                {
                    details.Add(detail);
                }
            }

            return await ImportOmniPlayDockerDetailsAsync(source, details, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
        {
            return CreateDiagnosticSummary(source, $"扫描 OmniPlay Docker“{ResolveSourceDisplayName(source)}”失败：{ex.Message}");
        }
    }

    private async Task<LibraryScanSummary> ImportOmniPlayDockerDetailsAsync(
        MediaSource source,
        IReadOnlyList<OmniPlayDockerLibraryDetail> details,
        CancellationToken cancellationToken)
    {
        var sourceId = source.Id!.Value;
        var expectedFileIds = details
            .SelectMany(FlattenDockerFiles)
            .Select(file => BuildOmniPlayDockerLocalVideoFileId(sourceId, file.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newMovies = 0;
        var newTvShows = 0;
        var newVideoFiles = 0;
        var removedVideoFiles = 0;
        var importedAssets = await ImportOmniPlayDockerAssetsAsync(source, details, cancellationToken);
        var dockerThumbnailDirectoryPrefix = storagePaths is null
            ? string.Empty
            : Path.Combine(storagePaths.ThumbnailsDirectory, "omniplay-docker-");

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var detail in details)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isTv = string.Equals(detail.ItemKind, "tv", StringComparison.OrdinalIgnoreCase) ||
                       detail.Seasons.Any(season => season.Episodes.Any(episode => GetDockerEpisodeFiles(episode).Count > 0));
            var entityId = CreateSyntheticEntityId(isTv ? "docker-tv" : "docker-movie", sourceId, detail.Id);
            importedAssets.Posters.TryGetValue(detail.Id, out var posterPath);
            var existingPosterPath = await connection.ExecuteScalarAsync<string?>(
                new CommandDefinition(
                    isTv
                        ? "SELECT posterPath FROM tvShow WHERE id = @Id"
                        : "SELECT posterPath FROM movie WHERE id = @Id",
                    new { Id = entityId },
                    transaction,
                    cancellationToken: cancellationToken));
            var importedDouban = BuildDoubanMetadata(detail, isTv ? "tv" : "movie", entityId);
            var effectiveOverview = string.IsNullOrWhiteSpace(detail.Overview)
                ? importedDouban?.Summary
                : detail.Overview;
            posterPath ??= existingPosterPath ?? importedDouban?.PosterUrl;

            if (isTv)
            {
                var existed = await connection.ExecuteScalarAsync<int>(
                    new CommandDefinition(
                        "SELECT COUNT(*) FROM tvShow WHERE id = @Id",
                        new { Id = entityId },
                        transaction,
                        cancellationToken: cancellationToken));
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO tvShow (id, title, firstAirDate, overview, posterPath, voteAverage, isLocked)
                        VALUES (@Id, @Title, @FirstAirDate, @Overview, @PosterPath, @VoteAverage, 1)
                        ON CONFLICT(id) DO UPDATE SET
                            title = excluded.title,
                            firstAirDate = excluded.firstAirDate,
                            overview = excluded.overview,
                            posterPath = COALESCE(excluded.posterPath, posterPath),
                            voteAverage = excluded.voteAverage,
                            isLocked = 1
                        """,
                        new
                        {
                            Id = entityId,
                            Title = detail.Title,
                            FirstAirDate = detail.ReleaseDate,
                            Overview = effectiveOverview,
                            PosterPath = posterPath,
                            VoteAverage = detail.VoteAverage
                        },
                        transaction,
                        cancellationToken: cancellationToken));
                if (existed == 0)
                {
                    newTvShows++;
                }
            }
            else
            {
                var existed = await connection.ExecuteScalarAsync<int>(
                    new CommandDefinition(
                        "SELECT COUNT(*) FROM movie WHERE id = @Id",
                        new { Id = entityId },
                        transaction,
                        cancellationToken: cancellationToken));
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO movie (id, title, releaseDate, overview, posterPath, voteAverage, isLocked)
                        VALUES (@Id, @Title, @ReleaseDate, @Overview, @PosterPath, @VoteAverage, 1)
                        ON CONFLICT(id) DO UPDATE SET
                            title = excluded.title,
                            releaseDate = excluded.releaseDate,
                            overview = excluded.overview,
                            posterPath = COALESCE(excluded.posterPath, posterPath),
                            voteAverage = excluded.voteAverage,
                            isLocked = 1
                        """,
                        new
                        {
                            Id = entityId,
                            Title = detail.Title,
                            ReleaseDate = detail.ReleaseDate,
                            Overview = effectiveOverview,
                            PosterPath = posterPath,
                            VoteAverage = detail.VoteAverage
                        },
                        transaction,
                        cancellationToken: cancellationToken));
                if (existed == 0)
                {
                    newMovies++;
                }
            }

            if (importedDouban is not null)
            {
                await SaveImportedDoubanMetadataAsync(connection, transaction, importedDouban, cancellationToken);
            }

            foreach (var file in FlattenDockerFiles(detail))
            {
                var localFileId = BuildOmniPlayDockerLocalVideoFileId(sourceId, file.Id);
                importedAssets.Thumbnails.TryGetValue(file.Id, out var thumbnailPath);
                var existed = await connection.ExecuteScalarAsync<int>(
                    new CommandDefinition(
                        "SELECT COUNT(*) FROM videoFile WHERE id = @Id",
                        new { Id = localFileId },
                        transaction,
                        cancellationToken: cancellationToken));
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO videoFile (
                            id, sourceId, metadataPath, relativePath, fileName, mediaType,
                            movieId, episodeId, playProgress, duration, customEpisodeThumbnailPath)
                        VALUES (
                            @Id, @SourceId, @MetadataPath, @RelativePath, @FileName, @MediaType,
                            @MovieId, @EpisodeId, @PlayProgress, @Duration, @CustomEpisodeThumbnailPath)
                        ON CONFLICT(id) DO UPDATE SET
                            metadataPath = excluded.metadataPath,
                            relativePath = excluded.relativePath,
                            fileName = excluded.fileName,
                            mediaType = excluded.mediaType,
                            movieId = excluded.movieId,
                            episodeId = excluded.episodeId,
                            playProgress = excluded.playProgress,
                            duration = excluded.duration,
                            customEpisodeThumbnailPath = CASE
                                WHEN videoFile.customEpisodeThumbnailPath IS NULL THEN excluded.customEpisodeThumbnailPath
                                WHEN @DockerThumbnailDirectoryPrefix <> ''
                                     AND instr(videoFile.customEpisodeThumbnailPath, @DockerThumbnailDirectoryPrefix) = 1
                                    THEN COALESCE(excluded.customEpisodeThumbnailPath, videoFile.customEpisodeThumbnailPath)
                                ELSE videoFile.customEpisodeThumbnailPath
                            END
                        """,
                        new
                        {
                            Id = localFileId,
                            SourceId = sourceId,
                            MetadataPath = file.RelativePath,
                            RelativePath = $"api/playback/files/{Uri.EscapeDataString(file.Id)}/stream",
                            FileName = ResolveOmniPlayDockerDisplayFileName(file),
                            MediaType = isTv ? "tv" : "movie",
                            MovieId = isTv ? (long?)null : entityId,
                            EpisodeId = isTv ? entityId : (long?)null,
                            PlayProgress = Math.Max(file.PositionSeconds, 0),
                            Duration = Math.Max(file.DurationSeconds, 0),
                            CustomEpisodeThumbnailPath = thumbnailPath,
                            DockerThumbnailDirectoryPrefix = dockerThumbnailDirectoryPrefix
                        },
                        transaction,
                        cancellationToken: cancellationToken));
                if (existed == 0)
                {
                    newVideoFiles++;
                }
            }
        }

        if (expectedFileIds.Count > 0)
        {
            var existingIds = (await connection.QueryAsync<string>(
                new CommandDefinition(
                    "SELECT id FROM videoFile WHERE sourceId = @SourceId",
                    new { SourceId = sourceId },
                    transaction,
                    cancellationToken: cancellationToken))).ToList();
            foreach (var staleId in existingIds.Where(id => !expectedFileIds.Contains(id)))
            {
                removedVideoFiles += await connection.ExecuteAsync(
                    new CommandDefinition(
                        "DELETE FROM videoFile WHERE id = @Id",
                        new { Id = staleId },
                        transaction,
                        cancellationToken: cancellationToken));
            }
        }

        transaction.Commit();
        return new LibraryScanSummary(1, newMovies, newVideoFiles, removedVideoFiles, newTvShows);
    }

    private async Task<LibraryScanSummary> ScanSourceWithDiagnosticsAsync(
        MediaSource source,
        CancellationToken cancellationToken,
        Func<LibraryScanIndexedItem, CancellationToken, Task>? afterItemIndexed = null,
        bool deferUnidentifiedGroups = false)
    {
        try
        {
            return source.ProtocolKind switch
            {
                MediaSourceProtocol.Local => await ScanLocalSourceAsync(source, cancellationToken, afterItemIndexed, deferUnidentifiedGroups),
                MediaSourceProtocol.WebDav => await ScanWebDavSourceAsync(source, cancellationToken, afterItemIndexed, deferUnidentifiedGroups),
                MediaSourceProtocol.Smb => await ScanSmbSourceAsync(source, cancellationToken, afterItemIndexed, deferUnidentifiedGroups),
                MediaSourceProtocol.Plex or MediaSourceProtocol.Emby or MediaSourceProtocol.Jellyfin =>
                    await ScanMediaServerSourceAsync(source, cancellationToken, afterItemIndexed, deferUnidentifiedGroups),
                MediaSourceProtocol.OmniPlayDocker => await ScanOmniPlayDockerSourceAsync(source, cancellationToken),
                MediaSourceProtocol.Direct => CreateDiagnosticSummary(source, $"已跳过媒体源“{ResolveSourceDisplayName(source)}”：当前版本暂不支持直接链接扫描。"),
                _ => CreateDiagnosticSummary(source, $"已跳过媒体源“{ResolveSourceDisplayName(source)}”：协议类型无效。")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            return CreateDiagnosticSummary(source, $"扫描媒体源“{ResolveSourceDisplayName(source)}”失败：{ex.Message}");
        }
        catch (IOException ex)
        {
            return CreateDiagnosticSummary(source, $"扫描媒体源“{ResolveSourceDisplayName(source)}”失败：{ex.Message}");
        }
    }

    private async Task<LibraryScanSummary> ScanSourceAsync(
        MediaSource source,
        IReadOnlyList<ScannedVideoFile> scannedFiles,
        CancellationToken cancellationToken,
        Func<LibraryScanIndexedItem, CancellationToken, Task>? afterItemIndexed = null,
        bool deferUnidentifiedGroups = false)
    {
        var newMovies = 0;
        var newVideoFiles = 0;
        var removedVideoFiles = 0;
        var newTvShows = 0;
        var importLocalMetadata = await ShouldImportLocalMetadataAsync(source, cancellationToken);

        using var connection = database.OpenConnection();
        var relativePaths = scannedFiles
            .Select(static file => file.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ExistingVideoFileRecord> existingFilesByRelativePath;

        {
            using var transaction = connection.BeginTransaction();
            var existingFiles = (await connection.QueryAsync<ExistingVideoFileRecord>(
                new CommandDefinition(
                    """
                    SELECT id AS Id,
                           relativePath AS RelativePath,
                           movieId AS MovieId,
                           episodeId AS EpisodeId,
                           playProgress AS PlayProgress,
                           duration AS Duration
                    FROM videoFile
                    WHERE sourceId = @SourceId
                    """,
                    new { SourceId = source.Id!.Value },
                    transaction,
                    cancellationToken: cancellationToken))).ToList();

            existingFilesByRelativePath = await NormalizeExistingFilesAsync(
                connection,
                transaction,
                existingFiles,
                cancellationToken);
            removedVideoFiles += existingFiles.Count - existingFilesByRelativePath.Count;

            foreach (var existingFile in existingFilesByRelativePath.Values.Where(x => !relativePaths.Contains(x.RelativePath)).ToList())
            {
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        "DELETE FROM videoFile WHERE id = @Id",
                        new { existingFile.Id },
                        transaction,
                        cancellationToken: cancellationToken));
                removedVideoFiles++;
                existingFilesByRelativePath.Remove(existingFile.RelativePath);
            }

            transaction.Commit();
        }

        var pendingGroups = BuildPendingScanGroups(
            source,
            scannedFiles,
            existingFilesByRelativePath,
            importLocalMetadata);
        var groupsToCommit = pendingGroups;
        if (deferUnidentifiedGroups)
        {
            var deferredGroups = pendingGroups
                .Where(static group => !group.ShouldAttemptAutomaticScrape)
                .Select(group => new DeferredPendingScanGroup(source, group))
                .ToList();
            if (deferredGroups.Count > 0)
            {
                lock (deferredUnidentifiedScanLock)
                {
                    deferredUnidentifiedScanGroups.AddRange(deferredGroups);
                }
            }

            groupsToCommit = pendingGroups
                .Where(static group => group.ShouldAttemptAutomaticScrape)
                .ToList();
        }

        foreach (var group in groupsToCommit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var commitResult = await UpsertPendingScanGroupAsync(
                connection,
                source,
                group,
                cancellationToken);
            newMovies += commitResult.NewMovieCount;
            newTvShows += commitResult.NewTvShowCount;
            newVideoFiles += commitResult.NewVideoFileCount;

            if (afterItemIndexed is not null && commitResult.IndexedItem is not null)
            {
                await afterItemIndexed(commitResult.IndexedItem, cancellationToken);
            }
        }

        using (var transaction = connection.BeginTransaction())
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "DELETE FROM movie WHERE id NOT IN (SELECT DISTINCT movieId FROM videoFile WHERE movieId IS NOT NULL)",
                    transaction: transaction,
                    cancellationToken: cancellationToken));
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "DELETE FROM tvShow WHERE id NOT IN (SELECT DISTINCT episodeId FROM videoFile WHERE episodeId IS NOT NULL)",
                    transaction: transaction,
                    cancellationToken: cancellationToken));
            transaction.Commit();
        }

        return new LibraryScanSummary(1, newMovies, newVideoFiles, removedVideoFiles, newTvShows);
    }

    private IReadOnlyList<PendingScanGroup> BuildPendingScanGroups(
        MediaSource source,
        IReadOnlyList<ScannedVideoFile> scannedFiles,
        IReadOnlyDictionary<string, ExistingVideoFileRecord> existingFilesByRelativePath,
        bool importLocalMetadata)
    {
        List<string> orderedKeys = [];
        Dictionary<string, PendingScanGroup> groups = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenScannedPaths = new(StringComparer.OrdinalIgnoreCase);

        foreach (var scannedFile in scannedFiles)
        {
            if (!seenScannedPaths.Add(scannedFile.RelativePath))
            {
                continue;
            }

            var metadata = MediaNameParser.CombinedSearchMetadata(scannedFile.RelativePath, scannedFile.FileName);
            var isTv = string.Equals(scannedFile.MediaType, "tv", StringComparison.OrdinalIgnoreCase) ||
                       MediaNameParser.IsLikelyTvEpisodePath(scannedFile.RelativePath) ||
                       MediaNameParser.IsLikelyTvEpisodePath(scannedFile.FileName);
            existingFilesByRelativePath.TryGetValue(scannedFile.RelativePath, out var existingFile);

            if (isTv)
            {
                var localShowMetadata = importLocalMetadata
                    ? localMetadataSidecarService?.ReadTvShowMetadata(scannedFile.MetadataPath)
                    : null;
                var localEpisodeMetadata = importLocalMetadata
                    ? localMetadataSidecarService?.ReadEpisodeMetadata(scannedFile.MetadataPath)
                    : null;
                var importedTitle = NormalizeText(localShowMetadata?.Title);
                var resolvedShowTitle = ResolveShowTitle(
                    scannedFile.MetadataPath,
                    scannedFile.RelativePath,
                    scannedFile.FileName,
                    source.ProtocolKind,
                    metadata);
                var shouldAttemptAutomaticScrape = importedTitle is not null || resolvedShowTitle is not null;
                var showTitle = importedTitle
                    ?? resolvedShowTitle
                    ?? ResolveFallbackDisplayTitle(scannedFile.RelativePath, scannedFile.FileName, isTv: true);

                if (!shouldAttemptAutomaticScrape)
                {
                    var unidentifiedMovieId = existingFile?.MovieId
                                              ?? CreateSyntheticEntityId(
                                                  "unidentified-tv",
                                                  source.Id!.Value,
                                                  showTitle.ToLowerInvariant());
                    var unidentifiedMovieGroupKey = $"movie:{unidentifiedMovieId}";
                    if (!groups.TryGetValue(unidentifiedMovieGroupKey, out var unidentifiedMovieGroup))
                    {
                        unidentifiedMovieGroup = new PendingScanGroup(
                            "movie",
                            unidentifiedMovieId,
                            showTitle,
                            metadata: null,
                            shouldAttemptAutomaticScrape: false);
                        groups[unidentifiedMovieGroupKey] = unidentifiedMovieGroup;
                        orderedKeys.Add(unidentifiedMovieGroupKey);
                    }

                    unidentifiedMovieGroup.Files.Add(new PendingScanFile(scannedFile, existingFile, null));
                    continue;
                }

                var tvShowId = CreateSyntheticEntityId(
                    "tv",
                    source.Id!.Value,
                    showTitle);
                var groupKey = $"tv:{tvShowId}";
                if (!groups.TryGetValue(groupKey, out var group))
                {
                    group = new PendingScanGroup(
                        "tv",
                        tvShowId,
                        showTitle,
                        localShowMetadata,
                        shouldAttemptAutomaticScrape,
                        existingFile?.EpisodeId);
                    groups[groupKey] = group;
                    orderedKeys.Add(groupKey);
                }
                else if (group.Metadata is null && localShowMetadata is not null)
                {
                    group.Metadata = localShowMetadata;
                }
                if (group.CopyFromTvShowId is null && existingFile?.EpisodeId is not null)
                {
                    group.CopyFromTvShowId = existingFile.EpisodeId;
                }

                group.Files.Add(new PendingScanFile(scannedFile, existingFile, localEpisodeMetadata));
                continue;
            }

            var localMovieMetadata = importLocalMetadata
                ? localMetadataSidecarService?.ReadMovieMetadata(scannedFile.MetadataPath)
                : null;
            var importedMovieTitle = NormalizeText(localMovieMetadata?.Title);
            var resolvedMovieTitle = ResolveMovieTitle(
                scannedFile.MetadataPath,
                scannedFile.RelativePath,
                scannedFile.FileName,
                metadata);
            var shouldAttemptMovieScrape = importedMovieTitle is not null || resolvedMovieTitle is not null;
            var movieTitle = importedMovieTitle
                ?? resolvedMovieTitle
                ?? ResolveFallbackDisplayTitle(scannedFile.RelativePath, scannedFile.FileName, isTv: false);

            var movieId = existingFile?.MovieId
                          ?? CreateSyntheticEntityId(
                              shouldAttemptMovieScrape ? "movie" : "unidentified-movie",
                              source.Id!.Value,
                              shouldAttemptMovieScrape
                                  ? GetMovieGroupingKey(scannedFile.RelativePath, movieTitle)
                                  : scannedFile.RelativePath);
            var movieGroupKey = $"movie:{movieId}";
            if (!groups.TryGetValue(movieGroupKey, out var movieGroup))
            {
                movieGroup = new PendingScanGroup("movie", movieId, movieTitle, localMovieMetadata, shouldAttemptMovieScrape);
                groups[movieGroupKey] = movieGroup;
                orderedKeys.Add(movieGroupKey);
            }
            else if (movieGroup.Metadata is null && localMovieMetadata is not null)
            {
                movieGroup.Metadata = localMovieMetadata;
            }

            movieGroup.Files.Add(new PendingScanFile(scannedFile, existingFile, null));
        }

        var orderedGroups = orderedKeys.Select(key => groups[key]).ToList();
        return orderedGroups
            .Where(static group => group.ShouldAttemptAutomaticScrape)
            .Concat(orderedGroups.Where(static group => !group.ShouldAttemptAutomaticScrape))
            .ToList();
    }

    private async Task<PendingScanGroupCommitResult> UpsertPendingScanGroupAsync(
        System.Data.IDbConnection connection,
        MediaSource source,
        PendingScanGroup group,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();
        var newMovies = 0;
        var newTvShows = 0;
        var newVideoFiles = 0;

        if (group.IsTvShow)
        {
            if (await EnsureTvShowAsync(
                    connection,
                    transaction,
                    group.EntityId,
                    group.Title,
                    group.CopyFromTvShowId,
                    !group.ShouldAttemptAutomaticScrape,
                    group.ShouldAttemptAutomaticScrape ? null : "未能自动识别影视名称，可手动重新匹配。",
                    cancellationToken))
            {
                newTvShows++;
            }

            if (group.Metadata is not null)
            {
                await ApplyLocalTvShowMetadataAsync(
                    connection,
                    transaction,
                    group.EntityId,
                    group.Metadata,
                    cancellationToken);
            }

            foreach (var file in group.Files)
            {
                if (file.ExistingFile is not null)
                {
                    await UpdateExistingVideoFileAsync(
                        connection,
                        transaction,
                        file.ExistingFile,
                        file.ScannedFile,
                        "tv",
                        movieId: null,
                        episodeId: group.EntityId,
                        cancellationToken,
                        file.EpisodeMetadata);
                    continue;
                }

                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO videoFile (
                            id,
                            sourceId,
                            metadataPath,
                            relativePath,
                            fileName,
                            mediaType,
                            movieId,
                            episodeId,
                            playProgress,
                            duration,
                            customSeasonNumber,
                            customEpisodeNumber,
                            customEpisodeSubtitle,
                            customEpisodeThumbnailPath)
                        VALUES (
                            @Id,
                            @SourceId,
                            @MetadataPath,
                            @RelativePath,
                            @FileName,
                            'tv',
                            NULL,
                            @EpisodeId,
                            0,
                            0,
                            @CustomSeasonNumber,
                            @CustomEpisodeNumber,
                            @CustomEpisodeSubtitle,
                            @CustomEpisodeThumbnailPath)
                        """,
                        new
                        {
                            Id = Guid.NewGuid().ToString(),
                            SourceId = source.Id!.Value,
                            MetadataPath = file.ScannedFile.MetadataPath,
                            RelativePath = file.ScannedFile.RelativePath,
                            FileName = file.ScannedFile.FileName,
                            EpisodeId = group.EntityId,
                            CustomSeasonNumber = file.EpisodeMetadata?.SeasonNumber,
                            CustomEpisodeNumber = file.EpisodeMetadata?.EpisodeNumber,
                            CustomEpisodeSubtitle = NormalizeText(file.EpisodeMetadata?.Title),
                            CustomEpisodeThumbnailPath = NormalizeText(file.EpisodeMetadata?.ThumbnailPath)
                        },
                        transaction,
                        cancellationToken: cancellationToken));
                newVideoFiles++;
            }
        }
        else
        {
            var existingMovie = await connection.ExecuteScalarAsync<long?>(
                new CommandDefinition(
                    "SELECT id FROM movie WHERE id = @Id LIMIT 1",
                    new { Id = group.EntityId },
                    transaction,
                    cancellationToken: cancellationToken));

            if (!existingMovie.HasValue)
            {
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO movie (
                            id,
                            title,
                            releaseDate,
                            overview,
                            posterPath,
                            voteAverage,
                            isLocked,
                            metadataLanguage)
                        VALUES (
                            @Id,
                            @Title,
                            @ReleaseDate,
                            @Overview,
                            @PosterPath,
                            @VoteAverage,
                            @IsLocked,
                            @MetadataLanguage)
                        """,
                        new
                        {
                            Id = group.EntityId,
                            Title = group.Title,
                            ReleaseDate = NormalizeText(group.Metadata?.Date),
                            Overview = NormalizeText(group.Metadata?.Overview)
                                       ?? (group.ShouldAttemptAutomaticScrape ? null : "未能自动识别影视名称，可手动重新匹配。"),
                            PosterPath = NormalizeText(group.Metadata?.PosterPath),
                            VoteAverage = group.Metadata?.VoteAverage,
                            IsLocked = group.Metadata is not null || !group.ShouldAttemptAutomaticScrape ? 1 : 0,
                            MetadataLanguage = group.Metadata is null ? null : "local"
                        },
                        transaction,
                        cancellationToken: cancellationToken));
                newMovies++;
            }
            else if (group.Metadata is not null)
            {
                await ApplyLocalMovieMetadataAsync(
                    connection,
                    transaction,
                    group.EntityId,
                    group.Metadata with { Title = group.Title },
                    cancellationToken);
            }
            else if (group.Files.Any(static file => file.ExistingFile?.MovieId is not null))
            {
                await UpdateUnenrichedMoviePlaceholderAsync(
                    connection,
                    transaction,
                    group.EntityId,
                    group.Title,
                    cancellationToken);
            }

            foreach (var file in group.Files)
            {
                if (file.ExistingFile is not null)
                {
                    await UpdateExistingVideoFileAsync(
                        connection,
                        transaction,
                        file.ExistingFile,
                        file.ScannedFile,
                        "movie",
                        group.EntityId,
                        episodeId: null,
                        cancellationToken);
                    continue;
                }

                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO videoFile (id, sourceId, metadataPath, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                        VALUES (@Id, @SourceId, @MetadataPath, @RelativePath, @FileName, 'movie', @MovieId, NULL, 0, 0)
                        """,
                        new
                        {
                            Id = Guid.NewGuid().ToString(),
                            SourceId = source.Id!.Value,
                            MetadataPath = file.ScannedFile.MetadataPath,
                            RelativePath = file.ScannedFile.RelativePath,
                            FileName = file.ScannedFile.FileName,
                            MovieId = group.EntityId
                        },
                        transaction,
                        cancellationToken: cancellationToken));
                newVideoFiles++;
            }
        }

        transaction.Commit();

        var shouldIndexExistingPlaceholder =
            group.ShouldAttemptAutomaticScrape &&
            newVideoFiles == 0 &&
            newMovies == 0 &&
            newTvShows == 0 &&
            group.EntityId < 0 &&
            await SyntheticPlaceholderNeedsMetadataAsync(connection, group, cancellationToken);

        var indexedItem = group.ShouldAttemptAutomaticScrape &&
                          (newVideoFiles > 0 || newMovies > 0 || newTvShows > 0 || shouldIndexExistingPlaceholder)
            ? new LibraryScanIndexedItem(group.EntityId, group.Title, group.MediaType, group.Files.Count)
            : null;
        return new PendingScanGroupCommitResult(newMovies, newTvShows, newVideoFiles, indexedItem);
    }

    private static async Task<bool> SyntheticPlaceholderNeedsMetadataAsync(
        System.Data.IDbConnection connection,
        PendingScanGroup group,
        CancellationToken cancellationToken)
    {
        var sql = group.IsTvShow
            ? """
              SELECT COUNT(*)
              FROM tvShow
              WHERE id = @Id
                AND isLocked = 0
                AND (firstAirDate IS NULL OR TRIM(firstAirDate) = '')
                AND (overview IS NULL OR TRIM(overview) = '')
                AND (posterPath IS NULL OR TRIM(posterPath) = '')
                AND voteAverage IS NULL
                AND (productionCountryCodes IS NULL OR TRIM(productionCountryCodes) = '')
                AND (originalLanguage IS NULL OR TRIM(originalLanguage) = '')
                AND (metadataLanguage IS NULL OR TRIM(metadataLanguage) = '')
              """
            : """
              SELECT COUNT(*)
              FROM movie
              WHERE id = @Id
                AND isLocked = 0
                AND (releaseDate IS NULL OR TRIM(releaseDate) = '')
                AND (overview IS NULL OR TRIM(overview) = '')
                AND (posterPath IS NULL OR TRIM(posterPath) = '')
                AND voteAverage IS NULL
                AND (productionCountryCodes IS NULL OR TRIM(productionCountryCodes) = '')
                AND (originalLanguage IS NULL OR TRIM(originalLanguage) = '')
                AND (metadataLanguage IS NULL OR TRIM(metadataLanguage) = '')
              """;
        var count = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                sql,
                new { Id = group.EntityId },
                cancellationToken: cancellationToken));
        return count > 0;
    }

    private async Task<bool> ShouldImportLocalMetadataAsync(MediaSource source, CancellationToken cancellationToken)
    {
        if (source.ProtocolKind != MediaSourceProtocol.Local ||
            localMetadataSidecarService is null ||
            settingsService is null)
        {
            return false;
        }

        var settings = await settingsService.LoadAsync(cancellationToken);
        return settings.LocalMetadata.EnableLocalMetadataImport;
    }

    private static async Task<bool> EnsureTvShowAsync(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        long tvShowId,
        string title,
        long? copyFromTvShowId,
        bool isLocked,
        string? overview,
        CancellationToken cancellationToken)
    {
        var existingShow = await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(
                "SELECT id FROM tvShow WHERE id = @Id LIMIT 1",
                new { Id = tvShowId },
                transaction,
                cancellationToken: cancellationToken));

        if (existingShow.HasValue)
        {
            return false;
        }

        if (copyFromTvShowId.HasValue && copyFromTvShowId.Value != tvShowId)
        {
            var copiedRows = await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO tvShow (
                        id,
                        title,
                        firstAirDate,
                        overview,
                        posterPath,
                        voteAverage,
                        isLocked,
                        productionCountryCodes,
                        originalLanguage,
                        metadataLanguage)
                    SELECT @Id,
                           @Title,
                           firstAirDate,
                           overview,
                           posterPath,
                           voteAverage,
                           isLocked,
                           productionCountryCodes,
                           originalLanguage,
                           metadataLanguage
                    FROM tvShow
                    WHERE id = @CopyFromId
                    LIMIT 1
                    """,
                    new
                    {
                        Id = tvShowId,
                        Title = title,
                        CopyFromId = copyFromTvShowId.Value
                    },
                    transaction,
                    cancellationToken: cancellationToken));

            if (copiedRows > 0)
            {
                return true;
            }
        }

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO tvShow (id, title, firstAirDate, overview, posterPath, voteAverage, isLocked)
                VALUES (@Id, @Title, NULL, @Overview, NULL, NULL, @IsLocked)
                """,
                new
                {
                    Id = tvShowId,
                    Title = title,
                    Overview = NormalizeText(overview),
                    IsLocked = isLocked ? 1 : 0
                },
                transaction,
                cancellationToken: cancellationToken));

        return true;
    }

    private static async Task UpdateUnenrichedMoviePlaceholderAsync(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        long movieId,
        string title,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE movie
                SET title = @Title,
                    releaseDate = NULL,
                    metadataLanguage = NULL
                WHERE id = @Id
                  AND isLocked = 0
                  AND (overview IS NULL OR TRIM(overview) = '')
                  AND (posterPath IS NULL OR TRIM(posterPath) = '')
                  AND voteAverage IS NULL
                  AND (productionCountryCodes IS NULL OR TRIM(productionCountryCodes) = '')
                  AND (originalLanguage IS NULL OR TRIM(originalLanguage) = '')
                  AND (title <> @Title OR releaseDate IS NOT NULL)
                """,
                new
                {
                    Id = movieId,
                    Title = title
                },
                transaction,
                cancellationToken: cancellationToken));
    }

    private static async Task UpdateExistingVideoFileAsync(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        ExistingVideoFileRecord existingFile,
        ScannedVideoFile scannedFile,
        string mediaType,
        long? movieId,
        long? episodeId,
        CancellationToken cancellationToken,
        LocalSidecarMetadata? localEpisodeMetadata = null)
    {
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE videoFile
                SET metadataPath = @MetadataPath,
                    relativePath = @RelativePath,
                    fileName = @FileName,
                    mediaType = @MediaType,
                    movieId = @MovieId,
                    episodeId = @EpisodeId,
                    customSeasonNumber = COALESCE(@CustomSeasonNumber, customSeasonNumber),
                    customEpisodeNumber = COALESCE(@CustomEpisodeNumber, customEpisodeNumber),
                    customEpisodeSubtitle = COALESCE(@CustomEpisodeSubtitle, customEpisodeSubtitle),
                    customEpisodeThumbnailPath = COALESCE(@CustomEpisodeThumbnailPath, customEpisodeThumbnailPath)
                WHERE id = @Id
                """,
                new
                {
                    Id = existingFile.Id,
                    MetadataPath = scannedFile.MetadataPath,
                    RelativePath = scannedFile.RelativePath,
                    FileName = scannedFile.FileName,
                    MediaType = mediaType,
                    MovieId = movieId,
                    EpisodeId = episodeId,
                    CustomSeasonNumber = localEpisodeMetadata?.SeasonNumber,
                    CustomEpisodeNumber = localEpisodeMetadata?.EpisodeNumber,
                    CustomEpisodeSubtitle = NormalizeText(localEpisodeMetadata?.Title),
                    CustomEpisodeThumbnailPath = NormalizeText(localEpisodeMetadata?.ThumbnailPath)
                },
                transaction,
                cancellationToken: cancellationToken));

        existingFile.RelativePath = scannedFile.RelativePath;
        existingFile.MovieId = movieId;
        existingFile.EpisodeId = episodeId;
    }

    private static async Task ApplyLocalMovieMetadataAsync(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        long movieId,
        LocalSidecarMetadata metadata,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE movie
                SET title = COALESCE(@Title, title),
                    releaseDate = COALESCE(@ReleaseDate, releaseDate),
                    overview = COALESCE(@Overview, overview),
                    posterPath = COALESCE(@PosterPath, posterPath),
                    voteAverage = COALESCE(@VoteAverage, voteAverage),
                    isLocked = 1,
                    metadataLanguage = 'local'
                WHERE id = @Id
                """,
                new
                {
                    Id = movieId,
                    Title = NormalizeText(metadata.Title),
                    ReleaseDate = NormalizeText(metadata.Date),
                    Overview = NormalizeText(metadata.Overview),
                    PosterPath = NormalizeText(metadata.PosterPath),
                    VoteAverage = metadata.VoteAverage
                },
                transaction,
                cancellationToken: cancellationToken));
    }

    private static async Task ApplyLocalTvShowMetadataAsync(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        long tvShowId,
        LocalSidecarMetadata metadata,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE tvShow
                SET title = COALESCE(@Title, title),
                    firstAirDate = COALESCE(@FirstAirDate, firstAirDate),
                    overview = COALESCE(@Overview, overview),
                    posterPath = COALESCE(@PosterPath, posterPath),
                    voteAverage = COALESCE(@VoteAverage, voteAverage),
                    isLocked = 1,
                    metadataLanguage = 'local'
                WHERE id = @Id
                """,
                new
                {
                    Id = tvShowId,
                    Title = NormalizeText(metadata.Title),
                    FirstAirDate = NormalizeText(metadata.Date),
                    Overview = NormalizeText(metadata.Overview),
                    PosterPath = NormalizeText(metadata.PosterPath),
                    VoteAverage = metadata.VoteAverage
                },
                transaction,
                cancellationToken: cancellationToken));
    }

    private static IReadOnlyList<ScannedVideoFile> EnumerateLocalVideoFiles(
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };

        List<ScannedVideoFile> scannedFiles = [];

        foreach (var absolutePath in Directory.EnumerateFiles(sourceRoot, "*.*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!VideoExtensions.Contains(Path.GetExtension(absolutePath)))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(absolutePath);
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(sourceRoot, fullPath));
            var scannedFile = new ScannedVideoFile(
                fullPath,
                relativePath,
                Path.GetFileName(fullPath),
                TryGetFileSize(fullPath));
            scannedFiles.Add(scannedFile);
        }

        return ResolvePrimaryVideoFiles(scannedFiles);
    }

    private static IEnumerable<ScannedVideoFile> SelectPrimaryBdmvStreams(IReadOnlyList<ScannedVideoFile> files)
    {
        if (files.Count == 0)
        {
            yield break;
        }

        var candidates = files
            .Select(static file => new MediaNameParser.BluRayStreamCandidate(
                file.FileName,
                file.FileSize,
                Duration: 0))
            .ToList();
        var selectedIndexes = MediaNameParser.SelectedBluRayStreamIndices(
            candidates,
            includeExtras: false);
        if (selectedIndexes.Count == 0)
        {
            yield break;
        }

        foreach (var index in selectedIndexes)
        {
            if (index >= 0 && index < files.Count)
            {
                yield return files[index];
            }
        }
    }

    private static async Task<Dictionary<string, ExistingVideoFileRecord>> NormalizeExistingFilesAsync(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        IReadOnlyList<ExistingVideoFileRecord> existingFiles,
        CancellationToken cancellationToken)
    {
        Dictionary<string, ExistingVideoFileRecord> normalizedFiles = new(StringComparer.OrdinalIgnoreCase);

        foreach (var group in existingFiles.GroupBy(static file => NormalizeRelativePath(file.RelativePath), StringComparer.OrdinalIgnoreCase))
        {
            var retainedFile = group.First();
            var groupedIds = group.Select(static file => file.Id).ToArray();
            var preferredId = await connection.ExecuteScalarAsync<string>(
                new CommandDefinition(
                    """
                    SELECT id
                    FROM videoFile
                    WHERE id IN @Ids
                    ORDER BY CASE WHEN duration > 0 THEN 1 ELSE 0 END DESC,
                             duration DESC,
                             playProgress DESC,
                             id COLLATE NOCASE ASC
                    LIMIT 1
                    """,
                    new { Ids = groupedIds },
                    transaction,
                    cancellationToken: cancellationToken)) ?? retainedFile.Id;

            if (!string.Equals(retainedFile.RelativePath, group.Key, StringComparison.Ordinal) ||
                !string.Equals(retainedFile.Id, preferredId, StringComparison.Ordinal))
            {
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE videoFile
                        SET relativePath = @RelativePath,
                            movieId = (SELECT movieId FROM videoFile WHERE id = @PreferredId),
                            episodeId = (SELECT episodeId FROM videoFile WHERE id = @PreferredId),
                            playProgress = (SELECT playProgress FROM videoFile WHERE id = @PreferredId),
                            duration = (SELECT duration FROM videoFile WHERE id = @PreferredId)
                        WHERE id = @Id
                        """,
                        new
                        {
                            Id = retainedFile.Id,
                            RelativePath = group.Key,
                            PreferredId = preferredId
                        },
                        transaction,
                        cancellationToken: cancellationToken));

                retainedFile.RelativePath = group.Key;
            }

            normalizedFiles[group.Key] = retainedFile;

            foreach (var duplicateFile in group)
            {
                if (duplicateFile.Id == retainedFile.Id)
                {
                    continue;
                }

                await connection.ExecuteAsync(
                    new CommandDefinition(
                        "DELETE FROM videoFile WHERE id = @Id",
                        new { duplicateFile.Id },
                        transaction,
                        cancellationToken: cancellationToken));
            }
        }

        return normalizedFiles;
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        var normalized = relativePath
            .Replace('\\', '/')
            .Replace(Path.AltDirectorySeparatorChar, '/')
            .Replace(Path.DirectorySeparatorChar, '/');

        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized.TrimStart('/');
    }

    private static string? NormalizeText(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static bool TryGetBdmvGroupKey(string relativePath, out string groupKey)
    {
        var normalized = NormalizeRelativePath(relativePath);
        const string rootMarker = "BDMV/STREAM/";
        if (normalized.StartsWith(rootMarker, StringComparison.OrdinalIgnoreCase))
        {
            groupKey = string.Empty;
            return true;
        }

        const string nestedMarker = "/BDMV/STREAM/";
        var index = normalized.IndexOf(nestedMarker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            groupKey = string.Empty;
            return false;
        }

        groupKey = normalized[..index];
        return true;
    }

    private static long TryGetFileSize(string absolutePath)
    {
        try
        {
            return new FileInfo(absolutePath).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static string? ResolveMovieTitle(
        string metadataPath,
        string relativePath,
        string fileName,
        MediaNameParser.CombinedSearchMetadataResult metadata)
    {
        if (TryGetBdmvGroupKey(relativePath, out var bdmvGroupKey) &&
            string.IsNullOrWhiteSpace(bdmvGroupKey))
        {
            var pathMetadata = MediaNameParser.CombinedSearchMetadata(metadataPath, fileName);
            return MediaNameParser.ExtractedDisplayTitle(metadataPath, fileName)
                   ?? UsableTitle(pathMetadata.ChineseTitle)
                   ?? UsableTitle(pathMetadata.ParentChineseTitle)
                   ?? UsableTitle(pathMetadata.ForeignTitle)
                   ?? UsableTitle(pathMetadata.FullCleanTitle);
        }

        return MediaNameParser.ExtractedDisplayTitle(relativePath, fileName)
               ?? UsableTitle(metadata.ChineseTitle)
               ?? UsableTitle(metadata.ParentChineseTitle)
               ?? UsableTitle(metadata.ForeignTitle)
               ?? UsableTitle(metadata.FullCleanTitle);
    }

    private static string? ResolveShowTitle(
        string absolutePath,
        string relativePath,
        string fileName,
        MediaSourceProtocol? sourceProtocol,
        MediaNameParser.CombinedSearchMetadataResult metadata)
    {
        if (sourceProtocol is MediaSourceProtocol.Plex or MediaSourceProtocol.Emby or MediaSourceProtocol.Jellyfin)
        {
            if (!MediaSourcePathResolver.IsMediaServerPlaybackEndpointPath(absolutePath))
            {
                var pathMetadata = MediaNameParser.CombinedSearchMetadata(absolutePath, fileName);
                return ResolveShowTitleFromRelativePath(absolutePath, sourceProtocol: null)
                       ?? MediaNameParser.ExtractedDisplayTitle(absolutePath, fileName)
                       ?? UsableTitle(pathMetadata.ChineseTitle)
                       ?? UsableTitle(pathMetadata.ParentChineseTitle)
                       ?? UsableTitle(pathMetadata.ForeignTitle)
                       ?? UsableTitle(pathMetadata.FullCleanTitle)
                       ?? UsableTitle(metadata.ChineseTitle)
                       ?? UsableTitle(metadata.ParentChineseTitle)
                       ?? UsableTitle(metadata.ForeignTitle)
                       ?? UsableTitle(metadata.FullCleanTitle);
            }

            return UsableTitle(metadata.ChineseTitle)
                   ?? UsableTitle(metadata.ParentChineseTitle)
                   ?? UsableTitle(metadata.ForeignTitle)
                   ?? UsableTitle(metadata.FullCleanTitle);
        }

        return ResolveShowTitleFromRelativePath(relativePath, sourceProtocol)
               ?? MediaNameParser.ExtractedDisplayTitle(relativePath, fileName)
               ?? UsableTitle(metadata.ChineseTitle)
               ?? UsableTitle(metadata.ParentChineseTitle)
               ?? UsableTitle(metadata.ForeignTitle)
               ?? UsableTitle(metadata.FullCleanTitle);
    }

    private static string? ResolveShowTitleFromRelativePath(string relativePath, MediaSourceProtocol? sourceProtocol)
    {
        if (sourceProtocol is MediaSourceProtocol.Plex or MediaSourceProtocol.Emby or MediaSourceProtocol.Jellyfin)
        {
            return null;
        }

        var normalized = NormalizeRelativePath(relativePath);
        var parts = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < 2)
        {
            return null;
        }

        var parentFolder = parts[^2];
        if (IsSeasonFolderName(parentFolder))
        {
            return parts.Length >= 3
                ? CleanShowFolderTitle(parts[^3])
                : null;
        }

        return CleanShowFolderTitle(parentFolder);
    }

    private static string ResolveFallbackDisplayTitle(string relativePath, string fileName, bool isTv)
    {
        var normalizedPath = NormalizeRelativePath(relativePath).Trim();
        var parts = normalizedPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var bdmvIndex = Array.FindIndex(parts, static part => part.Equals("BDMV", StringComparison.OrdinalIgnoreCase));
        if (bdmvIndex > 0)
        {
            return SanitizeFallbackTitle(parts[bdmvIndex - 1], fileName);
        }

        var streamIndex = Array.FindIndex(parts, static part => part.Equals("STREAM", StringComparison.OrdinalIgnoreCase));
        if (streamIndex > 1)
        {
            return SanitizeFallbackTitle(parts[streamIndex - 2], fileName);
        }

        if (isTv)
        {
            var showFolderTitle = ResolveFallbackShowFolderTitle(parts);
            if (!string.IsNullOrWhiteSpace(showFolderTitle))
            {
                return showFolderTitle;
            }
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        return SanitizeFallbackTitle(stem, string.IsNullOrWhiteSpace(normalizedPath) ? fileName : normalizedPath);
    }

    private static string? ResolveFallbackShowFolderTitle(IReadOnlyList<string> pathParts)
    {
        if (pathParts.Count < 2)
        {
            return null;
        }

        var parent = pathParts[^2];
        if (IsSeasonFolderName(parent) && pathParts.Count >= 3)
        {
            return SanitizeFallbackTitle(pathParts[^3], parent);
        }

        return SanitizeFallbackTitle(parent, pathParts[^1]);
    }

    private static string SanitizeFallbackTitle(string? value, string fallback)
    {
        var cleaned = Regex.Replace(value ?? string.Empty, @"[._]+", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        if (!string.IsNullOrWhiteSpace(cleaned))
        {
            return cleaned;
        }

        var fallbackStem = Path.GetFileNameWithoutExtension(fallback);
        if (string.IsNullOrWhiteSpace(fallbackStem))
        {
            fallbackStem = fallback;
        }

        fallbackStem = Regex.Replace(fallbackStem ?? string.Empty, @"[._]+", " ");
        fallbackStem = Regex.Replace(fallbackStem, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(fallbackStem) ? "未识别视频" : fallbackStem;
    }

    private static string? CleanShowFolderTitle(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return null;
        }

        var trimmed = folderName.Trim();
        var metadata = MediaNameParser.ExtractSearchMetadata(trimmed);
        return UsableTitle(ExtractPrimaryCjkTitleSegment(trimmed))
               ?? UsableTitle(ExtractPrimaryCjkTitleSegment(metadata.FullCleanTitle))
               ?? UsableTitle(metadata.ChineseTitle)
               ?? UsableTitle(metadata.ForeignTitle)
               ?? UsableTitle(metadata.FullCleanTitle)
               ?? UsableTitle(trimmed);
    }

    private static string? UsableTitle(string? title)
    {
        return MediaNameParser.IsUsableLibraryDisplayTitle(title) ? title!.Trim() : null;
    }

    private static string? ExtractPrimaryCjkTitleSegment(string? title)
    {
        var trimmed = title?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        var match = Regex.Match(trimmed, @"^\s*([\p{IsCJKUnifiedIdeographs}\d]+)(?:\s+(\d{2,4}))?");
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups[1].Value;
        if (match.Groups[2].Success)
        {
            value = $"{value} {match.Groups[2].Value}";
        }

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool IsSeasonFolderName(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return false;
        }

        var trimmed = folderName.Trim();
        var normalized = Regex.Replace(
            trimmed.Replace('_', ' ').Replace('-', ' ').Replace('.', ' '),
            @"\s+",
            " ").Trim();

        if (normalized.Equals("sp", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("special", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("specials", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("特别季", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("特别篇", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Regex.IsMatch(normalized, @"(?i)^s\d{1,2}$") ||
               Regex.IsMatch(normalized, @"(?i)^season\s*\d{1,2}$") ||
               Regex.IsMatch(normalized, @"^第\s*\d{1,2}\s*季$");
    }

    private static string GetMovieGroupingKey(string relativePath, string fallbackTitle)
    {
        var normalized = NormalizeRelativePath(relativePath);
        if (TryGetBdmvGroupKey(normalized, out var bdmvGroupKey))
        {
            return NormalizeExplicitMultipartMovieGroupingKey(
                NormalizeBluRayMovieGroupingKey(bdmvGroupKey, fallbackTitle),
                fallbackTitle);
        }

        if (MediaNameParser.MultipartMovieGroupingTitle(normalized, fallbackTitle) is { } multipartTitle)
        {
            return $"multipart:{multipartTitle}";
        }

        return normalized;
    }

    private static string NormalizeExplicitMultipartMovieGroupingKey(string groupingKey, string fallbackTitle)
    {
        if (MediaNameParser.MultipartMovieGroupingTitle(groupingKey, fallbackTitle) is { } multipartTitle)
        {
            return $"multipart:{multipartTitle}";
        }

        return groupingKey;
    }

    private static string NormalizeBluRayMovieGroupingKey(string bdmvGroupKey, string fallbackTitle)
    {
        var parts = NormalizeRelativePath(bdmvGroupKey)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        while (parts.Count > 0 && IsGenericDiscOrVolumeFolder(parts[^1]))
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return parts.Count == 0 ? fallbackTitle : string.Join('/', parts);
    }

    private static bool IsGenericDiscOrVolumeFolder(string input)
    {
        var token = Regex.Replace(input.Trim(), @"[._\-\s]+", string.Empty).ToLowerInvariant();
        return Regex.IsMatch(
            token,
            @"^(vol(ume)?\d{0,2}|disc\d{0,2}|disk\d{0,2}|dvd\d{0,2}|cd\d{0,2}|bdrom|bdmv)$");
    }

    private static long CreateSyntheticEntityId(string category, long sourceId, string key)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{category}:{sourceId}:{key.ToLowerInvariant()}"));
        var value = BitConverter.ToInt64(hash, 0);
        if (value == long.MinValue)
        {
            value = long.MaxValue;
        }

        var positive = Math.Abs(value);
        return -positive;
    }

    private static string BuildOmniPlayDockerLocalVideoFileId(long sourceId, string remoteFileId)
    {
        return $"omniplay-docker:{sourceId}:{Uri.EscapeDataString(remoteFileId)}";
    }

    private async Task<OmniPlayDockerImportedAssets> ImportOmniPlayDockerAssetsAsync(
        MediaSource source,
        IReadOnlyList<OmniPlayDockerLibraryDetail> details,
        CancellationToken cancellationToken)
    {
        if (omniPlayDockerClient is null || storagePaths is null || source.Id is null)
        {
            return new OmniPlayDockerImportedAssets(
                new Dictionary<string, string>(),
                new Dictionary<string, string>());
        }

        storagePaths.EnsureCreated();
        Dictionary<string, string> posters = new(StringComparer.Ordinal);
        Dictionary<string, string> thumbnails = new(StringComparer.OrdinalIgnoreCase);

        foreach (var detail in details)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(detail.PosterAssetId))
            {
                var destination = BuildOmniPlayDockerAssetPath(
                    storagePaths.PostersDirectory,
                    source.Id.Value,
                    "poster",
                    detail.PosterAssetId);
                if (await TryImportOmniPlayDockerAssetAsync(
                        destination,
                        token => omniPlayDockerClient.GetPosterDataAsync(source, detail.PosterAssetId, token),
                        cancellationToken))
                {
                    posters[detail.Id] = destination;
                }
            }

            foreach (var episode in detail.Seasons.SelectMany(static season => season.Episodes))
            {
                var episodeFiles = GetDockerEpisodeFiles(episode);
                if (episodeFiles.Count == 0 || string.IsNullOrWhiteSpace(episode.StillAssetId))
                {
                    continue;
                }

                var destination = BuildOmniPlayDockerAssetPath(
                    storagePaths.ThumbnailsDirectory,
                    source.Id.Value,
                    "thumbnail",
                    episode.StillAssetId);
                if (await TryImportOmniPlayDockerAssetAsync(
                        destination,
                        token => omniPlayDockerClient.GetThumbnailDataAsync(source, episode.StillAssetId, token),
                        cancellationToken))
                {
                    foreach (var file in episodeFiles)
                    {
                        thumbnails[file.Id] = destination;
                    }
                }
            }
        }

        return new OmniPlayDockerImportedAssets(posters, thumbnails);
    }

    private static async Task<bool> TryImportOmniPlayDockerAssetAsync(
        string destination,
        Func<CancellationToken, Task<byte[]>> downloadAsync,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destination) && new FileInfo(destination).Length > 0)
        {
            return true;
        }

        try
        {
            var data = await downloadAsync(cancellationToken);
            if (data.Length == 0)
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(destination, data, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string BuildOmniPlayDockerAssetPath(
        string directory,
        long sourceId,
        string assetKind,
        string assetId)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{sourceId}:{assetKind}:{assetId.Trim()}"))).ToLowerInvariant();
        return Path.Combine(directory, $"omniplay-docker-{hash}.jpg");
    }

    private static DoubanMetadata? BuildDoubanMetadata(OmniPlayDockerLibraryDetail detail, string itemKind, long entityId)
    {
        if (detail.Douban is { } douban)
        {
            return new DoubanMetadata(
                itemKind,
                entityId,
                douban.SubjectId,
                douban.SubjectUrl,
                douban.Title,
                douban.OriginalTitle,
                douban.Year,
                douban.Rating,
                douban.RatingCount,
                douban.Summary,
                douban.PosterUrl,
                douban.FetchedAt);
        }

        if (!detail.DoubanRating.HasValue)
        {
            return null;
        }

        return new DoubanMetadata(
            itemKind,
            entityId,
            $"docker-{detail.Id}",
            string.Empty,
            detail.Title,
            null,
            NormalizeYear(detail.ReleaseDate),
            detail.DoubanRating,
            null,
            null,
            null,
            DateTimeOffset.UtcNow);
    }

    private static async Task SaveImportedDoubanMetadataAsync(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        DoubanMetadata metadata,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO doubanMetadata (
                    itemKind, entityId, subjectId, subjectUrl, title, originalTitle, year,
                    rating, ratingCount, summary, posterUrl, fetchedAt)
                VALUES (
                    @ItemKind, @EntityId, @SubjectId, @SubjectUrl, @Title, @OriginalTitle, @Year,
                    @Rating, @RatingCount, @Summary, @PosterUrl, @FetchedAt)
                ON CONFLICT(itemKind, entityId) DO UPDATE SET
                    subjectId = excluded.subjectId,
                    subjectUrl = excluded.subjectUrl,
                    title = excluded.title,
                    originalTitle = excluded.originalTitle,
                    year = excluded.year,
                    rating = excluded.rating,
                    ratingCount = excluded.ratingCount,
                    summary = excluded.summary,
                    posterUrl = excluded.posterUrl,
                    fetchedAt = excluded.fetchedAt
                """,
                new
                {
                    metadata.ItemKind,
                    metadata.EntityId,
                    metadata.SubjectId,
                    metadata.SubjectUrl,
                    metadata.Title,
                    metadata.OriginalTitle,
                    metadata.Year,
                    metadata.Rating,
                    metadata.RatingCount,
                    metadata.Summary,
                    metadata.PosterUrl,
                    FetchedAt = metadata.FetchedAt.ToString("O")
                },
                transaction,
                cancellationToken: cancellationToken));
    }

    private static string? NormalizeYear(string? value)
    {
        var match = Regex.Match(value ?? string.Empty, "\\d{4}");
        return match.Success ? match.Value : null;
    }

    private static IReadOnlyList<OmniPlayDockerVideoFile> FlattenDockerFiles(OmniPlayDockerLibraryDetail detail)
    {
        var files = detail.VideoFiles.ToList();
        files.AddRange(detail.Seasons
            .SelectMany(season => season.Episodes)
            .SelectMany(GetDockerEpisodeFiles));
        return files
            .GroupBy(static file => file.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
    }

    private static IReadOnlyList<OmniPlayDockerVideoFile> GetDockerEpisodeFiles(OmniPlayDockerEpisode episode)
    {
        if (episode.VideoFiles is { Count: > 0 })
        {
            return episode.VideoFiles;
        }

        return episode.VideoFile is { } file ? [file] : [];
    }

    private static string ResolveOmniPlayDockerDisplayFileName(OmniPlayDockerVideoFile file)
    {
        var fileName = file.FileName.Trim();
        if (!string.IsNullOrWhiteSpace(fileName) &&
            !MediaSourcePathResolver.IsMediaServerPlaybackEndpointPath(fileName))
        {
            return fileName;
        }

        var relative = file.RelativePath.Trim();
        if (!string.IsNullOrWhiteSpace(relative) &&
            !MediaSourcePathResolver.IsMediaServerPlaybackEndpointPath(relative))
        {
            return Path.GetFileName(relative.Replace('\\', '/')) is { Length: > 0 } name ? name : relative;
        }

        return string.IsNullOrWhiteSpace(fileName) ? file.Id : fileName;
    }

    private sealed class ExistingVideoFileRecord
    {
        public string Id { get; init; } = string.Empty;

        public string RelativePath { get; set; } = string.Empty;

        public long? MovieId { get; set; }

        public long? EpisodeId { get; set; }

        public double PlayProgress { get; set; }

        public double Duration { get; set; }
    }

    private sealed class PendingScanGroup
    {
        public PendingScanGroup(
            string mediaType,
            long entityId,
            string title,
            LocalSidecarMetadata? metadata,
            bool shouldAttemptAutomaticScrape = true,
            long? copyFromTvShowId = null)
        {
            MediaType = mediaType;
            EntityId = entityId;
            Title = title;
            Metadata = metadata;
            ShouldAttemptAutomaticScrape = shouldAttemptAutomaticScrape;
            CopyFromTvShowId = copyFromTvShowId;
        }

        public string MediaType { get; }

        public long EntityId { get; }

        public string Title { get; }

        public LocalSidecarMetadata? Metadata { get; set; }

        public bool ShouldAttemptAutomaticScrape { get; }

        public long? CopyFromTvShowId { get; set; }

        public List<PendingScanFile> Files { get; } = [];

        public bool IsTvShow => string.Equals(MediaType, "tv", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record PendingScanFile(
        ScannedVideoFile ScannedFile,
        ExistingVideoFileRecord? ExistingFile,
        LocalSidecarMetadata? EpisodeMetadata);

    private sealed record DeferredPendingScanGroup(MediaSource Source, PendingScanGroup Group);

    private sealed record PendingScanGroupCommitResult(
        int NewMovieCount,
        int NewTvShowCount,
        int NewVideoFileCount,
        LibraryScanIndexedItem? IndexedItem);

    private sealed record OmniPlayDockerImportedAssets(
        IReadOnlyDictionary<string, string> Posters,
        IReadOnlyDictionary<string, string> Thumbnails);

    private static IReadOnlyList<ScannedVideoFile> ResolvePrimaryVideoFiles(IReadOnlyList<ScannedVideoFile> scannedFiles)
    {
        List<ScannedVideoFile> normalFiles = [];
        Dictionary<string, List<ScannedVideoFile>> bdmvGroups = new(StringComparer.OrdinalIgnoreCase);

        foreach (var scannedFile in scannedFiles)
        {
            if (TryGetBdmvGroupKey(scannedFile.RelativePath, out var bdmvGroupKey))
            {
                if (!bdmvGroups.TryGetValue(bdmvGroupKey, out var group))
                {
                    group = [];
                    bdmvGroups[bdmvGroupKey] = group;
                }

                group.Add(scannedFile);
                continue;
            }

            normalFiles.Add(scannedFile);
        }

        Dictionary<string, ScannedVideoFile> resolvedFiles = new(StringComparer.OrdinalIgnoreCase);

        foreach (var file in normalFiles)
        {
            resolvedFiles[file.RelativePath] = file;
        }

        foreach (var group in bdmvGroups.Values)
        {
            foreach (var file in SelectPrimaryBdmvStreams(group))
            {
                resolvedFiles[file.RelativePath] = file;
            }
        }

        return resolvedFiles.Values
            .OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static LibraryScanSummary CreateDiagnosticSummary(MediaSource source, string diagnostic)
    {
        return new LibraryScanSummary(1, 0, 0, 0, 0, [diagnostic]);
    }

    private static string ResolveSourceDisplayName(MediaSource source)
    {
        return string.IsNullOrWhiteSpace(source.Name)
            ? source.BaseUrl
            : source.Name;
    }

    private sealed record ScannedVideoFile(
        string MetadataPath,
        string RelativePath,
        string FileName,
        long FileSize,
        string? MediaType = null);

}
