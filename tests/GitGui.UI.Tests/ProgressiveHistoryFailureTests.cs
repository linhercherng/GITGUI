using Avalonia.Headless.XUnit;
using GitGui.Core;
using GitGui.UI;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class ProgressiveHistoryFailureTests
{
    [AvaloniaFact]
    public async Task Failed_post_init_progressive_refresh_never_enables_old_repository_under_new_history()
    {
        var backend = new Backend();
        using var vm = new MainWindowViewModel(backend, new Recent()) { RepositoryPath = backend.OriginalRoot };
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.HasRepository); Assert.True(vm.CanOperate);
        Assert.Equal("Original repository", Assert.Single(vm.Commits).Subject);
        vm.RepositoryPath = backend.DestinationRoot;
        await vm.ActionCommand.ExecuteAsync("Init");
        Assert.Equal((backend.DestinationRoot, GitAction.Init), Assert.Single(backend.Operations));
        Assert.True(backend.PublishedDestination);
        Assert.Contains("destination status failed", vm.Notice);
        Assert.False(vm.IsBusy);
        Assert.False(vm.HasRepository);
        Assert.False(vm.CanOperate); Assert.False(vm.CanApplyPatch);
        Assert.Empty(vm.Commits); Assert.Empty(vm.Changes); Assert.Empty(vm.Refs);
    }

    private sealed class Recent : IRecentRepositories
    {
        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task RememberAsync(string path, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Backend : IProgressiveWorkspaceService
    {
        public string OriginalRoot { get; } = Path.Combine(Path.GetTempPath(), "gitgui-preview-original");
        public string DestinationRoot { get; } = Path.Combine(Path.GetTempPath(), "gitgui-preview-destination");
        public bool PublishedDestination { get; private set; }
        public List<(string Root, GitAction Action)> Operations { get; } = [];
        public event EventHandler<CommandEntry>? CommandExecuted { add { } remove { } }
        private readonly CommitInfo original = new(new string('1', 40), "Original repository", "Author", DateTimeOffset.UnixEpoch, []);
        private readonly CommitInfo destination = new(new string('2', 40), "Destination repository", "Author", DateTimeOffset.UnixEpoch, []);

        public Task<WorkspaceState> OpenAsync(string path, LogFilter filter, CancellationToken ct)
            => Task.FromResult(new WorkspaceState(OriginalRoot, new("main", original.Hash, []), [original], [], [], InProgressState.None, true, [])
                { HistoryGeneration = "original-generation" });
        public async Task<WorkspaceState> OpenWithHistoryAsync(string path, LogFilter filter,
            Func<WorkspaceHistoryPreview, Task> publishHistory, CancellationToken ct)
        {
            if (path == OriginalRoot) return await OpenAsync(path, filter, ct);
            Assert.Equal(DestinationRoot, path);
            await publishHistory(new(DestinationRoot, new HistoryPage([destination], "destination-generation", 1, false)));
            PublishedDestination = true;
            throw new InvalidOperationException("destination status failed");
        }
        public Task<OperationOutcome> ExecuteAsync(string root, GitOperation operation, IProgress<OperationProgress>? progress, CancellationToken ct)
        {
            Operations.Add((root, operation.Action));
            return Task.FromResult(new OperationOutcome(true, "initialized destination"));
        }
        public Task<string> GetDiffAsync(string root, DiffRequest request, CancellationToken ct) => Task.FromResult("");
        public Task<FilePreview> GetPreviewAsync(string root, DiffRequest request, CancellationToken ct) => Task.FromResult(new FilePreview(null, null, null, null));
        public Task<CommitDetails> GetCommitAsync(string root, string commit, CancellationToken ct) => throw new NotSupportedException();
        public Task<ConflictContent> GetConflictAsync(string root, string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsAsync(string root, CancellationToken ct) => Task.FromResult<IReadOnlyList<SnapshotInfo>>([]);
        public Task TrustAsync(string root, CancellationToken ct) => Task.CompletedTask;
    }
}
