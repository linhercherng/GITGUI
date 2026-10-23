using GitGui.Core;
using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

// This collection changes a Git-for-Windows test environment switch. xUnit
// runs it without any other collection so unrelated child processes cannot inherit it.
[CollectionDefinition("LegacyWindowsDeletion", DisableParallelization = true)]
public sealed class LegacyWindowsDeletionCollection;

[Collection("LegacyWindowsDeletion")]
public sealed class ReftableCleanupTests
{
    [ReftableAvailableFact]
    public async Task Legacy_windows_reports_partial_remove_and_requires_explicit_confirmed_prune()
    {
        if (!OperatingSystem.IsWindows()) return;
        var previous = Environment.GetEnvironmentVariable("GIT_TEST_LEGACY_DELETE");
        using var repo = new TemporaryRepository(); using var linked = new TemporaryRepository();
        try
        {
            // Official Git-for-Windows switch reproduces Windows Server 2022
            // mapped-reftable deletion behavior on newer Windows as well.
            Environment.SetEnvironmentVariable("GIT_TEST_LEGACY_DELETE", "1");
            await repo.Git("init", "-b", "main", "--ref-format=reftable");
            await repo.Git("config", "user.name", "GITGUI Test");
            await repo.Git("config", "user.email", "test@example.invalid");
            await repo.Git("config", "commit.gpgsign", "false");
            await repo.Git("config", "core.autocrlf", "false");
            await repo.Git("config", "core.hooksPath", Path.Combine(repo.Root, ".git", "hooks"));
            await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "base\n");
            await repo.Git("add", "tracked.txt"); await repo.Git("commit", "-m", "initial");
            var runner = new GitProcessRunner(); var catalog = new MemoryCatalog(); var prompt = new TestInteraction();
            var snapshots = new SnapshotManager(runner, catalog);
            var workspace = new WorkspaceService(runner, new TestSettings(), catalog, snapshots, prompt);
            var tools = new RepositoryToolsService(runner, workspace, snapshots, prompt);
            var add = await tools.ExecuteAsync(repo.Root, new(RepositoryToolAction.WorktreeAdd) { Path = linked.Root, Name = "retained" }, null, default);
            Assert.True(add.Success, add.Message);
            var admin = (await repo.Git("-C", linked.Root, "rev-parse", "--absolute-git-dir")).Trim();
            var head = await repo.Git("rev-parse", "HEAD");
            var refs = await repo.Git("for-each-ref", "--format=%(refname) %(objectname)");
            var remove = await tools.ExecuteAsync(repo.Root, new(RepositoryToolAction.WorktreeRemove) { Path = linked.Root }, null, default);
            Assert.False(remove.Success); Assert.Equal(GitErrorKind.WorktreeCleanupIncomplete, remove.Error);
            Assert.Contains("Prune stale records", remove.Message);
            Assert.False(Directory.Exists(linked.Root)); Assert.True(Directory.Exists(admin));
            Assert.Equal(head, await repo.Git("rev-parse", "HEAD"));
            Assert.Equal(refs, await repo.Git("for-each-ref", "--format=%(refname) %(objectname)"));
            prompt.Accept = false;
            var cancelled = await tools.ExecuteAsync(repo.Root, new(RepositoryToolAction.WorktreePrune), null, default);
            Assert.True(cancelled.Cancelled); Assert.True(Directory.Exists(admin));
            prompt.Accept = true;
            var prune = await tools.ExecuteAsync(repo.Root, new(RepositoryToolAction.WorktreePrune), null, default);
            Assert.True(prune.Success, prune.Message); Assert.False(Directory.Exists(admin));
            Assert.Equal(head, await repo.Git("rev-parse", "HEAD"));
            Assert.Equal(refs, await repo.Git("for-each-ref", "--format=%(refname) %(objectname)"));
        }
        finally { Environment.SetEnvironmentVariable("GIT_TEST_LEGACY_DELETE", previous); }
    }
}
