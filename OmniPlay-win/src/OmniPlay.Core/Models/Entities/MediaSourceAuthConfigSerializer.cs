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

    public static string? SerializeMediaServer(MediaServerAuthConfig? config)
    {
        if (config is null)
        {
            return null;
        }

        var normalized = new MediaServerAuthConfig(
            config.Token?.Trim() ?? string.Empty,
            config.UserId?.Trim() ?? string.Empty,
            config.LibraryId?.Trim() ?? string.Empty,
            config.LibraryName?.Trim() ?? string.Empty,
            config.LibraryType?.Trim() ?? string.Empty);

        return string.IsNullOrWhiteSpace(normalized.Token) &&
               string.IsNullOrWhiteSpace(normalized.UserId) &&
               string.IsNullOrWhiteSpace(normalized.LibraryId)
            ? null
            : JsonSerializer.Serialize(normalized, SerializerOptions);
    }

    public static MediaServerAuthConfig? DeserializeMediaServer(string? authConfig)
    {
        if (string.IsNullOrWhiteSpace(authConfig))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MediaServerAuthConfig>(authConfig, SerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    public static string? SerializeOmniPlayDocker(OmniPlayDockerAuthConfig? config)
    {
        if (config is null)
        {
            return null;
        }

        var normalized = new OmniPlayDockerAuthConfig(
            string.IsNullOrWhiteSpace(config.Username) ? null : config.Username.Trim(),
            string.IsNullOrWhiteSpace(config.SessionCookie) ? null : config.SessionCookie.Trim());

        return string.IsNullOrWhiteSpace(normalized.Username) &&
               string.IsNullOrWhiteSpace(normalized.SessionCookie)
            ? null
            : JsonSerializer.Serialize(normalized, SerializerOptions);
    }

    public static OmniPlayDockerAuthConfig? DeserializeOmniPlayDocker(string? authConfig)
    {
        if (string.IsNullOrWhiteSpace(authConfig))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<OmniPlayDockerAuthConfig>(authConfig, SerializerOptions);
        }
        catch
        {
            return null;
        }
    }
}

public sealed record MediaServerAuthConfig(
    string Token,
    string? UserId = null,
    string? LibraryId = null,
    string? LibraryName = null,
    string? LibraryType = null);

public sealed record OmniPlayDockerAuthConfig(string? Username = null, string? SessionCookie = null);
