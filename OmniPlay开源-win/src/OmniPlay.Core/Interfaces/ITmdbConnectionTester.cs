using OmniPlay.Core.Models;
using OmniPlay.Core.Settings;

namespace OmniPlay.Core.Interfaces;

public interface ITmdbConnectionTester
{
    Task<TmdbConnectionTestResult> TestConnectionAsync(
        TmdbSettings settings,
        CancellationToken cancellationToken = default);
}
