using System.Net.Http.Json;
using System.Text.Json;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Infrastructure.Library;

public sealed class OmniPlayDockerClient : IOmniPlayDockerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;

    public OmniPlayDockerClient(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public async Task<IReadOnlyList<OmniPlayDockerLibraryItem>> GetLibraryItemsAsync(
        MediaSource source,
        CancellationToken cancellationToken = default)
    {
        using var request = BuildRequest(source.BaseUrl, source.AuthConfig, HttpMethod.Get, "/api/library/items");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<OmniPlayDockerLibraryItem>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task<OmniPlayDockerLibraryDetail?> GetLibraryDetailAsync(
        MediaSource source,
        string libraryItemId,
        CancellationToken cancellationToken = default)
    {
        using var request = BuildRequest(
            source.BaseUrl,
            source.AuthConfig,
            HttpMethod.Get,
            $"/api/library/items/{Uri.EscapeDataString(libraryItemId)}");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<OmniPlayDockerLibraryDetail>(JsonOptions, cancellationToken);
    }

    public Task<byte[]> GetPosterDataAsync(
        MediaSource source,
        string posterAssetId,
        CancellationToken cancellationToken = default)
    {
        return GetAssetDataAsync(source, "posters", posterAssetId, cancellationToken);
    }

    public Task<byte[]> GetThumbnailDataAsync(
        MediaSource source,
        string thumbnailAssetId,
        CancellationToken cancellationToken = default)
    {
        return GetAssetDataAsync(source, "thumbnails", thumbnailAssetId, cancellationToken);
    }

    public async Task<string?> GetPlaybackUrlAsync(
        string baseUrl,
        string? authConfig,
        string videoFileId,
        CancellationToken cancellationToken = default)
    {
        using var request = BuildRequest(
            baseUrl,
            authConfig,
            HttpMethod.Post,
            $"/api/playback/files/{Uri.EscapeDataString(videoFileId)}/ticket");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var ticket = await response.Content.ReadFromJsonAsync<PlaybackTicket>(JsonOptions, cancellationToken);
        return ticket?.StreamUrl is { Length: > 0 } streamUrl
            ? AbsoluteUrl(baseUrl, streamUrl)
            : null;
    }

    public async Task<OmniPlayDockerPlaybackProgress?> GetPlaybackProgressAsync(
        string baseUrl,
        string? authConfig,
        string videoFileId,
        CancellationToken cancellationToken = default)
    {
        using var request = BuildRequest(
            baseUrl,
            authConfig,
            HttpMethod.Get,
            $"/api/playback/files/{Uri.EscapeDataString(videoFileId)}/progress");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<OmniPlayDockerPlaybackProgress>(JsonOptions, cancellationToken);
    }

    public async Task UpdateProgressAsync(
        string baseUrl,
        string? authConfig,
        string videoFileId,
        double positionSeconds,
        double durationSeconds,
        CancellationToken cancellationToken = default)
    {
        using var request = BuildRequest(baseUrl, authConfig, HttpMethod.Post, "/api/playback/progress");
        request.Content = JsonContent.Create(new
        {
            videoFileId,
            positionSeconds = Math.Max(positionSeconds, 0),
            durationSeconds = Math.Max(durationSeconds, 0),
            userId = "local"
        }, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task ImportDoubanMetadataAsync(
        MediaSource source,
        string libraryItemId,
        DoubanMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        using var request = BuildRequest(
            source.BaseUrl,
            source.AuthConfig,
            HttpMethod.Post,
            $"/api/library/items/{Uri.EscapeDataString(libraryItemId)}/douban/import");
        request.Content = JsonContent.Create(new DoubanMetadataImportRequest(
            metadata.SubjectId,
            metadata.SubjectUrl,
            metadata.Title,
            metadata.OriginalTitle,
            metadata.Year,
            metadata.Rating,
            metadata.RatingCount,
            metadata.Summary,
            Genres: null,
            Countries: null,
            metadata.PosterUrl,
            metadata.FetchedAt), options: JsonOptions);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<byte[]> GetAssetDataAsync(
        MediaSource source,
        string assetKind,
        string assetId,
        CancellationToken cancellationToken)
    {
        using var request = BuildRequest(
            source.BaseUrl,
            source.AuthConfig,
            HttpMethod.Get,
            $"/api/assets/{assetKind}/{Uri.EscapeDataString(assetId.Trim())}");
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("image/*");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return data.Length > 0
            ? data
            : throw new InvalidDataException("Docker 图片资源为空。");
    }

    private static HttpRequestMessage BuildRequest(string baseUrl, string? authConfig, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, AbsoluteUrl(baseUrl, path));
        request.Headers.Accept.ParseAdd("application/json");
        var config = MediaSourceAuthConfigSerializer.DeserializeOmniPlayDocker(authConfig);
        if (!string.IsNullOrWhiteSpace(config?.SessionCookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"omniplay_session={config.SessionCookie.Trim()}");
        }

        return request;
    }

    private static string AbsoluteUrl(string baseUrl, string value)
    {
        var normalizedBase = MediaSourceNormalizer.NormalizeBaseUrl(MediaSourceProtocol.OmniPlayDocker, baseUrl);
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        return new Uri(new Uri(normalizedBase.TrimEnd('/') + "/"), value.TrimStart('/')).ToString();
    }

    private sealed record PlaybackTicket(string StreamUrl);

    private sealed record DoubanMetadataImportRequest(
        string SubjectId,
        string SubjectUrl,
        string Title,
        string? OriginalTitle,
        string? Year,
        double? Rating,
        int? RatingCount,
        string? Summary,
        string? Genres,
        string? Countries,
        string? PosterUrl,
        DateTimeOffset FetchedAt);
}
