using GitGui.Core;
using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public class RepositoryToolsTests
{
    private static (RepositoryToolsService Tools, WorkspaceService Workspace, TestInteraction Interaction) Services()
    {
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog(); var prompt = new TestInteraction();
        var snapshots = new SnapshotManager(runner, catalog);
        var workspace = new WorkspaceService(runner, new TestSettings(), catalog, snapshots, prompt);
        return (new(runner, workspace, snapshots, prompt), workspace, prompt);
    }

    [Fact]
    public async Task Worktree_lifecycle_lists_unicode_paths_and_protects_dirty_files()
    {
        using var repo = new TemporaryRepository(); await repo.Init(); var (tools, _, prompt) = Services();
        var path = (await repo.Git("rev-parse", "--show-toplevel")).Trim() + " 工作樹";
        try
        {
            await Do(tools, repo, new(RepositoryToolAction.WorktreeAdd) { Path = path, Name = "feature" });
            var row = Assert.Single((await tools.GetStateAsync(repo.Root, default)).Worktrees, w => w.Path == path);
            Assert.Equal("refs/heads/feature", row.Branch);
            await Do(tools, repo, new(RepositoryToolAction.WorktreeLock) { Path = path, Reason = "外接磁碟" });
            Assert.True((await tools.GetStateAsync(repo.Root, default)).Worktrees.Single(w => w.Path == path).Locked);
            Assert.False((await tools.ExecuteAsync(repo.Root, new(RepositoryToolAction.WorktreeRemove) { Path = path }, null, default)).Success);
            await Do(tools, repo, new(RepositoryToolAction.WorktreeUnlock) { Path = path });
            await File.WriteAllTextAsync(Path.Combine(path, "tracked.txt"), "unsaved");
            Assert.False((await tools.ExecuteAsync(repo.Root, new(RepositoryToolAction.WorktreeRemove) { Path = path }, null, default)).Success);
            Assert.Equal("unsaved", await File.ReadAllTextAsync(Path.Combine(path, "tracked.txt")));
            await File.WriteAllTextAsync(Path.Combine(path, "tracked.txt"), "base\n");
            await Do(tools, repo, new(RepositoryToolAction.WorktreeRemove) { Path = path });
            Assert.False(Directory.Exists(path)); Assert.Contains(prompt.Confirmations, p => p.Risk == RiskLevel.High);
        }
        finally { if (Directory.Exists(path)) await repo.Git("worktree", "remove", "--force", path); }
    }

    [Fact]
    public async Task Worktree_remove_cancellation_and_option_input_do_not_mutate()
    {
        using var repo = new TemporaryRepository(); await repo.Init(); var (tools, _, prompt) = Services();
        Assert.False((await tools.ExecuteAsync(repo.Root, new(RepositoryToolAction.WorktreeAdd) { Path = repo.Root + "-bad", Name = "--orphan" }, null, default)).Success);
        prompt.Accept = false;
        var result = await tools.ExecuteAsync(repo.Root, new(RepositoryToolAction.WorktreePrune), null, default);
        Assert.True(result.Cancelled); Assert.False(result.Success);
        Assert.Single((await tools.GetStateAsync(repo.Root, default)).Worktrees);
    }

    [Fact]
    public async Task Worktree_remove_preserves_ignored_private_files()
    {
        using var repo = new TemporaryRepository(); await repo.Init(); var tools = Services().Tools;
        var path = repo.Root + "-ignored";
        try
        {
            await Do(tools, repo, new(RepositoryToolAction.WorktreeAdd) { Path = path });
            await File.WriteAllTextAsync(Path.Combine(repo.Root, ".git", "info", "exclude"), "private.bin\n");
            await File.WriteAllTextAsync(Path.Combine(path, "private.bin"), "private data");
            var result = await tools.ExecuteAsync(repo.Root, new(RepositoryToolAction.WorktreeRemove) { Path = path }, null, default);
            Assert.False(result.Success); Assert.Equal("private data", await File.ReadAllTextAsync(Path.Combine(path, "private.bin")));
        }
        finally { if (Directory.Exists(path)) await repo.Git("worktree", "remove", "--force", path); }
    }

    [Theory]
    [InlineData("--assume-unchanged")]
    [InlineData("--skip-worktree")]
    public async Task Worktree_remove_refuses_index_flags_that_hide_unsaved_bytes(string flag)
    {
        using var repo = new TemporaryRepository(); await repo.Init(); var tools = Services().Tools;
        var path = repo.Root + "-hidden";
        try
        {
            await Do(tools, repo, new(RepositoryToolAction.WorktreeAdd) { Path = path });
            await repo.Git("-C", path, "update-index", flag, "tracked.txt");
            await File.WriteAllTextAsync(Path.Combine(path, "tracked.txt"), "hidden unsaved data");
            Assert.Empty((await repo.Git("-C", path, "status", "--porcelain")).Trim());
            var result = await tools.ExecuteAsync(repo.Root, new(RepositoryToolAction.WorktreeRemove) { Path = path }, null, default);
            Assert.False(result.Success); Assert.Equal("hidden unsaved data", await File.ReadAllTextAsync(Path.Combine(path, "tracked.txt")));
        }
        finally { if (Directory.Exists(path)) await repo.Git("worktree", "remove", "--force", path); }
    }

    [Fact]
    public async Task Bisect_start_mark_and_reset_survive_service_restart()
    {
        using var repo = new TemporaryRepository(); await repo.Init(); var (tools, workspace, _) = Services();
        var good = (await repo.Git("rev-parse", "HEAD")).Trim();
        for (var i = 0; i < 4; i++) { await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), $"{i}\n"); await repo.Git("commit", "-am", "step " + i); }
        var original = (await repo.Git("rev-parse", "HEAD")).Trim();
        await Do(tools, repo, new(RepositoryToolAction.BisectStart) { GoodRevision = good, Revision = original });
        Assert.True((await tools.GetStateAsync(repo.Root, default)).BisectActive);
        Assert.Equal("bisect", (await workspace.OpenAsync(repo.Root, new(Take: 1), default)).InProgress.Kind);
        await Do(tools, repo, new(RepositoryToolAction.BisectGood));
        tools = Services().Tools;
        await Do(tools, repo, new(RepositoryToolAction.BisectReset));
        Assert.False((await tools.GetStateAsync(repo.Root, default)).BisectActive);
        Assert.Equal(original, (await repo.Git("rev-parse", "HEAD")).Trim());
        Assert.Equal("main", (await repo.Git("branch", "--show-current")).Trim());
    }

    [Theory]
    [InlineData("secret.bin")]
    [InlineData("SECRET.BIN")]
    public async Task Bisect_refuses_ignored_collision_before_changing_head(string privatePath)
    {
        using var repo = new TemporaryRepository(); await repo.Init(); var tools = Services().Tools;
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "secret.bin"), "old tracked"); await repo.Git("add", "."); await repo.Git("commit", "-m", "old file");
        var good = (await repo.Git("rev-parse", "HEAD")).Trim();
        await repo.Git("rm", "secret.bin"); await repo.Git("commit", "-m", "remove");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".git", "info", "exclude"), "secret.bin\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, privatePath), "private ignored data");
        var head = (await repo.Git("rev-parse", "HEAD")).Trim();
        var result = await tools.ExecuteAsync(repo.Root, new(RepositoryToolAction.BisectStart) { GoodRevision = good }, null, default);
        if (!OperatingSystem.IsLinux() || privatePath == "secret.bin")
        { Assert.False(result.Success); Assert.Equal(head, (await repo.Git("rev-parse", "HEAD")).Trim()); }
        else
        { Assert.True(result.Success, result.Message); await Do(tools, repo, new(RepositoryToolAction.BisectReset)); }
        Assert.Equal("private ignored data", await File.ReadAllTextAsync(Path.Combine(repo.Root, privatePath)));
    }

    [Fact]
    public async Task Lfs_install_and_patterns_are_repository_local()
    {
        using var repo = new TemporaryRepository(); await repo.Init(); var tools = Services().Tools;
        await Do(tools, repo, new(RepositoryToolAction.LfsInstall));
        await Do(tools, repo, new(RepositoryToolAction.LfsTrack) { Patterns = ["*.大型檔"] });
        Assert.Contains("filter=lfs", await File.ReadAllTextAsync(Path.Combine(repo.Root, ".gitattributes")));
        Assert.Equal("git-lfs filter-process", (await repo.Git("config", "--local", "filter.lfs.process")).Trim());
        Assert.Contains("git-lfs/", (await tools.GetStateAsync(repo.Root, default)).LfsVersion);
        await Do(tools, repo, new(RepositoryToolAction.LfsUntrack) { Patterns = ["*.大型檔"] });
        Assert.DoesNotContain("filter=lfs", await File.ReadAllTextAsync(Path.Combine(repo.Root, ".gitattributes")));
    }

    [Fact]
    public async Task Lfs_fetch_and_pull_materialize_real_local_remote_objects()
    {
        using var source = new TemporaryRepository(); await source.Init();
        using var remote = new TemporaryRepository(); await remote.Git("init", "--bare", "-b", "main");
        using var clone = new TemporaryRepository();
        var tools = Services().Tools;
        await Do(tools, source, new(RepositoryToolAction.LfsInstall));
        await Do(tools, source, new(RepositoryToolAction.LfsTrack) { Patterns = ["large.bin"] });
        var bytes = Enumerable.Range(0, 100_000).Select(i => (byte)(i % 251)).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(source.Root, "large.bin"), bytes);
        await source.Git("add", "."); await source.Git("commit", "-m", "LFS object");
        await source.Git("remote", "add", "origin", remote.Root); await source.Git("push", "origin", "main");
        await clone.Git("clone", "--no-checkout", remote.Root, ".");
        await clone.Git("config", "core.hooksPath", Path.Combine(clone.Root, ".git", "hooks"));
        await Do(tools, clone, new(RepositoryToolAction.LfsInstall));
        var runner = new GitProcessRunner();
        var reset = await runner.RunAsync(clone.Root, ["reset", "--hard", "HEAD"], default, new()
        { IsReadOnly = false, Trusted = true, Environment = new Dictionary<string, string?> { ["GIT_LFS_SKIP_SMUDGE"] = "1" } });
        Assert.Equal(0, reset.ExitCode);
        Assert.Contains("version https://git-lfs.github.com/spec/v1", await File.ReadAllTextAsync(Path.Combine(clone.Root, "large.bin")));
        await Do(tools, clone, new(RepositoryToolAction.LfsFetch) { Name = "origin" });
        await Do(tools, clone, new(RepositoryToolAction.LfsPull) { Name = "origin" });
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(clone.Root, "large.bin")));
    }

    [Fact]
    public async Task Tools_wait_for_same_common_directory_lease()
    {
        using var repo = new TemporaryRepository(); await repo.Init(); var (tools, workspace, _) = Services();
        await using var lease = await workspace.AcquireTerminalLeaseAsync(repo.Root, default);
        using var cancel = new CancellationTokenSource();
        var pending = tools.ExecuteAsync(repo.Root, new(RepositoryToolAction.WorktreePrune), null, cancel.Token);
        cancel.Cancel(); var outcome = await pending;
        Assert.True(outcome.Cancelled); Assert.False(outcome.Success);
    }

    private static async Task Do(RepositoryToolsService tools, TemporaryRepository repo, RepositoryToolOperation op)
    { var outcome = await tools.ExecuteAsync(repo.Root, op, null, default); Assert.True(outcome.Success, outcome.Message); }
}
