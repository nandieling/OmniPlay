using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Core.Interfaces;

public interface IMovieRepository
{
    Task<IReadOnlyList<Movie>> GetAllAsync(CancellationToken cancellationToken = default);

    Task UpdatePosterPathAsync(long id, string? posterPath, CancellationToken cancellationToken = default);
}
