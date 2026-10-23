using GitGui.Core;
using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public class RecoveryWorkflowTests
{
    [Fact]
    public async Task Discard_is_reversible_and_redo_preserves_a_colliding_ignored_file()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "keep tracked\r\n");
        await File.WriteAllBytesAsync(Path.Combine(repo.Root, "new.bin"), [0, 255, 1]);
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction());
        var discard = await Execute(new(GitAction.Discard) { Paths = ["tracked.txt", "new.bin"] });
        Assert.Equal("base\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
        Assert.False(File.Exists(Path.Combine(repo.Root, "new.bin")));
        // A file can become ignored after a snapshot. Undo must save that collision for redo.
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".git", "info", "exclude"), "new.bin\nprivate.txt\n");
        await File.WriteAllBytesAsync(Path.Combine(repo.Root, "new.bin"), [8, 9]);
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "private.txt"), "untouched ignored data");
        var undo = await Execute(new(GitAction.RestoreSnapshot) { SnapshotId = discard.Snapshot!.Id });
        Assert.Equal("keep tracked\r\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
        Assert.Equal(new byte[] { 0, 255, 1 }, await File.ReadAllBytesAsync(Path.Combine(repo.Root, "new.bin")));
        await Execute(new(GitAction.RestoreSnapshot) { SnapshotId = undo.Snapshot!.Id });
        Assert.Equal(new byte[] { 8, 9 }, await File.ReadAllBytesAsync(Path.Combine(repo.Root, "new.bin")));
        Assert.Equal("untouched ignored data", await File.ReadAllTextAsync(Path.Combine(repo.Root, "private.txt")));
        async Task<OperationOutcome> Execute(GitOperation op)
        {
            var result = await service.ExecuteAsync(repo.Root, op, null, default);
            Assert.True(result.Success, result.Message); return result;
        }
    }

    [Fact]
    public async Task Repository_queue_serializes_writes_and_a_cancelled_waiter_does_not_write()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var interaction = new PausedConfirmation(); var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), interaction);
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "changed\n");
        using var cancellation = new CancellationTokenSource();
        var first = service.ExecuteAsync(repo.Root, new(GitAction.Discard) { Paths = ["tracked.txt"] }, null, default);
        Task<OperationOutcome>? second = null;
        try
        {
            // Confirmation follows real Git probes; allow slow Windows hosts under parallel I/O load.
            await interaction.Entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
            var waiting = new QueueProgress();
            second = service.ExecuteAsync(repo.Root, new(GitAction.CreateBranch) { Name = "queued" }, waiting, cancellation.Token);
            await waiting.Entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
            cancellation.Cancel();
            var cancelled = await second;
            Assert.True(cancelled.Cancelled);
            Assert.DoesNotContain("queued", await repo.Git("for-each-ref", "refs/heads"));
            interaction.Answer.SetResult(new(true));
            Assert.True((await first).Success);
            Assert.Equal("base\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
            var third = await service.ExecuteAsync(repo.Root, new(GitAction.CreateBranch) { Name = "after" }, null, default);
            Assert.True(third.Success, third.Message);
        }
        finally
        {
            cancellation.Cancel();
            interaction.Answer.TrySetResult(new(false));
            if (second is not null) await second;
            // A timeout/assertion must not leave Git using a directory while Dispose deletes it.
            await first;
        }
    }

    [Fact]
    public async Task Conflict_editor_preserves_executable_mode_and_undo_recovers_unmerged_index()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        await repo.Git("update-index", "--chmod=+x", "tracked.txt"); await repo.Git("commit", "-m", "executable");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(repo.Root, "tracked.txt"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        await repo.Git("switch", "-c", "topic");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "theirs\n"); await repo.Git("commit", "-am", "theirs");
        await repo.Git("switch", "main");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "ours\n"); await repo.Git("commit", "-am", "ours");
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.Git("merge", "topic"));
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction());
        var content = await service.GetConflictAsync(repo.Root, "tracked.txt", default);
        Assert.Equal("base\n", content.Base); Assert.Equal("ours\n", content.Ours); Assert.Equal("theirs\n", content.Theirs);
        Assert.False(content.IsBinary);
        var original = await repo.Git("ls-files", "--stage");
        var saved = await service.ExecuteAsync(repo.Root, new(GitAction.WriteConflictResult) { Paths = ["tracked.txt"], Message = "ours and theirs\n" }, null, default);
        Assert.True(saved.Success, saved.Message);
        Assert.Equal("ours and theirs\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
        var resolved = await service.ExecuteAsync(repo.Root, new(GitAction.MarkResolved) { Paths = ["tracked.txt"] }, null, default);
        Assert.True(resolved.Success, resolved.Message);
        Assert.StartsWith("100755 ", await repo.Git("ls-files", "--stage"));
        var restored = await service.ExecuteAsync(repo.Root, new(GitAction.RestoreSnapshot) { SnapshotId = saved.Snapshot!.Id }, null, default);
        Assert.True(restored.Success, restored.Message); Assert.Equal(original, await repo.Git("ls-files", "--stage"));
    }

    private sealed class PausedConfirmation : IWorkspaceInteraction
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ConfirmationAnswer> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ConfirmationAnswer> ConfirmAsync(Confirmation confirmation, CancellationToken ct)
        { Entered.SetResult(); return Answer.Task; }
        public Task<string?> CredentialAsync(string prompt, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class QueueProgress : IProgress<OperationProgress>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Report(OperationProgress value) => Entered.TrySetResult();
    }
}
