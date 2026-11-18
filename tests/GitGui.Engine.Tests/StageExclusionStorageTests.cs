using GitGui.Core;
using GitGui.Infrastructure;
using GitGui.Engine.Cli;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class StageExclusionStorageTests
{
    [Fact]
    public async Task Filtered_staging_changes_only_eligible_index_entries_and_keeps_excluded_files()
    {
        using var repo = new TemporaryRepository(); using var settings = new TemporaryRepository();
        await repo.Init();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "private.txt"), "private baseline\n");
        await repo.Git("add", "--", "private.txt"); await repo.Git("commit", "-m", "private baseline");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "tracked changed\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "private.txt"), "private changed\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "keep.txt"), "new source\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "output"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "output", "generated.txt"), "generated\n");
        IStageExclusionStore store = new WorkspaceStorage(Path.Combine(settings.Root, "settings.db"));
        await store.SaveStageExclusionsAsync(repo.Root, [new("private.txt", false), new("output", true)], default);
        var rules = new StageExclusionRules(await store.LoadStageExclusionsAsync(repo.Root, default));
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new SnapshotManager(runner, catalog), new TestInteraction());
        var state = await service.OpenAsync(repo.Root, new(), default);
        var paths = state.Status.Files.Where(file => !rules.IsExcluded(file)).Select(file => file.Path).ToArray();
        var outcome = await service.ExecuteAsync(repo.Root, new(GitAction.Stage) { Paths = paths }, null, default);
        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(["keep.txt", "tracked.txt"], (await repo.Git("diff", "--cached", "--name-only", "-z")).Split('\0', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("private changed\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "private.txt")));
        Assert.True(File.Exists(Path.Combine(repo.Root, "output", "generated.txt")));
        Assert.False(File.Exists(Path.Combine(repo.Root, ".gitignore")));
    }

    [Fact]
    public async Task Rules_survive_reopening_and_are_isolated_by_repository()
    {
        using var temporary = new TemporaryRepository();
        var database = Path.Combine(temporary.Root, "settings.db");
        IStageExclusionStore store = new WorkspaceStorage(database);
        var first = Path.Combine(temporary.Root, "專案 A"); var second = Path.Combine(temporary.Root, "專案 B");
        await store.SaveStageExclusionsAsync(first, [new("秘密.txt", false), new("output", true)], default);
        await store.SaveStageExclusionsAsync(second, [new("other.txt", false)], default);
        store = new WorkspaceStorage(database);
        var rules = await store.LoadStageExclusionsAsync(first, default);
        Assert.Equal(2, rules.Count); Assert.Contains(new("秘密.txt", false), rules); Assert.Contains(new("output", true), rules);
        Assert.Equal([new StageExclusion("other.txt", false)], await store.LoadStageExclusionsAsync(second, default));
        await store.SaveStageExclusionsAsync(first, [], default);
        Assert.Empty(await store.LoadStageExclusionsAsync(first, default));
        Assert.Single(await store.LoadStageExclusionsAsync(second, default));
    }

    [Fact]
    public async Task Invalid_input_and_failed_insert_preserve_the_previous_rules()
    {
        using var temporary = new TemporaryRepository();
        var database = Path.Combine(temporary.Root, "settings.db");
        IStageExclusionStore store = new WorkspaceStorage(database);
        await store.SaveStageExclusionsAsync(temporary.Root, [new("keep.txt", false)], default);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveStageExclusionsAsync(temporary.Root, [new("../outside", false)], default));
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
        {
            await connection.OpenAsync(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_rule BEFORE INSERT ON stage_exclusions WHEN NEW.path='blocked.txt' BEGIN SELECT RAISE(ABORT,'injected storage failure'); END";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => store.SaveStageExclusionsAsync(temporary.Root, [new("new.txt", false), new("blocked.txt", false)], default));
        Assert.Equal([new StageExclusion("keep.txt", false)], await store.LoadStageExclusionsAsync(temporary.Root, default));
    }
}
