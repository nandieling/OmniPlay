namespace OmniPlay.Core.Models;

public sealed record LibraryScanSummary
{
    public LibraryScanSummary(
        int sourceCount,
        int newMovieCount,
        int newVideoFileCount,
        int removedVideoFileCount = 0,
        int newTvShowCount = 0,
        IReadOnlyList<string>? diagnostics = null)
    {
        SourceCount = sourceCount;
        NewMovieCount = newMovieCount;
        NewVideoFileCount = newVideoFileCount;
        RemovedVideoFileCount = removedVideoFileCount;
        NewTvShowCount = newTvShowCount;
        Diagnostics = diagnostics ?? [];
    }

    public int SourceCount { get; }

    public int NewMovieCount { get; }

    public int NewVideoFileCount { get; }

    public int RemovedVideoFileCount { get; }

    public int NewTvShowCount { get; }

    public IReadOnlyList<string> Diagnostics { get; }

    public bool HasDiagnostics => Diagnostics.Count > 0;
}
