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
            MediaSourceProtocol.WebDav => NormalizeWebDavUrl(trimmed),
            MediaSourceProtocol.Smb => NormalizeLocalPath(trimmed),
            MediaSourceProtocol.Direct => "/",
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
            _ => false
        };
    }

    private static string NormalizeLocalPath(string value)
    {
        if (value == "/")
        {
            return value;
        }

        var pathRoot = Path.GetPathRoot(value);
        if (!string.IsNullOrWhiteSpace(pathRoot) &&
            string.Equals(
                value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return pathRoot;
        }

        return value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string NormalizeWebDavUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return value;
        }

        var normalized = uri.ToString().TrimEnd('/');
        return normalized;
    }
}
