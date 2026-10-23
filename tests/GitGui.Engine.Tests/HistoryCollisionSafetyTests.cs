using GitGui.Core;
using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class HistoryCollisionSafetyTests
{
    private static WorkspaceService Service()
    {
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        return new(runner, new TestSettings(), catalog, new SnapshotManager(runner, catalog), new TestInteraction());
    }

    private static async Task<string> Prepare(TemporaryRepository repo, GitAction action)
    {
        await repo.Init();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".gitignore"), "payload\n");
        await repo.Git("add", "--", ".gitignore"); await repo.Git("commit", "-m", "ignore private payload");
        if (action is GitAction.RebaseInteractive or GitAction.CherryPick) await repo.Git("branch", "feature");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "payload"), "tracked payload from history\n");
        await repo.Git("add", "--force", "--", "payload"); await repo.Git("commit", "-m", "main adds tracked payload");
        var revision = (await repo.Git("rev-parse", "HEAD")).Trim();
        if (action == GitAction.Revert)
        {
            // Reverting a deletion adds the path from the target's parent tree.
            await repo.Git("rm", "--", "payload"); await repo.Git("commit", "-m", "delete tracked payload");
            revision = (await repo.Git("rev-parse", "HEAD")).Trim();
        }
        else await repo.Git("switch", "feature");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "independent.txt"), "feature work\n");
        await repo.Git("add", "--", "independent.txt"); await repo.Git("commit", "-m", "independent feature work");
        return revision;
    }

    [Theory]
    [InlineData(GitAction.RebaseInteractive)]
    [InlineData(GitAction.CherryPick)]
    [InlineData(GitAction.Revert)]
    public async Task History_rewrite_undo_preserves_previously_ignored_binary_collision(GitAction action)
    {
        using var repo = new TemporaryRepository(); var revision = await Prepare(repo, action);
        byte[] privateBytes = [0, 255, 128, 13, 10, 97, 42, 0, 238];
        await File.WriteAllBytesAsync(Path.Combine(repo.Root, "payload"), privateBytes);
        Assert.Equal("payload", (await repo.Git("check-ignore", "--", "payload")).Trim());
        Assert.Empty((await repo.Git("status", "--porcelain")).Trim());
        var beforeHead = (await repo.Git("rev-parse", "HEAD")).Trim();
        var beforeIndex = await repo.Git("ls-files", "--stage", "-z");
        var beforeRefs = await repo.Git("for-each-ref", "--format=%(refname) %(objectname)", "refs/heads", "refs/tags");
        var service = Service();
        var operation = action == GitAction.RebaseInteractive
            ? new GitOperation(action) { Revision = "main", RebasePlan = await service.GetRebasePlanAsync(repo.Root, "main", default) }
            : new GitOperation(action) { Revision = revision };

        var result = await service.ExecuteAsync(repo.Root, operation, null, default);
        Assert.True(result.Success, result.Message); Assert.NotNull(result.Snapshot);
        var rewrittenHead = (await repo.Git("rev-parse", "HEAD")).Trim();
        Assert.NotEqual(beforeHead, rewrittenHead);
        Assert.Equal("tracked payload from history\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "payload")));
        await repo.Git("gc", "--prune=now");

        var undo = await service.ExecuteAsync(repo.Root, new(GitAction.RestoreSnapshot) { SnapshotId = result.Snapshot!.Id }, null, default);
        Assert.True(undo.Success, undo.Message); Assert.NotNull(undo.Snapshot);
        Assert.Equal(beforeHead, (await repo.Git("rev-parse", "HEAD")).Trim());
        Assert.Equal(beforeIndex, await repo.Git("ls-files", "--stage", "-z"));
        Assert.Equal(beforeRefs, await repo.Git("for-each-ref", "--format=%(refname) %(objectname)", "refs/heads", "refs/tags"));
        Assert.Equal(privateBytes, await File.ReadAllBytesAsync(Path.Combine(repo.Root, "payload")));
        Assert.Equal("payload", (await repo.Git("check-ignore", "--", "payload")).Trim());
        Assert.Empty((await repo.Git("status", "--porcelain")).Trim());

        if (action == GitAction.RebaseInteractive)
        {
            var redo = await service.ExecuteAsync(repo.Root, new(GitAction.RestoreSnapshot) { SnapshotId = undo.Snapshot!.Id }, null, default);
            Assert.True(redo.Success, redo.Message);
            Assert.Equal(rewrittenHead, (await repo.Git("rev-parse", "HEAD")).Trim());
            Assert.Equal("tracked payload from history\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "payload")));
            Assert.Empty((await repo.Git("status", "--porcelain")).Trim());
        }
    }
}
