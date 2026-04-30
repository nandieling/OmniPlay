using OmniPlay.Core.Models.Playback;

namespace OmniPlay.Core.Interfaces;

public interface IMediaPlayer
{
    bool IsAvailable { get; }

    string BackendName { get; }

    void Initialize();

    void AttachToHost(IntPtr hostHandle);

    Task<MediaPlayerOpenResult> OpenAsync(PlaybackOpenRequest request, CancellationToken cancellationToken = default);

    Task<PlayerPlaybackState> GetStateAsync(CancellationToken cancellationToken = default);

    Task SetPausedAsync(bool isPaused, CancellationToken cancellationToken = default);

    Task SeekAsync(double positionSeconds, CancellationToken cancellationToken = default);

    Task SetMutedAsync(bool isMuted, CancellationToken cancellationToken = default);

    Task SetVolumeAsync(double volumePercent, CancellationToken cancellationToken = default);

    Task SelectAudioTrackAsync(long? trackId, CancellationToken cancellationToken = default);

    Task SelectSubtitleTrackAsync(long? trackId, CancellationToken cancellationToken = default);

    Task<bool> LoadExternalSubtitleAsync(string subtitlePath, CancellationToken cancellationToken = default);

    Task SetSubtitleDelayAsync(double delaySeconds, CancellationToken cancellationToken = default);

    Task SetSubtitleFontSizeAsync(int fontSize, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
