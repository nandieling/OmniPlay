using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
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
    private readonly HttpClient httpClient;

    public NetworkShareDiscoveryService(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public async Task<IReadOnlyList<NetworkSourceDiscoveryItem>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        Dictionary<string, NetworkSourceDiscoveryItem> results = new(StringComparer.OrdinalIgnoreCase)
        {
            ["webdav:manual"] = new()
            {
                Name = "手动输入 WebDAV",
                ProtocolType = "webdav",
                BaseUrl = "https://",
                Description = "输入 WebDAV 地址后登录并选择文件夹。"
            },
            ["smb:manual"] = new()
            {
                Name = "手动输入 SMB",
                ProtocolType = "smb",
                BaseUrl = @"\\server\share",
                Description = "输入 SMB 服务器或共享路径后登录并选择文件夹。"
            }
        };

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
        }

        foreach (var server in await ListSmbServersAsync(cancellationToken))
        {
            results[$"smb:{server}"] = new NetworkSourceDiscoveryItem
            {
                Name = server,
                ProtocolType = "smb",
                BaseUrl = server,
                Description = "预扫描到的 SMB 服务器。"
            };
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
            _ => []
        };
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
                    Name = Path.GetFileName(share),
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
                    Name = Path.GetFileName(directory),
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
                line.StartsWith("The command", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("命令成功", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            var shareName = MultiSpace.Split(line)[0];
            if (shareName.Length == 0 || shareName.EndsWith('$'))
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
}
