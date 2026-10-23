using GitGui.Infrastructure;
using Xunit;

namespace GitGui.Engine.Tests;

public class RecentRepositoriesTests
{
    [Fact]
    public async Task Persists_parameterized_paths_and_deduplicates_across_connections()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gitgui-db-" + Guid.NewGuid());
        var database = Path.Combine(directory, "recent.db");
        var path = Path.Combine(directory, "中文 ' quoted repository");
        try
        {
            var recent = new RecentRepositories(database);
            await recent.RememberAsync(path, default);
            await recent.RememberAsync(path, default);
            var reopened = new RecentRepositories(database);
            Assert.Equal(path, Assert.Single(await reopened.ListAsync(default)));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
