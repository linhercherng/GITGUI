using System.Security.Cryptography;
using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class AdvancedSnapshotTests
{
    [Theory]
    [InlineData("files")]
    [InlineData("reftable")]
    public async Task Duplicate_stash_oids_keep_individual_reflog_entries(string refFormat)
    {
        using var repo = new TemporaryRepository();
        if (!await InitWithRefs(repo, refFormat)) return;
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "stash A\n");
        await repo.Git("stash", "push", "-m", "original A");
        var a = (await repo.Git("rev-parse", "refs/stash")).Trim();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "stash B\n");
        await repo.Git("stash", "push", "-m", "middle B");
        await repo.Git("stash", "store", "-m", "duplicate A", a);
        await repo.Git("stash", "drop", "stash@{1}");
        var log = await StashLog(repo);
        var manager = new SnapshotManager(new GitProcessRunner(), new MemoryCatalog());
        var snapshot = await manager.CreateAsync(repo.Root, "duplicate stash OIDs", default);
        await repo.Git("stash", "clear");
        await repo.Git("gc", "--prune=now");
        await manager.RestoreAsync(repo.Root, snapshot, default);
        Assert.Equal(log, await StashLog(repo));
        Assert.Equal(2, (await repo.Git("stash", "list")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(a, (await repo.Git("rev-parse", "stash@{0}")).Trim());
        Assert.Equal(a, (await repo.Git("rev-parse", "stash@{1}")).Trim());
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task Full_stash_stack_metadata_untracked_payload_and_remote_configuration_survive_gc(string format)
    {
        using var repo = new TemporaryRepository();
        await repo.Init(format);
        await repo.Git("remote", "add", "origin", "https://example.invalid/original.git");
        await repo.Git("config", "--add", "remote.origin.pushurl", "https://example.invalid/push-one.git");
        await repo.Git("config", "--add", "remote.origin.pushurl", "https://example.invalid/push-two.git");
        var initial = (await repo.Git("rev-parse", "HEAD")).Trim();
        await repo.Git("update-ref", "refs/remotes/origin/main", initial);
        await repo.Git("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        for (var n = 0; n < 3; n++)
        {
            await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), $"stash {n}\r\n");
            await File.WriteAllBytesAsync(Path.Combine(repo.Root, $"untracked-{n}.bin"), [(byte)n, 0, 255, 13, 10]);
            await repo.Git("stash", "push", "--include-untracked", "-m", $"測試 stash {n}");
        }
        var log = await StashLog(repo);
        var config = await repo.Git("config", "--local", "-z", "--get-regexp", "^remote\\.");
        var manager = new SnapshotManager(new GitProcessRunner(), new MemoryCatalog());
        var snapshot = await manager.CreateAsync(repo.Root, "before stash mutation", default);
        await repo.Git("stash", "drop", "stash@{1}");
        await repo.Git("stash", "pop");
        await repo.Git("reset", "--hard");
        await repo.Git("clean", "-f");
        await repo.Git("stash", "clear");
        await repo.Git("remote", "rename", "origin", "renamed");
        await repo.Git("remote", "set-url", "renamed", "https://example.invalid/changed.git");
        await repo.Git("reflog", "expire", "--expire=now", "--all");
        await repo.Git("gc", "--prune=now");
        await manager.RestoreAsync(repo.Root, snapshot, default);
        Assert.Equal(log, await StashLog(repo));
        Assert.Equal(config, await repo.Git("config", "--local", "-z", "--get-regexp", "^remote\\."));
        Assert.Equal(initial, (await repo.Git("rev-parse", "refs/remotes/origin/main")).Trim());
        Assert.Equal("refs/remotes/origin/main", (await repo.Git("symbolic-ref", "refs/remotes/origin/HEAD")).Trim());
        Assert.Empty((await repo.Git("for-each-ref", "refs/remotes/renamed")).Trim());
        for (var n = 2; n >= 0; n--)
        {
            await repo.Git("stash", "pop");
            Assert.Equal($"stash {n}\r\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
            Assert.Equal(new byte[] { (byte)n, 0, 255, 13, 10 }, await File.ReadAllBytesAsync(Path.Combine(repo.Root, $"untracked-{n}.bin")));
            await repo.Git("reset", "--hard");
            await repo.Git("clean", "-f");
        }
    }

    [Theory]
    [InlineData("--merge", "rebase-merge", "files")]
    [InlineData("--apply", "rebase-apply", "files")]
    [InlineData("--merge", "rebase-merge", "reftable")]
    public async Task Rebase_conflict_state_and_nested_editor_helpers_restore_and_can_continue(string backend, string stateDirectory, string refFormat)
    {
        using var repo = new TemporaryRepository();
        if (!await InitWithRefs(repo, refFormat)) return;
        await repo.Git("switch", "-c", "topic");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "topic\n");
        await repo.Git("commit", "-am", "topic change");
        await repo.Git("switch", "main");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "main\n");
        await repo.Git("commit", "-am", "main change");
        await repo.Git("switch", "topic");
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.Git("rebase", backend, "main"));
        var helper = Path.Combine(repo.Root, ".git", "gitgui-rebase", "nested", "editor.sh");
        Directory.CreateDirectory(Path.GetDirectoryName(helper)!);
        await File.WriteAllTextAsync(helper, "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var state = StateFiles(repo, stateDirectory, "gitgui-rebase");
        var index = await repo.Git("ls-files", "--stage", "-z");
        var bytes = await File.ReadAllBytesAsync(Path.Combine(repo.Root, "tracked.txt"));
        var manager = new SnapshotManager(new GitProcessRunner(), new MemoryCatalog());
        var snapshot = await manager.CreateAsync(repo.Root, "paused rebase", default);
        await repo.Git("rebase", "--abort");
        await File.WriteAllTextAsync(helper, "changed helper\n");
        await repo.Git("reset", "--hard", "main");
        await repo.Git("reflog", "expire", "--expire=now", "--all");
        await repo.Git("gc", "--prune=now");
        await manager.RestoreAsync(repo.Root, snapshot, default);
        Assert.Equal(state, StateFiles(repo, stateDirectory, "gitgui-rebase"));
        Assert.Equal(index, await repo.Git("ls-files", "--stage", "-z"));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(repo.Root, "tracked.txt")));
        if (!OperatingSystem.IsWindows()) Assert.True((File.GetUnixFileMode(helper) & UnixFileMode.UserExecute) != 0);
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "resolved\n");
        await repo.Git("add", "tracked.txt");
        await repo.Git("-c", "core.editor=true", "rebase", "--continue");
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".git", stateDirectory)));
        Assert.Equal("topic", (await repo.Git("branch", "--show-current")).Trim());
    }

    [Theory]
    [InlineData("cherry-pick", "CHERRY_PICK_HEAD", "files")]
    [InlineData("revert", "REVERT_HEAD", "files")]
    [InlineData("cherry-pick", "CHERRY_PICK_HEAD", "reftable")]
    [InlineData("revert", "REVERT_HEAD", "reftable")]
    public async Task Sequencer_conflicts_restore_after_abort_and_remain_abortable(string command, string marker, string refFormat)
    {
        using var repo = new TemporaryRepository();
        if (!await InitWithRefs(repo, refFormat)) return;
        await repo.Git("switch", "-c", "topic");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "first\n");
        await repo.Git("commit", "-am", "first");
        var first = (await repo.Git("rev-parse", "HEAD")).Trim();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "second\n");
        await repo.Git("commit", "-am", "second");
        var second = (await repo.Git("rev-parse", "HEAD")).Trim();
        await repo.Git("config", "core.abbrev", "4");
        if (command == "cherry-pick")
        {
            await repo.Git("switch", "main");
            await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "different\n");
            await repo.Git("commit", "-am", "different");
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.Git(command, first, second));
        if (command == "cherry-pick") await repo.Git("branch", "-D", "topic");
        var markerTarget = await repo.Git("rev-parse", "--verify", marker);
        var state = StateFiles(repo, "sequencer");
        var index = await repo.Git("ls-files", "--stage", "-z");
        var manager = new SnapshotManager(new GitProcessRunner(), new MemoryCatalog());
        var snapshot = await manager.CreateAsync(repo.Root, "paused " + command, default);
        await repo.Git(command, "--abort");
        await repo.Git("reflog", "expire", "--expire=now", "--all");
        await repo.Git("gc", "--prune=now");
        Assert.Equal("commit", (await repo.Git("cat-file", "-t", second)).Trim());
        await manager.RestoreAsync(repo.Root, snapshot, default);
        Assert.Equal(markerTarget, await repo.Git("rev-parse", "--verify", marker));
        Assert.Equal(state, StateFiles(repo, "sequencer"));
        Assert.Equal(index, await repo.Git("ls-files", "--stage", "-z"));
        await repo.Git(command, "--abort");
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.Git("rev-parse", "--verify", marker));
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".git", "sequencer")));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(79)]
    [InlineData(314)]
    public async Task Randomized_history_stash_and_remote_mutations_restore_the_same_saved_state(int seed)
    {
        using var repo = new TemporaryRepository();
        await repo.Init();
        await repo.Git("remote", "add", "origin", "https://example.invalid/original.git");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "saved stash\n");
        await repo.Git("stash", "push", "-m", "saved stash");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "saved stage\n");
        await repo.Git("add", "tracked.txt");
        await File.AppendAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "saved worktree\r\n");
        byte[] binary = [0, 255, 42, (byte)seed];
        await File.WriteAllBytesAsync(Path.Combine(repo.Root, "saved.bin"), binary);
        var refs = await PublicRefs(repo);
        var index = await repo.Git("ls-files", "--stage", "-z");
        var status = await repo.Git("status", "--porcelain=v2", "-z");
        var stashLog = await StashLog(repo);
        var manager = new SnapshotManager(new GitProcessRunner(), new MemoryCatalog());
        var snapshot = await manager.CreateAsync(repo.Root, "seed " + seed, default);
        await repo.Git("reset", "--hard");
        await repo.Git("clean", "-f");
        var random = new Random(seed);
        for (var n = 0; n < 16; n++)
        {
            switch (random.Next(6))
            {
                case 0:
                    await File.AppendAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), $"mutation {n}\n");
                    await repo.Git("commit", "-am", "mutation " + n);
                    break;
                case 1:
                    await File.WriteAllTextAsync(Path.Combine(repo.Root, $"new-{n}.txt"), "untracked\n");
                    await repo.Git("stash", "push", "--include-untracked", "-m", "random stash " + n);
                    break;
                case 2:
                    if ((await repo.Git("stash", "list")).Length > 0) await repo.Git("stash", "drop", "stash@{0}");
                    break;
                case 3: await repo.Git("branch", "random-" + n); await repo.Git("tag", "random-tag-" + n); break;
                case 4: await repo.Git("remote", "set-url", "origin", "https://example.invalid/mutation-" + n + ".git"); break;
                case 5: await File.WriteAllBytesAsync(Path.Combine(repo.Root, $"extra-{n}.bin"), [(byte)n, 0, 255]); break;
            }
        }
        await repo.Git("reflog", "expire", "--expire=now", "--all");
        await repo.Git("gc", "--prune=now");
        await manager.RestoreAsync(repo.Root, snapshot, default);
        Assert.Equal(refs, await PublicRefs(repo));
        Assert.Equal(index, await repo.Git("ls-files", "--stage", "-z"));
        Assert.Equal(status, await repo.Git("status", "--porcelain=v2", "-z"));
        Assert.Equal(stashLog, await StashLog(repo));
        Assert.Equal("saved stage\nsaved worktree\r\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
        Assert.Equal(binary, await File.ReadAllBytesAsync(Path.Combine(repo.Root, "saved.bin")));
        Assert.Equal("https://example.invalid/original.git", (await repo.Git("remote", "get-url", "origin")).Trim());
    }

    private static Task<string> StashLog(TemporaryRepository repo) => repo.Git("log", "-g", "-z", "--date=raw",
        "--format=%H%x00%gn%x00%ge%x00%gD%x00%gs", "refs/stash", "--");
    private static Task<string> PublicRefs(TemporaryRepository repo) => repo.Git("for-each-ref", "--format=%(refname) %(objectname)",
        "refs/heads", "refs/tags", "refs/remotes", "refs/stash");
    private static async Task<bool> InitWithRefs(TemporaryRepository repo, string refFormat)
    {
        if (refFormat == "files") { await repo.Init(); return true; }
        try { await repo.Git("init", "-b", "main", "--ref-format=" + refFormat); }
        catch (InvalidOperationException ex) when (refFormat == "reftable" && ex.Message.Contains("unknown option", StringComparison.OrdinalIgnoreCase))
        {
            // Git 2.40 supports the application but predates the reftable backend.
            return false;
        }
        await repo.Git("config", "user.name", "GITGUI Test");
        await repo.Git("config", "user.email", "test@example.invalid");
        await repo.Git("config", "commit.gpgsign", "false");
        await repo.Git("config", "core.autocrlf", "false");
        await repo.Git("config", "core.hooksPath", Path.Combine(repo.Root, ".git", "hooks"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "base\n");
        await repo.Git("add", "tracked.txt");
        await repo.Git("commit", "-m", "initial");
        Assert.Equal("reftable", (await repo.Git("config", "--get", "extensions.refStorage")).Trim());
        return true;
    }
    private static string[] StateFiles(TemporaryRepository repo, params string[] names) => names.SelectMany(name =>
    {
        var directory = Path.Combine(repo.Root, ".git", name);
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories) : [];
    }).OrderBy(p => p, StringComparer.Ordinal).Select(p => Path.GetRelativePath(repo.Root, p).Replace('\\', '/') + ":" +
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
}
