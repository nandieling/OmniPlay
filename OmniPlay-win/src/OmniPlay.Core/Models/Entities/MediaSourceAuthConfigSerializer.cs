using System.Text.Json;

namespace OmniPlay.Core.Models.Entities;

public static class MediaSourceAuthConfigSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static string? SerializeWebDav(WebDavAuthConfig? config)
    {
        if (config is null)
        {
            return null;
        }

        var normalized = new WebDavAuthConfig(
            config.Username?.Trim() ?? string.Empty,
            config.Password ?? string.Empty);

        return string.IsNullOrWhiteSpace(normalized.Username) && string.IsNullOrEmpty(normalized.Password)
            ? null
            : JsonSerializer.Serialize(normalized, SerializerOptions);
    }

    public static WebDavAuthConfig? DeserializeWebDav(string? authConfig)
    {
        if (string.IsNullOrWhiteSpace(authConfig))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WebDavAuthConfig>(authConfig, SerializerOptions);
        }
        catch
        {
            return null;
        }
    }
}
