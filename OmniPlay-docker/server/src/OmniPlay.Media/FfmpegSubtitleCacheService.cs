using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Core.Runtime;

namespace OmniPlay.Media;

public sealed class FfmpegSubtitleCacheService : ISubtitleCacheService
{
    private readonly ConcurrentDictionary<string, byte> nextEpisodePrewarmRequests = new(StringComparer.Ordinal);
    private readonly IStoragePaths storagePaths;
    private readonly IAppSettingsRepository appSettingsRepository;
    private readonly ILibraryRepository libraryRepository;
    private readonly IPlayableFileResolver playableFileResolver;

    public FfmpegSubtitleCacheService(
        IStoragePaths storagePaths,
        IAppSettingsRepository appSettingsRepository,
        ILibraryRepository libraryRepository,
        IPlayableFileResolver playableFileResolver)
    {
        this.storagePaths = storagePaths;
        this.appSettingsRepository = appSettingsRepository;
        this.libraryRepository = libraryRepository;
        this.playableFileResolver = playableFileResolver;
    }

    public async Task<string?> ReadExternalSubtitleAsWebVttAsync(
        string subtitlePath,
        CancellationToken cancellationToken = default)
    {
        var settings = await appSettingsRepository.GetAsync(cancellationToken);
        var cacheKey = BuildSubtitleCacheKey("external", subtitlePath, null);
        return await ReadCachedWebVttAsync(
            settings.Cache,
            cacheKey,
            token => ReadSubtitleAsWebVttUncachedAsync(subtitlePath, token),
            cancellationToken);
    }

    public async Task<string?> ReadEmbeddedSubtitleAsWebVttAsync(
        string inputPath,
        int subtitleOrdinal,
        CancellationToken cancellationToken = default)
    {
        if (subtitleOrdinal < 0)
        {
            return null;
        }

        var settings = await appSettingsRepository.GetAsync(cancellationToken);
        var cacheKey = BuildSubtitleCacheKey("embedded", inputPath, subtitleOrdinal);
        return await ReadCachedWebVttAsync(
            settings.Cache,
            cacheKey,
            token => ReadEmbeddedSubtitleAsWebVttUncachedAsync(inputPath, subtitleOrdinal, token),
            cancellationToken);
    }

    public async Task<string?> ExtractEmbeddedSubtitleAsSupAsync(
        string inputPath,
        int subtitleOrdinal,
        CancellationToken cancellationToken = default)
    {
        if (subtitleOrdinal < 0)
        {
            return null;
        }

        var settings = await appSettingsRepository.GetAsync(cancellationToken);
        var cacheKey = BuildSubtitleCacheKey("embedded-sup", inputPath, subtitleOrdinal);
        var cachePath = ResolveSubtitleSupCachePath(settings.Cache, cacheKey);
        var cached = new FileInfo(cachePath);
        if (cached.Exists && cached.Length > 0)
        {
            TouchLastAccessTime(cached);
            return cachePath;
        }

        var extractedPath = await ExtractEmbeddedSubtitleAsSupUncachedAsync(
            inputPath,
            subtitleOrdinal,
            cachePath,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(extractedPath))
        {
            await CleanupAsync(SettingsToBytes(settings.Cache.SubtitleMaxGb), cancellationToken);
        }

        return extractedPath;
    }

    public async Task<SubtitleCacheStatus> GetPgsCacheStatusAsync(
        string videoFileId,
        string? inputPath = null,
        CancellationToken cancellationToken = default)
    {
        var file = await playableFileResolver.ResolveAsync(videoFileId, cancellationToken);
        var effectiveInputPath = string.IsNullOrWhiteSpace(inputPath) ? file?.AbsolutePath : inputPath;
        if (string.IsNullOrWhiteSpace(effectiveInputPath) || !File.Exists(effectiveInputPath))
        {
            return new SubtitleCacheStatus(0, 0, 0, 0, 0);
        }
        var summary = await ResolveVideoFileSummaryAsync(videoFileId, cancellationToken);
        if (summary is null)
        {
            return new SubtitleCacheStatus(0, 0, 0, 0, 0);
        }

        var settings = await appSettingsRepository.GetAsync(cancellationToken);
        var total = 0;
        var cached = 0;
        var subtitleTotal = 0;
        var subtitleCached = 0;
        var cachedBytes = 0L;

        foreach (var candidate in SelectSubtitleCandidates(string.Empty, summary))
        {
            subtitleTotal++;
            if (candidate.Kind == SubtitleCacheKind.PgsSup)
            {
                total++;
            }

            var scope = candidate.Kind == SubtitleCacheKind.PgsSup ? "embedded-sup" : "embedded";
            var cacheKey = BuildSubtitleCacheKey(scope, effectiveInputPath, candidate.SubtitleOrdinal);
            var cachePath = candidate.Kind == SubtitleCacheKind.PgsSup
                ? ResolveSubtitleSupCachePath(settings.Cache, cacheKey)
                : ResolveSubtitleWebVttCachePath(settings.Cache, cacheKey);
            var cachedFile = new FileInfo(cachePath);
            if (!cachedFile.Exists || cachedFile.Length <= 0)
            {
                continue;
            }

            subtitleCached++;
            if (candidate.Kind == SubtitleCacheKind.PgsSup)
            {
                cached++;
            }
            cachedBytes += cachedFile.Length;
        }

        return new SubtitleCacheStatus(total, cached, cachedBytes, subtitleTotal, subtitleCached);
    }

    public async Task<SubtitleCachePrewarmSummary> PrewarmLibraryAsync(
        string? targetLibraryItemId = null,
        IProgress<BackgroundTaskProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await appSettingsRepository.GetAsync(cancellationToken);
        var strategy = SubtitleCacheStrategies.Normalize(settings.Cache.SubtitleCacheStrategy);
        if (string.Equals(strategy, SubtitleCacheStrategies.Disabled, StringComparison.OrdinalIgnoreCase))
        {
            return new SubtitleCachePrewarmSummary(0, 0, 0, 0);
        }

        progress?.Report(new BackgroundTaskProgress("subtitle-cache", "正在准备字幕缓存", null, null));

        var details = await LoadTargetDetailsAsync(targetLibraryItemId, cancellationToken);
        var candidates = details
            .SelectMany(detail => SelectPrewarmCandidates(detail, strategy))
            .ToArray();

        var cached = 0;
        var skipped = 0;
        var cachedBytes = 0L;
        for (var index = 0; index < candidates.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[index];
            progress?.Report(new BackgroundTaskProgress(
                "subtitle-cache",
                $"正在缓存字幕 {index + 1}/{candidates.Length}",
                candidates.Length > 0 ? index * 100d / candidates.Length : null,
                candidate.DisplayName));

            var result = await CacheCandidateAsync(candidate, cancellationToken);
            if (result is null)
            {
                skipped++;
                continue;
            }

            cached++;
            cachedBytes += result.CachedBytes;
        }

        if (candidates.Length > 0)
        {
            await CleanupAsync(SettingsToBytes(settings.Cache.SubtitleMaxGb), cancellationToken);
        }

        progress?.Report(new BackgroundTaskProgress(
            "subtitle-cache",
            $"字幕缓存完成：{cached}/{candidates.Length}",
            100,
            null));
        return new SubtitleCachePrewarmSummary(candidates.Length, cached, skipped, cachedBytes);
    }

    public async Task<SubtitleCachePrewarmSummary> PrewarmNextEpisodeAsync(
        string videoFileId,
        CancellationToken cancellationToken = default)
    {
        var normalizedVideoFileId = videoFileId.Trim();
        var settings = await appSettingsRepository.GetAsync(cancellationToken);
        if (string.Equals(
                SubtitleCacheStrategies.Normalize(settings.Cache.SubtitleCacheStrategy),
                SubtitleCacheStrategies.Disabled,
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(normalizedVideoFileId)
            || !nextEpisodePrewarmRequests.TryAdd(normalizedVideoFileId, 0))
        {
            return new SubtitleCachePrewarmSummary(0, 0, 0, 0);
        }

        var libraryItemId = await libraryRepository.GetLibraryItemIdForVideoFileAsync(normalizedVideoFileId, cancellationToken);
        if (string.IsNullOrWhiteSpace(libraryItemId))
        {
            return new SubtitleCachePrewarmSummary(0, 0, 0, 0);
        }

        var detail = await libraryRepository.GetItemDetailAsync(libraryItemId, cancellationToken);
        if (detail is null)
        {
            return new SubtitleCachePrewarmSummary(0, 0, 0, 0);
        }

        var orderedFiles = OrderEpisodeFiles(detail.VideoFiles).ToArray();
        var currentIndex = Array.FindIndex(
            orderedFiles,
            file => string.Equals(file.Id, normalizedVideoFileId, StringComparison.Ordinal));
        if (currentIndex < 0 || currentIndex + 1 >= orderedFiles.Length)
        {
            return new SubtitleCachePrewarmSummary(0, 0, 0, 0);
        }

        var nextFile = orderedFiles[currentIndex + 1];
        var candidates = SelectSubtitleCandidates(detail.Title, nextFile).ToArray();
        var cached = 0;
        var skipped = 0;
        var cachedBytes = 0L;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await CacheCandidateAsync(candidate, cancellationToken);
            if (result is null)
            {
                skipped++;
                continue;
            }

            cached++;
            cachedBytes += result.CachedBytes;
        }

        if (candidates.Length > 0)
        {
            await CleanupAsync(SettingsToBytes(settings.Cache.SubtitleMaxGb), cancellationToken);
        }

        return new SubtitleCachePrewarmSummary(candidates.Length, cached, skipped, cachedBytes);
    }

    public async Task<SubtitleCacheCleanupSummary> CleanupAsync(
        long? maxBytes = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await appSettingsRepository.GetAsync(cancellationToken);
        var root = ResolveSubtitleCacheRoot(settings.Cache);
        if (!Directory.Exists(root))
        {
            return new SubtitleCacheCleanupSummary(0, 0);
        }

        var budget = maxBytes ?? SettingsToBytes(settings.Cache.SubtitleMaxGb);
        var files = EnumerateCacheFiles(root)
            .OrderByDescending(static file => file.LastAccessTimeUtc)
            .ToArray();
        var totalBytes = files.Sum(static file => file.Length);
        if (totalBytes <= budget)
        {
            return new SubtitleCacheCleanupSummary(0, 0);
        }

        var removedFiles = 0;
        var removedBytes = 0L;
        foreach (var file in files.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (totalBytes <= budget)
            {
                break;
            }

            try
            {
                var length = file.Length;
                file.Delete();
                totalBytes -= length;
                removedBytes += length;
                removedFiles++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return new SubtitleCacheCleanupSummary(removedFiles, removedBytes);
    }

    private async Task<IReadOnlyList<LibraryItemDetail>> LoadTargetDetailsAsync(
        string? targetLibraryItemId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(targetLibraryItemId))
        {
            var detail = await libraryRepository.GetItemDetailAsync(targetLibraryItemId.Trim(), cancellationToken);
            return detail is null ? [] : [detail];
        }

        var items = await libraryRepository.GetItemsAsync(cancellationToken);
        List<LibraryItemDetail> details = [];
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detail = await libraryRepository.GetItemDetailAsync(item.Id, cancellationToken);
            if (detail is not null)
            {
                details.Add(detail);
            }
        }

        return details;
    }

    private async Task<VideoFileSummary?> ResolveVideoFileSummaryAsync(
        string videoFileId,
        CancellationToken cancellationToken)
    {
        var items = await libraryRepository.GetItemsAsync(cancellationToken);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detail = await libraryRepository.GetItemDetailAsync(item.Id, cancellationToken);
            var file = detail?.VideoFiles.FirstOrDefault(file => string.Equals(file.Id, videoFileId, StringComparison.Ordinal));
            if (file is not null)
            {
                return file;
            }
        }

        return null;
    }

    private static IEnumerable<SubtitleCacheCandidate> SelectPrewarmCandidates(
        LibraryItemDetail detail,
        string strategy)
    {
        if (!string.Equals(detail.ItemKind, "tv", StringComparison.OrdinalIgnoreCase))
        {
            return detail.VideoFiles.SelectMany(file => SelectSubtitleCandidates(detail.Title, file));
        }

        var orderedFiles = OrderEpisodeFiles(detail.VideoFiles).ToArray();
        if (string.Equals(strategy, SubtitleCacheStrategies.Full, StringComparison.OrdinalIgnoreCase))
        {
            return orderedFiles.SelectMany(file => SelectSubtitleCandidates(detail.Title, file));
        }

        var currentIndex = Array.FindIndex(orderedFiles, IsUnfinishedEpisode);
        if (currentIndex < 0)
        {
            currentIndex = Array.FindIndex(orderedFiles, static file => !file.IsWatched);
        }

        if (currentIndex < 0)
        {
            return [];
        }

        return orderedFiles
            .Skip(currentIndex)
            .Take(2)
            .SelectMany(file => SelectSubtitleCandidates(detail.Title, file));
    }

    private static IEnumerable<VideoFileSummary> OrderEpisodeFiles(IEnumerable<VideoFileSummary> files)
    {
        return files
            .OrderBy(static file => file.SeasonNumber ?? int.MaxValue)
            .ThenBy(static file => file.EpisodeNumber ?? int.MaxValue)
            .ThenBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsUnfinishedEpisode(VideoFileSummary file)
    {
        if (file.IsWatched || file.PositionSeconds <= 5)
        {
            return false;
        }

        return file.DurationSeconds <= 0 || file.PositionSeconds < file.DurationSeconds * 0.95;
    }

    private static IEnumerable<SubtitleCacheCandidate> SelectPgsCandidates(
        string itemTitle,
        VideoFileSummary file)
    {
        return file.SubtitleStreams
            .Select((stream, ordinal) => (Stream: stream, Ordinal: ordinal))
            .Where(static item => IsPgsSubtitle(item.Stream.Codec))
            .Select(item => new SubtitleCacheCandidate(
                file.Id,
                item.Ordinal,
                SubtitleCacheKind.PgsSup,
                $"{itemTitle} · {file.FileName} · PGS 字幕 {item.Ordinal + 1}"));
    }

    private static IEnumerable<SubtitleCacheCandidate> SelectSubtitleCandidates(
        string itemTitle,
        VideoFileSummary file)
    {
        return file.SubtitleStreams
            .Select((stream, ordinal) => (Stream: stream, Ordinal: ordinal))
            .Where(static item => IsPrewarmableSubtitle(item.Stream.Codec))
            .Select(item => new SubtitleCacheCandidate(
                file.Id,
                item.Ordinal,
                IsPgsSubtitle(item.Stream.Codec) ? SubtitleCacheKind.PgsSup : SubtitleCacheKind.WebVtt,
                $"{itemTitle} · {file.FileName} · {SubtitleDisplayFormat(item.Stream.Codec)} 字幕 {item.Ordinal + 1}"));
    }

    private static bool IsPgsSubtitle(string? codec)
    {
        var normalized = codec?.Trim().ToLowerInvariant().Replace('_', '-');
        return normalized is "hdmv-pgs-subtitle" or "pgs" or "pgssub" or "sup";
    }

    private static bool IsPrewarmableSubtitle(string? codec)
    {
        var normalized = codec?.Trim().ToLowerInvariant().Replace('_', '-');
        return IsPgsSubtitle(codec)
               || normalized is "subrip" or "srt" or "webvtt" or "vtt" or "ass" or "ssa";
    }

    private static string SubtitleDisplayFormat(string? codec)
    {
        var normalized = codec?.Trim().ToLowerInvariant().Replace('_', '-');
        if (IsPgsSubtitle(codec)) { return "PGS"; }
        if (normalized is "subrip" or "srt") { return "SRT"; }
        if (normalized is "webvtt" or "vtt") { return "VTT"; }
        if (normalized == "ass") { return "ASS"; }
        if (normalized == "ssa") { return "SSA"; }
        return "文本";
    }

    private async Task<SubtitleCacheResult?> CacheCandidateAsync(
        SubtitleCacheCandidate candidate,
        CancellationToken cancellationToken)
    {
        var file = await playableFileResolver.ResolveAsync(candidate.VideoFileId, cancellationToken);
        if (file is null || string.IsNullOrWhiteSpace(file.AbsolutePath) || !File.Exists(file.AbsolutePath))
        {
            return null;
        }

        if (candidate.Kind == SubtitleCacheKind.PgsSup)
        {
            var path = await ExtractEmbeddedSubtitleAsSupAsync(file.AbsolutePath, candidate.SubtitleOrdinal, cancellationToken);
            return string.IsNullOrWhiteSpace(path)
                ? null
                : new SubtitleCacheResult(SafeFileLength(path));
        }

        var webVtt = await ReadEmbeddedSubtitleAsWebVttAsync(file.AbsolutePath, candidate.SubtitleOrdinal, cancellationToken);
        return string.IsNullOrWhiteSpace(webVtt)
            ? null
            : new SubtitleCacheResult(Encoding.UTF8.GetByteCount(webVtt));
    }

    private async Task<string?> ReadSubtitleAsWebVttUncachedAsync(
        string subtitlePath,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(subtitlePath).ToLowerInvariant();
        if (extension == ".vtt")
        {
            var text = await File.ReadAllTextAsync(subtitlePath, cancellationToken);
            return text.TrimStart().StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)
                ? text
                : $"WEBVTT\n\n{text}";
        }

        if (extension is not ".srt")
        {
            return await ConvertSubtitleFileAsWebVttAsync(subtitlePath, cancellationToken);
        }

        var srt = await File.ReadAllTextAsync(subtitlePath, cancellationToken);
        var builder = new StringBuilder("WEBVTT\n\n");
        foreach (var line in srt.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            builder.AppendLine(line.Contains("-->", StringComparison.Ordinal)
                ? line.Replace(',', '.')
                : line);
        }

        return builder.ToString();
    }

    private async Task<string?> ConvertSubtitleFileAsWebVttAsync(
        string subtitlePath,
        CancellationToken cancellationToken)
    {
        using var process = CreateFfmpegProcess(redirectStandardOutput: true);
        ConfigureFfmpegFontEnvironment(process.StartInfo);
        foreach (var argument in new[]
                 {
                     "-hide_banner",
                     "-nostdin",
                     "-loglevel", "error",
                     "-i", subtitlePath,
                     "-vn",
                     "-an",
                     "-f", "webvtt",
                     "-"
                 })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception)
        {
            return null;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token);
            var stdout = await stdoutTask;
            _ = await stderrTask;
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                return null;
            }

            var text = stdout.TrimStart('\uFEFF');
            return text.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)
                ? text
                : $"WEBVTT\n\n{text}";
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            return null;
        }
    }

    private async Task<string?> ReadEmbeddedSubtitleAsWebVttUncachedAsync(
        string inputPath,
        int subtitleOrdinal,
        CancellationToken cancellationToken)
    {
        using var process = CreateFfmpegProcess(redirectStandardOutput: true);
        ConfigureFfmpegFontEnvironment(process.StartInfo);
        foreach (var argument in new[]
                 {
                     "-hide_banner",
                     "-nostdin",
                     "-loglevel", "error",
                     "-i", inputPath,
                     "-map", $"0:s:{subtitleOrdinal}",
                     "-vn",
                     "-an",
                     "-f", "webvtt",
                     "-"
                 })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception)
        {
            return null;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token);
            var stdout = await stdoutTask;
            _ = await stderrTask;
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                return null;
            }

            var text = stdout.TrimStart('\uFEFF');
            return text.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)
                ? text
                : $"WEBVTT\n\n{text}";
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            return null;
        }
    }

    private async Task<string?> ExtractEmbeddedSubtitleAsSupUncachedAsync(
        string inputPath,
        int subtitleOrdinal,
        string cachePath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var tempPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
        using var process = CreateFfmpegProcess(redirectStandardOutput: false);
        foreach (var argument in new[]
                 {
                     "-hide_banner",
                     "-nostdin",
                     "-loglevel", "error",
                     "-i", inputPath,
                     "-map", $"0:s:{subtitleOrdinal}",
                     "-vn",
                     "-an",
                     "-c:s", "copy",
                     "-f", "sup",
                     tempPath
                 })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception)
        {
            return null;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token);
            _ = await stderrTask;

            var output = new FileInfo(tempPath);
            if (process.ExitCode != 0 || !output.Exists || output.Length == 0)
            {
                return null;
            }

            File.Move(tempPath, cachePath, overwrite: true);
            TouchLastAccessTime(new FileInfo(cachePath));
            return cachePath;
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            return null;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private async Task<string?> ReadCachedWebVttAsync(
        CacheSettings cacheSettings,
        string cacheKey,
        Func<CancellationToken, Task<string?>> factory,
        CancellationToken cancellationToken)
    {
        var cachePath = ResolveSubtitleWebVttCachePath(cacheSettings, cacheKey);
        var cached = new FileInfo(cachePath);
        if (cached.Exists && cached.Length > 0)
        {
            try
            {
                TouchLastAccessTime(cached);
                return await File.ReadAllTextAsync(cachePath, cancellationToken);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var webVtt = await factory(cancellationToken);
        if (string.IsNullOrWhiteSpace(webVtt))
        {
            return webVtt;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var tempPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(tempPath, webVtt, Encoding.UTF8, cancellationToken);
            File.Move(tempPath, cachePath, overwrite: true);
            TouchLastAccessTime(new FileInfo(cachePath));
            await CleanupAsync(SettingsToBytes(cacheSettings.SubtitleMaxGb), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Subtitle conversion can still be served for this request even if cache write fails.
        }

        return webVtt;
    }

    private Process CreateFfmpegProcess(bool redirectStandardOutput)
    {
        return new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = MediaToolPathResolver.ResolveFfmpegPath(),
                RedirectStandardOutput = redirectStandardOutput,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
    }

    private void ConfigureFfmpegFontEnvironment(ProcessStartInfo startInfo)
    {
        var packagedFontconfigPath = Path.Combine(AppContext.BaseDirectory, "tools", "fontconfig");
        var packagedFontconfigFile = Path.Combine(packagedFontconfigPath, "fonts.conf");
        if (File.Exists(packagedFontconfigFile))
        {
            startInfo.Environment.TryAdd("FONTCONFIG_FILE", packagedFontconfigFile);
            startInfo.Environment.TryAdd("FONTCONFIG_PATH", packagedFontconfigPath);
        }

        var fontconfigCache = Path.Combine(storagePaths.CacheDirectory, "fontconfig");
        Directory.CreateDirectory(fontconfigCache);
        startInfo.Environment.TryAdd("FONTCONFIG_CACHE", fontconfigCache);
    }

    private string ResolveSubtitleCacheRoot(CacheSettings cacheSettings)
    {
        return SubtitleCachePathResolver.ResolveRoot(storagePaths, cacheSettings);
    }

    private string ResolveSubtitleWebVttCachePath(CacheSettings cacheSettings, string cacheKey)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey))).ToLowerInvariant();
        return Path.Combine(ResolveSubtitleCacheRoot(cacheSettings), "webvtt", $"{hash}.vtt");
    }

    private string ResolveSubtitleSupCachePath(CacheSettings cacheSettings, string cacheKey)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey))).ToLowerInvariant();
        return Path.Combine(ResolveSubtitleCacheRoot(cacheSettings), "sup", $"{hash}.sup");
    }

    private static string BuildSubtitleCacheKey(string scope, string subtitlePath, int? subtitleOrdinal)
    {
        var info = new FileInfo(subtitlePath);
        var length = info.Exists ? info.Length : 0;
        var lastWriteTicks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
        return string.Join(
            "|",
            scope,
            Path.GetFullPath(subtitlePath),
            length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            lastWriteTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            subtitleOrdinal?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "file");
    }

    private static IEnumerable<FileInfo> EnumerateCacheFiles(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(static path => new FileInfo(path))
                .Where(static file => file.Exists)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static long SettingsToBytes(int maxGb)
    {
        return Math.Max(1, maxGb) * 1024L * 1024L * 1024L;
    }

    private static long SafeFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void TouchLastAccessTime(FileInfo file)
    {
        try
        {
            file.LastAccessTimeUtc = DateTime.UtcNow;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch
        {
        }
    }

    private enum SubtitleCacheKind
    {
        PgsSup,
        WebVtt
    }

    private sealed record SubtitleCacheCandidate(
        string VideoFileId,
        int SubtitleOrdinal,
        SubtitleCacheKind Kind,
        string DisplayName);

    private sealed record SubtitleCacheResult(long CachedBytes);
}
