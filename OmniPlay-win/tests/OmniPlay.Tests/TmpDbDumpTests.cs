using Microsoft.Data.Sqlite;

namespace OmniPlay.Tests;

public sealed class TmpDbDumpTests
{
    [Fact]
    public void DumpLiveData()
    {
        var dbPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tmp", "live1.sqlite"));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        var log = new System.Text.StringBuilder();
        Dump(log, connection, "SELECT id, name, protocolType, baseUrl, isEnabled, authConfig, addressConfig, disabledAt, removedAt FROM mediaSource");
        log.AppendLine("== videoFile count: " + Scalar(connection, "SELECT COUNT(*) FROM videoFile") + " | movie: " + Scalar(connection, "SELECT COUNT(*) FROM movie") + " | tvShow: " + Scalar(connection, "SELECT COUNT(*) FROM tvShow"));
        Dump(log, connection, "SELECT sourceId, mediaType, COUNT(*) FROM videoFile GROUP BY sourceId, mediaType");
        Dump(log, connection, "SELECT id, sourceId, relativePath, fileName, mediaType, movieId, episodeId FROM videoFile LIMIT 30");
        Dump(log, connection, "SELECT * FROM movie LIMIT 15");
        Dump(log, connection, "SELECT * FROM tvShow LIMIT 15");
        File.WriteAllText(@"C:\软件\OmniPlay-win\tmp\dbdump.txt", log.ToString());
    }

    private static void Dump(System.Text.StringBuilder log, SqliteConnection connection, string sql)
    {
        log.AppendLine("== " + sql);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var parts = new System.Collections.Generic.List<string>();
            for (var i = 0; i < reader.FieldCount; i++) parts.Add(reader.IsDBNull(i) ? "null" : reader.GetValue(i).ToString());
            log.AppendLine(string.Join(" | ", parts));
        }
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}

