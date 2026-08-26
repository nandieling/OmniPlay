using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml;
using Microsoft.Data.Sqlite;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Data;

namespace OmniPlay.Infrastructure.Library;

public sealed class LibraryScanner : ILibraryScanner
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m2ts", ".m2t", ".iso", ".ts",
        ".rmvb", ".flv", ".webm", ".m4v"
    };

    private readonly SqliteDatabase database;
    private readonly IMediaProbeService mediaProbeService;
    private readonly IWebDavFileEnumerator webDavFileEnumerator;

    public LibraryScanner(SqliteDatabase database)
        : this(database, NoOpMediaProbeService.Instance, NoOpWebDavFileEnumerator.Instance)
    {
    }

    public LibraryScanner(SqliteDatabase database, IMediaProbeService mediaProbeService)
        : this(database, mediaProbeService, NoOpWebDavFileEnumerator.Instance)
    {
    }

    public LibraryScanner(
        SqliteDatabase database,
        IMediaProbeService mediaProbeService,
        IWebDavFileEnumerator webDavFileEnumerator)
    {
        this.database = database;
        this.mediaProbeService = mediaProbeService ?? NoOpMediaProbeService.Instance;
        this.webDavFileEnumerator = webDavFileEnumerator;
    }

    public async Task<LibraryScanSummary> ScanAllAsync(CancellationToken cancellationToken = default)
    {
        return await ScanAllAsync(null, hideNewItemsUntilScraped: false, cancellationToken);
    }

    public async Task<LibraryScanSummary> ScanAllAsync(
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        return await ScanAllAsync(progress, hideNewItemsUntilScraped: false, cancellationToken);
    }

    public async Task<LibraryScanSummary> ScanAllAsync(
        IProgress<LibraryScanProgress>? progress,
        bool hideNewItemsUntilScraped,
        CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        var sources = await GetEnabledSourcesAsync(connection, cancellationToken);
        return await ScanSourcesAsync(connection, sources, progress, hideNewItemsUntilScraped, cancellationToken);
    }

    public async Task<LibraryScanSummary> ScanSourceAsync(
        long sourceId,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        return await ScanSourceAsync(sourceId, progress, hideNewItemsUntilScraped: false, cancellationToken);
    }

    public async Task<LibraryScanSummary> ScanSourceAsync(
        long sourceId,
        IProgress<LibraryScanProgress>? progress,
        bool hideNewItemsUntilScraped,
        CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        var sources = await GetEnabledSourcesAsync(connection, sourceId, cancellationToken);
        return await ScanSourcesAsync(connection, sources, progress, hideNewItemsUntilScraped, cancellationToken);
    }

    private async Task<LibraryScanSummary> ScanSourcesAsync(
        SqliteConnection connection,
        IReadOnlyList<MediaSource> sources,
        IProgress<LibraryScanProgress>? progress,
        bool hideNewItemsUntilScraped,
        CancellationToken cancellationToken)
    {
        var newMovies = 0;
        var newTvShows = 0;
        var newVideoFiles = 0;
        var removedVideoFiles = 0;
        List<string> diagnostics = [];

        ReportProgress(progress, "starting", sources.Count, 0, null, 0, 0, 0, 0, null);
        var completedSourceCount = 0;
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var summary = await ScanSourceAsync(
                connection,
                source,
                sources.Count,
                completedSourceCount,
                progress,
                hideNewItemsUntilScraped,
                cancellationToken);
            newMovies += summary.NewMovieCount;
            newTvShows += summary.NewTvShowCount;
            newVideoFiles += summary.NewVideoFileCount;
            removedVideoFiles += summary.RemovedVideoFileCount;
            diagnostics.AddRange(summary.Diagnostics);
            completedSourceCount++;
            ReportProgress(progress, "source-completed", sources.Count, completedSourceCount, source.Name, 0, 0, 0, 0, null);
        }

        return new LibraryScanSummary(
            sources.Count,
            newMovies,
            newVideoFiles,
            removedVideoFiles,
            newTvShows,
            diagnostics);
    }

    private async Task<LibraryScanSummary> ScanSourceAsync(
        SqliteConnection connection,
        MediaSource source,
        int sourceCount,
        int completedSourceCount,
        IProgress<LibraryScanProgress>? progress,
        bool hideNewItemsUntilScraped,
        CancellationToken cancellationToken)
    {
        var shouldProbe = false;
        IReadOnlyList<ScannedVideoFile> scannedFiles;
        if (string.Equals(source.Kind, "local", StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(source.BaseUrl))
            {
                return new LibraryScanSummary(1, 0, 0, 0, 0, [$"已跳过媒体源“{source.Name}”：目录不存在。"]);
            }

            ReportProgress(
                progress,
                "discovering",
                sourceCount,
                completedSourceCount,
                source.Name,
                0,
                0,
                0,
                0,
                null);
            scannedFiles = EnumerateLocalVideoFiles(
                source.BaseUrl,
                progress,
                sourceCount,
                completedSourceCount,
                source.Name,
                cancellationToken);
            shouldProbe = true;
        }
        else if (string.Equals(source.Kind, "webdav", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                ReportProgress(
                    progress,
                    "discovering",
                    sourceCount,
                    completedSourceCount,
                    source.Name,
                    0,
                    0,
                    0,
                    0,
                    null);
                scannedFiles = await EnumerateWebDavVideoFilesAsync(source, cancellationToken);
            }
            catch (Exception ex) when (ex is ArgumentException
                                       or HttpRequestException
                                       or InvalidOperationException
                                       or UnauthorizedAccessException
                                       or XmlException)
            {
                return new LibraryScanSummary(
                    1,
                    0,
                    0,
                    0,
                    0,
                    [$"已跳过媒体源“{source.Name}”：WebDAV 扫描失败：{ex.Message}"]);
            }
        }
        else
        {
            return new LibraryScanSummary(1, 0, 0, 0, 0, [$"已跳过媒体源“{source.Name}”：暂不支持 {source.Kind} 扫描。"]);
        }

        List<string> diagnostics = [];
        var existing = await GetExistingVideoFilesAsync(connection, source.Id, cancellationToken);
        if (shouldProbe)
        {
            ReportProgress(
                progress,
                "probing",
                sourceCount,
                completedSourceCount,
                source.Name,
                scannedFiles.Count,
                0,
                0,
                0,
                null);
        }

        var probes = shouldProbe
            ? await ProbeScannedFilesAsync(
                scannedFiles,
                existing,
                diagnostics,
                sourceCount,
                completedSourceCount,
                source.Name,
                progress,
                cancellationToken)
            : new Dictionary<string, MediaProbeSnapshot>(StringComparer.OrdinalIgnoreCase);
        var newMovies = 0;
        var newTvShows = 0;
        var newVideoFiles = 0;
        var removedVideoFiles = 0;
        var now = DateTimeOffset.UtcNow.ToString("O");

        using var transaction = connection.BeginTransaction();
        var scannedRelativePaths = scannedFiles
            .Select(static file => file.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var episodeSubtitleByPath = ResolveDuplicateEpisodeSubtitles(scannedFiles);

        foreach (var file in existing.Values.Where(file => !scannedRelativePaths.Contains(file.RelativePath)))
        {
            await MarkVideoMissingAsync(connection, transaction, file.Id, now, cancellationToken);
            removedVideoFiles++;
        }

        var processedVideoFiles = 0;
        foreach (var scannedFile in scannedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(
                progress,
                "indexing",
                sourceCount,
                completedSourceCount,
                source.Name,
                scannedFiles.Count,
                processedVideoFiles,
                probes.Count,
                probes.Count,
                scannedFile.RelativePath);

            var metadata = MediaNameParser.ExtractSearchMetadata(scannedFile.MetadataPath);
            var isTv = MediaNameParser.IsLikelyTvEpisodePath(scannedFile.RelativePath);
            var libraryItemId = isTv
                ? CreateTvLibraryItemId(ResolveShowTitle(scannedFile.RelativePath, metadata))
                : StableId.Create("movie", source.Id.ToString(), GetMovieGroupingKey(scannedFile.RelativePath, metadata));
            var itemKind = isTv ? "tv" : "movie";
            var title = isTv
                ? ResolveShowTitle(scannedFile.RelativePath, metadata)
                : ResolveMovieTitle(scannedFile.MetadataPath, metadata);

            if (await UpsertLibraryItemAsync(
                    connection,
                    transaction,
                    libraryItemId,
                    itemKind,
                    title,
                    metadata.Year,
                    now,
                    isVisible: !hideNewItemsUntilScraped,
                    cancellationToken))
            {
                if (isTv)
                {
                    newTvShows++;
                }
                else
                {
                    newMovies++;
                }
            }

            string? episodeId = null;
            if (isTv)
            {
                episodeId = await UpsertEpisodeHierarchyAsync(
                    connection,
                    transaction,
                    libraryItemId,
                    scannedFile,
                    title,
                    episodeSubtitleByPath.TryGetValue(scannedFile.RelativePath, out var episodeSubtitle) ? episodeSubtitle : null,
                    now,
                    cancellationToken);
            }

            var videoFileId = StableId.Create("vf", source.Id.ToString(), scannedFile.RelativePath);
            if (!existing.ContainsKey(scannedFile.RelativePath))
            {
                newVideoFiles++;
            }

            await UpsertVideoFileAsync(
                connection,
                transaction,
                videoFileId,
                source.Id,
                libraryItemId,
                episodeId,
                scannedFile,
                itemKind,
                probes.TryGetValue(scannedFile.RelativePath, out var probe) ? probe : null,
                now,
                cancellationToken);

            processedVideoFiles++;
            ReportProgress(
                progress,
                "indexing",
                sourceCount,
                completedSourceCount,
                source.Name,
                scannedFiles.Count,
                processedVideoFiles,
                probes.Count,
                probes.Count,
                scannedFile.RelativePath);
        }

        await DeleteOrphanEpisodesAsync(connection, transaction, cancellationToken);

        await ExecuteAsync(connection, transaction, """
            UPDATE media_sources
            SET last_scanned_at = $now,
                updated_at = $now
            WHERE id = $sourceId;
            """,
            cancellationToken,
            ("$now", now),
            ("$sourceId", source.Id));
        transaction.Commit();
        return new LibraryScanSummary(1, newMovies, newVideoFiles, removedVideoFiles, newTvShows, diagnostics);
    }

    private async Task<Dictionary<string, MediaProbeSnapshot>> ProbeScannedFilesAsync(
        IReadOnlyList<ScannedVideoFile> scannedFiles,
        IReadOnlyDictionary<string, ExistingVideoFile> existing,
        List<string> diagnostics,
        int sourceCount,
        int completedSourceCount,
        string sourceName,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var probeCandidates = scannedFiles
            .Where(file => ShouldProbe(file, existing))
            .ToArray();
        ConcurrentDictionary<string, MediaProbeSnapshot> probes = new(StringComparer.OrdinalIgnoreCase);
        if (probeCandidates.Length == 0)
        {
            return probes.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.OrdinalIgnoreCase);
        }

        var probedVideoFiles = 0;
        await Parallel.ForEachAsync(
            probeCandidates,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = ResolveProbeConcurrency()
            },
            async (scannedFile, token) =>
            {
                ReportProgress(
                    progress,
                    "probing",
                    sourceCount,
                    completedSourceCount,
                    sourceName,
                    scannedFiles.Count,
                    0,
                    probeCandidates.Length,
                    Volatile.Read(ref probedVideoFiles),
                    scannedFile.RelativePath);

                try
                {
                    var probe = await mediaProbeService.ProbeAsync(scannedFile.MetadataPath, token);
                    if (probe is not null)
                    {
                        probes[scannedFile.RelativePath] = probe;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lock (diagnostics)
                    {
                        diagnostics.Add($"媒体探测失败：{scannedFile.RelativePath}（{ex.Message}）");
                    }
                }

                var completed = Interlocked.Increment(ref probedVideoFiles);
                ReportProgress(
                    progress,
                    "probing",
                    sourceCount,
                    completedSourceCount,
                    sourceName,
                    scannedFiles.Count,
                    0,
                    probeCandidates.Length,
                    completed,
                    scannedFile.RelativePath);
            });

        return probes.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<MediaSource>> GetEnabledSourcesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        return await GetEnabledSourcesAsync(connection, sourceId: null, cancellationToken);
    }

    private static async Task<IReadOnlyList<MediaSource>> GetEnabledSourcesAsync(
        SqliteConnection connection,
        long? sourceId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ms.id,
                   ms.name,
                   ms.kind,
                   ms.base_url,
                   c.username,
                   c.secret_json
            FROM media_sources ms
            LEFT JOIN media_source_credentials c ON c.id = ms.auth_reference
            WHERE ms.kind IN ('local', 'webdav')
              AND ms.is_enabled = 1
              AND ms.removed_at IS NULL
              AND ($sourceId IS NULL OR ms.id = $sourceId)
            ORDER BY ms.id ASC;
            """;
        command.Parameters.AddWithValue("$sourceId", sourceId.HasValue ? sourceId.Value : DBNull.Value);

        List<MediaSource> sources = [];
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sources.Add(new MediaSource(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : ReadWebDavPassword(reader.GetString(5))));
        }

        return sources;
    }

    private static IReadOnlyList<ScannedVideoFile> EnumerateLocalVideoFiles(
        string sourceRoot,
        IProgress<LibraryScanProgress>? progress,
        int sourceCount,
        int completedSourceCount,
        string sourceName,
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
            var relativePath = MediaNameParser.NormalizeRelativePath(Path.GetRelativePath(sourceRoot, fullPath));
            scannedFiles.Add(new ScannedVideoFile(
                fullPath,
                relativePath,
                Path.GetFileName(fullPath),
                TryGetFileSize(fullPath),
                TryGetModifiedAt(fullPath)));
            if (scannedFiles.Count % 100 == 0)
            {
                ReportProgress(
                    progress,
                    "discovering",
                    sourceCount,
                    completedSourceCount,
                    sourceName,
                    0,
                    scannedFiles.Count,
                    0,
                    0,
                    relativePath);
            }
        }

        ReportProgress(
            progress,
            "discovering",
            sourceCount,
            completedSourceCount,
            sourceName,
            scannedFiles.Count,
            scannedFiles.Count,
            0,
            0,
            scannedFiles.LastOrDefault()?.RelativePath);

        return ResolvePrimaryVideoFiles(scannedFiles);
    }

    private async Task<IReadOnlyList<ScannedVideoFile>> EnumerateWebDavVideoFilesAsync(
        MediaSource source,
        CancellationToken cancellationToken)
    {
        var remoteFiles = await webDavFileEnumerator.EnumerateFilesAsync(
            source.BaseUrl,
            source.Username,
            source.Password,
            cancellationToken);
        var scannedFiles = remoteFiles
            .Where(static file => VideoExtensions.Contains(Path.GetExtension(file.RelativePath)))
            .Select(static file =>
            {
                var relativePath = MediaNameParser.NormalizeRelativePath(file.RelativePath);
                return new ScannedVideoFile(
                    relativePath,
                    relativePath,
                    GetRemoteFileName(relativePath),
                    file.ContentLength,
                    file.LastModified);
            })
            .ToArray();

        return ResolvePrimaryVideoFiles(scannedFiles);
    }

    private static IReadOnlyList<ScannedVideoFile> ResolvePrimaryVideoFiles(IReadOnlyList<ScannedVideoFile> files)
    {
        var normalFiles = new List<ScannedVideoFile>();
        var bdmvGroups = new Dictionary<string, List<ScannedVideoFile>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            if (TryGetBdmvGroupKey(file.RelativePath, out var groupKey))
            {
                if (!bdmvGroups.TryGetValue(groupKey, out var group))
                {
                    group = [];
                    bdmvGroups[groupKey] = group;
                }

                group.Add(file);
            }
            else
            {
                normalFiles.Add(file);
            }
        }

        foreach (var group in bdmvGroups.Values)
        {
            normalFiles.AddRange(SelectPrimaryBdmvStreams(group));
        }

        return normalFiles
            .OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<ScannedVideoFile> SelectPrimaryBdmvStreams(IReadOnlyList<ScannedVideoFile> files)
    {
        if (files.Count == 0)
        {
            yield break;
        }

        var streamFiles = files
            .Where(static file => TryGetBdmvPathInfo(file.RelativePath, out _, out var section)
                                  && section == BdmvPathSection.Stream)
            .ToArray();
        if (streamFiles.Length == 0)
        {
            yield break;
        }

        var candidates = streamFiles
            .Select(static file => new MediaNameParser.BluRayStreamCandidate(
                file.FileName,
                file.FileSizeBytes ?? 0,
                Duration: 0))
            .ToArray();
        var selectedIndexes = MediaNameParser.SelectedBluRayStreamIndices(
            candidates,
            includeExtras: false);

        foreach (var index in selectedIndexes)
        {
            if (index >= 0 && index < streamFiles.Length)
            {
                yield return streamFiles[index];
            }
        }
    }

    private static bool TryGetBdmvGroupKey(string relativePath, out string groupKey)
    {
        return TryGetBdmvPathInfo(relativePath, out groupKey, out _);
    }

    private static bool TryGetBdmvPathInfo(
        string relativePath,
        out string groupKey,
        out BdmvPathSection section)
    {
        var normalized = MediaNameParser.NormalizeRelativePath(relativePath);
        foreach (var marker in BdmvPathMarkers)
        {
            var index = normalized.IndexOf(marker.NestedMarker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                groupKey = normalized[..index];
                section = marker.Section;
                return true;
            }

            if (normalized.StartsWith(marker.RootMarker, StringComparison.OrdinalIgnoreCase))
            {
                groupKey = string.Empty;
                section = marker.Section;
                return true;
            }
        }

        groupKey = string.Empty;
        section = BdmvPathSection.Unknown;
        return false;
    }

    private enum BdmvPathSection
    {
        Unknown,
        Stream,
        Playlist
    }

    private static readonly (string NestedMarker, string RootMarker, BdmvPathSection Section)[] BdmvPathMarkers =
    [
        ("/BDMV/STREAM/", "BDMV/STREAM/", BdmvPathSection.Stream),
        ("/BDMV/PLAYLIST/", "BDMV/PLAYLIST/", BdmvPathSection.Playlist)
    ];

    private static async Task<Dictionary<string, ExistingVideoFile>> GetExistingVideoFilesAsync(
        SqliteConnection connection,
        long sourceId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,
                   relative_path,
                   file_size_bytes,
                   modified_at,
                   probe_json
            FROM video_files
            WHERE source_id = $sourceId;
            """;
        command.Parameters.AddWithValue("$sourceId", sourceId);

        Dictionary<string, ExistingVideoFile> existing = new(StringComparer.OrdinalIgnoreCase);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var file = new ExistingVideoFile(
                reader.GetString(0),
                MediaNameParser.NormalizeRelativePath(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                !reader.IsDBNull(4) && !string.IsNullOrWhiteSpace(reader.GetString(4)));
            existing[file.RelativePath] = file;
        }

        return existing;
    }

    private static bool ShouldProbe(
        ScannedVideoFile scannedFile,
        IReadOnlyDictionary<string, ExistingVideoFile> existing)
    {
        if (!existing.TryGetValue(scannedFile.RelativePath, out var existingFile))
        {
            return true;
        }

        if (!existingFile.HasProbeJson)
        {
            return true;
        }

        return scannedFile.FileSizeBytes != existingFile.FileSizeBytes
               || scannedFile.ModifiedAt?.ToString("O") != existingFile.ModifiedAt;
    }

    private static async Task<bool> UpsertLibraryItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        string itemKind,
        string title,
        string? releaseYear,
        string now,
        bool isVisible,
        CancellationToken cancellationToken)
    {
        using var existsCommand = connection.CreateCommand();
        existsCommand.Transaction = transaction;
        existsCommand.CommandText = "SELECT 1 FROM library_items WHERE id = $id LIMIT 1;";
        existsCommand.Parameters.AddWithValue("$id", id);
        var isNew = await existsCommand.ExecuteScalarAsync(cancellationToken) is null;

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO library_items (
                id, item_kind, title, sort_title, release_date, overview, poster_asset_id, vote_average, is_locked, is_visible, created_at, updated_at)
            VALUES (
                $id, $itemKind, $title, $sortTitle, $releaseDate, NULL, NULL, NULL, 0, $isVisible, $now, $now)
            ON CONFLICT(id) DO UPDATE SET
                title = CASE WHEN library_items.is_locked = 1 THEN library_items.title ELSE excluded.title END,
                sort_title = CASE WHEN library_items.is_locked = 1 THEN library_items.sort_title ELSE excluded.sort_title END,
                release_date = CASE
                    WHEN library_items.release_date IS NULL THEN excluded.release_date
                    WHEN excluded.release_date IS NULL THEN library_items.release_date
                    WHEN excluded.release_date < library_items.release_date THEN excluded.release_date
                    ELSE library_items.release_date
                END,
                updated_at = excluded.updated_at;
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$itemKind", itemKind);
        insert.Parameters.AddWithValue("$title", title);
        insert.Parameters.AddWithValue("$sortTitle", NormalizeSortTitle(title));
        insert.Parameters.AddWithValue("$releaseDate", string.IsNullOrWhiteSpace(releaseYear) ? DBNull.Value : $"{releaseYear}-01-01");
        insert.Parameters.AddWithValue("$isVisible", isVisible ? 1 : 0);
        insert.Parameters.AddWithValue("$now", now);

        await insert.ExecuteNonQueryAsync(cancellationToken);
        return isNew;
    }

    private static async Task<string> UpsertEpisodeHierarchyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string libraryItemId,
        ScannedVideoFile scannedFile,
        string showTitle,
        string? episodeSubtitle,
        string now,
        CancellationToken cancellationToken)
    {
        var tvShowId = StableId.Create("show", libraryItemId);
        var episodeInfo = MediaNameParser.ParseEpisodeInfo(scannedFile.FileName, 0);
        var seasonId = StableId.Create("season", tvShowId, episodeInfo.Season.ToString());
        var episodeId = string.IsNullOrWhiteSpace(episodeSubtitle)
            ? StableId.Create("episode", seasonId, episodeInfo.Episode.ToString())
            : StableId.Create("episode", seasonId, episodeInfo.Episode.ToString(), episodeSubtitle);

        await ExecuteAsync(connection, transaction, """
            INSERT INTO tv_shows (id, library_item_id, tmdb_id, original_name, first_air_date)
            VALUES ($id, $libraryItemId, NULL, NULL, NULL)
            ON CONFLICT(id) DO UPDATE SET library_item_id = excluded.library_item_id;
            """,
            cancellationToken,
            ("$id", tvShowId),
            ("$libraryItemId", libraryItemId));

        await ExecuteAsync(connection, transaction, """
            INSERT INTO seasons (id, tv_show_id, season_number, title, poster_asset_id)
            VALUES ($id, $tvShowId, $seasonNumber, $title, NULL)
            ON CONFLICT(id) DO UPDATE SET title = excluded.title;
            """,
            cancellationToken,
            ("$id", seasonId),
            ("$tvShowId", tvShowId),
            ("$seasonNumber", episodeInfo.Season),
            ("$title", episodeInfo.Season == 0 ? "特别篇" : $"第 {episodeInfo.Season} 季"));

        await ExecuteAsync(connection, transaction, """
            INSERT INTO episodes (id, season_id, episode_number, title, overview, still_asset_id, air_date)
            VALUES ($id, $seasonId, $episodeNumber, $title, NULL, NULL, NULL)
            ON CONFLICT(id) DO UPDATE SET title = excluded.title;
            """,
            cancellationToken,
            ("$id", episodeId),
            ("$seasonId", seasonId),
            ("$episodeNumber", episodeInfo.Episode),
            ("$title", BuildLocalEpisodeTitle(showTitle, episodeInfo, episodeSubtitle)));

        return episodeId;
    }

    private static string BuildLocalEpisodeTitle(string showTitle, EpisodeInfo episodeInfo, string? episodeSubtitle)
    {
        return string.IsNullOrWhiteSpace(episodeSubtitle)
            ? $"{showTitle} {episodeInfo.DisplayName}"
            : $"{episodeInfo.DisplayName}·{episodeSubtitle.Trim()}";
    }

    private static async Task UpsertVideoFileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        long sourceId,
        string libraryItemId,
        string? episodeId,
        ScannedVideoFile scannedFile,
        string mediaKind,
        MediaProbeSnapshot? probe,
        string now,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            INSERT INTO video_files (
                id, source_id, library_item_id, episode_id, relative_path, file_name, file_size_bytes, modified_at,
                media_kind, duration_seconds, container, video_codec, audio_codec, subtitle_summary, probe_json,
                created_at, updated_at, missing_at)
            VALUES (
                $id, $sourceId, $libraryItemId, $episodeId, $relativePath, $fileName, $fileSizeBytes, $modifiedAt,
                $mediaKind, $durationSeconds, $container, $videoCodec, $audioCodec, $subtitleSummary, $probeJson,
                $now, $now, NULL)
            ON CONFLICT(id) DO UPDATE SET
                library_item_id = excluded.library_item_id,
                episode_id = excluded.episode_id,
                relative_path = excluded.relative_path,
                file_name = excluded.file_name,
                file_size_bytes = excluded.file_size_bytes,
                modified_at = excluded.modified_at,
                media_kind = excluded.media_kind,
                duration_seconds = CASE WHEN $hasProbe = 1 THEN excluded.duration_seconds ELSE video_files.duration_seconds END,
                container = CASE WHEN $hasProbe = 1 THEN excluded.container ELSE video_files.container END,
                video_codec = CASE WHEN $hasProbe = 1 THEN excluded.video_codec ELSE video_files.video_codec END,
                audio_codec = CASE WHEN $hasProbe = 1 THEN excluded.audio_codec ELSE video_files.audio_codec END,
                subtitle_summary = CASE WHEN $hasProbe = 1 THEN excluded.subtitle_summary ELSE video_files.subtitle_summary END,
                probe_json = CASE WHEN $hasProbe = 1 THEN excluded.probe_json ELSE video_files.probe_json END,
                updated_at = excluded.updated_at,
                missing_at = NULL;
            """,
            cancellationToken,
            ("$id", id),
            ("$sourceId", sourceId),
            ("$libraryItemId", libraryItemId),
            ("$episodeId", episodeId),
            ("$relativePath", scannedFile.RelativePath),
            ("$fileName", scannedFile.FileName),
            ("$fileSizeBytes", scannedFile.FileSizeBytes),
            ("$modifiedAt", scannedFile.ModifiedAt?.ToString("O")),
            ("$mediaKind", mediaKind),
            ("$durationSeconds", Math.Max(0, probe?.DurationSeconds ?? 0)),
            ("$container", NullIfWhiteSpace(probe?.Container)),
            ("$videoCodec", NullIfWhiteSpace(probe?.VideoCodec)),
            ("$audioCodec", NullIfWhiteSpace(probe?.AudioCodec)),
            ("$subtitleSummary", NullIfWhiteSpace(probe?.SubtitleSummary)),
            ("$probeJson", NullIfWhiteSpace(probe?.RawJson)),
            ("$hasProbe", probe is null ? 0 : 1),
            ("$now", now));
    }

    private static async Task MarkVideoMissingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        string now,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            UPDATE video_files
            SET missing_at = $now,
                updated_at = $now
            WHERE id = $id AND missing_at IS NULL;
            """,
            cancellationToken,
            ("$id", id),
            ("$now", now));
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ReportProgress(
        IProgress<LibraryScanProgress>? progress,
        string phase,
        int sourceCount,
        int completedSourceCount,
        string? currentSourceName,
        int totalVideoFileCount,
        int processedVideoFileCount,
        int probeCandidateCount,
        int probedVideoFileCount,
        string? currentRelativePath)
    {
        progress?.Report(new LibraryScanProgress(
            phase,
            sourceCount,
            completedSourceCount,
            currentSourceName,
            totalVideoFileCount,
            processedVideoFileCount,
            probeCandidateCount,
            probedVideoFileCount,
            currentRelativePath,
            DateTimeOffset.UtcNow));
    }

    private static int ResolveProbeConcurrency()
    {
        var configured = Environment.GetEnvironmentVariable("OMNIPLAY_SCAN_PROBE_CONCURRENCY");
        return int.TryParse(configured, out var value)
            ? Math.Clamp(value, 1, 4)
            : 2;
    }

    private static object NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    }

    private static string ResolveMovieTitle(string absolutePath, SearchMetadata metadata)
    {
        return metadata.ChineseTitle
               ?? metadata.ForeignTitle
               ?? metadata.FullCleanTitle
               ?? MediaNameParser.CleanedTitleSource(absolutePath);
    }

    private static IReadOnlyDictionary<string, string> ResolveDuplicateEpisodeSubtitles(
        IReadOnlyList<ScannedVideoFile> scannedFiles)
    {
        var contexts = scannedFiles
            .Where(static file => MediaNameParser.IsLikelyTvEpisodePath(file.RelativePath))
            .Select(file =>
            {
                var metadata = MediaNameParser.ExtractSearchMetadata(file.MetadataPath);
                var showTitle = ResolveShowTitle(file.RelativePath, metadata);
                var libraryItemId = CreateTvLibraryItemId(showTitle);
                var episodeInfo = MediaNameParser.ParseEpisodeInfo(file.FileName, 0);
                return new EpisodeSubtitleContext(file, libraryItemId, episodeInfo.Season, episodeInfo.Episode);
            })
            .ToArray();

        Dictionary<string, string> subtitles = new(StringComparer.OrdinalIgnoreCase);
        foreach (var group in contexts
                     .GroupBy(static item => (item.LibraryItemId, item.Season, item.Episode))
                     .Where(static group => group.Count() > 1))
        {
            var tokenLists = group
                .Select(static item => new
                {
                    item.File.RelativePath,
                    Tokens = MediaNameParser.ExtractEpisodeSubtitleTokens(item.File.FileName)
                })
                .ToArray();
            var commonTokens = tokenLists
                .Select(static item => item.Tokens.Select(static token => token.Key).ToHashSet(StringComparer.OrdinalIgnoreCase))
                .Aggregate((left, right) =>
                {
                    left.IntersectWith(right);
                    return left;
                });
            var proposedSubtitles = tokenLists
                .Select(item => new
                {
                    item.RelativePath,
                    Subtitle = MediaNameParser.BuildEpisodeSubtitle(
                        item.Tokens.Where(token => !commonTokens.Contains(token.Key)).ToArray())
                })
                .Where(static item => !string.IsNullOrWhiteSpace(item.Subtitle))
                .ToArray();

            if (proposedSubtitles.Length != tokenLists.Length
                || proposedSubtitles.Select(static item => item.Subtitle).Distinct(StringComparer.OrdinalIgnoreCase).Count() != proposedSubtitles.Length)
            {
                continue;
            }

            foreach (var item in proposedSubtitles)
            {
                subtitles[item.RelativePath] = item.Subtitle!;
            }
        }

        return subtitles;
    }

    private static string ResolveShowTitle(string relativePath, SearchMetadata metadata)
    {
        var title = ResolveShowTitleFromRelativePath(relativePath)
                    ?? metadata.ChineseTitle
                    ?? metadata.ForeignTitle
                    ?? metadata.FullCleanTitle
                    ?? "未命名剧集";
        return MediaNameParser.NormalizeTvSeriesTitle(title);
    }

    private static string CreateTvLibraryItemId(string showTitle)
    {
        return StableId.Create("tv", showTitle);
    }

    private static string GetMovieGroupingKey(string relativePath, SearchMetadata metadata)
    {
        var normalized = MediaNameParser.NormalizeRelativePath(relativePath);
        if (TryGetBdmvGroupKey(normalized, out var bdmvGroupKey))
        {
            return NormalizeBluRayMovieGroupingKey(
                bdmvGroupKey,
                ResolveMovieTitle(relativePath, metadata));
        }

        var parent = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
        return string.IsNullOrWhiteSpace(parent)
            ? ResolveMovieTitle(relativePath, metadata)
            : parent;
    }

    private static string NormalizeBluRayMovieGroupingKey(string bdmvGroupKey, string fallbackTitle)
    {
        var parts = MediaNameParser.NormalizeRelativePath(bdmvGroupKey)
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
        var token = System.Text.RegularExpressions.Regex
            .Replace(input.Trim(), @"[._\-\s]+", string.Empty)
            .ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.IsMatch(
            token,
            @"^(vol(ume)?\d{0,2}|disc\d{0,2}|disk\d{0,2}|dvd\d{0,2}|cd\d{0,2}|bdrom|bdmv)$");
    }

    private static string? ResolveShowTitleFromRelativePath(string relativePath)
    {
        var parts = MediaNameParser.NormalizeRelativePath(relativePath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var parent = parts[^2];
        if (IsSeasonFolderName(parent))
        {
            return parts.Length >= 3 ? CleanFolderTitle(parts[^3]) : null;
        }

        return CleanFolderTitle(parent);
    }

    private static bool IsSeasonFolderName(string value)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(value, @"(?i)^(season|s)\s*\d{1,2}$|^第\s*\d{1,2}\s*季$|^特别篇$");
    }

    private static string CleanFolderTitle(string folderName)
    {
        var metadata = MediaNameParser.ExtractSearchMetadata(folderName);
        var title = metadata.ChineseTitle
                    ?? metadata.ForeignTitle
                    ?? metadata.FullCleanTitle
                    ?? folderName;
        return MediaNameParser.NormalizeTvSeriesTitle(title);
    }

    private static string NormalizeSortTitle(string title)
    {
        return title.Trim().ToLowerInvariant();
    }

    private static async Task DeleteOrphanEpisodesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            DELETE FROM episodes
            WHERE NOT EXISTS (
                SELECT 1
                FROM video_files vf
                WHERE vf.episode_id = episodes.id
                  AND vf.missing_at IS NULL
            );
            """,
            cancellationToken);
    }

    private sealed record EpisodeSubtitleContext(
        ScannedVideoFile File,
        string LibraryItemId,
        int Season,
        int Episode);

    private static long? TryGetFileSize(string absolutePath)
    {
        try
        {
            return new FileInfo(absolutePath).Length;
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? TryGetModifiedAt(string absolutePath)
    {
        try
        {
            return File.GetLastWriteTimeUtc(absolutePath);
        }
        catch
        {
            return null;
        }
    }

    private static string GetRemoteFileName(string relativePath)
    {
        var parts = MediaNameParser.NormalizeRelativePath(relativePath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? relativePath : parts[^1];
    }

    private static string? ReadWebDavPassword(string secretJson)
    {
        if (string.IsNullOrWhiteSpace(secretJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(secretJson);
        if (document.RootElement.TryGetProperty("password", out var password))
        {
            return password.GetString();
        }

        if (document.RootElement.TryGetProperty("Password", out var legacyPassword))
        {
            return legacyPassword.GetString();
        }

        return null;
    }

    private sealed record MediaSource(
        long Id,
        string Name,
        string Kind,
        string BaseUrl,
        string? Username,
        string? Password);

    private sealed record ScannedVideoFile(
        string MetadataPath,
        string RelativePath,
        string FileName,
        long? FileSizeBytes,
        DateTimeOffset? ModifiedAt);

    private sealed record ExistingVideoFile(
        string Id,
        string RelativePath,
        long? FileSizeBytes,
        string? ModifiedAt,
        bool HasProbeJson);

    private sealed class NoOpMediaProbeService : IMediaProbeService
    {
        public static readonly NoOpMediaProbeService Instance = new();

        private NoOpMediaProbeService()
        {
        }

        public Task<MediaProbeSnapshot?> ProbeAsync(string filePath, CancellationToken cancellationToken)
        {
            return Task.FromResult<MediaProbeSnapshot?>(null);
        }
    }

    private sealed class NoOpWebDavFileEnumerator : IWebDavFileEnumerator
    {
        public static readonly NoOpWebDavFileEnumerator Instance = new();

        private NoOpWebDavFileEnumerator()
        {
        }

        public Task<IReadOnlyList<WebDavFileEntry>> EnumerateFilesAsync(
            string rootUrl,
            string? username,
            string? password,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<WebDavFileEntry>>([]);
        }
    }
}
