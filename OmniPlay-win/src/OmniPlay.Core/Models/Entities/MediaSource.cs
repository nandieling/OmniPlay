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

public sealed class LuckyStunSourceConfiguration
{
    [JsonPropertyName("managementURL")]
    public string ManagementUrl { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("ruleID")]
    public string RuleId { get; set; } = string.Empty;

    [JsonPropertyName("ruleName")]
    public string RuleName { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("autoUpdate")]
    public bool AutoUpdate { get; set; } = true;

    [JsonPropertyName("updateIntervalMinutes")]
    public int UpdateIntervalMinutes { get; set; } = 30;

    [JsonPropertyName("lastUpdatedAt")]
    public DateTimeOffset? LastUpdatedAt { get; set; }

    [JsonPropertyName("pathSuffix")]
    public string PathSuffix { get; set; } = string.Empty;
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

    [JsonPropertyName("luckyStun")]
    public LuckyStunSourceConfiguration? LuckyStun { get; set; }

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
            ActiveLabel = ActiveLabel,
            LuckyStun = LuckyStun is null ? null : new LuckyStunSourceConfiguration
            {
                ManagementUrl = LuckyStun.ManagementUrl,
                Username = LuckyStun.Username,
                Password = LuckyStun.Password,
                RuleId = LuckyStun.RuleId,
                RuleName = LuckyStun.RuleName,
                DisplayName = LuckyStun.DisplayName,
                AutoUpdate = LuckyStun.AutoUpdate,
                UpdateIntervalMinutes = LuckyStun.UpdateIntervalMinutes,
                LastUpdatedAt = LuckyStun.LastUpdatedAt,
                PathSuffix = LuckyStun.PathSuffix
            }
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

    // 媒体源行第一行：文件夹名称 + 媒体源类型
    public string DisplayNameWithType
    {
        get
        {
            var label = HasLuckyStunSourceConfig ? "WebDAV 穿透" : ProtocolLabel;
            return string.IsNullOrWhiteSpace(Name) ? label : Name + " · " + label;
        }
    }

    // 媒体源行第二行：当前地址（穿透源显示真实文件夹名，而非随机隧道路径）
    public string DisplayAddress
    {
        get
        {
            var configuration = GetAddressConfiguration();
            var address = string.IsNullOrWhiteSpace(configuration.ActiveAddress) ? BaseUrl : configuration.ActiveAddress;
            if (HasLuckyStunSourceConfig)
            {
                var suffix = (configuration.LuckyStun?.PathSuffix ?? string.Empty).Trim('/');
                if (suffix.Length > 0 && Uri.TryCreate(address, UriKind.Absolute, out var luckyUri))
                {
                    var decodedSuffix = Uri.UnescapeDataString(suffix).Trim();
                    if (decodedSuffix.Length > 0)
                    {
                        return luckyUri.Scheme + "://" + luckyUri.Authority + "/" + decodedSuffix;
                    }
                }
            }
            return DecodeUrlPathForDisplay(address);
        }
    }

    private static string DecodeUrlPathForDisplay(string address)
    {
        if (!address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return address;
        }

        try
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
            {
                var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');
                return uri.Scheme + "://" + uri.Authority + (path.Length == 0 ? string.Empty : "/" + path);
            }
        }
        catch (UriFormatException)
        {
        }

        return address;
    }

    [JsonIgnore]
    public double RowOpacity => IsEnabled ? 1.0 : 0.55;

    public string? LuckyStunDisplayName
    {
        get
        {
            var configuration = GetAddressConfiguration();
            if (configuration.LuckyStun is null)
            {
                return null;
            }

            var displayName = configuration.LuckyStun.DisplayName.Trim();
            return string.IsNullOrWhiteSpace(displayName) ? null : displayName;
        }
    }

    [JsonIgnore]
    public bool HasLuckyStunSourceConfig => GetAddressConfiguration().LuckyStun is not null;

    // 媒体源行图标：Lucky STUN 穿透 > WebDAV > SMB > 本地 > 媒体服务器
    [JsonIgnore]
    public string ProtocolIconKey => HasLuckyStunSourceConfig
        ? "lucky"
        : ProtocolKind switch
        {
            MediaSourceProtocol.WebDav => "webdav",
            MediaSourceProtocol.Smb => "smb",
            MediaSourceProtocol.Local => "local",
            _ => "server"
        };

    [JsonIgnore]
    public bool IsLuckySourceKind => HasLuckyStunSourceConfig;

    [JsonIgnore]
    public bool IsWebDavSourceKind => !HasLuckyStunSourceConfig && ProtocolKind == MediaSourceProtocol.WebDav;

    [JsonIgnore]
    public bool IsSmbSourceKind => !HasLuckyStunSourceConfig && ProtocolKind == MediaSourceProtocol.Smb;

    [JsonIgnore]
    public bool IsLocalSourceKind => !HasLuckyStunSourceConfig && ProtocolKind == MediaSourceProtocol.Local;

    [JsonIgnore]
    public bool IsServerSourceKind => !HasLuckyStunSourceConfig
        && ProtocolKind is not (MediaSourceProtocol.WebDav or MediaSourceProtocol.Smb or MediaSourceProtocol.Local);

    public bool IsValidConfiguration()
    {
        return MediaSourceNormalizer.IsValidBaseUrl(ProtocolKind, BaseUrl);
    }

    public MediaSource Clone()
    {
        return new MediaSource
        {
            Id = Id,
            Name = Name,
            ProtocolType = ProtocolType,
            BaseUrl = BaseUrl,
            AddressConfig = AddressConfig,
            AuthConfig = AuthConfig,
            IsEnabled = IsEnabled,
            DisabledAt = DisabledAt,
            RemovedAt = RemovedAt
        };
    }
}
