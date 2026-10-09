using Dapper;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Infrastructure.Library;

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
                                doubanMetadata.rating AS DoubanRating,
                                tvShow.isLocked,
                                tvShow.productionCountryCodes,
                                tvShow.originalLanguage,
                                tvShow.metadataLanguage,
                                tvShow.director,
                                tvShow.mainCast,
                                tvShow.directors,
                                tvShow.casts
                FROM tvShow
                JOIN videoFile ON videoFile.episodeId = tvShow.id AND videoFile.mediaType = 'tv'
                JOIN mediaSource ON mediaSource.id = videoFile.sourceId
                                AND mediaSource.isEnabled = 1
                                AND mediaSource.removedAt IS NULL
                LEFT JOIN doubanMetadata ON doubanMetadata.itemKind = 'tv'
                                        AND doubanMetadata.entityId = tvShow.id
                ORDER BY tvShow.title COLLATE NOCASE ASC
                """,
                cancellationToken: cancellationToken));
        return shows.Where(ShouldExposeShowOnHome).ToList();
    }

    public async Task UpdatePosterPathAsync(long id, string? posterPath, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE tvShow SET posterPath = @PosterPath WHERE id = @Id",
                new { Id = id, PosterPath = posterPath },
                cancellationToken: cancellationToken));
    }

    private static bool ShouldExposeShowOnHome(TvShow show)
    {
        if (show.Id >= 0)
        {
            return true;
        }

        if (show.IsLocked)
        {
            return true;
        }

        // 未刮削的合成占位只要标题可用就要在首页展示，保证其能进入自动刮削队列（对齐 mac 版 fetchVisibleLibrary）。
        return MediaNameParser.IsUsableLibraryDisplayTitle(show.Title);
    }
}
