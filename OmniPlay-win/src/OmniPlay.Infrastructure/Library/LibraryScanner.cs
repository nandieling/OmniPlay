using Dapper;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Core.Models.Playback;
using OmniPlay.Infrastructure.Data;
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
    private readonly IWebDavDiscoveryClient? webDavDiscoveryClient;

    public LibraryScanner(
        SqliteDatabase database,
        IMediaSourceRepository mediaSourceRepository,
        ILibraryMetadataEnricher? metadataEnricher = null,
        ISettingsService? settingsService = null,
        IWebDavDiscoveryClient? webDavDiscoveryClient = null)
    {
        this.database = database;
        this.mediaSourceRepository = mediaSourceRepository;
        this.webDavDiscoveryClient = webDavDiscoveryClient;
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

    private async Task<LibraryScanSummary> ScanLocalSourceAsync(MediaSource source, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source.BaseUrl))
        {
            return CreateDiagnosticSummary(source, $"已跳过本地媒体源“{ResolveSourceDisplayName(source)}”：目录不存在。");
        }

        var scannedFiles = EnumerateLocalVideoFiles(source.BaseUrl, cancellationToken);
        return await ScanSourceAsync(source, scannedFiles, cancellationToken);
    }

    private async Task<LibraryScanSummary> ScanSmbSourceAsync(MediaSource source, CancellationToken cancellationToken)
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
            return await ScanSourceAsync(source, scannedFiles, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return CreateDiagnosticSummary(source, $"扫描 SMB 媒体源“{ResolveSourceDisplayName(source)}”失败：{ex.Message}");
        }
    }

    private async Task<LibraryScanSummary> ScanWebDavSourceAsync(MediaSource source, CancellationToken cancellationToken)
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

        return await ScanSourceAsync(source, scannedFiles, cancellationToken);
    }

    private async Task<LibraryScanSummary> ScanSourceWithDiagnosticsAsync(MediaSource source, CancellationToken cancellationToken)
    {
        try
        {
            return source.ProtocolKind switch
            {
                MediaSourceProtocol.Local => await ScanLocalSourceAsync(source, cancellationToken),
                MediaSourceProtocol.WebDav => await ScanWebDavSourceAsync(source, cancellationToken),
                MediaSourceProtocol.Smb => await ScanSmbSourceAsync(source, cancellationToken),
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
        CancellationToken cancellationToken)
    {
        var newMovies = 0;
        var newVideoFiles = 0;
        var removedVideoFiles = 0;
        var newTvShows = 0;

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var relativePaths = scannedFiles
            .Select(static file => file.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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

        var existingFilesByRelativePath = await NormalizeExistingFilesAsync(
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

        foreach (var scannedFile in scannedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var metadata = MediaNameParser.ExtractSearchMetadata(scannedFile.MetadataPath);
            var isTv = MediaNameParser.IsLikelyTvEpisodePath(scannedFile.RelativePath);
            var hasExistingFile = existingFilesByRelativePath.TryGetValue(scannedFile.RelativePath, out var existingFile);

            if (isTv)
            {
                var showTitle = ResolveShowTitle(scannedFile.MetadataPath, scannedFile.RelativePath, metadata);
                var tvShowId = CreateSyntheticEntityId("tv", source.Id!.Value, showTitle);

                if (await EnsureTvShowAsync(
                        connection,
                        transaction,
                        tvShowId,
                        showTitle,
                        existingFile?.EpisodeId,
                        cancellationToken))
                {
                    newTvShows++;
                }

                if (hasExistingFile)
                {
                    await UpdateExistingVideoFileAsync(
                        connection,
                        transaction,
                        existingFile!,
                        scannedFile,
                        "tv",
                        movieId: null,
                        episodeId: tvShowId,
                        cancellationToken);
                    continue;
                }

                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                        VALUES (@Id, @SourceId, @RelativePath, @FileName, 'tv', NULL, @EpisodeId, 0, 0)
                        """,
                        new
                        {
                            Id = Guid.NewGuid().ToString(),
                            SourceId = source.Id!.Value,
                            RelativePath = scannedFile.RelativePath,
                            FileName = scannedFile.FileName,
                            EpisodeId = tvShowId
                        },
                        transaction,
                        cancellationToken: cancellationToken));
            }
            else
            {
                var movieTitle = ResolveMovieTitle(scannedFile.MetadataPath, metadata);
                if (hasExistingFile && existingFile!.MovieId.HasValue)
                {
                    await UpdateUnenrichedMoviePlaceholderAsync(
                        connection,
                        transaction,
                        existingFile.MovieId.Value,
                        movieTitle,
                        cancellationToken);
                    await UpdateExistingVideoFileAsync(
                        connection,
                        transaction,
                        existingFile,
                        scannedFile,
                        "movie",
                        existingFile.MovieId,
                        episodeId: null,
                        cancellationToken);
                    continue;
                }

                var movieId = CreateSyntheticEntityId("movie", source.Id!.Value, GetMovieGroupingKey(scannedFile.RelativePath, movieTitle));
                var existingMovie = await connection.ExecuteScalarAsync<long?>(
                    new CommandDefinition(
                        "SELECT id FROM movie WHERE id = @Id LIMIT 1",
                        new { Id = movieId },
                        transaction,
                        cancellationToken: cancellationToken));

                if (!existingMovie.HasValue)
                {
                    await connection.ExecuteAsync(
                        new CommandDefinition(
                            """
                            INSERT INTO movie (id, title, releaseDate, overview, posterPath, voteAverage, isLocked)
                            VALUES (@Id, @Title, @ReleaseDate, NULL, NULL, NULL, 0)
                            """,
                            new
                            {
                                Id = movieId,
                                Title = movieTitle,
                                ReleaseDate = (string?)null
                            },
                            transaction,
                            cancellationToken: cancellationToken));
                    newMovies++;
                }

                if (hasExistingFile)
                {
                    await UpdateExistingVideoFileAsync(
                        connection,
                        transaction,
                        existingFile!,
                        scannedFile,
                        "movie",
                        movieId,
                        episodeId: null,
                        cancellationToken);
                    continue;
                }

                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO videoFile (id, sourceId, relativePath, fileName, mediaType, movieId, episodeId, playProgress, duration)
                        VALUES (@Id, @SourceId, @RelativePath, @FileName, 'movie', @MovieId, NULL, 0, 0)
                        """,
                        new
                        {
                            Id = Guid.NewGuid().ToString(),
                            SourceId = source.Id!.Value,
                            RelativePath = scannedFile.RelativePath,
                            FileName = scannedFile.FileName,
                            MovieId = movieId
                        },
                        transaction,
                        cancellationToken: cancellationToken));
            }

            newVideoFiles++;
        }

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
        return new LibraryScanSummary(1, newMovies, newVideoFiles, removedVideoFiles, newTvShows);
    }

    private static async Task<bool> EnsureTvShowAsync(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        long tvShowId,
        string title,
        long? copyFromTvShowId,
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
                VALUES (@Id, @Title, NULL, NULL, NULL, NULL, 0)
                """,
                new
                {
                    Id = tvShowId,
                    Title = title
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
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE videoFile
                SET relativePath = @RelativePath,
                    fileName = @FileName,
                    mediaType = @MediaType,
                    movieId = @MovieId,
                    episodeId = @EpisodeId
                WHERE id = @Id
                """,
                new
                {
                    Id = existingFile.Id,
                    RelativePath = scannedFile.RelativePath,
                    FileName = scannedFile.FileName,
                    MediaType = mediaType,
                    MovieId = movieId,
                    EpisodeId = episodeId
                },
                transaction,
                cancellationToken: cancellationToken));

        existingFile.RelativePath = scannedFile.RelativePath;
        existingFile.MovieId = movieId;
        existingFile.EpisodeId = episodeId;
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

        var maxSize = files.Max(static file => file.FileSize);
        if (maxSize <= 0)
        {
            yield return files
                .OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .First();
            yield break;
        }

        var threshold = maxSize * 0.3;
        foreach (var file in files
                     .Where(file => file.FileSize >= threshold)
                     .OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            yield return file;
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

    private static string ResolveMovieTitle(string absolutePath, SearchMetadata metadata)
    {
        return metadata.ChineseTitle
               ?? metadata.ForeignTitle
               ?? metadata.FullCleanTitle
               ?? MediaNameParser.CleanedTitleSource(absolutePath);
    }

    private static string ResolveShowTitle(string absolutePath, string relativePath, SearchMetadata metadata)
    {
        return ResolveShowTitleFromRelativePath(relativePath)
               ?? metadata.ChineseTitle
               ?? metadata.ForeignTitle
               ?? MediaNameParser.CleanedTitleSource(absolutePath);
    }

    private static string? ResolveShowTitleFromRelativePath(string relativePath)
    {
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

    private static string? CleanShowFolderTitle(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return null;
        }

        var trimmed = folderName.Trim();
        var metadata = MediaNameParser.ExtractSearchMetadata(trimmed);
        return ExtractPrimaryCjkTitleSegment(metadata.FullCleanTitle)
               ?? metadata.ChineseTitle
               ?? metadata.ForeignTitle
               ?? metadata.FullCleanTitle
               ?? trimmed;
    }

    private static string? ExtractPrimaryCjkTitleSegment(string? title)
    {
        var trimmed = title?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        var match = Regex.Match(trimmed, @"^\s*([\p{IsCJKUnifiedIdeographs}\d]+)(?:\s+(\d{2,4}))?(?=\s|$)");
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
        var normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith("BDMV/", StringComparison.OrdinalIgnoreCase))
        {
            return fallbackTitle;
        }

        if (normalized.Contains("/BDMV/", StringComparison.OrdinalIgnoreCase))
        {
            var bdmvIndex = normalized.IndexOf("/BDMV/", StringComparison.OrdinalIgnoreCase);
            return bdmvIndex > 0 ? normalized[..bdmvIndex] : fallbackTitle;
        }

        return normalized;
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

    private sealed class ExistingVideoFileRecord
    {
        public string Id { get; init; } = string.Empty;

        public string RelativePath { get; set; } = string.Empty;

        public long? MovieId { get; set; }

        public long? EpisodeId { get; set; }

        public double PlayProgress { get; set; }

        public double Duration { get; set; }
    }

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
        long FileSize);

}
