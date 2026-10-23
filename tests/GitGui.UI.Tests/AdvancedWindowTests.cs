using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitGui.Core;
using GitGui.UI;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class AdvancedWindowTests
{
    [AvaloniaFact]
    public async Task Advanced_window_exposes_all_six_workflows_and_renders_current_themed_state()
    {
        var backend = new Backend(); using var vm = Vm(backend);
        var window = new AdvancedWindow(vm); window.Show(); window.UpdateLayout();
        Assert.Equal(6, Control<TabControl>(window, "AdvancedTabs").ItemCount);
        Assert.Single(vm.Stashes); Assert.Single(vm.Tags); Assert.Single(vm.Remotes);
        Assert.Single(Control<ListBox>(window, "AdvancedList_Stashes").Items);
        Assert.Equal(Sha('f'), vm.Revision);
        if (Environment.GetEnvironmentVariable("GITGUI_M2_SCREENSHOT") is { } screenshot)
        {
            Capture(window, screenshot);
            Control<TabControl>(window, "AdvancedTabs").SelectedIndex = 1; vm.Upstream = "main"; await vm.LoadPlanCommand.ExecuteAsync(null);
            Capture(window, Suffix(screenshot, "rebase"));
            Control<TabControl>(window, "AdvancedTabs").SelectedIndex = 5; await vm.LoadFileCommand.ExecuteAsync(null); Capture(window, Suffix(screenshot, "file-history"));
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task Operation_mappings_keep_full_stash_identity_tag_modes_remote_urls_and_reflog_target()
    {
        var backend = new Backend { State = State(new("rebase", false)) }; using var vm = Vm(backend); await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedStash = vm.Stashes[0]; vm.StashMessage = "temporary work"; vm.IncludeUntracked = true;
        Assert.Equal(Sha('a'), vm.CreateOperation("StashPop").StashOid);
        foreach (var action in new[] { "StashApply", "StashPop", "StashDrop" })
            Assert.Equal(vm.SelectedStash.Selector, vm.CreateOperation(action).Name);
        Assert.True(vm.CreateOperation("StashPush").IncludeUntracked);
        vm.TagName = "v2.0.0"; vm.TagMessage = "Release note"; vm.AnnotatedTag = true;
        Assert.True(vm.CreateOperation("CreateTag").Annotated); Assert.Equal("Release note", vm.CreateOperation("CreateTag").Message);
        vm.AnnotatedTag = false; Assert.Null(vm.CreateOperation("CreateTag").Message);
        vm.SelectedTag = vm.Tags[0]; vm.TagName = "v2.0.0"; Assert.Equal("v2.0.0", vm.CreateOperation("DeleteTag").Name);
        vm.SelectedReflog = vm.Reflog[0]; Assert.Equal(Sha('c'), vm.CreateOperation("CherryPick").Revision);
        vm.SelectedRemote = vm.Remotes[0]; vm.NewRemoteName = "upstream"; vm.PushUrl = true;
        Assert.Equal("ssh://example.invalid/project.git", vm.RemoteUrl);
        Assert.True(vm.CreateOperation("SetRemoteUrl").PushUrl); Assert.Equal("upstream", vm.CreateOperation("RenameRemote").NewName);
        Assert.Contains("fetch:", vm.SelectedRemoteUrls); Assert.Contains("push:", vm.SelectedRemoteUrls);
        foreach (var action in new[] { "StashPush", "StashApply", "StashPop", "StashDrop", "CherryPick", "Revert", "Reset", "CreateTag", "DeleteTag", "AddRemote", "RenameRemote", "SetRemoteUrl", "RemoveRemote", "ContinueOperation", "SkipOperation", "AbortOperation" })
            Assert.Equal(action, vm.CreateOperation(action).Action.ToString());
    }

    [AvaloniaTheory]
    [InlineData(ResetMode.Soft)] [InlineData(ResetMode.Mixed)] [InlineData(ResetMode.Hard)]
    public void Reset_mapping_preserves_explicit_mode_and_revision(ResetMode mode)
    {
        using var vm = Vm(new()); vm.ResetMode = mode; vm.Revision = Sha('e');
        var op = vm.CreateOperation("Reset"); Assert.Equal(mode, op.ResetMode); Assert.Equal(Sha('e'), op.Revision);
    }

    [AvaloniaFact]
    public async Task Rebase_plan_is_ordered_editable_and_invalidated_when_upstream_changes()
    {
        using var vm = Vm(new()); vm.Upstream = "main";
        Assert.Throws<InvalidOperationException>(() => vm.CreateOperation("RebaseInteractive"));
        await vm.LoadPlanCommand.ExecuteAsync(null);
        vm.SelectedStep = vm.Plan[2]; vm.MoveUpCommand.Execute(null);
        Assert.Equal(Sha('e'), vm.Plan[1].Commit);
        vm.Plan[0].Action = RebaseStepKind.Reword; vm.Plan[0].Message = "";
        Assert.Throws<InvalidOperationException>(() => vm.CreateOperation("RebaseInteractive"));
        vm.Plan[0].Message = "A revised subject"; vm.Plan[1].Action = RebaseStepKind.Squash; vm.Plan[1].Message = "";
        var operation = vm.CreateOperation("RebaseInteractive");
        Assert.Equal(RebaseStepKind.Reword, operation.RebasePlan[0].Action); Assert.Null(operation.RebasePlan[1].Message);
        Assert.Equal(Sha('e'), operation.RebasePlan[1].Commit);
        vm.Plan[0].Action = RebaseStepKind.Fixup; Assert.Throws<InvalidOperationException>(() => vm.CreateOperation("RebaseInteractive"));
        vm.Upstream = "other"; Assert.Empty(vm.Plan); Assert.Throws<InvalidOperationException>(() => vm.CreateOperation("RebaseInteractive"));
    }

    [AvaloniaFact]
    public async Task Actual_pointer_drag_reorders_rebase_steps_and_accessible_buttons_preserve_selection()
    {
        using var vm = Vm(new()); var window = new AdvancedWindow(vm); window.Show();
        Control<TabControl>(window, "AdvancedTabs").SelectedIndex = 1; vm.Upstream = "main"; await vm.LoadPlanCommand.ExecuteAsync(null);
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var handle = Control<Border>(window, "AdvancedDrag_" + Sha('b')[..10]);
        var target = Control<ListBox>(window, "AdvancedRebasePlan").GetVisualDescendants().OfType<ListBoxItem>().Single(i => ReferenceEquals(i.DataContext, vm.Plan[2]));
        var start = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
        var end = target.TranslatePoint(new Point(12, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(start, MouseButton.Left, RawInputModifiers.None); window.MouseMove(end, RawInputModifiers.LeftMouseButton); window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
        Assert.Equal(Sha('b'), vm.Plan[2].Commit); Assert.Same(vm.Plan[2], vm.SelectedStep);
        vm.MoveUpCommand.Execute(null); Assert.Equal(Sha('b'), vm.Plan[1].Commit);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Completed_write_refreshes_state_notifies_owner_and_exposes_recovery_snapshot()
    {
        var backend = new Backend { Outcome = new(true, "stash saved", Snapshot: Snapshot("before-stash")) }; using var vm = Vm(backend);
        await vm.RefreshCommand.ExecuteAsync(null); var changed = new List<OperationOutcome>(); vm.WorkspaceChanged += (_, outcome) => changed.Add(outcome);
        vm.StashMessage = "in flight"; await vm.ExecuteCommand.ExecuteAsync("StashPush");
        Assert.True(vm.CanUndo); Assert.Empty(vm.StashMessage); Assert.Single(changed); Assert.Equal(2, backend.RefreshCount);
        Assert.Equal("in flight", Assert.Single(backend.Executed).Message);
        backend.Outcome = new(true, "restored", Snapshot: Snapshot("before-restore")); await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(GitAction.RestoreSnapshot, backend.Executed[^1].Action); Assert.Equal("before-stash", backend.Executed[^1].SnapshotId);
    }

    [AvaloniaFact]
    public async Task Conflict_outcome_refreshes_restarted_operation_and_requires_resolution_before_continue()
    {
        var backend = new Backend { State = State(new("cherry-pick", true)), Outcome = new(false, "resolve conflict", GitErrorKind.Conflict) }; using var vm = Vm(backend);
        var notified = false; vm.WorkspaceChanged += (_, _) => notified = true;
        await vm.ExecuteCommand.ExecuteAsync("CherryPick");
        Assert.True(notified); Assert.True(vm.HasActiveOperation); Assert.False(vm.CanContinue); Assert.True(vm.CanSkip); Assert.True(vm.CanAbort);
        Assert.Throws<InvalidOperationException>(() => vm.CreateOperation("ContinueOperation"));
        backend.State = State(new("cherry-pick", false)); await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.CanContinue); Assert.Equal(GitAction.ContinueOperation, vm.CreateOperation("ContinueOperation").Action);
    }

    [AvaloniaFact]
    public async Task File_history_rejects_stale_result_and_does_not_mutate_closed_workspace()
    {
        var backend = new Backend { PendingFile = new() }; var vm = Vm(backend); var load = vm.LoadFileCommand.ExecuteAsync(null);
        vm.FilePath = "different.txt"; backend.PendingFile.SetResult(History()); await load;
        Assert.Empty(vm.Blame); Assert.Empty(vm.FileHistory);
        backend.PendingFile = new(); var late = vm.LoadFileCommand.ExecuteAsync(null); vm.Dispose(); backend.PendingFile.SetResult(History()); await late;
        Assert.Empty(vm.Blame);
    }

    [AvaloniaFact]
    public async Task File_history_loads_blame_and_renamed_paths_and_selected_commit_becomes_operation_target()
    {
        using var vm = Vm(new()); await vm.LoadFileCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Blame.Count); Assert.Equal("src/old-name.cs", vm.FileHistory[1].Path); Assert.Contains("fixture", vm.FileBackend);
        vm.SelectedFileCommit = vm.FileHistory[1]; Assert.Equal(Sha('c'), vm.CreateOperation("Revert").Revision);
    }

    [AvaloniaFact]
    public async Task Closing_during_write_is_deferred_and_locale_updates_existing_window()
    {
        var backend = new Backend { PendingWrite = new() }; using var vm = Vm(backend); var window = new AdvancedWindow(vm); window.Show();
        vm.L.SetCulture("en"); Dispatcher.UIThread.RunJobs(); Assert.Equal("Advanced Git workspace", window.Title);
        var write = vm.ExecuteCommand.ExecuteAsync("StashPush"); Assert.True(vm.IsWriting);
        window.Close(); Assert.True(window.IsVisible); Assert.Equal(vm.L["AdvancedWait"], vm.Notice);
        backend.PendingWrite.SetResult(new(true, "saved")); await write; window.Close(); Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task Main_toolbar_opens_advanced_window_through_pointer_and_refreshes_after_advanced_write_and_close()
    {
        var backend = new Backend(); using var main = new MainWindowViewModel(backend, new Recent()) { RepositoryPath = Path.Combine(Path.GetTempPath(), "gitgui-m2-fixture") };
        var window = new MainWindow(main, workspace: backend); window.Show(); await main.RefreshCommand.ExecuteAsync(null);
        var before = backend.MainRefreshCount;
        var button = window.FindControl<Button>("AdvancedButton")!;
        window.UpdateLayout();
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        var advanced = Assert.IsType<AdvancedWindow>(Assert.Single(window.OwnedWindows));
        await advanced.ViewModel.ExecuteCommand.ExecuteAsync("StashPush");
        Assert.True(backend.MainRefreshCount > before); var after = backend.MainRefreshCount;
        advanced.Close(); Dispatcher.UIThread.RunJobs(); Assert.True(backend.MainRefreshCount > after);
        window.Close();
    }

    private static T Control<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static string Sha(char value) => new(value, 40);
    private static AdvancedWindowViewModel Vm(Backend backend) => new(backend, backend, Path.Combine(Path.GetTempPath(), "gitgui-m2-fixture"), "src/current.cs", Sha('f'));
    private static SnapshotInfo Snapshot(string id) => new(id, "fixture", "before operation", DateTimeOffset.UnixEpoch, "refs/gitgui/" + id, Sha('f'), 1);
    private static AdvancedState State(InProgressState? active = null) => new([new(Sha('a'), "stash@{0}", "WIP: graphite UI", DateTimeOffset.Now)], [new("v1.0.0", Sha('b'), Sha('c'), true, "Stable release")], [new(Sha('c'), "HEAD@{1}", "commit: add repository navigation", "Will Lin", DateTimeOffset.Now)], [new("origin", ["https://example.invalid/project.git"], ["ssh://example.invalid/project.git"])], active ?? InProgressState.None);
    private static FileHistoryResult History() => new([new(1, Sha('b'), "Will Lin", "public class Workspace"), new(2, Sha('c'), "Alex Chen", "    // safe operations")], [new(new(Sha('b'), "Rename workspace model", "Will Lin", DateTimeOffset.Now, [Sha('c')]), "src/current.cs"), new(new(Sha('c'), "Add workspace model", "Alex Chen", DateTimeOffset.Now.AddDays(-1), []), "src/old-name.cs")], "fixture · rename-following history");
    private static string Suffix(string path, string suffix) => Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-" + suffix + ".png");
    private static void Capture(Window window, string path)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); Directory.CreateDirectory(Path.GetDirectoryName(path)!); frame.Save(path);
    }
    private sealed class Backend : IWorkspaceService, IAdvancedWorkspaceService
    {
        public event EventHandler<CommandEntry>? CommandExecuted { add { } remove { } }
        public AdvancedState State { get; set; } = AdvancedWindowTests.State();
        public int RefreshCount { get; private set; }
        public int MainRefreshCount { get; private set; }
        public List<GitOperation> Executed { get; } = [];
        public OperationOutcome Outcome { get; set; } = new(true, "Completed");
        public TaskCompletionSource<OperationOutcome>? PendingWrite { get; set; }
        public TaskCompletionSource<FileHistoryResult>? PendingFile { get; set; }
        public Task<AdvancedState> GetAdvancedStateAsync(string root, CancellationToken ct) { RefreshCount++; return Task.FromResult(State); }
        public Task<IReadOnlyList<RebaseStep>> GetRebasePlanAsync(string root, string upstream, CancellationToken ct) => Task.FromResult<IReadOnlyList<RebaseStep>>([new(Sha('b'), Message: "Add recovery model"), new(Sha('d'), Message: "Add professional UI"), new(Sha('e'), Message: "Polish repository navigation")]);
        public Task<FileHistoryResult> GetFileHistoryAsync(string root, string path, CancellationToken ct) => PendingFile?.Task ?? Task.FromResult(History());
        public Task<OperationOutcome> ExecuteAsync(string root, GitOperation operation, IProgress<OperationProgress>? progress, CancellationToken ct) { Executed.Add(operation); return PendingWrite?.Task ?? Task.FromResult(Outcome); }
        public Task<WorkspaceState> OpenAsync(string path, LogFilter filter, CancellationToken ct)
        {
            MainRefreshCount++;
            return Task.FromResult(new WorkspaceState(path, new("main", Sha('f'), []), [new(Sha('f'), "M2 workspace", "Will Lin", DateTimeOffset.Now, [])], [], [], State.InProgress, true, []));
        }
        public Task<string> GetDiffAsync(string root, DiffRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommitDetails> GetCommitAsync(string root, string commit, CancellationToken ct) => throw new NotSupportedException();
        public Task<ConflictContent> GetConflictAsync(string root, string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<FilePreview> GetPreviewAsync(string root, DiffRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsAsync(string root, CancellationToken ct) => Task.FromResult<IReadOnlyList<SnapshotInfo>>([]);
        public Task TrustAsync(string root, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Recent : IRecentRepositories
    {
        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task RememberAsync(string path, CancellationToken ct) => Task.CompletedTask;
    }
}
