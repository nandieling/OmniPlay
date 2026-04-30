using OmniPlay.Core.Models;
using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Core.Interfaces;

public interface IWebDavConnectionTester
{
    Task<WebDavConnectionTestResult> TestConnectionAsync(
        MediaSource source,
        CancellationToken cancellationToken = default);
}
