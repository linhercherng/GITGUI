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

public sealed class RepositoryToolsWindowTests
{
    [AvaloniaFact]
    public async Task All_tool_actions_map_visible_fields_and_sparse_patterns_without_shell_parsing()
    {
        var backend = new Backend(); using var vm = Vm(backend);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SubmodulePath = "vendor/中文 library"; vm.SubmoduleUrl = "https://example.invalid/library.git"; vm.Recursive = false;
        Assert.Equal(vm.SubmodulePath, vm.CreateOperation("SubmoduleAdd").Path);
        Assert.Equal(vm.SubmoduleUrl, vm.CreateOperation("SubmoduleAdd").Url);
        Assert.False(vm.CreateOperation("SubmoduleUpdate").Recursive);
        vm.SelectedWorktree = vm.Worktrees[0]; vm.WorktreeBranch = "feature/tools"; vm.WorktreeRevision = "main"; vm.LockReason = "正在驗證";
        Assert.Equal(vm.Worktrees[0].Path, vm.CreateOperation("WorktreeRemove").Path);
        Assert.Equal("feature/tools", vm.CreateOperation("WorktreeAdd").Name);
        Assert.Equal("正在驗證", vm.CreateOperation("WorktreeLock").Reason);
        vm.LfsPatterns = "*.psd\nassets/中文 image.png\r\n"; vm.LfsRemote = "origin";
        Assert.Equal(new[] { "*.psd", "assets/中文 image.png" }, vm.CreateOperation("LfsTrack").Patterns);
        Assert.Equal("origin", vm.CreateOperation("LfsFetch").Name);
        vm.BadRevision = "bad-branch"; vm.GoodRevision = "known-good";
        Assert.Equal("known-good", vm.CreateOperation("BisectStart").GoodRevision);
        vm.SparsePatterns = "src\ndocs\n"; vm.SparseCone = false;
        Assert.Equal(new[] { "src", "docs" }, vm.CreateOperation("SparseSet").Patterns);
        Assert.False(vm.CreateOperation("SparseSet").Cone);
        foreach (var action in Enum.GetValues<RepositoryToolAction>()) Assert.Equal(action, vm.CreateOperation(action.ToString()).Action);
    }

    [AvaloniaFact]
    public async Task Write_blocks_duplicate_actions_and_closing_then_refreshes_and_recovers_snapshot()
    {
        var backend = new Backend { Pending = new() }; using var vm = Vm(backend);
        var window = new RepositoryToolsWindow(vm); window.Show();
        var changed = 0; vm.WorkspaceChanged += (_, _) => changed++;
        var write = vm.ExecuteCommand.ExecuteAsync("LfsInstall"); Assert.True(vm.IsWriting);
        window.Close(); Assert.True(window.IsVisible);
        await vm.ExecuteCommand.ExecuteAsync("LfsInstall"); Assert.Single(backend.ToolsExecuted);
        backend.Pending.SetResult(new(true, "Installed", Snapshot: Snapshot())); await write;
        Assert.Equal(1, changed); Assert.True(vm.CanUndo); Assert.False(vm.IsBusy);
        await vm.UndoCommand.ExecuteAsync(null); Assert.Equal("tools-before", Assert.Single(backend.WorkspaceExecuted).SnapshotId);
        window.Close(); Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task Blocked_writes_validation_errors_and_late_read_do_not_mutate_a_closed_workspace()
    {
        var backend = new Backend(); var vm = Vm(backend);
        vm.IsBlocked = true; await vm.ExecuteCommand.ExecuteAsync("LfsInstall"); Assert.Empty(backend.ToolsExecuted);
        vm.IsBlocked = false; await vm.ExecuteCommand.ExecuteAsync("SubmoduleAdd"); Assert.Empty(backend.ToolsExecuted); Assert.NotEmpty(vm.Notice);
        backend.PendingRead = new(); var refresh = vm.RefreshCommand.ExecuteAsync(null); vm.Dispose();
        backend.PendingRead.SetResult(State()); await refresh; Assert.Empty(vm.Worktrees);
    }

    [AvaloniaFact]
    public void Five_tabs_render_all_themes_localize_and_show_actual_command_output()
    {
        var backend = new Backend(); using var vm = Vm(backend); var appearance = new AppearanceManager();
        var window = new RepositoryToolsWindow(vm); window.Show();
        try
        {
            Assert.Equal(5, Find<TabControl>(window, "RepositoryToolsTabs").ItemCount);
            Assert.True(vm.BisectActive); Assert.True(vm.SparseEnabled); Assert.Single(vm.Submodules);
            backend.Emit(new(DateTimeOffset.Now, "git lfs status", "On branch main", 0, TimeSpan.Zero)); Dispatcher.UIThread.RunJobs();
            Assert.Contains("git lfs status", vm.Commands); Assert.Contains("On branch main", vm.Commands);
            foreach (var palette in AppearanceManager.Palettes)
            {
                appearance.Select(palette.Id); window.Width = 940; window.Height = 720; window.UpdateLayout();
                Assert.NotNull(window.Icon);
                Assert.All(window.GetVisualDescendants().OfType<ActionButton>(), button => Assert.False(string.IsNullOrWhiteSpace(button.Content?.ToString())));
            }
            vm.L.SetCulture("en"); Assert.Equal("Repository tools", window.Title);
            if (Environment.GetEnvironmentVariable("GITGUI_M3_SCREENSHOT") is { } path)
            {
                appearance.Select("graphite"); vm.L.SetCulture("zh-TW"); window.Width = 1120; window.Height = 840;
                for (var tab = 0; tab < 5; tab++)
                {
                    Find<TabControl>(window, "RepositoryToolsTabs").SelectedIndex = tab;
                    Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                    var output = tab == 0 ? path : Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-" + tab + ".png");
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!); frame.Save(output);
                }
            }
        }
        finally { window.Close(); new AppearanceManager(); }
    }

    [AvaloniaFact]
    public async Task Main_toolbar_owns_tools_and_blocks_main_writes_terminal_and_close_during_tool_write()
    {
        var backend = new Backend { Pending = new() }; using var main = new MainWindowViewModel(backend, new Recent()) { RepositoryPath = Root };
        var window = new MainWindow(main, workspace: backend, repositoryTools: backend); window.Show(); await main.RefreshCommand.ExecuteAsync(null);
        window.UpdateLayout(); var button = Find<Button>(window, "RepositoryToolsButton");
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        var tools = Assert.IsType<RepositoryToolsWindow>(Assert.Single(window.OwnedWindows));
        var write = tools.ViewModel.ExecuteCommand.ExecuteAsync("LfsInstall");
        Assert.False(main.CanOperate); Assert.False(Find<Button>(window, "TerminalButton").IsEnabled);
        Assert.False(Find<Button>(window, "GitSettingsButton").IsEnabled);
        await main.ActionCommand.ExecuteAsync("CreateBranch"); Assert.Empty(backend.WorkspaceExecuted);
        window.Close(); Assert.True(window.IsVisible);
        backend.Pending.SetResult(new(true, "Installed")); await write;
        Assert.True(main.CanOperate);
        main.IsBusy = true; Assert.True(tools.ViewModel.IsBlocked);
        await tools.ViewModel.ExecuteCommand.ExecuteAsync("LfsInstall"); Assert.Single(backend.ToolsExecuted);
        main.IsBusy = false; Assert.False(tools.ViewModel.IsBlocked);
        tools.Close(); window.Close();
    }

    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static string Root => Path.Combine(Path.GetTempPath(), "gitgui-m3-ui-fixture");
    private static RepositoryToolsWindowViewModel Vm(Backend backend) => new(backend, backend, Root);
    private static SnapshotInfo Snapshot() => new("tools-before", Root, "Before sparse change", DateTimeOffset.Now, "refs/gitgui/tools", "abc", 1);
    private static RepositoryToolsState State() => new([new("vendor/中文-library", new string('a', 40), "initialized")], [new(Root, new string('b', 40), "refs/heads/main", false, false, false, "", false)], "git-lfs/3.7", "Objects to be committed: 0", true, "git bisect start\ngit bisect bad\ngit bisect good", true, true, ["src", "docs"]);
    private sealed class Backend : IRepositoryToolsService, IWorkspaceService
    {
        public event EventHandler<CommandEntry>? CommandExecuted;
        public void Emit(CommandEntry entry) => CommandExecuted?.Invoke(this, entry);
        public List<RepositoryToolOperation> ToolsExecuted { get; } = [];
        public List<GitOperation> WorkspaceExecuted { get; } = [];
        public TaskCompletionSource<OperationOutcome>? Pending { get; set; }
        public TaskCompletionSource<RepositoryToolsState>? PendingRead { get; set; }
        public Task<RepositoryToolsState> GetStateAsync(string root, CancellationToken ct) => PendingRead?.Task ?? Task.FromResult(State());
        public Task<OperationOutcome> ExecuteAsync(string root, RepositoryToolOperation operation, IProgress<OperationProgress>? progress, CancellationToken ct) { ToolsExecuted.Add(operation); return Pending?.Task ?? Task.FromResult(new OperationOutcome(true, "Tools complete")); }
        public Task<OperationOutcome> ExecuteAsync(string root, GitOperation operation, IProgress<OperationProgress>? progress, CancellationToken ct) { WorkspaceExecuted.Add(operation); return Task.FromResult(new OperationOutcome(true, "Restored")); }
        public Task<WorkspaceState> OpenAsync(string path, LogFilter filter, CancellationToken ct) => Task.FromResult(new WorkspaceState(path, new("main", "abc", []), [], [], [], InProgressState.None, true, []));
        public Task<string> GetDiffAsync(string root, DiffRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommitDetails> GetCommitAsync(string root, string commit, CancellationToken ct) => throw new NotSupportedException();
        public Task<ConflictContent> GetConflictAsync(string root, string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<FilePreview> GetPreviewAsync(string root, DiffRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsAsync(string root, CancellationToken ct) => Task.FromResult<IReadOnlyList<SnapshotInfo>>([]);
        public Task TrustAsync(string root, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Recent : IRecentRepositories
    {
        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task RememberAsync(string path, CancellationToken ct) => Task.CompletedTask;
    }
}
