using GitGui.Core;
using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class RepositoryRecoveryTests
{
    [Fact]
    public async Task Parent_gitlink_undo_and_redo_leave_child_head_index_and_dirty_files_untouched()
    {
        using var child = new TemporaryRepository(); await child.Init();
        using var parent = new TemporaryRepository(); await parent.Init();
        var manager = new SnapshotManager(new GitProcessRunner(), new MemoryCatalog());
        var beforeAdd = await manager.CreateAsync(parent.Root, "before add", default);
        await parent.Git("-c", "protocol.file.allow=always", "submodule", "add", child.Root, "module");
        await parent.Git("commit", "-am", "add module");
        var module = Path.Combine(parent.Root, "module");
        await parent.Git("-C", module, "config", "user.name", "Child Test");
        await parent.Git("-C", module, "config", "user.email", "child@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(module, "tracked.txt"), "child second\n");
        await parent.Git("-C", module, "commit", "-am", "second");
        await File.AppendAllTextAsync(Path.Combine(module, "tracked.txt"), "child staged\n");
        await parent.Git("-C", module, "add", "tracked.txt");
        await File.AppendAllTextAsync(Path.Combine(module, "tracked.txt"), "child unstaged\r\n");
        byte[] secret = [255, 0, 13, 10, 42];
        await File.WriteAllBytesAsync(Path.Combine(module, "untracked.bin"), secret);
        var childHead = await parent.Git("-C", module, "rev-parse", "HEAD");
        var childIndex = await parent.Git("-C", module, "ls-files", "--stage", "-z");
        var childContent = await File.ReadAllBytesAsync(Path.Combine(module, "tracked.txt"));
        var parentIndex = await parent.Git("ls-files", "--stage", "-z");
        var afterAdd = await manager.CreateAsync(parent.Root, "after add", default);
        await parent.Git("gc", "--prune=now");
        await manager.RestoreAsync(parent.Root, beforeAdd, default);
        Assert.DoesNotContain("module", await parent.Git("ls-files", "--stage"));
        Assert.Equal(childHead, await parent.Git("-C", module, "rev-parse", "HEAD"));
        // The cloned child is now an untracked nested repository. Taking a redo
        // snapshot must recognize its boundary without traversing or deleting it.
        await manager.CreateAsync(parent.Root, "redo boundary", default);
        await manager.RestoreAsync(parent.Root, afterAdd, default);
        Assert.Equal(parentIndex, await parent.Git("ls-files", "--stage", "-z"));
        Assert.Equal(childHead, await parent.Git("-C", module, "rev-parse", "HEAD"));
        Assert.Equal(childIndex, await parent.Git("-C", module, "ls-files", "--stage", "-z"));
        Assert.Equal(childContent, await File.ReadAllBytesAsync(Path.Combine(module, "tracked.txt")));
        Assert.Equal(secret, await File.ReadAllBytesAsync(Path.Combine(module, "untracked.bin")));
        Assert.Contains("submodule.module.url", await parent.Git("config", "--local", "--get-regexp", "^submodule\\."));
        await parent.Git("config", "submodule.recurse", "true");
        var (_, workspace, _) = Services();
        var reset = await workspace.ExecuteAsync(parent.Root, new(GitAction.Reset) { Revision = "HEAD~1", ResetMode = ResetMode.Hard }, null, default);
        Assert.True(reset.Success, reset.Message);
        Assert.Equal(childHead, await parent.Git("-C", module, "rev-parse", "HEAD"));
        Assert.Equal(childIndex, await parent.Git("-C", module, "ls-files", "--stage", "-z"));
        Assert.Equal(childContent, await File.ReadAllBytesAsync(Path.Combine(module, "tracked.txt")));
        var undo = await workspace.ExecuteAsync(parent.Root, new(GitAction.RestoreSnapshot) { SnapshotId = reset.Snapshot!.Id }, null, default);
        Assert.True(undo.Success, undo.Message);
        Assert.Equal(parentIndex, await parent.Git("ls-files", "--stage", "-z"));
        Assert.Equal(childContent, await File.ReadAllBytesAsync(Path.Combine(module, "tracked.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sparse_snapshot_restores_patterns_skip_flags_and_only_saved_materialized_files(bool sparseIndex)
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        foreach (var directory in new[] { "keep", "excluded" })
        {
            Directory.CreateDirectory(Path.Combine(repo.Root, directory));
            await File.WriteAllTextAsync(Path.Combine(repo.Root, directory, "file.txt"), directory + "\n");
        }
        await repo.Git("add", "."); await repo.Git("commit", "-m", "directories");
        await repo.Git("sparse-checkout", "set", "--cone", sparseIndex ? "--sparse-index" : "--no-sparse-index", "--", "keep");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "keep", "file.txt"), "saved dirty\r\n");
        var patternsPath = (await repo.Git("rev-parse", "--path-format=absolute", "--git-path", "info/sparse-checkout")).Trim();
        var patterns = await File.ReadAllBytesAsync(patternsPath);
        var indexFlags = await repo.Git("ls-files", "-t", "-z");
        var manager = new SnapshotManager(new GitProcessRunner(), new MemoryCatalog());
        var sparse = await manager.CreateAsync(repo.Root, "sparse", default);
        await repo.Git("sparse-checkout", "disable");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "excluded", "file.txt"), "discard by restore\n");
        await manager.RestoreAsync(repo.Root, sparse, default);
        Assert.Equal("true", (await repo.Git("config", "--bool", "core.sparseCheckout")).Trim());
        Assert.Equal(patterns, await File.ReadAllBytesAsync(patternsPath));
        Assert.Equal(indexFlags, await repo.Git("ls-files", "-t", "-z"));
        Assert.False(File.Exists(Path.Combine(repo.Root, "excluded", "file.txt")));
        Assert.Equal("saved dirty\r\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "keep", "file.txt")));
        await repo.Git("sparse-checkout", "disable");
        Assert.Equal("excluded\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "excluded", "file.txt")));
    }

    [Fact]
    public async Task Submodule_tools_accept_explicit_local_bare_source_and_refuse_dirty_or_ignored_child_removal()
    {
        using var source = new TemporaryRepository(); await source.Init();
        using var bare = new TemporaryRepository(); await bare.Git("clone", "--bare", source.Root, ".");
        using var parent = new TemporaryRepository(); await parent.Init();
        var (tools, workspace, _) = Services();
        var add = await tools.ExecuteAsync(parent.Root, new(RepositoryToolAction.SubmoduleAdd) { Path = "子 模組", Url = bare.Root }, null, default);
        Assert.True(add.Success, add.Message); Assert.NotNull(add.Snapshot);
        await parent.Git("commit", "-am", "add child");
        var child = Path.Combine(parent.Root, "子 模組");
        await File.AppendAllTextAsync(Path.Combine(child, "tracked.txt"), "dirty\n");
        var stage = await workspace.ExecuteAsync(parent.Root, new(GitAction.Stage) { Paths = ["子 模組"] }, null, default);
        Assert.True(stage.Success, stage.Message);
        var deinit = new RepositoryToolOperation(RepositoryToolAction.SubmoduleDeinitialize) { Path = "子 模組" };
        Assert.False((await tools.ExecuteAsync(parent.Root, deinit, null, default)).Success);
        await parent.Git("-C", child, "restore", "tracked.txt");
        await parent.Git("-C", child, "config", "core.excludesFile", Path.Combine(parent.Root, ".git", "child-excludes"));
        await File.WriteAllTextAsync(Path.Combine(parent.Root, ".git", "child-excludes"), "private.bin\n");
        await File.WriteAllTextAsync(Path.Combine(child, "private.bin"), "private ignored data");
        Assert.False((await tools.ExecuteAsync(parent.Root, deinit, null, default)).Success);
        Assert.Equal("private ignored data", await File.ReadAllTextAsync(Path.Combine(child, "private.bin")));
        File.Delete(Path.Combine(child, "private.bin"));
        await Do(tools, parent.Root, deinit);
        Assert.False(File.Exists(Path.Combine(child, "tracked.txt")));
        await Do(tools, parent.Root, new(RepositoryToolAction.SubmoduleInitialize) { Path = "子 模組" });
        await Do(tools, parent.Root, new(RepositoryToolAction.SubmoduleSync) { Path = "子 模組" });
        await Do(tools, parent.Root, new(RepositoryToolAction.SubmoduleUpdate) { Path = "子 模組" });
        Assert.Equal("base\n", (await File.ReadAllTextAsync(Path.Combine(child, "tracked.txt"))).Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sparse_undo_keeps_sibling_worktree_valid_and_retains_only_the_storage_upgrade()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        foreach (var name in new[] { "one", "two" })
        {
            Directory.CreateDirectory(Path.Combine(repo.Root, name));
            await File.WriteAllTextAsync(Path.Combine(repo.Root, name, "file.txt"), name + "\n");
        }
        await repo.Git("add", "."); await repo.Git("commit", "-m", "directories");
        var sibling = repo.Root + "-sibling";
        await repo.Git("worktree", "add", "-b", "sibling", sibling);
        try
        {
            var commonDirectory = await repo.Git("rev-parse", "--path-format=absolute", "--git-common-dir");
            var siblingHead = await repo.Git("-C", sibling, "rev-parse", "HEAD");
            Assert.Equal(commonDirectory, await repo.Git("-C", sibling, "rev-parse", "--path-format=absolute", "--git-common-dir"));
            var (tools, workspace, _) = Services();
            var change = await tools.ExecuteAsync(repo.Root, new(RepositoryToolAction.SparseSet) { Patterns = ["one"] }, null, default);
            Assert.True(change.Success, change.Message);
            var undo = await workspace.ExecuteAsync(repo.Root, new(GitAction.RestoreSnapshot) { SnapshotId = change.Snapshot!.Id }, null, default);
            Assert.True(undo.Success, undo.Message);
            Assert.Equal("true", (await repo.Git("config", "--bool", "extensions.worktreeConfig")).Trim());
            Assert.DoesNotContain("S ", await repo.Git("ls-files", "-t"));
            Assert.Equal("two\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "two", "file.txt")));
            Assert.Equal("two\n", await File.ReadAllTextAsync(Path.Combine(sibling, "two", "file.txt")));
            Assert.Empty((await repo.Git("-C", sibling, "status", "--porcelain")).Trim());
            Assert.Equal(commonDirectory, await repo.Git("-C", sibling, "rev-parse", "--path-format=absolute", "--git-common-dir"));
            Assert.Equal(siblingHead, await repo.Git("-C", sibling, "rev-parse", "HEAD"));
        }
        finally { await repo.Git("worktree", "remove", "--force", sibling); }
    }

    [Fact]
    public async Task Mac_standard_tmp_source_alias_works_without_permitting_arbitrary_destination_symlinks()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var source = new TemporaryRepository(); await source.Init();
        using var parent = new TemporaryRepository(); await parent.Init();
        var alias = Path.Combine("/tmp", "gitgui-system-alias-" + Guid.NewGuid().ToString("N"));
        var destinationLink = Path.Combine(parent.Root, "jump");
        try
        {
            await source.Git("clone", source.Root, alias);
            Assert.StartsWith("/private/tmp/", (await source.Git("-C", alias, "rev-parse", "--show-toplevel")).Trim());
            var tools = Services().Tools;
            var add = await tools.ExecuteAsync(parent.Root, new(RepositoryToolAction.SubmoduleAdd) { Path = "module", Url = alias }, null, default);
            Assert.True(add.Success, add.Message); Assert.NotNull(add.Snapshot);
            Assert.Equal((await source.Git("rev-parse", "HEAD")).Trim(), (await parent.Git("-C", "module", "rev-parse", "HEAD")).Trim());
            Directory.CreateSymbolicLink(destinationLink, alias);
            var unsafeAdd = await tools.ExecuteAsync(parent.Root, new(RepositoryToolAction.SubmoduleAdd) { Path = "jump/escape", Url = alias }, null, default);
            Assert.False(unsafeAdd.Success); Assert.Null(unsafeAdd.Snapshot);
            Assert.False(Directory.Exists(Path.Combine(alias, "escape")));
            var unsafeSource = await tools.ExecuteAsync(parent.Root, new(RepositoryToolAction.SubmoduleAdd) { Path = "othermodule", Url = destinationLink }, null, default);
            Assert.False(unsafeSource.Success); Assert.Null(unsafeSource.Snapshot);
        }
        finally
        {
            if (new DirectoryInfo(destinationLink).LinkTarget is not null) Directory.Delete(destinationLink);
            if (Directory.Exists(alias)) Directory.Delete(alias, recursive: true);
        }
    }

    [Fact]
    public async Task Sparse_noncone_patterns_round_trip_and_unsafe_submodule_inputs_do_not_mutate()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        Directory.CreateDirectory(Path.Combine(repo.Root, "folder"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "folder", "data.txt"), "data\n");
        await repo.Git("add", "."); await repo.Git("commit", "-m", "folder");
        var (tools, workspace, _) = Services();
        foreach (var invalid in new[] { "../outside", ".git/modules/escape", ".git\\escape" })
            Assert.False((await tools.ExecuteAsync(repo.Root, new(RepositoryToolAction.SubmoduleAdd) { Path = invalid, Url = "ext::bad" }, null, default)).Success);
        var sparse = await tools.ExecuteAsync(repo.Root, new(RepositoryToolAction.SparseSet) { Cone = false, Patterns = ["/folder/", "!/folder/private*"] }, null, default);
        Assert.True(sparse.Success, sparse.Message);
        Assert.Equal("false", (await repo.Git("config", "--bool", "core.sparseCheckoutCone")).Trim());
        var disabled = await tools.ExecuteAsync(repo.Root, new(RepositoryToolAction.SparseDisable), null, default);
        Assert.True(disabled.Success, disabled.Message);
        var undo = await workspace.ExecuteAsync(repo.Root, new(GitAction.RestoreSnapshot) { SnapshotId = disabled.Snapshot!.Id }, null, default);
        Assert.True(undo.Success, undo.Message);
        Assert.Contains("!/folder/private*", (await tools.GetStateAsync(repo.Root, default)).SparsePatterns);
    }

    private static (RepositoryToolsService Tools, WorkspaceService Workspace, SnapshotManager Snapshots) Services()
    {
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog(); var prompt = new TestInteraction();
        var manager = new SnapshotManager(runner, catalog);
        var workspace = new WorkspaceService(runner, new TestSettings(), catalog, manager, prompt);
        return (new(runner, workspace, manager, prompt), workspace, manager);
    }

    [Theory]
    [InlineData(GitAction.Reset)]
    [InlineData(GitAction.SwitchBranch)]
    [InlineData(GitAction.Merge)]
    public async Task File_to_gitlink_parent_operation_is_refused_before_snapshot_or_worktree_mutation(GitAction action)
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "module"), "parent file\n");
        await repo.Git("add", "module"); await repo.Git("commit", "-m", "parent file");
        var fileCommit = (await repo.Git("rev-parse", "HEAD")).Trim();
        await repo.Git("rm", "module");
        await repo.Git("update-index", "--add", "--cacheinfo", "160000," + fileCommit + ",module");
        await repo.Git("commit", "-m", "gitlink");
        var gitlinkCommit = (await repo.Git("rev-parse", "HEAD")).Trim();
        await repo.Git("switch", "--detach", fileCommit);
        var (_, workspace, _) = Services();
        var outcome = await workspace.ExecuteAsync(repo.Root, new(action) { Name = gitlinkCommit, Revision = gitlinkCommit, ResetMode = ResetMode.Mixed }, null, default);
        Assert.False(outcome.Success); Assert.Null(outcome.Snapshot);
        Assert.Equal("parent file\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "module")));
        Assert.Equal(fileCommit, (await repo.Git("rev-parse", "HEAD")).Trim());
    }

    [Theory]
    [InlineData(GitAction.Reset)]
    [InlineData(GitAction.SwitchBranch)]
    [InlineData(GitAction.Merge)]
    public async Task Gitlink_to_file_parent_operation_leaves_a_dirty_child_untouched(GitAction action)
    {
        using var source = new TemporaryRepository(); await source.Init();
        using var repo = new TemporaryRepository(); await repo.Init();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "module"), "parent file\n");
        await repo.Git("add", "module"); await repo.Git("commit", "-m", "parent file");
        var fileCommit = (await repo.Git("rev-parse", "HEAD")).Trim();
        await repo.Git("rm", "module");
        await repo.Git("update-index", "--add", "--cacheinfo", "160000," + (await source.Git("rev-parse", "HEAD")).Trim() + ",module");
        await repo.Git("commit", "-m", "gitlink");
        await repo.Git("clone", source.Root, "module");
        var childFile = Path.Combine(repo.Root, "module", "tracked.txt");
        await File.WriteAllTextAsync(childFile, "child private bytes\r\n");
        await repo.Git("config", "submodule.recurse", "true");
        var beforeHead = await repo.Git("rev-parse", "HEAD");
        var result = await Services().Workspace.ExecuteAsync(repo.Root, new(action) { Name = fileCommit, Revision = fileCommit, ResetMode = ResetMode.Hard }, null, default);
        Assert.False(result.Success); Assert.Null(result.Snapshot);
        Assert.Equal(beforeHead, await repo.Git("rev-parse", "HEAD"));
        Assert.Equal("child private bytes\r\n", await File.ReadAllTextAsync(childFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Submodule_update_refuses_an_ignored_child_file_target_collision(bool caseVariation)
    {
        if (caseVariation && OperatingSystem.IsLinux()) return;
        using var source = new TemporaryRepository(); await source.Init();
        using var parent = new TemporaryRepository(); await parent.Init();
        await parent.Git("-c", "protocol.file.allow=always", "submodule", "add", source.Root, "module");
        var child = Path.Combine(parent.Root, "module");
        await parent.Git("-C", child, "config", "user.name", "Child");
        await parent.Git("-C", child, "config", "user.email", "child@example.invalid");
        var old = (await parent.Git("-C", child, "rev-parse", "HEAD")).Trim();
        var targetName = caseVariation ? "PRIVATE.BIN" : "private.bin";
        await File.WriteAllTextAsync(Path.Combine(child, targetName), "tracked target\n");
        await parent.Git("-C", child, "add", targetName); await parent.Git("-C", child, "commit", "-m", "target");
        var target = (await parent.Git("-C", child, "rev-parse", "HEAD")).Trim();
        await parent.Git("-C", child, "reset", "--hard", old);
        var childGit = (await parent.Git("-C", child, "rev-parse", "--absolute-git-dir")).Trim();
        await File.WriteAllTextAsync(Path.Combine(childGit, "info", "exclude"), "private.bin\n");
        await File.WriteAllTextAsync(Path.Combine(child, "private.bin"), "private ignored bytes\n");
        await parent.Git("update-index", "--cacheinfo", "160000," + target + ",module");
        var result = await Services().Tools.ExecuteAsync(parent.Root, new(RepositoryToolAction.SubmoduleUpdate) { Path = "module" }, null, default);
        Assert.False(result.Success); Assert.Null(result.Snapshot);
        Assert.Equal(old, (await parent.Git("-C", child, "rev-parse", "HEAD")).Trim());
        Assert.Equal("private ignored bytes\n", await File.ReadAllTextAsync(Path.Combine(child, "private.bin")));
    }

    [Fact]
    public async Task Sparse_directory_collision_preserves_ignored_data_before_snapshot()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        File.Delete(Path.Combine(repo.Root, "tracked.txt"));
        Directory.CreateDirectory(Path.Combine(repo.Root, "tracked.txt"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt", "private.bin"), "preserve\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".git", "info", "exclude"), "tracked.txt/\n");
        var result = await Services().Tools.ExecuteAsync(repo.Root, new(RepositoryToolAction.SparseSet) { Patterns = ["folder"] }, null, default);
        Assert.False(result.Success); Assert.Null(result.Snapshot);
        Assert.Equal("preserve\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt", "private.bin")));
    }

    [Fact]
    public async Task Submodule_update_fetches_a_missing_target_from_the_validated_local_source()
    {
        using var source = new TemporaryRepository(); await source.Init();
        using var parent = new TemporaryRepository(); await parent.Init();
        await parent.Git("-c", "protocol.file.allow=always", "submodule", "add", source.Root, "module");
        await parent.Git("commit", "-am", "module");
        await File.WriteAllTextAsync(Path.Combine(source.Root, "tracked.txt"), "new upstream target\n");
        await source.Git("commit", "-am", "new target");
        var target = (await source.Git("rev-parse", "HEAD")).Trim();
        await parent.Git("update-index", "--cacheinfo", "160000," + target + ",module");
        var result = await Services().Tools.ExecuteAsync(parent.Root, new(RepositoryToolAction.SubmoduleUpdate) { Path = "module" }, null, default);
        Assert.True(result.Success, result.Message); Assert.NotNull(result.Snapshot);
        Assert.Equal(target, (await parent.Git("-C", "module", "rev-parse", "HEAD")).Trim());
        Assert.Equal("new upstream target\n", (await File.ReadAllTextAsync(Path.Combine(parent.Root, "module", "tracked.txt"))).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(source.Root, (await parent.Git("config", "--get", "submodule.module.url")).Trim());
        Assert.DoesNotContain("protocol.file.allow=always", await parent.Git("config", "--local", "--list"));
    }

    [Theory]
    [InlineData(RepositoryToolAction.SubmoduleUpdate, "--assume-unchanged")]
    [InlineData(RepositoryToolAction.SubmoduleDeinitialize, "--assume-unchanged")]
    [InlineData(RepositoryToolAction.SubmoduleUpdate, "--skip-worktree")]
    [InlineData(RepositoryToolAction.SubmoduleDeinitialize, "--skip-worktree")]
    public async Task Child_index_flags_cannot_hide_data_from_update_or_deinit(RepositoryToolAction action, string flag)
    {
        using var source = new TemporaryRepository(); await source.Init();
        using var parent = new TemporaryRepository(); await parent.Init();
        await parent.Git("-c", "protocol.file.allow=always", "submodule", "add", source.Root, "module");
        await parent.Git("commit", "-am", "module");
        var file = Path.Combine(parent.Root, "module", "tracked.txt");
        await parent.Git("-C", "module", "update-index", flag, "tracked.txt");
        await File.WriteAllTextAsync(file, "private hidden changes\r\n");
        Assert.Empty((await parent.Git("-C", "module", "status", "--porcelain")).Trim());
        var result = await Services().Tools.ExecuteAsync(parent.Root, new(action) { Path = "module" }, null, default);
        Assert.False(result.Success); Assert.Null(result.Snapshot);
        Assert.Contains("hide private changes", result.Message);
        Assert.Equal("private hidden changes\r\n", await File.ReadAllTextAsync(file));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Child_update_refuses_nested_repository_to_file_transition_even_without_recursion(bool recursive)
    {
        using var nested = new TemporaryRepository(); await nested.Init();
        using var source = new TemporaryRepository(); await source.Init();
        var nestedHead = (await nested.Git("rev-parse", "HEAD")).Trim();
        await source.Git("update-index", "--add", "--cacheinfo", "160000," + nestedHead + ",nested");
        await source.Git("commit", "-m", "nested child");
        using var parent = new TemporaryRepository(); await parent.Init();
        await parent.Git("-c", "protocol.file.allow=always", "submodule", "add", source.Root, "module");
        await parent.Git("commit", "-am", "module");
        await parent.Git("-C", "module", "clone", nested.Root, "nested");
        var privateFile = Path.Combine(parent.Root, "module", "nested", "private.bin");
        await File.WriteAllTextAsync(privateFile, "nested private bytes\r\n");
        await source.Git("rm", "--cached", "nested");
        Directory.CreateDirectory(Path.Combine(source.Root, "nested"));
        await File.WriteAllTextAsync(Path.Combine(source.Root, "nested", "private.bin"), "overwriting upstream data\n");
        await source.Git("add", "nested/private.bin"); await source.Git("commit", "-m", "replace nested gitlink");
        var target = (await source.Git("rev-parse", "HEAD")).Trim();
        await parent.Git("-C", "module", "fetch", source.Root, target);
        var childHead = await parent.Git("-C", "module", "rev-parse", "HEAD");
        await parent.Git("update-index", "--cacheinfo", "160000," + target + ",module");
        var result = await Services().Tools.ExecuteAsync(parent.Root, new(RepositoryToolAction.SubmoduleUpdate) { Path = "module", Recursive = recursive }, null, default);
        Assert.False(result.Success); Assert.Null(result.Snapshot);
        Assert.Contains("type transition", result.Message);
        Assert.Equal(childHead, await parent.Git("-C", "module", "rev-parse", "HEAD"));
        Assert.Equal(nestedHead, (await parent.Git("-C", "module/nested", "rev-parse", "HEAD")).Trim());
        Assert.Equal("nested private bytes\r\n", await File.ReadAllTextAsync(privateFile));
    }
    private static async Task Do(RepositoryToolsService tools, string root, RepositoryToolOperation op)
    { var result = await tools.ExecuteAsync(root, op, null, default); Assert.True(result.Success, result.Message); }
}
