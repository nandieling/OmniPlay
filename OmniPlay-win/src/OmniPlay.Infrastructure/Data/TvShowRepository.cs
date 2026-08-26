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
                                tvShow.metadataLanguage
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

        return MediaNameParser.IsUsableLibraryDisplayTitle(show.Title) &&
               HasVisibleMetadata(show);
    }

    private static bool HasVisibleMetadata(TvShow show)
    {
        return show.IsLocked ||
               HasText(show.FirstAirDate) ||
               HasText(show.Overview) ||
               HasText(show.PosterPath) ||
               show.VoteAverage.HasValue ||
               HasText(show.ProductionCountryCodes) ||
               HasText(show.OriginalLanguage) ||
               HasText(show.MetadataLanguage);
    }

    private static bool HasText(string? value)
    {
        return !string.IsNullOrWhiteSpace(value);
    }
}
