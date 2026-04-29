using OmniPlay.Core.Settings;
using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface ILibraryThumbnailEnricher
{
    Task<LibraryThumbnailEnrichmentSummary> EnrichMissingThumbnailsAsync(
        TmdbSettings? settings = null,
        CancellationToken cancellationToken = default);

    Task<LibraryThumbnailEnrichmentSummary> EnrichMissingThumbnailsForTvShowAsync(
        long tvShowId,
        TmdbSettings? settings = null,
        CancellationToken cancellationToken = default);
}
