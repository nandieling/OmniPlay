using Dapper;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Infrastructure.Library;

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
                                doubanMetadata.rating AS DoubanRating,
                                movie.isLocked,
                                movie.productionCountryCodes,
                                movie.originalLanguage,
                                movie.metadataLanguage
                FROM movie
                JOIN videoFile ON videoFile.movieId = movie.id AND videoFile.mediaType = 'movie'
                JOIN mediaSource ON mediaSource.id = videoFile.sourceId
                                AND mediaSource.isEnabled = 1
                                AND mediaSource.removedAt IS NULL
                LEFT JOIN doubanMetadata ON doubanMetadata.itemKind = 'movie'
                                        AND doubanMetadata.entityId = movie.id
                ORDER BY movie.title COLLATE NOCASE ASC
                """,
                cancellationToken: cancellationToken));
        return movies.Where(ShouldExposeMovieOnHome).ToList();
    }

    private static bool ShouldExposeMovieOnHome(Movie movie)
    {
        if (movie.Id is null or >= 0)
        {
            return true;
        }

        if (movie.IsLocked)
        {
            return true;
        }

        return MediaNameParser.IsUsableLibraryDisplayTitle(movie.Title) &&
               HasVisibleMetadata(movie);
    }

    private static bool HasVisibleMetadata(Movie movie)
    {
        return movie.IsLocked ||
               HasText(movie.ReleaseDate) ||
               HasText(movie.Overview) ||
               HasText(movie.PosterPath) ||
               movie.VoteAverage.HasValue ||
               HasText(movie.ProductionCountryCodes) ||
               HasText(movie.OriginalLanguage) ||
               HasText(movie.MetadataLanguage);
    }

    private static bool HasText(string? value)
    {
        return !string.IsNullOrWhiteSpace(value);
    }
}
