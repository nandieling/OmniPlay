using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface ILibraryRepository
{
    Task<IReadOnlyList<LibraryItemSummary>> GetItemsAsync(CancellationToken cancellationToken = default);

    Task<LibraryItemDetail?> GetItemDetailAsync(
        string id,
        CancellationToken cancellationToken = default);

    Task<PlayableVideoFile?> GetPlayableVideoFileAsync(
        string videoFileId,
        CancellationToken cancellationToken = default);

    Task<string?> GetLibraryItemIdForVideoFileAsync(
        string videoFileId,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateVideoFileProbeAsync(
        VideoFileProbeUpdate request,
        CancellationToken cancellationToken = default);

    Task<bool> ApplyMetadataMatchAsync(
        LibraryItemMetadataApplyRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateCustomMetadataAsync(
        LibraryItemCustomMetadataUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> SaveDoubanMetadataAsync(
        DoubanMetadata metadata,
        CancellationToken cancellationToken = default);

    Task<bool> SetLibraryItemLockedAsync(
        LibraryItemLockUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> UpdatePlaybackProgressAsync(
        PlaybackProgressUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task<PlaybackProgressState?> GetPlaybackProgressAsync(
        string videoFileId,
        CancellationToken cancellationToken = default);

    Task<bool> SetWatchedAsync(
        WatchedStatusUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> SetLibraryItemWatchedAsync(
        LibraryItemWatchedStatusUpdateRequest request,
        CancellationToken cancellationToken = default);
}
