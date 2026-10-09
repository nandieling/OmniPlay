namespace OmniPlay.Core.Models.Entities;

public static class MediaSourcePathResolver
{
    private static readonly string[] BluRayIndexFileNames =
    [
        "index.bdmv",
        "MovieObject.bdmv"
    ];

    private static readonly string[] DvdIndexFileNames =
    [
        "VIDEO_TS.IFO",
        "VIDEO_TS.BUP"
    ];

    private sealed record LocalBluRayStreamCandidate(
        string Path,
        string FileName,
        long FileSize);

    public sealed record LocalBluRayMainFeatureSegment(
        string Path,
        double DurationSeconds = 0);

    public static string ResolvePlaybackPath(string? protocolType, string? baseUrl, string? relativePath)
    {
        return ResolvePlaybackPath(ParseProtocolKind(protocolType), baseUrl, relativePath);
    }

    public static string ResolveAuthenticatedPlaybackPath(
        string? protocolType,
        string? baseUrl,
        string? relativePath,
        string? authConfig)
    {
        return ResolveAuthenticatedPlaybackPath(ParseProtocolKind(protocolType), baseUrl, relativePath, authConfig);
    }

    public static string ResolvePlaybackPath(MediaSourceProtocol? protocol, string? baseUrl, string? relativePath)
    {
        var normalizedRelativePath = NormalizeRelativePath(relativePath);

        return protocol switch
        {
            MediaSourceProtocol.Local => ResolveLocalPath(baseUrl, normalizedRelativePath),
            MediaSourceProtocol.WebDav => ResolveWebDavUrl(baseUrl, normalizedRelativePath),
            MediaSourceProtocol.Smb => ResolveLocalPath(baseUrl, normalizedRelativePath),
            MediaSourceProtocol.Direct => normalizedRelativePath,
            MediaSourceProtocol.Plex or MediaSourceProtocol.Emby or MediaSourceProtocol.Jellyfin =>
                ResolveMediaServerUrl(baseUrl, normalizedRelativePath),
            _ => string.Empty
        };
    }

    public static string ResolveAuthenticatedPlaybackPath(
        MediaSourceProtocol? protocol,
        string? baseUrl,
        string? relativePath,
        string? authConfig)
    {
        var normalizedRelativePath = NormalizeRelativePath(relativePath);

        return protocol switch
        {
            MediaSourceProtocol.WebDav => ResolveWebDavUrl(
                baseUrl,
                normalizedRelativePath,
                MediaSourceAuthConfigSerializer.DeserializeWebDav(authConfig),
                includeCredentials: true),
            MediaSourceProtocol.Plex => AppendQueryParameterIfMissing(
                AppendQueryParameterIfMissing(
                    ResolveMediaServerUrl(baseUrl, normalizedRelativePath),
                    "download",
                    "1"),
                "X-Plex-Token",
                MediaSourceAuthConfigSerializer.DeserializeMediaServer(authConfig)?.Token),
            MediaSourceProtocol.Emby or MediaSourceProtocol.Jellyfin => AppendQueryParameter(
                ResolveMediaServerUrl(baseUrl, normalizedRelativePath),
                "api_key",
                MediaSourceAuthConfigSerializer.DeserializeMediaServer(authConfig)?.Token),
            _ => ResolvePlaybackPath(protocol, baseUrl, relativePath)
        };
    }

    public static string ResolveMetadataPath(string? protocolType, string? baseUrl, string? relativePath)
    {
        return ResolveMetadataPath(ParseProtocolKind(protocolType), baseUrl, relativePath);
    }

    public static string ResolveMetadataPath(MediaSourceProtocol? protocol, string? baseUrl, string? relativePath)
    {
        var playbackPath = ResolvePlaybackPath(protocol, baseUrl, relativePath);
        if (protocol == MediaSourceProtocol.WebDav &&
            Uri.TryCreate(playbackPath, UriKind.Absolute, out var playbackUri))
        {
            return Uri.UnescapeDataString(playbackUri.AbsolutePath);
        }

        return playbackPath;
    }

    public static string NormalizeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        var normalized = relativePath
            .Trim()
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized.TrimStart('/');
    }

    public static bool IsRemoteHttpUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    public static bool IsMediaServerPlaybackEndpointPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            candidate = uri.AbsolutePath;
        }

        var pathOnly = candidate.Split('?', 2)[0]
            .Replace('\\', '/')
            .Trim('/');
        if (string.IsNullOrWhiteSpace(pathOnly))
        {
            return false;
        }

        var parts = pathOnly
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static part => part.ToLowerInvariant())
            .ToArray();

        return parts is ["items", _, "download", ..] ||
               parts is ["videos", _, "master.m3u8", ..] ||
               (parts.Length >= 3 &&
                parts[0] == "videos" &&
                parts[2].StartsWith("stream.", StringComparison.Ordinal)) ||
               parts is ["library", "parts", ..] ||
               parts is ["library", "metadata", ..];
    }

    public static bool IsPlayableLocation(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               (IsRemoteHttpUrl(value) ||
                File.Exists(value) ||
                ResolveSingleIsoFileFromDirectory(value) is not null ||
                ResolveLocalBluRayRoot(value) is not null ||
                ResolveLocalDvdRoot(value) is not null);
    }

    public static string ResolvePlayableLocation(string value)
    {
        var singleIsoFile = ResolveSingleIsoFileFromDirectory(value);
        return singleIsoFile ?? value;
    }

    public static string? ResolveSingleIsoFileFromDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || IsRemoteHttpUrl(value))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(value.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!Directory.Exists(fullPath) ||
            ResolveBluRayRootFromDirectory(fullPath) is not null ||
            ResolveDvdRootFromDirectory(fullPath) is not null)
        {
            return null;
        }

        try
        {
            var isoFiles = Directory.EnumerateFiles(fullPath, "*.iso", SearchOption.TopDirectoryOnly)
                .Take(2)
                .ToList();
            return isoFiles.Count == 1 ? isoFiles[0] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? ResolveLocalBluRayRoot(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || IsRemoteHttpUrl(value))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(value.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (Directory.Exists(fullPath))
        {
            return ResolveBluRayRootFromDirectory(fullPath);
        }

        if (!File.Exists(fullPath))
        {
            return null;
        }

        var parentDirectory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            return null;
        }

        if (IsBluRayIndexFile(Path.GetFileName(fullPath)) &&
            DirectoryNameEquals(parentDirectory, "BDMV") &&
            IsBluRayDirectory(parentDirectory))
        {
            return Path.GetDirectoryName(parentDirectory);
        }

        if (IsBluRayStreamFile(fullPath))
        {
            var streamDirectory = Path.GetDirectoryName(fullPath);
            var bdmvDirectory = string.IsNullOrWhiteSpace(streamDirectory)
                ? null
                : Path.GetDirectoryName(streamDirectory);
            return string.IsNullOrWhiteSpace(bdmvDirectory)
                ? null
                : Path.GetDirectoryName(bdmvDirectory);
        }

        var ancestorBluRayDirectory = FindAncestorBluRayDirectory(parentDirectory);
        return ancestorBluRayDirectory is null ? null : Path.GetDirectoryName(ancestorBluRayDirectory);
    }

    public static string? ResolveLocalDvdRoot(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || IsRemoteHttpUrl(value))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(value.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (Directory.Exists(fullPath))
        {
            return ResolveDvdRootFromDirectory(fullPath);
        }

        if (!File.Exists(fullPath))
        {
            return null;
        }

        var parentDirectory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            return null;
        }

        if (DirectoryNameEquals(parentDirectory, "VIDEO_TS") && IsDvdVideoTsDirectory(parentDirectory))
        {
            return Path.GetDirectoryName(parentDirectory);
        }

        var ancestorVideoTsDirectory = FindAncestorDvdVideoTsDirectory(parentDirectory);
        return ancestorVideoTsDirectory is null ? null : Path.GetDirectoryName(ancestorVideoTsDirectory);
    }

    public static IReadOnlyList<string> ResolveLocalBluRayMainFeaturePaths(string? value)
    {
        return ResolveLocalBluRayMainFeatureSegments(value)
            .Select(static segment => segment.Path)
            .ToList();
    }

    public static IReadOnlyList<LocalBluRayMainFeatureSegment> ResolveLocalBluRayMainFeatureSegments(string? value)
    {
        var bluRayRoot = ResolveLocalBluRayRoot(value);
        if (string.IsNullOrWhiteSpace(bluRayRoot) || !Directory.Exists(bluRayRoot))
        {
            return [];
        }

        var bdmvDirectory = FindChildDirectory(bluRayRoot, "BDMV");
        if (string.IsNullOrWhiteSpace(bdmvDirectory))
        {
            return [];
        }

        var streamDirectory = FindChildDirectory(bdmvDirectory, "STREAM");
        if (string.IsNullOrWhiteSpace(streamDirectory))
        {
            return [];
        }

        var playlistFeaturePaths = ResolveLocalBluRayPlaylistFeaturePaths(bdmvDirectory, streamDirectory);
        if (playlistFeaturePaths.Count > 0)
        {
            return playlistFeaturePaths;
        }

        IReadOnlyList<LocalBluRayStreamCandidate> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(streamDirectory)
                .Where(IsBluRayStreamExtension)
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return new LocalBluRayStreamCandidate(
                        info.FullName,
                        info.Name,
                        info.Exists ? info.Length : 0);
                })
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return SelectedBluRayStreamIndices(candidates)
            .Select(index => new LocalBluRayMainFeatureSegment(candidates[index].Path))
            .ToList();
    }

    private static IReadOnlyList<LocalBluRayMainFeatureSegment> ResolveLocalBluRayPlaylistFeaturePaths(
        string bdmvDirectory,
        string streamDirectory)
    {
        var playlistDirectory = FindChildDirectory(bdmvDirectory, "PLAYLIST");
        if (string.IsNullOrWhiteSpace(playlistDirectory))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(playlistDirectory, "*.mpls")
                .Select(path => ResolveBluRayPlaylistCandidate(path, streamDirectory))
                .Where(static candidate => candidate.Paths.Count > 0 && candidate.TotalSize > 0)
                .OrderByDescending(static candidate => candidate.TotalSize)
                .ThenBy(static candidate => candidate.Paths.Count)
                .Select(static candidate => candidate.Paths)
                .FirstOrDefault() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static (IReadOnlyList<LocalBluRayMainFeatureSegment> Paths, long TotalSize) ResolveBluRayPlaylistCandidate(
        string playlistPath,
        string streamDirectory)
    {
        try
        {
            var segments = ParseBluRayPlaylistSegments(playlistPath, streamDirectory);
            if (segments.Count > 0)
            {
                var totalSize = segments.Sum(segment => new FileInfo(segment.Path).Length);
                return (segments, totalSize);
            }

            var text = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(playlistPath));
            var fallbackSegments = new List<LocalBluRayMainFeatureSegment>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"\d{5}"))
            {
                var streamPath = Path.Combine(streamDirectory, match.Value + ".m2ts");
                if (!File.Exists(streamPath) || !seen.Add(streamPath))
                {
                    continue;
                }

                fallbackSegments.Add(new LocalBluRayMainFeatureSegment(streamPath));
            }

            var fallbackTotalSize = fallbackSegments.Sum(segment => new FileInfo(segment.Path).Length);
            return (fallbackSegments, fallbackTotalSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ([], 0);
        }
    }

    private static IReadOnlyList<LocalBluRayMainFeatureSegment> ParseBluRayPlaylistSegments(
        string playlistPath,
        string streamDirectory)
    {
        var bytes = File.ReadAllBytes(playlistPath);
        if (bytes.Length < 18 ||
            bytes[0] != (byte)'M' ||
            bytes[1] != (byte)'P' ||
            bytes[2] != (byte)'L' ||
            bytes[3] != (byte)'S')
        {
            return [];
        }

        var playlistStart = ReadBigEndianUInt32(bytes, 8);
        if (playlistStart <= 0 || playlistStart + 10 > bytes.Length)
        {
            return [];
        }

        var playItemCount = ReadBigEndianUInt16(bytes, (int)playlistStart + 6);
        var offset = (int)playlistStart + 10;
        var segments = new List<LocalBluRayMainFeatureSegment>();
        for (var index = 0; index < playItemCount && offset + 22 <= bytes.Length; index++)
        {
            var itemLength = ReadBigEndianUInt16(bytes, offset);
            if (itemLength <= 0 || offset + 2 + itemLength > bytes.Length)
            {
                break;
            }

            var clipName = System.Text.Encoding.ASCII.GetString(bytes, offset + 2, 5);
            var streamPath = Path.Combine(streamDirectory, clipName + ".m2ts");
            if (File.Exists(streamPath))
            {
                var inTime = ReadBigEndianUInt32(bytes, offset + 14);
                var outTime = ReadBigEndianUInt32(bytes, offset + 18);
                var durationSeconds = outTime > inTime
                    ? (outTime - inTime) / 45_000d
                    : 0;
                segments.Add(new LocalBluRayMainFeatureSegment(streamPath, durationSeconds));
            }

            offset += 2 + itemLength;
        }

        return segments;
    }

    private static ushort ReadBigEndianUInt16(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + 2 > bytes.Length)
        {
            return 0;
        }

        return (ushort)((bytes[offset] << 8) | bytes[offset + 1]);
    }

    private static uint ReadBigEndianUInt32(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + 4 > bytes.Length)
        {
            return 0;
        }

        return ((uint)bytes[offset] << 24) |
               ((uint)bytes[offset + 1] << 16) |
               ((uint)bytes[offset + 2] << 8) |
               bytes[offset + 3];
    }

    public static string GetDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (IsRemoteHttpUrl(value) && Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            var lastSegment = uri.Segments.LastOrDefault()?.Trim('/');
            if (!string.IsNullOrWhiteSpace(lastSegment))
            {
                return Uri.UnescapeDataString(lastSegment);
            }

            return uri.Host;
        }

        return Path.GetFileName(value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static string? ResolveBluRayRootFromDirectory(string directoryPath)
    {
        if (DirectoryNameEquals(directoryPath, "BDMV") && IsBluRayDirectory(directoryPath))
        {
            return Path.GetDirectoryName(directoryPath);
        }

        if (DirectoryNameEquals(directoryPath, "STREAM"))
        {
            var parentDirectory = Path.GetDirectoryName(directoryPath);
            if (!string.IsNullOrWhiteSpace(parentDirectory) &&
                DirectoryNameEquals(parentDirectory, "BDMV") &&
                IsBluRayDirectory(parentDirectory))
            {
                return Path.GetDirectoryName(parentDirectory);
            }
        }

        var childBluRayDirectory = FindChildDirectory(directoryPath, "BDMV");
        if (childBluRayDirectory is not null && IsBluRayDirectory(childBluRayDirectory))
        {
            return directoryPath;
        }

        var nestedBluRayRoot = FindSingleNestedBluRayRoot(directoryPath);
        return nestedBluRayRoot;
    }

    private static string? ResolveDvdRootFromDirectory(string directoryPath)
    {
        if (DirectoryNameEquals(directoryPath, "VIDEO_TS") && IsDvdVideoTsDirectory(directoryPath))
        {
            return Path.GetDirectoryName(directoryPath);
        }

        var childVideoTsDirectory = FindChildDirectory(directoryPath, "VIDEO_TS");
        return childVideoTsDirectory is not null && IsDvdVideoTsDirectory(childVideoTsDirectory)
            ? directoryPath
            : null;
    }

    private static bool IsBluRayStreamFile(string filePath)
    {
        if (!IsBluRayStreamExtension(filePath))
        {
            return false;
        }

        var streamDirectory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(streamDirectory) ||
            !DirectoryNameEquals(streamDirectory, "STREAM"))
        {
            return false;
        }

        var bdmvDirectory = Path.GetDirectoryName(streamDirectory);
        return !string.IsNullOrWhiteSpace(bdmvDirectory) &&
               DirectoryNameEquals(bdmvDirectory, "BDMV") &&
               IsBluRayDirectory(bdmvDirectory);
    }

    private static bool IsBluRayStreamExtension(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return string.Equals(extension, ".m2ts", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".m2t", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<int> SelectedBluRayStreamIndices(
        IReadOnlyList<LocalBluRayStreamCandidate> candidates)
    {
        var ordered = Enumerable.Range(0, candidates.Count)
            .OrderBy(index => BluRayStreamPlaybackSortKey(candidates[index].FileName).Number)
            .ThenBy(index => BluRayStreamPlaybackSortKey(candidates[index].FileName).Name, StringComparer.Ordinal)
            .ToList();
        if (ordered.Count == 0)
        {
            return [];
        }

        var bySize = SelectedBluRayMainFeatureIndices(
            ordered,
            candidates,
            candidate => Math.Max(0, candidate.FileSize));
        return bySize ?? [ordered[0]];
    }

    private static IReadOnlyList<int>? SelectedBluRayMainFeatureIndices(
        IReadOnlyList<int> ordered,
        IReadOnlyList<LocalBluRayStreamCandidate> candidates,
        Func<LocalBluRayStreamCandidate, double> metric)
    {
        var known = ordered
            .Where(index => metric(candidates[index]) > 0)
            .ToList();
        if (known.Count == 0)
        {
            return null;
        }

        var byMetric = known
            .OrderByDescending(index => metric(candidates[index]))
            .ThenBy(index => BluRayStreamPlaybackSortKey(candidates[index].FileName).Number)
            .ThenBy(index => BluRayStreamPlaybackSortKey(candidates[index].FileName).Name, StringComparer.Ordinal)
            .ToList();
        var largest = byMetric[0];
        var largestMetric = metric(candidates[largest]);
        if (largestMetric <= 0)
        {
            return null;
        }

        if (byMetric.Count == 1)
        {
            return [largest];
        }

        var secondMetric = metric(candidates[byMetric[1]]);
        if (secondMetric <= 0 || largestMetric >= secondMetric * 1.55)
        {
            return [largest];
        }

        var clusterThreshold = largestMetric * 0.72;
        var cluster = ordered
            .Where(index => metric(candidates[index]) >= clusterThreshold)
            .ToList();
        return cluster.Count == 0 ? [largest] : cluster;
    }

    private static (int Number, string Name) BluRayStreamPlaybackSortKey(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).Trim();
        var number = int.TryParse(stem, out var parsed)
            ? parsed
            : int.MaxValue;
        return (number, fileName.ToLowerInvariant());
    }

    private static string? FindAncestorBluRayDirectory(string startDirectory)
    {
        var current = startDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (DirectoryNameEquals(current, "BDMV") && IsBluRayDirectory(current))
            {
                return current;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            current = parent;
        }

        return null;
    }

    private static bool IsBluRayDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return false;
        }

        return BluRayIndexFileNames.Any(name => File.Exists(Path.Combine(directoryPath, name))) ||
               FindChildDirectory(directoryPath, "STREAM") is not null;
    }

    private static string? FindAncestorDvdVideoTsDirectory(string startDirectory)
    {
        var current = startDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (DirectoryNameEquals(current, "VIDEO_TS") && IsDvdVideoTsDirectory(current))
            {
                return current;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            current = parent;
        }

        return null;
    }

    private static bool IsDvdVideoTsDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return false;
        }

        return DvdIndexFileNames.Any(name => File.Exists(Path.Combine(directoryPath, name))) ||
               SafeEnumerateFiles(directoryPath).Any(path =>
                   string.Equals(Path.GetExtension(path), ".vob", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> SafeEnumerateFiles(string directoryPath)
    {
        try
        {
            return Directory.EnumerateFiles(directoryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsBluRayIndexFile(string fileName)
    {
        return BluRayIndexFileNames.Any(name => string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool DirectoryNameEquals(string directoryPath, string expectedName)
    {
        var directoryName = new DirectoryInfo(directoryPath).Name;
        return string.Equals(directoryName, expectedName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindChildDirectory(string directoryPath, string childName)
    {
        try
        {
            return Directory.EnumerateDirectories(directoryPath)
                .FirstOrDefault(path => DirectoryNameEquals(path, childName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FindSingleNestedBluRayRoot(string directoryPath)
    {
        try
        {
            var matches = Directory.EnumerateDirectories(directoryPath)
                .Select(child => new
                {
                    Root = child,
                    BluRayDirectory = FindChildDirectory(child, "BDMV")
                })
                .Where(static candidate =>
                    candidate.BluRayDirectory is not null &&
                    IsBluRayDirectory(candidate.BluRayDirectory))
                .Take(2)
                .ToList();

            return matches.Count == 1 ? matches[0].Root : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ResolveLocalPath(string? baseUrl, string normalizedRelativePath)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return normalizedRelativePath;
        }

        if (string.IsNullOrWhiteSpace(normalizedRelativePath))
        {
            return baseUrl;
        }

        return Path.Combine(baseUrl, normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string ResolveWebDavUrl(
        string? baseUrl,
        string normalizedRelativePath,
        WebDavAuthConfig? authConfig = null,
        bool includeCredentials = false)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return string.Empty;
        }

        var normalizedBaseUrl = MediaSourceNormalizer.NormalizeBaseUrl(MediaSourceProtocol.WebDav, baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedRelativePath))
        {
            return normalizedBaseUrl;
        }

        if (!Uri.TryCreate(AppendTrailingSlash(normalizedBaseUrl), UriKind.Absolute, out var baseUri))
        {
            return normalizedBaseUrl;
        }

        var encodedRelativePath = string.Join(
            '/',
            normalizedRelativePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));

        var resourceUri = new Uri(baseUri, encodedRelativePath);
        if (!includeCredentials ||
            authConfig is null ||
            (string.IsNullOrWhiteSpace(authConfig.Username) && string.IsNullOrEmpty(authConfig.Password)))
        {
            return resourceUri.AbsoluteUri;
        }

        var builder = new UriBuilder(resourceUri)
        {
            UserName = authConfig.Username ?? string.Empty,
            Password = authConfig.Password ?? string.Empty
        };
        return builder.Uri.AbsoluteUri;
    }

    private static string ResolveMediaServerUrl(string? baseUrl, string normalizedRelativePath)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return string.Empty;
        }

        var normalizedBaseUrl = baseUrl.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalizedRelativePath))
        {
            return normalizedBaseUrl;
        }

        if (!Uri.TryCreate(AppendTrailingSlash(normalizedBaseUrl), UriKind.Absolute, out var baseUri))
        {
            return normalizedBaseUrl;
        }

        return new Uri(baseUri, normalizedRelativePath.TrimStart('/')).AbsoluteUri;
    }

    private static string AppendQueryParameter(string url, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(value))
        {
            return url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var separator = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
        return $"{url}{separator}{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value.Trim())}";
    }

    private static string AppendQueryParameterIfMissing(string url, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(value))
        {
            return url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var needle = $"{Uri.EscapeDataString(name)}=";
        var hasParameter = uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.StartsWith(needle, StringComparison.OrdinalIgnoreCase));
        return hasParameter ? url : AppendQueryParameter(url, name, value);
    }

    private static string AppendTrailingSlash(string value)
    {
        return value.EndsWith("/", StringComparison.Ordinal) ? value : $"{value}/";
    }

    private static MediaSourceProtocol? ParseProtocolKind(string? protocolType)
    {
        return protocolType?.Trim().ToLowerInvariant() switch
        {
            "local" => MediaSourceProtocol.Local,
            "webdav" => MediaSourceProtocol.WebDav,
            "smb" => MediaSourceProtocol.Smb,
            "direct" => MediaSourceProtocol.Direct,
            "plex" => MediaSourceProtocol.Plex,
            "emby" => MediaSourceProtocol.Emby,
            "jellyfin" => MediaSourceProtocol.Jellyfin,
            _ => null
        };
    }
}
