using GitGui.Core;
using GitGui.Engine.Cli;
using GitGui.Engine.Lib;
using LibGit2Sharp;
using System.Diagnostics;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class AdvancedWorkflowTests
{
    private static WorkspaceService Service()
    {
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        return new(runner, new TestSettings(), catalog, new SnapshotManager(runner, catalog), new TestInteraction(), fileHistory: new LibHistoryReader());
    }
    private static async Task Success(WorkspaceService service, TemporaryRepository repo, GitOperation operation)
    {
        var outcome = await service.ExecuteAsync(repo.Root, operation, null, default);
        Assert.True(outcome.Success, outcome.Message);
    }
    private static async Task<string> Commit(TemporaryRepository repo, string file, string text, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(repo.Root, file), text);
        await repo.Git("add", "--", file); await repo.Git("commit", "-m", message);
        return (await repo.Git("rev-parse", "HEAD")).Trim();
    }
    private static async Task<string> Diverge(TemporaryRepository repo)
    {
        await repo.Init(); await repo.Git("switch", "-c", "topic");
        var topic = await Commit(repo, "tracked.txt", "topic\n", "topic conflict");
        await Commit(repo, "independent.txt", "independent\n", "safe followup");
        await repo.Git("switch", "main"); await Commit(repo, "tracked.txt", "main\n", "main conflicting change");
        await repo.Git("switch", "topic"); return topic;
    }

    [Theory]
    [InlineData(GitAction.SkipOperation)]
    [InlineData(GitAction.AbortOperation)]
    public async Task Rebase_conflict_can_skip_or_abort_after_service_restart(GitAction action)
    {
        using var repo = new TemporaryRepository(); await Diverge(repo); var service = Service();
        var before = (await repo.Git("rev-parse", "HEAD")).Trim();
        var plan = await service.GetRebasePlanAsync(repo.Root, "main", default);
        var failed = await service.ExecuteAsync(repo.Root, new(GitAction.RebaseInteractive) { Revision = "main", RebasePlan = plan }, null, default);
        Assert.False(failed.Success); Assert.NotNull(failed.Snapshot);
        var state = await service.OpenAsync(repo.Root, new(Take: 1), default);
        Assert.Equal("rebase", state.InProgress.Kind); Assert.True(state.InProgress.HasConflicts);
        service = Service();
        var premature = await service.ExecuteAsync(repo.Root, new(GitAction.ContinueOperation), null, default);
        Assert.False(premature.Success); Assert.Null(premature.Snapshot);
        await Success(service, repo, new(action));
        Assert.False((await service.OpenAsync(repo.Root, new(Take: 1), default)).InProgress.IsActive);
        Assert.Equal("topic", (await repo.Git("branch", "--show-current")).Trim());
        if (action == GitAction.AbortOperation)
        {
            Assert.Equal(before, (await repo.Git("rev-parse", "HEAD")).Trim());
            Assert.Equal("topic\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
        }
        else
        {
            Assert.Equal("main\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
            Assert.Equal("safe followup", (await repo.Git("log", "-1", "--format=%s")).Trim());
        }
    }

    [Fact]
    public async Task Reword_message_survives_conflict_resolution_and_service_restart()
    {
        using var repo = new TemporaryRepository(); await Diverge(repo); var service = Service();
        var plan = (await service.GetRebasePlanAsync(repo.Root, "main", default)).ToArray();
        plan[0] = plan[0] with { Action = RebaseStepKind.Reword, Message = "衝突後保留重新命名" };
        var failed = await service.ExecuteAsync(repo.Root, new(GitAction.RebaseInteractive) { Revision = "main", RebasePlan = plan }, null, default);
        Assert.False(failed.Success); Assert.NotNull(failed.Snapshot);
        service = Service();
        await Success(service, repo, new(GitAction.WriteConflictResult) { Paths = ["tracked.txt"], Message = "resolved\n" });
        await Success(service, repo, new(GitAction.MarkResolved) { Paths = ["tracked.txt"] });
        await Success(service, repo, new(GitAction.ContinueOperation));
        Assert.Equal("衝突後保留重新命名\nsafe followup", (await repo.Git("log", "--reverse", "--format=%s", "main..HEAD")).Trim());
        Assert.Equal("resolved\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
    }

    [Fact]
    public async Task Interactive_edit_pause_allows_staging_amending_and_continuing_from_GUI_operations()
    {
        using var repo = new TemporaryRepository(); await repo.Init(); var service = Service();
        await Commit(repo, "tracked.txt", "second\n", "second");
        var plan = await service.GetRebasePlanAsync(repo.Root, "HEAD~1", default);
        await Success(service, repo, new(GitAction.RebaseInteractive) { Revision = "HEAD~1", RebasePlan = [plan[0] with { Action = RebaseStepKind.Edit }] });
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "amended during edit\n");
        await Success(service, repo, new(GitAction.Stage) { Paths = ["tracked.txt"] });
        await Success(service, repo, new(GitAction.Commit) { Amend = true, Message = "amended second" });
        await Success(service, repo, new(GitAction.ContinueOperation));
        Assert.Equal("amended second", (await repo.Git("log", "-1", "--format=%s")).Trim());
        Assert.Equal("amended during edit\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
        Assert.False((await service.OpenAsync(repo.Root, new(Take: 1), default)).InProgress.IsActive);
    }

    [Theory]
    [InlineData(GitAction.CherryPick, GitAction.SkipOperation)]
    [InlineData(GitAction.CherryPick, GitAction.AbortOperation)]
    [InlineData(GitAction.Revert, GitAction.SkipOperation)]
    [InlineData(GitAction.Revert, GitAction.AbortOperation)]
    public async Task Cherry_pick_and_revert_conflicts_can_skip_or_abort_after_restart(GitAction operation, GitAction continuation)
    {
        using var repo = new TemporaryRepository(); await repo.Init(); string target; string original;
        if (operation == GitAction.CherryPick)
        {
            await repo.Git("switch", "-c", "topic"); target = await Commit(repo, "tracked.txt", "topic\n", "topic");
            await repo.Git("switch", "main"); await Commit(repo, "tracked.txt", "main\n", "main"); original = "main\n";
        }
        else
        {
            target = await Commit(repo, "tracked.txt", "changed\n", "changed");
            await Commit(repo, "tracked.txt", "after\n", "after"); original = "after\n";
        }
        var before = (await repo.Git("rev-parse", "HEAD")).Trim(); var service = Service();
        var failed = await service.ExecuteAsync(repo.Root, new(operation) { Revision = target }, null, default);
        Assert.False(failed.Success); Assert.NotNull(failed.Snapshot);
        Assert.Equal(operation == GitAction.CherryPick ? "cherry-pick" : "revert", (await service.OpenAsync(repo.Root, new(Take: 1), default)).InProgress.Kind);
        service = Service(); await Success(service, repo, new(continuation));
        Assert.Equal(before, (await repo.Git("rev-parse", "HEAD")).Trim());
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
        Assert.False((await service.OpenAsync(repo.Root, new(Take: 1), default)).InProgress.IsActive);
    }

    [Fact]
    public async Task Empty_HEAD_lists_advanced_sections_without_failing_the_entire_window()
    {
        using var repo = new TemporaryRepository(); await repo.Init(commit: false);
        var state = await Service().GetAdvancedStateAsync(repo.Root, default);
        Assert.Empty(state.Stashes); Assert.Empty(state.Tags); Assert.Empty(state.Reflog); Assert.Empty(state.Remotes);
    }

    [Fact]
    public async Task Merge_commit_ranges_are_rejected_before_interactive_rebase()
    {
        using var repo = new TemporaryRepository(); await repo.Init(); var upstream = (await repo.Git("rev-parse", "HEAD")).Trim();
        await repo.Git("switch", "-c", "topic"); await Commit(repo, "topic.txt", "topic\n", "topic");
        await repo.Git("switch", "main"); await Commit(repo, "main.txt", "main\n", "main");
        await repo.Git("merge", "--no-edit", "topic");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service().GetRebasePlanAsync(repo.Root, upstream, default));
        Assert.Contains("merge commits", error.Message);
    }

    [Fact]
    public async Task Terminal_lease_blocks_GUI_writes_and_cancellation_does_not_change_repository()
    {
        using var repo = new TemporaryRepository(); await repo.Init(); var service = Service();
        var lease = await service.AcquireTerminalLeaseAsync(repo.Root, default);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
        var cancelled = await service.ExecuteAsync(repo.Root, new(GitAction.CreateTag) { Name = "blocked" }, null, cancellation.Token);
        Assert.True(cancelled.Cancelled); Assert.Null(cancelled.Snapshot);
        Assert.Empty((await repo.Git("tag", "--list")).Trim());
        var pending = service.ExecuteAsync(repo.Root, new(GitAction.CreateTag) { Name = "queued" }, null, default);
        await Task.Delay(150); Assert.False(pending.IsCompleted);
        await lease.DisposeAsync();
        var completed = await pending; Assert.True(completed.Success, completed.Message);
        Assert.Equal("queued", (await repo.Git("tag", "--list")).Trim());
    }

    [Fact]
    public async Task Native_blame_hunks_use_zero_based_offsets_and_library_exposes_one_based_lines()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        await Commit(repo, "tracked.txt", "base\nsecond\n", "second line");
        using (var native = new LibGit2Sharp.Repository(repo.Root))
        {
            var hunks = native.Blame("tracked.txt").ToArray();
            Assert.Contains(hunks, h => h.ContainsLine(0)); Assert.Contains(hunks, h => h.ContainsLine(1));
            Assert.DoesNotContain(hunks, h => h.ContainsLine(2));
        }
        var result = await new LibHistoryReader().ReadAsync(repo.Root, "tracked.txt", default);
        Assert.Equal("libgit2", result.Backend); Assert.Equal(new[] { "base", "second" }, result.Blame.Select(l => l.Text));
        Assert.Equal(new[] { 1, 2 }, result.Blame.Select(l => l.LineNumber));
    }

    [Fact]
    public async Task Native_library_reads_Unicode_renamed_file_without_hiding_exceptions_behind_fallback()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        await repo.Git("mv", "tracked.txt", "中文 空白.txt"); await repo.Git("commit", "-m", "rename");
        await Commit(repo, "中文 空白.txt", "base\nsecond\n", "change");
        var result = await new LibHistoryReader().ReadAsync(repo.Root, "中文 空白.txt", default);
        Assert.Equal("libgit2", result.Backend); Assert.Equal(2, result.Blame.Count);
        Assert.Equal(3, result.History.Count); Assert.Equal("tracked.txt", result.History[^1].Path);
    }

    [Theory]
    [InlineData("sha1", "Git CLI")]
    [InlineData("sha256", "Git CLI")]
    public async Task Deleted_file_retains_history_without_requiring_HEAD_blame(string format, string backend)
    {
        using var repo = new TemporaryRepository(); await repo.Init(format);
        await repo.Git("rm", "--", "tracked.txt"); await repo.Git("commit", "-m", "delete tracked file");
        var result = await Service().GetFileHistoryAsync(repo.Root, "tracked.txt", default);
        Assert.Equal(backend, result.Backend); Assert.Empty(result.Blame);
        Assert.Equal(new[] { "delete tracked file", "initial" }, result.History.Select(h => h.Commit.Subject));
        Assert.All(result.History, h => Assert.Equal("tracked.txt", h.Path));
    }

    [Fact]
    public async Task Reflog_uses_action_actor_and_event_time_instead_of_old_commit_metadata()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var start = new ProcessStartInfo(GitRuntimeTests.FindSystemGit()) { WorkingDirectory = repo.Root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "commit", "--amend", "--no-edit", "--reset-author" }) start.ArgumentList.Add(argument);
        start.Environment["GIT_AUTHOR_DATE"] = "2001-01-01T12:00:00+00:00";
        start.Environment["GIT_COMMITTER_DATE"] = "2001-01-01T12:00:00+00:00";
        start.Environment["GIT_AUTHOR_NAME"] = "Old Commit Author"; start.Environment["GIT_COMMITTER_NAME"] = "Old Committer";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(repo.Root, "missing-global-config");
        using (var process = Process.Start(start)!)
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); await output; Assert.True(process.ExitCode == 0, await error);
        }
        var old = (await repo.Git("rev-parse", "HEAD")).Trim();
        await Commit(repo, "tracked.txt", "new\n", "new");
        var before = DateTimeOffset.UtcNow.AddSeconds(-3);
        await repo.Git("-c", "user.name=Reset Action Actor", "reset", "--hard", old);
        var entry = (await Service().GetAdvancedStateAsync(repo.Root, default)).Reflog.First();
        Assert.Equal(old, entry.Oid); Assert.Equal("Reset Action Actor", entry.Author);
        Assert.InRange(entry.CreatedAt, before, DateTimeOffset.UtcNow.AddSeconds(3));
        Assert.StartsWith("2001-01-01", (await repo.Git("show", "-s", "--format=%cI", old)).Trim());
    }

    [Theory]
    [InlineData(GitAction.CherryPick)]
    [InlineData(GitAction.Revert)]
    public async Task Reftable_in_progress_pseudoref_is_detected_and_can_abort(GitAction operation)
    {
        using var repo = new TemporaryRepository();
        try { await repo.Git("init", "-b", "main", "--ref-format=reftable"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("unknown option", StringComparison.OrdinalIgnoreCase)) { return; }
        await repo.Git("config", "user.name", "GITGUI Test"); await repo.Git("config", "user.email", "test@example.invalid");
        await repo.Git("config", "commit.gpgsign", "false"); await repo.Git("config", "core.autocrlf", "false");
        await repo.Git("config", "core.hooksPath", Path.Combine(repo.Root, ".git", "hooks"));
        await Commit(repo, "tracked.txt", "base\n", "initial"); string revision;
        if (operation == GitAction.CherryPick)
        {
            await repo.Git("switch", "-c", "topic"); revision = await Commit(repo, "tracked.txt", "topic\n", "topic");
            await repo.Git("switch", "main"); await Commit(repo, "tracked.txt", "main\n", "main");
        }
        else
        {
            revision = await Commit(repo, "tracked.txt", "changed\n", "changed");
            await Commit(repo, "tracked.txt", "after\n", "after");
        }
        var service = Service(); var before = (await repo.Git("rev-parse", "HEAD")).Trim();
        var conflict = await service.ExecuteAsync(repo.Root, new(operation) { Revision = revision }, null, default);
        Assert.False(conflict.Success); Assert.NotNull(conflict.Snapshot);
        var state = await service.OpenAsync(repo.Root, new(Take: 1), default);
        Assert.Equal(operation == GitAction.CherryPick ? "cherry-pick" : "revert", state.InProgress.Kind);
        Assert.True(state.InProgress.HasConflicts);
        await Success(service, repo, new(GitAction.AbortOperation));
        Assert.Equal(before, (await repo.Git("rev-parse", "HEAD")).Trim());
        Assert.False((await service.OpenAsync(repo.Root, new(Take: 1), default)).InProgress.IsActive);
    }
}
