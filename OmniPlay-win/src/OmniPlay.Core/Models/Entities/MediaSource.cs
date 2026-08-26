using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniPlay.Core.Models.Entities;

public sealed class MediaSourceExternalAddress
{
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("useLuckyStun")]
    public bool UseLuckyStun { get; set; }

    [JsonPropertyName("luckyRuleId")]
    public string LuckyRuleId { get; set; } = string.Empty;

    [JsonPropertyName("luckyRuleName")]
    public string LuckyRuleName { get; set; } = string.Empty;

    [JsonIgnore]
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? "外网" : Label.Trim();
}

public sealed class MediaSourceAddressConfiguration
{
    [JsonPropertyName("localAddress")]
    public string LocalAddress { get; set; } = string.Empty;

    [JsonPropertyName("localLabel")]
    public string LocalLabel { get; set; } = "局域网";

    [JsonPropertyName("externalAddresses")]
    public List<MediaSourceExternalAddress> ExternalAddresses { get; set; } = [];

    [JsonPropertyName("activeAddress")]
    public string ActiveAddress { get; set; } = string.Empty;

    [JsonPropertyName("activeLabel")]
    public string ActiveLabel { get; set; } = string.Empty;

    public MediaSourceAddressConfiguration Clone()
    {
        return new MediaSourceAddressConfiguration
        {
            LocalAddress = LocalAddress,
            LocalLabel = LocalLabel,
            ExternalAddresses = ExternalAddresses.Select(static item => new MediaSourceExternalAddress
            {
                Address = item.Address,
                Label = item.Label,
                UseLuckyStun = item.UseLuckyStun,
                LuckyRuleId = item.LuckyRuleId,
                LuckyRuleName = item.LuckyRuleName
            }).ToList(),
            ActiveAddress = ActiveAddress,
            ActiveLabel = ActiveLabel
        };
    }
}

public sealed class MediaSource
{
    public long? Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string ProtocolType { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    // 保留 BaseUrl 作为当前实际地址，兼容既有扫描、播放和 Docker 数据。
    public string? AddressConfig { get; set; }

    public string? AuthConfig { get; set; }

    public bool IsEnabled { get; set; } = true;

    public string? DisabledAt { get; set; }

    public string? RemovedAt { get; set; }

    public bool SupportsInlineEditing => ProtocolKind == MediaSourceProtocol.WebDav;

    public bool SupportsPickerEditing => ProtocolKind == MediaSourceProtocol.Local;

    public bool SupportsEditing => SupportsInlineEditing || SupportsPickerEditing;

    public string EditActionText => SupportsPickerEditing ? "更换目录" : "编辑";

    public string ProtocolLabel => ProtocolKind switch
    {
        MediaSourceProtocol.Local => "本地目录",
        MediaSourceProtocol.WebDav => "WebDAV",
        MediaSourceProtocol.Smb => "SMB",
        MediaSourceProtocol.Direct => "直连",
        MediaSourceProtocol.Plex => "Plex",
        MediaSourceProtocol.Emby => "Emby",
        MediaSourceProtocol.Jellyfin => "Jellyfin",
        MediaSourceProtocol.OmniPlayDocker => "OmniPlay Docker",
        _ => ProtocolType
    };

    public bool IsRemoved => !string.IsNullOrWhiteSpace(RemovedAt);

    public bool IsActive => IsEnabled && !IsRemoved;

    public string ToggleActionText => IsEnabled ? "关闭" : "开启";

    public string SourceStateText => IsRemoved
        ? "已移除"
        : IsEnabled
            ? "已开启"
            : "已关闭";

    public string RetentionHintText => IsEnabled
        ? "开启后会参与扫描和首页展示。"
        : "关闭后不显示在首页，扫描和刮削数据保留 30 天。";

    public MediaSourceProtocol? ProtocolKind =>
        ProtocolType.Trim().ToLowerInvariant() switch
        {
            "local" => MediaSourceProtocol.Local,
            "webdav" => MediaSourceProtocol.WebDav,
            "smb" => MediaSourceProtocol.Smb,
            "direct" => MediaSourceProtocol.Direct,
            "plex" => MediaSourceProtocol.Plex,
            "emby" => MediaSourceProtocol.Emby,
            "jellyfin" => MediaSourceProtocol.Jellyfin,
            "omniplaydocker" or "omniplay-docker" or "omniplay_docker" => MediaSourceProtocol.OmniPlayDocker,
            _ => null
        };

    public string GetNormalizedBaseUrl()
    {
        return MediaSourceNormalizer.NormalizeBaseUrl(ProtocolKind, BaseUrl);
    }

    public MediaSourceAddressConfiguration GetAddressConfiguration()
    {
        MediaSourceAddressConfiguration? configuration = null;
        if (!string.IsNullOrWhiteSpace(AddressConfig))
        {
            try
            {
                configuration = JsonSerializer.Deserialize<MediaSourceAddressConfiguration>(AddressConfig);
            }
            catch (JsonException)
            {
                configuration = null;
            }
        }

        configuration ??= new MediaSourceAddressConfiguration();
        configuration.ExternalAddresses ??= [];
        if (string.IsNullOrWhiteSpace(configuration.LocalAddress))
        {
            configuration.LocalAddress = BaseUrl;
        }

        if (string.IsNullOrWhiteSpace(configuration.ActiveAddress))
        {
            configuration.ActiveAddress = BaseUrl;
        }

        if (string.IsNullOrWhiteSpace(configuration.ActiveLabel))
        {
            configuration.ActiveLabel = string.Equals(
                configuration.ActiveAddress,
                configuration.LocalAddress,
                StringComparison.OrdinalIgnoreCase)
                ? configuration.LocalLabel
                : string.Empty;
        }

        return configuration;
    }

    public void SetAddressConfiguration(MediaSourceAddressConfiguration configuration)
    {
        AddressConfig = JsonSerializer.Serialize(configuration);
    }

    public string DisplayNameWithActiveLabel
    {
        get
        {
            var label = GetAddressConfiguration().ActiveLabel.Trim();
            return string.IsNullOrWhiteSpace(label) ? Name : $"{Name} · {label}";
        }
    }

    public string ActiveAddressLabel => GetAddressConfiguration().ActiveLabel;

    public bool IsValidConfiguration()
    {
        return MediaSourceNormalizer.IsValidBaseUrl(ProtocolKind, BaseUrl);
    }
}
