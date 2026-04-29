namespace OmniPlay.Core.Models.Entities;

public sealed class MediaSource
{
    public long? Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string ProtocolType { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

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
            _ => null
        };

    public string GetNormalizedBaseUrl()
    {
        return MediaSourceNormalizer.NormalizeBaseUrl(ProtocolKind, BaseUrl);
    }

    public bool IsValidConfiguration()
    {
        return MediaSourceNormalizer.IsValidBaseUrl(ProtocolKind, BaseUrl);
    }
}
