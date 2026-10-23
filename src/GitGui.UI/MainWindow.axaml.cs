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
    private FileSystemWatcher? worktreeWatcher;
    private FileSystemWatcher? gitWatcher;
    private FileSystemWatcher? commonWatcher;
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool closed;
    private int openDialogs;
    private bool skipNextActivationRefresh;
    public MainWindow()
    {
        if (!Application.Current!.Resources.ContainsKey("CanvasBrush")) _ = new AppearanceManager();
        AvaloniaXamlLoader.Load(this);
    }
    public MainWindow(MainWindowViewModel vm, Control? graph = null, IGitRuntimeSettings? gitSettings = null, AppearanceManager? appearance = null) : this()
    {
        viewModel = vm;
        this.gitSettings = gitSettings;
        this.appearance = appearance ?? new AppearanceManager();
        DataContext = vm;
        if (graph is null)
        {
            var list = new ListBox();
            list.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.Commits)));
            list.Bind(SelectingItemsControlSelectedItem(), new Binding(nameof(vm.SelectedCommit)) { Mode = BindingMode.TwoWay });
            graph = list;
        }
        this.FindControl<ContentControl>("GraphHost")!.Content = new ScrollViewer { Content = graph, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.P, KeyModifiers.Control | KeyModifiers.Shift), Command = new AsyncRelayCommand(OpenCommandPaletteAsync) });
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
        debounce.Tick += async (_, _) => { debounce.Stop(); if (vm.HasRepository && !vm.IsBusy && !closed) await vm.RefreshCommand.ExecuteAsync(null); };
    }
    private static AvaloniaProperty SelectingItemsControlSelectedItem() => Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty;
    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
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
    private void RepositoryOpened(object? sender, EventArgs args)
    {
        worktreeWatcher?.Dispose(); gitWatcher?.Dispose(); commonWatcher?.Dispose();
        if (viewModel is null || !Directory.Exists(viewModel.Root)) return;
        try
        {
            worktreeWatcher = Watch(viewModel.Root);
            var dotGit = Path.Combine(viewModel.Root, ".git");
            if (File.Exists(dotGit))
            {
                var pointer = File.ReadAllText(dotGit).Trim();
                if (pointer.StartsWith("gitdir: ", StringComparison.Ordinal))
                {
                    var directory = Path.GetFullPath(Path.Combine(viewModel.Root, pointer[8..]));
                    if (Directory.Exists(directory))
                    {
                        gitWatcher = Watch(directory);
                        var commonPointer = Path.Combine(directory, "commondir");
                        if (File.Exists(commonPointer))
                        {
                            var common = Path.GetFullPath(Path.Combine(directory, File.ReadAllText(commonPointer).Trim()));
                            if (Directory.Exists(common)) commonWatcher = Watch(common);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { viewModel.Notice += " · " + ex.Message; }
    }
    private FileSystemWatcher Watch(string root)
    {
        var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
        FileSystemEventHandler changed = (_, _) => ScheduleRefresh();
        watcher.Changed += changed; watcher.Created += changed; watcher.Deleted += changed; watcher.Renamed += (_, _) => ScheduleRefresh();
        watcher.Error += (_, _) => ScheduleRefresh(); watcher.EnableRaisingEvents = true;
        return watcher;
    }
    private void ScheduleRefresh() => Dispatcher.UIThread.Post(() => { if (closed || viewModel?.IsBusy != false) return; debounce.Stop(); debounce.Start(); });
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
        if (viewModel is not { IsBusy: false } vm || gitSettings is null) return;
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
        var risk = new TextBlock { Text = $"{l["Safety"]}: {confirmation.Risk}", FontSize = 12 };
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

    private async Task OpenCommandPaletteAsync()
    {
        if (viewModel is null) return;
        var vm = viewModel;
        var dialog = CreateDialog(vm.L["Commands"], 560);
        var search = new TextBox { Watermark = vm.L["CommandSearch"] };
        string[] actions = ["Refresh", "Stage", "StageAll", "Unstage", "UnstageAll", "StagePatch", "UnstagePatch", "Discard", "Commit", "CreateBranch", "SwitchBranch", "RenameBranch", "DeleteBranch", "Merge", "Fetch", "Pull", "Push", "AbortMerge", "ResolveOurs", "ResolveTheirs", "MarkResolved", "ExternalMergeTool", "RestoreSnapshot", "Undo", "Redo"];
        var entries = actions.Select(a => new PaletteEntry(a, vm.L[a switch { "ResolveOurs" => "Ours", "ResolveTheirs" => "Theirs", "ExternalMergeTool" => "MergeTool", "RestoreSnapshot" => "Restore", _ => a }])).ToArray();
        var list = new ListBox { ItemsSource = entries, Height = 340 };
        search.TextChanged += (_, _) => { list.ItemsSource = entries.Where(e => e.ToString().Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToArray(); list.SelectedIndex = 0; };
        var execute = new Button { Content = vm.L["Continue"], HorizontalAlignment = HorizontalAlignment.Right };
        execute.Click += (_, _) => { if (list.SelectedItem is PaletteEntry entry) dialog.Close(entry.Action); };
        list.DoubleTapped += (_, _) => { if (list.SelectedItem is PaletteEntry entry) dialog.Close(entry.Action); };
        dialog.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = new RelayCommand(() => dialog.Close(null)) });
        dialog.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Enter), Command = new RelayCommand(() => { if (list.SelectedItem is PaletteEntry entry) dialog.Close(entry.Action); }) });
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 }; panel.Children.Add(Heading(vm.L["Commands"])); panel.Children.Add(search); panel.Children.Add(list); panel.Children.Add(execute); dialog.Content = panel;
        dialog.Opened += (_, _) => { search.Focus(); list.SelectedIndex = 0; };
        var chosen = await dialog.ShowDialog<string?>(this);
        if (chosen == "Refresh") await vm.RefreshCommand.ExecuteAsync(null);
        else if (chosen == "Undo") await vm.UndoCommand.ExecuteAsync(null);
        else if (chosen == "Redo") await vm.RedoCommand.ExecuteAsync(null);
        else if (chosen is not null) await vm.ActionCommand.ExecuteAsync(chosen);
    }
    private sealed record PaletteEntry(string Action, string Label) { public override string ToString() => Label + "   ·   " + Action; }
}
