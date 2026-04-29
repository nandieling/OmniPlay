using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Core.Interfaces;

public interface IMediaSourceRepository
{
    Task<IReadOnlyList<MediaSource>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<long> AddAsync(MediaSource source, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(MediaSource source, CancellationToken cancellationToken = default);

    Task<bool> SetEnabledAsync(long sourceId, bool isEnabled, DateTimeOffset now, CancellationToken cancellationToken = default);

    Task<bool> SoftRemoveAsync(long sourceId, DateTimeOffset now, CancellationToken cancellationToken = default);

    Task<int> PurgeExpiredInactiveAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(long sourceId, CancellationToken cancellationToken = default);
}
