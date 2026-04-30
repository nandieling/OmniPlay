namespace OmniPlay.Core.Models;

public sealed record LibraryMetadataEnrichmentSummary(
    int UpdatedMovieCount = 0,
    int UpdatedTvShowCount = 0,
    int DownloadedPosterCount = 0,
    bool EncounteredNetworkError = false,
    string? ErrorMessage = null)
{
    public bool HasChanges => UpdatedMovieCount > 0 || UpdatedTvShowCount > 0 || DownloadedPosterCount > 0;
}
