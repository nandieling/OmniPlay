using OmniPlay.Core.Models.Library;
using OmniPlay.Core.Models.Playback;

namespace OmniPlay.Core.Interfaces;

public interface IVideoFileRepository
{
    Task<IReadOnlyList<LibraryVideoItem>> GetByMovieAsync(long movieId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LibraryVideoItem>> GetByTvShowAsync(long tvShowId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LibraryPosterItem>> GetContinueWatchingAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, PlaybackWatchState>> GetLibraryPlaybackStatesAsync(CancellationToken cancellationToken = default);

    Task UpdatePlayProgressAsync(string videoFileId, double playProgress, CancellationToken cancellationToken = default);

    Task UpdatePlaybackStateAsync(
        string videoFileId,
        double playProgress,
        double? durationSeconds,
        CancellationToken cancellationToken = default);

    Task UpdateEpisodeMetadataAsync(
        string videoFileId,
        LibraryEpisodeEditRequest request,
        CancellationToken cancellationToken = default);
}
