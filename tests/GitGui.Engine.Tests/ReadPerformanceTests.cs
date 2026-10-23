using GitGui.Core;
using GitGui.Engine.Cli;
using GitGui.Engine.Lib;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class ReadPerformanceTests
{
    [Fact]
    public async Task Large_commit_messages_are_returned_without_retention_in_the_details_cache()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var message = new string('x', 270_000);
        var messageFile = Path.Combine(repo.Root, ".git", "large-message");
        await File.WriteAllTextAsync(messageFile, message);
        var oid = (await repo.Git("commit-tree", "HEAD^{tree}", "-F", messageFile)).Trim();
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction());
        var first = await service.GetCommitAsync(repo.Root, oid, default);
        var second = await service.GetCommitAsync(repo.Root, oid, default);
        Assert.Equal(message, first.Message); Assert.Equal(message, second.Message);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task Working_diff_of_a_tracked_edit_needs_one_process_and_untracked_content_still_loads()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction());
        await File.AppendAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "tracked edit\n");
        var commands = new List<string>(); runner.CommandExecuted += (_, e) => commands.Add(e.Command);
        Assert.Contains("+tracked edit", await service.GetDiffAsync(repo.Root, new("tracked.txt"), default));
        Assert.Single(commands);
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "new.txt"), "untracked content\n");
        Assert.Contains("+untracked content", await service.GetDiffAsync(repo.Root, new("new.txt"), default));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "empty.txt"), "");
        Assert.Contains("new file mode", await service.GetDiffAsync(repo.Root, new("empty.txt"), default));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".git", "info", "exclude"), "private.txt\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "private.txt"), "ignored content\n");
        Assert.Contains("+ignored content", await service.GetDiffAsync(repo.Root, new("private.txt"), default));
        await repo.Git("add", "tracked.txt");
        Assert.Empty(await service.GetDiffAsync(repo.Root, new("tracked.txt"), default));
    }

    [Fact]
    public async Task Cancelled_history_page_does_not_poison_the_next_read()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var probe = new CancelOnceHistory(); var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction(), probe);
        using var cancellation = new CancellationTokenSource();
        var opening = service.OpenAsync(repo.Root, new(), cancellation.Token);
        await probe.Started.Task; cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        Assert.Single((await service.OpenAsync(repo.Root, new(), default)).Commits);
    }
    [Fact]
    public async Task Status_refresh_reads_external_changes_without_reloading_history_or_configuration()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        IWorkspaceService service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction(), new LibHistoryReader());
        await service.OpenAsync(repo.Root, new(), default);
        var commands = new List<string>(); runner.CommandExecuted += (_, e) => commands.Add(e.Command);
        await File.AppendAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "external edit\n");
        var updated = await service.RefreshStatusAsync(repo.Root, default);
        Assert.Contains(updated.Status.Files, f => f.Path == "tracked.txt" && f.WorktreeStatus == "M");
        Assert.Single(commands);
        Assert.DoesNotContain(commands, c => c.Contains("--version", StringComparison.Ordinal) || c.Contains("for-each-ref", StringComparison.Ordinal) || c.Contains("--max-count", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unchanged_visible_tips_reuse_history_but_external_commit_invalidates_it()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var history = new CountedHistory(); var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction(), history);
        var first = await service.OpenAsync(repo.Root, new(), default);
        var again = await service.OpenAsync(repo.Root, new(), default);
        Assert.Equal(first.Commits.Select(c => c.Hash), again.Commits.Select(c => c.Hash));
        Assert.Equal(1, history.Reads);
        await repo.Git("update-ref", "refs/gitgui/snapshots/internal-test", first.Status.Head!);
        Assert.Equal(first.HistoryGeneration, (await service.OpenAsync(repo.Root, new(), default)).HistoryGeneration);
        Assert.Equal(1, history.Reads);
        await File.AppendAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "external commit\n");
        await repo.Git("commit", "-am", "external commit");
        var changed = await service.OpenAsync(repo.Root, new(), default);
        Assert.Equal(2, changed.Commits.Count); Assert.Equal("external commit", changed.Commits[0].Subject);
        Assert.Equal(2, history.Reads);
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task Pages_append_against_stable_tips_and_reset_when_a_visible_branch_changes(string format)
    {
        using var repo = new TemporaryRepository(); await repo.Init(format);
        for (var i = 0; i < 5; i++) await repo.Git("commit", "--allow-empty", "-m", "Commit " + i);
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        IWorkspaceService service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction(), new LibHistoryReader());
        var first = await service.OpenAsync(repo.Root, new(Take: 2), default);
        Assert.Equal(2, first.Commits.Count); Assert.True(first.HasMoreHistory);
        var commands = new List<string>(); runner.CommandExecuted += (_, e) => commands.Add(e.Command);
        var next = await service.GetHistoryPageAsync(repo.Root, new(Skip: 2, Take: 3), first.HistoryGeneration, default);
        Assert.False(next.Reset); Assert.Equal(first.HistoryGeneration, next.Generation); Assert.Equal(5, next.NextSkip);
        Assert.Equal(5, first.Commits.Concat(next.Commits).Select(c => c.Hash).Distinct().Count());
        Assert.DoesNotContain(commands, c => c.Contains("porcelain=v2", StringComparison.Ordinal));
        var newTip = (await repo.Git("commit-tree", "HEAD^{tree}", "-p", "HEAD", "-m", "new branch tip")).Trim();
        await repo.Git("update-ref", "refs/heads/new-visible-tip", newTip);
        var changed = await service.GetHistoryPageAsync(repo.Root, new(Skip: 5, Take: 3), first.HistoryGeneration, default);
        Assert.True(changed.Reset); Assert.NotEqual(first.HistoryGeneration, changed.Generation);
        Assert.Contains(changed.Commits, c => c.Hash == newTip); Assert.Equal(7, changed.Commits.Count);
    }

    [Fact]
    public async Task Cached_clean_presentation_never_authorizes_a_write_after_external_edits()
    {
        using var repo = new TemporaryRepository(); await repo.Init(); await repo.Git("branch", "other");
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog(); var history = new CountedHistory();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction(), history);
        Assert.Empty((await service.OpenAsync(repo.Root, new(), default)).Status.Files);
        await File.AppendAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "external private edits\n");
        var result = await service.ExecuteAsync(repo.Root, new(GitAction.SwitchBranch) { Name = "other" }, null, default);
        Assert.False(result.Success); Assert.Null(result.Snapshot); Assert.Equal(1, history.Reads);
        Assert.Equal("main", (await repo.Git("branch", "--show-current")).Trim());
        Assert.Contains("external private edits", await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
    }

    [Fact]
    public async Task Commit_diff_and_preview_do_not_repeat_the_changed_file_listing()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction());
        var commands = new List<string>(); runner.CommandExecuted += (_, e) => commands.Add(e.Command);
        var oid = (await repo.Git("rev-parse", "HEAD")).Trim();
        Assert.Single((await service.GetCommitAsync(repo.Root, oid, default)).Files);
        Assert.Contains("base", await service.GetDiffAsync(repo.Root, new("tracked.txt", Commit: oid), default));
        Assert.Equal(5, (await service.GetPreviewAsync(repo.Root, new("tracked.txt", Commit: oid), default)).AfterSize);
        Assert.Single(commands, c => c.Contains("--name-status", StringComparison.Ordinal));
    }

    private sealed class CountedHistory : IGraphHistoryReader
    {
        public int Reads { get; private set; }
        public Task<IReadOnlyList<CommitInfo>> GetGraphAsync(string root, int skip, int take, CancellationToken ct)
        { Reads++; return new LibHistoryReader().GetGraphAsync(root, skip, take, ct); }
    }
    private sealed class CancelOnceHistory : IGraphHistoryReader
    {
        private bool first = true;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<CommitInfo>> GetGraphAsync(string root, int skip, int take, CancellationToken ct)
        {
            if (first) { first = false; Started.SetResult(); await Task.Delay(Timeout.Infinite, ct); }
            using var reader = new LibHistoryReader();
            return await reader.GetGraphAsync(root, skip, take, ct);
        }
    }
}
