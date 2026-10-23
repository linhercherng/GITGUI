using GitGui.Core;
using Microsoft.Data.Sqlite;

namespace GitGui.Infrastructure;

public sealed class RecentRepositories(string databasePath) : IRecentRepositories
{
    private async Task<SqliteConnection> Open(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS repositories(path TEXT PRIMARY KEY, opened TEXT NOT NULL)";
        await command.ExecuteNonQueryAsync(ct);
        return connection;
    }

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct)
    {
        await using var connection = await Open(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM repositories ORDER BY opened DESC LIMIT 20";
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetString(0));
        return result;
    }

    public async Task RememberAsync(string path, CancellationToken ct)
    {
        await using var connection = await Open(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO repositories(path, opened) VALUES($path, $time) " +
            "ON CONFLICT(path) DO UPDATE SET opened=excluded.opened";
        command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }
}
