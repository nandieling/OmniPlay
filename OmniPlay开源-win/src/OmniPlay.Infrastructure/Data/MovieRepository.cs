using Dapper;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Infrastructure.Data;

public sealed class MovieRepository : IMovieRepository
{
    private readonly SqliteDatabase database;

    public MovieRepository(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task<IReadOnlyList<Movie>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        var movies = await connection.QueryAsync<Movie>(
            new CommandDefinition(
                """
                SELECT DISTINCT movie.id,
                                movie.title,
                                movie.releaseDate,
                                movie.overview,
                                movie.posterPath,
                                movie.voteAverage,
                                movie.isLocked,
                                movie.productionCountryCodes,
                                movie.originalLanguage,
                                movie.metadataLanguage
                FROM movie
                JOIN videoFile ON videoFile.movieId = movie.id AND videoFile.mediaType = 'movie'
                JOIN mediaSource ON mediaSource.id = videoFile.sourceId
                                AND mediaSource.isEnabled = 1
                                AND mediaSource.removedAt IS NULL
                ORDER BY movie.title COLLATE NOCASE ASC
                """,
                cancellationToken: cancellationToken));
        return movies.ToList();
    }
}
