using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Core.Models.Playback;

namespace OmniPlay.Desktop.Services;

public sealed class ShellPlaybackLauncher : IPlaybackLauncher
{
    private readonly StandalonePlayerWindowManager playerWindowManager;

    public ShellPlaybackLauncher(StandalonePlayerWindowManager playerWindowManager)
    {
        this.playerWindowManager = playerWindowManager;
    }

    public async Task<bool> OpenAsync(
        PlaybackOpenRequest request,
        Func<PlaybackCloseResult, Task>? onPlaybackClosed = null,
        double? startPositionSeconds = null,
        bool replaceCurrentSession = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!MediaSourcePathResolver.IsPlayableLocation(request.PlaybackPath))
        {
            return false;
        }

        await playerWindowManager.ShowAsync(
            request,
            onPlaybackClosed,
            startPositionSeconds,
            replaceCurrentSession,
            cancellationToken);
        return true;
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        return playerWindowManager.CloseAsync(cancellationToken);
    }
}
