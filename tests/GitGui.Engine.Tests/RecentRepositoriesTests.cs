using GitGui.Infrastructure;
using Xunit;

namespace GitGui.Engine.Tests;

public class RecentRepositoriesTests
{
    [Fact]
    public async Task Empty_recent_list_does_not_create_storage_and_first_write_still_persists()
    {
        using var temporary = new TemporaryRepository();
        var directory = Path.Combine(temporary.Root, "settings");
        var database = Path.Combine(directory, "recent.db");
        var recent = new RecentRepositories(database);
        Assert.Empty(await recent.ListAsync(default));
        Assert.False(Directory.Exists(directory));
        await recent.RememberAsync(temporary.Root, default);
        Assert.True(File.Exists(database));
        Assert.Equal(temporary.Root, Assert.Single(await new RecentRepositories(database).ListAsync(default)));
    }

    [Fact]
    public async Task Existing_invalid_database_is_not_silently_treated_as_an_empty_list()
    {
        using var temporary = new TemporaryRepository();
        var database = Path.Combine(temporary.Root, "recent.db");
        await File.WriteAllTextAsync(database, "not a SQLite database");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => new RecentRepositories(database).ListAsync(default));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.Equal("not a SQLite database", await File.ReadAllTextAsync(database));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Database_lock_does_not_block_the_calling_thread(bool write)
    {
        var directory = Path.Combine(Path.GetTempPath(), "gitgui-recent-lock-" + Guid.NewGuid());
        var database = Path.Combine(directory, "recent.db");
        var recent = new RecentRepositories(database);
        var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? operation = null;
        Thread? caller = null;
        try
        {
            await recent.RememberAsync(directory, default);
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + database);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "BEGIN EXCLUSIVE"; command.ExecuteNonQuery();
            try
            {
                caller = new Thread(() =>
                {
                    try { returned.TrySetResult(write ? recent.RememberAsync(directory, default) : recent.ListAsync(default)); }
                    catch (Exception ex) { returned.TrySetException(ex); }
                }) { IsBackground = true };
                caller.Start();
                // The database is deliberately unavailable. Returning a pending Task
                // must not wait for SQLite's synchronous busy handler on the caller.
                operation = await returned.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(operation.IsCompleted);
            }
            finally
            {
                command.CommandText = "ROLLBACK"; command.ExecuteNonQuery();
                Assert.True(caller!.Join(TimeSpan.FromSeconds(5)));
                operation ??= await returned.Task;
                await operation;
            }
            Assert.Equal(directory, Assert.Single(await recent.ListAsync(default)));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

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
