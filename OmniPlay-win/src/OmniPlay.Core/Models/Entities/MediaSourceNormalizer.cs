namespace OmniPlay.Core.Models.Entities;

public static class MediaSourceNormalizer
{
    public static string NormalizeBaseUrl(MediaSourceProtocol? protocol, string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || protocol is null)
        {
            return trimmed;
        }

        return protocol.Value switch
        {
            MediaSourceProtocol.Local => NormalizeLocalPath(trimmed),
            MediaSourceProtocol.WebDav => NormalizeHttpUrl(trimmed),
            MediaSourceProtocol.Smb => NormalizeLocalPath(trimmed),
            MediaSourceProtocol.Direct => "/",
            MediaSourceProtocol.Plex => NormalizeHttpUrl(trimmed, 32400),
            MediaSourceProtocol.Emby => NormalizeHttpUrl(trimmed, 8096),
            MediaSourceProtocol.Jellyfin => NormalizeHttpUrl(trimmed, 8096),
            MediaSourceProtocol.OmniPlayDocker => NormalizeHttpUrl(trimmed, 45722),
            _ => trimmed
        };
    }

    public static bool IsValidBaseUrl(MediaSourceProtocol? protocol, string value)
    {
        var normalized = NormalizeBaseUrl(protocol, value);
        if (protocol is null)
        {
            return false;
        }

        return protocol.Value switch
        {
            MediaSourceProtocol.Local => normalized.Length > 0,
            MediaSourceProtocol.WebDav => Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
                                           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                                           && !string.IsNullOrWhiteSpace(uri.Host),
            MediaSourceProtocol.Smb => normalized.StartsWith(@"\\", StringComparison.Ordinal)
                                       && normalized.Trim('\\').Length > 0,
            MediaSourceProtocol.Direct => normalized == "/",
            MediaSourceProtocol.Plex or MediaSourceProtocol.Emby or MediaSourceProtocol.Jellyfin or MediaSourceProtocol.OmniPlayDocker =>
                Uri.TryCreate(normalized, UriKind.Absolute, out var serverUri)
                && (serverUri.Scheme == Uri.UriSchemeHttp || serverUri.Scheme == Uri.UriSchemeHttps)
                && !string.IsNullOrWhiteSpace(serverUri.Host),
            _ => false
        };
    }

    private static string NormalizeLocalPath(string value)
    {
        if (value == "/")
        {
            return value;
        }

        var directorySeparators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '\\', '/' };
        var pathRoot = Path.GetPathRoot(value);
        if (!string.IsNullOrWhiteSpace(pathRoot) &&
            string.Equals(
                value.TrimEnd(directorySeparators),
                pathRoot.TrimEnd(directorySeparators),
                StringComparison.OrdinalIgnoreCase))
        {
            return pathRoot;
        }

        return value.TrimEnd(directorySeparators);
    }

    private static string NormalizeHttpUrl(string value, int? defaultHttpPort = null)
    {
        var candidate = value;
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = $"http://{candidate}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return value;
        }

        var builder = new UriBuilder(uri);
        if (string.Equals(builder.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            builder.Host = "127.0.0.1";
        }
        if (defaultHttpPort == 45722)
        {
            var path = builder.Path ?? string.Empty;
            var apiIndex = path.LastIndexOf("/api", StringComparison.OrdinalIgnoreCase);
            if (apiIndex >= 0 && (apiIndex + 4 == path.Length || path[apiIndex + 4] == '/'))
            {
                builder.Path = path[..apiIndex];
                builder.Query = string.Empty;
                builder.Fragment = string.Empty;
            }
        }
        if (defaultHttpPort.HasValue &&
            string.Equals(builder.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            uri.IsDefaultPort)
        {
            builder.Port = defaultHttpPort.Value;
        }

        var normalized = builder.Uri.ToString().TrimEnd('/');
        return normalized;
    }
}
