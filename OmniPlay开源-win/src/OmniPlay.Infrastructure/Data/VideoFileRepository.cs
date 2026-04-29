using Dapper;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Core.Models.Library;
using OmniPlay.Core.Models.Playback;
using OmniPlay.Infrastructure.Library;

namespace OmniPlay.Infrastructure.Data;

public sealed class VideoFileRepository : IVideoFileRepository
{
    private readonly SqliteDatabase database;
    private readonly IStoragePaths storagePaths;

    public VideoFileRepository(SqliteDatabase database, IStoragePaths storagePaths)
    {
        this.database = database;
        this.storagePaths = storagePaths;
    }

    public async Task<IReadOnlyList<LibraryVideoItem>> GetByMovieAsync(long movieId, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        var rows = await connection.QueryAsync<VideoFileRow>(
            new CommandDefinition(
                """
                SELECT videoFile.id,
                       videoFile.fileName,
                       videoFile.relativePath,
                       mediaSource.protocolType AS SourceProtocolType,
                       mediaSource.baseUrl AS SourceBasePath,
                       mediaSource.authConfig AS SourceAuthConfig,
                       movie.posterPath AS FallbackImagePath,
                       videoFile.playProgress,
                       videoFile.duration,
                       videoFile.customSeasonNumber,
                       videoFile.customEpisodeNumber,
                       videoFile.customEpisodeYear,
                       videoFile.customEpisodeSubtitle,
                       videoFile.customEpisodeThumbnailPath,
                       ROW_NUMBER() OVER (ORDER BY videoFile.fileName COLLATE NOCASE ASC) - 1 AS SortIndex
                FROM videoFile
                JOIN mediaSource ON mediaSource.id = videoFile.sourceId
                                AND mediaSource.isEnabled = 1
                                AND mediaSource.removedAt IS NULL
                LEFT JOIN movie ON movie.id = videoFile.movieId
                WHERE videoFile.movieId = @MovieId
                ORDER BY videoFile.fileName COLLATE NOCASE ASC
                """,
                new { MovieId = movieId },
                cancellationToken: cancellationToken));

        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<LibraryVideoItem>> GetByTvShowAsync(long tvShowId, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        var rows = await connection.QueryAsync<VideoFileRow>(
            new CommandDefinition(
                """
                SELECT videoFile.id,
                       videoFile.fileName,
                       videoFile.relativePath,
                       mediaSource.protocolType AS SourceProtocolType,
                       mediaSource.baseUrl AS SourceBasePath,
                       mediaSource.authConfig AS SourceAuthConfig,
                       tvShow.posterPath AS FallbackImagePath,
                       videoFile.playProgress,
                       videoFile.duration,
                       videoFile.customSeasonNumber,
                       videoFile.customEpisodeNumber,
                       videoFile.customEpisodeYear,
                       videoFile.customEpisodeSubtitle,
                       videoFile.customEpisodeThumbnailPath,
                       ROW_NUMBER() OVER (ORDER BY videoFile.fileName COLLATE NOCASE ASC) - 1 AS SortIndex
                FROM videoFile
                JOIN mediaSource ON mediaSource.id = videoFile.sourceId
                                AND mediaSource.isEnabled = 1
                                AND mediaSource.removedAt IS NULL
                LEFT JOIN tvShow ON tvShow.id = videoFile.episodeId
                WHERE videoFile.episodeId = @TvShowId
                ORDER BY videoFile.fileName COLLATE NOCASE ASC
                """,
                new { TvShowId = tvShowId },
                cancellationToken: cancellationToken));

        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<LibraryPosterItem>> GetContinueWatchingAsync(CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        var rows = await connection.QueryAsync<ContinueWatchingRow>(
            new CommandDefinition(
                """
                SELECT movie.id AS MovieId,
                       NULL AS TvShowId,
                       movie.title AS Title,
                       movie.releaseDate AS Subtitle,
                       movie.posterPath AS PosterPath,
                       movie.voteAverage AS VoteAverage,
                       '电影' AS MediaKind,
                       MAX(
                           CASE
                               WHEN videoFile.duration > 0 THEN MIN(videoFile.playProgress / videoFile.duration, 1.0)
                               ELSE 0
                           END
                       ) AS ContinueWatchingProgress
                FROM movie
                JOIN videoFile ON videoFile.movieId = movie.id AND videoFile.mediaType = 'movie'
                JOIN mediaSource ON mediaSource.id = videoFile.sourceId
                                AND mediaSource.isEnabled = 1
                                AND mediaSource.removedAt IS NULL
                WHERE videoFile.playProgress > @MinimumProgressSeconds
                  AND (videoFile.duration <= 0 OR (videoFile.playProgress / videoFile.duration) < @CompletionRatio)
                GROUP BY movie.id, movie.title, movie.releaseDate, movie.posterPath, movie.voteAverage

                UNION ALL

                SELECT NULL AS MovieId,
                       tvShow.id AS TvShowId,
                       tvShow.title AS Title,
                       tvShow.firstAirDate AS Subtitle,
                       tvShow.posterPath AS PosterPath,
                       tvShow.voteAverage AS VoteAverage,
                       '剧集' AS MediaKind,
                       MAX(
                           CASE
                               WHEN videoFile.duration > 0 THEN MIN(videoFile.playProgress / videoFile.duration, 1.0)
                               ELSE 0
                           END
                       ) AS ContinueWatchingProgress
                FROM tvShow
                JOIN videoFile ON videoFile.episodeId = tvShow.id AND videoFile.mediaType = 'tv'
                JOIN mediaSource ON mediaSource.id = videoFile.sourceId
                                AND mediaSource.isEnabled = 1
                                AND mediaSource.removedAt IS NULL
                WHERE videoFile.playProgress > @MinimumProgressSeconds
                  AND (videoFile.duration <= 0 OR (videoFile.playProgress / videoFile.duration) < @CompletionRatio)
                GROUP BY tvShow.id, tvShow.title, tvShow.firstAirDate, tvShow.posterPath, tvShow.voteAverage
                """,
                new
                {
                    CompletionRatio = PlaybackProgressRules.CompletionRatio,
                    MinimumProgressSeconds = PlaybackProgressRules.MinimumProgressSeconds
                },
                cancellationToken: cancellationToken));

        return rows
            .Select(row => new LibraryPosterItem
            {
                Id = row.MovieId.HasValue ? $"movie-{row.MovieId}" : $"tv-{row.TvShowId}",
                Title = row.Title,
                Subtitle = FormatYear(row.Subtitle),
                PosterPath = row.PosterPath,
                VoteAverage = row.VoteAverage,
                MediaKind = row.MediaKind,
                ContinueWatchingProgress = Math.Clamp(row.ContinueWatchingProgress, 0, 1),
                IsContinuing = true,
                WatchState = PlaybackWatchState.InProgress,
                ContinueWatchingLabel = $"\u672A\u770B\u5B8C {Math.Clamp(row.ContinueWatchingProgress * 100, 0, 100):F0}%",
                MovieId = row.MovieId,
                TvShowId = row.TvShowId
            })
            .OrderByDescending(static item => item.ContinueWatchingProgress)
            .ThenBy(static item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyDictionary<string, PlaybackWatchState>> GetLibraryPlaybackStatesAsync(CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        var rows = await connection.QueryAsync<LibraryPlaybackStateRow>(
            new CommandDefinition(
                """
                SELECT 'movie-' || movie.id AS LibraryId,
                       'movie' AS MediaKind,
                       COUNT(videoFile.id) AS FileCount,
                       SUM(CASE WHEN videoFile.playProgress > @MinimumProgressSeconds THEN 1 ELSE 0 END) AS ProgressCount,
                       SUM(CASE
                               WHEN videoFile.duration > 0
                                AND (videoFile.playProgress / videoFile.duration) >= @CompletionRatio THEN 1
                               ELSE 0
                           END) AS WatchedCount
                FROM movie
                LEFT JOIN videoFile ON videoFile.movieId = movie.id
                                   AND videoFile.mediaType = 'movie'
                                   AND EXISTS (
                                       SELECT 1
                                       FROM mediaSource
                                       WHERE mediaSource.id = videoFile.sourceId
                                         AND mediaSource.isEnabled = 1
                                         AND mediaSource.removedAt IS NULL
                                   )
                GROUP BY movie.id

                UNION ALL

                SELECT 'tv-' || tvShow.id AS LibraryId,
                       'tv' AS MediaKind,
                       COUNT(videoFile.id) AS FileCount,
                       SUM(CASE WHEN videoFile.playProgress > @MinimumProgressSeconds THEN 1 ELSE 0 END) AS ProgressCount,
                       SUM(CASE
                               WHEN videoFile.duration > 0
                                AND (videoFile.playProgress / videoFile.duration) >= @CompletionRatio THEN 1
                               ELSE 0
                           END) AS WatchedCount
                FROM tvShow
                LEFT JOIN videoFile ON videoFile.episodeId = tvShow.id
                                   AND videoFile.mediaType = 'tv'
                                   AND EXISTS (
                                       SELECT 1
                                       FROM mediaSource
                                       WHERE mediaSource.id = videoFile.sourceId
                                         AND mediaSource.isEnabled = 1
                                         AND mediaSource.removedAt IS NULL
                                   )
                GROUP BY tvShow.id
                """,
                new
                {
                    CompletionRatio = PlaybackProgressRules.CompletionRatio,
                    MinimumProgressSeconds = PlaybackProgressRules.MinimumProgressSeconds
                },
                cancellationToken: cancellationToken));

        return rows.ToDictionary(
            static row => row.LibraryId,
            static row => ResolveLibraryWatchState(row),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task UpdatePlayProgressAsync(string videoFileId, double playProgress, CancellationToken cancellationToken = default)
    {
        await UpdatePlaybackStateAsync(videoFileId, playProgress, durationSeconds: null, cancellationToken);
    }

    public async Task UpdatePlaybackStateAsync(
        string videoFileId,
        double playProgress,
        double? durationSeconds,
        CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE videoFile
                SET playProgress = @PlayProgress,
                    duration = CASE
                        WHEN @DurationSeconds IS NOT NULL AND @DurationSeconds > 0 THEN @DurationSeconds
                        ELSE duration
                    END
                WHERE id = @Id
                """,
                new
                {
                    Id = videoFileId,
                    PlayProgress = Math.Max(playProgress, 0),
                    DurationSeconds = durationSeconds is > 0 ? durationSeconds : null
                },
                cancellationToken: cancellationToken));
    }

    public async Task UpdateEpisodeMetadataAsync(
        string videoFileId,
        LibraryEpisodeEditRequest request,
        CancellationToken cancellationToken = default)
    {
        static string? NormalizeText(string? value)
        {
            var trimmed = value?.Trim();
            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }

        using var connection = database.OpenConnection();
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE videoFile
                SET customSeasonNumber = @CustomSeasonNumber,
                    customEpisodeNumber = @CustomEpisodeNumber,
                    customEpisodeYear = @CustomEpisodeYear,
                    customEpisodeSubtitle = @CustomEpisodeSubtitle,
                    customEpisodeThumbnailPath = @CustomEpisodeThumbnailPath
                WHERE id = @Id
                """,
                new
                {
                    Id = videoFileId,
                    CustomSeasonNumber = request.SeasonNumber,
                    CustomEpisodeNumber = request.EpisodeNumber,
                    CustomEpisodeYear = NormalizeText(request.Year),
                    CustomEpisodeSubtitle = NormalizeText(request.Subtitle),
                    CustomEpisodeThumbnailPath = NormalizeText(request.ThumbnailPath)
                },
                cancellationToken: cancellationToken));
    }

    private LibraryVideoItem Map(VideoFileRow row)
    {
        var episodeInfo = MediaNameParser.ParseEpisodeInfo(row.FileName, row.SortIndex);
        var seasonNumber = row.CustomSeasonNumber ?? episodeInfo.Season;
        var episodeNumber = row.CustomEpisodeNumber ?? episodeInfo.Episode;
        var thumbnailPath = Path.Combine(storagePaths.ThumbnailsDirectory, $"{row.Id}.jpg");
        var resolvedThumbnailPath = File.Exists(thumbnailPath) ? thumbnailPath : null;
        var customThumbnailPath = ResolveCustomThumbnailPath(row.CustomEpisodeThumbnailPath);
        var authConfig = MediaSourceAuthConfigProtector.UnprotectFromStorage(row.SourceAuthConfig);

        return new LibraryVideoItem
        {
            Id = row.Id,
            FileName = row.FileName,
            RelativePath = row.RelativePath,
            AbsolutePath = MediaSourcePathResolver.ResolvePlaybackPath(
                row.SourceProtocolType,
                row.SourceBasePath,
                row.RelativePath),
            PlaybackPath = MediaSourcePathResolver.ResolveAuthenticatedPlaybackPath(
                row.SourceProtocolType,
                row.SourceBasePath,
                row.RelativePath,
                authConfig),
            ThumbnailPath = resolvedThumbnailPath,
            CustomThumbnailPath = customThumbnailPath,
            FallbackImagePath = row.FallbackImagePath,
            PlayProgress = row.PlayProgress,
            Duration = row.Duration,
            SeasonNumber = seasonNumber,
            EpisodeNumber = episodeNumber,
            IsTvEpisode = episodeInfo.IsTvShow,
            CustomEpisodeSubtitle = string.IsNullOrWhiteSpace(row.CustomEpisodeSubtitle)
                ? null
                : row.CustomEpisodeSubtitle.Trim(),
            EpisodeSubtitle = string.IsNullOrWhiteSpace(row.CustomEpisodeSubtitle)
                ? episodeInfo.Subtitle
                : row.CustomEpisodeSubtitle.Trim(),
            EpisodeYear = string.IsNullOrWhiteSpace(row.CustomEpisodeYear)
                ? episodeInfo.Year
                : row.CustomEpisodeYear.Trim(),
            EpisodeLabel = episodeInfo.IsTvShow
                ? $"S{seasonNumber:00}E{episodeNumber:00}"
                : string.Empty
        };
    }

    private static string? ResolveCustomThumbnailPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim();
        return Path.IsPathRooted(trimmed) && File.Exists(trimmed)
            ? trimmed
            : null;
    }

    private sealed class VideoFileRow
    {
        public string Id { get; init; } = string.Empty;

        public string FileName { get; init; } = string.Empty;

        public string RelativePath { get; init; } = string.Empty;

        public string SourceProtocolType { get; init; } = string.Empty;

        public string SourceBasePath { get; init; } = string.Empty;

        public string? SourceAuthConfig { get; init; }

        public string? FallbackImagePath { get; init; }

        public double PlayProgress { get; init; }

        public double Duration { get; init; }

        public int? CustomSeasonNumber { get; init; }

        public int? CustomEpisodeNumber { get; init; }

        public string? CustomEpisodeYear { get; init; }

        public string? CustomEpisodeSubtitle { get; init; }

        public string? CustomEpisodeThumbnailPath { get; init; }

        public int SortIndex { get; init; }
    }

    private sealed class ContinueWatchingRow
    {
        public long? MovieId { get; init; }

        public long? TvShowId { get; init; }

        public string Title { get; init; } = string.Empty;

        public string? Subtitle { get; init; }

        public string? PosterPath { get; init; }

        public double? VoteAverage { get; init; }

        public string MediaKind { get; init; } = string.Empty;

        public double ContinueWatchingProgress { get; init; }
    }

    private static PlaybackWatchState ResolveLibraryWatchState(LibraryPlaybackStateRow row)
    {
        if (row.FileCount <= 0 || row.ProgressCount <= 0)
        {
            return PlaybackWatchState.Unwatched;
        }

        if (string.Equals(row.MediaKind, "tv", StringComparison.OrdinalIgnoreCase))
        {
            return row.WatchedCount >= row.FileCount
                ? PlaybackWatchState.Watched
                : PlaybackWatchState.InProgress;
        }

        return row.WatchedCount > 0
            ? PlaybackWatchState.Watched
            : PlaybackWatchState.InProgress;
    }

    private static string FormatYear(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        return trimmed.Length >= 4
            ? trimmed[..4]
            : trimmed;
    }

    private sealed class LibraryPlaybackStateRow
    {
        public string LibraryId { get; init; } = string.Empty;

        public string MediaKind { get; init; } = string.Empty;

        public int FileCount { get; init; }

        public int ProgressCount { get; init; }

        public int WatchedCount { get; init; }
    }
}
