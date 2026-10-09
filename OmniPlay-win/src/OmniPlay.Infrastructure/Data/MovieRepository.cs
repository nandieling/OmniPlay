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
                                movie.metadataLanguage,
                                movie.director,
                                movie.mainCast,
                                movie.directors,
                                movie.casts,
                                movie.tmdbId
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

    public async Task UpdatePosterPathAsync(long id, string? posterPath, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE movie SET posterPath = @PosterPath WHERE id = @Id",
                new { Id = id, PosterPath = posterPath },
                cancellationToken: cancellationToken));
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

        // 未刮削的合成占位只要标题可用就要在首页展示，保证其能进入自动刮削队列（对齐 mac 版 fetchVisibleLibrary）。
        return MediaNameParser.IsUsableLibraryDisplayTitle(movie.Title);
    }
}
