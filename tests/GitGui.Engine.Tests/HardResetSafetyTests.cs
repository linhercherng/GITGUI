using GitGui.Core;
using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class HardResetSafetyTests
{
    private static WorkspaceService Service(MemoryCatalog catalog)
    {
        var runner = new GitProcessRunner();
        return new(runner, new TestSettings(), catalog, new SnapshotManager(runner, catalog), new TestInteraction());
    }

    private static async Task<string> TargetThenIgnoredHead(TemporaryRepository repo, string targetPath)
    {
        await repo.Init();
        var target = Path.Combine(repo.Root, targetPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "tracked target content\n");
        // A tracked path can still match .gitignore. Keep the rule in both revisions to exercise
        // recovery of a directory which remains ignored after reset removes its tracked child.
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".gitignore"), "payload\n");
        await repo.Git("add", "--force", "--", ".gitignore", targetPath); await repo.Git("commit", "-m", "target tracks payload");
        var revision = (await repo.Git("rev-parse", "HEAD")).Trim();
        await repo.Git("rm", "-r", "--", "payload");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".gitignore"), "payload\n");
        await repo.Git("add", "--", ".gitignore"); await repo.Git("commit", "-m", "ignore private payload");
        return revision;
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("payload/child.txt")]
    public async Task Hard_reset_undo_preserves_ignored_binary_file_and_file_blocking_target_ancestor(string targetPath)
    {
        using var repo = new TemporaryRepository();
        var revision = await TargetThenIgnoredHead(repo, targetPath);
        var beforeHead = (await repo.Git("rev-parse", "HEAD")).Trim();
        var beforeIndex = await repo.Git("ls-files", "--stage", "-z");
        byte[] privateBytes = [0, 255, 128, 13, 10, 19, 42, 0, 243];
        await File.WriteAllBytesAsync(Path.Combine(repo.Root, "payload"), privateBytes);
        Assert.Equal("payload", (await repo.Git("check-ignore", "--", "payload")).Trim());
        Assert.Empty((await repo.Git("status", "--porcelain")).Trim());

        var catalog = new MemoryCatalog(); var service = Service(catalog);

        var reset = await service.ExecuteAsync(repo.Root, new(GitAction.Reset) { Revision = revision, ResetMode = ResetMode.Hard }, null, default);
        Assert.True(reset.Success, reset.Message); Assert.NotNull(reset.Snapshot);
        Assert.Equal(revision, (await repo.Git("rev-parse", "HEAD")).Trim());
        Assert.Equal("tracked target content\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, targetPath.Replace('/', Path.DirectorySeparatorChar))));

        // The ignored bytes must be rooted by the snapshot, not retained accidentally as loose objects.
        await repo.Git("gc", "--prune=now");
        var undo = await service.ExecuteAsync(repo.Root, new(GitAction.RestoreSnapshot) { SnapshotId = reset.Snapshot!.Id }, null, default);
        Assert.True(undo.Success, undo.Message); Assert.NotNull(undo.Snapshot);
        Assert.Equal(beforeHead, (await repo.Git("rev-parse", "HEAD")).Trim());
        Assert.Equal(beforeIndex, await repo.Git("ls-files", "--stage", "-z"));
        Assert.Equal(privateBytes, await File.ReadAllBytesAsync(Path.Combine(repo.Root, "payload")));
        Assert.Equal("payload", (await repo.Git("check-ignore", "--", "payload")).Trim());
        Assert.Empty((await repo.Git("status", "--porcelain")).Trim());

        var redo = await service.ExecuteAsync(repo.Root, new(GitAction.RestoreSnapshot) { SnapshotId = undo.Snapshot!.Id }, null, default);
        Assert.True(redo.Success, redo.Message); Assert.NotNull(redo.Snapshot);
        Assert.Equal(revision, (await repo.Git("rev-parse", "HEAD")).Trim());
        Assert.Equal("tracked target content\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, targetPath.Replace('/', Path.DirectorySeparatorChar))));
        await repo.Git("gc", "--prune=now");
        var undoAgain = await service.ExecuteAsync(repo.Root, new(GitAction.RestoreSnapshot) { SnapshotId = redo.Snapshot!.Id }, null, default);
        Assert.True(undoAgain.Success, undoAgain.Message);
        Assert.Equal(beforeHead, (await repo.Git("rev-parse", "HEAD")).Trim());
        Assert.Equal(beforeIndex, await repo.Git("ls-files", "--stage", "-z"));
        Assert.Equal(privateBytes, await File.ReadAllBytesAsync(Path.Combine(repo.Root, "payload")));
        Assert.Empty((await repo.Git("status", "--porcelain")).Trim());
    }

    [Fact]
    public async Task Hard_reset_refuses_ignored_directory_blocking_target_file_without_mutating_user_data()
    {
        using var repo = new TemporaryRepository(); var revision = await TargetThenIgnoredHead(repo, "payload");
        var beforeHead = await repo.Git("rev-parse", "HEAD"); var beforeIndex = await repo.Git("ls-files", "--stage", "-z");
        var privateDirectory = Path.Combine(repo.Root, "payload"); Directory.CreateDirectory(Path.Combine(privateDirectory, "nested"));
        byte[] privateBytes = [255, 0, 222, 173, 13, 10, 190, 239];
        await File.WriteAllBytesAsync(Path.Combine(privateDirectory, "nested", "private.bin"), privateBytes);
        var catalog = new MemoryCatalog(); var service = Service(catalog);

        var reset = await service.ExecuteAsync(repo.Root, new(GitAction.Reset) { Revision = revision, ResetMode = ResetMode.Hard }, null, default);
        Assert.False(reset.Success); Assert.Null(reset.Snapshot);
        Assert.Empty(await catalog.ListAsync(repo.Root, default));
        Assert.Equal(beforeHead, await repo.Git("rev-parse", "HEAD"));
        Assert.Equal(beforeIndex, await repo.Git("ls-files", "--stage", "-z"));
        Assert.Equal(privateBytes, await File.ReadAllBytesAsync(Path.Combine(privateDirectory, "nested", "private.bin")));
        Assert.Equal("payload\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, ".gitignore")));
        Assert.Empty((await repo.Git("status", "--porcelain")).Trim());
    }

    [Theory]
    [InlineData(ResetMode.Mixed)]
    [InlineData(ResetMode.Hard)]
    public async Task Reset_refuses_target_tree_with_gitlink_before_changing_HEAD_index_or_files(ResetMode mode)
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var linkedCommit = (await repo.Git("rev-parse", "HEAD")).Trim();
        // A gitlink needs only a commit OID, so this fixture requires no clone, network or .gitmodules helper.
        await repo.Git("update-index", "--add", "--cacheinfo", "160000," + linkedCommit + ",module");
        await repo.Git("commit", "-m", "target includes gitlink");
        var target = (await repo.Git("rev-parse", "HEAD")).Trim();
        await repo.Git("rm", "--cached", "--", "module"); await repo.Git("commit", "-m", "current has no gitlink");
        var beforeHead = await repo.Git("rev-parse", "HEAD"); var beforeIndex = await repo.Git("ls-files", "--stage", "-z");
        byte[] original = [0, 255, 128, 13, 10, 42];
        await File.WriteAllBytesAsync(Path.Combine(repo.Root, "private.bin"), original);
        var catalog = new MemoryCatalog();
        var outcome = await Service(catalog).ExecuteAsync(repo.Root, new(GitAction.Reset) { Revision = target, ResetMode = mode }, null, default);
        Assert.False(outcome.Success); Assert.Null(outcome.Snapshot); Assert.Empty(await catalog.ListAsync(repo.Root, default));
        Assert.Equal(beforeHead, await repo.Git("rev-parse", "HEAD"));
        Assert.Equal(beforeIndex, await repo.Git("ls-files", "--stage", "-z"));
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(repo.Root, "private.bin")));
        Assert.False(Directory.Exists(Path.Combine(repo.Root, "module")));
    }

    [Fact]
    public async Task Undo_refuses_unknown_ignored_descendant_before_mutating_target_HEAD_index_or_files()
    {
        using var repo = new TemporaryRepository(); var revision = await TargetThenIgnoredHead(repo, "payload/child.txt");
        byte[] originalIgnoredFile = [0, 255, 128, 13, 10, 77];
        await File.WriteAllBytesAsync(Path.Combine(repo.Root, "payload"), originalIgnoredFile);
        var catalog = new MemoryCatalog(); var service = Service(catalog);
        var reset = await service.ExecuteAsync(repo.Root, new(GitAction.Reset) { Revision = revision, ResetMode = ResetMode.Hard }, null, default);
        Assert.True(reset.Success, reset.Message); Assert.NotNull(reset.Snapshot);
        byte[] newlyCreatedPrivateFile = [255, 0, 123, 13, 10, 222, 42];
        var privatePath = Path.Combine(repo.Root, "payload", "private.bin");
        await File.WriteAllBytesAsync(privatePath, newlyCreatedPrivateFile);
        Assert.Equal("payload/private.bin", (await repo.Git("check-ignore", "--", "payload/private.bin")).Trim());
        var beforeHead = await repo.Git("rev-parse", "HEAD"); var beforeIndex = await repo.Git("ls-files", "--stage", "-z");
        var childPath = Path.Combine(repo.Root, "payload", "child.txt"); var beforeChild = await File.ReadAllBytesAsync(childPath);

        var refused = await service.ExecuteAsync(repo.Root, new(GitAction.RestoreSnapshot) { SnapshotId = reset.Snapshot!.Id }, null, default);
        Assert.False(refused.Success);
        Assert.Contains("ignored file blocks snapshot restoration", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(beforeHead, await repo.Git("rev-parse", "HEAD"));
        Assert.Equal(beforeIndex, await repo.Git("ls-files", "--stage", "-z"));
        Assert.Equal(beforeChild, await File.ReadAllBytesAsync(childPath));
        Assert.Equal(newlyCreatedPrivateFile, await File.ReadAllBytesAsync(privatePath));
        Assert.Equal("payload\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, ".gitignore")));
        Assert.Empty((await repo.Git("status", "--porcelain")).Trim());
    }
}
