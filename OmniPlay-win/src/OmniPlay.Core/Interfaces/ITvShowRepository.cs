using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Core.Interfaces;

public interface ITvShowRepository
{
    Task<IReadOnlyList<TvShow>> GetAllAsync(CancellationToken cancellationToken = default);

    Task UpdatePosterPathAsync(long id, string? posterPath, CancellationToken cancellationToken = default);
}
