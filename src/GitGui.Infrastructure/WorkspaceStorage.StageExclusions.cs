using GitGui.Core;
using Microsoft.Data.Sqlite;

namespace GitGui.Infrastructure;

public sealed partial class WorkspaceStorage : IStageExclusionStore
{
    private async Task<SqliteConnection> OpenStageExclusions(CancellationToken ct)
    {
        var connection = await Open(ct);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS stage_exclusions(root TEXT NOT NULL,path TEXT NOT NULL,directory INTEGER NOT NULL CHECK(directory IN (0,1)),PRIMARY KEY(root,path,directory))";
            await command.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task<IReadOnlyList<StageExclusion>> LoadStageExclusionsAsync(string root, CancellationToken ct)
    {
        var key = Normalize(root);
        await using var connection = await OpenStageExclusions(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path,directory FROM stage_exclusions WHERE root=$root ORDER BY directory,path";
        command.Parameters.AddWithValue("$root", key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rules = new List<StageExclusion>();
        while (await reader.ReadAsync(ct)) rules.Add(new(reader.GetString(0), reader.GetBoolean(1)));
        return new StageExclusionRules(rules).Rules;
    }

    public async Task SaveStageExclusionsAsync(string root, IReadOnlyList<StageExclusion> rules, CancellationToken ct)
    {
        var key = Normalize(root);
        var normalized = new StageExclusionRules(rules).Rules;
        await using var connection = await OpenStageExclusions(ct);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM stage_exclusions WHERE root=$root";
        command.Parameters.AddWithValue("$root", key);
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = "INSERT INTO stage_exclusions(root,path,directory) VALUES($root,$path,$directory)";
        var path = command.Parameters.Add("$path", SqliteType.Text);
        var directory = command.Parameters.Add("$directory", SqliteType.Integer);
        foreach (var rule in normalized)
        {
            path.Value = rule.Path; directory.Value = rule.IsDirectory ? 1 : 0;
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }
}
