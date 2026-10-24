using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public class SnapshotTests
{
    [Fact]
    public async Task Snapshot_manifest_bytes_match_the_legacy_reflection_contract()
    {
        using var repo = new TemporaryRepository();
        await repo.Init();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "中文 空白.txt"), "內容\r\n");
        var manager = new SnapshotManager(new GitProcessRunner(), new MemoryCatalog());
        var snapshot = await manager.CreateAsync(repo.Root, "format compatibility", default);
        var manifest = await repo.Git("show", snapshot.StateRef + ":manifest");
        var contract = typeof(SnapshotManager).Assembly.GetType("GitGui.Engine.Cli.SnapshotManifest", throwOnError: true)!;
        var legacy = System.Text.Json.JsonSerializer.Deserialize(manifest, contract)!;
        Assert.Equal(manifest, System.Text.Json.JsonSerializer.Serialize(legacy, contract));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "中文 空白.txt"), "changed");
        await manager.RestoreAsync(repo.Root, snapshot, default);
        Assert.Equal("內容\r\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "中文 空白.txt")));
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task Restores_refs_index_raw_worktree_untracked_binary_and_survives_gc(string format)
    {
        using var repo = new TemporaryRepository();
        await repo.Init(format);
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "staged\r\n");
        await repo.Git("add", "--", "tracked.txt");
        await File.AppendAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "unstaged\r\n");
        byte[] binary = [0, 255, 128, 10, 13, 42];
        await File.WriteAllBytesAsync(Path.Combine(repo.Root, "中文 file.bin"), binary);
        var head = await repo.Git("rev-parse", "HEAD");
        var index = await repo.Git("ls-files", "--stage", "-z");
        var manager = new SnapshotManager(new GitProcessRunner(), new MemoryCatalog());
        var snapshot = await manager.CreateAsync(repo.Root, "before discard", default);
        await repo.Git("reset", "--hard");
        await repo.Git("clean", "-f");
        await repo.Git("branch", "new-branch");
        await repo.Git("tag", "new-tag");
        await repo.Git("gc", "--prune=now");
        await manager.RestoreAsync(repo.Root, snapshot, default);
        Assert.Equal(head, await repo.Git("rev-parse", "HEAD"));
        Assert.Equal(index, await repo.Git("ls-files", "--stage", "-z"));
        Assert.Equal("staged\r\nunstaged\r\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
        Assert.Equal(binary, await File.ReadAllBytesAsync(Path.Combine(repo.Root, "中文 file.bin")));
        Assert.Empty((await repo.Git("for-each-ref", "refs/heads/new-branch", "refs/tags/new-tag")).Trim());
    }

    [Fact]
    public async Task Restores_unborn_and_detached_HEAD()
    {
        using var repo = new TemporaryRepository();
        await repo.Init(commit: false);
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "new.txt"), "new\n");
        await repo.Git("add", "--", "new.txt");
        var manager = new SnapshotManager(new GitProcessRunner(), new MemoryCatalog());
        var unborn = await manager.CreateAsync(repo.Root, "unborn", default);
        await repo.Git("commit", "-m", "first");
        await manager.RestoreAsync(repo.Root, unborn, default);
        Assert.Equal("refs/heads/main", (await repo.Git("symbolic-ref", "HEAD")).Trim());
        Assert.Empty((await repo.Git("for-each-ref", "refs/heads")).Trim());
        Assert.Contains("new.txt", await repo.Git("diff", "--cached", "--name-only"));
        await repo.Git("commit", "-m", "first again");
        await repo.Git("switch", "--detach");
        var detachedHead = await repo.Git("rev-parse", "HEAD");
        var detached = await manager.CreateAsync(repo.Root, "detached", default);
        await repo.Git("switch", "main");
        await manager.RestoreAsync(repo.Root, detached, default);
        Assert.Equal(detachedHead, await repo.Git("rev-parse", "HEAD"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.Git("symbolic-ref", "-q", "HEAD"));
    }

    [Fact]
    public async Task Restores_three_stage_conflicted_index_and_merge_markers()
    {
        using var repo = new TemporaryRepository();
        await repo.Init();
        await repo.Git("switch", "-c", "topic");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "theirs\n");
        await repo.Git("commit", "-am", "topic change");
        await repo.Git("switch", "main");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "ours\n");
        await repo.Git("commit", "-am", "main change");
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.Git("merge", "topic"));
        var stages = await repo.Git("ls-files", "--stage", "-z");
        var contents = await File.ReadAllBytesAsync(Path.Combine(repo.Root, "tracked.txt"));
        var marker = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".git", "MERGE_HEAD"));
        var manager = new SnapshotManager(new GitProcessRunner(), new MemoryCatalog());
        var conflict = await manager.CreateAsync(repo.Root, "conflict", default);
        await repo.Git("merge", "--abort");
        await manager.RestoreAsync(repo.Root, conflict, default);
        Assert.Equal(stages, await repo.Git("ls-files", "--stage", "-z"));
        Assert.Equal(contents, await File.ReadAllBytesAsync(Path.Combine(repo.Root, "tracked.txt")));
        Assert.Equal(marker, await File.ReadAllTextAsync(Path.Combine(repo.Root, ".git", "MERGE_HEAD")));
    }
}
