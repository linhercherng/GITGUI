using System.Text;
using GitGui.Core;
using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public class SnapshotSafetyTests
{
    [Fact]
    public async Task Trusted_clean_filter_reads_match_the_real_git_index()
    {
        using var repo = new TemporaryRepository();
        await repo.Init();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".gitattributes"), "tracked.txt filter=lower\n");
        await repo.Git("config", "filter.lower.clean", "tr '[:upper:]' '[:lower:]'");
        await repo.Git("add", "--", ".gitattributes");
        await repo.Git("commit", "-m", "attributes");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "BASE\n");
        var runner = new GitProcessRunner();
        var catalog = new MemoryCatalog();
        var trusted = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction());
        Assert.Empty((await trusted.OpenAsync(repo.Root, new(), default)).Status.Files);
        var untrusted = new WorkspaceService(runner, new TestSettings(false), catalog, new(runner, catalog), new TestInteraction());
        Assert.Contains((await untrusted.OpenAsync(repo.Root, new(), default)).Status.Files, f => f.Path == "tracked.txt");
    }

    [Fact]
    public async Task Untrusted_status_never_executes_clean_filter_but_trusted_git_can()
    {
        using var repo = new TemporaryRepository();
        await repo.Init();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".gitattributes"), "tracked.txt filter=spy\n");
        await repo.Git("config", "filter.spy.clean", "sh -c 'echo invoked > filter-marker; cat'");
        await repo.Git("config", "filter.spy.required", "true");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "BASE\n");
        var runner = new GitProcessRunner();
        await runner.ReadAsync(repo.Root, ["status", "--porcelain=v2"], default);
        Assert.False(File.Exists(Path.Combine(repo.Root, "filter-marker")));
        await runner.RunAsync(repo.Root, ["status", "--porcelain=v2"], default, new() { Trusted = true });
        Assert.True(File.Exists(Path.Combine(repo.Root, "filter-marker")));
    }

    [Fact]
    public async Task Existing_index_lock_is_preserved_and_blocks_before_worktree_changes()
    {
        using var repo = new TemporaryRepository();
        await repo.Init();
        var manager = new SnapshotManager(new GitProcessRunner(), new MemoryCatalog());
        var snapshot = await manager.CreateAsync(repo.Root, "before", default);
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "keep current\n");
        var indexLock = Path.Combine(repo.Root, ".git", "index.lock");
        await File.WriteAllTextAsync(indexLock, "external process");
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RestoreAsync(repo.Root, snapshot, default));
        Assert.Equal("keep current\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
        Assert.Equal("external process", await File.ReadAllTextAsync(indexLock));
    }

    [Fact]
    public async Task Ref_namespace_conflict_fails_before_materializing_or_rebuilding_index()
    {
        using var repo = new TemporaryRepository();
        await repo.Init();
        var manager = new SnapshotManager(new GitProcessRunner(), new MemoryCatalog());
        var snapshot = await manager.CreateAsync(repo.Root, "before", default);
        await repo.Git("branch", "-m", "main", "main/nested");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "keep\n");
        var index = await File.ReadAllBytesAsync(Path.Combine(repo.Root, ".git", "index"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RestoreAsync(repo.Root, snapshot, default));
        Assert.Equal("keep\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
        Assert.Equal(index, await File.ReadAllBytesAsync(Path.Combine(repo.Root, ".git", "index")));
    }

    [Fact]
    public async Task Regular_symlink_placeholder_preserves_actual_worktree_type()
    {
        using var repo = new TemporaryRepository();
        await repo.Init();
        await repo.Git("config", "core.symlinks", "false");
        var runner = new GitProcessRunner();
        var result = await runner.RunAsync(repo.Root, ["hash-object", "-w", "--stdin"], default,
            new() { IsReadOnly = false, Input = Encoding.UTF8.GetBytes("target.txt") });
        var oid = Encoding.UTF8.GetString(result.Output).Trim();
        await repo.Git("update-index", "--add", "--cacheinfo", "120000," + oid + ",link.txt");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "link.txt"), "target.txt");
        var manager = new SnapshotManager(runner, new MemoryCatalog());
        var snapshot = await manager.CreateAsync(repo.Root, "placeholder", default);
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "link.txt"), "modified");
        await manager.RestoreAsync(repo.Root, snapshot, default);
        Assert.Null(new FileInfo(Path.Combine(repo.Root, "link.txt")).LinkTarget);
        Assert.Equal("target.txt", await File.ReadAllTextAsync(Path.Combine(repo.Root, "link.txt")));
        Assert.Contains("120000 " + oid, await repo.Git("ls-files", "--stage"));
    }

    [Fact]
    public async Task Retention_keeps_recent_age_window_even_beyond_count_window()
    {
        using var repo = new TemporaryRepository();
        await repo.Init();
        var oid = (await repo.Git("rev-parse", "HEAD")).Trim();
        var catalog = new MemoryCatalog();
        foreach (var days in new[] { 1, 2, 3, 20, 30 })
        {
            var id = "retention-" + days;
            var reference = $"refs/gitgui/snapshots/{id}/state";
            await repo.Git("update-ref", reference, oid);
            await catalog.SaveAsync(new(id, repo.Root, "retention", DateTimeOffset.UtcNow.AddDays(-days), reference, oid, 1), default);
        }
        var manager = new SnapshotManager(new GitProcessRunner(), catalog) { MinimumRetainedCount = 2 };
        await manager.PruneAsync(repo.Root, default);
        Assert.Equal(3, (await catalog.ListAsync(repo.Root, default)).Count);
        Assert.Equal(3, (await repo.Git("for-each-ref", "--format=%(refname)", "refs/gitgui/snapshots")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }
}
