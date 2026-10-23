using Avalonia.Headless.XUnit;
using GitGui.Core;
using GitGui.UI;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class ReadPerformanceViewModelTests
{
    [AvaloniaFact]
    public async Task Applying_filters_during_pending_write_keeps_history_generation_so_next_page_resets_instead_of_mixing()
    {
        var backend = new ReadWorkspace { PendingWrite = new(), ResetChangedSearch = true };
        using var vm = new MainWindowViewModel(backend, new NoRecent()) { RepositoryPath = backend.Root, BranchName = "feature/test" };
        await vm.RefreshCommand.ExecuteAsync(null); await vm.LoadMoreCommand.ExecuteAsync(null); Assert.Equal(1500, vm.Commits.Count);
        var pending = vm.ActionCommand.ExecuteAsync("CreateBranch"); Assert.True(vm.IsBusy);
        vm.Search = "changed"; await vm.SearchHistoryCommand.ExecuteAsync(null);
        backend.PendingWrite.SetResult(new(false, "cancelled", Cancelled: true)); await pending;
        await vm.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal("generation", backend.PageGeneration); Assert.Equal(10, vm.Commits.Count);
        Assert.All(vm.Commits, commit => Assert.StartsWith("changed", commit.Subject));
    }
    [AvaloniaFact]
    public async Task Status_only_refresh_preserves_loaded_history_and_does_not_reload_snapshots()
    {
        var backend = new ReadWorkspace { FileCount = 2 };
        using var vm = new MainWindowViewModel(backend, new NoRecent()) { RepositoryPath = backend.Root };
        await vm.RefreshCommand.ExecuteAsync(null);
        var commits = vm.Commits.Select(c => c.Hash).ToArray();
        await vm.RefreshStatusAsync();
        Assert.Equal(1, backend.Opens); Assert.Equal(1, backend.StatusReads); Assert.Equal(1, backend.SnapshotReads);
        Assert.Equal(commits, vm.Commits.Select(c => c.Hash)); Assert.Equal(3, vm.Changes.Count);
    }
    [AvaloniaFact]
    public async Task Initial_history_is_500_and_load_more_appends_1000_without_reopening_the_workspace()
    {
        var backend = new ReadWorkspace();
        using var vm = new MainWindowViewModel(backend, new NoRecent()) { RepositoryPath = backend.Root };
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(500, backend.FirstFilter!.Take); Assert.Equal(500, vm.Commits.Count);
        await vm.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(1, backend.Opens);
        Assert.Equal(500, backend.PageFilter!.Skip); Assert.Equal(1000, backend.PageFilter.Take);
        Assert.Equal(1500, vm.Commits.Count); Assert.Equal(1500, vm.Commits.Select(c => c.Hash).Distinct().Count());
    }

    [AvaloniaFact]
    public async Task Fifty_thousand_status_entries_publish_one_collection_change()
    {
        var backend = new ReadWorkspace { FileCount = 50_000 };
        using var vm = new MainWindowViewModel(backend, new NoRecent()) { RepositoryPath = backend.Root };
        var changes = 0; vm.Changes.CollectionChanged += (_, _) => changes++;
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(50_000, vm.Changes.Count); Assert.Equal(1, changes);
        Assert.Equal(50_000, vm.UnstagedChanges.Count());
    }

    private sealed class NoRecent : IRecentRepositories
    {
        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task RememberAsync(string path, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class ReadWorkspace : IWorkspaceService
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gitgui-read-view");
        public int FileCount { get; init; }
        public int Opens { get; private set; }
        public int StatusReads { get; private set; }
        public int SnapshotReads { get; private set; }
        public LogFilter? FirstFilter { get; private set; }
        public LogFilter? PageFilter { get; private set; }
        public string? PageGeneration { get; private set; }
        public bool ResetChangedSearch { get; init; }
        public TaskCompletionSource<OperationOutcome>? PendingWrite { get; init; }
        public event EventHandler<CommandEntry>? CommandExecuted { add { } remove { } }
        private static CommitInfo Commit(int i) => new(i.ToString("x40"), "Commit " + i, "Author", DateTimeOffset.UnixEpoch, []);
        public Task<WorkspaceState> OpenAsync(string path, LogFilter filter, CancellationToken ct)
        {
            Opens++; FirstFilter ??= filter;
            var files = Enumerable.Range(0, FileCount).Select(i => new FileChange("file-" + i, ".", "M", [])).ToArray();
            return Task.FromResult(new WorkspaceState(Root, new("main", "head", files), Enumerable.Range(0, filter.Take).Select(Commit).ToArray(), [], [], InProgressState.None, true, []));
        }
        public Task<HistoryPage> GetHistoryPageAsync(string root, LogFilter filter, string? generation, CancellationToken ct)
        {
            PageFilter = filter; PageGeneration = generation;
            if (ResetChangedSearch && filter.Search == "changed") return Task.FromResult(new HistoryPage(Enumerable.Range(20_000, 10).Select(i => Commit(i) with { Subject = "changed " + i }).ToArray(), "changed-generation", 10, false, generation is not null && generation != "changed-generation"));
            return Task.FromResult(new HistoryPage(Enumerable.Range(filter.Skip, filter.Take).Select(Commit).ToArray(), "generation", filter.Skip + filter.Take, true));
        }
        public Task<WorkspaceStatus> RefreshStatusAsync(string root, CancellationToken ct)
        {
            StatusReads++;
            return Task.FromResult(new WorkspaceStatus(Root, new("main", "head", Enumerable.Range(0, FileCount + 1)
                .Select(i => new FileChange("file-" + i, ".", "M", [])).ToArray()), InProgressState.None));
        }
        public Task<string> GetDiffAsync(string root, DiffRequest request, CancellationToken ct) => Task.FromResult("");
        public Task<FilePreview> GetPreviewAsync(string root, DiffRequest request, CancellationToken ct) => Task.FromResult(new FilePreview(null, null, null, null));
        public Task<CommitDetails> GetCommitAsync(string root, string commit, CancellationToken ct) => throw new NotSupportedException();
        public Task<ConflictContent> GetConflictAsync(string root, string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsAsync(string root, CancellationToken ct) { SnapshotReads++; return Task.FromResult<IReadOnlyList<SnapshotInfo>>([]); }
        public Task TrustAsync(string root, CancellationToken ct) => Task.CompletedTask;
        public Task<OperationOutcome> ExecuteAsync(string root, GitOperation operation, IProgress<OperationProgress>? progress, CancellationToken ct) => PendingWrite?.Task ?? throw new NotSupportedException();
    }
}
