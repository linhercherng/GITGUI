using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitGui.Core;
using GitGui.UI;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class ReadCancellationTests
{
    [AvaloniaTheory]
    [InlineData("preview", "file")]
    [InlineData("preview", "commit")]
    [InlineData("preview", "dispose")]
    [InlineData("commit", "file")]
    [InlineData("commit", "dispose")]
    [InlineData("conflict", "file")]
    [InlineData("conflict", "commit")]
    [InlineData("conflict", "dispose")]
    public async Task Selection_and_disposal_do_not_wait_for_pending_read_cancellation_callbacks(string read, string action)
    {
        using var block = new BlockingCancellation(); var backend = new Backend(block);
        using var vm = new MainWindowViewModel(backend, new Recent()) { RepositoryPath = backend.Root };
        await vm.RefreshCommand.ExecuteAsync(null); backend.BlockRead = read;
        if (read == "commit") vm.SelectedCommit = vm.Commits[0];
        else { vm.SelectedChange = null; vm.SelectedChange = vm.Changes[0]; }
        Assert.True(block.Registered);
        // The watchdog only releases an incorrect synchronous implementation, so a failing regression cannot deadlock the test host.
        var watchdog = Task.Run(async () =>
        {
            try { await block.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Task.Delay(1000); }
            finally { block.Release.Set(); }
        });
        try
        {
            if (action == "file") vm.SelectedChange = vm.Changes[1];
            else if (action == "commit") vm.SelectedCommit = vm.Commits[0];
            else vm.Dispose();
            Assert.False(block.Release.IsSet);
            await block.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(block.CallbackOnUiThread);
            Assert.False(block.Release.IsSet);
        }
        finally
        {
            block.Release.Set();
            try { await block.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { await watchdog; }
        }
    }

    private sealed class BlockingCancellation : IDisposable
    {
        private CancellationTokenRegistration registration;
        public bool Registered { get; private set; }
        public bool CallbackOnUiThread { get; private set; }
        public ManualResetEventSlim Release { get; } = new(false);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<T> Pending<T>(CancellationToken ct)
        {
            if (Registered) throw new InvalidOperationException("Only one blocked read is expected.");
            Registered = true; var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            registration = ct.Register(() =>
            {
                CallbackOnUiThread = Dispatcher.UIThread.CheckAccess(); Started.TrySetResult(); Release.Wait();
                result.TrySetCanceled(ct); Finished.TrySetResult();
            });
            return result.Task;
        }
        public void Dispose() { Release.Set(); registration.Dispose(); Release.Dispose(); }
    }
    private sealed class Recent : IRecentRepositories
    {
        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task RememberAsync(string path, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Backend(BlockingCancellation block) : IWorkspaceService
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gitgui-read-cancellation");
        public string? BlockRead { get; set; }
        public event EventHandler<CommandEntry>? CommandExecuted { add { } remove { } }
        public Task<WorkspaceState> OpenAsync(string path, LogFilter filter, CancellationToken ct) => Task.FromResult(new WorkspaceState(Root,
            new("main", new string('a', 40), [new("conflict.txt", "U", "U", []), new("other.txt", ".", "M", [])]),
            [new(new string('a', 40), "Commit", "Author", DateTimeOffset.UnixEpoch, [])], [], [], new("merge", true), true, []));
        public Task<string> GetDiffAsync(string root, DiffRequest request, CancellationToken ct) => Task.FromResult("");
        public Task<FilePreview> GetPreviewAsync(string root, DiffRequest request, CancellationToken ct)
            => BlockRead == "preview" && request.Path == "conflict.txt" ? block.Pending<FilePreview>(ct) : Task.FromResult(new FilePreview(null, null, null, null));
        public Task<CommitDetails> GetCommitAsync(string root, string commit, CancellationToken ct)
            => BlockRead == "commit" ? block.Pending<CommitDetails>(ct) : Task.FromResult(new CommitDetails(new(commit, "Commit", "Author", DateTimeOffset.UnixEpoch, []), "Commit", []));
        public Task<ConflictContent> GetConflictAsync(string root, string path, CancellationToken ct)
            => BlockRead == "conflict" ? block.Pending<ConflictContent>(ct) : Task.FromResult(new ConflictContent("base", "ours", "theirs", "result", false));
        public Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsAsync(string root, CancellationToken ct) => Task.FromResult<IReadOnlyList<SnapshotInfo>>([]);
        public Task TrustAsync(string root, CancellationToken ct) => Task.CompletedTask;
        public Task<OperationOutcome> ExecuteAsync(string root, GitOperation operation, IProgress<OperationProgress>? progress, CancellationToken ct) => throw new NotSupportedException();
    }
}
