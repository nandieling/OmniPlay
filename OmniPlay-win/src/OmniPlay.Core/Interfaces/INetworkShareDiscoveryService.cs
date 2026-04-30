using OmniPlay.Core.Models.Network;

namespace OmniPlay.Core.Interfaces;

public interface INetworkShareDiscoveryService
{
    Task<IReadOnlyList<NetworkSourceDiscoveryItem>> DiscoverAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NetworkShareFolderItem>> ListFoldersAsync(
        NetworkSourceDiscoveryItem source,
        string username,
        string password,
        CancellationToken cancellationToken = default);
}
