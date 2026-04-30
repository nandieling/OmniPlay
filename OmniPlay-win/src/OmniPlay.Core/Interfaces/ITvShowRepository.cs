using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Core.Interfaces;

public interface ITvShowRepository
{
    Task<IReadOnlyList<TvShow>> GetAllAsync(CancellationToken cancellationToken = default);
}
