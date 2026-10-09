using Dapper;

namespace OmniPlay.Infrastructure.Data;

/// <summary>
/// 中文译名缓存表。key 规则与 mac 版 <c>chineseNameCache</c> 一致：
/// <c>person#&lt;tmdbId&gt;</c>、<c>work#&lt;movie|tv&gt;#&lt;id&gt;</c>、<c>alias#&lt;kind&gt;#&lt;刮削名&gt;</c>；
/// value 为 NULL 表示"TMDB 确认没有中文译名"的负缓存。
/// </summary>
public sealed class ChineseNameCacheRepository
{
    private readonly SqliteDatabase database;

    public ChineseNameCacheRepository(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task<Dictionary<string, ChineseNameCacheEntry>> LoadAsync(IEnumerable<string> keys)
    {
        var result = new Dictionary<string, ChineseNameCacheEntry>(StringComparer.Ordinal);
        var list = keys.Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0)
        {
            return result;
        }

        using var connection = database.OpenConnection();
        var rows = await connection.QueryAsync<ChineseNameCacheEntry>(
            new CommandDefinition(
                "SELECT key AS Key, value AS Value, updatedAt AS UpdatedAt FROM chineseNameCache WHERE key IN @keys",
                new { keys = list }));
        foreach (var row in rows)
        {
            result[row.Key] = row;
        }

        return result;
    }

    public async Task SaveAsync(IEnumerable<ChineseNameCacheEntry> entries, CancellationToken cancellationToken)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var entry in entries)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO chineseNameCache (key, value, updatedAt)
                VALUES (@Key, @Value, @UpdatedAt)
                ON CONFLICT(key) DO UPDATE SET value = @Value, updatedAt = @UpdatedAt
                """,
                entry,
                transaction,
                cancellationToken: cancellationToken));
        }

        transaction.Commit();
    }

    public async Task<ChineseNameCacheEntry?> FindAsync(string key)
    {
        using var connection = database.OpenConnection();
        return await connection.QueryFirstOrDefaultAsync<ChineseNameCacheEntry>(
            new CommandDefinition(
                "SELECT key AS Key, value AS Value, updatedAt AS UpdatedAt FROM chineseNameCache WHERE key = @key",
                new { key }));
    }
}

public sealed class ChineseNameCacheEntry
{
    public required string Key { get; init; }

    public string? Value { get; init; }

    public required double UpdatedAt { get; init; }
}
