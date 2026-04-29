using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface ILibraryMetadataEditor
{
    Task<IReadOnlyList<LibraryMetadataSearchCandidate>> SearchMovieMatchesAsync(
        long movieId,
        string? manualQuery = null,
        string? manualYear = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LibraryMetadataSearchCandidate>> SearchTvShowMatchesAsync(
        long tvShowId,
        string? manualQuery = null,
        string? manualYear = null,
        CancellationToken cancellationToken = default);

    Task<LibraryMetadataRefreshResult> RefreshMovieAsync(
        long movieId,
        string? manualQuery = null,
        string? manualYear = null,
        CancellationToken cancellationToken = default);

    Task<LibraryMetadataRefreshResult> RefreshTvShowAsync(
        long tvShowId,
        string? manualQuery = null,
        string? manualYear = null,
        CancellationToken cancellationToken = default);

    Task<LibraryMetadataRefreshResult> ApplyMovieMatchAsync(
        long movieId,
        LibraryMetadataSearchCandidate candidate,
        CancellationToken cancellationToken = default);

    Task<LibraryMetadataRefreshResult> ApplyTvShowMatchAsync(
        long tvShowId,
        LibraryMetadataSearchCandidate candidate,
        CancellationToken cancellationToken = default);

    Task<LibraryMetadataRefreshResult> UpdateMovieMetadataAsync(
        long movieId,
        LibraryMetadataEditRequest request,
        CancellationToken cancellationToken = default);

    Task<LibraryMetadataRefreshResult> UpdateTvShowMetadataAsync(
        long tvShowId,
        LibraryMetadataEditRequest request,
        CancellationToken cancellationToken = default);

    Task SetMovieLockedAsync(long movieId, bool isLocked, CancellationToken cancellationToken = default);

    Task SetTvShowLockedAsync(long tvShowId, bool isLocked, CancellationToken cancellationToken = default);
}
