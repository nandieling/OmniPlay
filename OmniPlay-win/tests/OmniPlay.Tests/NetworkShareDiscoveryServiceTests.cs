using System.Net;
using OmniPlay.Infrastructure.Library;

namespace OmniPlay.Tests;

public sealed class NetworkShareDiscoveryServiceTests
{
    [Fact]
    public async Task DiscoverAsync_ReturnsManualNetworkEntries()
    {
        var service = new NetworkShareDiscoveryService(new HttpClient(new EmptyHandler()));

        var sources = await service.DiscoverAsync();

        Assert.Contains(sources, source =>
            string.Equals(source.ProtocolType, "webdav", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(source.BaseUrl, "https://", StringComparison.Ordinal));
        Assert.Contains(sources, source =>
            string.Equals(source.ProtocolType, "smb", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(source.BaseUrl, @"\\server\share", StringComparison.Ordinal));
    }

    private sealed class EmptyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
