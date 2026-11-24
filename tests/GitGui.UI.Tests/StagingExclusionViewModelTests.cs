using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitGui.Core;
using GitGui.UI;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class StagingExclusionViewModelTests
{
    private static readonly StageExclusion[] Exclusions = [new("private", true), new("output", true), new("already.txt", false)];

    [AvaloniaFact]
    public async Task Manager_saves_selected_files_and_all_buttons_fit_at_minimum_size()
    {
        var backend = new Backend(); var store = new RuleStore();
        using var vm = new MainWindowViewModel(backend, new NoRecent(), store) { RepositoryPath = backend.Root };
        await vm.RefreshCommand.ExecuteAsync(null); vm.SelectedChange = vm.Changes[1];
        if (!Application.Current!.Resources.ContainsKey("CanvasBrush")) _ = new AppearanceManager();
        var dialog = new StagingExclusionsWindow(vm) { Width = 620, Height = 460 };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Closed += (_, _) => closed.TrySetResult(); dialog.Show();
        try
        {
            dialog.UpdateLayout();
            var buttons = dialog.GetVisualDescendants().OfType<ActionButton>().ToArray();
            foreach (var button in buttons)
            {
                Assert.False(string.IsNullOrWhiteSpace(ControlAutomationPeer.CreatePeerForElement(button)!.GetName()));
                var point = button.TranslatePoint(default, dialog)!.Value;
                Assert.InRange(point.X, 0, dialog.ClientSize.Width - button.Bounds.Width + 1);
                Assert.InRange(point.Y, 0, dialog.ClientSize.Height - button.Bounds.Height + 1);
            }
            buttons.Single(button => button.Name == "AddSelectedExclusions").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var list = dialog.GetVisualDescendants().OfType<ListBox>().Single();
            Assert.Single(list.Items); Assert.Empty(vm.StagingExclusions);
            buttons.Single(button => button.Name == "SaveStageExclusions").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal([new StageExclusion("private/token.txt", false)], vm.StagingExclusions);
            Assert.Empty(backend.Operations);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task Cancelling_manager_keeps_unsaved_rules_out_of_the_active_policy()
    {
        var backend = new Backend(); var store = new RuleStore();
        using var vm = new MainWindowViewModel(backend, new NoRecent(), store) { RepositoryPath = backend.Root };
        await vm.RefreshCommand.ExecuteAsync(null);
        if (!Application.Current!.Resources.ContainsKey("CanvasBrush")) _ = new AppearanceManager();
        var dialog = new StagingExclusionsWindow(vm); dialog.Show();
        try
        {
            dialog.UpdateLayout();
            var buttons = dialog.GetVisualDescendants().OfType<ActionButton>().ToArray();
            buttons.Single(button => button.Name == "AddSelectedExclusions").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            buttons.Single(button => button.Name == "CancelStageExclusions").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(vm.StagingExclusions); Assert.Contains("keep.txt", vm.CreateOperation("StageAll")!.Paths);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task Selected_and_all_staging_filter_rules_while_unstage_and_discard_remain_available()
    {
        var backend = new Backend(); var store = new RuleStore();
        using var vm = new MainWindowViewModel(backend, new NoRecent(), store) { RepositoryPath = backend.Root };
        await vm.RefreshCommand.ExecuteAsync(null); await vm.SaveStagingExclusionsAsync(Exclusions);
        foreach (var file in vm.Changes) vm.SelectedFiles.Add(file);
        Assert.Equal(["keep.txt"], vm.CreateOperation("Stage")!.Paths);
        Assert.Equal(["keep.txt"], vm.CreateOperation("StageAll")!.Paths);
        Assert.Equal(["already.txt", "renamed.txt"], vm.CreateOperation("UnstageAll")!.Paths);
        Assert.Contains("private/token.txt", vm.CreateOperation("Discard")!.Paths);
        Assert.Equal(3, vm.ExcludedWorktreeCount); Assert.Equal(2, vm.ExcludedStagedCount);
        vm.SelectedChange = vm.Changes[1];
        Assert.True(vm.SelectedStagingExcluded);
        Assert.Throws<InvalidOperationException>(() => vm.CreateOperation("StagePatch"));
    }

    [AvaloniaFact]
    public async Task All_excluded_selection_does_not_execute_git()
    {
        var backend = new Backend(); var store = new RuleStore();
        using var vm = new MainWindowViewModel(backend, new NoRecent(), store) { RepositoryPath = backend.Root };
        await vm.RefreshCommand.ExecuteAsync(null); await vm.SaveStagingExclusionsAsync([new("keep.txt", false), .. Exclusions]);
        await vm.ActionCommand.ExecuteAsync("StageAll");
        Assert.Empty(backend.Operations); Assert.Equal(vm.L["StageExclusionsNothingToStage"], vm.Notice);
    }

    [AvaloniaFact]
    public async Task Reopening_and_switching_roots_reload_only_their_own_rules()
    {
        var backend = new Backend(); var store = new RuleStore();
        using (var first = new MainWindowViewModel(backend, new NoRecent(), store) { RepositoryPath = backend.Root })
        { await first.RefreshCommand.ExecuteAsync(null); await first.SaveStagingExclusionsAsync(Exclusions); }
        using var vm = new MainWindowViewModel(backend, new NoRecent(), store) { RepositoryPath = backend.Root };
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(["keep.txt"], vm.CreateOperation("StageAll")!.Paths);
        vm.RepositoryPath = backend.Root + "-other"; await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Empty(vm.StagingExclusions); Assert.Contains("private/token.txt", vm.CreateOperation("StageAll")!.Paths);
    }

    [AvaloniaFact]
    public async Task Failed_load_blocks_staging_and_failed_save_keeps_the_previous_policy()
    {
        var backend = new Backend(); var store = new RuleStore { FailLoad = true };
        using var vm = new MainWindowViewModel(backend, new NoRecent(), store) { RepositoryPath = backend.Root };
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.HasRepository); Assert.False(vm.CanStageSelection);
        Assert.Throws<InvalidOperationException>(() => vm.CreateOperation("StageAll"));
        Assert.NotNull(vm.CreateOperation("UnstageAll"));
        store.FailLoad = false; await vm.RefreshCommand.ExecuteAsync(null); await vm.SaveStagingExclusionsAsync(Exclusions);
        store.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => vm.SaveStagingExclusionsAsync([]));
        Assert.Equal(["keep.txt"], vm.CreateOperation("StageAll")!.Paths);
    }

    [AvaloniaFact]
    public async Task Staging_during_refresh_waits_for_the_new_rules_before_selecting_paths()
    {
        var backend = new Backend(); var store = new RuleStore();
        using var vm = new MainWindowViewModel(backend, new NoRecent(), store) { RepositoryPath = backend.Root };
        await vm.RefreshCommand.ExecuteAsync(null);
        store.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = vm.RefreshCommand.ExecuteAsync(null);
        await store.Entered.Task;
        var stage = vm.ActionCommand.ExecuteAsync("StageAll"); Assert.False(stage.IsCompleted);
        store.Pending.SetResult(Exclusions);
        await read; await stage;
        Assert.Equal(["keep.txt"], Assert.Single(backend.Operations).Paths);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queued_selected_staging_rechecks_new_rules_after_refresh(bool includeAllowed)
    {
        var backend = new Backend(); var store = new RuleStore();
        using var vm = new MainWindowViewModel(backend, new NoRecent(), store) { RepositoryPath = backend.Root };
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedFiles.Add(vm.Changes[1]);
        if (includeAllowed) vm.SelectedFiles.Add(vm.Changes[0]);
        store.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = vm.RefreshCommand.ExecuteAsync(null);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stage = vm.ActionCommand.ExecuteAsync("Stage");
        Assert.False(stage.IsCompleted);
        store.Pending.SetResult(Exclusions);
        await refresh; await stage;
        if (includeAllowed) Assert.Equal(["keep.txt"], Assert.Single(backend.Operations).Paths);
        else
        {
            Assert.Empty(backend.Operations);
            Assert.Equal(vm.L["StageExclusionsNothingToStage"], vm.Notice);
        }
    }

    private sealed class RuleStore : IStageExclusionStore
    {
        private readonly ConcurrentDictionary<string, IReadOnlyList<StageExclusion>> values = new();
        public bool FailLoad { get; set; }
        public bool FailSave { get; set; }
        public TaskCompletionSource<IReadOnlyList<StageExclusion>>? Pending { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<StageExclusion>> LoadStageExclusionsAsync(string root, CancellationToken ct)
        {
            if (FailLoad) throw new IOException("injected load failure");
            if (Pending is not null) { Entered.TrySetResult(); return Pending.Task; }
            return Task.FromResult(values.TryGetValue(root, out var rules) ? rules : (IReadOnlyList<StageExclusion>)[]);
        }
        public Task SaveStageExclusionsAsync(string root, IReadOnlyList<StageExclusion> rules, CancellationToken ct)
        { if (FailSave) throw new IOException("injected save failure"); values[root] = rules.ToArray(); return Task.CompletedTask; }
    }

    private sealed class NoRecent : IRecentRepositories
    {
        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task RememberAsync(string path, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Backend : IWorkspaceService
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gitgui-stage-policy-fixture");
        private readonly FileChange[] files = [new("keep.txt", ".", "M", []), new("private/token.txt", ".", "M", []),
            new("output/generated.txt", "?", "?", []), new("already.txt", "A", ".", []), new("renamed.txt", "R", "M", [], "private/old.txt")];
        public List<GitOperation> Operations { get; } = [];
        public event EventHandler<CommandEntry>? CommandExecuted { add { } remove { } }
        public Task<WorkspaceState> OpenAsync(string path, LogFilter filter, CancellationToken ct)
            => Task.FromResult(new WorkspaceState(path, new("main", "head", files), [], [], [], InProgressState.None, true, []));
        public Task<WorkspaceStatus> RefreshStatusAsync(string root, CancellationToken ct)
            => Task.FromResult(new WorkspaceStatus(root, new("main", "head", files), InProgressState.None));
        public Task<HistoryPage> GetHistoryPageAsync(string root, LogFilter filter, string? generation, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GetDiffAsync(string root, DiffRequest request, CancellationToken ct) => Task.FromResult("");
        public Task<FilePreview> GetPreviewAsync(string root, DiffRequest request, CancellationToken ct) => Task.FromResult(new FilePreview(null, null, null, null));
        public Task<CommitDetails> GetCommitAsync(string root, string commit, CancellationToken ct) => throw new NotSupportedException();
        public Task<ConflictContent> GetConflictAsync(string root, string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsAsync(string root, CancellationToken ct) => Task.FromResult<IReadOnlyList<SnapshotInfo>>([]);
        public Task TrustAsync(string root, CancellationToken ct) => Task.CompletedTask;
        public Task<OperationOutcome> ExecuteAsync(string root, GitOperation operation, IProgress<OperationProgress>? progress, CancellationToken ct)
        { Operations.Add(operation); return Task.FromResult(new OperationOutcome(true, "done")); }
    }
}
