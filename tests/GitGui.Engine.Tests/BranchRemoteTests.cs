using GitGui.Core;
using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class BranchRemoteTests
{
    [Fact]
    public async Task Branch_create_switch_rename_delete_and_snapshot_restore_are_complete()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var service = Service();
        await Execute(service, repo.Root, new(GitAction.CreateBranch) { Name = "topic-work" });
        await Execute(service, repo.Root, new(GitAction.SwitchBranch) { Name = "topic-work" });
        Assert.Equal("topic-work", (await repo.Git("branch", "--show-current")).Trim());
        await Execute(service, repo.Root, new(GitAction.RenameBranch) { Name = "topic-work", NewName = "topic-ready" });
        Assert.Equal("topic-ready", (await repo.Git("branch", "--show-current")).Trim());
        await Execute(service, repo.Root, new(GitAction.SwitchBranch) { Name = "main" });
        var deleted = await Execute(service, repo.Root, new(GitAction.DeleteBranch) { Name = "topic-ready" });
        Assert.NotNull(deleted.Snapshot);
        Assert.Empty((await repo.Git("for-each-ref", "refs/heads/topic-ready")).Trim());
        await Execute(service, repo.Root, new(GitAction.RestoreSnapshot) { SnapshotId = deleted.Snapshot.Id });
        Assert.Contains("refs/heads/topic-ready", await repo.Git("for-each-ref", "refs/heads/topic-ready"));
        Assert.Equal("main", (await repo.Git("branch", "--show-current")).Trim());
    }

    [Fact]
    public async Task Switching_branches_preserves_ignored_file_colliding_with_target_tree()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        await repo.Git("switch", "-c", "topic");
        await CommitFile(repo, "protected.bin", "tracked on topic\n");
        await repo.Git("switch", "main");
        await CommitFile(repo, ".gitignore", "protected.bin\n");
        const string ignored = "private ignored bytes\r\n";
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "protected.bin"), ignored);
        var outcome = await Service().ExecuteAsync(repo.Root, new(GitAction.SwitchBranch) { Name = "topic" }, null, default);
        Assert.False(outcome.Success);
        Assert.Equal(ignored, await File.ReadAllTextAsync(Path.Combine(repo.Root, "protected.bin")));
        Assert.Equal("main", (await repo.Git("branch", "--show-current")).Trim());
    }

    [Fact]
    public async Task Fast_forward_merge_advances_head_and_exposes_recovery_snapshot()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        await repo.Git("switch", "-c", "topic"); await CommitFile(repo, "topic.txt", "topic\n");
        var topic = await repo.Git("rev-parse", "HEAD"); await repo.Git("switch", "main");
        var outcome = await Execute(Service(), repo.Root, new(GitAction.Merge) { Name = "topic" });
        Assert.NotNull(outcome.Snapshot); Assert.Equal(topic, await repo.Git("rev-parse", "HEAD"));
        Assert.False(File.Exists(Path.Combine(repo.Root, ".git", "MERGE_HEAD")));
    }

    [Theory]
    [InlineData(GitAction.ResolveOurs, "ours\n")]
    [InlineData(GitAction.ResolveTheirs, "theirs\n")]
    public async Task Conflict_resolution_merge_commit_undo_and_redo_preserve_parents_and_contents(GitAction choice, string expected)
    {
        using var repo = new TemporaryRepository(); await repo.Init(); await Diverge(repo);
        var service = Service();
        var before = await repo.Git("rev-parse", "HEAD");
        var merge = await service.ExecuteAsync(repo.Root, new(GitAction.Merge) { Name = "topic" }, null, default);
        Assert.False(merge.Success); Assert.Equal(GitErrorKind.Conflict, merge.Error); Assert.NotNull(merge.Snapshot);
        var state = await service.OpenAsync(repo.Root, new(), default);
        Assert.Equal("merge", state.InProgress.Kind); Assert.True(state.InProgress.HasConflicts);
        await Execute(service, repo.Root, new(choice) { Paths = ["tracked.txt"] });
        Assert.Equal(expected, await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
        await Execute(service, repo.Root, new(GitAction.MarkResolved) { Paths = ["tracked.txt"] });
        Assert.False((await service.OpenAsync(repo.Root, new(), default)).InProgress.HasConflicts);
        await Execute(service, repo.Root, new(GitAction.Commit) { Message = "Merge topic after review" });
        var merged = await repo.Git("rev-parse", "HEAD");
        Assert.Equal(3, (await repo.Git("rev-list", "--parents", "-n", "1", "HEAD")).Trim().Split(' ').Length);
        var undo = await Execute(service, repo.Root, new(GitAction.RestoreSnapshot) { SnapshotId = merge.Snapshot.Id });
        Assert.Equal(before, await repo.Git("rev-parse", "HEAD"));
        Assert.Equal("ours\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
        Assert.False(File.Exists(Path.Combine(repo.Root, ".git", "MERGE_HEAD")));
        await Execute(service, repo.Root, new(GitAction.RestoreSnapshot) { SnapshotId = undo.Snapshot!.Id });
        Assert.Equal(merged, await repo.Git("rev-parse", "HEAD"));
        Assert.Equal(expected, await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
    }

    [Fact]
    public async Task Aborting_conflicted_merge_is_itself_reversible()
    {
        using var repo = new TemporaryRepository(); await repo.Init(); await Diverge(repo);
        var service = Service();
        var merge = await service.ExecuteAsync(repo.Root, new(GitAction.Merge) { Name = "topic" }, null, default);
        Assert.Equal(GitErrorKind.Conflict, merge.Error);
        var conflictedIndex = await repo.Git("ls-files", "--stage");
        var conflictedContents = await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt"));
        var abort = await Execute(service, repo.Root, new(GitAction.AbortMerge));
        Assert.Equal("ours\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
        Assert.False(File.Exists(Path.Combine(repo.Root, ".git", "MERGE_HEAD")));
        await Execute(service, repo.Root, new(GitAction.RestoreSnapshot) { SnapshotId = abort.Snapshot!.Id });
        Assert.Equal(conflictedIndex, await repo.Git("ls-files", "--stage"));
        Assert.Equal(conflictedContents, await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
        Assert.True(File.Exists(Path.Combine(repo.Root, ".git", "MERGE_HEAD")));
        await Execute(service, repo.Root, new(GitAction.AbortMerge));
    }

    [Fact]
    public async Task Local_bare_remote_clone_fetch_pull_and_push_use_real_git_transport()
    {
        var service = Service(); using var fixture = new LocalRemote(); await fixture.Initialize(service);
        Assert.Equal(await fixture.Seed.Git("rev-parse", "HEAD"), await fixture.Client.Git("rev-parse", "HEAD"));
        await CommitFile(fixture.Client, "from-client.txt", "client\n");
        await Execute(service, fixture.Client.Root, new(GitAction.Push) { Remote = "origin" });
        Assert.Equal(await fixture.Client.Git("rev-parse", "HEAD"), await fixture.Bare.Git("rev-parse", "refs/heads/main"));
        await Execute(service, fixture.Seed.Root, new(GitAction.Fetch) { Remote = "origin" });
        Assert.Equal(await fixture.Client.Git("rev-parse", "HEAD"), await fixture.Seed.Git("rev-parse", "refs/remotes/origin/main"));
        await Execute(service, fixture.Seed.Root, new(GitAction.Pull) { Remote = "origin" });
        Assert.Equal(await fixture.Client.Git("rev-parse", "HEAD"), await fixture.Seed.Git("rev-parse", "HEAD"));
        await CommitFile(fixture.Seed, "from-seed.txt", "seed\n");
        await Execute(service, fixture.Seed.Root, new(GitAction.Push) { Remote = "origin" });
        await Execute(service, fixture.Client.Root, new(GitAction.Fetch) { Remote = "origin" });
        await Execute(service, fixture.Client.Root, new(GitAction.Pull) { Remote = "origin" });
        Assert.Equal(await fixture.Seed.Git("rev-parse", "HEAD"), await fixture.Client.Git("rev-parse", "HEAD"));
    }

    [Fact]
    public async Task Protected_force_push_requires_exact_branch_and_stale_lease_never_overwrites_remote()
    {
        var runner = new GitProcessRunner(); var interaction = new TestInteraction(); var service = Service(interaction, runner);
        using var fixture = new LocalRemote(); await fixture.Initialize(service);
        await CommitFile(fixture.Seed, "upstream.txt", "upstream\n"); await fixture.Seed.Git("push", "origin", "main");
        var upstream = await fixture.Bare.Git("rev-parse", "refs/heads/main");
        await CommitFile(fixture.Client, "local.txt", "local\n");
        var wrong = new WrongBranchInteraction();
        var rejected = await Service(wrong).ExecuteAsync(fixture.Client.Root, new(GitAction.Push) { Remote = "origin", Force = true }, null, default);
        Assert.True(rejected.Cancelled); Assert.Equal("main", Assert.Single(wrong.Confirmations).RequiredText);
        Assert.Equal(upstream, await fixture.Bare.Git("rev-parse", "refs/heads/main"));
        var commands = new List<CommandEntry>(); runner.CommandExecuted += (_, command) => commands.Add(command);
        var stale = await service.ExecuteAsync(fixture.Client.Root, new(GitAction.Push) { Remote = "origin", Force = true }, null, default);
        Assert.False(stale.Success); Assert.Equal(GitErrorKind.StaleLease, stale.Error);
        var confirmation = Assert.Single(interaction.Confirmations, c => c.Risk == RiskLevel.RemoteIrreversible);
        Assert.Equal("main", confirmation.RequiredText);
        var push = Assert.Single(commands, c => c.Command.Contains(" push ", StringComparison.Ordinal));
        Assert.Contains("--force-with-lease", push.Command); Assert.Contains("--force-if-includes", push.Command); Assert.DoesNotContain(" --force ", push.Command);
        Assert.Equal(upstream, await fixture.Bare.Git("rev-parse", "refs/heads/main"));
    }

    [Fact]
    public async Task Pull_honors_ff_only_when_local_and_remote_histories_diverge()
    {
        var service = Service(); using var fixture = new LocalRemote(); await fixture.Initialize(service);
        await CommitFile(fixture.Seed, "remote-change.txt", "remote\n"); await fixture.Seed.Git("push", "origin", "main");
        await CommitFile(fixture.Client, "local-change.txt", "local\n");
        await fixture.Client.Git("config", "pull.ff", "only");
        var head = await fixture.Client.Git("rev-parse", "HEAD");
        var outcome = await service.ExecuteAsync(fixture.Client.Root, new(GitAction.Pull) { Remote = "origin" }, null, default);
        Assert.False(outcome.Success); Assert.Contains("fast-forward", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(head, await fixture.Client.Git("rev-parse", "HEAD"));
        Assert.False(File.Exists(Path.Combine(fixture.Client.Root, ".git", "MERGE_HEAD")));
    }

    [Fact]
    public async Task Commit_hook_rejection_is_visible_and_no_verify_is_explicit_per_attempt()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var hook = Path.Combine(repo.Root, ".git", "hooks", "pre-commit");
        await File.WriteAllTextAsync(hook, "#!/bin/sh\necho 'pre-commit hook rejected' >&2\nexit 1\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "updated\n"); await repo.Git("add", "--", "tracked.txt");
        var service = Service(); var head = await repo.Git("rev-parse", "HEAD");
        var blocked = await service.ExecuteAsync(repo.Root, new(GitAction.Commit) { Message = "hook should block" }, null, default);
        Assert.False(blocked.Success); Assert.Equal(GitErrorKind.HookRejected, blocked.Error); Assert.Contains("pre-commit hook rejected", blocked.Message);
        Assert.Equal(head, await repo.Git("rev-parse", "HEAD"));
        await Execute(service, repo.Root, new(GitAction.Commit) { Message = "explicit one-time bypass", SkipHooks = true });
        Assert.NotEqual(head, await repo.Git("rev-parse", "HEAD"));
        await File.AppendAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "again\n"); await repo.Git("add", "--", "tracked.txt");
        var blockedAgain = await service.ExecuteAsync(repo.Root, new(GitAction.Commit) { Message = "hook enabled again" }, null, default);
        Assert.False(blockedAgain.Success); Assert.Equal(GitErrorKind.HookRejected, blockedAgain.Error);
    }

    private static WorkspaceService Service(IWorkspaceInteraction? interaction = null, GitProcessRunner? runner = null)
    {
        runner ??= new(); var catalog = new MemoryCatalog();
        return new(runner, new TestSettings(), catalog, new SnapshotManager(runner, catalog), interaction ?? new TestInteraction());
    }
    private static async Task<OperationOutcome> Execute(WorkspaceService service, string root, GitOperation operation)
    {
        var result = await service.ExecuteAsync(root, operation, null, default); Assert.True(result.Success, result.Message); return result;
    }
    private static async Task CommitFile(TemporaryRepository repo, string path, string content)
    {
        await File.WriteAllTextAsync(Path.Combine(repo.Root, path), content); await repo.Git("add", "--", path); await repo.Git("commit", "-m", path);
    }
    private static async Task Diverge(TemporaryRepository repo)
    {
        await repo.Git("switch", "-c", "topic"); await CommitFile(repo, "tracked.txt", "theirs\n");
        await repo.Git("switch", "main"); await CommitFile(repo, "tracked.txt", "ours\n");
    }
    private sealed class WrongBranchInteraction : IWorkspaceInteraction
    {
        public List<Confirmation> Confirmations { get; } = [];
        public Task<ConfirmationAnswer> ConfirmAsync(Confirmation confirmation, CancellationToken ct) { Confirmations.Add(confirmation); return Task.FromResult(new ConfirmationAnswer(true, "MAIN")); }
        public Task<string?> CredentialAsync(string prompt, CancellationToken ct) => Task.FromResult<string?>(null);
    }
    private sealed class LocalRemote : IDisposable
    {
        public TemporaryRepository Bare { get; } = new();
        public TemporaryRepository Seed { get; } = new();
        public TemporaryRepository Client { get; } = new();
        public async Task Initialize(WorkspaceService service)
        {
            await Bare.Git("init", "--bare", "--initial-branch=main"); await Seed.Init();
            await Seed.Git("remote", "add", "origin", Bare.Root); await Seed.Git("push", "--set-upstream", "origin", "main");
            await Execute(service, Client.Root, new(GitAction.Clone) { Url = Bare.Root });
            await Client.Git("config", "user.name", "GITGUI Test"); await Client.Git("config", "user.email", "test@example.invalid");
            await Client.Git("config", "commit.gpgsign", "false"); await Client.Git("config", "core.autocrlf", "false");
            await Client.Git("config", "core.hooksPath", Path.Combine(Client.Root, ".git", "hooks"));
            await Client.Git("reset", "--hard", "HEAD");
        }
        public void Dispose() { Client.Dispose(); Seed.Dispose(); Bare.Dispose(); }
    }
}
