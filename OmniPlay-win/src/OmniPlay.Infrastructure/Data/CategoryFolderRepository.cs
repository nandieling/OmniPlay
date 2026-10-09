using System.Data;
using Dapper;
using OmniPlay.Core.Models;
using OmniPlay.Core.Models.Library;

namespace OmniPlay.Infrastructure.Data;

/// <summary>分类文件夹与人物档案的读写，以及"改名"带来的跨表迁移。</summary>
public sealed class CategoryFolderRepository
{
    private const string ProfileColumns =
        "id AS Id, name AS Name, kind AS Kind, tmdbPersonId AS TmdbPersonId, photoPath AS PhotoPath, " +
        "filmographyJson AS FilmographyJson, updatedAt AS UpdatedAt";

    private const string FolderColumns =
        "id AS Id, kind AS Kind, name AS Name, posterPath AS PosterPath, updatedAt AS UpdatedAt";

    private readonly SqliteDatabase database;

    public CategoryFolderRepository(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task<IReadOnlyList<CategoryFolder>> LoadFoldersAsync(string kind, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        var rows = await connection.QueryAsync<CategoryFolder>(new CommandDefinition(
            $"SELECT {FolderColumns} FROM categoryFolder WHERE kind = @kind ORDER BY name COLLATE NOCASE",
            new { kind },
            cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<PersonProfile>> LoadProfilesAsync(string kind, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        var rows = await connection.QueryAsync<PersonProfile>(new CommandDefinition(
            $"SELECT {ProfileColumns} FROM personProfile WHERE kind = @kind ORDER BY name COLLATE NOCASE",
            new { kind },
            cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task UpsertFoldersAsync(
        IEnumerable<(string Kind, string Name)> people,
        CancellationToken cancellationToken = default)
    {
        var now = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var (kind, name) in people)
        {
            await WriteAsync(connection, transaction,
                "INSERT INTO categoryFolder (id, kind, name, posterPath, updatedAt) " +
                "VALUES (@Id, @Kind, @Name, NULL, @UpdatedAt) " +
                "ON CONFLICT(id) DO UPDATE SET updatedAt = @UpdatedAt",
                new { Id = CategoryFolder.FolderId(kind, name), Kind = kind, Name = name, UpdatedAt = now },
                cancellationToken);
        }

        transaction.Commit();
    }

    /// <summary>设置（或清空）某个分类文件夹的封面；文件夹不存在时顺带创建。</summary>
    public async Task SetFolderPosterAsync(
        string kind,
        string name,
        string? posterPath,
        CancellationToken cancellationToken = default)
    {
        var now = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var connection = database.OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO categoryFolder (id, kind, name, posterPath, updatedAt) " +
            "VALUES (@Id, @Kind, @Name, @PosterPath, @UpdatedAt) " +
            "ON CONFLICT(id) DO UPDATE SET posterPath = @PosterPath, updatedAt = @UpdatedAt",
            new { Id = CategoryFolder.FolderId(kind, name), Kind = kind, Name = name, PosterPath = posterPath, UpdatedAt = now },
            cancellationToken: cancellationToken));
    }

    /// <summary>登记人物档案：已存在时只补空字段，不覆盖用户手选头像与已缓存的作品列表。</summary>
    public async Task UpsertProfilesAsync(
        IEnumerable<(string Kind, string Name, long TmdbPersonId, string? PhotoPath)> people,
        CancellationToken cancellationToken = default)
    {
        var now = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var (kind, name, tmdbPersonId, photoPath) in people)
        {
            var id = PersonProfile.ProfileId(kind, name);
            var existing = await ReadAsync<PersonProfile>(
                connection, transaction, $"SELECT {ProfileColumns} FROM personProfile WHERE id = @id", new { id }, cancellationToken);
            if (existing is null)
            {
                await WriteAsync(connection, transaction,
                    "INSERT INTO personProfile (id, name, kind, tmdbPersonId, photoPath, filmographyJson, updatedAt) " +
                    "VALUES (@Id, @Name, @Kind, @TmdbPersonId, @PhotoPath, NULL, @UpdatedAt)",
                    new { Id = id, Name = name, Kind = kind, TmdbPersonId = tmdbPersonId, PhotoPath = photoPath, UpdatedAt = now },
                    cancellationToken);
                continue;
            }

            await WriteAsync(connection, transaction,
                "UPDATE personProfile SET name = @Name, tmdbPersonId = COALESCE(tmdbPersonId, @TmdbPersonId), " +
                "photoPath = CASE WHEN photoPath IS NULL OR photoPath = '' THEN @PhotoPath ELSE photoPath END, " +
                "updatedAt = @UpdatedAt WHERE id = @Id",
                new { Id = id, Name = name, TmdbPersonId = tmdbPersonId, PhotoPath = photoPath, UpdatedAt = now },
                cancellationToken);
        }

        transaction.Commit();
    }

    public async Task SetProfileFilmographyAsync(
        string kind,
        string name,
        string? filmographyJson,
        CancellationToken cancellationToken = default)
    {
        var now = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var connection = database.OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO personProfile (id, name, kind, tmdbPersonId, photoPath, filmographyJson, updatedAt) " +
            "VALUES (@Id, @Name, @Kind, NULL, NULL, @FilmographyJson, @UpdatedAt) " +
            "ON CONFLICT(id) DO UPDATE SET filmographyJson = @FilmographyJson, updatedAt = @UpdatedAt",
            new { Id = PersonProfile.ProfileId(kind, name), Name = name, Kind = kind, FilmographyJson = filmographyJson, UpdatedAt = now },
            cancellationToken: cancellationToken));
    }

    /// <summary>更新人物头像路径（刮削时先记 TMDB 相对路径，首次用到时换成本地文件路径）。</summary>
    public async Task SetProfilePhotoAsync(
        string kind,
        string name,
        string? photoPath,
        CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE personProfile SET photoPath = @PhotoPath WHERE id = @Id",
            new { Id = PersonProfile.ProfileId(kind, name), PhotoPath = photoPath },
            cancellationToken: cancellationToken));
    }

    public async Task<PersonProfile?> FindProfileAsync(string kind, string name, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        return await connection.QueryFirstOrDefaultAsync<PersonProfile>(new CommandDefinition(
            $"SELECT {ProfileColumns} FROM personProfile WHERE id = @id",
            new { id = PersonProfile.ProfileId(kind, name) },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// 人物改名：改写影片名单（JSON 数组与历史扁平列）、文件夹主键（保留已手选封面）
    /// 与人物档案主键（保留头像与作品列表缓存）。已有同名目标时按补空合并。
    /// </summary>
    public async Task<bool> RenamePersonAsync(
        string kind,
        string oldName,
        string newName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName) ||
            string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return false;
        }

        var changed = false;
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var now = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var table in new[] { "movie", "tvShow" })
        {
            var rows = (await connection.QueryAsync<PersonListRow>(new CommandDefinition(
                $"SELECT id AS Id, directors AS Directors, casts AS Casts FROM {table} " +
                "WHERE directors IS NOT NULL OR casts IS NOT NULL",
                transaction,
                cancellationToken: cancellationToken))).ToList();

            foreach (var row in rows)
            {
                var directors = PersonNames.Decode(row.Directors);
                var casts = PersonNames.Decode(row.Casts);
                var target = string.Equals(kind, "director", StringComparison.OrdinalIgnoreCase) ? directors : casts;
                var index = target.FindIndex(name => string.Equals(name, oldName, StringComparison.Ordinal));
                if (index < 0)
                {
                    continue;
                }

                // 改名后可能与既有成员重名：去重保序。
                target[index] = newName;
                var deduped = target
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (string.Equals(kind, "director", StringComparison.OrdinalIgnoreCase))
                {
                    directors = deduped;
                }
                else
                {
                    casts = deduped;
                }

                await WriteAsync(connection, transaction,
                    $"UPDATE {table} SET directors = @Directors, casts = @Casts, director = @DirectorText, " +
                    "mainCast = @MainCastText WHERE id = @Id",
                    new
                    {
                        row.Id,
                        Directors = PersonNames.Encode(directors),
                        Casts = PersonNames.Encode(casts),
                        DirectorText = PersonNames.Flatten(PersonNames.Encode(directors)),
                        MainCastText = PersonNames.Flatten(PersonNames.Encode(casts))
                    },
                    cancellationToken);
                changed = true;
            }
        }

        changed |= await MigrateFolderAsync(connection, transaction, kind, oldName, newName, now, cancellationToken);
        changed |= await MigrateProfileAsync(connection, transaction, kind, oldName, newName, now, cancellationToken);
        transaction.Commit();
        return changed;
    }

    private static async Task<bool> MigrateFolderAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string kind,
        string oldName,
        string newName,
        double now,
        CancellationToken cancellationToken)
    {
        var sourceId = CategoryFolder.FolderId(kind, oldName);
        var targetId = CategoryFolder.FolderId(kind, newName);
        var source = await ReadAsync<CategoryFolder>(
            connection, transaction, $"SELECT {FolderColumns} FROM categoryFolder WHERE id = @id", new { id = sourceId }, cancellationToken);
        if (source is null)
        {
            return false;
        }

        var target = await ReadAsync<CategoryFolder>(
            connection, transaction, $"SELECT {FolderColumns} FROM categoryFolder WHERE id = @id", new { id = targetId }, cancellationToken);
        if (target is null)
        {
            await WriteAsync(connection, transaction,
                "UPDATE categoryFolder SET id = @targetId, name = @newName, updatedAt = @now WHERE id = @sourceId",
                new { targetId, newName, now, sourceId },
                cancellationToken);
            return true;
        }

        if (string.IsNullOrEmpty(target.PosterPath) && !string.IsNullOrEmpty(source.PosterPath))
        {
            await WriteAsync(connection, transaction,
                "UPDATE categoryFolder SET posterPath = @poster WHERE id = @targetId",
                new { poster = source.PosterPath, targetId },
                cancellationToken);
        }

        await WriteAsync(connection, transaction,
            "DELETE FROM categoryFolder WHERE id = @sourceId",
            new { sourceId },
            cancellationToken);
        return true;
    }

    private static async Task<bool> MigrateProfileAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string kind,
        string oldName,
        string newName,
        double now,
        CancellationToken cancellationToken)
    {
        var sourceId = PersonProfile.ProfileId(kind, oldName);
        var targetId = PersonProfile.ProfileId(kind, newName);
        var source = await ReadAsync<PersonProfile>(
            connection, transaction, $"SELECT {ProfileColumns} FROM personProfile WHERE id = @id", new { id = sourceId }, cancellationToken);
        if (source is null)
        {
            return false;
        }

        var target = await ReadAsync<PersonProfile>(
            connection, transaction, $"SELECT {ProfileColumns} FROM personProfile WHERE id = @id", new { id = targetId }, cancellationToken);
        if (target is null)
        {
            await WriteAsync(connection, transaction,
                "UPDATE personProfile SET id = @targetId, name = @newName, updatedAt = @now WHERE id = @sourceId",
                new { targetId, newName, now, sourceId },
                cancellationToken);
            return true;
        }

        await WriteAsync(connection, transaction,
            "UPDATE personProfile SET tmdbPersonId = COALESCE(tmdbPersonId, @TmdbPersonId), " +
            "photoPath = CASE WHEN photoPath IS NULL OR photoPath = '' THEN @PhotoPath ELSE photoPath END, " +
            "filmographyJson = COALESCE(filmographyJson, @FilmographyJson), updatedAt = @now WHERE id = @targetId",
            new { targetId, source.TmdbPersonId, source.PhotoPath, source.FilmographyJson, now },
            cancellationToken);
        await WriteAsync(connection, transaction,
            "DELETE FROM personProfile WHERE id = @sourceId",
            new { sourceId },
            cancellationToken);
        return true;
    }

    private static Task WriteAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string sql,
        object param,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(sql, param, transaction, cancellationToken: cancellationToken));

    private static Task<T?> ReadAsync<T>(
        IDbConnection connection,
        IDbTransaction transaction,
        string sql,
        object param,
        CancellationToken cancellationToken) =>
        connection.QueryFirstOrDefaultAsync<T>(new CommandDefinition(sql, param, transaction, cancellationToken: cancellationToken));

    private sealed class PersonListRow
    {
        public long Id { get; init; }

        public string? Directors { get; init; }

        public string? Casts { get; init; }
    }
}
