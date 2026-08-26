using Microsoft.Data.Sqlite;

namespace OmniPlay.Infrastructure.Library;

internal static class TvLibraryItemMerger
{
    public static async Task<bool> MergeDuplicatesByTmdbIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string canonicalLibraryItemId,
        int tmdbId,
        string updatedAt,
        CancellationToken cancellationToken)
    {
        if (tmdbId <= 0 || string.IsNullOrWhiteSpace(canonicalLibraryItemId))
        {
            return false;
        }

        await ExecuteAsync(connection, transaction, """
            CREATE TEMP TABLE IF NOT EXISTS merge_duplicate_tv_item_ids (
                id TEXT PRIMARY KEY
            );
            """, cancellationToken);
        await ExecuteAsync(connection, transaction, "DELETE FROM merge_duplicate_tv_item_ids;", cancellationToken);
        await ExecuteAsync(connection, transaction, """
            INSERT OR IGNORE INTO merge_duplicate_tv_item_ids (id)
            SELECT DISTINCT tv.library_item_id
            FROM tv_shows tv
            JOIN library_items li ON li.id = tv.library_item_id
            WHERE tv.tmdb_id = $tmdbId
              AND tv.library_item_id <> $canonicalLibraryItemId
              AND li.item_kind = 'tv'
              AND EXISTS (
                  SELECT 1
                  FROM video_files vf
                  WHERE vf.library_item_id = tv.library_item_id
                    AND vf.media_kind = 'tv'
                    AND vf.missing_at IS NULL
              );
            """,
            cancellationToken,
            ("$tmdbId", tmdbId),
            ("$canonicalLibraryItemId", canonicalLibraryItemId));

        var duplicateCount = await ExecuteScalarAsync(connection, transaction, """
            SELECT COUNT(*)
            FROM merge_duplicate_tv_item_ids;
            """, cancellationToken);
        if (duplicateCount <= 0)
        {
            return false;
        }

        var canonicalTvShowId = StableId.Create("show", canonicalLibraryItemId);
        var episodes = await ReadDuplicateEpisodesAsync(connection, transaction, cancellationToken);
        foreach (var episode in episodes)
        {
            var seasonId = StableId.Create("season", canonicalTvShowId, episode.SeasonNumber.ToString());
            var episodeId = StableId.Create("episode", seasonId, episode.EpisodeNumber.ToString());
            await UpsertSeasonAsync(
                connection,
                transaction,
                seasonId,
                canonicalTvShowId,
                episode.SeasonNumber,
                episode.SeasonTitle,
                episode.SeasonPosterAssetId,
                cancellationToken);
            await UpsertEpisodeAsync(
                connection,
                transaction,
                episodeId,
                seasonId,
                episode,
                cancellationToken);
            await ExecuteAsync(connection, transaction, """
                UPDATE video_files
                SET episode_id = $targetEpisodeId
                WHERE episode_id = $sourceEpisodeId
                  AND library_item_id IN (SELECT id FROM merge_duplicate_tv_item_ids);
                """,
                cancellationToken,
                ("$targetEpisodeId", episodeId),
                ("$sourceEpisodeId", episode.Id));
        }

        await ExecuteAsync(connection, transaction, """
            UPDATE video_files
            SET library_item_id = $canonicalLibraryItemId,
                media_kind = 'tv',
                updated_at = $updatedAt
            WHERE library_item_id IN (SELECT id FROM merge_duplicate_tv_item_ids)
              AND media_kind = 'tv';
            """,
            cancellationToken,
            ("$canonicalLibraryItemId", canonicalLibraryItemId),
            ("$updatedAt", updatedAt));
        await ExecuteAsync(connection, transaction, """
            DELETE FROM tv_shows
            WHERE library_item_id IN (SELECT id FROM merge_duplicate_tv_item_ids);
            """, cancellationToken);
        await ExecuteAsync(connection, transaction, """
            DELETE FROM library_items
            WHERE id IN (SELECT id FROM merge_duplicate_tv_item_ids)
              AND NOT EXISTS (
                  SELECT 1
                  FROM video_files vf
                  WHERE vf.library_item_id = library_items.id
                    AND vf.missing_at IS NULL
              );
            """, cancellationToken);
        return true;
    }

    private static async Task<IReadOnlyList<DuplicateEpisode>> ReadDuplicateEpisodesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT e.id,
                   s.season_number,
                   s.title,
                   s.poster_asset_id,
                   e.episode_number,
                   e.title,
                   e.overview,
                   e.still_asset_id,
                   e.air_date
            FROM tv_shows tv
            JOIN seasons s ON s.tv_show_id = tv.id
            JOIN episodes e ON e.season_id = s.id
            WHERE tv.library_item_id IN (SELECT id FROM merge_duplicate_tv_item_ids)
            ORDER BY s.season_number, e.episode_number;
            """;

        List<DuplicateEpisode> episodes = [];
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            episodes.Add(new DuplicateEpisode(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return episodes;
    }

    private static async Task UpsertSeasonAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string seasonId,
        string tvShowId,
        int seasonNumber,
        string? title,
        string? posterAssetId,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            INSERT INTO seasons (id, tv_show_id, season_number, title, poster_asset_id)
            VALUES ($id, $tvShowId, $seasonNumber, $title, $posterAssetId)
            ON CONFLICT(id) DO UPDATE SET
                title = COALESCE(seasons.title, excluded.title),
                poster_asset_id = COALESCE(seasons.poster_asset_id, excluded.poster_asset_id);
            """,
            cancellationToken,
            ("$id", seasonId),
            ("$tvShowId", tvShowId),
            ("$seasonNumber", seasonNumber),
            ("$title", title),
            ("$posterAssetId", posterAssetId));
    }

    private static async Task UpsertEpisodeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string episodeId,
        string seasonId,
        DuplicateEpisode episode,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            INSERT INTO episodes (id, season_id, episode_number, title, overview, still_asset_id, air_date)
            VALUES ($id, $seasonId, $episodeNumber, $title, $overview, $stillAssetId, $airDate)
            ON CONFLICT(id) DO UPDATE SET
                title = COALESCE(episodes.title, excluded.title),
                overview = COALESCE(episodes.overview, excluded.overview),
                still_asset_id = COALESCE(episodes.still_asset_id, excluded.still_asset_id),
                air_date = COALESCE(episodes.air_date, excluded.air_date);
            """,
            cancellationToken,
            ("$id", episodeId),
            ("$seasonId", seasonId),
            ("$episodeNumber", episode.EpisodeNumber),
            ("$title", episode.Title),
            ("$overview", episode.Overview),
            ("$stillAssetId", episode.StillAssetId),
            ("$airDate", episode.AirDate));
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ExecuteScalarAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private sealed record DuplicateEpisode(
        string Id,
        int SeasonNumber,
        string? SeasonTitle,
        string? SeasonPosterAssetId,
        int EpisodeNumber,
        string? Title,
        string? Overview,
        string? StillAssetId,
        string? AirDate);
}
