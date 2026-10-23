using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using GitGui.Core;

namespace GitGui.UI;

public partial class MainWindow : Window, IWorkspaceInteraction
{
    private MainWindowViewModel? viewModel;
    private IGitRuntimeSettings? gitSettings;
    private AppearanceManager? appearance;
    private IWorkspaceService? workspace;
    private ITerminalService? terminalService;
    private TerminalPanel? terminalPanel;
    private Window? terminalWindow;
    private AdvancedWindow? advancedWindow;
    private IRepositoryToolsService? repositoryTools;
    private RepositoryToolsWindow? toolsWindow;
    private ShortcutManager shortcuts = new();
    private Control? historyGraph;
    private FileSystemWatcher? worktreeWatcher;
    private FileSystemWatcher? gitWatcher;
    private FileSystemWatcher? commonWatcher;
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly RepositoryRefreshQueue refreshQueue = new();
    private string? watchedGitDirectory;
    private string? watchedCommonDirectory;
    private bool closed;
    private int openDialogs;
    private bool skipNextActivationRefresh;
    public MainWindow()
    {
        if (!Application.Current!.Resources.ContainsKey("CanvasBrush")) _ = new AppearanceManager();
        AvaloniaXamlLoader.Load(this);
    }
    public MainWindow(MainWindowViewModel vm, Control? graph = null, IGitRuntimeSettings? gitSettings = null, AppearanceManager? appearance = null,
        IWorkspaceService? workspace = null, ITerminalService? terminalService = null, IRepositoryToolsService? repositoryTools = null, ShortcutManager? shortcuts = null) : this()
    {
        viewModel = vm;
        this.gitSettings = gitSettings;
        this.appearance = appearance ?? new AppearanceManager();
        this.workspace = workspace; this.terminalService = terminalService;
        this.repositoryTools = repositoryTools;
        this.shortcuts = shortcuts ?? new();
        vm.English = this.shortcuts.Culture == "en";
        this.shortcuts.CultureChanged += CultureChanged;
        DataContext = vm;
        vm.Feedback.Navigate = HandleSuggestedActionAsync;
        vm.ConfirmSkipHooks = async () => (await ConfirmAsync(new(vm.L["RetryWithoutHooks"], vm.L["RetryWithoutHooksDetails"], RiskLevel.High), CancellationToken.None)).Accepted;
        if (graph is null)
        {
            var list = new ListBox();
            list.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.Commits)));
            list.Bind(SelectingItemsControlSelectedItem(), new Binding(nameof(vm.SelectedCommit)) { Mode = BindingMode.TwoWay });
            graph = list;
        }
        historyGraph = graph;
        foreach (var name in new[] { "ChangesList", "StagedChangesList", "CommitFilesList" }) UiAccessibility.NameFileItems(this.FindControl<ListBox>(name)!, vm.L);
        vm.L.Bind(graph, Avalonia.Automation.AutomationProperties.NameProperty, "GraphName");
        this.FindControl<ContentControl>("GraphHost")!.Content = new ScrollViewer { Content = graph, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        this.shortcuts.Attach(this, "Main", vm.L, ShortcutManager.Definitions.Where(d => d.Scope == "Main").Select(d => new UiCommand(d.Id[5..], () => ExecuteUiCommand(d.Id[5..]), () => CanExecuteUiCommand(d.Id[5..]))).ToArray());
        Closed += (_, _) => this.shortcuts.CultureChanged -= CultureChanged;
        Opened += async (_, _) => await vm.InitializeCommand.ExecuteAsync(null);
        Closed += (_, _) => { closed = true; debounce.Stop(); worktreeWatcher?.Dispose(); gitWatcher?.Dispose(); commonWatcher?.Dispose(); vm.RepositoryOpened -= RepositoryOpened; vm.PropertyChanged -= ViewModelChanged; vm.Dispose(); };
        Activated += async (_, _) =>
        {
            if (openDialogs > 0) return;
            if (skipNextActivationRefresh) { skipNextActivationRefresh = false; return; }
            if (vm.HasRepository && !vm.IsBusy && !closed) await vm.RefreshCommand.ExecuteAsync(null);
        };
        vm.RepositoryOpened += RepositoryOpened;
        vm.PropertyChanged += ViewModelChanged;
        ExpandActiveHistoryFilters();
        Closing += (_, args) =>
        {
            if (vm.IsBusy || advancedWindow?.ViewModel.IsWriting == true || toolsWindow?.ViewModel.IsWriting == true)
            { args.Cancel = true; vm.Notice = vm.L["ToolsWait"]; }
        };
        debounce.Tick += async (_, _) =>
        {
            debounce.Stop();
            if (closed || !vm.HasRepository) return;
            if (!refreshQueue.Take(Environment.TickCount64, vm.IsBusy, out var kinds, out var delay))
            { if (delay > 0) { debounce.Interval = TimeSpan.FromMilliseconds(delay); debounce.Start(); } return; }
            if ((kinds & (RepositoryRefreshKind.History | RepositoryRefreshKind.Configuration)) != 0) await vm.RefreshCommand.ExecuteAsync(null);
            else await vm.RefreshStatusAsync();
        };
    }
    private void CultureChanged(object? sender, EventArgs args) { if (viewModel is { } vm) vm.English = shortcuts.Culture == "en"; }
    private bool CanExecuteUiCommand(string action)
    {
        if (viewModel is not { } vm || closed || openDialogs > 0) return false;
        if (action == "Cancel") return vm.CanCancel;
        if (action is "StagePatch" or "UnstagePatch") return vm.CanApplyPatch;
        if (action is "WriteConflictResult" or "UseOursFile" or "UseTheirsFile" or "UseBothFile" or "SectionOurs" or "SectionTheirs" or "SectionBoth") return vm.CanSaveConflict;
        if (action is "Palette" or "ShortcutSettings" or "Appearance") return true;
        if (action is "Refresh" or "Browse" or "Open" or "GitSettings" or "RepositoryActions" or "Init" or "Clone") return vm.CanConfigure;
        if (action.StartsWith("Focus", StringComparison.Ordinal) || action is "NextPane" or "PreviousPane") return vm.HasRepository;
        return vm.CanOperate;
    }
    private int focusedPane;
    private async Task ExecuteUiCommand(string action)
    {
        if (viewModel is not { } vm || !CanExecuteUiCommand(action)) return;
        var args = new RoutedEventArgs();
        switch (action)
        {
            case "Palette": await ShowCommandPaletteAsync(); break;
            case "ShortcutSettings": await ShowShortcutSettingsAsync(); break;
            case "Browse": BrowseClick(this, args); break;
            case "Open": case "Refresh": await vm.RefreshCommand.ExecuteAsync(null); break;
            case "RepositoryActions": RepositoryActionsClick(this, args); break;
            case "GitSettings": GitSettingsClick(this, args); break;
            case "Appearance": AppearanceClick(this, args); break;
            case "AdvancedOpen": AdvancedClick(this, args); break;
            case "ToolsOpen": RepositoryToolsClick(this, args); break;
            case "Terminal": TerminalClick(this, args); break;
            case "Undo": await vm.UndoCommand.ExecuteAsync(null); break;
            case "Redo": await vm.RedoCommand.ExecuteAsync(null); break;
            case "Cancel": vm.CancelCommand.Execute(null); break;
            case "SearchHistory": await vm.SearchHistoryCommand.ExecuteAsync(null); break;
            case "LoadMore": await vm.LoadMoreCommand.ExecuteAsync(null); break;
            case "LoadLargeDiff": await vm.LoadLargeDiffCommand.ExecuteAsync(null); break;
            case "UseOursFile": vm.UseConflictSideCommand.Execute("Ours"); break;
            case "UseTheirsFile": vm.UseConflictSideCommand.Execute("Theirs"); break;
            case "UseBothFile": vm.UseConflictSideCommand.Execute("Both"); break;
            case "SectionOurs": vm.UseConflictSectionCommand.Execute("Ours"); break;
            case "SectionTheirs": vm.UseConflictSectionCommand.Execute("Theirs"); break;
            case "SectionBoth": vm.UseConflictSectionCommand.Execute("Both"); break;
            case "FocusFiles": FocusPane(0); break;
            case "FocusHistory": FocusPane(1); break;
            case "FocusDiff": FocusPane(2); break;
            case "FocusCommit": FocusPane(3); break;
            case "NextPane": FocusPane((focusedPane + 1) % 4); break;
            case "PreviousPane": FocusPane((focusedPane + 3) % 4); break;
            default: await vm.ActionCommand.ExecuteAsync(action); break;
        }
    }
    private void FocusPane(int index)
    {
        focusedPane = index;
        this.FindControl<TabControl>("WorkspaceTabs")!.SelectedIndex = index == 1 ? 1 : 0;
        if (index == 0)
        {
            var list = this.FindControl<ListBox>(viewModel?.ShowStaged == true ? "StagedChangesList" : "ChangesList")!;
            list.Focusable = true; list.Focus();
        }
        else if (index == 1) historyGraph?.Focus();
        else if (index == 2) { this.FindControl<TabControl>("DetailTabs")!.SelectedIndex = 0; this.FindControl<DiffEditor>("DiffViewer")!.FocusDocument(); }
        else this.FindControl<TextBox>("CommitMessageInput")!.Focus();
    }
    private async Task ShowShortcutSettingsAsync()
    {
        if (viewModel is not { } vm) return;
        var focus = FocusManager?.GetFocusedElement(); openDialogs++;
        try { await new ShortcutSettingsWindow(shortcuts, vm.L).ShowDialog(this); }
        finally { openDialogs--; focus?.Focus(); skipNextActivationRefresh = true; }
    }
    private async Task ShowCommandPaletteAsync()
    {
        openDialogs++;
        try { await shortcuts.ShowPaletteAsync(this); }
        finally { openDialogs--; skipNextActivationRefresh = true; }
    }
    private async void ShortcutSettingsClick(object? sender, RoutedEventArgs args) => await ShowShortcutSettingsAsync();
    internal async Task HandleSuggestedActionAsync(GitSuggestedAction action)
    {
        if (viewModel is not { } vm) return;
        if (action is GitSuggestedAction.Fetch or GitSuggestedAction.Pull)
        { if (vm.CanOperate) await vm.ActionCommand.ExecuteAsync(action.ToString()); }
        else if (action == GitSuggestedAction.ResolveConflicts)
        { this.FindControl<TabControl>("WorkspaceTabs")!.SelectedIndex = 0; this.FindControl<TabControl>("DetailTabs")!.SelectedIndex = vm.HasConflict ? 3 : 0; Activate(); }
        else if (action == GitSuggestedAction.Stash)
        { AdvancedClick(this, new()); if (advancedWindow is { } advanced) advanced.FindControl<TabControl>("AdvancedTabs")!.SelectedIndex = 0; }
        else if (action == GitSuggestedAction.ShowOutput) this.FindControl<Expander>("MainOperationPanel")!.IsExpanded = true;
        else await OperationFeedback.ShowHelpAsync(this, vm.L, action);
    }
    private static AvaloniaProperty SelectingItemsControlSelectedItem() => Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty;
    private void RepositoryToolsClick(object? sender, RoutedEventArgs args)
    {
        if (toolsWindow is not null) { toolsWindow.Activate(); return; }
        if (viewModel is not { CanOperate: true } vm || workspace is null || repositoryTools is null) return;
        advancedWindow?.Close();
        if (advancedWindow is not null) { vm.Notice = vm.L["AdvancedWait"]; return; }
        var model = new RepositoryToolsWindowViewModel(workspace, repositoryTools, vm.Root, vm.L);
        var window = toolsWindow = new RepositoryToolsWindow(model, shortcuts);
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(model.IsWriting)) vm.ExternalWriteActive = model.IsWriting; };
        model.WorkspaceChanged += async (_, _) => { if (!closed && !vm.IsBusy) await vm.RefreshCommand.ExecuteAsync(null); };
        window.Closed += async (_, _) => { toolsWindow = null; vm.ExternalWriteActive = false; if (!closed && !vm.IsBusy) await vm.RefreshCommand.ExecuteAsync(null); };
        window.Show(this);
    }
    private void AdvancedClick(object? sender, RoutedEventArgs args)
    {
        if (viewModel is not { CanOperate: true } vm || workspace is not IAdvancedWorkspaceService advanced) return;
        if (advancedWindow is not null) { advancedWindow.Activate(); return; }
        toolsWindow?.Close();
        if (toolsWindow is not null) { vm.Notice = vm.L["ToolsWait"]; return; }
        var model = new AdvancedWindowViewModel(workspace, advanced, vm.Root,
            vm.SelectedCommitFile?.Path ?? vm.SelectedChange?.Path, vm.SelectedCommit?.Hash, vm.L);
        var window = advancedWindow = new AdvancedWindow(model, shortcuts);
        model.WorkspaceChanged += async (_, _) => { if (!closed && !vm.IsBusy) await vm.RefreshCommand.ExecuteAsync(null); };
        window.Closed += async (_, _) => { advancedWindow = null; if (!closed && !vm.IsBusy) await vm.RefreshCommand.ExecuteAsync(null); };
        window.Show(this);
    }
    private async void TerminalClick(object? sender, RoutedEventArgs args)
    {
        if (viewModel is not { CanOperate: true } vm || terminalService is null) return;
        if (terminalWindow is not null) { terminalWindow.Activate(); return; }
        // Close the advanced writer before leasing this repository for a shell.
        advancedWindow?.Close();
        if (advancedWindow is not null) { vm.Notice = vm.L["AdvancedWait"]; return; }
        toolsWindow?.Close();
        if (toolsWindow is not null) { vm.Notice = vm.L["ToolsWait"]; return; }
        var panel = terminalPanel = new TerminalPanel(terminalService, vm.L);
        var window = terminalWindow = new Window { Title = "GITGUI · Terminal", Width = 1040, Height = 680,
            MinWidth = 640, MinHeight = 400, Content = new Border { Padding = new Thickness(16), Child = panel } };
        AppearanceManager.StyleWindow(window);
        window.Bind(TitleProperty, new Binding("[Terminal]") { Source = vm.L });
        panel.RunningChanged += (_, _) => vm.TerminalActive = panel.IsRunning;
        window.Closed += async (_, _) =>
        {
            await panel.CloseAsync(); terminalPanel = null; terminalWindow = null; vm.TerminalActive = false;
            if (!closed && !vm.IsBusy) await vm.RefreshCommand.ExecuteAsync(null);
        };
        window.Show(this);
        await panel.OpenAsync(vm.Root);
    }
    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.AuthorFilter) or nameof(MainWindowViewModel.PathFilter) or nameof(MainWindowViewModel.SinceFilter) or nameof(MainWindowViewModel.UntilFilter))
            ExpandActiveHistoryFilters();
        if (e.PropertyName == nameof(MainWindowViewModel.English) && viewModel is { } localized)
            try { shortcuts.SetCulture(localized.English ? "en" : "zh-TW"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { localized.Notice = localized.L["ShortcutSaveFailed"]; }
        if (toolsWindow is { } tools && viewModel is { } main && e.PropertyName is nameof(MainWindowViewModel.IsBusy) or nameof(MainWindowViewModel.TerminalActive))
            tools.ViewModel.IsBlocked = main.IsBusy || main.TerminalActive;
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedCommit) && viewModel?.SelectedCommit is not null)
            this.FindControl<TabControl>("DetailTabs")!.SelectedIndex = 2;
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedChange) && viewModel?.SelectedChange is not null)
        {
            this.FindControl<TabControl>("DetailTabs")!.SelectedIndex = viewModel.HasConflict ? 3 : 0;
            this.FindControl<ListBox>(viewModel.ShowStaged ? "StagedChangesList" : "ChangesList")!.SelectedItem = viewModel.SelectedChange;
        }
        if (e.PropertyName == nameof(MainWindowViewModel.ShowStaged) && viewModel is not null)
            this.FindControl<TabControl>("ChangeTabs")!.SelectedIndex = viewModel.ShowStaged ? 1 : 0;
        if (e.PropertyName == nameof(MainWindowViewModel.HasConflict) && viewModel?.HasConflict == true)
            this.FindControl<TabControl>("DetailTabs")!.SelectedIndex = 3;
    }
    private void ExpandActiveHistoryFilters()
    {
        if (viewModel is { } vm && (!string.IsNullOrWhiteSpace(vm.AuthorFilter) || !string.IsNullOrWhiteSpace(vm.PathFilter) ||
            !string.IsNullOrWhiteSpace(vm.SinceFilter) || !string.IsNullOrWhiteSpace(vm.UntilFilter)))
            this.FindControl<Expander>("HistoryAdvancedFilters")!.IsExpanded = true;
    }
    private void HistoryFiltersExpanding(object? sender, CancelRoutedEventArgs args)
    {
        if (sender is not Expander filters || filters.Content is not null || viewModel is not { } vm) return;
        // Create the editor templates only on first expansion. Keep the same controls
        // thereafter so collapsed sections and tab switches retain input/selection state.
        var grid = new Grid { ColumnDefinitions = new("*,*"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 6, RowSpacing = 6, Margin = new(0, 6, 0, 0) };
        AddFilter(nameof(vm.AuthorFilter), "Author", 0, 0);
        AddFilter(nameof(vm.PathFilter), "Path", 0, 1);
        AddFilter(nameof(vm.SinceFilter), "Since", 1, 0);
        AddFilter(nameof(vm.UntilFilter), "Until", 1, 1);
        filters.Content = grid;
        void AddFilter(string property, string label, int row, int column)
        {
            var input = new TextBox { Name = "History" + property };
            input.Bind(TextBox.TextProperty, new Binding(property) { Source = vm, Mode = BindingMode.TwoWay });
            vm.L.Bind(input, TextBox.WatermarkProperty, label);
            vm.L.Bind(input, Avalonia.Automation.AutomationProperties.NameProperty, label);
            Grid.SetRow(input, row); Grid.SetColumn(input, column); grid.Children.Add(input);
        }
    }
    private void RepositoryOpened(object? sender, EventArgs args)
    {
        worktreeWatcher?.Dispose(); gitWatcher?.Dispose(); commonWatcher?.Dispose();
        watchedGitDirectory = watchedCommonDirectory = null;
        if (viewModel is null || !Directory.Exists(viewModel.Root)) return;
        try
        {
            var dotGit = Path.Combine(viewModel.Root, ".git");
            if (Directory.Exists(dotGit)) watchedGitDirectory = watchedCommonDirectory = dotGit;
            if (File.Exists(dotGit))
            {
                var pointer = File.ReadAllText(dotGit).Trim();
                if (pointer.StartsWith("gitdir: ", StringComparison.Ordinal))
                {
                    var directory = Path.GetFullPath(Path.Combine(viewModel.Root, pointer[8..]));
                    if (Directory.Exists(directory))
                    {
                        watchedGitDirectory = directory;
                        gitWatcher = Watch(directory);
                        var commonPointer = Path.Combine(directory, "commondir");
                        if (File.Exists(commonPointer))
                        {
                            var common = Path.GetFullPath(Path.Combine(directory, File.ReadAllText(commonPointer).Trim()));
                            if (Directory.Exists(common)) { watchedCommonDirectory = common; commonWatcher = Watch(common); }
                        }
                    }
                }
            }
            worktreeWatcher = Watch(viewModel.Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { viewModel.Notice += " · " + ex.Message; }
    }
    private FileSystemWatcher Watch(string root)
    {
        var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
        FileSystemEventHandler changed = (_, e) => ScheduleRefresh(e.FullPath);
        watcher.Changed += changed; watcher.Created += changed; watcher.Deleted += changed;
        watcher.Renamed += (_, e) => { ScheduleRefresh(e.OldFullPath); ScheduleRefresh(e.FullPath); };
        watcher.Error += (_, _) => ScheduleRefresh(null); watcher.EnableRaisingEvents = true;
        return watcher;
    }
    private void ScheduleRefresh(string? path)
    {
        var kind = path is null || viewModel is null ? RepositoryRefreshKind.All :
            RepositoryRefreshQueue.Classify(viewModel.Root, watchedGitDirectory, watchedCommonDirectory, path);
        if (!refreshQueue.Add(kind, Environment.TickCount64)) return;
        Dispatcher.UIThread.Post(() => { if (closed) return; debounce.Stop(); debounce.Interval = TimeSpan.FromMilliseconds(300); debounce.Start(); });
    }
    private async void BrowseClick(object? sender, RoutedEventArgs args)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = viewModel?.L["RepositoryPath"], AllowMultiple = false });
        if (folders.Count == 0 || viewModel is null || folders[0].TryGetLocalPath() is not { } path) return;
        viewModel.RepositoryPath = path; await viewModel.RefreshCommand.ExecuteAsync(null);
    }
    private async void RecentSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (viewModel is not null && args.AddedItems.Count > 0 && args.AddedItems[0] is string path && !viewModel.IsBusy)
            await viewModel.OpenRecentCommand.ExecuteAsync(path);
    }
    private void ChangesSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (viewModel is null || sender is not ListBox list) return;
        if (args.AddedItems.Count > 0)
        {
            viewModel.ShowStaged = list.Name == "StagedChangesList";
            viewModel.SelectedChange = args.AddedItems[0] as FileChange;
        }
        viewModel.SelectedFiles.Clear();
        if (list.SelectedItems is not null) foreach (var item in list.SelectedItems.OfType<FileChange>()) viewModel.SelectedFiles.Add(item);
    }
    private void ChangeTabSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (viewModel is not null && sender is TabControl tabs && ReferenceEquals(args.Source, tabs)) viewModel.ShowStaged = tabs.SelectedIndex == 1;
    }
    private async void BranchDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (viewModel?.SelectedRef is not null) await viewModel.ActionCommand.ExecuteAsync("SwitchBranch");
    }
    private async void CopyCommandsClick(object? sender, RoutedEventArgs args)
    {
        if (viewModel is not null && Clipboard is not null) await Clipboard.SetTextAsync(viewModel.Commands);
    }
    private async void TrustClick(object? sender, RoutedEventArgs args)
    {
        if (viewModel is null) return;
        await viewModel.ActionCommand.ExecuteAsync("Trust");
    }
    private async void RepositoryActionsClick(object? sender, RoutedEventArgs args)
    {
        if (viewModel is null) return;
        var vm = viewModel;
        var dialog = CreateDialog(vm.L["RepositoryActions"], 620);
        var path = new TextBox { Text = vm.RepositoryPath, Watermark = vm.L["Destination"] };
        var url = new TextBox { Text = vm.CloneUrl, Watermark = vm.L["Url"] };
        var stack = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        stack.Children.Add(Heading(vm.L["RepositoryActions"])); stack.Children.Add(Label(vm.L["Destination"])); stack.Children.Add(path); stack.Children.Add(Label(vm.L["Url"])); stack.Children.Add(url);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        var init = new ActionButton { Icon = "Plus", Content = vm.L["Init"], Margin = new Thickness(0, 0, 8, 0) };
        var clone = new ActionButton { Icon = "Repository", Content = vm.L["Clone"], Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new ActionButton { Icon = "Close", Content = vm.L["Cancel"] };
        init.Click += (_, _) => dialog.Close("Init"); clone.Click += (_, _) => dialog.Close("Clone"); cancel.Click += (_, _) => dialog.Close(null);
        buttons.Children.Add(init); buttons.Children.Add(clone); buttons.Children.Add(cancel); stack.Children.Add(buttons); dialog.Content = stack;
        var action = await dialog.ShowDialog<string?>(this);
        if (action is null) return;
        vm.RepositoryPath = path.Text ?? ""; vm.CloneUrl = url.Text ?? "";
        await vm.ActionCommand.ExecuteAsync(action);
    }

    private async void GitSettingsClick(object? sender, RoutedEventArgs args)
    {
        if (viewModel is not { CanConfigure: true } vm || gitSettings is null) return;
        vm.IsBusy = true; openDialogs++;
        try
        {
            var result = await new GitSettingsWindow(gitSettings, vm.L).ShowDialog<GitRuntimeInfo?>(this);
            if (result is not null) vm.Notice = result.Version + " · " + result.Executable;
        }
        finally { vm.IsBusy = false; openDialogs--; skipNextActivationRefresh = true; }
    }

    private async void AppearanceClick(object? sender, RoutedEventArgs args)
    {
        if (appearance is null || viewModel is null) return;
        openDialogs++;
        try { await new AppearanceWindow(appearance, viewModel.L).ShowDialog(this); }
        finally { openDialogs--; skipNextActivationRefresh = true; }
    }

    public Task<ConfirmationAnswer> ConfirmAsync(Confirmation confirmation, CancellationToken ct)
        => OnUiAsync(() => ShowConfirmationAsync(confirmation, ct));
    private async Task<ConfirmationAnswer> ShowConfirmationAsync(Confirmation confirmation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var l = viewModel?.L ?? new Localization();
        var dialog = CreateDialog(confirmation.Title, 660);
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(24) };
        panel.Children.Add(Heading(confirmation.Title));
        var risk = new TextBlock { Text = $"{l["Safety"]}: {l["Risk" + confirmation.Risk]}", FontSize = 12 };
        AppearanceManager.Bind(risk, TextBlock.ForegroundProperty, "WarningBrush"); panel.Children.Add(risk);
        panel.Children.Add(new ScrollViewer { MaxHeight = 360, Content = new TextBlock { Text = confirmation.Details, TextWrapping = TextWrapping.Wrap } });
        TextBox? typed = null;
        if (confirmation.RequiredText is { } required)
        {
            panel.Children.Add(Label(l["RequiredText"] + " · " + required));
            typed = new TextBox { Name = "ConfirmationTextInput" };
            panel.Children.Add(typed);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        var reject = new ActionButton { Icon = "Close", Content = l["Cancel"], Name = "ConfirmationCancelButton" };
        var accept = new ActionButton { Icon = "Shield", Content = l["Confirm"], Name = "ConfirmationAcceptButton", IsEnabled = confirmation.RequiredText is null };
        accept.Classes.Add("destructive");
        reject.Click += (_, _) => dialog.Close(new ConfirmationAnswer(false));
        accept.Click += (_, _) => { if (confirmation.RequiredText is null || typed?.Text == confirmation.RequiredText) dialog.Close(new ConfirmationAnswer(true, typed?.Text)); };
        if (typed is not null) typed.TextChanged += (_, _) => accept.IsEnabled = typed.Text == confirmation.RequiredText;
        buttons.Children.Add(reject); buttons.Children.Add(accept); panel.Children.Add(buttons); dialog.Content = panel;
        dialog.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = new RelayCommand(() => dialog.Close(new ConfirmationAnswer(false))) });
        dialog.Opened += (_, _) => { if (typed is not null) typed.Focus(); else reject.Focus(); };
        using var registration = ct.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(new ConfirmationAnswer(false))));
        return await dialog.ShowDialog<ConfirmationAnswer?>(this) ?? new(false);
    }
    public Task<string?> CredentialAsync(string prompt, CancellationToken ct)
        => OnUiAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var l = viewModel?.L ?? new Localization();
            var dialog = CreateDialog(l["Credential"], 560);
            var input = new TextBox { PasswordChar = '●', Name = "CredentialInput" };
            var panel = new StackPanel { Spacing = 14, Margin = new Thickness(24) };
            panel.Children.Add(Heading(l["Credential"])); panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap }); panel.Children.Add(input);
            panel.Children.Add(Label(l["CredentialHint"]));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
            var cancel = new ActionButton { Icon = "Close", Content = l["Cancel"] }; var accept = new ActionButton { Icon = "Check", Content = l["Continue"], Name = "CredentialAcceptButton" };
            accept.Classes.Add("primary");
            cancel.Click += (_, _) => dialog.Close(null); accept.Click += (_, _) => dialog.Close(input.Text);
            buttons.Children.Add(cancel); buttons.Children.Add(accept); panel.Children.Add(buttons); dialog.Content = panel;
            dialog.Opened += (_, _) => input.Focus();
            using var registration = ct.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(null)));
            var result = await dialog.ShowDialog<string?>(this); input.Text = ""; return result;
        });
    private static async Task<T> OnUiAsync<T>(Func<Task<T>> action)
    {
        if (Dispatcher.UIThread.CheckAccess()) return await action();
        return await Dispatcher.UIThread.InvokeAsync(action);
    }
    private Window CreateDialog(string title, double width)
    {
        var dialog = new Window
        {
            Title = title, Width = width, SizeToContent = SizeToContent.Height, MaxHeight = 760,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = new("Segoe UI,Microsoft JhengHei,PingFang TC,Noto Sans TC,sans-serif"), FontSize = 13
        };
        AppearanceManager.StyleWindow(dialog);
        dialog.Opened += (_, _) => openDialogs++;
        dialog.Closed += (_, _) => { openDialogs--; skipNextActivationRefresh = true; };
        return dialog;
    }
    private static TextBlock Heading(string title) => new() { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Label(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        AppearanceManager.Bind(label, TextBlock.ForegroundProperty, "MutedBrush"); return label;
    }

}
