using System.Globalization;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Core.Models.Playback;
using OmniPlay.Player.Mpv.Interop;

namespace OmniPlay.Player.Mpv;

public sealed class MpvPlayer : IMediaPlayer
{
    private const int UnknownTrackListCount = -1;
    private bool initialized;
    private string? detectedLibraryName;
    private IntPtr hostHandle;
    private IntPtr playerHandle;
    private int cachedTrackListCount = UnknownTrackListCount;
    private long? cachedSelectedAudioTrackId;
    private long? cachedSelectedSubtitleTrackId;
    private IReadOnlyList<PlayerTrackInfo> cachedAudioTracks = [];
    private IReadOnlyList<PlayerTrackInfo> cachedSubtitleTracks = [];

    public bool IsAvailable { get; private set; }

    public string BackendName => IsAvailable && detectedLibraryName is not null
        ? $"libmpv ({Path.GetFileName(detectedLibraryName)})"
        : "libmpv";

    public void Initialize()
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        IsAvailable = MpvNative.TryLoadLibrary(out detectedLibraryName);
    }

    public void AttachToHost(IntPtr hostHandle)
    {
        this.hostHandle = hostHandle;
    }

    public Task<MediaPlayerOpenResult> OpenAsync(PlaybackOpenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Initialize();

        var filePath = request.PlaybackPath;

        if (!MediaSourcePathResolver.IsPlayableLocation(filePath))
        {
            return Task.FromResult(MediaPlayerOpenResult.Failure("文件不存在，无法开始播放。"));
        }

        if (!IsAvailable)
        {
            return Task.FromResult(MediaPlayerOpenResult.Failure("未找到 libmpv 原生库，请将 libmpv-2.dll 放到程序目录或 PATH 后重试。"));
        }

        if (hostHandle == IntPtr.Zero)
        {
            return Task.FromResult(MediaPlayerOpenResult.Failure("播放器宿主句柄尚未准备好。"));
        }

        try
        {
            if (playerHandle != IntPtr.Zero)
            {
                MpvNative.TerminateDestroy(playerHandle);
                playerHandle = IntPtr.Zero;
                InvalidateTrackCache();
            }

            playerHandle = MpvNative.Create();
            if (playerHandle == IntPtr.Zero)
            {
                return Task.FromResult(MediaPlayerOpenResult.Failure("libmpv 创建播放器实例失败。"));
            }

            SetOption("terminal", "no");
            SetOption("input-default-bindings", "yes");
            SetOption("input-vo-keyboard", "yes");
            SetOption("force-window", "yes");
            SetOption("keep-open", "yes");
            SetOption("panscan", "1.0");
            SetOption("wid", hostHandle.ToInt64().ToString(CultureInfo.InvariantCulture));

            var initResult = MpvNative.Initialize(playerHandle);
            if (initResult < 0)
            {
                return Task.FromResult(MediaPlayerOpenResult.Failure($"libmpv 初始化失败，错误码 {initResult}。"));
            }

            var loadResult = MpvNative.Command(playerHandle, "loadfile", filePath);
            if (loadResult < 0)
            {
                return Task.FromResult(MediaPlayerOpenResult.Failure($"libmpv 加载文件失败，错误码 {loadResult}。"));
            }

            return Task.FromResult(MediaPlayerOpenResult.Success($"已通过 {BackendName} 在应用内打开视频。"));
        }
        catch (DllNotFoundException)
        {
            return Task.FromResult(MediaPlayerOpenResult.Failure("已复制 libmpv-2.dll，但运行时仍无法解析其依赖项。"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(MediaPlayerOpenResult.Failure($"libmpv 播放启动失败：{ex.Message}"));
        }
    }

    public Task<PlayerPlaybackState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        if (playerHandle == IntPtr.Zero)
        {
            return Task.FromResult(PlayerPlaybackState.Empty);
        }

        try
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

            return Task.FromResult(new PlayerPlaybackState
            {
                HasMedia = duration > 0 || position > 0 || audioTracks.Count > 0 || subtitleTracks.Count > 0,
                DurationSeconds = duration,
                PositionSeconds = position,
                IsPaused = pause,
                IsPlaybackCompleted = eofReached,
                IsMuted = mute,
                VolumePercent = Math.Clamp(volume, 0, 100),
                AudioTracks = audioTracks,
                SubtitleTracks = subtitleTracks,
                SubtitleDelaySeconds = subtitleDelay,
                SubtitleFontSize = subtitleFontSize > 0 ? subtitleFontSize : 16
            });
        }
        catch
        {
            return Task.FromResult(PlayerPlaybackState.Empty);
        }
    }

    public Task SetPausedAsync(bool isPaused, CancellationToken cancellationToken = default)
    {
        if (playerHandle != IntPtr.Zero)
        {
            MpvNative.SetPropertyString(playerHandle, "pause", isPaused ? "yes" : "no");
        }

        return Task.CompletedTask;
    }

    public Task SeekAsync(double positionSeconds, CancellationToken cancellationToken = default)
    {
        if (playerHandle != IntPtr.Zero)
        {
            var seconds = Math.Max(0, positionSeconds).ToString("0.###", CultureInfo.InvariantCulture);
            MpvNative.Command(playerHandle, "seek", seconds, "absolute");
        }

        return Task.CompletedTask;
    }

    public Task SetMutedAsync(bool isMuted, CancellationToken cancellationToken = default)
    {
        if (playerHandle != IntPtr.Zero)
        {
            MpvNative.SetPropertyString(playerHandle, "mute", isMuted ? "yes" : "no");
        }

        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(double volumePercent, CancellationToken cancellationToken = default)
    {
        if (playerHandle != IntPtr.Zero)
        {
            var normalized = Math.Clamp(volumePercent, 0, 100).ToString("0.###", CultureInfo.InvariantCulture);
            MpvNative.SetPropertyString(playerHandle, "volume", normalized);
        }

        return Task.CompletedTask;
    }

    public Task SelectAudioTrackAsync(long? trackId, CancellationToken cancellationToken = default)
    {
        if (playerHandle != IntPtr.Zero)
        {
            MpvNative.SetPropertyString(
                playerHandle,
                "aid",
                trackId?.ToString(CultureInfo.InvariantCulture) ?? "no");
        }

        return Task.CompletedTask;
    }

    public Task SelectSubtitleTrackAsync(long? trackId, CancellationToken cancellationToken = default)
    {
        if (playerHandle != IntPtr.Zero)
        {
            MpvNative.SetPropertyString(
                playerHandle,
                "sid",
                trackId?.ToString(CultureInfo.InvariantCulture) ?? "no");
        }

        return Task.CompletedTask;
    }

    public Task<bool> LoadExternalSubtitleAsync(string subtitlePath, CancellationToken cancellationToken = default)
    {
        if (playerHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(subtitlePath) || !File.Exists(subtitlePath))
        {
            return Task.FromResult(false);
        }

        var title = Path.GetFileName(subtitlePath);
        var result = MpvNative.Command(playerHandle, "sub-add", subtitlePath, "select", title);
        if (result >= 0)
        {
            InvalidateTrackCache();
        }

        return Task.FromResult(result >= 0);
    }

    public Task SetSubtitleDelayAsync(double delaySeconds, CancellationToken cancellationToken = default)
    {
        if (playerHandle != IntPtr.Zero)
        {
            MpvNative.SetPropertyString(
                playerHandle,
                "sub-delay",
                delaySeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }

        return Task.CompletedTask;
    }

    public Task SetSubtitleFontSizeAsync(int fontSize, CancellationToken cancellationToken = default)
    {
        if (playerHandle != IntPtr.Zero)
        {
            var normalized = Math.Clamp(fontSize, 8, 96).ToString(CultureInfo.InvariantCulture);
            MpvNative.SetPropertyString(playerHandle, "sub-font-size", normalized);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (playerHandle != IntPtr.Zero)
        {
            MpvNative.TerminateDestroy(playerHandle);
            playerHandle = IntPtr.Zero;
            InvalidateTrackCache();
        }

        return Task.CompletedTask;
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
            var codec = MpvNative.GetPropertyString(playerHandle, $"{propertyPrefix}/codec");
            var audioChannels = string.Equals(trackType, "audio", StringComparison.OrdinalIgnoreCase)
                ? MpvNative.GetPropertyString(playerHandle, $"{propertyPrefix}/audio-channels")
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
}
