using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Infrastructure.Library;

public sealed class WebDavDiscoveryClient : IWebDavDiscoveryClient, IWebDavConnectionTester
{
    private static readonly HttpMethod PropFindMethod = new("PROPFIND");
    private static readonly XNamespace DavNamespace = "DAV:";
    private readonly HttpClient httpClient;

    public WebDavDiscoveryClient(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public async Task<IReadOnlyList<WebDavFileEntry>> EnumerateFilesAsync(
        MediaSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.ProtocolKind != MediaSourceProtocol.WebDav || !source.IsValidConfiguration())
        {
            return [];
        }

        var normalizedBaseUrl = source.GetNormalizedBaseUrl();
        if (!Uri.TryCreate(AppendTrailingSlash(normalizedBaseUrl), UriKind.Absolute, out var rootUri))
        {
            return [];
        }

        var authConfig = MediaSourceAuthConfigSerializer.DeserializeWebDav(source.AuthConfig);
        Dictionary<string, WebDavFileEntry> files = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> visitedDirectories = new(StringComparer.OrdinalIgnoreCase);

        await EnumerateDirectoryAsync(
            rootUri,
            rootUri,
            authConfig,
            files,
            visitedDirectories,
            cancellationToken);

        return files.Values
            .OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<WebDavConnectionTestResult> TestConnectionAsync(
        MediaSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.ProtocolKind != MediaSourceProtocol.WebDav || !source.IsValidConfiguration())
        {
            return new WebDavConnectionTestResult(false, "WebDAV 地址无效，请输入以 http:// 或 https:// 开头的目录地址。");
        }

        var normalizedBaseUrl = source.GetNormalizedBaseUrl();
        if (!Uri.TryCreate(AppendTrailingSlash(normalizedBaseUrl), UriKind.Absolute, out var rootUri))
        {
            return new WebDavConnectionTestResult(false, "WebDAV 地址无效，请检查后重试。");
        }

        try
        {
            using var request = BuildRequest(
                rootUri,
                MediaSourceAuthConfigSerializer.DeserializeWebDav(source.AuthConfig),
                depth: 0);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                return new WebDavConnectionTestResult(
                    true,
                    $"WebDAV 连接成功：HTTP {statusCode} {response.ReasonPhrase}".TrimEnd());
            }

            return response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                    new WebDavConnectionTestResult(false, "WebDAV 连接失败：认证被拒绝，请检查用户名或密码。"),
                _ => new WebDavConnectionTestResult(
                    false,
                    $"WebDAV 连接失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd())
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WebDavConnectionTestResult(false, "WebDAV 连接超时。");
        }
        catch (HttpRequestException ex)
        {
            return new WebDavConnectionTestResult(false, $"WebDAV 连接失败：{ex.Message}");
        }
    }

    private async Task EnumerateDirectoryAsync(
        Uri rootUri,
        Uri directoryUri,
        WebDavAuthConfig? authConfig,
        Dictionary<string, WebDavFileEntry> files,
        HashSet<string> visitedDirectories,
        CancellationToken cancellationToken)
    {
        var directoryKey = NormalizeUri(directoryUri);
        if (!visitedDirectories.Add(directoryKey))
        {
            return;
        }

        using var request = BuildRequest(directoryUri, authConfig, depth: 1);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);

        foreach (var item in ParseResponses(document, directoryUri))
        {
            if (string.Equals(NormalizeUri(item.ResourceUri), directoryKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = ResolveRelativePath(rootUri, item.ResourceUri);
            if (string.IsNullOrWhiteSpace(relativePath) || relativePath.StartsWith("../", StringComparison.Ordinal))
            {
                continue;
            }

            if (item.IsDirectory)
            {
                await EnumerateDirectoryAsync(
                    rootUri,
                    EnsureDirectoryUri(item.ResourceUri),
                    authConfig,
                    files,
                    visitedDirectories,
                    cancellationToken);
                continue;
            }

            files[relativePath] = new WebDavFileEntry(
                relativePath,
                Path.GetFileName(relativePath),
                item.ContentLength);
        }
    }

    private static HttpRequestMessage BuildRequest(Uri directoryUri, WebDavAuthConfig? authConfig, int depth)
    {
        var request = new HttpRequestMessage(PropFindMethod, directoryUri)
        {
            Content = new StringContent(
                """
                <?xml version="1.0" encoding="utf-8"?>
                <d:propfind xmlns:d="DAV:">
                  <d:prop>
                    <d:resourcetype />
                    <d:getcontentlength />
                  </d:prop>
                </d:propfind>
                """,
                Encoding.UTF8,
                "application/xml")
        };

        request.Headers.TryAddWithoutValidation("Depth", depth.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (authConfig is not null &&
            (!string.IsNullOrWhiteSpace(authConfig.Username) || !string.IsNullOrEmpty(authConfig.Password)))
        {
            var raw = $"{authConfig.Username}:{authConfig.Password}";
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        }

        return request;
    }

    private static IEnumerable<WebDavResponseItem> ParseResponses(XDocument document, Uri requestUri)
    {
        foreach (var responseElement in document.Descendants(DavNamespace + "response"))
        {
            var href = responseElement.Element(DavNamespace + "href")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(requestUri, href, out var resourceUri))
            {
                continue;
            }

            var prop = responseElement
                .Elements(DavNamespace + "propstat")
                .Where(static propStat =>
                    (propStat.Element(DavNamespace + "status")?.Value?.Contains(" 200 ", StringComparison.Ordinal) ?? false))
                .Select(static propStat => propStat.Element(DavNamespace + "prop"))
                .FirstOrDefault(element => element is not null)
                ?? responseElement
                    .Elements(DavNamespace + "propstat")
                    .Select(static propStat => propStat.Element(DavNamespace + "prop"))
                    .FirstOrDefault(element => element is not null);

            var isDirectory = prop?.Element(DavNamespace + "resourcetype")?.Element(DavNamespace + "collection") is not null
                              || href.EndsWith("/", StringComparison.Ordinal);
            var contentLengthText = prop?.Element(DavNamespace + "getcontentlength")?.Value;

            yield return new WebDavResponseItem(
                isDirectory ? EnsureDirectoryUri(resourceUri) : resourceUri,
                isDirectory,
                long.TryParse(contentLengthText, out var contentLength) ? contentLength : 0);
        }
    }

    private static string ResolveRelativePath(Uri rootUri, Uri resourceUri)
    {
        return MediaSourcePathResolver.NormalizeRelativePath(
            Uri.UnescapeDataString(rootUri.MakeRelativeUri(resourceUri).ToString()));
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

    private sealed record WebDavResponseItem(
        Uri ResourceUri,
        bool IsDirectory,
        long ContentLength);
}
