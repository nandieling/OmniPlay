using Dapper;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Infrastructure.Data;

public sealed class MediaSourceRepository : IMediaSourceRepository
{
    private static readonly TimeSpan InactiveRetention = TimeSpan.FromDays(30);
    private readonly SqliteDatabase database;

    public MediaSourceRepository(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task<IReadOnlyList<MediaSource>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        var sources = (await connection.QueryAsync<MediaSource>(
            new CommandDefinition(
                """
                SELECT id, name, protocolType, baseUrl, authConfig, isEnabled, disabledAt, removedAt
                FROM mediaSource
                WHERE removedAt IS NULL
                ORDER BY id ASC
                """,
                cancellationToken: cancellationToken))).ToList();

        foreach (var source in sources)
        {
            source.AuthConfig = MediaSourceAuthConfigProtector.UnprotectFromStorage(source.AuthConfig);
        }

        return sources;
    }

    public async Task<long> AddAsync(MediaSource source, CancellationToken cancellationToken = default)
    {
        var normalizedBaseUrl = source.GetNormalizedBaseUrl();
        var protectedAuthConfig = MediaSourceAuthConfigProtector.ProtectForStorage(source.AuthConfig);

        using var connection = database.OpenConnection();
        var existingId = await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(
                "SELECT id FROM mediaSource WHERE protocolType = @ProtocolType AND baseUrl = @BaseUrl LIMIT 1",
                new
                {
                    source.ProtocolType,
                    BaseUrl = normalizedBaseUrl
                },
                cancellationToken: cancellationToken));

        if (existingId.HasValue)
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE mediaSource
                    SET name = @Name,
                        authConfig = @AuthConfig,
                        isEnabled = 1,
                        disabledAt = NULL,
                        removedAt = NULL
                    WHERE id = @Id
                    """,
                    new
                    {
                        Id = existingId.Value,
                        source.Name,
                        AuthConfig = protectedAuthConfig
                    },
                    cancellationToken: cancellationToken));
            return existingId.Value;
        }

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO mediaSource (name, protocolType, baseUrl, authConfig, isEnabled, disabledAt, removedAt)
                VALUES (@Name, @ProtocolType, @BaseUrl, @AuthConfig, @IsEnabled, NULL, NULL)
                """,
                new
                {
                    source.Name,
                    source.ProtocolType,
                    BaseUrl = normalizedBaseUrl,
                    AuthConfig = protectedAuthConfig,
                    source.IsEnabled
                },
                cancellationToken: cancellationToken));

        return await connection.ExecuteScalarAsync<long>(
            new CommandDefinition("SELECT last_insert_rowid()", cancellationToken: cancellationToken));
    }

    public async Task<bool> UpdateAsync(MediaSource source, CancellationToken cancellationToken = default)
    {
        if (source.Id is null)
        {
            return false;
        }

        var normalizedBaseUrl = source.GetNormalizedBaseUrl();
        var protectedAuthConfig = MediaSourceAuthConfigProtector.ProtectForStorage(source.AuthConfig);

        using var connection = database.OpenConnection();
        var duplicateId = await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(
                """
                SELECT id
                FROM mediaSource
                WHERE protocolType = @ProtocolType
                  AND baseUrl = @BaseUrl
                  AND id <> @Id
                LIMIT 1
                """,
                new
                {
                    Id = source.Id.Value,
                    source.ProtocolType,
                    BaseUrl = normalizedBaseUrl
                },
                cancellationToken: cancellationToken));

        if (duplicateId.HasValue)
        {
            return false;
        }

        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE mediaSource
                SET name = @Name,
                    protocolType = @ProtocolType,
                    baseUrl = @BaseUrl,
                    authConfig = @AuthConfig,
                    isEnabled = @IsEnabled,
                    disabledAt = @DisabledAt,
                    removedAt = @RemovedAt
                WHERE id = @Id
                """,
                new
                {
                    Id = source.Id.Value,
                    source.Name,
                    source.ProtocolType,
                    BaseUrl = normalizedBaseUrl,
                    AuthConfig = protectedAuthConfig,
                    source.IsEnabled,
                    source.DisabledAt,
                    source.RemovedAt
                },
                cancellationToken: cancellationToken));

        return affected > 0;
    }

    public async Task<bool> SetEnabledAsync(
        long sourceId,
        bool isEnabled,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE mediaSource
                SET isEnabled = @IsEnabled,
                    disabledAt = CASE WHEN @IsEnabled = 1 THEN NULL ELSE @ChangedAt END,
                    removedAt = NULL
                WHERE id = @Id
                """,
                new
                {
                    Id = sourceId,
                    IsEnabled = isEnabled ? 1 : 0,
                    ChangedAt = FormatTimestamp(now)
                },
                cancellationToken: cancellationToken));

        return affected > 0;
    }

    public async Task<bool> SoftRemoveAsync(long sourceId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE mediaSource
                SET isEnabled = 0,
                    disabledAt = COALESCE(disabledAt, @ChangedAt),
                    removedAt = @ChangedAt
                WHERE id = @Id
                """,
                new
                {
                    Id = sourceId,
                    ChangedAt = FormatTimestamp(now)
                },
                cancellationToken: cancellationToken));

        return affected > 0;
    }

    public async Task<int> PurgeExpiredInactiveAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var cutoff = FormatTimestamp(now.Subtract(InactiveRetention));
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var removedSources = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                DELETE FROM mediaSource
                WHERE (removedAt IS NOT NULL AND removedAt <= @Cutoff)
                   OR (removedAt IS NULL AND isEnabled = 0 AND disabledAt IS NOT NULL AND disabledAt <= @Cutoff)
                """,
                new { Cutoff = cutoff },
                transaction,
                cancellationToken: cancellationToken));

        await PruneOrphanEntitiesAsync(connection, transaction, cancellationToken);
        transaction.Commit();
        return removedSources;
    }

    public async Task<bool> RemoveAsync(long sourceId, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM mediaSource WHERE id = @Id",
                new { Id = sourceId },
                cancellationToken: cancellationToken));

        return affected > 0;
    }

    private static async Task PruneOrphanEntitiesAsync(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM movie WHERE id NOT IN (SELECT DISTINCT movieId FROM videoFile WHERE movieId IS NOT NULL)",
                transaction: transaction,
                cancellationToken: cancellationToken));
        await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM tvShow WHERE id NOT IN (SELECT DISTINCT episodeId FROM videoFile WHERE episodeId IS NOT NULL)",
                transaction: transaction,
                cancellationToken: cancellationToken));
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    }
}
