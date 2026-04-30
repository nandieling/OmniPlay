namespace OmniPlay.Core.Models;

public sealed record LibraryMetadataRefreshResult(
    bool FoundMatch = false,
    bool Updated = false,
    bool DownloadedPoster = false,
    bool EncounteredNetworkError = false,
    string? MatchedTitle = null,
    string Message = "")
{
    public bool Succeeded => FoundMatch && !EncounteredNetworkError;
}
