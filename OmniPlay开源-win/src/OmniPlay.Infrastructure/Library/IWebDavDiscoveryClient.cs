using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Infrastructure.Library;

public interface IWebDavDiscoveryClient
{
    Task<IReadOnlyList<WebDavFileEntry>> EnumerateFilesAsync(
        MediaSource source,
        CancellationToken cancellationToken = default);
}

public sealed record WebDavFileEntry(
    string RelativePath,
    string FileName,
    long ContentLength);
