namespace OmniPlay.Core.Models;

public sealed record LibraryThumbnailEnrichmentSummary(
    int DownloadedThumbnailCount = 0,
    int SkippedEpisodeCount = 0,
    bool EncounteredNetworkError = false,
    string? ErrorMessage = null)
{
    public bool HasChanges => DownloadedThumbnailCount > 0;
}
