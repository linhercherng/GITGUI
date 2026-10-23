using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitGui.Core;
using GitGui.UI;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class ProgressiveHistoryTests
{
    [AvaloniaFact]
    public async Task History_is_visible_while_status_is_pending_without_enabling_writes_or_republishing_the_same_page()
    {
        var backend = new Backend();
        using var vm = new MainWindowViewModel(backend, new Recent()) { RepositoryPath = backend.Root };
        var publications = 0; vm.Commits.CollectionChanged += (_, _) => publications++;
        var refresh = vm.RefreshCommand.ExecuteAsync(null);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.False(refresh.IsCompleted);
        Assert.Equal(500, vm.Commits.Count);
        Assert.True(vm.IsBusy); Assert.False(vm.CanOperate); Assert.False(vm.CanApplyPatch);
        vm.SelectedCommit = vm.Commits[0];
        Assert.Equal(0, backend.CommitReads);
        backend.Complete(); await refresh;
        Assert.True(vm.HasRepository); Assert.True(vm.CanOperate);
        Assert.Equal(1, publications); Assert.Equal(500, vm.Commits.Count);
    }

    [AvaloniaFact]
    public async Task A_preview_arriving_after_disposal_is_ignored()
    {
        var backend = new Backend { DelayPreview = true };
        using var vm = new MainWindowViewModel(backend, new Recent()) { RepositoryPath = backend.Root };
        var refresh = vm.RefreshCommand.ExecuteAsync(null);
        vm.Dispose(); await backend.Publish(); backend.Complete(); await refresh;
        Assert.Empty(vm.Commits); Assert.False(vm.HasRepository);
    }

    private sealed class Recent : IRecentRepositories
    {
        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task RememberAsync(string path, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Backend : IProgressiveWorkspaceService
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gitgui-progressive-test");
        public bool DelayPreview { get; init; }
        public int CommitReads { get; private set; }
        private readonly TaskCompletionSource<WorkspaceState> status = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<WorkspaceHistoryPreview, Task>? callback;
        private readonly HistoryPage page = new(Enumerable.Range(0, 500)
            .Select(i => new CommitInfo(i.ToString("x40"), "Commit " + i, "Author", DateTimeOffset.UnixEpoch, [])).ToArray(), "generation", 500, true);
        public event EventHandler<CommandEntry>? CommandExecuted { add { } remove { } }
        public Task<WorkspaceState> OpenAsync(string path, LogFilter filter, CancellationToken ct) => status.Task;
        public async Task<WorkspaceState> OpenWithHistoryAsync(string path, LogFilter filter, Func<WorkspaceHistoryPreview, Task> publishHistory, CancellationToken ct)
        { callback = publishHistory; if (!DelayPreview) await Publish(); return await status.Task; }
        public Task Publish() => callback?.Invoke(new(Root, page)) ?? Task.CompletedTask;
        public void Complete() => status.SetResult(new(Root, new("main", page.Commits[0].Hash, []), page.Commits, [], [], InProgressState.None, true, []) { HistoryGeneration = page.Generation });
        public Task<string> GetDiffAsync(string root, DiffRequest request, CancellationToken ct) => Task.FromResult("");
        public Task<FilePreview> GetPreviewAsync(string root, DiffRequest request, CancellationToken ct) => Task.FromResult(new FilePreview(null, null, null, null));
        public Task<CommitDetails> GetCommitAsync(string root, string commit, CancellationToken ct) { CommitReads++; throw new InvalidOperationException("Preview must not start selection reads"); }
        public Task<ConflictContent> GetConflictAsync(string root, string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsAsync(string root, CancellationToken ct) => Task.FromResult<IReadOnlyList<SnapshotInfo>>([]);
        public Task TrustAsync(string root, CancellationToken ct) => Task.CompletedTask;
        public Task<OperationOutcome> ExecuteAsync(string root, GitOperation operation, IProgress<OperationProgress>? progress, CancellationToken ct) => throw new NotSupportedException();
    }
}
