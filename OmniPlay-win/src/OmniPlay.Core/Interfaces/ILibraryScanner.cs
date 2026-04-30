using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface ILibraryScanner
{
    Task<LibraryScanSummary> ScanAllAsync(CancellationToken cancellationToken = default);
}
