using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Core.Models.Network;

namespace OmniPlay.Infrastructure.Library;

public sealed class NetworkShareDiscoveryService : INetworkShareDiscoveryService
{
    private static readonly HttpMethod PropFindMethod = new("PROPFIND");
    private static readonly XNamespace DavNamespace = "DAV:";
    private static readonly Regex MultiSpace = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly TimeSpan MediaProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LanTcpProbeTimeout = TimeSpan.FromMilliseconds(600);
    private static readonly string[] WebDavProbePaths =
    [
        "/",
        "/dav/",
        "/webdav/"
    ];
    private static readonly (string Scheme, int Port)[] LanHttpProbeEndpoints =
    [
        (Uri.UriSchemeHttp, 80),
        (Uri.UriSchemeHttps, 443),
        (Uri.UriSchemeHttp, 5005),
        (Uri.UriSchemeHttps, 5006),
        (Uri.UriSchemeHttp, 5244),
        (Uri.UriSchemeHttp, 8080),
        (Uri.UriSchemeHttps, 8443),
        (Uri.UriSchemeHttp, 8096),
        (Uri.UriSchemeHttps, 8096),
        (Uri.UriSchemeHttp, 8097),
        (Uri.UriSchemeHttps, 8097),
        (Uri.UriSchemeHttp, 8098),
        (Uri.UriSchemeHttps, 8098),
        (Uri.UriSchemeHttp, 8099),
        (Uri.UriSchemeHttps, 8099),
        (Uri.UriSchemeHttps, 8920),
        (Uri.UriSchemeHttp, 45722),
        (Uri.UriSchemeHttp, 45721),
        (Uri.UriSchemeHttp, 32400)
    ];
    private readonly HttpClient httpClient;
    private readonly Func<CancellationToken, Task<IReadOnlyList<Uri>>> mediaServerEndpointProvider;
    private readonly bool enableActiveLanDiscovery;

    public NetworkShareDiscoveryService(
        HttpClient httpClient,
        Func<CancellationToken, Task<IReadOnlyList<Uri>>>? mediaServerEndpointProvider = null)
    {
        this.httpClient = httpClient;
        this.mediaServerEndpointProvider = mediaServerEndpointProvider ?? DiscoverMediaServerEndpointCandidatesAsync;
        enableActiveLanDiscovery = mediaServerEndpointProvider is null;
    }

    public async Task<IReadOnlyList<NetworkSourceDiscoveryItem>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        Dictionary<string, NetworkSourceDiscoveryItem> results = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> smbServers = new(StringComparer.OrdinalIgnoreCase);

        foreach (var drive in DriveInfo.GetDrives().Where(static drive => drive.DriveType == DriveType.Network))
        {
            var root = drive.RootDirectory.FullName.TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            results[$"smb:{root}"] = new NetworkSourceDiscoveryItem
            {
                Name = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? root : drive.VolumeLabel,
                ProtocolType = "smb",
                BaseUrl = root,
                Description = "Windows 已映射的网络文件夹。"
            };
            if (TryGetSmbHost(root, out var mappedHost))
            {
                smbServers.Add($@"\\{mappedHost}");
            }
        }

        foreach (var server in await ListSmbServersAsync(cancellationToken))
        {
            smbServers.Add(server);
            results[$"smb:{server}"] = new NetworkSourceDiscoveryItem
            {
                Name = server,
                ProtocolType = "smb",
                BaseUrl = server,
                Description = "预扫描到的 SMB 服务器。"
            };
        }

        if (enableActiveLanDiscovery)
        {
            foreach (var server in await DiscoverSmbServersByTcpAsync(cancellationToken))
            {
                smbServers.Add(server);
                results[$"smb:{server}"] = new NetworkSourceDiscoveryItem
                {
                    Name = server,
                    ProtocolType = "smb",
                    BaseUrl = server,
                    Description = "预扫描到的 SMB 服务器。"
                };
            }
        }

        var httpCandidates = await LoadHttpEndpointCandidatesAsync(smbServers, cancellationToken);
        foreach (var webDavServer in await DiscoverWebDavServersAsync(httpCandidates, cancellationToken))
        {
            results[$"{webDavServer.ProtocolType}:{webDavServer.BaseUrl}"] = webDavServer;
        }

        foreach (var mediaServer in await DiscoverMediaServersAsync(httpCandidates, cancellationToken))
        {
            results[$"{mediaServer.ProtocolType}:{mediaServer.BaseUrl}"] = mediaServer;
        }

        return results.Values
            .OrderBy(static item => item.ProtocolLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<NetworkShareFolderItem>> ListFoldersAsync(
        NetworkSourceDiscoveryItem source,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        return source.ProtocolKind switch
        {
            MediaSourceProtocol.WebDav => await ListWebDavFoldersAsync(source.BaseUrl, username, password, cancellationToken),
            MediaSourceProtocol.Smb => await ListSmbFoldersAsync(source.BaseUrl, username, password, cancellationToken),
            MediaSourceProtocol.Plex or MediaSourceProtocol.Emby or MediaSourceProtocol.Jellyfin => [],
            _ => []
        };
    }

    private async Task<IReadOnlyList<Uri>> LoadHttpEndpointCandidatesAsync(
        IEnumerable<string> knownSmbServers,
        CancellationToken cancellationToken)
    {
        Dictionary<string, Uri> results = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var uri in await mediaServerEndpointProvider(cancellationToken))
            {
                AddUri(results, uri);
            }
        }
        catch
        {
        }

        foreach (var uri in BuildLanHttpEndpointCandidatesFromSmbServers(knownSmbServers))
        {
            AddUri(results, uri);
        }

        if (enableActiveLanDiscovery)
        {
            foreach (var uri in await DiscoverLanHttpEndpointCandidatesAsync(cancellationToken))
            {
                AddUri(results, uri);
            }
        }

        return results.Values.ToList();
    }

    private async Task<IReadOnlyList<NetworkSourceDiscoveryItem>> DiscoverWebDavServersAsync(
        IReadOnlyList<Uri> candidates,
        CancellationToken cancellationToken)
    {
        Dictionary<string, NetworkSourceDiscoveryItem> results = new(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates.DistinctBy(static uri => uri.GetLeftPart(UriPartial.Authority)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = await ProbeWebDavAsync(candidate, cancellationToken);
            if (item is not null)
            {
                results[$"{item.ProtocolType}:{item.BaseUrl}"] = item;
            }
        }

        return results.Values
            .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<NetworkSourceDiscoveryItem>> DiscoverMediaServersAsync(
        IReadOnlyList<Uri> candidates,
        CancellationToken cancellationToken)
    {
        Dictionary<string, NetworkSourceDiscoveryItem> results = new(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates.DistinctBy(static uri => uri.GetLeftPart(UriPartial.Authority)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = await ProbeMediaServerAsync(candidate, cancellationToken);
            if (item is not null)
            {
                results[$"{item.ProtocolType}:{item.BaseUrl}"] = item;
            }
        }

        return results.Values
            .OrderBy(static item => item.ProtocolLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<NetworkSourceDiscoveryItem?> ProbeWebDavAsync(Uri candidate, CancellationToken cancellationToken)
    {
        if (!candidate.IsAbsoluteUri ||
            (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(candidate.Host))
        {
            return null;
        }

        var baseUri = new Uri(candidate.GetLeftPart(UriPartial.Authority));
        foreach (var probePath in WebDavProbePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probeUri = new Uri(baseUri, probePath);
            using var request = BuildWebDavRequest(probeUri, null);

            try
            {
                using var response = await SendProbeAsync(request, cancellationToken);
                if (response is null || !LooksLikeWebDavProbeResponse(probeUri, response))
                {
                    continue;
                }

                var normalized = MediaSourceNormalizer.NormalizeBaseUrl(
                    MediaSourceProtocol.WebDav,
                    probeUri.AbsoluteUri.TrimEnd('/'));
                return new NetworkSourceDiscoveryItem
                {
                    Name = ResolveWebDavDiscoveryName(probeUri, normalized),
                    ProtocolType = "webdav",
                    BaseUrl = normalized,
                    Description = $"预扫描到的 WebDAV 入口：{normalized}"
                };
            }
            catch
            {
            }
        }

        return null;
    }

    private async Task<NetworkSourceDiscoveryItem?> ProbeMediaServerAsync(Uri candidate, CancellationToken cancellationToken)
    {
        if (!candidate.IsAbsoluteUri ||
            (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(candidate.Host))
        {
            return null;
        }

        var baseUri = new Uri(candidate.GetLeftPart(UriPartial.Authority));

        var docker = await ProbeOmniPlayDockerAsync(baseUri, cancellationToken);
        if (docker is not null)
        {
            return docker;
        }

        if (baseUri.Port == 32400)
        {
            var plex = await ProbePlexAsync(baseUri, cancellationToken);
            if (plex is not null)
            {
                return plex;
            }
        }

        var embyCompatible = await ProbeEmbyCompatibleAsync(baseUri, cancellationToken);
        if (embyCompatible is not null)
        {
            return embyCompatible;
        }

        return await ProbePlexAsync(baseUri, cancellationToken);
    }

    private async Task<NetworkSourceDiscoveryItem?> ProbeOmniPlayDockerAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        foreach (var path in new[] { "/api/health", "/api/auth/status" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
            request.Headers.Accept.ParseAdd("application/json");
            try
            {
                using var response = await SendProbeAsync(request, cancellationToken);
                if (response is null ||
                    (!response.IsSuccessStatusCode &&
                     response.StatusCode is not HttpStatusCode.Unauthorized and not HttpStatusCode.Forbidden))
                {
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!LooksLikeOmniPlayDockerResponse(path, body, response))
                {
                    continue;
                }

                var normalized = MediaSourceNormalizer.NormalizeBaseUrl(MediaSourceProtocol.OmniPlayDocker, baseUri.AbsoluteUri);
                return new NetworkSourceDiscoveryItem
                {
                    Name = "OmniPlay Docker",
                    ProtocolType = "omniplay-docker",
                    BaseUrl = normalized,
                    Description = $"预扫描到的 OmniPlay Docker 服务：{normalized}"
                };
            }
            catch
            {
            }
        }

        return null;
    }

    private async Task<NetworkSourceDiscoveryItem?> ProbePlexAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/identity"));
        request.Headers.Accept.ParseAdd("application/xml");
        request.Headers.TryAddWithoutValidation("X-Plex-Product", "OmniPlay");
        request.Headers.TryAddWithoutValidation("X-Plex-Version", "1.0");
        request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", "omniplay-windows");
        request.Headers.TryAddWithoutValidation("X-Plex-Device-Name", "OmniPlay");

        try
        {
            using var response = await SendProbeAsync(request, cancellationToken);
            if (response is null)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var hasPlexHeader = response.Headers.Contains("X-Plex-Protocol") ||
                                response.Headers.Contains("X-Plex-Content-Original-Length");
            var hasPlexIdentity = body.Contains("MediaContainer", StringComparison.OrdinalIgnoreCase) &&
                                  body.Contains("machineIdentifier", StringComparison.OrdinalIgnoreCase);
            if (!response.IsSuccessStatusCode || (!hasPlexHeader && !hasPlexIdentity))
            {
                return null;
            }

            var normalized = MediaSourceNormalizer.NormalizeBaseUrl(MediaSourceProtocol.Plex, baseUri.AbsoluteUri);
            return new NetworkSourceDiscoveryItem
            {
                Name = "Plex",
                ProtocolType = "plex",
                BaseUrl = normalized,
                Description = $"预扫描到的 Plex 媒体服务器：{normalized}"
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<NetworkSourceDiscoveryItem?> ProbeEmbyCompatibleAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/System/Info/Public"));
        request.Headers.Accept.ParseAdd("application/json");

        try
        {
            using var response = await SendProbeAsync(request, cancellationToken);
            if (response is null || !response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var combined = body;
            if (document.RootElement.TryGetProperty("ProductName", out var productName))
            {
                combined += " " + productName.GetString();
            }
            if (document.RootElement.TryGetProperty("ServerName", out var serverName))
            {
                combined += " " + serverName.GetString();
            }

            var protocol = combined.Contains("Jellyfin", StringComparison.OrdinalIgnoreCase)
                ? MediaSourceProtocol.Jellyfin
                : combined.Contains("Emby", StringComparison.OrdinalIgnoreCase)
                    ? MediaSourceProtocol.Emby
                    : LooksLikeEmbyCompatiblePublicInfo(document.RootElement, baseUri)
                        ? MediaSourceProtocol.Emby
                        : (MediaSourceProtocol?)null;
            if (protocol is null)
            {
                return null;
            }

            var normalized = MediaSourceNormalizer.NormalizeBaseUrl(protocol, baseUri.AbsoluteUri);
            return new NetworkSourceDiscoveryItem
            {
                Name = protocol == MediaSourceProtocol.Jellyfin ? "Jellyfin" : "Emby",
                ProtocolType = protocol == MediaSourceProtocol.Jellyfin ? "jellyfin" : "emby",
                BaseUrl = normalized,
                Description = $"预扫描到的 {(protocol == MediaSourceProtocol.Jellyfin ? "Jellyfin" : "Emby")} 媒体服务器：{normalized}"
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage?> SendProbeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MediaProbeTimeout);
        try
        {
            return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<Uri>> DiscoverMediaServerEndpointCandidatesAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, Uri> results = new(StringComparer.OrdinalIgnoreCase);
        AddUri(results, "http://127.0.0.1:32400");
        AddUri(results, "http://localhost:32400");
        AddUri(results, "http://127.0.0.1:8096");
        AddUri(results, "http://localhost:8096");
        AddUri(results, "http://127.0.0.1:8097");
        AddUri(results, "http://localhost:8097");
        AddUri(results, "http://127.0.0.1:8098");
        AddUri(results, "http://localhost:8098");
        AddUri(results, "http://127.0.0.1:45722");
        AddUri(results, "http://localhost:45722");
        AddUri(results, "http://127.0.0.1:45721");
        AddUri(results, "http://localhost:45721");
        AddUri(results, "https://127.0.0.1:8920");
        AddUri(results, "https://localhost:8920");

        foreach (var uri in await DiscoverPlexGdmUrisAsync(cancellationToken))
        {
            AddUri(results, uri);
        }

        foreach (var uri in await DiscoverEmbyUdpUrisAsync(cancellationToken))
        {
            AddUri(results, uri);
        }

        foreach (var uri in await DiscoverSsdpLocationUrisAsync(cancellationToken))
        {
            AddUri(results, uri);
        }

        return results.Values.ToList();
    }

    private static async Task<IReadOnlyList<Uri>> DiscoverLanHttpEndpointCandidatesAsync(CancellationToken cancellationToken)
    {
        var hosts = EnumerateLanProbeAddresses();
        if (hosts.Count == 0)
        {
            return [];
        }

        var probes = hosts
            .SelectMany(host => LanHttpProbeEndpoints.Select(endpoint => (Host: host, endpoint.Scheme, endpoint.Port)))
            .ToList();
        using var gate = new SemaphoreSlim(128);
        var tasks = probes.Select(async probe =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (!await CanConnectTcpAsync(probe.Host, probe.Port, cancellationToken))
                {
                    return null;
                }

                return new Uri($"{probe.Scheme}://{probe.Host}:{probe.Port}");
            }
            finally
            {
                gate.Release();
            }
        });

        return (await Task.WhenAll(tasks))
            .OfType<Uri>()
            .DistinctBy(static uri => uri.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<Uri> BuildLanHttpEndpointCandidatesFromSmbServers(IEnumerable<string> smbServers)
    {
        HashSet<string> hosts = new(StringComparer.OrdinalIgnoreCase);
        foreach (var server in smbServers)
        {
            if (TryGetSmbHost(server, out var host))
            {
                if (IsValidHttpProbeHost(host))
                {
                    hosts.Add(host);
                }
            }
        }

        foreach (var host in hosts)
        {
            foreach (var endpoint in LanHttpProbeEndpoints)
            {
                yield return new Uri($"{endpoint.Scheme}://{host}:{endpoint.Port}");
            }
        }
    }

    private static async Task<IReadOnlyList<string>> DiscoverSmbServersByTcpAsync(CancellationToken cancellationToken)
    {
        var hosts = EnumerateLanProbeAddresses();
        if (hosts.Count == 0)
        {
            return [];
        }

        using var gate = new SemaphoreSlim(128);
        var tasks = hosts.Select(async host =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await CanConnectTcpAsync(host, 445, cancellationToken)
                    ? $@"\\{host}"
                    : null;
            }
            finally
            {
                gate.Release();
            }
        });

        return (await Task.WhenAll(tasks))
            .Where(static server => !string.IsNullOrWhiteSpace(server))
            .Select(static server => server!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static server => server, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<bool> CanConnectTcpAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(LanTcpProbeTimeout);
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Parse(host), port, timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string> EnumerateLanProbeAddresses()
    {
        List<string> addresses = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        void AddAddress(IPAddress candidate)
        {
            if (!IsPrivateProbeAddress(candidate))
            {
                return;
            }

            var value = candidate.ToString();
            if (seen.Add(value))
            {
                addresses.Add(value);
            }
        }

        AddAddress(IPAddress.Loopback);

        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var properties = networkInterface.GetIPProperties();
                foreach (var gateway in properties.GatewayAddresses)
                {
                    AddAddress(gateway.Address);
                }

                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                        unicast.IPv4Mask is null ||
                        !IsPrivateProbeAddress(unicast.Address))
                    {
                        continue;
                    }

                    AddAddress(unicast.Address);
                    foreach (var address in EnumerateSubnetProbeAddresses(unicast.Address, unicast.IPv4Mask))
                    {
                        AddAddress(address);
                    }
                }
            }
        }
        catch
        {
        }

        return addresses.Take(1024).ToList();
    }

    private static IEnumerable<IPAddress> EnumerateSubnetProbeAddresses(IPAddress address, IPAddress mask)
    {
        var addressValue = ToIPv4UInt32(address);
        var maskValue = ToIPv4UInt32(mask);
        var network = addressValue & maskValue;
        var broadcast = network | ~maskValue;
        var hostCount = broadcast > network ? broadcast - network - 1 : 0;
        if (hostCount is > 0 and <= 510)
        {
            for (var value = network + 1; value < broadcast; value++)
            {
                yield return FromIPv4UInt32(value);
            }

            yield break;
        }

        var local24 = addressValue & 0xFFFFFF00u;
        for (uint suffix = 1; suffix <= 254; suffix++)
        {
            yield return FromIPv4UInt32(local24 | suffix);
        }
    }

    private static uint ToIPv4UInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) |
               ((uint)bytes[1] << 16) |
               ((uint)bytes[2] << 8) |
               bytes[3];
    }

    private static IPAddress FromIPv4UInt32(uint value)
    {
        return new IPAddress(
        [
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        ]);
    }

    private static bool IsPrivateProbeAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return IPAddress.IsLoopback(address) ||
               bytes[0] == 10 ||
               bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127 ||
               bytes[0] == 169 && bytes[1] == 254 ||
               bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31 ||
               bytes[0] == 192 && bytes[1] == 168;
    }

    private static bool TryGetSmbHost(string uncPath, out string host)
    {
        host = string.Empty;
        var parts = uncPath.Trim()
            .Trim('\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
        {
            return false;
        }

        host = parts[0];
        return true;
    }

    private static bool IsValidHttpProbeHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) ||
            host.Contains('\\', StringComparison.Ordinal) ||
            host.Contains('/', StringComparison.Ordinal) ||
            host.Contains(' ', StringComparison.Ordinal))
        {
            return false;
        }

        return Uri.CheckHostName(host) != UriHostNameType.Unknown;
    }

    private static string ResolveSmbDisplayName(string uncPath)
    {
        var parts = uncPath.Trim()
            .Trim('\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[1]))
        {
            return parts[1];
        }

        return string.IsNullOrWhiteSpace(uncPath) ? "SMB" : uncPath.Trim();
    }

    private static bool LooksLikeWebDavProbeResponse(Uri probeUri, HttpResponseMessage response)
    {
        var isMultiStatus = (int)response.StatusCode == 207;
        var hasDavHeader = response.Headers.Contains("DAV");
        if (isMultiStatus || hasDavHeader)
        {
            return true;
        }

        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
        {
            return false;
        }

        var path = probeUri.AbsolutePath.Trim('/');
        return IsLikelyWebDavPort(probeUri.Port) ||
               path.Equals("dav", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("webdav", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyWebDavPort(int port)
    {
        return port is 5005 or 5006 or 5244;
    }

    private static bool LooksLikeEmbyCompatiblePublicInfo(JsonElement root, Uri baseUri)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !IsLikelyEmbyCompatiblePort(baseUri.Port))
        {
            return false;
        }

        return TryGetJsonString(root, "ServerName", out _) ||
               TryGetJsonString(root, "LocalAddress", out _) ||
               TryGetJsonString(root, "Id", out _) ||
               TryGetJsonString(root, "Version", out _);
    }

    private static bool LooksLikeOmniPlayDockerResponse(string path, string body, HttpResponseMessage response)
    {
        var normalized = body.ToLowerInvariant();
        if (path.Contains("auth/status", StringComparison.OrdinalIgnoreCase) &&
            (normalized.Contains("\"issetuprequired\"", StringComparison.Ordinal) ||
             normalized.Contains("\"isauthenticated\"", StringComparison.Ordinal)))
        {
            return true;
        }

        return normalized.Contains("omniplay.server", StringComparison.Ordinal) ||
               normalized.Contains("\"omniplay\"", StringComparison.Ordinal) ||
               (normalized.Contains("\"service\"", StringComparison.Ordinal) &&
                normalized.Contains("omniplay", StringComparison.Ordinal)) ||
               (response.Headers.Server.Any(product => product.Product?.Name?.Contains("Kestrel", StringComparison.OrdinalIgnoreCase) == true) &&
                normalized.Contains("database", StringComparison.Ordinal));
    }

    private static bool IsLikelyEmbyCompatiblePort(int port)
    {
        return port is 8096 or 8097 or 8098 or 8099 or 8920;
    }

    private static async Task<IReadOnlyList<Uri>> DiscoverPlexGdmUrisAsync(CancellationToken cancellationToken)
    {
        List<Uri> results = [];
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork)
            {
                EnableBroadcast = true
            };
            var payload = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.0\r\n\r\n");
            await udp.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, 32414));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            while (!timeout.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await udp.ReceiveAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var text = Encoding.UTF8.GetString(received.Buffer);
                var headers = ParseHeaderBlock(text);
                if (!LooksLikePlexGdmResponse(text, headers))
                {
                    continue;
                }

                if (headers.TryGetValue("Port", out var portText) &&
                    int.TryParse(portText, out var port) &&
                    Uri.TryCreate($"http://{received.RemoteEndPoint.Address}:{port}", UriKind.Absolute, out var uri))
                {
                    results.Add(uri);
                }
            }
        }
        catch
        {
        }

        return results;
    }

    private static async Task<IReadOnlyList<Uri>> DiscoverEmbyUdpUrisAsync(CancellationToken cancellationToken)
    {
        List<Uri> results = [];
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork)
            {
                EnableBroadcast = true
            };
            var payload = Encoding.UTF8.GetBytes("who is EmbyServer?");
            foreach (var endpoint in GetUdpBroadcastEndpoints(7359))
            {
                await udp.SendAsync(payload, payload.Length, endpoint);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            while (!timeout.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await udp.ReceiveAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var text = Encoding.UTF8.GetString(received.Buffer);
                if (TryCreateEmbyUdpDiscoveryUri(text, received.RemoteEndPoint.Address, out var uri))
                {
                    results.Add(uri);
                }
            }
        }
        catch
        {
        }

        return results;
    }

    private static IReadOnlyList<IPEndPoint> GetUdpBroadcastEndpoints(int port)
    {
        Dictionary<string, IPEndPoint> endpoints = new(StringComparer.OrdinalIgnoreCase)
        {
            [IPAddress.Broadcast.ToString()] = new(IPAddress.Broadcast, port)
        };

        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                        unicast.IPv4Mask is null ||
                        CreateBroadcastAddress(unicast.Address, unicast.IPv4Mask) is not { } broadcast)
                    {
                        continue;
                    }

                    endpoints[broadcast.ToString()] = new IPEndPoint(broadcast, port);
                }
            }
        }
        catch
        {
        }

        return endpoints.Values.ToList();
    }

    private static IPAddress? CreateBroadcastAddress(IPAddress address, IPAddress mask)
    {
        var addressBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        if (addressBytes.Length != 4 || maskBytes.Length != 4)
        {
            return null;
        }

        var broadcastBytes = new byte[4];
        for (var i = 0; i < broadcastBytes.Length; i++)
        {
            broadcastBytes[i] = (byte)(addressBytes[i] | (maskBytes[i] ^ 0xFF));
        }

        return new IPAddress(broadcastBytes);
    }

    private static bool TryCreateEmbyUdpDiscoveryUri(string text, IPAddress remoteAddress, out Uri uri)
    {
        uri = null!;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (TryGetJsonString(document.RootElement, "Address", out var address) &&
                Uri.TryCreate(address, UriKind.Absolute, out var discovered) &&
                (discovered.Scheme == Uri.UriSchemeHttp || discovered.Scheme == Uri.UriSchemeHttps))
            {
                uri = discovered;
                return true;
            }

            if ((TryGetJsonString(document.RootElement, "Id", out _) ||
                 TryGetJsonString(document.RootElement, "Name", out _)) &&
                Uri.TryCreate($"http://{remoteAddress}:8096", UriKind.Absolute, out var fallback))
            {
                uri = fallback;
                return true;
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    private static bool TryGetJsonString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) ||
                property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            value = property.Value.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        return false;
    }

    private static async Task<IReadOnlyList<Uri>> DiscoverSsdpLocationUrisAsync(CancellationToken cancellationToken)
    {
        List<Uri> results = [];
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var payload = Encoding.ASCII.GetBytes(
                "M-SEARCH * HTTP/1.1\r\n" +
                "HOST: 239.255.255.250:1900\r\n" +
                "MAN: \"ssdp:discover\"\r\n" +
                "MX: 1\r\n" +
                "ST: ssdp:all\r\n\r\n");
            await udp.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            while (!timeout.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await udp.ReceiveAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var text = Encoding.UTF8.GetString(received.Buffer);
                var headers = ParseHeaderBlock(text);
                if (headers.TryGetValue("Location", out var location) &&
                    Uri.TryCreate(location, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    results.Add(new Uri(uri.GetLeftPart(UriPartial.Authority)));
                }
            }
        }
        catch
        {
        }

        return results;
    }

    private static Dictionary<string, string> ParseHeaderBlock(string text)
    {
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = rawLine.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            headers[rawLine[..separator].Trim()] = rawLine[(separator + 1)..].Trim();
        }

        return headers;
    }

    private static bool LooksLikePlexGdmResponse(string text, IReadOnlyDictionary<string, string> headers)
    {
        return text.Contains("Plex Media Server", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("plex/media-server", StringComparison.OrdinalIgnoreCase) ||
               headers.TryGetValue("Product", out var product) && product.Contains("Plex", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddUri(IDictionary<string, Uri> results, string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            AddUri(results, parsed);
        }
    }

    private static void AddUri(IDictionary<string, Uri> results, Uri uri)
    {
        if ((!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return;
        }

        var authority = uri.GetLeftPart(UriPartial.Authority);
        results[authority] = new Uri(authority);
    }

    private async Task<IReadOnlyList<NetworkShareFolderItem>> ListWebDavFoldersAsync(
        string baseUrl,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var source = new MediaSource
        {
            Name = ResolveWebDavName(baseUrl),
            ProtocolType = "webdav",
            BaseUrl = baseUrl,
            AuthConfig = MediaSourceAuthConfigSerializer.SerializeWebDav(new WebDavAuthConfig(username, password))
        };

        if (!source.IsValidConfiguration() ||
            !Uri.TryCreate(AppendTrailingSlash(source.GetNormalizedBaseUrl()), UriKind.Absolute, out var rootUri))
        {
            return [];
        }

        List<NetworkShareFolderItem> folders =
        [
            new()
            {
                Name = source.Name,
                ProtocolType = "webdav",
                BaseUrl = source.GetNormalizedBaseUrl(),
                Description = source.GetNormalizedBaseUrl(),
                AuthConfig = source.AuthConfig
            }
        ];

        using var request = BuildWebDavRequest(
            rootUri,
            MediaSourceAuthConfigSerializer.DeserializeWebDav(source.AuthConfig));
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        var rootKey = NormalizeUri(rootUri);

        foreach (var folderUri in ParseWebDavFolders(document, rootUri))
        {
            if (string.Equals(NormalizeUri(folderUri), rootKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = folderUri.AbsoluteUri.TrimEnd('/');
            folders.Add(new NetworkShareFolderItem
            {
                Name = ResolveWebDavName(url),
                ProtocolType = "webdav",
                BaseUrl = url,
                Description = url,
                AuthConfig = source.AuthConfig
            });
        }

        return folders
            .GroupBy(static folder => folder.BaseUrl, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static folder => folder.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<NetworkShareFolderItem>> ListSmbFoldersAsync(
        string baseUrl,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var normalizedBaseUrl = MediaSourceNormalizer.NormalizeBaseUrl(MediaSourceProtocol.Smb, baseUrl);
        if (!normalizedBaseUrl.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return [];
        }

        var authConfig = MediaSourceAuthConfigSerializer.SerializeWebDav(new WebDavAuthConfig(username, password));
        var parts = normalizedBaseUrl.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            using var ipcConnection = SmbNetworkConnection.Connect($@"{normalizedBaseUrl.TrimEnd('\\')}\IPC$", username, password);
            return (await ListSmbSharesAsync(normalizedBaseUrl, cancellationToken))
                .Select(share => new NetworkShareFolderItem
                {
                    Name = ResolveSmbDisplayName(share),
                    ProtocolType = "smb",
                    BaseUrl = share,
                    Description = share,
                    AuthConfig = authConfig
                })
                .ToList();
        }

        using var connection = SmbNetworkConnection.Connect(normalizedBaseUrl, username, password);
        List<NetworkShareFolderItem> folders =
        [
            new()
            {
                Name = Path.GetFileName(normalizedBaseUrl.TrimEnd('\\')) ?? normalizedBaseUrl,
                ProtocolType = "smb",
                BaseUrl = normalizedBaseUrl,
                Description = normalizedBaseUrl,
                AuthConfig = authConfig
            }
        ];

        if (Directory.Exists(normalizedBaseUrl))
        {
            foreach (var directory in Directory.EnumerateDirectories(normalizedBaseUrl).Take(200))
            {
                folders.Add(new NetworkShareFolderItem
                {
                    Name = Path.GetFileName(directory.TrimEnd('\\')) ?? ResolveSmbDisplayName(directory),
                    ProtocolType = "smb",
                    BaseUrl = directory.TrimEnd('\\'),
                    Description = directory.TrimEnd('\\'),
                    AuthConfig = authConfig
                });
            }
        }

        return folders;
    }

    private static async Task<IReadOnlyList<string>> ListSmbServersAsync(CancellationToken cancellationToken)
    {
        var output = await RunNetViewAsync("view", cancellationToken);
        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(static line => line.StartsWith(@"\\", StringComparison.Ordinal))
            .Select(static line => MultiSpace.Split(line)[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static line => line, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<IReadOnlyList<string>> ListSmbSharesAsync(string server, CancellationToken cancellationToken)
    {
        var output = await RunNetViewAsync($"view {server}", cancellationToken);
        List<string> shares = [];
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 ||
                line.StartsWith("Share name", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("共享名", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("在 \\\\", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Shared resources at", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Share resources at", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Server Name", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("服务器名称", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("The command", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("命令成功", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("没有", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            var shareName = MultiSpace.Split(line)[0];
            if (shareName.Length == 0 ||
                shareName.EndsWith('$') ||
                shareName.Contains('\\', StringComparison.Ordinal) ||
                shareName.Contains(':', StringComparison.Ordinal) ||
                shareName.Equals("Disk", StringComparison.OrdinalIgnoreCase) ||
                shareName.Equals("磁盘", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            shares.Add($@"{server.TrimEnd('\\')}\{shareName}");
        }

        return shares
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static share => share, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<string> RunNetViewAsync(string arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "net",
                Arguments = arguments,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });

            if (process is null)
            {
                return string.Empty;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                return await outputTask;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return string.Empty;
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    private static HttpRequestMessage BuildWebDavRequest(Uri directoryUri, WebDavAuthConfig? authConfig)
    {
        var request = new HttpRequestMessage(PropFindMethod, directoryUri)
        {
            Content = new StringContent(
                """
                <?xml version="1.0" encoding="utf-8"?>
                <d:propfind xmlns:d="DAV:">
                  <d:prop>
                    <d:resourcetype />
                  </d:prop>
                </d:propfind>
                """,
                Encoding.UTF8,
                "application/xml")
        };

        request.Headers.TryAddWithoutValidation("Depth", "1");
        if (authConfig is not null &&
            (!string.IsNullOrWhiteSpace(authConfig.Username) || !string.IsNullOrEmpty(authConfig.Password)))
        {
            var raw = $"{authConfig.Username}:{authConfig.Password}";
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        }

        return request;
    }

    private static IEnumerable<Uri> ParseWebDavFolders(XDocument document, Uri requestUri)
    {
        foreach (var responseElement in document.Descendants(DavNamespace + "response"))
        {
            var href = responseElement.Element(DavNamespace + "href")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(requestUri, href, out var resourceUri))
            {
                continue;
            }

            var isDirectory = responseElement
                                  .Descendants(DavNamespace + "resourcetype")
                                  .Any(static element => element.Element(DavNamespace + "collection") is not null)
                              || href.EndsWith("/", StringComparison.Ordinal);
            if (isDirectory)
            {
                yield return EnsureDirectoryUri(resourceUri);
            }
        }
    }

    private static Uri EnsureDirectoryUri(Uri resourceUri)
    {
        var absoluteUri = resourceUri.AbsoluteUri;
        return absoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? resourceUri
            : new Uri($"{absoluteUri}/");
    }

    private static string AppendTrailingSlash(string value)
    {
        return value.EndsWith("/", StringComparison.Ordinal) ? value : $"{value}/";
    }

    private static string NormalizeUri(Uri uri)
    {
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static string ResolveWebDavName(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return "WebDAV";
        }

        var lastSegment = uri.Segments.LastOrDefault()?.Trim('/');
        return string.IsNullOrWhiteSpace(lastSegment)
            ? uri.Host
            : Uri.UnescapeDataString(lastSegment);
    }

    private static string ResolveWebDavDiscoveryName(Uri probeUri, string normalizedBaseUrl)
    {
        var path = probeUri.AbsolutePath.Trim('/');
        return string.IsNullOrWhiteSpace(path) ||
               path.Equals("dav", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("webdav", StringComparison.OrdinalIgnoreCase)
            ? "WebDAV"
            : ResolveWebDavName(normalizedBaseUrl);
    }
}
