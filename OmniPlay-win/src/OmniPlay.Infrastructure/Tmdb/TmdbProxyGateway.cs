using System.Globalization;

namespace OmniPlay.Infrastructure.Tmdb;

/// <summary>
/// Resolves the optional TMDB reverse-proxy gateway base URL and rewrites official API
/// request URLs onto it. Mirrors the ARM mac implementation
/// (<c>TMDBService.proxyAPIBaseURL</c> / <c>TMDBService.resolvedAPIURL</c>).
/// </summary>
public static class TmdbProxyGateway
{
    public const string OfficialApiBaseUrl = "https://api.themoviedb.org/3";

    /// <summary>
    /// Parses the user-supplied proxy input. Empty input yields the official base URL.
    /// Returns <c>null</c> when the input is syntactically invalid.
    /// </summary>
    public static string? ResolveApiBaseUrl(string? proxyInput)
    {
        var input = (proxyInput ?? string.Empty).Trim();
        if (input.Length == 0)
        {
            return OfficialApiBaseUrl;
        }

        if (!input.Contains("://", StringComparison.Ordinal))
        {
            input = "https://" + input;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            !uri.IsAbsoluteUri ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return null;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

        var path = builder.Path ?? string.Empty;
        while (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
        {
            path = path[..^1];
        }

        if (path.Length is 0 or 1)
        {
            path = "/3";
        }
        else if (path != "/3" && !path.EndsWith("/3", StringComparison.Ordinal))
        {
            path += "/3";
        }

        builder.Path = path;
        return builder.Uri.ToString();
    }

    /// <summary>
    /// Rewrites an official API URL (e.g. <c>https://api.themoviedb.org/3/search/movie?x=1</c>)
    /// onto the proxy base. Returns <c>null</c> when the input URL is not absolute or the
    /// proxy base is invalid.
    /// </summary>
    public static string? ResolveRequestUrl(string officialUrl, string? proxyInput)
    {
        if (!Uri.TryCreate(officialUrl, UriKind.Absolute, out var source))
        {
            return null;
        }

        var proxyBase = ResolveApiBaseUrl(proxyInput);
        if (proxyBase is null)
        {
            return null;
        }

        var basePath = proxyBase;
        while (basePath.Length > 1 && basePath.EndsWith("/", StringComparison.Ordinal))
        {
            basePath = basePath[..^1];
        }

        var sourcePath = source.AbsolutePath;
        string endpointPath;
        if (sourcePath == "/3")
        {
            endpointPath = string.Empty;
        }
        else if (sourcePath.StartsWith("/3/", StringComparison.Ordinal))
        {
            endpointPath = sourcePath[2..];
        }
        else
        {
            endpointPath = sourcePath;
        }

        var query = source.Query.Length > 0 ? source.Query : string.Empty;
        return basePath + endpointPath + query;
    }

    /// <summary>True when the input is non-empty but not a valid proxy gateway address.</summary>
    public static bool HasInvalidInput(string? proxyInput)
    {
        var input = (proxyInput ?? string.Empty).Trim();
        return input.Length > 0 && ResolveApiBaseUrl(input) is null;
    }
}
