namespace OmniPlay.Core.Models.Network;

/// <summary>
/// Address helpers shared by the per-source Lucky STUN flow. Mirrors the ARM mac
/// implementations (<c>LuckyStunClient.normalizeAddress</c> and
/// <c>LuckyStunCoordinator.appendingPathSuffix</c>).
/// </summary>
public static class LuckyStunAddressHelper
{
    public static string NormalizeAddress(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return $"http:{trimmed}";
        }

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.TrimEnd('/');
        }

        return trimmed.Contains(':', StringComparison.Ordinal) || trimmed.Contains('.', StringComparison.Ordinal)
            ? $"http://{trimmed.TrimEnd('/')}"
            : trimmed;
    }

    /// <summary>Appends the configured path suffix (e.g. "45722") to the penetration address path.</summary>
    public static string AppendPathSuffix(string? suffix, string address)
    {
        var trimmedSuffix = (suffix ?? string.Empty).Trim('/');
        if (trimmedSuffix.Length == 0 || !Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            return address;
        }

        var basePath = uri.AbsolutePath.Trim('/');
        var builder = new UriBuilder(uri)
        {
            Path = "/" + string.Join('/', new[] { basePath, trimmedSuffix }.Where(static part => part.Length > 0)),
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }
}
