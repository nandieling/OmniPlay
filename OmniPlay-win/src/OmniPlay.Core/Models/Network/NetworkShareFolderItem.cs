using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Core.Models.Network;

public sealed class NetworkShareFolderItem
{
    public required string Name { get; init; }

    public required string ProtocolType { get; init; }

    public required string BaseUrl { get; init; }

    public string Description { get; init; } = string.Empty;

    public string? AuthConfig { get; init; }

    public MediaSource ToMediaSource()
    {
        return new MediaSource
        {
            Name = Name,
            ProtocolType = ProtocolType,
            BaseUrl = BaseUrl,
            AuthConfig = AuthConfig,
            IsEnabled = true
        };
    }
}
