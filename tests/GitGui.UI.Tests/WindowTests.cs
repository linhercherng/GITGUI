using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitGui.Controls;
using GitGui.Core;
using GitGui.UI;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(GitGui.UI.Tests.TestApplication))]
namespace GitGui.UI.Tests;

public class TestApplication : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    public override void Initialize() { RequestedThemeVariant = ThemeVariant.Dark; Styles.Add(new FluentTheme()); }
}

public sealed class WindowTests
{
    [AvaloniaFact]
    public void Diff_loading_keeps_the_visible_editor_viewport_document_and_scroll_position_stable()
    {
        using var vm = new MainWindowViewModel(new SampleWorkspace(), new MemoryRecent())
        {
            Diff = string.Join('\n', Enumerable.Range(1, 600).Select(line => $" context line {line}"))
        };
        var window = new MainWindow(vm); window.Show();
        try
        {
            void Arrange()
            {
                window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            }
            Arrange();
            var viewer = window.FindControl<DiffEditor>("DiffViewer")!;
            var editor = Assert.Single(viewer.GetVisualDescendants().OfType<CodeEditor>(), e => e.IsEffectivelyVisible);
            editor.ScrollToLine(100); Arrange();
            var bounds = viewer.Bounds; var editorBounds = editor.Bounds; var textViewBounds = editor.TextArea.TextView.Bounds;
            var document = editor.Document; var offset = editor.TextArea.TextView.ScrollOffset;
            Assert.True(bounds.Height > 0); Assert.True(offset.Y > 0);
            ProgressBar? progress = null;
            foreach (var loading in new[] { true, false, true, false })
            {
                vm.IsDiffLoading = loading; Arrange();
                Assert.Equal(bounds, viewer.Bounds);
                Assert.Equal(editorBounds, editor.Bounds); Assert.Equal(textViewBounds, editor.TextArea.TextView.Bounds);
                Assert.Same(document, editor.Document); Assert.Equal(vm.Diff, editor.Document.Text);
                Assert.Equal(offset, editor.TextArea.TextView.ScrollOffset);
                if (loading) progress = Assert.Single(window.GetVisualDescendants().OfType<ProgressBar>(), p => p.IsEffectivelyVisible);
                Assert.NotNull(progress); Assert.Equal(loading, progress.IsVisible); Assert.Equal(loading, progress.IsIndeterminate);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Rejected_commit_hook_offers_explicit_one_time_bypass_with_confirmation_and_scope_guards()
    {
        var backend = new SampleWorkspace { NextOutcome = new(false, "pre-commit hook failed", GitErrorKind.HookRejected) };
        using var vm = new MainWindowViewModel(backend, new MemoryRecent()) { RepositoryPath = SampleWorkspace.RootPath, CommitMessage = "Reviewed commit" };
        var window = new MainWindow(vm); window.Show(); if (vm.InitializeCommand.ExecutionTask is { } initialization) await initialization;
        await vm.RefreshCommand.ExecuteAsync(null); await vm.ActionCommand.ExecuteAsync("Commit");
        Assert.Equal(vm.L["RetryWithoutHooks"], vm.Feedback.ActionLabel);
        var declined = vm.Feedback.RemedyCommand.ExecuteAsync(null);
        var dialog = Assert.Single(window.OwnedWindows);
        dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConfirmationCancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await declined; Assert.Single(backend.Executed); Assert.False(vm.SkipHooks);
        backend.NextOutcome = new(true, "committed");
        var accepted = vm.Feedback.RemedyCommand.ExecuteAsync(null); dialog = Assert.Single(window.OwnedWindows);
        dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConfirmationAcceptButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await accepted; Assert.Equal(2, backend.Executed.Count); Assert.True(backend.Executed[^1].SkipHooks); Assert.False(vm.SkipHooks);
        vm.CommitMessage = "another"; backend.NextOutcome = new(false, "hook failed", GitErrorKind.HookRejected); await vm.ActionCommand.ExecuteAsync("Commit");
        vm.CommitMessage = "newer draft"; await vm.Feedback.RemedyCommand.ExecuteAsync(null); Assert.Empty(window.OwnedWindows); Assert.Equal(3, backend.Executed.Count);
        backend.NextOutcome = new(false, "pre-push hook failed", GitErrorKind.HookRejected); await vm.ActionCommand.ExecuteAsync("Push");
        Assert.Equal(vm.L["RemedyShowOutput"], vm.Feedback.ActionLabel); window.Close();
    }
    [AvaloniaFact]
    public async Task Keyboard_routes_stage_commit_and_cancel_palette_restore_focus_and_name_full_files()
    {
        var filename = "src/" + new string('長', 80) + ".cs";
        var backend = new SampleWorkspace { FilesOverride = [new(filename, "M", "M", [])] };
        using var vm = new MainWindowViewModel(backend, new MemoryRecent()) { RepositoryPath = SampleWorkspace.RootPath };
        var shortcuts = new ShortcutManager(); shortcuts.SetGesture("Main.FocusFiles", "Ctrl+1"); shortcuts.SetGesture("Main.Stage", "Ctrl+T"); shortcuts.SetGesture("Main.FocusCommit", "Ctrl+2");
        var window = new MainWindow(vm, shortcuts: shortcuts); window.Show(); if (vm.InitializeCommand.ExecutionTask is { } initialization) await initialization; await vm.RefreshCommand.ExecuteAsync(null); window.UpdateLayout();
        try
        {
            void Press(PhysicalKey key, RawInputModifiers modifiers) { window.KeyPressQwerty(key, modifiers); window.KeyReleaseQwerty(key, modifiers); Dispatcher.UIThread.RunJobs(); }
            Press(PhysicalKey.Digit1, RawInputModifiers.Control); Press(PhysicalKey.ArrowDown, RawInputModifiers.None);
            var files = window.FindControl<ListBox>("ChangesList")!; Assert.True(files.IsKeyboardFocusWithin, $"repository={vm.HasRepository},busy={vm.IsBusy},staged={vm.ShowStaged},focus={window.FocusManager?.GetFocusedElement()},visible={files.IsEffectivelyVisible},keys={string.Join(',', window.KeyBindings.Select(b => b.Gesture))}");
            var item = Assert.Single(files.GetRealizedContainers());
            Assert.Equal(filename, Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(item)!.GetName());
            Press(PhysicalKey.T, RawInputModifiers.Control); Assert.Equal(GitAction.Stage, Assert.Single(backend.Executed).Action);
            Press(PhysicalKey.Digit2, RawInputModifiers.Control); var input = window.FindControl<TextBox>("CommitMessageInput")!; Assert.True(input.IsKeyboardFocusWithin);
            window.KeyTextInput("Keyboard commit"); Assert.Equal("Keyboard commit", vm.CommitMessage);
            Press(PhysicalKey.P, RawInputModifiers.Control | RawInputModifiers.Shift); var dialog = Assert.Single(window.OwnedWindows);
            var paletteTask = ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)window.KeyBindings.Single(b => b.Gesture!.ToString() == "Ctrl+Shift+P").Command!).ExecutionTask;
            dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); if (paletteTask is not null) await paletteTask; Dispatcher.UIThread.RunJobs();
            Assert.True(input.IsKeyboardFocusWithin, $"After palette focus={window.FocusManager?.GetFocusedElement()}, busy={vm.IsBusy},enabled={input.IsEffectivelyEnabled}, owned={window.OwnedWindows.Count}");
            Press(PhysicalKey.Enter, RawInputModifiers.Control); Assert.Equal(GitAction.Commit, backend.Executed[^1].Action);
            Assert.Equal("Keyboard commit", backend.Executed[^1].Message);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public void Appearance_toolbar_and_palette_buttons_work_through_actual_pointer_input()
    {
        using var vm = new MainWindowViewModel(new SampleWorkspace(), new MemoryRecent());
        var appearance = new AppearanceManager();
        var window = new MainWindow(vm, appearance: appearance); window.Show();
        try
        {
            Click(window, window.FindControl<Button>("AppearanceButton")!);
            var picker = Assert.IsType<AppearanceWindow>(Assert.Single(window.OwnedWindows));
            Click(picker, picker.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "Theme_forest"));
            Assert.Equal("forest", appearance.Current.Id);
            picker.Close();
        }
        finally { window.Close(); new AppearanceManager(); }
        static void Click(Window owner, Button button)
        {
            owner.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), owner)!.Value;
            owner.MouseDown(point, MouseButton.Left); owner.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Four_palettes_preserve_actions_labels_and_layout_at_supported_window_sizes()
    {
        using var vm = new MainWindowViewModel(new SampleWorkspace(), new MemoryRecent()) { RepositoryPath = SampleWorkspace.RootPath };
        var appearance = new AppearanceManager(); var graph = Graph();
        var window = new MainWindow(vm, graph, appearance: appearance); window.Show();
        try
        {
            await vm.RefreshCommand.ExecuteAsync(null);
            vm.SelectedChange = vm.Changes[0];
            foreach (var palette in AppearanceManager.Palettes)
            {
                appearance.Select(palette.Id);
                window.FindControl<TabControl>("WorkspaceTabs")!.SelectedIndex = 0;
                window.FindControl<TabControl>("DetailTabs")!.SelectedIndex = 0;
                window.UpdateLayout();
                Assert.NotNull(window.Icon);
                Assert.NotNull(window.FindControl<Button>("AppearanceButton"));
                Assert.All(window.GetVisualDescendants().OfType<ActionButton>(), button => Assert.False(string.IsNullOrWhiteSpace(button.Content?.ToString())));
                Assert.NotEmpty(window.GetVisualDescendants().OfType<ActionIcon>());
                if (Environment.GetEnvironmentVariable("GITGUI_APPEARANCE_SCREENSHOTS") is { } directory)
                {
                    await Capture(window, Path.Combine(directory, "appearance-" + palette.Id + ".png"));
                    window.FindControl<TabControl>("WorkspaceTabs")!.SelectedIndex = 1;
                    graph.SelectedCommit = vm.Commits[0];
                    await Capture(window, Path.Combine(directory, "appearance-" + palette.Id + "-history.png"));
                    graph.SelectedCommit = null;
                }
            }
            foreach (var english in new[] { false, true })
            {
                window.FindControl<TabControl>("WorkspaceTabs")!.SelectedIndex = 0;
                vm.English = english; window.Width = 1100; window.Height = 800;
                window.UpdateLayout();
                foreach (var name in new[] { "AppearanceButton", "GitSettingsButton", "OpenRepositoryButton", "CommitButton", "AdvancedButton", "TerminalButton", "RepositoryToolsButton" })
                {
                    var button = window.FindControl<Button>(name)!;
                    var point = button.TranslatePoint(default, window)!.Value;
                    Assert.InRange(point.X + button.Bounds.Width, 0, window.Bounds.Width);
                    Assert.True(button.Bounds.Height >= 34);
                }
            }
            window.Width = 1480; window.Height = 940; vm.English = false;
            appearance.Select("porcelain");
            var picker = new AppearanceWindow(appearance, vm.L); picker.Show(window);
            if (Environment.GetEnvironmentVariable("GITGUI_APPEARANCE_SCREENSHOTS") is { } folder)
                await Capture(picker, Path.Combine(folder, "appearance-picker.png"));
            picker.Close();
        }
        finally { window.Close(); new AppearanceManager(); }
    }

    [AvaloniaFact]
    public async Task Workspace_binds_actions_diff_and_graph_and_can_capture_actual_frame()
    {
        var backend = new SampleWorkspace();
        using var vm = new MainWindowViewModel(backend, new MemoryRecent()) { RepositoryPath = SampleWorkspace.RootPath };
        var graph = Graph();
        var window = new MainWindow(vm, graph); window.Show();
        await vm.RefreshCommand.ExecuteAsync(null);
        window.UpdateLayout();
        Assert.Equal(3, window.FindControl<ListBox>("ChangesList")!.ItemCount);
        Assert.True(window.FindControl<DiffEditor>("DiffViewer")!.IsReadOnly);
        Assert.Equal("main", vm.Branch);
        Assert.DoesNotContain("M0", window.Title);
        Assert.Contains("+new behavior", vm.Diff);
        Assert.Equal(4, vm.Commits.Count);
        graph.SelectedCommit = vm.Commits[0];
        Assert.Equal(vm.Commits[0], vm.SelectedCommit);
        Assert.Equal(2, vm.CommitFiles.Count);
        vm.SelectedCommit = null;
        vm.SelectedChange = null;
        vm.SelectedChange = vm.Changes[0];
        var screenshot = Environment.GetEnvironmentVariable("GITGUI_SCREENSHOT");
        if (screenshot is not null)
        {
            await Capture(window, screenshot);
            window.FindControl<TabControl>("WorkspaceTabs")!.SelectedIndex = 1;
            graph.SelectedCommit = vm.Commits[0];
            await Capture(window, Path.Combine(Path.GetDirectoryName(screenshot)!, Path.GetFileNameWithoutExtension(screenshot) + "-history.png"));
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task Every_P0_operation_is_mapped_with_its_explicit_options()
    {
        var backend = new SampleWorkspace();
        using var vm = new MainWindowViewModel(backend, new MemoryRecent()) { RepositoryPath = SampleWorkspace.RootPath, BranchName = "feature/work", NewBranchName = "feature/review", CloneUrl = "https://example.org/project.git", CommitMessage = "Complete M1", Amend = true, SkipHooks = true, SignCommit = true, ForcePush = true, ForceDelete = true };
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedChange = vm.Changes[0];
        foreach (var action in new[] { "Init", "Clone", "Stage", "StageAll", "Unstage", "UnstageAll", "Discard", "Commit", "CreateBranch", "SwitchBranch", "RenameBranch", "DeleteBranch", "Fetch", "Pull", "Push", "Merge", "AbortMerge", "ResolveOurs", "ResolveTheirs", "MarkResolved", "ExternalMergeTool" })
        {
            var operation = vm.CreateOperation(action);
            Assert.NotNull(operation);
            Assert.Equal(action.Replace("All", ""), operation.Action.ToString());
        }
        var commit = vm.CreateOperation("Commit")!;
        Assert.True(commit.Amend && commit.SkipHooks && commit.Sign);
        Assert.True(vm.CreateOperation("Push")!.Force);
        Assert.True(vm.CreateOperation("DeleteBranch")!.Force);
        Assert.Equal("feature/review", vm.CreateOperation("RenameBranch")!.NewName);
        Assert.Equal(new[] { "src/Workspace.cs", "docs/README.md", "notes/new.txt" }, vm.CreateOperation("StageAll")!.Paths);
        Assert.Equal(new[] { "src/Workspace.cs", "src/Graph.cs" }, vm.CreateOperation("UnstageAll")!.Paths);
    }

    [AvaloniaFact]
    public async Task Selected_hunks_and_lines_build_patch_and_staged_direction_is_enforced()
    {
        using var vm = new MainWindowViewModel(new SampleWorkspace(), new MemoryRecent()) { RepositoryPath = SampleWorkspace.RootPath };
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.PatchRows);
        vm.PatchRows.First(r => r.LineId is not null && r.Label.Contains("+new behavior")).Selected = true;
        var stage = vm.CreateOperation("StagePatch")!;
        Assert.Contains("+new behavior", stage.Patch);
        Assert.Throws<InvalidOperationException>(() => vm.CreateOperation("UnstagePatch"));
        vm.ShowStaged = true;
        vm.PatchRows.First(r => r.LineId is null).Selected = true;
        var unstage = vm.CreateOperation("UnstagePatch")!;
        Assert.Contains("-new behavior", unstage.Patch);
        Assert.Contains("+old behavior", unstage.Patch);
    }

    [AvaloniaFact]
    public async Task Failed_and_successful_commit_reset_skip_hooks_and_errors_reach_notice()
    {
        var backend = new SampleWorkspace { NextOutcome = new(false, "pre-commit rejected", GitErrorKind.HookRejected) };
        using var vm = new MainWindowViewModel(backend, new MemoryRecent()) { RepositoryPath = SampleWorkspace.RootPath, CommitMessage = "A message", SkipHooks = true };
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.ActionCommand.ExecuteAsync("Commit");
        Assert.False(vm.SkipHooks); Assert.Equal("A message", vm.CommitMessage);
        Assert.Contains("HookRejected", vm.Notice); Assert.Contains("pre-commit rejected", vm.Notice);
        backend.NextOutcome = new(true, "committed"); vm.SkipHooks = true;
        await vm.ActionCommand.ExecuteAsync("Commit");
        Assert.False(vm.SkipHooks); Assert.Empty(vm.CommitMessage); Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task Commit_disables_message_editor_and_preserves_newer_programmatic_draft()
    {
        var backend = new SampleWorkspace();
        using var vm = new MainWindowViewModel(backend, new MemoryRecent()) { RepositoryPath = SampleWorkspace.RootPath, CommitMessage = "Submitted message", Amend = true };
        var window = new MainWindow(vm); window.Show(); await vm.RefreshCommand.ExecuteAsync(null);
        try
        {
            backend.PendingOperation = new();
            var commit = vm.ActionCommand.ExecuteAsync("Commit");
            Assert.True(vm.IsBusy);
            Assert.False(window.FindControl<TextBox>("CommitMessageInput")!.IsEnabled);
            vm.CommitMessage = "New draft entered while commit completes";
            backend.PendingOperation.SetResult(new(true, "Committed submitted message")); await commit;
            Assert.Equal("Submitted message", Assert.Single(backend.Executed).Message);
            Assert.Equal("New draft entered while commit completes", vm.CommitMessage);
            Assert.True(window.FindControl<TextBox>("CommitMessageInput")!.IsEnabled);
            backend.PendingOperation = null;
            await vm.ActionCommand.ExecuteAsync("Commit");
            Assert.Empty(vm.CommitMessage);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Undo_restore_and_redo_use_new_pre_restore_snapshot()
    {
        var backend = new SampleWorkspace { NextOutcome = new(true, "merged", Snapshot: Snapshot("before-merge")) };
        using var vm = new MainWindowViewModel(backend, new MemoryRecent()) { RepositoryPath = SampleWorkspace.RootPath, BranchName = "feature" };
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.ActionCommand.ExecuteAsync("Merge");
        Assert.True(vm.CanUndo);
        backend.NextOutcome = new(true, "restored", Snapshot: Snapshot("before-undo"));
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal("before-merge", backend.Executed[^1].SnapshotId);
        Assert.True(vm.CanRedo); Assert.False(vm.CanUndo);
        backend.NextOutcome = new(true, "redone", Snapshot: Snapshot("before-redo"));
        await vm.RedoCommand.ExecuteAsync(null);
        Assert.Equal("before-undo", backend.Executed[^1].SnapshotId);
        Assert.True(vm.CanUndo); Assert.False(vm.CanRedo);
    }

    [AvaloniaFact]
    public async Task Filters_paging_recent_repository_and_failed_open_clear_stale_data()
    {
        var backend = new SampleWorkspace();
        using var vm = new MainWindowViewModel(backend, new MemoryRecent()) { Search = "graph", AuthorFilter = "Lin", PathFilter = "src/", SinceFilter = "2026-01-01", UntilFilter = "2026-10-23" };
        await vm.OpenRecentCommand.ExecuteAsync(SampleWorkspace.RootPath);
        Assert.Equal(SampleWorkspace.RootPath, vm.RepositoryPath); Assert.True(vm.HasRepository);
        await vm.SearchHistoryCommand.ExecuteAsync(null);
        Assert.Equal(new LogFilter("graph", "Lin", "src/", "2026-01-01", "2026-10-23", Take: 500), backend.LastFilter);
        await vm.LoadMoreCommand.ExecuteAsync(null); Assert.Equal(1000, backend.LastFilter!.Take); Assert.Equal(4, backend.LastFilter.Skip);
        vm.RepositoryPath = ""; await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.HasRepository); Assert.Empty(vm.Changes); Assert.Empty(vm.Commits); Assert.Empty(vm.Diff); Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task Large_diff_requires_explicit_load_and_binary_diff_has_no_patch_controls()
    {
        var backend = new SampleWorkspace { DiffText = new string('x', 1024 * 1024 + 1) };
        using var vm = new MainWindowViewModel(backend, new MemoryRecent()) { RepositoryPath = SampleWorkspace.RootPath };
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.IsLargeDiff); Assert.True(vm.Diff.Length < 1000); Assert.Empty(vm.PatchRows);
        await vm.LoadLargeDiffCommand.ExecuteAsync(null); Assert.False(vm.IsLargeDiff); Assert.True(vm.Diff.Length > 1024 * 1024);
        backend.DiffText = "Binary files a/image.png and b/image.png differ";
        vm.SelectedChange = vm.Changes[1]; Assert.Empty(vm.PatchRows); Assert.Contains("Binary files", vm.Diff);
    }

    [AvaloniaFact]
    public async Task Typed_confirmation_has_safe_default_and_requires_exact_text()
    {
        using var vm = new MainWindowViewModel(new SampleWorkspace(), new MemoryRecent());
        var window = new MainWindow(vm); window.Show();
        var pending = window.ConfirmAsync(new("Force push", "Remote main will be rewritten", RiskLevel.RemoteIrreversible, "main"), CancellationToken.None);
        var modal = Assert.Single(window.OwnedWindows);
        var accept = modal.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConfirmationAcceptButton");
        var input = modal.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "ConfirmationTextInput");
        Assert.False(accept.IsEnabled);
        input.Text = "Main"; Assert.False(accept.IsEnabled);
        input.Text = "main"; Dispatcher.UIThread.RunJobs(); Assert.True(accept.IsEnabled);
        accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(new ConfirmationAnswer(true, "main"), await pending);
        var cancelled = window.ConfirmAsync(new("Discard", "Files will be removed", RiskLevel.High), CancellationToken.None);
        Assert.Single(window.OwnedWindows).Close();
        Assert.False((await cancelled).Accepted);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Credential_prompt_masks_input_returns_secret_once_and_closes_on_cancel()
    {
        using var vm = new MainWindowViewModel(new SampleWorkspace(), new MemoryRecent());
        var window = new MainWindow(vm); window.Show();
        var pending = window.CredentialAsync("Password for https://example.org", CancellationToken.None);
        var modal = Assert.Single(window.OwnedWindows);
        var input = modal.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "CredentialInput");
        Assert.NotEqual('\0', input.PasswordChar);
        input.Text = "test-secret";
        modal.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CredentialAcceptButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("test-secret", await pending); Assert.DoesNotContain("test-secret", vm.Commands);
        using var cancellation = new CancellationTokenSource();
        var cancelled = window.CredentialAsync("Password", cancellation.Token); cancellation.Cancel();
        Assert.Null(await cancelled); window.Close();
    }

    [AvaloniaFact]
    public async Task Localization_switches_live_and_disposal_unsubscribes_command_events()
    {
        var backend = new SampleWorkspace();
        var vm = new MainWindowViewModel(backend, new MemoryRecent());
        Assert.Equal("開啟", vm.L["Open"]); vm.English = true; Assert.Equal("Open", vm.L["Open"]);
        Assert.Equal("Open a repository to begin.", vm.Notice);
        backend.Emit(new(DateTimeOffset.Now, "git status", "clean", 0, TimeSpan.Zero));
        Assert.Contains("git status", vm.Commands);
        await Task.Run(() => backend.Emit(new(DateTimeOffset.Now, "git log", "history", 0, TimeSpan.Zero)));
        Dispatcher.UIThread.RunJobs(); Assert.Contains("git log", vm.Commands);
        vm.Dispose(); var before = vm.Commands;
        backend.Emit(new(DateTimeOffset.Now, "git push", "ignored", 0, TimeSpan.Zero)); Assert.Equal(before, vm.Commands);
    }

    [AvaloniaFact]
    public async Task Init_uses_new_destination_even_when_another_repository_is_open()
    {
        var backend = new SampleWorkspace();
        using var vm = new MainWindowViewModel(backend, new MemoryRecent()) { RepositoryPath = SampleWorkspace.RootPath };
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.RepositoryPath = Path.Combine(Path.GetTempPath(), "new-gitgui-fixture");
        await vm.ActionCommand.ExecuteAsync("Init");
        Assert.Equal(vm.RepositoryPath, backend.LastExecutionRoot);
    }

    [AvaloniaFact]
    public async Task Refresh_reloads_selected_diff_when_status_and_filename_are_unchanged()
    {
        var backend = new SampleWorkspace();
        using var vm = new MainWindowViewModel(backend, new MemoryRecent()) { RepositoryPath = SampleWorkspace.RootPath };
        await vm.RefreshCommand.ExecuteAsync(null);
        backend.DiffText = backend.DiffText.Replace("+new behavior", "+latest external edit", StringComparison.Ordinal);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Contains("+latest external edit", vm.Diff);
    }

    [AvaloniaFact]
    public async Task Cancellation_is_enabled_only_when_backend_reports_safe_phase()
    {
        var backend = new SampleWorkspace { PendingOperation = new(), AllowCancellation = false };
        using var vm = new MainWindowViewModel(backend, new MemoryRecent()) { RepositoryPath = SampleWorkspace.RootPath };
        await vm.RefreshCommand.ExecuteAsync(null);
        var stage = vm.ActionCommand.ExecuteAsync("StageAll");
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsBusy); Assert.False(vm.CanCancel);
        vm.CancelCommand.Execute(null); Assert.False(backend.ActiveToken.IsCancellationRequested);
        backend.PendingOperation.SetResult(new(true, "Staged")); await stage;
        backend.PendingOperation = new(); backend.AllowCancellation = true;
        var fetch = vm.ActionCommand.ExecuteAsync("Fetch");
        Dispatcher.UIThread.RunJobs(); Assert.True(vm.CanCancel);
        vm.CancelCommand.Execute(null); await fetch;
        Assert.True(backend.ActiveToken.IsCancellationRequested); Assert.False(vm.IsBusy); Assert.False(vm.CanCancel);
    }

    [AvaloniaFact]
    public async Task Language_switch_updates_existing_window_controls()
    {
        using var vm = new MainWindowViewModel(new SampleWorkspace(), new MemoryRecent());
        var window = new MainWindow(vm); window.Show();
        Assert.Equal("開啟", window.FindControl<Button>("OpenRepositoryButton")!.Content);
        vm.English = true; Dispatcher.UIThread.RunJobs();
        Assert.Equal("Open", window.FindControl<Button>("OpenRepositoryButton")!.Content);
        window.Close(); await Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task Switching_files_clears_old_patch_and_stale_async_completion_cannot_enable_staging()
    {
        var backend = new SampleWorkspace();
        using var vm = new MainWindowViewModel(backend, new MemoryRecent()) { RepositoryPath = SampleWorkspace.RootPath };
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.PatchRows.First(r => r.LineId is null).Selected = true;
        Assert.True(vm.CanApplyPatch);
        var first = new TaskCompletionSource<string>(); var second = new TaskCompletionSource<string>();
        backend.DeferredDiffs[vm.Changes[1].Path] = first;
        backend.DeferredDiffs[vm.Changes[2].Path] = second;
        vm.SelectedChange = vm.Changes[1];
        Assert.True(vm.IsDiffLoading); Assert.False(vm.CanApplyPatch); Assert.Empty(vm.PatchRows); Assert.Empty(vm.Diff);
        await vm.ActionCommand.ExecuteAsync("StagePatch"); Assert.Empty(backend.Executed);
        vm.SelectedChange = vm.Changes[2];
        first.SetResult(backend.DiffText); await Task.Yield(); Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsDiffLoading); Assert.False(vm.CanApplyPatch); Assert.Empty(vm.PatchRows);
        var loaded = new TaskCompletionSource();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsDiffLoading) && !vm.IsDiffLoading) loaded.TrySetResult(); };
        second.SetResult(backend.DiffText.Replace("Workspace.cs", "Graph.cs", StringComparison.Ordinal));
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.CanApplyPatch); Assert.Contains("Graph.cs", vm.Diff);
    }

    [AvaloniaFact]
    public async Task Three_way_conflict_editor_supports_sections_manual_result_save_and_mark_resolved()
    {
        var backend = new SampleWorkspace { HasConflict = true };
        using var vm = new MainWindowViewModel(backend, new MemoryRecent()) { RepositoryPath = SampleWorkspace.RootPath };
        var window = new MainWindow(vm); window.Show(); await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.HasConflict); Assert.True(vm.CanSaveConflict); Assert.Equal("base\n", vm.ConflictBase);
        Assert.Equal(2, vm.ConflictSections.Count);
        if (Environment.GetEnvironmentVariable("GITGUI_SCREENSHOT") is { } screenshot)
            await Capture(window, Path.Combine(Path.GetDirectoryName(screenshot)!, Path.GetFileNameWithoutExtension(screenshot) + "-conflict.png"));
        vm.UseConflictSectionCommand.Execute("Ours"); Assert.Single(vm.ConflictSections);
        vm.UseConflictSectionCommand.Execute("Theirs"); Assert.Empty(vm.ConflictSections);
        Assert.Equal("before\nours one\nbetween\ntheirs two\nafter\n", vm.ConflictResult);
        window.FindControl<TabControl>("DetailTabs")!.SelectedIndex = 3;
        var editor = window.FindControl<CodeEditor>("ConflictResultInput")!;
        Assert.False(editor.IsReadOnly); editor.Text += "manual edit\n"; Dispatcher.UIThread.RunJobs();
        Assert.EndsWith("manual edit\n", vm.ConflictResult);
        await vm.RefreshCommand.ExecuteAsync(null); Assert.EndsWith("manual edit\n", vm.ConflictResult);
        vm.SelectedChange = vm.Changes[1]; vm.SelectedChange = vm.Changes[0];
        Assert.EndsWith("manual edit\n", vm.ConflictResult);
        Assert.Throws<InvalidOperationException>(() => vm.CreateOperation("MarkResolved"));
        await vm.ActionCommand.ExecuteAsync("WriteConflictResult");
        var save = Assert.Single(backend.Executed); Assert.Equal(GitAction.WriteConflictResult, save.Action); Assert.EndsWith("manual edit\n", save.Message);
        await vm.ActionCommand.ExecuteAsync("MarkResolved"); Assert.False(vm.HasConflict);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Binary_conflict_disables_text_save_and_image_preview_owns_native_bitmap()
    {
        using var png = new MemoryStream();
        using (var bitmap = new RenderTargetBitmap(new PixelSize(4, 4), new Vector(96, 96))) bitmap.Save(png);
        var bytes = png.ToArray();
        var backend = new SampleWorkspace { HasConflict = true, Conflict = new("", "", "", "", true), Preview = new(80, 96, bytes, bytes, "PNG") };
        using var vm = new MainWindowViewModel(backend, new MemoryRecent()) { RepositoryPath = SampleWorkspace.RootPath };
        var window = new MainWindow(vm); window.Show(); await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.ConflictBinary); Assert.False(vm.CanSaveConflict); Assert.Throws<InvalidOperationException>(() => vm.CreateOperation("WriteConflictResult"));
        Assert.True(vm.HasImages); Assert.Contains("80", vm.PreviewSummary); Assert.Contains("96", vm.PreviewSummary);
        window.FindControl<TabControl>("DetailTabs")!.SelectedIndex = 4; window.UpdateLayout();
        var previews = window.GetVisualDescendants().OfType<PreviewImage>().ToArray(); Assert.Equal(2, previews.Length);
        Assert.All(previews, preview => Assert.NotNull(((Image)preview.Child!).Source));
        previews[0].Bytes = null; Assert.Null(((Image)previews[0].Child!).Source);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Conflict_save_disables_editor_and_adoption_and_preserves_newer_programmatic_draft()
    {
        var backend = new SampleWorkspace { HasConflict = true };
        using var vm = new MainWindowViewModel(backend, new MemoryRecent()) { RepositoryPath = SampleWorkspace.RootPath };
        var window = new MainWindow(vm); window.Show(); await vm.RefreshCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
        vm.UseConflictSideCommand.Execute("Ours");
        var captured = vm.ConflictResult;
        backend.PendingOperation = new();
        var save = vm.ActionCommand.ExecuteAsync("WriteConflictResult");
        Assert.True(vm.IsBusy); Assert.False(vm.CanSaveConflict);
        Assert.False(window.FindControl<CodeEditor>("ConflictResultInput")!.IsEnabled);
        vm.UseConflictSideCommand.Execute("Theirs"); vm.UseConflictSectionCommand.Execute("Both");
        Assert.Equal(captured, vm.ConflictResult);
        var newer = captured + "late draft edit\n";
        vm.ConflictResult = newer;
        backend.PendingOperation.SetResult(new(true, "Saved captured result")); await save;
        Assert.Equal(captured, Assert.Single(backend.Executed).Message);
        Assert.Equal(newer, vm.ConflictResult); Assert.True(vm.ConflictDirty);
        backend.PendingOperation = null;
        await vm.ActionCommand.ExecuteAsync("WriteConflictResult");
        Assert.Equal(newer, vm.ConflictResult); Assert.False(vm.ConflictDirty);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Conflict_loading_disables_editor_and_side_or_section_adoption_until_current_content_arrives()
    {
        var backend = new SampleWorkspace { HasConflict = true };
        using var vm = new MainWindowViewModel(backend, new MemoryRecent()) { RepositoryPath = SampleWorkspace.RootPath };
        var window = new MainWindow(vm); window.Show(); await vm.RefreshCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
        if (vm.RefreshCommand.ExecutionTask is { } initialRefresh) await initialRefresh.WaitAsync(TimeSpan.FromSeconds(5));
        backend.DeferredOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(window.Activate); Dispatcher.UIThread.RunJobs();
        Assert.NotNull(backend.DeferredOpenState); Assert.True(vm.IsBusy);
        var activationRefresh = vm.RefreshCommand.ExecutionTask;
        Assert.NotNull(activationRefresh); Assert.False(activationRefresh.IsCompleted);
        backend.DeferredOpen.SetResult(backend.DeferredOpenState); backend.DeferredOpen = null;
        // Pumping dispatcher jobs starts an activation refresh; fence its real asynchronous completion before testing conflict-only loading.
        await activationRefresh.WaitAsync(TimeSpan.FromSeconds(5));
        vm.SelectedChange = vm.Changes[1];
        backend.DeferredConflict = new();
        vm.SelectedChange = vm.Changes[0];
        Assert.False(vm.IsBusy); Assert.True(vm.ConflictLoading); Assert.False(vm.CanSaveConflict);
        Assert.False(window.FindControl<CodeEditor>("ConflictResultInput")!.IsEnabled);
        vm.UseConflictSideCommand.Execute("Ours"); vm.UseConflictSectionCommand.Execute("Both");
        Assert.Empty(vm.ConflictResult);
        var loaded = new TaskCompletionSource();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.ConflictLoading) && !vm.ConflictLoading) loaded.TrySetResult(); };
        backend.DeferredConflict.SetResult(backend.Conflict); await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(backend.Conflict.Result, vm.ConflictResult); Assert.True(vm.CanSaveConflict);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Workspace_selection_invalidates_delayed_commit_details_and_keeps_current_working_diff()
    {
        var backend = new SampleWorkspace();
        using var vm = new MainWindowViewModel(backend, new MemoryRecent()) { RepositoryPath = SampleWorkspace.RootPath };
        await vm.RefreshCommand.ExecuteAsync(null);
        backend.DeferredCommit = new();
        var commit = vm.Commits[0]; vm.SelectedCommit = commit;
        backend.DiffText = backend.DiffText.Replace("src/Workspace.cs", "docs/README.md", StringComparison.Ordinal);
        vm.SelectedChange = vm.Changes[1];
        Assert.Null(vm.SelectedCommit); Assert.False(vm.IsHistoryDiff);
        backend.DeferredCommit.SetResult(new(commit, "stale history", [new("old-history.txt", "M", ".", [])]));
        await Task.Yield(); Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.SelectedCommit); Assert.False(vm.IsHistoryDiff); Assert.Empty(vm.CommitFiles);
        Assert.Contains("docs/README.md", vm.Diff); Assert.DoesNotContain("old-history.txt", vm.Diff);
    }

    private static CommitGraphControl Graph()
    {
        var graph = new CommitGraphControl();
        graph.Bind(CommitGraphControl.CommitsProperty, new Binding("Commits"));
        graph.Bind(CommitGraphControl.SelectedCommitProperty, new Binding("SelectedCommit") { Mode = BindingMode.TwoWay });
        return graph;
    }
    private static async Task Capture(Window window, string path)
    {
        Dispatcher.UIThread.RunJobs();
        foreach (var graph in window.GetVisualDescendants().OfType<CommitGraphControl>()) await graph.LayoutReady;
        window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); frame.Save(path);
    }
    private static SnapshotInfo Snapshot(string id) => new(id, SampleWorkspace.RootPath, "Merge feature/graph", DateTimeOffset.Parse("2026-10-23T14:25:00+08:00"), "refs/gitgui/snapshots/" + id, "abc", 4);

    private sealed class SampleWorkspace : IWorkspaceService
    {
        public static string RootPath => Path.Combine(Path.GetTempPath(), "GITGUI-studio-fixture");
        public event EventHandler<CommandEntry>? CommandExecuted;
        public readonly List<GitOperation> Executed = [];
        public LogFilter? LastFilter { get; private set; }
        public string? LastExecutionRoot { get; private set; }
        public OperationOutcome NextOutcome { get; set; } = new(true, "Done");
        public TaskCompletionSource<OperationOutcome>? PendingOperation { get; set; }
        public TaskCompletionSource<WorkspaceState>? DeferredOpen { get; set; }
        public WorkspaceState? DeferredOpenState { get; private set; }
        public bool AllowCancellation { get; set; }
        public CancellationToken ActiveToken { get; private set; }
        public bool HasConflict { get; set; }
        public FileChange[]? FilesOverride { get; set; }
        public ConflictContent Conflict { get; set; } = new("base\n", "ours one\nours two\n", "theirs one\ntheirs two\n", "before\n<<<<<<< HEAD\nours one\n=======\ntheirs one\n>>>>>>> topic\nbetween\n<<<<<<< HEAD\nours two\n=======\ntheirs two\n>>>>>>> topic\nafter\n", false);
        public FilePreview Preview { get; set; } = new(80, 96, null, null, null);
        public Dictionary<string, TaskCompletionSource<string>> DeferredDiffs { get; } = [];
        public TaskCompletionSource<ConflictContent>? DeferredConflict { get; set; }
        public TaskCompletionSource<CommitDetails>? DeferredCommit { get; set; }
        public string DiffText { get; set; } = "diff --git a/src/Workspace.cs b/src/Workspace.cs\n--- a/src/Workspace.cs\n+++ b/src/Workspace.cs\n@@ -1,3 +1,3 @@\n context\n-old behavior\n+new behavior\n end\n";
        public void Emit(CommandEntry entry) => CommandExecuted?.Invoke(this, entry);
        public Task<WorkspaceState> OpenAsync(string path, LogFilter filter, CancellationToken ct)
        {
            LastFilter = filter;
            FileChange[] files = FilesOverride ?? [new("src/Workspace.cs", "M", "M", []), new("docs/README.md", ".", "M", []), new("src/Graph.cs", "A", ".", []), new("notes/new.txt", "?", "?", [])];
            if (HasConflict) files[0] = new(files[0].Path, "U", "U", []);
            CommitInfo[] commits = [new("a1b2c3d4e5", "Complete safe workspace operations", "Will Lin", DateTimeOffset.Now, ["f2a3b4c5", "b3c4d5e6"]), new("f2a3b4c5", "Build virtualized commit graph", "Will Lin", DateTimeOffset.Now.AddHours(-1), ["c4d5e6f7"]), new("b3c4d5e6", "Protect work with reversible snapshots", "Alex Chen", DateTimeOffset.Now.AddHours(-2), ["c4d5e6f7"]), new("c4d5e6f7", "Initialize cross-platform Git workspace", "Will Lin", DateTimeOffset.Now.AddDays(-1), [])];
            RefInfo[] refs = [new("main", "refs/heads/main", commits[0].Hash, "origin/main", "↑ 2  ↓ 0", true), new("feature/graph", "refs/heads/feature/graph", commits[1].Hash), new("origin/main", "refs/remotes/origin/main", commits[2].Hash)];
            var state = new WorkspaceState(path, new("main", commits[0].Hash, files), commits, refs, [new("origin", "https://example.org/team/project.git")], HasConflict ? new("merge", true) : InProgressState.None, true, []);
            if (DeferredOpen is { } pending) { DeferredOpenState = state; return pending.Task; }
            return Task.FromResult(state);
        }
        public Task<string> GetDiffAsync(string root, DiffRequest request, CancellationToken ct) => DeferredDiffs.TryGetValue(request.Path, out var deferred) ? deferred.Task : Task.FromResult(DiffText);
        public Task<ConflictContent> GetConflictAsync(string root, string path, CancellationToken ct) => DeferredConflict?.Task ?? Task.FromResult(Conflict);
        public Task<FilePreview> GetPreviewAsync(string root, DiffRequest request, CancellationToken ct) => Task.FromResult(Preview);
        public Task<CommitDetails> GetCommitAsync(string root, string commit, CancellationToken ct) => DeferredCommit?.Task ?? Task.FromResult(new CommitDetails(new(commit, "Complete safe workspace operations", "Will Lin", DateTimeOffset.Now, []), "Complete safe workspace operations\n\nAdd reversible Git actions and clear operation output.", [new("src/Workspace.cs", "M", ".", []), new("src/Graph.cs", "A", ".", [])]));
        public Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsAsync(string root, CancellationToken ct) => Task.FromResult<IReadOnlyList<SnapshotInfo>>([Snapshot("before-merge"), Snapshot("before-amend")]);
        public Task TrustAsync(string root, CancellationToken ct) => Task.CompletedTask;
        public Task<OperationOutcome> ExecuteAsync(string root, GitOperation operation, IProgress<OperationProgress>? progress, CancellationToken ct)
        {
            LastExecutionRoot = root; Executed.Add(operation); ActiveToken = ct;
            if (operation.Action == GitAction.WriteConflictResult) Conflict = Conflict with { Result = operation.Message! };
            if (operation.Action == GitAction.MarkResolved) HasConflict = false;
            if (PendingOperation is not null)
            {
                progress?.Report(new("Downloading", 35, AllowCancellation));
                return PendingOperation.Task.WaitAsync(ct);
            }
            return Task.FromResult(NextOutcome);
        }
    }
    private sealed class MemoryRecent : IRecentRepositories
    {
        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task RememberAsync(string path, CancellationToken ct) => Task.CompletedTask;
    }
}
