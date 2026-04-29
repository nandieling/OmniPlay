using OmniPlay.Core.Models.Playback;

namespace OmniPlay.Core.Interfaces;

public interface IPlaybackLauncher
{
    Task<bool> OpenAsync(
        PlaybackOpenRequest request,
        Func<PlaybackCloseResult, Task>? onPlaybackClosed = null,
        double? startPositionSeconds = null,
        bool replaceCurrentSession = false,
        CancellationToken cancellationToken = default);

    Task CloseAsync(CancellationToken cancellationToken = default);
}
