using Dapper;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Infrastructure.Data;

public sealed class TvShowRepository : ITvShowRepository
{
    private readonly SqliteDatabase database;

    public TvShowRepository(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task<IReadOnlyList<TvShow>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        var shows = await connection.QueryAsync<TvShow>(
            new CommandDefinition(
                """
                SELECT DISTINCT tvShow.id,
                                tvShow.title,
                                tvShow.firstAirDate,
                                tvShow.overview,
                                tvShow.posterPath,
                                tvShow.voteAverage,
                                tvShow.isLocked,
                                tvShow.productionCountryCodes,
                                tvShow.originalLanguage,
                                tvShow.metadataLanguage
                FROM tvShow
                JOIN videoFile ON videoFile.episodeId = tvShow.id AND videoFile.mediaType = 'tv'
                JOIN mediaSource ON mediaSource.id = videoFile.sourceId
                                AND mediaSource.isEnabled = 1
                                AND mediaSource.removedAt IS NULL
                ORDER BY tvShow.title COLLATE NOCASE ASC
                """,
                cancellationToken: cancellationToken));
        return shows.ToList();
    }
}
