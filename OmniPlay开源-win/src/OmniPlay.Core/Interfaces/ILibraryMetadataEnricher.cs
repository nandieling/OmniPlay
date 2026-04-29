using OmniPlay.Core.Settings;
using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface ILibraryMetadataEnricher
{
    Task<LibraryMetadataEnrichmentSummary> EnrichMissingMetadataAsync(
        TmdbSettings? settings = null,
        CancellationToken cancellationToken = default);

    Task<LibraryMetadataEnrichmentSummary> EnrichMissingMovieMetadataAsync(
        long movieId,
        TmdbSettings? settings = null,
        CancellationToken cancellationToken = default);

    Task<LibraryMetadataEnrichmentSummary> EnrichMissingTvShowMetadataAsync(
        long tvShowId,
        TmdbSettings? settings = null,
        CancellationToken cancellationToken = default);
}
