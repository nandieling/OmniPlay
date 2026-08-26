using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Library;

namespace OmniPlay.Infrastructure.Data;

public sealed class LibraryRepository : ILibraryRepository
{
    private const string DefaultUserId = "local";
    private readonly SqliteDatabase database;

    public LibraryRepository(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task<IReadOnlyList<LibraryItemSummary>> GetItemsAsync(CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT li.id,
                   li.item_kind,
                   li.title,
                   li.release_date,
                   li.overview,
                   li.poster_asset_id,
                   li.vote_average,
                   dm.rating AS douban_rating,
                   li.is_locked,
                   COALESCE(MIN(COALESCE(pp.is_watched, 0)), 0) AS is_watched,
                   COUNT(vf.id) AS video_count,
                   CASE
                       WHEN li.item_kind = 'movie' THEN COALESCE(SUM(
                           CASE
                               WHEN COALESCE(NULLIF(vf.duration_seconds, 0), pp.duration_seconds, 0) <= 0 THEN 0
                               WHEN COALESCE(pp.is_watched, 0) = 1 THEN COALESCE(NULLIF(vf.duration_seconds, 0), pp.duration_seconds, 0)
                               ELSE MIN(COALESCE(pp.position_seconds, 0), COALESCE(NULLIF(vf.duration_seconds, 0), pp.duration_seconds, 0))
                           END), 0)
                       ELSE COALESCE(MAX(pp.position_seconds), 0)
                   END AS max_progress,
                   CASE
                       WHEN li.item_kind = 'movie' THEN COALESCE(SUM(COALESCE(NULLIF(vf.duration_seconds, 0), pp.duration_seconds, 0)), 0)
                       ELSE COALESCE(MAX(CASE WHEN vf.duration_seconds > 0 THEN vf.duration_seconds ELSE pp.duration_seconds END), 0)
                   END AS max_duration,
                   li.updated_at
            FROM library_items li
            JOIN video_files vf ON vf.library_item_id = li.id AND vf.missing_at IS NULL
            JOIN media_sources ms ON ms.id = vf.source_id AND ms.is_enabled = 1 AND ms.removed_at IS NULL
            LEFT JOIN douban_metadata dm ON dm.library_item_id = li.id
            LEFT JOIN playback_progress pp ON pp.video_file_id = vf.id AND pp.user_id = $userId
            WHERE li.is_visible = 1
            GROUP BY li.id
            ORDER BY li.sort_title COLLATE NOCASE ASC;
            """;
        command.Parameters.AddWithValue("$userId", DefaultUserId);

        List<LibraryItemSummary> items = [];
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadItemSummary(reader));
        }

        return items;
    }

    public async Task<LibraryItemDetail?> GetItemDetailAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        var item = await GetItemSummaryAsync(connection, id, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var files = await GetVideoFilesAsync(connection, id, cancellationToken);
        var seasons = await GetSeasonsAsync(connection, id, files, cancellationToken);
        var tmdbId = await GetTmdbIdAsync(connection, id, item.ItemKind, cancellationToken);
        var douban = await GetDoubanMetadataAsync(connection, id, cancellationToken);

        return new LibraryItemDetail(
            item.Id,
            item.ItemKind,
            item.Title,
            item.ReleaseDate,
            item.Overview,
            item.PosterAssetId,
            item.VoteAverage,
            item.DoubanRating,
            douban,
            item.IsLocked,
            item.IsWatched,
            item.VideoFileCount,
            item.MaxProgressSeconds,
            item.MaxDurationSeconds,
            item.UpdatedAt,
            tmdbId,
            files,
            seasons);
    }

    public async Task<PlayableVideoFile?> GetPlayableVideoFileAsync(
        string videoFileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoFileId))
        {
            return null;
        }

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT vf.id,
                   vf.file_name,
                   vf.media_kind,
                   vf.file_size_bytes,
                   vf.duration_seconds,
                   vf.relative_path,
                   vf.container,
                   vf.video_codec,
                   vf.audio_codec,
                   vf.subtitle_summary,
                   ms.kind,
                   ms.base_url
            FROM video_files vf
            JOIN media_sources ms ON ms.id = vf.source_id
            WHERE vf.id = $id
              AND vf.missing_at IS NULL
              AND ms.is_enabled = 1
              AND ms.removed_at IS NULL
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", videoFileId.Trim());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!string.Equals(reader.GetString(10), "local", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var sourceRoot = Path.GetFullPath(reader.GetString(11));
        var relativePath = reader.GetString(5).Replace('/', Path.DirectorySeparatorChar);
        var absolutePath = Path.GetFullPath(Path.Combine(sourceRoot, relativePath));
        if (!IsPathInsideRoot(sourceRoot, absolutePath) || !File.Exists(absolutePath))
        {
            return null;
        }

        return new PlayableVideoFile(
            reader.GetString(0),
            absolutePath,
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.GetDouble(4),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    public async Task<string?> GetLibraryItemIdForVideoFileAsync(
        string videoFileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoFileId))
        {
            return null;
        }

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT vf.library_item_id
            FROM video_files vf
            JOIN media_sources ms ON ms.id = vf.source_id
            WHERE vf.id = $id
              AND vf.missing_at IS NULL
              AND ms.is_enabled = 1
              AND ms.removed_at IS NULL
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", videoFileId.Trim());

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string id && !string.IsNullOrWhiteSpace(id) ? id : null;
    }

    public async Task<bool> UpdateVideoFileProbeAsync(
        VideoFileProbeUpdate request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.VideoFileId))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE video_files
            SET duration_seconds = $durationSeconds,
                container = $container,
                video_codec = $videoCodec,
                audio_codec = $audioCodec,
                subtitle_summary = $subtitleSummary,
                probe_json = $probeJson,
                updated_at = $updatedAt
            WHERE id = $id
              AND missing_at IS NULL;
            """;
        command.Parameters.AddWithValue("$id", request.VideoFileId);
        command.Parameters.AddWithValue("$durationSeconds", Math.Max(0, request.DurationSeconds));
        command.Parameters.AddWithValue("$container", NullIfWhiteSpace(request.Container));
        command.Parameters.AddWithValue("$videoCodec", NullIfWhiteSpace(request.VideoCodec));
        command.Parameters.AddWithValue("$audioCodec", NullIfWhiteSpace(request.AudioCodec));
        command.Parameters.AddWithValue("$subtitleSummary", NullIfWhiteSpace(request.SubtitleSummary));
        command.Parameters.AddWithValue("$probeJson", NullIfWhiteSpace(request.ProbeJson));
        command.Parameters.AddWithValue("$updatedAt", now);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> ApplyMetadataMatchAsync(
        LibraryItemMetadataApplyRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.LibraryItemId)
            || string.IsNullOrWhiteSpace(request.Title)
            || request.TmdbId <= 0)
        {
            return false;
        }

        var mediaType = string.Equals(request.MediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = database.OpenConnection();
        var itemKind = await GetLibraryItemKindAsync(connection, request.LibraryItemId.Trim(), cancellationToken);
        if (itemKind is null)
        {
            return false;
        }

        using var transaction = connection.BeginTransaction();
        string? posterAssetId = null;
        if (!string.IsNullOrWhiteSpace(request.PosterLocalPath))
        {
            posterAssetId = StableId.Create(
                "poster",
                mediaType,
                request.TmdbId.ToString(),
                request.PosterPath ?? request.PosterLocalPath);
            using var poster = connection.CreateCommand();
            poster.Transaction = transaction;
            poster.CommandText = """
                INSERT INTO poster_assets (id, remote_path, local_path, width, height, created_at)
                VALUES ($id, $remotePath, $localPath, NULL, NULL, $createdAt)
                ON CONFLICT(id) DO UPDATE SET
                    remote_path = excluded.remote_path,
                    local_path = excluded.local_path;
                """;
            poster.Parameters.AddWithValue("$id", posterAssetId);
            poster.Parameters.AddWithValue("$remotePath", string.IsNullOrWhiteSpace(request.PosterPath) ? DBNull.Value : request.PosterPath);
            poster.Parameters.AddWithValue("$localPath", request.PosterLocalPath);
            poster.Parameters.AddWithValue("$createdAt", now);
            await poster.ExecuteNonQueryAsync(cancellationToken);
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE library_items
            SET title = $title,
                sort_title = $sortTitle,
                release_date = COALESCE($releaseDate, release_date),
                overview = COALESCE($overview, overview),
                poster_asset_id = COALESCE($posterAssetId, poster_asset_id),
                vote_average = COALESCE($voteAverage, vote_average),
                is_locked = $isLocked,
                is_visible = 1,
                updated_at = $updatedAt
            WHERE id = $id;
            """;
        update.Parameters.AddWithValue("$id", request.LibraryItemId.Trim());
        update.Parameters.AddWithValue("$title", request.Title.Trim());
        update.Parameters.AddWithValue("$sortTitle", request.Title.Trim().ToLowerInvariant());
        update.Parameters.AddWithValue("$releaseDate", string.IsNullOrWhiteSpace(request.ReleaseDate) ? DBNull.Value : request.ReleaseDate);
        update.Parameters.AddWithValue("$overview", string.IsNullOrWhiteSpace(request.Overview) ? DBNull.Value : request.Overview);
        update.Parameters.AddWithValue("$posterAssetId", string.IsNullOrWhiteSpace(posterAssetId) ? DBNull.Value : posterAssetId);
        update.Parameters.AddWithValue("$voteAverage", request.VoteAverage ?? (object)DBNull.Value);
        update.Parameters.AddWithValue("$isLocked", request.LockMetadata ? 1 : 0);
        update.Parameters.AddWithValue("$updatedAt", now);
        var updated = await update.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (updated)
        {
            await UpdateTmdbIdAsync(
                connection,
                transaction,
                request.LibraryItemId.Trim(),
                itemKind,
                request.TmdbId,
                cancellationToken);
        }

        transaction.Commit();
        return updated;
    }

    public async Task<bool> UpdateCustomMetadataAsync(
        LibraryItemCustomMetadataUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.LibraryItemId)
            || string.IsNullOrWhiteSpace(request.Title))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = database.OpenConnection();
        var itemKind = await GetLibraryItemKindAsync(connection, request.LibraryItemId.Trim(), cancellationToken);
        if (itemKind is null)
        {
            return false;
        }

        using var transaction = connection.BeginTransaction();
        string? posterAssetId = null;
        if (!string.IsNullOrWhiteSpace(request.PosterLocalPath))
        {
            posterAssetId = StableId.Create(
                "poster",
                "custom",
                request.LibraryItemId.Trim(),
                request.PosterLocalPath);
            using var poster = connection.CreateCommand();
            poster.Transaction = transaction;
            poster.CommandText = """
                INSERT INTO poster_assets (id, remote_path, local_path, width, height, created_at)
                VALUES ($id, $remotePath, $localPath, NULL, NULL, $createdAt)
                ON CONFLICT(id) DO UPDATE SET
                    remote_path = excluded.remote_path,
                    local_path = excluded.local_path;
                """;
            poster.Parameters.AddWithValue("$id", posterAssetId);
            poster.Parameters.AddWithValue("$remotePath", NullIfWhiteSpace(request.PosterRemotePath));
            poster.Parameters.AddWithValue("$localPath", request.PosterLocalPath.Trim());
            poster.Parameters.AddWithValue("$createdAt", now);
            await poster.ExecuteNonQueryAsync(cancellationToken);
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE library_items
            SET title = $title,
                sort_title = $sortTitle,
                release_date = $releaseDate,
                overview = $overview,
                poster_asset_id = COALESCE($posterAssetId, poster_asset_id),
                vote_average = $voteAverage,
                is_locked = $isLocked,
                is_visible = 1,
                updated_at = $updatedAt
            WHERE id = $id;
            """;
        update.Parameters.AddWithValue("$id", request.LibraryItemId.Trim());
        update.Parameters.AddWithValue("$title", request.Title.Trim());
        update.Parameters.AddWithValue("$sortTitle", request.Title.Trim().ToLowerInvariant());
        update.Parameters.AddWithValue("$releaseDate", NullIfWhiteSpace(request.ReleaseDate));
        update.Parameters.AddWithValue("$overview", NullIfWhiteSpace(request.Overview));
        update.Parameters.AddWithValue("$posterAssetId", string.IsNullOrWhiteSpace(posterAssetId) ? DBNull.Value : posterAssetId);
        update.Parameters.AddWithValue("$voteAverage", request.VoteAverage ?? (object)DBNull.Value);
        update.Parameters.AddWithValue("$isLocked", request.LockMetadata ? 1 : 0);
        update.Parameters.AddWithValue("$updatedAt", now);
        var updated = await update.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (updated)
        {
            await UpsertCustomDoubanRatingAsync(
                connection,
                transaction,
                request.LibraryItemId.Trim(),
                request.Title.Trim(),
                request.ReleaseDate,
                request.DoubanRating,
                now,
                cancellationToken);
        }
        if (updated && !string.IsNullOrWhiteSpace(request.EpisodeId))
        {
            await UpdateEpisodeSubtitleAsync(
                connection,
                transaction,
                request.LibraryItemId.Trim(),
                request.EpisodeId,
                request.EpisodeSubtitle,
                now,
                cancellationToken);
        }

        transaction.Commit();
        return updated;
    }

    public async Task<bool> SaveDoubanMetadataAsync(
        DoubanMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(metadata.LibraryItemId)
            || string.IsNullOrWhiteSpace(metadata.SubjectId)
            || string.IsNullOrWhiteSpace(metadata.Title))
        {
            return false;
        }

        using var connection = database.OpenConnection();
        if (await GetLibraryItemKindAsync(connection, metadata.LibraryItemId.Trim(), cancellationToken) is null)
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO douban_metadata (
                library_item_id,
                subject_id,
                subject_url,
                title,
                original_title,
                year,
                rating,
                rating_count,
                summary,
                genres,
                countries,
                poster_url,
                fetched_at
            )
            VALUES (
                $libraryItemId,
                $subjectId,
                $subjectUrl,
                $title,
                $originalTitle,
                $year,
                $rating,
                $ratingCount,
                $summary,
                $genres,
                $countries,
                $posterUrl,
                $fetchedAt
            )
            ON CONFLICT(library_item_id) DO UPDATE SET
                subject_id = excluded.subject_id,
                subject_url = excluded.subject_url,
                title = excluded.title,
                original_title = excluded.original_title,
                year = excluded.year,
                rating = excluded.rating,
                rating_count = excluded.rating_count,
                summary = excluded.summary,
                genres = excluded.genres,
                countries = excluded.countries,
                poster_url = excluded.poster_url,
                fetched_at = excluded.fetched_at;
            """;
        AddDoubanParameters(command, metadata);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    private static async Task UpsertCustomDoubanRatingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string libraryItemId,
        string title,
        string? releaseDate,
        double? rating,
        string now,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO douban_metadata (
                library_item_id,
                subject_id,
                subject_url,
                title,
                original_title,
                year,
                rating,
                rating_count,
                summary,
                genres,
                countries,
                poster_url,
                fetched_at
            )
            VALUES (
                $libraryItemId,
                $subjectId,
                '',
                $title,
                NULL,
                $year,
                $rating,
                NULL,
                NULL,
                NULL,
                NULL,
                NULL,
                $fetchedAt
            )
            ON CONFLICT(library_item_id) DO UPDATE SET
                title = CASE WHEN douban_metadata.subject_url = '' THEN excluded.title ELSE douban_metadata.title END,
                year = COALESCE(douban_metadata.year, excluded.year),
                rating = excluded.rating,
                fetched_at = excluded.fetched_at;
            """;
        command.Parameters.AddWithValue("$libraryItemId", libraryItemId);
        command.Parameters.AddWithValue("$subjectId", $"manual-{libraryItemId}");
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$year", NormalizeYear(releaseDate) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$rating", rating ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$fetchedAt", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateEpisodeSubtitleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string libraryItemId,
        string episodeId,
        string? subtitle,
        string now,
        CancellationToken cancellationToken)
    {
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT e.title,
                   s.season_number,
                   e.episode_number
            FROM episodes e
            JOIN seasons s ON s.id = e.season_id
            JOIN tv_shows tv ON tv.id = s.tv_show_id
            WHERE e.id = $episodeId
              AND tv.library_item_id = $libraryItemId
            LIMIT 1;
            """;
        select.Parameters.AddWithValue("$episodeId", episodeId.Trim());
        select.Parameters.AddWithValue("$libraryItemId", libraryItemId);

        string? currentTitle = null;
        int seasonNumber;
        int episodeNumber;
        using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return;
            }

            currentTitle = reader.IsDBNull(0) ? null : reader.GetString(0);
            seasonNumber = reader.GetInt32(1);
            episodeNumber = reader.GetInt32(2);
        }

        var baseTitle = StripEpisodeSubtitle(currentTitle);
        if (string.IsNullOrWhiteSpace(baseTitle))
        {
            baseTitle = seasonNumber == 0 ? $"特别篇第 {episodeNumber} 集" : $"第 {seasonNumber} 季第 {episodeNumber} 集";
        }

        var trimmedSubtitle = subtitle?.Trim();
        var nextTitle = string.IsNullOrWhiteSpace(trimmedSubtitle)
            ? baseTitle
            : $"{baseTitle}·{trimmedSubtitle}";

        using var updateEpisode = connection.CreateCommand();
        updateEpisode.Transaction = transaction;
        updateEpisode.CommandText = """
            UPDATE episodes
            SET title = $title
            WHERE id = $episodeId;

            UPDATE library_items
            SET updated_at = $updatedAt
            WHERE id = $libraryItemId;
            """;
        updateEpisode.Parameters.AddWithValue("$title", nextTitle);
        updateEpisode.Parameters.AddWithValue("$episodeId", episodeId.Trim());
        updateEpisode.Parameters.AddWithValue("$updatedAt", now);
        updateEpisode.Parameters.AddWithValue("$libraryItemId", libraryItemId);
        await updateEpisode.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? StripEpisodeSubtitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var separatorIndex = title.IndexOf('·', StringComparison.Ordinal);
        return separatorIndex < 0 ? title.Trim() : title[..separatorIndex].Trim();
    }

    public async Task<bool> SetLibraryItemLockedAsync(
        LibraryItemLockUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.LibraryItemId))
        {
            return false;
        }

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE library_items
            SET is_locked = $isLocked,
                updated_at = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", request.LibraryItemId.Trim());
        command.Parameters.AddWithValue("$isLocked", request.IsLocked ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> UpdatePlaybackProgressAsync(
        PlaybackProgressUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.VideoFileId))
        {
            return false;
        }

        var userId = NormalizeUserId(request.UserId);
        var position = Math.Max(0, request.PositionSeconds);
        var duration = Math.Max(0, request.DurationSeconds);
        var isWatched = duration > 0 && position / duration >= 0.95;
        var now = DateTimeOffset.UtcNow.ToString("O");

        using var connection = database.OpenConnection();
        if (!await VideoFileExistsAsync(connection, request.VideoFileId, cancellationToken))
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO playback_progress (id, user_id, video_file_id, position_seconds, duration_seconds, is_watched, updated_at)
            VALUES ($id, $userId, $videoFileId, $positionSeconds, $durationSeconds, $isWatched, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                position_seconds = CASE
                    WHEN excluded.position_seconds <= 5 AND playback_progress.position_seconds > 5 THEN playback_progress.position_seconds
                    ELSE excluded.position_seconds
                END,
                duration_seconds = MAX(playback_progress.duration_seconds, excluded.duration_seconds),
                is_watched = CASE
                    WHEN excluded.position_seconds <= 5 AND playback_progress.position_seconds > 5 THEN playback_progress.is_watched
                    ELSE excluded.is_watched
                END,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", ProgressId(userId, request.VideoFileId));
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$videoFileId", request.VideoFileId);
        command.Parameters.AddWithValue("$positionSeconds", position);
        command.Parameters.AddWithValue("$durationSeconds", duration);
        command.Parameters.AddWithValue("$isWatched", isWatched ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    public async Task<PlaybackProgressState?> GetPlaybackProgressAsync(
        string videoFileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoFileId))
        {
            return null;
        }

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT vf.duration_seconds,
                   COALESCE(pp.position_seconds, 0),
                   COALESCE(pp.duration_seconds, 0),
                   COALESCE(pp.is_watched, 0)
            FROM video_files vf
            LEFT JOIN playback_progress pp
                ON pp.video_file_id = vf.id
               AND pp.user_id = $userId
            WHERE vf.id = $videoFileId
              AND vf.missing_at IS NULL
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$videoFileId", videoFileId.Trim());
        command.Parameters.AddWithValue("$userId", DefaultUserId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var storedDuration = reader.IsDBNull(0) ? 0 : reader.GetDouble(0);
        var progressDuration = reader.IsDBNull(2) ? 0 : reader.GetDouble(2);
        return new PlaybackProgressState(
            Math.Max(reader.IsDBNull(1) ? 0 : reader.GetDouble(1), 0),
            Math.Max(storedDuration, progressDuration),
            !reader.IsDBNull(3) && reader.GetInt64(3) == 1);
    }

    public async Task<bool> SetWatchedAsync(
        WatchedStatusUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.VideoFileId))
        {
            return false;
        }

        var userId = NormalizeUserId(request.UserId);
        var now = DateTimeOffset.UtcNow.ToString("O");

        using var connection = database.OpenConnection();
        if (!await VideoFileExistsAsync(connection, request.VideoFileId, cancellationToken))
        {
            return false;
        }

        var existingDuration = await GetVideoDurationAsync(connection, request.VideoFileId, cancellationToken);
        var duration = Math.Max(0, request.DurationSeconds ?? existingDuration);
        var position = request.IsWatched && duration > 0 ? duration : 0;

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO playback_progress (id, user_id, video_file_id, position_seconds, duration_seconds, is_watched, updated_at)
            VALUES ($id, $userId, $videoFileId, $positionSeconds, $durationSeconds, $isWatched, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                position_seconds = excluded.position_seconds,
                duration_seconds = excluded.duration_seconds,
                is_watched = excluded.is_watched,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", ProgressId(userId, request.VideoFileId));
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$videoFileId", request.VideoFileId);
        command.Parameters.AddWithValue("$positionSeconds", position);
        command.Parameters.AddWithValue("$durationSeconds", duration);
        command.Parameters.AddWithValue("$isWatched", request.IsWatched ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetLibraryItemWatchedAsync(
        LibraryItemWatchedStatusUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.LibraryItemId))
        {
            return false;
        }

        var userId = NormalizeUserId(request.UserId);
        var now = DateTimeOffset.UtcNow.ToString("O");

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO playback_progress (id, user_id, video_file_id, position_seconds, duration_seconds, is_watched, updated_at)
            SELECT $userId || ':' || vf.id,
                   $userId,
                   vf.id,
                   CASE
                       WHEN $isWatched = 1 THEN
                           CASE
                               WHEN COALESCE(NULLIF(vf.duration_seconds, 0), NULLIF(pp.duration_seconds, 0), 0) > 0
                                   THEN COALESCE(NULLIF(vf.duration_seconds, 0), NULLIF(pp.duration_seconds, 0), 0)
                               ELSE 100
                           END
                       ELSE 0
                   END AS position_seconds,
                   CASE
                       WHEN COALESCE(NULLIF(vf.duration_seconds, 0), NULLIF(pp.duration_seconds, 0), 0) > 0
                           THEN COALESCE(NULLIF(vf.duration_seconds, 0), NULLIF(pp.duration_seconds, 0), 0)
                       WHEN $isWatched = 1 THEN 100
                       ELSE 0
                   END AS duration_seconds,
                   $isWatched,
                   $updatedAt
            FROM video_files vf
            LEFT JOIN playback_progress pp ON pp.video_file_id = vf.id AND pp.user_id = $userId
            WHERE vf.library_item_id = $libraryItemId
              AND vf.missing_at IS NULL
            ON CONFLICT(id) DO UPDATE SET
                position_seconds = excluded.position_seconds,
                duration_seconds = excluded.duration_seconds,
                is_watched = excluded.is_watched,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$libraryItemId", request.LibraryItemId.Trim());
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$isWatched", request.IsWatched ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", now);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static async Task<LibraryItemSummary?> GetItemSummaryAsync(
        SqliteConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT li.id,
                   li.item_kind,
                   li.title,
                   li.release_date,
                   li.overview,
                   li.poster_asset_id,
                   li.vote_average,
                   dm.rating AS douban_rating,
                   li.is_locked,
                   COALESCE(MIN(COALESCE(pp.is_watched, 0)), 0) AS is_watched,
                   COUNT(vf.id) AS video_count,
                   CASE
                       WHEN li.item_kind = 'movie' THEN COALESCE(SUM(
                           CASE
                               WHEN COALESCE(NULLIF(vf.duration_seconds, 0), pp.duration_seconds, 0) <= 0 THEN 0
                               WHEN COALESCE(pp.is_watched, 0) = 1 THEN COALESCE(NULLIF(vf.duration_seconds, 0), pp.duration_seconds, 0)
                               ELSE MIN(COALESCE(pp.position_seconds, 0), COALESCE(NULLIF(vf.duration_seconds, 0), pp.duration_seconds, 0))
                           END), 0)
                       ELSE COALESCE(MAX(pp.position_seconds), 0)
                   END AS max_progress,
                   CASE
                       WHEN li.item_kind = 'movie' THEN COALESCE(SUM(COALESCE(NULLIF(vf.duration_seconds, 0), pp.duration_seconds, 0)), 0)
                       ELSE COALESCE(MAX(CASE WHEN vf.duration_seconds > 0 THEN vf.duration_seconds ELSE pp.duration_seconds END), 0)
                   END AS max_duration,
                   li.updated_at
            FROM library_items li
            JOIN video_files vf ON vf.library_item_id = li.id AND vf.missing_at IS NULL
            JOIN media_sources ms ON ms.id = vf.source_id AND ms.is_enabled = 1 AND ms.removed_at IS NULL
            LEFT JOIN douban_metadata dm ON dm.library_item_id = li.id
            LEFT JOIN playback_progress pp ON pp.video_file_id = vf.id AND pp.user_id = $userId
            WHERE li.id = $id
            GROUP BY li.id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$userId", DefaultUserId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadItemSummary(reader)
            : null;
    }

    private static async Task<IReadOnlyList<VideoFileSummary>> GetVideoFilesAsync(
        SqliteConnection connection,
        string libraryItemId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT vf.id,
                   vf.source_id,
                   ms.name,
                   vf.relative_path,
                   vf.file_name,
                   vf.media_kind,
                   vf.file_size_bytes,
                   vf.duration_seconds,
                   COALESCE(pp.position_seconds, 0) AS position_seconds,
                   COALESCE(pp.is_watched, 0) AS is_watched,
                   vf.episode_id,
                   s.season_number,
                   e.episode_number,
                   e.title,
                   vf.container,
                   vf.video_codec,
                   vf.audio_codec,
                   vf.subtitle_summary,
                   vf.probe_json
            FROM video_files vf
            JOIN media_sources ms ON ms.id = vf.source_id
            LEFT JOIN playback_progress pp ON pp.video_file_id = vf.id AND pp.user_id = $userId
            LEFT JOIN episodes e ON e.id = vf.episode_id
            LEFT JOIN seasons s ON s.id = e.season_id
            WHERE vf.library_item_id = $libraryItemId
              AND vf.missing_at IS NULL
              AND ms.is_enabled = 1
              AND ms.removed_at IS NULL
            ORDER BY CASE
                         WHEN s.season_number IS NULL THEN 9999
                         WHEN s.season_number = 0 THEN 9998
                         ELSE s.season_number
                     END ASC,
                     COALESCE(e.episode_number, 9999) ASC,
                     vf.relative_path COLLATE NOCASE ASC;
            """;
        command.Parameters.AddWithValue("$libraryItemId", libraryItemId);
        command.Parameters.AddWithValue("$userId", DefaultUserId);

        List<VideoFileSummary> files = [];
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            files.Add(new VideoFileSummary(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.GetDouble(7),
                reader.GetDouble(8),
                reader.GetInt64(9) == 1,
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetInt32(11),
                reader.IsDBNull(12) ? null : reader.GetInt32(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                ParseStreams(reader.IsDBNull(18) ? null : reader.GetString(18), "audio"),
                ParseStreams(reader.IsDBNull(18) ? null : reader.GetString(18), "subtitle")));
        }

        return files;
    }

    private static async Task<IReadOnlyList<SeasonDetail>> GetSeasonsAsync(
        SqliteConnection connection,
        string libraryItemId,
        IReadOnlyList<VideoFileSummary> files,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT seasons.id,
                   seasons.season_number,
                   seasons.title,
                   seasons.poster_asset_id,
                   episodes.id,
                   episodes.episode_number,
                   episodes.title,
                   episodes.overview,
                   episodes.still_asset_id,
                   episodes.air_date
            FROM tv_shows
            JOIN seasons ON seasons.tv_show_id = tv_shows.id
            LEFT JOIN episodes ON episodes.season_id = seasons.id
            WHERE tv_shows.library_item_id = $libraryItemId
            ORDER BY CASE WHEN seasons.season_number = 0 THEN 9998 ELSE seasons.season_number END ASC,
                     episodes.episode_number ASC;
            """;
        command.Parameters.AddWithValue("$libraryItemId", libraryItemId);

        var filesByEpisodeId = files
            .Where(static file => !string.IsNullOrWhiteSpace(file.EpisodeId))
            .GroupBy(static file => file.EpisodeId!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderByDescending(static file => file.PositionSeconds > 0 && !file.IsWatched)
                    .ThenBy(static file => file.IsWatched)
                    .ThenByDescending(static file => file.DurationSeconds)
                    .ThenBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.Ordinal);
        var seasons = new Dictionary<string, MutableSeason>(StringComparer.Ordinal);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var seasonId = reader.GetString(0);
            if (!seasons.TryGetValue(seasonId, out var season))
            {
                season = new MutableSeason(
                    seasonId,
                    reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3));
                seasons[seasonId] = season;
            }

            if (reader.IsDBNull(4))
            {
                continue;
            }

            var episodeId = reader.GetString(4);
            filesByEpisodeId.TryGetValue(episodeId, out var episodeFiles);
            episodeFiles ??= [];
            season.Episodes.Add(new EpisodeDetail(
                episodeId,
                seasonId,
                season.SeasonNumber,
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                episodeFiles.FirstOrDefault(),
                episodeFiles));
        }

        return seasons.Values
            .OrderBy(static season => SeasonSortNumber(season.SeasonNumber))
            .Select(static season => new SeasonDetail(
                season.Id,
                season.SeasonNumber,
                season.Title,
                season.PosterAssetId,
                season.Episodes.OrderBy(static episode => episode.EpisodeNumber).ToArray()))
            .ToArray();
    }

    private static int SeasonSortNumber(int seasonNumber)
    {
        return seasonNumber == 0 ? int.MaxValue : seasonNumber;
    }

    private static async Task<int?> GetTmdbIdAsync(
        SqliteConnection connection,
        string libraryItemId,
        string itemKind,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = string.Equals(itemKind, "tv", StringComparison.OrdinalIgnoreCase)
            ? """
                SELECT tmdb_id
                FROM tv_shows
                WHERE library_item_id = $libraryItemId
                LIMIT 1;
                """
            : """
                SELECT tmdb_id
                FROM movies
                WHERE library_item_id = $libraryItemId
                LIMIT 1;
                """;
        command.Parameters.AddWithValue("$libraryItemId", libraryItemId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    private static async Task<DoubanMetadata?> GetDoubanMetadataAsync(
        SqliteConnection connection,
        string libraryItemId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT library_item_id,
                   subject_id,
                   subject_url,
                   title,
                   original_title,
                   year,
                   rating,
                   rating_count,
                   summary,
                   genres,
                   countries,
                   poster_url,
                   fetched_at
            FROM douban_metadata
            WHERE library_item_id = $libraryItemId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$libraryItemId", libraryItemId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DoubanMetadata(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetDouble(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            DateTimeOffset.Parse(reader.GetString(12)));
    }

    private static async Task<string?> GetLibraryItemKindAsync(
        SqliteConnection connection,
        string libraryItemId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_kind
            FROM library_items
            WHERE id = $libraryItemId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$libraryItemId", libraryItemId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task UpdateTmdbIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string libraryItemId,
        string mediaType,
        int tmdbId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var isTv = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase);
        command.CommandText = isTv
            ? """
                INSERT INTO tv_shows (id, library_item_id, tmdb_id, original_name, first_air_date)
                VALUES ($id, $libraryItemId, $tmdbId, NULL, NULL)
                ON CONFLICT(id) DO UPDATE SET
                    library_item_id = excluded.library_item_id,
                    tmdb_id = excluded.tmdb_id;
                """
            : """
                INSERT INTO movies (id, library_item_id, tmdb_id, original_title, runtime_seconds)
                VALUES ($id, $libraryItemId, $tmdbId, NULL, NULL)
                ON CONFLICT(id) DO UPDATE SET
                    library_item_id = excluded.library_item_id,
                    tmdb_id = excluded.tmdb_id;
                """;
        command.Parameters.AddWithValue("$id", StableId.Create(isTv ? "show" : "movie", libraryItemId));
        command.Parameters.AddWithValue("$libraryItemId", libraryItemId);
        command.Parameters.AddWithValue("$tmdbId", tmdbId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (isTv)
        {
            await TvLibraryItemMerger.MergeDuplicatesByTmdbIdAsync(
                connection,
                transaction,
                libraryItemId,
                tmdbId,
                DateTimeOffset.UtcNow.ToString("O"),
                cancellationToken);
        }
        else
        {
            await MergeDuplicateMovieItemsByTmdbIdAsync(
                connection,
                transaction,
                libraryItemId,
                tmdbId,
                cancellationToken);
        }
    }

    private static async Task<bool> MergeDuplicateMovieItemsByTmdbIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string canonicalLibraryItemId,
        int tmdbId,
        CancellationToken cancellationToken)
    {
        if (tmdbId <= 0 || string.IsNullOrWhiteSpace(canonicalLibraryItemId))
        {
            return false;
        }

        await ExecuteMergeCommandAsync(connection, transaction, """
            CREATE TEMP TABLE IF NOT EXISTS merge_duplicate_movie_item_ids (
                id TEXT PRIMARY KEY
            );
            """, cancellationToken);
        await ExecuteMergeCommandAsync(connection, transaction, "DELETE FROM merge_duplicate_movie_item_ids;", cancellationToken);
        await ExecuteMergeCommandAsync(connection, transaction, """
            INSERT OR IGNORE INTO merge_duplicate_movie_item_ids (id)
            SELECT DISTINCT m.library_item_id
            FROM movies m
            JOIN library_items li ON li.id = m.library_item_id
            WHERE m.tmdb_id = $tmdbId
              AND m.library_item_id <> $canonicalLibraryItemId
              AND li.item_kind = 'movie'
              AND EXISTS (
                  SELECT 1
                  FROM video_files vf
                  WHERE vf.library_item_id = m.library_item_id
                    AND vf.media_kind = 'movie'
                    AND vf.missing_at IS NULL
              );
            """,
            cancellationToken,
            ("$tmdbId", tmdbId),
            ("$canonicalLibraryItemId", canonicalLibraryItemId));

        var duplicateCount = await ExecuteMergeScalarAsync(connection, transaction, """
            SELECT COUNT(*)
            FROM merge_duplicate_movie_item_ids;
            """, cancellationToken);
        if (duplicateCount <= 0)
        {
            return false;
        }

        await ExecuteMergeCommandAsync(connection, transaction, """
            UPDATE video_files
            SET library_item_id = $canonicalLibraryItemId,
                episode_id = NULL,
                media_kind = 'movie',
                updated_at = $updatedAt
            WHERE library_item_id IN (SELECT id FROM merge_duplicate_movie_item_ids)
              AND media_kind = 'movie';
            """,
            cancellationToken,
            ("$canonicalLibraryItemId", canonicalLibraryItemId),
            ("$updatedAt", DateTimeOffset.UtcNow.ToString("O")));
        await ExecuteMergeCommandAsync(connection, transaction, """
            DELETE FROM movies
            WHERE library_item_id IN (SELECT id FROM merge_duplicate_movie_item_ids);
            """, cancellationToken);
        await ExecuteMergeCommandAsync(connection, transaction, """
            DELETE FROM library_items
            WHERE id IN (SELECT id FROM merge_duplicate_movie_item_ids)
              AND NOT EXISTS (
                  SELECT 1
                  FROM video_files vf
                  WHERE vf.library_item_id = library_items.id
                    AND vf.missing_at IS NULL
              );
            """, cancellationToken);
        return true;
    }

    private static async Task ExecuteMergeCommandAsync(
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

    private static async Task<int> ExecuteMergeScalarAsync(
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

    private static LibraryItemSummary ReadItemSummary(SqliteDataReader reader)
    {
        return new LibraryItemSummary(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetDouble(6),
            reader.IsDBNull(7) ? null : reader.GetDouble(7),
            reader.GetInt64(8) == 1,
            reader.GetInt64(9) == 1,
            reader.GetInt32(10),
            reader.GetDouble(11),
            reader.GetDouble(12),
            DateTimeOffset.Parse(reader.GetString(13)));
    }

    private static void AddDoubanParameters(SqliteCommand command, DoubanMetadata metadata)
    {
        command.Parameters.AddWithValue("$libraryItemId", metadata.LibraryItemId.Trim());
        command.Parameters.AddWithValue("$subjectId", metadata.SubjectId.Trim());
        command.Parameters.AddWithValue("$subjectUrl", metadata.SubjectUrl.Trim());
        command.Parameters.AddWithValue("$title", metadata.Title.Trim());
        command.Parameters.AddWithValue("$originalTitle", NullIfWhiteSpace(metadata.OriginalTitle));
        command.Parameters.AddWithValue("$year", NullIfWhiteSpace(metadata.Year));
        command.Parameters.AddWithValue("$rating", metadata.Rating ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$ratingCount", metadata.RatingCount ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$summary", NullIfWhiteSpace(metadata.Summary));
        command.Parameters.AddWithValue("$genres", NullIfWhiteSpace(metadata.Genres));
        command.Parameters.AddWithValue("$countries", NullIfWhiteSpace(metadata.Countries));
        command.Parameters.AddWithValue("$posterUrl", NullIfWhiteSpace(metadata.PosterUrl));
        command.Parameters.AddWithValue("$fetchedAt", metadata.FetchedAt.ToString("O"));
    }

    private static string? NormalizeYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length >= 4 && trimmed[..4].All(char.IsDigit) ? trimmed[..4] : null;
    }

    private static async Task<bool> VideoFileExistsAsync(
        SqliteConnection connection,
        string videoFileId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM video_files WHERE id = $id AND missing_at IS NULL LIMIT 1;";
        command.Parameters.AddWithValue("$id", videoFileId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<double> GetVideoDurationAsync(
        SqliteConnection connection,
        string videoFileId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(NULLIF(vf.duration_seconds, 0), pp.duration_seconds, 0)
            FROM video_files vf
            LEFT JOIN playback_progress pp ON pp.video_file_id = vf.id AND pp.user_id = $userId
            WHERE vf.id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", videoFileId);
        command.Parameters.AddWithValue("$userId", DefaultUserId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToDouble(value);
    }

    private static string NormalizeUserId(string? userId)
    {
        return string.IsNullOrWhiteSpace(userId) ? DefaultUserId : userId.Trim();
    }

    private static string ProgressId(string userId, string videoFileId)
    {
        return $"{userId}:{videoFileId}";
    }

    private static object NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    }

    private static IReadOnlyList<VideoFileStreamSummary> ParseStreams(string? probeJson, string kind)
    {
        if (string.IsNullOrWhiteSpace(probeJson))
        {
            return [];
        }

        try
        {
            var probe = JsonSerializer.Deserialize<ProbeJsonSnapshot>(
                probeJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return probe?.Streams?
                .Where(stream => string.Equals(stream.CodecType, kind, StringComparison.OrdinalIgnoreCase))
                .Select(static stream => new VideoFileStreamSummary(
                    stream.Index,
                    stream.CodecType ?? string.Empty,
                    stream.CodecName,
                    ReadTag(stream.Tags, "language"),
                    ReadTag(stream.Tags, "title"),
                    stream.Channels,
                    stream.ChannelLayout,
                    ReadDisposition(stream.Disposition, "default"),
                    ReadDisposition(stream.Disposition, "forced")))
                .ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ReadTag(IReadOnlyDictionary<string, string>? tags, string key)
    {
        if (tags is null)
        {
            return null;
        }

        return tags.TryGetValue(key, out var value)
               || tags.TryGetValue(key.ToUpperInvariant(), out value)
            ? value
            : null;
    }

    private static bool ReadDisposition(IReadOnlyDictionary<string, int>? disposition, string key)
    {
        return disposition is not null
               && disposition.TryGetValue(key, out var value)
               && value == 1;
    }

    private static bool IsPathInsideRoot(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);

        return normalizedCandidate.Equals(normalizedRoot, comparison)
               || normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    private sealed class MutableSeason
    {
        public MutableSeason(string id, int seasonNumber, string? title, string? posterAssetId)
        {
            Id = id;
            SeasonNumber = seasonNumber;
            Title = title;
            PosterAssetId = posterAssetId;
        }

        public string Id { get; }

        public int SeasonNumber { get; }

        public string? Title { get; }

        public string? PosterAssetId { get; }

        public List<EpisodeDetail> Episodes { get; } = [];
    }

    private sealed record ProbeJsonSnapshot(
        [property: JsonPropertyName("streams")] IReadOnlyList<ProbeJsonStream>? Streams);

    private sealed record ProbeJsonStream(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("codec_type")] string? CodecType,
        [property: JsonPropertyName("codec_name")] string? CodecName,
        [property: JsonPropertyName("channels")] int? Channels,
        [property: JsonPropertyName("channel_layout")] string? ChannelLayout,
        [property: JsonPropertyName("tags")] IReadOnlyDictionary<string, string>? Tags,
        [property: JsonPropertyName("disposition")] IReadOnlyDictionary<string, int>? Disposition);
}
