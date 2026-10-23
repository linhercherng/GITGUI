using GitGui.Core;
using Microsoft.Data.Sqlite;

namespace GitGui.Infrastructure;

public sealed class WorkspaceStorage(string databasePath) : IRepositorySettings, ISnapshotCatalog
{
    private async Task<SqliteConnection> Open(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        try
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS trusted_repositories(root TEXT PRIMARY KEY);" +
                "CREATE TABLE IF NOT EXISTS snapshots(id TEXT PRIMARY KEY, root TEXT NOT NULL, title TEXT NOT NULL," +
                "created TEXT NOT NULL, state_ref TEXT NOT NULL, head TEXT, file_count INTEGER NOT NULL);";
            await command.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task<bool> IsTrustedAsync(string root, CancellationToken ct)
    {
        await using var connection = await Open(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM trusted_repositories WHERE root=$root";
        command.Parameters.AddWithValue("$root", Normalize(root));
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) > 0;
    }
    public async Task TrustAsync(string root, CancellationToken ct)
    {
        await using var connection = await Open(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO trusted_repositories(root) VALUES($root)";
        command.Parameters.AddWithValue("$root", Normalize(root));
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task SaveAsync(SnapshotInfo snapshot, CancellationToken ct)
    {
        await using var connection = await Open(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO snapshots VALUES($id,$root,$title,$created,$state,$head,$count)";
        command.Parameters.AddWithValue("$id", snapshot.Id);
        command.Parameters.AddWithValue("$root", Normalize(snapshot.Repository));
        command.Parameters.AddWithValue("$title", snapshot.Title);
        command.Parameters.AddWithValue("$created", snapshot.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$state", snapshot.StateRef);
        command.Parameters.AddWithValue("$head", (object?)snapshot.Head ?? DBNull.Value);
        command.Parameters.AddWithValue("$count", snapshot.FileCount);
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<IReadOnlyList<SnapshotInfo>> ListAsync(string root, CancellationToken ct)
    {
        await using var connection = await Open(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,title,created,state_ref,head,file_count FROM snapshots WHERE root=$root ORDER BY created DESC";
        command.Parameters.AddWithValue("$root", Normalize(root));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<SnapshotInfo>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0), root, reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
            reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5)));
        return result;
    }
    public async Task RemoveAsync(string id, CancellationToken ct)
    {
        await using var connection = await Open(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM snapshots WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
    }
    private static string Normalize(string root) => OperatingSystem.IsWindows()
        ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant()
        : Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
}
