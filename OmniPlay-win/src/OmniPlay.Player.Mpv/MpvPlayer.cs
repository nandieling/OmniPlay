using System.Globalization;
using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;
using System.Text;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Core.Models.Playback;
using OmniPlay.Player.Mpv.Interop;

namespace OmniPlay.Player.Mpv;

public sealed class MpvPlayer : IMediaPlayer
{
    private const int UnknownTrackListCount = -1;
    private readonly object initializationGate = new();
    private readonly SemaphoreSlim playerGate = new(1, 1);
    private bool initialized;
    private string? detectedLibraryName;
    private IntPtr hostHandle;
    private IntPtr playerHandle;
    private int cachedTrackListCount = UnknownTrackListCount;
    private long? cachedSelectedAudioTrackId;
    private long? cachedSelectedSubtitleTrackId;
    private IReadOnlyList<PlayerTrackInfo> cachedAudioTracks = [];
    private IReadOnlyList<PlayerTrackInfo> cachedSubtitleTracks = [];
    private readonly List<string> remoteIsoProxyRouteIds = [];
    private MountedIsoImage? mountedIsoImage;
    private string? temporaryPlaylistPath;
    private IReadOnlyList<double> activePlaylistSegmentDurations = [];

    public bool IsAvailable { get; private set; }

    public string BackendName => IsAvailable && detectedLibraryName is not null
        ? $"libmpv ({Path.GetFileName(detectedLibraryName)})"
        : "libmpv";

    public void Initialize()
    {
        lock (initializationGate)
        {
            if (initialized)
            {
                return;
            }

            initialized = true;
            IsAvailable = MpvNative.TryLoadLibrary(out detectedLibraryName);
        }
    }

    public void AttachToHost(IntPtr hostHandle)
    {
        this.hostHandle = hostHandle;
    }

    public async Task<MediaPlayerOpenResult> OpenAsync(PlaybackOpenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await playerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => OpenCore(request),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            playerGate.Release();
        }
    }

    public async Task<PlayerPlaybackState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var gateEntered = false;
        try
        {
            await playerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            if (playerHandle == IntPtr.Zero)
            {
                return PlayerPlaybackState.Empty;
            }

            return await Task.Run(ReadStateCore, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return PlayerPlaybackState.Empty;
        }
        finally
        {
            if (gateEntered)
            {
                playerGate.Release();
            }
        }
    }

    public Task SetPausedAsync(bool isPaused, CancellationToken cancellationToken = default)
    {
        return RunPlayerActionAsync(
            handle => MpvNative.SetPropertyString(handle, "pause", isPaused ? "yes" : "no"),
            cancellationToken);
    }

    public Task SeekAsync(double positionSeconds, CancellationToken cancellationToken = default)
    {
        var seconds = Math.Max(0, positionSeconds).ToString("0.###", CultureInfo.InvariantCulture);
        return RunPlayerActionAsync(
            handle => MpvNative.Command(handle, "seek", seconds, "absolute"),
            cancellationToken);
    }

    public Task SetMutedAsync(bool isMuted, CancellationToken cancellationToken = default)
    {
        return RunPlayerActionAsync(
            handle => MpvNative.SetPropertyString(handle, "mute", isMuted ? "yes" : "no"),
            cancellationToken);
    }

    public Task SetVolumeAsync(double volumePercent, CancellationToken cancellationToken = default)
    {
        var normalized = Math.Clamp(volumePercent, 0, 100).ToString("0.###", CultureInfo.InvariantCulture);
        return RunPlayerActionAsync(
            handle => MpvNative.SetPropertyString(handle, "volume", normalized),
            cancellationToken);
    }

    public Task SelectAudioTrackAsync(long? trackId, CancellationToken cancellationToken = default)
    {
        return RunPlayerActionAsync(
            handle => MpvNative.SetPropertyString(
                handle,
                "aid",
                trackId?.ToString(CultureInfo.InvariantCulture) ?? "no"),
            cancellationToken);
    }

    public Task SelectSubtitleTrackAsync(long? trackId, CancellationToken cancellationToken = default)
    {
        return RunPlayerActionAsync(
            handle => MpvNative.SetPropertyString(
                handle,
                "sid",
                trackId?.ToString(CultureInfo.InvariantCulture) ?? "no"),
            cancellationToken);
    }

    public Task<bool> LoadExternalSubtitleAsync(string subtitlePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subtitlePath) || !File.Exists(subtitlePath))
        {
            return Task.FromResult(false);
        }

        var title = Path.GetFileName(subtitlePath);
        return RunPlayerFunctionAsync(
            handle =>
            {
                var result = MpvNative.Command(handle, "sub-add", subtitlePath, "select", title);
                if (result >= 0)
                {
                    InvalidateTrackCache();
                }

                return result >= 0;
            },
            false,
            cancellationToken);
    }

    public Task SetSubtitleDelayAsync(double delaySeconds, CancellationToken cancellationToken = default)
    {
        var normalized = delaySeconds.ToString("0.###", CultureInfo.InvariantCulture);
        return RunPlayerActionAsync(
            handle => MpvNative.SetPropertyString(
                handle,
                "sub-delay",
                normalized),
            cancellationToken);
    }

    public Task SetSubtitleFontSizeAsync(int fontSize, CancellationToken cancellationToken = default)
    {
        var normalized = Math.Clamp(fontSize, 8, 96).ToString(CultureInfo.InvariantCulture);
        return RunPlayerActionAsync(
            handle => MpvNative.SetPropertyString(handle, "sub-font-size", normalized),
            cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await playerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(DestroyPlayer, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            playerGate.Release();
        }
    }

    private MediaPlayerOpenResult OpenCore(PlaybackOpenRequest request)
    {
        Initialize();
        var filePath = request.PlaybackPath;
        filePath = MediaSourcePathResolver.ResolvePlayableLocation(filePath);

        if (!MediaSourcePathResolver.IsPlayableLocation(filePath))
        {
            return MediaPlayerOpenResult.Failure("文件不存在，无法开始播放。");
        }

        if (!IsAvailable)
        {
            return MediaPlayerOpenResult.Failure("未找到 libmpv 原生库，请将 libmpv-2.dll 放到程序目录或 PATH 后重试。");
        }

        if (hostHandle == IntPtr.Zero)
        {
            return MediaPlayerOpenResult.Failure("播放器宿主句柄尚未准备好。");
        }

        try
        {
            DestroyPlayer();

            playerHandle = MpvNative.Create();
            if (playerHandle == IntPtr.Zero)
            {
                return MediaPlayerOpenResult.Failure("libmpv 创建播放器实例失败。");
            }

            SetOption("terminal", "no");
            SetOption("input-default-bindings", "yes");
            SetOption("input-vo-keyboard", "yes");
            SetOption("force-window", "yes");
            SetOption("keep-open", "yes");
            SetOption("panscan", "1.0");
            SetOption("wid", hostHandle.ToInt64().ToString(CultureInfo.InvariantCulture));
            if (TryCreateRemoteHttpHeaderFields(filePath, out var remoteHeaders))
            {
                SetOption("http-header-fields", remoteHeaders);
            }
            var bluRayTarget = ResolveBluRayPlaybackTarget(
                filePath,
                request.MediaFileName,
                request.EffectiveDisplayPath);
            if (!string.IsNullOrWhiteSpace(bluRayTarget?.DevicePath))
            {
                SetOption("bluray-device", bluRayTarget.DevicePath);
            }
            if (!string.IsNullOrWhiteSpace(bluRayTarget?.DvdDevicePath) &&
                bluRayTarget.PlaybackPath.StartsWith("dvd://", StringComparison.OrdinalIgnoreCase))
            {
                SetOption("dvd-device", bluRayTarget.DvdDevicePath);
            }
            if (bluRayTarget is not null)
            {
                Trace.WriteLine(
                    $"[MpvPlayer] BluRay target: PlaybackPath={bluRayTarget.PlaybackPath}, " +
                    $"UsePlaylist={bluRayTarget.UsePlaylist}, DevicePath={bluRayTarget.DevicePath ?? "<none>"}, " +
                    $"FallbackPlaybackPath={bluRayTarget.FallbackPlaybackPath ?? "<none>"}, " +
                    $"FallbackUsesPlaylist={bluRayTarget.FallbackUsesPlaylist}, " +
                    $"FinalFallbackPlaybackPath={bluRayTarget.FinalFallbackPlaybackPath ?? "<none>"}, " +
                    $"DvdDevicePath={bluRayTarget.DvdDevicePath ?? "<none>"}");
            }

            var initResult = MpvNative.Initialize(playerHandle);
            if (initResult < 0)
            {
                DestroyPlayer();
                return MediaPlayerOpenResult.Failure($"libmpv 初始化失败，错误码 {initResult}。");
            }

            var loadedUsesPlaylist = bluRayTarget?.UsePlaylist == true;
            var loadedSegmentDurations = loadedUsesPlaylist
                ? bluRayTarget?.PlaybackSegmentDurations ?? []
                : [];

            var loadResult = bluRayTarget is not null
                ? LoadBluRayTarget(playerHandle, bluRayTarget)
                : MpvNative.Command(playerHandle, "loadfile", filePath);
            Trace.WriteLine($"[MpvPlayer] Initial load result: {loadResult}");
            if (loadResult < 0 &&
                bluRayTarget is not null &&
                !string.IsNullOrWhiteSpace(bluRayTarget.FallbackPlaybackPath) &&
                !string.Equals(bluRayTarget.FallbackPlaybackPath, bluRayTarget.PlaybackPath, StringComparison.OrdinalIgnoreCase))
            {
                loadResult = LoadBluRayFallbackTarget(playerHandle, bluRayTarget);
                loadedUsesPlaylist = bluRayTarget.FallbackUsesPlaylist;
                loadedSegmentDurations = loadedUsesPlaylist ? bluRayTarget.FallbackSegmentDurations : [];
                Trace.WriteLine($"[MpvPlayer] Fallback load result: {loadResult}");
            }
            if (loadResult < 0 && !string.IsNullOrWhiteSpace(bluRayTarget?.DvdDevicePath))
            {
                MpvNative.SetPropertyString(playerHandle, "dvd-device", bluRayTarget.DvdDevicePath);
                loadResult = MpvNative.Command(playerHandle, "loadfile", "dvd://");
                loadedUsesPlaylist = false;
                loadedSegmentDurations = [];
                Trace.WriteLine($"[MpvPlayer] DVD fallback load result: {loadResult}");
            }
            if (loadResult < 0 &&
                bluRayTarget is not null &&
                !string.Equals(bluRayTarget.PlaybackPath, "bd://", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(bluRayTarget.DevicePath))
            {
                loadResult = MpvNative.Command(playerHandle, "loadfile", "bd://");
                loadedUsesPlaylist = false;
                loadedSegmentDurations = [];
                Trace.WriteLine($"[MpvPlayer] BluRay device fallback load result: {loadResult}");
            }
            if (loadResult < 0 &&
                !string.IsNullOrWhiteSpace(bluRayTarget?.FinalFallbackPlaybackPath) &&
                !string.Equals(bluRayTarget.FinalFallbackPlaybackPath, bluRayTarget.PlaybackPath, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(bluRayTarget.FinalFallbackPlaybackPath, bluRayTarget.FallbackPlaybackPath, StringComparison.OrdinalIgnoreCase))
            {
                loadResult = MpvNative.Command(playerHandle, "loadfile", bluRayTarget.FinalFallbackPlaybackPath);
                loadedUsesPlaylist = false;
                loadedSegmentDurations = [];
                Trace.WriteLine($"[MpvPlayer] Final fallback load result: {loadResult}");
            }

            activePlaylistSegmentDurations = loadResult >= 0 && loadedUsesPlaylist ? loadedSegmentDurations : [];
            if (loadResult < 0)
            {
                DestroyPlayer();
                return MediaPlayerOpenResult.Failure($"libmpv 加载文件失败，错误码 {loadResult}。");
            }

            return MediaPlayerOpenResult.Success($"已通过 {BackendName} 在应用内打开视频。");
        }
        catch (DllNotFoundException)
        {
            DestroyPlayer();
            return MediaPlayerOpenResult.Failure("已复制 libmpv-2.dll，但运行时仍无法解析其依赖项。");
        }
        catch (Exception ex)
        {
            DestroyPlayer();
            return MediaPlayerOpenResult.Failure($"libmpv 播放启动失败：{ex.Message}");
        }
    }

    private BluRayPlaybackTarget? ResolveBluRayPlaybackTarget(
        string filePath,
        string? mediaFileName,
        string? displayPath)
    {
        if (MediaSourcePathResolver.IsRemoteHttpUrl(filePath))
        {
            if (IsRemoteIsoPlaybackPath(filePath, mediaFileName, displayPath))
            {
                return CreateRemoteIsoPlaybackTarget(filePath);
            }

            return null;
        }

        var localBluRayRoot = MediaSourcePathResolver.ResolveLocalBluRayRoot(filePath);
        if (!string.IsNullOrWhiteSpace(localBluRayRoot))
        {
            return CreateLocalBluRayPlaybackTarget(
                localBluRayRoot,
                isIsoImage: false,
                fallbackPlaybackPath: File.Exists(filePath) ? Path.GetFullPath(filePath) : null);
        }

        var localDvdRoot = MediaSourcePathResolver.ResolveLocalDvdRoot(filePath);
        if (!string.IsNullOrWhiteSpace(localDvdRoot))
        {
            return CreateLocalDvdPlaybackTarget(
                localDvdRoot,
                isIsoImage: false,
                fallbackPlaybackPath: File.Exists(filePath) ? Path.GetFullPath(filePath) : null);
        }

        if (!string.Equals(Path.GetExtension(filePath), ".iso", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(filePath))
        {
            return null;
        }

        var isoPath = Path.GetFullPath(filePath);
        var mountedImage = TryMountIsoImage(isoPath);
        if (mountedImage is not null)
        {
            mountedIsoImage = mountedImage;
            var mountedBluRayRoot = MediaSourcePathResolver.ResolveLocalBluRayRoot(mountedImage.RootPath);
            if (!string.IsNullOrWhiteSpace(mountedBluRayRoot))
            {
                return CreateLocalBluRayPlaybackTarget(
                    mountedBluRayRoot,
                    isIsoImage: true,
                    fallbackPlaybackPath: isoPath);
            }

            var mountedDvdRoot = MediaSourcePathResolver.ResolveLocalDvdRoot(mountedImage.RootPath);
            if (!string.IsNullOrWhiteSpace(mountedDvdRoot))
            {
                return CreateLocalDvdPlaybackTarget(
                    mountedDvdRoot,
                    isIsoImage: true,
                    fallbackPlaybackPath: isoPath);
            }
        }

        return new BluRayPlaybackTarget(
            DevicePath: isoPath,
            PlaybackPath: "bd://longest",
            UsePlaylist: false,
            IsIsoImage: true,
            FallbackPlaybackPath: "bd://",
            FallbackUsesPlaylist: false,
            FinalFallbackPlaybackPath: isoPath,
            DvdDevicePath: isoPath,
            PlaybackSegmentDurations: [],
            FallbackSegmentDurations: []);
    }

    private BluRayPlaybackTarget CreateRemoteIsoPlaybackTarget(string sourceUrl)
    {
        var registration = RemoteIsoStreamProxy.Shared
            .PrepareAsync(sourceUrl)
            .GetAwaiter()
            .GetResult();
        remoteIsoProxyRouteIds.AddRange(registration.RouteIds);

        if (registration.Urls.Count == 0)
        {
            RemoteIsoStreamProxy.Shared.Unregister(registration.RouteIds);
            remoteIsoProxyRouteIds.RemoveAll(registration.RouteIds.Contains);
            throw new InvalidDataException("远程 ISO 代理没有生成可播放地址。");
        }

        var playbackPath = registration.Urls[0];
        var usePlaylist = false;
        if (registration.Urls.Count > 1)
        {
            if (!TryCreateTemporaryM3uPlaylist(registration.Urls, out playbackPath))
            {
                RemoteIsoStreamProxy.Shared.Unregister(registration.RouteIds);
                remoteIsoProxyRouteIds.RemoveAll(registration.RouteIds.Contains);
                throw new IOException("无法创建远程蓝光分段播放列表。");
            }

            usePlaylist = true;
        }

        return new BluRayPlaybackTarget(
            DevicePath: null,
            PlaybackPath: playbackPath,
            UsePlaylist: usePlaylist,
            IsIsoImage: true,
            FallbackPlaybackPath: null,
            FallbackUsesPlaylist: false,
            FinalFallbackPlaybackPath: null,
            DvdDevicePath: null,
            PlaybackSegmentDurations: [],
            FallbackSegmentDurations: []);
    }

    private static bool IsRemoteIsoPlaybackPath(
        string filePath,
        string? mediaFileName,
        string? displayPath)
    {
        if (!string.IsNullOrWhiteSpace(mediaFileName) &&
            mediaFileName.Trim().EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(filePath, UriKind.Absolute, out var sourceUri))
            {
                var sourcePath = sourceUri.AbsolutePath.TrimEnd('/');
                if (sourcePath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ||
                    (sourcePath.Contains("/api/playback/files/", StringComparison.OrdinalIgnoreCase) &&
                     sourcePath.EndsWith("/stream", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            if (Uri.TryCreate(displayPath, UriKind.Absolute, out var displayUri) &&
                displayUri.AbsolutePath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static BluRayPlaybackTarget CreateLocalDvdPlaybackTarget(
        string devicePath,
        bool isIsoImage,
        string? fallbackPlaybackPath)
    {
        return new BluRayPlaybackTarget(
            DevicePath: null,
            PlaybackPath: "dvd://",
            UsePlaylist: false,
            IsIsoImage: isIsoImage,
            FallbackPlaybackPath: fallbackPlaybackPath,
            FallbackUsesPlaylist: false,
            FinalFallbackPlaybackPath: null,
            DvdDevicePath: devicePath,
            PlaybackSegmentDurations: [],
            FallbackSegmentDurations: []);
    }

    private BluRayPlaybackTarget CreateLocalBluRayPlaybackTarget(
        string devicePath,
        bool isIsoImage,
        string? fallbackPlaybackPath)
    {
        var mainFeatureSegments = MediaSourcePathResolver.ResolveLocalBluRayMainFeatureSegments(devicePath);
        var mainFeaturePaths = mainFeatureSegments.Select(static segment => segment.Path).ToList();
        var mainFeatureDurations = mainFeatureSegments.Select(static segment => segment.DurationSeconds).ToList();
        string? streamFallbackPath = null;
        var streamFallbackUsesPlaylist = false;
        if (mainFeaturePaths.Count == 1)
        {
            streamFallbackPath = mainFeaturePaths[0];
        }
        else if (mainFeaturePaths.Count > 1 &&
            TryCreateTemporaryM3uPlaylist(mainFeaturePaths, out var playlistPath))
        {
            streamFallbackPath = playlistPath;
            streamFallbackUsesPlaylist = true;
        }

        if (!string.IsNullOrWhiteSpace(streamFallbackPath))
        {
            return new BluRayPlaybackTarget(
                DevicePath: devicePath,
                PlaybackPath: streamFallbackPath,
                UsePlaylist: streamFallbackUsesPlaylist,
                IsIsoImage: isIsoImage,
                FallbackPlaybackPath: "bd://longest",
                FallbackUsesPlaylist: false,
                FinalFallbackPlaybackPath: fallbackPlaybackPath,
                DvdDevicePath: null,
                PlaybackSegmentDurations: streamFallbackUsesPlaylist ? mainFeatureDurations : [],
                FallbackSegmentDurations: []);
        }

        return new BluRayPlaybackTarget(
            DevicePath: devicePath,
            PlaybackPath: "bd://longest",
            UsePlaylist: false,
            IsIsoImage: isIsoImage,
            FallbackPlaybackPath: streamFallbackPath ?? fallbackPlaybackPath,
            FallbackUsesPlaylist: streamFallbackUsesPlaylist,
            FinalFallbackPlaybackPath: streamFallbackPath is null ? null : fallbackPlaybackPath,
            DvdDevicePath: null,
            PlaybackSegmentDurations: [],
            FallbackSegmentDurations: streamFallbackUsesPlaylist ? mainFeatureDurations : []);
    }

    private bool TryCreateTemporaryM3uPlaylist(IReadOnlyList<string> filePaths, out string playlistPath)
    {
        playlistPath = string.Empty;
        if (filePaths.Count == 0)
        {
            return false;
        }

        try
        {
            ReleaseTemporaryPlaylist();

            playlistPath = Path.Combine(
                Path.GetTempPath(),
                $"omniplay-bdmv-{Guid.NewGuid():N}.m3u8");
            List<string> lines = ["#EXTM3U"];
            foreach (var path in filePaths)
            {
                lines.Add($"#EXTINF:-1,{Path.GetFileName(path)}");
                lines.Add(path);
            }

            File.WriteAllLines(playlistPath, lines, Encoding.UTF8);
            temporaryPlaylistPath = playlistPath;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            playlistPath = string.Empty;
            return false;
        }
    }

    private static int LoadBluRayTarget(IntPtr handle, BluRayPlaybackTarget target)
    {
        return target.UsePlaylist
            ? MpvNative.Command(handle, "loadlist", target.PlaybackPath)
            : MpvNative.Command(handle, "loadfile", target.PlaybackPath);
    }

    private static int LoadBluRayFallbackTarget(IntPtr handle, BluRayPlaybackTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.FallbackPlaybackPath))
        {
            return -1;
        }

        return target.FallbackUsesPlaylist
            ? MpvNative.Command(handle, "loadlist", target.FallbackPlaybackPath)
            : MpvNative.Command(handle, "loadfile", target.FallbackPlaybackPath);
    }

    private static bool TryCreateRemoteHttpHeaderFields(string filePath, out string headerFields)
    {
        headerFields = string.Empty;
        if (!Uri.TryCreate(filePath, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var token = ReadQueryParameter(uri.Query, "X-Plex-Token");
        if (!string.IsNullOrWhiteSpace(token))
        {
            headerFields = string.Join(
                ',',
                "X-Plex-Product: OmniPlay",
                "X-Plex-Version: 1.0",
                "X-Plex-Client-Identifier: omniplay-windows",
                "X-Plex-Device-Name: OmniPlay",
                "X-Plex-Platform: Windows",
                $"X-Plex-Token: {token.Trim()}");
            return true;
        }

        token = ReadQueryParameter(uri.Query, "api_key");
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        headerFields = string.Join(
            ',',
            $"X-Emby-Token: {token.Trim()}",
            $"X-MediaBrowser-Token: {token.Trim()}");
        return true;
    }

    private static string? ReadQueryParameter(string query, string name)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var rawName = separator >= 0 ? part[..separator] : part;
            if (!string.Equals(Uri.UnescapeDataString(rawName), name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rawValue = separator >= 0 ? part[(separator + 1)..] : string.Empty;
            return Uri.UnescapeDataString(rawValue.Replace("+", "%20", StringComparison.Ordinal));
        }

        return null;
    }

    private PlayerPlaybackState ReadStateCore()
    {
        var duration = ParseDouble(MpvNative.GetPropertyString(playerHandle, "duration"));
        var position = ParseDouble(MpvNative.GetPropertyString(playerHandle, "time-pos"));
        var pause = ParseFlag(MpvNative.GetPropertyString(playerHandle, "pause"));
        var eofReached = ParseFlag(MpvNative.GetPropertyString(playerHandle, "eof-reached"));
        var mute = ParseFlag(MpvNative.GetPropertyString(playerHandle, "mute"));
        var volumeText = MpvNative.GetPropertyString(playerHandle, "volume");
        var volume = string.IsNullOrWhiteSpace(volumeText) ? 100 : ParseDouble(volumeText);
        var selectedAudioTrackId = ParseLong(MpvNative.GetPropertyString(playerHandle, "aid"));
        var selectedSubtitleTrackId = ParseLong(MpvNative.GetPropertyString(playerHandle, "sid"));
        var subtitleDelay = ParseDouble(MpvNative.GetPropertyString(playerHandle, "sub-delay"));
        var subtitleFontSize = ParseInt(MpvNative.GetPropertyString(playerHandle, "sub-font-size"));
        var trackListCount = ParseInt(MpvNative.GetPropertyString(playerHandle, "track-list/count"));
        var (audioTracks, subtitleTracks) = ReadCachedTracks(
            trackListCount,
            selectedAudioTrackId,
            selectedSubtitleTrackId);
        var (effectivePosition, effectiveDuration) = ResolvePlaylistPositionAndDuration(position, duration);

        return new PlayerPlaybackState
        {
            HasMedia = effectiveDuration > 0 || effectivePosition > 0 || audioTracks.Count > 0 || subtitleTracks.Count > 0,
            DurationSeconds = effectiveDuration,
            PositionSeconds = effectivePosition,
            IsPaused = pause,
            IsPlaybackCompleted = eofReached,
            IsMuted = mute,
            VolumePercent = Math.Clamp(volume, 0, 100),
            AudioTracks = audioTracks,
            SubtitleTracks = subtitleTracks,
            SubtitleDelaySeconds = subtitleDelay,
            SubtitleFontSize = subtitleFontSize > 0 ? subtitleFontSize : 16
        };
    }

    private (double PositionSeconds, double DurationSeconds) ResolvePlaylistPositionAndDuration(
        double positionSeconds,
        double durationSeconds)
    {
        var segmentDurations = activePlaylistSegmentDurations;
        if (segmentDurations.Count <= 1 || segmentDurations.Any(static duration => duration <= 0))
        {
            return (positionSeconds, durationSeconds);
        }

        var playlistPosition = ParseInt(MpvNative.GetPropertyString(playerHandle, "playlist-pos"));
        if (playlistPosition < 0 || playlistPosition >= segmentDurations.Count)
        {
            return (positionSeconds, Math.Max(durationSeconds, segmentDurations.Sum()));
        }

        var priorSegmentsDuration = segmentDurations.Take(playlistPosition).Sum();
        var currentSegmentDuration = segmentDurations[playlistPosition];
        var totalDuration = segmentDurations.Sum();
        var currentSegmentPosition = Math.Clamp(positionSeconds, 0, currentSegmentDuration);
        return (priorSegmentsDuration + currentSegmentPosition, totalDuration);
    }

    private Task RunPlayerActionAsync(Action<IntPtr> action, CancellationToken cancellationToken)
    {
        return RunPlayerFunctionAsync(
            handle =>
            {
                action(handle);
                return true;
            },
            false,
            cancellationToken);
    }

    private async Task<T> RunPlayerFunctionAsync<T>(
        Func<IntPtr, T> function,
        T fallback,
        CancellationToken cancellationToken)
    {
        await playerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var handle = playerHandle;
            if (handle == IntPtr.Zero)
            {
                return fallback;
            }

            return await Task.Run(() => function(handle), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            playerGate.Release();
        }
    }

    private void DestroyPlayer()
    {
        activePlaylistSegmentDurations = [];
        if (playerHandle == IntPtr.Zero)
        {
            InvalidateTrackCache();
            ReleaseRemoteIsoProxyRoutes();
            ReleaseMountedIsoImage();
            ReleaseTemporaryPlaylist();
            return;
        }

        var handle = playerHandle;
        playerHandle = IntPtr.Zero;
        try
        {
            MpvNative.TerminateDestroy(handle);
        }
        finally
        {
            InvalidateTrackCache();
            ReleaseRemoteIsoProxyRoutes();
            ReleaseMountedIsoImage();
            ReleaseTemporaryPlaylist();
        }
    }

    private void ReleaseRemoteIsoProxyRoutes()
    {
        if (remoteIsoProxyRouteIds.Count == 0)
        {
            return;
        }

        RemoteIsoStreamProxy.Shared.Unregister(remoteIsoProxyRouteIds);
        remoteIsoProxyRouteIds.Clear();
    }

    private void ReleaseTemporaryPlaylist()
    {
        var playlistPath = temporaryPlaylistPath;
        temporaryPlaylistPath = null;
        if (string.IsNullOrWhiteSpace(playlistPath))
        {
            return;
        }

        try
        {
            if (File.Exists(playlistPath))
            {
                File.Delete(playlistPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时播放列表清理失败不影响播放器生命周期。
        }
    }

    private MountedIsoImage? TryMountIsoImage(string isoPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        const string script = """
            $ErrorActionPreference = 'Stop'
            $path = [System.IO.Path]::GetFullPath($args[0])
            $disk = Get-DiskImage -ImagePath $path -ErrorAction SilentlyContinue
            $alreadyAttached = $false
            if ($null -ne $disk) {
                $alreadyAttached = [bool]$disk.Attached
            }
            if (-not $alreadyAttached) {
                $disk = Mount-DiskImage -ImagePath $path -StorageType ISO -Access ReadOnly -PassThru
            } else {
                $disk = Get-DiskImage -ImagePath $path
            }

            function Test-BluRayRoot([string]$root) {
                if ([string]::IsNullOrWhiteSpace($root)) { return $false }
                return (Test-Path -LiteralPath $root -PathType Container) -and
                    (Test-Path -LiteralPath (Join-Path $root 'BDMV') -PathType Container)
            }

            $fallbackRoot = $null
            for ($attempt = 0; $attempt -lt 40; $attempt++) {
                if ($attempt -gt 0) {
                    Start-Sleep -Milliseconds 250
                }
                $disk = Get-DiskImage -ImagePath $path -ErrorAction SilentlyContinue

                $volumeRoots = @()
                if ($null -ne $disk) {
                    $volumeRoots += @(
                        $disk |
                            Get-Volume -ErrorAction SilentlyContinue |
                            Where-Object { $_.DriveLetter } |
                            ForEach-Object { [string]$_.DriveLetter + ':\' }
                    )

                    $volumeRoots += @(
                        $disk |
                            Get-Disk -ErrorAction SilentlyContinue |
                            Get-Partition -ErrorAction SilentlyContinue |
                        Where-Object { $_.DriveLetter } |
                        ForEach-Object { [string]$_.DriveLetter + ':\' }
                    )
                }

                $volumeRoots = @(
                    $volumeRoots |
                        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                        Select-Object -Unique
                )

                foreach ($root in $volumeRoots) {
                    if ($null -eq $fallbackRoot -and
                        (Test-Path -LiteralPath $root -PathType Container)) {
                        $fallbackRoot = $root
                    }

                    if (Test-BluRayRoot $root) {
                        [pscustomobject]@{
                            Root = $root
                            ShouldDismount = (-not $alreadyAttached)
                        } | ConvertTo-Json -Compress
                        exit 0
                    }
                }
            }

            if ($null -ne $fallbackRoot) {
                [pscustomobject]@{
                    Root = $fallbackRoot
                    ShouldDismount = (-not $alreadyAttached)
                } | ConvertTo-Json -Compress
                exit 0
            }

            throw 'mounted ISO has no accessible drive letter'
            """;

        if (!TryRunPowerShell(script, isoPath, timeoutMilliseconds: 60_000, out var output))
        {
            const string legacyScript = """
                $ErrorActionPreference = 'Stop'
                $path = [System.IO.Path]::GetFullPath($args[0])
                $disk = Get-DiskImage -ImagePath $path -ErrorAction SilentlyContinue
                $alreadyAttached = $false
                if ($null -ne $disk) {
                    $alreadyAttached = [bool]$disk.Attached
                }
                if (-not $alreadyAttached) {
                    $disk = Mount-DiskImage -ImagePath $path -PassThru
                    Start-Sleep -Seconds 1
                } else {
                    $disk = Get-DiskImage -ImagePath $path
                }
                $volume = $disk | Get-Volume | Where-Object { $_.DriveLetter } | Select-Object -First 1
                if ($null -eq $volume) {
                    $volume = Get-DiskImage -ImagePath $path | Get-Volume | Where-Object { $_.DriveLetter } | Select-Object -First 1
                }
                if ($null -eq $volume -or -not $volume.DriveLetter) {
                    throw 'mounted ISO has no drive letter'
                }
                [pscustomobject]@{
                    Root = ([string]$volume.DriveLetter + ':\')
                    ShouldDismount = (-not $alreadyAttached)
                } | ConvertTo-Json -Compress
                """;

            if (!TryRunPowerShell(legacyScript, isoPath, timeoutMilliseconds: 20_000, out output))
            {
                return null;
            }
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().FirstOrDefault()
                : document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("Root", out var rootProperty))
            {
                return null;
            }

            var rootPath = rootProperty.GetString();
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return null;
            }

            var shouldDismount = root.TryGetProperty("ShouldDismount", out var shouldDismountProperty) &&
                                  shouldDismountProperty.ValueKind == JsonValueKind.True;
            return new MountedIsoImage(isoPath, rootPath, shouldDismount);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void ReleaseMountedIsoImage()
    {
        var image = mountedIsoImage;
        mountedIsoImage = null;
        if (image is null || !image.ShouldDismount || !OperatingSystem.IsWindows())
        {
            return;
        }

        const string script = """
            $ErrorActionPreference = 'SilentlyContinue'
            for ($attempt = 0; $attempt -lt 6; $attempt++) {
                Dismount-DiskImage -ImagePath $args[0]
                Start-Sleep -Milliseconds 300
                $disk = Get-DiskImage -ImagePath $args[0] -ErrorAction SilentlyContinue
                if ($null -eq $disk -or -not [bool]$disk.Attached) {
                    exit 0
                }
            }
            """;
        _ = TryRunPowerShell(script, image.IsoPath, timeoutMilliseconds: 10_000, out _);
    }

    private static bool TryRunPowerShell(
        string script,
        string pathArgument,
        int timeoutMilliseconds,
        out string output)
    {
        output = string.Empty;
        foreach (var executable in new[] { "powershell.exe", "pwsh.exe" })
        {
            try
            {
                using var process = new Process();
                process.StartInfo.FileName = executable;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                var encodedCommand = Convert.ToBase64String(
                    Encoding.Unicode.GetBytes($"$isoPath = @'\n{pathArgument}\n'@\n& {{ {script} }} $isoPath"));
                process.StartInfo.ArgumentList.Add("-NoProfile");
                process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
                process.StartInfo.ArgumentList.Add("Bypass");
                process.StartInfo.ArgumentList.Add("-EncodedCommand");
                process.StartInfo.ArgumentList.Add(encodedCommand);

                if (!process.Start())
                {
                    continue;
                }

                var standardOutputTask = process.StandardOutput.ReadToEndAsync();
                var standardErrorTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMilliseconds))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    continue;
                }

                var standardOutput = standardOutputTask.GetAwaiter().GetResult();
                _ = standardErrorTask.GetAwaiter().GetResult();
                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(standardOutput))
                {
                    continue;
                }

                output = standardOutput.Trim();
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException)
            {
            }
        }

        return false;
    }

    private (IReadOnlyList<PlayerTrackInfo> AudioTracks, IReadOnlyList<PlayerTrackInfo> SubtitleTracks) ReadCachedTracks(
        int trackListCount,
        long? selectedAudioTrackId,
        long? selectedSubtitleTrackId)
    {
        if (trackListCount <= 0)
        {
            cachedTrackListCount = 0;
            cachedSelectedAudioTrackId = selectedAudioTrackId;
            cachedSelectedSubtitleTrackId = selectedSubtitleTrackId;
            cachedAudioTracks = [];
            cachedSubtitleTracks = [];
            return (cachedAudioTracks, cachedSubtitleTracks);
        }

        if (trackListCount != cachedTrackListCount)
        {
            cachedTrackListCount = trackListCount;
            cachedSelectedAudioTrackId = selectedAudioTrackId;
            cachedSelectedSubtitleTrackId = selectedSubtitleTrackId;
            cachedAudioTracks = ReadTracks("audio", "\u97F3\u8F68", selectedAudioTrackId, trackListCount);
            cachedSubtitleTracks = ReadTracks("sub", "\u5B57\u5E55", selectedSubtitleTrackId, trackListCount);
            return (cachedAudioTracks, cachedSubtitleTracks);
        }

        if (cachedSelectedAudioTrackId != selectedAudioTrackId)
        {
            cachedSelectedAudioTrackId = selectedAudioTrackId;
            cachedAudioTracks = ApplySelectedTrack(cachedAudioTracks, selectedAudioTrackId);
        }

        if (cachedSelectedSubtitleTrackId != selectedSubtitleTrackId)
        {
            cachedSelectedSubtitleTrackId = selectedSubtitleTrackId;
            cachedSubtitleTracks = ApplySelectedTrack(cachedSubtitleTracks, selectedSubtitleTrackId);
        }

        return (cachedAudioTracks, cachedSubtitleTracks);
    }

    private IReadOnlyList<PlayerTrackInfo> ReadTracks(
        string trackType,
        string fallbackPrefix,
        long? selectedTrackId,
        int trackListCount)
    {
        if (trackListCount <= 0)
        {
            return [];
        }

        var tracks = new List<PlayerTrackInfo>(trackListCount);

        for (var index = 0; index < trackListCount; index++)
        {
            var propertyPrefix = $"track-list/{index}";
            var type = MpvNative.GetPropertyString(playerHandle, $"{propertyPrefix}/type");
            if (!string.Equals(type, trackType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var trackId = ParseLong(MpvNative.GetPropertyString(playerHandle, $"{propertyPrefix}/id"));
            if (trackId is null)
            {
                continue;
            }

            var title = MpvNative.GetPropertyString(playerHandle, $"{propertyPrefix}/title");
            var language = MpvNative.GetPropertyString(playerHandle, $"{propertyPrefix}/lang");
            var codec = BuildCodecDisplaySource(
                MpvNative.GetPropertyString(playerHandle, $"{propertyPrefix}/codec"),
                MpvNative.GetPropertyString(playerHandle, $"{propertyPrefix}/codec-profile"),
                MpvNative.GetPropertyString(playerHandle, $"{propertyPrefix}/decoder-desc"),
                title);
            var audioChannels = string.Equals(trackType, "audio", StringComparison.OrdinalIgnoreCase)
                ? FirstNonWhiteSpace(
                    MpvNative.GetPropertyString(playerHandle, $"{propertyPrefix}/demux-channel-count"),
                    MpvNative.GetPropertyString(playerHandle, $"{propertyPrefix}/demux-channels"),
                    MpvNative.GetPropertyString(playerHandle, $"{propertyPrefix}/audio-channels"))
                : null;
            var isDefault = ParseFlag(MpvNative.GetPropertyString(playerHandle, $"{propertyPrefix}/default"));
            var isForced = ParseFlag(MpvNative.GetPropertyString(playerHandle, $"{propertyPrefix}/forced"));
            var isExternal = ParseFlag(MpvNative.GetPropertyString(playerHandle, $"{propertyPrefix}/external"));

            tracks.Add(new PlayerTrackInfo(
                trackType,
                trackId,
                PlayerTrackDisplayNameFormatter.Format(
                    fallbackPrefix,
                    trackId.Value,
                    title,
                    language,
                    codec,
                    audioChannels,
                    isDefault,
                    isForced,
                    isExternal),
                trackId == selectedTrackId,
                false,
                language?.Trim() ?? string.Empty));
        }

        return tracks;
    }

    private static string? BuildCodecDisplaySource(params string?[] values)
    {
        var parts = values
            .Select(static value => value?.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return parts.Length == 0 ? null : string.Join(' ', parts);
    }

    private static string? FirstNonWhiteSpace(params string?[] values)
    {
        foreach (var value in values)
        {
            var trimmed = value?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                return trimmed;
            }
        }

        return null;
    }

    private static IReadOnlyList<PlayerTrackInfo> ApplySelectedTrack(
        IReadOnlyList<PlayerTrackInfo> tracks,
        long? selectedTrackId)
    {
        if (tracks.Count == 0)
        {
            return tracks;
        }

        var changed = false;
        var updated = new PlayerTrackInfo[tracks.Count];

        for (var index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index];
            var isSelected = track.TrackId == selectedTrackId;
            updated[index] = track.IsSelected == isSelected
                ? track
                : track with { IsSelected = isSelected };
            changed |= !ReferenceEquals(updated[index], track);
        }

        return changed ? updated : tracks;
    }

    private void InvalidateTrackCache()
    {
        cachedTrackListCount = UnknownTrackListCount;
        cachedSelectedAudioTrackId = null;
        cachedSelectedSubtitleTrackId = null;
        cachedAudioTracks = [];
        cachedSubtitleTracks = [];
    }

    private void SetOption(string name, string value)
    {
        if (playerHandle == IntPtr.Zero)
        {
            return;
        }

        MpvNative.SetOptionString(playerHandle, name, value);
    }

    private static double ParseDouble(string? value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static int ParseInt(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static long? ParseLong(string? value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool ParseFlag(string? value)
    {
        return string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record BluRayPlaybackTarget(
        string? DevicePath,
        string PlaybackPath,
        bool UsePlaylist,
        bool IsIsoImage,
        string? FallbackPlaybackPath,
        bool FallbackUsesPlaylist,
        string? FinalFallbackPlaybackPath,
        string? DvdDevicePath,
        IReadOnlyList<double> PlaybackSegmentDurations,
        IReadOnlyList<double> FallbackSegmentDurations);

    private sealed record MountedIsoImage(
        string IsoPath,
        string RootPath,
        bool ShouldDismount);
}
