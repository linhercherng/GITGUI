using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using GitGui.Core;

namespace GitGui.UI;

/// <summary>Owned non-modal tools window; risk and trust prompts remain owned by MainWindow.</summary>
public sealed class RepositoryToolsWindow : Window
{
    public RepositoryToolsWindowViewModel ViewModel { get; }
    public RepositoryToolsWindow(RepositoryToolsWindowViewModel vm)
    {
        ViewModel = vm; DataContext = vm; Width = 1120; Height = 840; MinWidth = 940; MinHeight = 700;
        FontSize = 13; FontFamily = new("Segoe UI,Microsoft JhengHei,PingFang TC,Noto Sans TC,sans-serif");
        WindowStartupLocation = WindowStartupLocation.CenterOwner; AppearanceManager.StyleWindow(this);
        Bind(TitleProperty, B("L[ToolsTitle]")); AppearanceManager.Bind(this, BackgroundProperty, "CanvasBrush");
        var root = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), RowSpacing = 12, Margin = new Thickness(18) };
        var header = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        var heading = new StackPanel { Spacing = 5 }; heading.Children.Add(Label("ToolsTitle", 23, true));
        heading.Children.Add(new TextBlock { Text = vm.Root, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis }); header.Children.Add(heading);
        var refresh = Button("Refresh", "Refresh", "Refresh"); refresh.Command = vm.RefreshCommand; Grid.SetColumn(refresh, 1); header.Children.Add(refresh); root.Children.Add(header);
        var tabs = new TabControl { Name = "RepositoryToolsTabs" };
        tabs.Items.Add(Tab("ToolsSubmodules", SubmodulePanel())); tabs.Items.Add(Tab("ToolsWorktrees", WorktreePanel()));
        tabs.Items.Add(Tab("ToolsLfs", LfsPanel())); tabs.Items.Add(Tab("ToolsBisect", BisectPanel())); tabs.Items.Add(Tab("ToolsSparse", SparsePanel()));
        Grid.SetRow(tabs, 1); root.Children.Add(tabs);
        var output = new Expander { Name = "RepositoryToolsOutput", Header = Label("CommandOutput"), MaxHeight = 210 };
        var log = new TextBox { Name = "RepositoryToolsCommands", IsReadOnly = true, AcceptsReturn = true, MinHeight = 110, MaxHeight = 170, FontFamily = new("Cascadia Code,Consolas,Menlo,monospace"), FontSize = 11 };
        log.Bind(TextBox.TextProperty, B(nameof(vm.Commands)));
        var outputGrid = new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 6 }; outputGrid.Children.Add(log);
        var copy = new ActionButton { Icon = "Copy", HorizontalAlignment = HorizontalAlignment.Right }; copy.Bind(ContentControl.ContentProperty, B("L[CopyCommands]"));
        copy.Click += async (_, _) => { if (Clipboard is { } clipboard) await clipboard.SetTextAsync(vm.Commands); }; Grid.SetRow(copy, 1); outputGrid.Children.Add(copy);
        output.Content = outputGrid; Grid.SetRow(output, 2); root.Children.Add(output);
        var footer = new Grid { ColumnDefinitions = new("*,Auto,Auto,Auto"), ColumnSpacing = 8 };
        var status = new StackPanel { Spacing = 6 }; var progress = new ProgressBar { IsIndeterminate = true, Height = 3 }; progress.Bind(IsVisibleProperty, B(nameof(vm.IsBusy))); status.Children.Add(progress);
        var notice = new TextBlock { TextWrapping = TextWrapping.Wrap }; notice.Bind(TextBlock.TextProperty, B(nameof(vm.Notice))); status.Children.Add(notice);
        var phase = new TextBlock { FontSize = 11 }; phase.Bind(TextBlock.TextProperty, B(nameof(vm.ProgressText))); AppearanceManager.Bind(phase, TextBlock.ForegroundProperty, "MutedBrush"); status.Children.Add(phase); footer.Children.Add(status);
        var cancel = Button("Cancel", "Cancel", "Close"); cancel.Command = vm.CancelCommand; cancel.Bind(IsVisibleProperty, B(nameof(vm.CanCancel))); cancel.Bind(IsEnabledProperty, B(nameof(vm.CanCancel))); Grid.SetColumn(cancel, 1); footer.Children.Add(cancel);
        var undo = Button("AdvancedUndo", "Undo", "Undo", nameof(vm.CanUndo)); undo.Command = vm.UndoCommand; Grid.SetColumn(undo, 2); footer.Children.Add(undo);
        var close = new ActionButton { Icon = "Close" }; close.Bind(ContentControl.ContentProperty, B("L[Close]")); close.Click += (_, _) => Close(); Grid.SetColumn(close, 3); footer.Children.Add(close);
        Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        Opened += async (_, _) => await vm.RefreshCommand.ExecuteAsync(null);
        Closing += (_, args) => { if (vm.IsWriting) { args.Cancel = true; vm.Notice = vm.L["ToolsWait"]; } };
        Closed += (_, _) => vm.Dispose();
        KeyBindings.Add(new KeyBinding { Gesture = new(Key.F5), Command = vm.RefreshCommand });
        KeyBindings.Add(new KeyBinding { Gesture = new(Key.Escape), Command = new RelayCommand(() => Close()) });
    }
    private Binding B(string path, BindingMode mode = BindingMode.Default) => new(path) { Source = ViewModel, Mode = mode };
    private TextBlock Label(string key, double size = 12, bool heading = false)
    {
        var label = new TextBlock { FontSize = size, FontWeight = heading ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
        label.Bind(TextBlock.TextProperty, B("L[" + key + "]")); if (!heading) AppearanceManager.Bind(label, TextBlock.ForegroundProperty, "MutedBrush"); return label;
    }
    private ActionButton Button(string key, string action, string icon = "Command", string availability = nameof(RepositoryToolsWindowViewModel.CanOperate))
    {
        var button = new ActionButton { Name = "Tools_" + action, Icon = icon, Margin = new Thickness(0, 0, 6, 6), Command = ViewModel.ExecuteCommand, CommandParameter = action };
        button.Bind(ContentControl.ContentProperty, B("L[" + key + "]")); button.Bind(IsEnabledProperty, B(availability)); return button;
    }
    private Control Field(string property, string label, bool multiline = false)
    {
        var stack = Stack(); stack.Children.Add(Label(label));
        var input = new TextBox { Name = "ToolsInput_" + property, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 92 : 32, MaxHeight = multiline ? 160 : double.PositiveInfinity };
        input.Bind(TextBox.TextProperty, B(property, BindingMode.TwoWay)); input.Bind(IsEnabledProperty, B(nameof(ViewModel.CanOperate))); AutomationProperties.SetName(input, ViewModel.L[label]); stack.Children.Add(input); return stack;
    }
    private CheckBox Check(string property, string key)
    {
        var check = new CheckBox(); check.Bind(ContentControl.ContentProperty, B("L[" + key + "]")); check.Bind(CheckBox.IsCheckedProperty, B(property, BindingMode.TwoWay)); check.Bind(IsEnabledProperty, B(nameof(ViewModel.CanOperate))); return check;
    }
    private static StackPanel Stack() => new() { Spacing = 7 };
    private TabItem Tab(string key, Control panel) => new() { Header = Label(key, 13, true), Content = panel };
    private static Grid Split() => new() { ColumnDefinitions = new("1.05*,1*"), ColumnSpacing = 22, Margin = new Thickness(8, 14, 8, 8) };
    private static TextBlock RowText(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5, 8), FontSize = 12 };
    private ListBox List<T>(string property, string selection, Func<T, string> text)
    {
        var list = new ListBox { Name = "ToolsList_" + property, Background = Brushes.Transparent };
        list.Bind(ItemsControl.ItemsSourceProperty, B(property)); list.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty, B(selection, BindingMode.TwoWay));
        list.Bind(IsEnabledProperty, B(nameof(ViewModel.CanOperate))); list.ItemTemplate = new FuncDataTemplate<T>((item, _) => item is null ? new TextBlock() : RowText(text(item))); return list;
    }
    private static void Right(Grid grid, Control panel) { Grid.SetColumn(panel, 1); grid.Children.Add(new ScrollViewer { Content = panel, [Grid.ColumnProperty] = 1 }); }
    private Control SubmodulePanel()
    {
        var grid = Split(); var rows = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 9 }; rows.Children.Add(Label("ToolsSubmoduleBoundary"));
        var list = List<SubmoduleInfo>(nameof(ViewModel.Submodules), nameof(ViewModel.SelectedSubmodule), item => item.Path + "\n" + item.State + " · " + item.Commit); Grid.SetRow(list, 1); rows.Children.Add(list); grid.Children.Add(rows);
        var form = Stack(); form.Children.Add(Field(nameof(ViewModel.SubmodulePath), "ToolsSubmodulePath")); form.Children.Add(Field(nameof(ViewModel.SubmoduleUrl), "Url"));
        form.Children.Add(Button("ToolsSubmoduleAdd", "SubmoduleAdd", "Plus")); form.Children.Add(Check(nameof(ViewModel.Recursive), "ToolsRecursive"));
        var actions = new WrapPanel(); actions.Children.Add(Button("ToolsInitialize", "SubmoduleInitialize", "Open")); actions.Children.Add(Button("ToolsUpdate", "SubmoduleUpdate", "Refresh")); actions.Children.Add(Button("ToolsSync", "SubmoduleSync", "Switch")); actions.Children.Add(Button("ToolsDeinitialize", "SubmoduleDeinitialize", "Minus")); form.Children.Add(actions); Right(grid, form); return grid;
    }
    private Control WorktreePanel()
    {
        var grid = Split(); var rows = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 9 }; rows.Children.Add(Label("ToolsWorktreeBoundary"));
        var list = List<WorktreeInfo>(nameof(ViewModel.Worktrees), nameof(ViewModel.SelectedWorktree), item => item.Path + "\n" + (item.Detached ? "detached" : item.Branch) + " · " + item.Head + (item.Locked ? "\n" + ViewModel.L["ToolsLocked"] + ": " + item.LockReason : "") + (item.Prunable ? "\n" + ViewModel.L["ToolsPrunable"] : "")); Grid.SetRow(list, 1); rows.Children.Add(list); grid.Children.Add(rows);
        var form = Stack(); form.Children.Add(Field(nameof(ViewModel.WorktreePath), "ToolsWorktreePath")); form.Children.Add(Field(nameof(ViewModel.WorktreeBranch), "ToolsWorktreeBranch")); form.Children.Add(Field(nameof(ViewModel.WorktreeRevision), "AdvancedRevision")); form.Children.Add(Button("ToolsWorktreeAdd", "WorktreeAdd", "Plus"));
        form.Children.Add(Field(nameof(ViewModel.LockReason), "ToolsLockReason")); var actions = new WrapPanel(); actions.Children.Add(Button("ToolsLock", "WorktreeLock", "Shield")); actions.Children.Add(Button("ToolsUnlock", "WorktreeUnlock", "Open")); actions.Children.Add(Button("ToolsRemove", "WorktreeRemove", "Trash")); actions.Children.Add(Button("ToolsPrune", "WorktreePrune", "Refresh")); form.Children.Add(actions); Right(grid, form); return grid;
    }
    private TextBox Readout(string property, string name)
    {
        var readout = new TextBox { Name = name, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new("Cascadia Code,Consolas,Menlo,Microsoft JhengHei,monospace"), FontSize = 12, MinHeight = 160 };
        readout.Bind(TextBox.TextProperty, B(property)); return readout;
    }
    private Control LfsPanel()
    {
        var grid = Split(); var left = new Grid { RowDefinitions = new("Auto,Auto,*"), RowSpacing = 9 }; left.Children.Add(Label("ToolsLfsBoundary"));
        var version = new TextBlock { FontWeight = FontWeight.SemiBold }; version.Bind(TextBlock.TextProperty, B(nameof(ViewModel.LfsVersion))); Grid.SetRow(version, 1); left.Children.Add(version);
        var status = Readout(nameof(ViewModel.LfsStatus), "ToolsLfsStatus"); Grid.SetRow(status, 2); left.Children.Add(status); grid.Children.Add(left);
        var form = Stack(); form.Children.Add(Button("ToolsLfsInstall", "LfsInstall", "Open")); form.Children.Add(Field(nameof(ViewModel.LfsPatterns), "ToolsLfsPatterns", true));
        var patterns = new WrapPanel(); patterns.Children.Add(Button("ToolsTrack", "LfsTrack", "Plus")); patterns.Children.Add(Button("ToolsUntrack", "LfsUntrack", "Minus")); form.Children.Add(patterns);
        form.Children.Add(Field(nameof(ViewModel.LfsRemote), "Remote")); var network = new WrapPanel(); network.Children.Add(Button("Fetch", "LfsFetch", "Fetch")); network.Children.Add(Button("Pull", "LfsPull", "Pull")); form.Children.Add(network); Right(grid, form); return grid;
    }
    private Control BisectPanel()
    {
        var grid = Split(); var left = new Grid { RowDefinitions = new("Auto,Auto,*"), RowSpacing = 9 }; left.Children.Add(Label("ToolsBisectBoundary"));
        var active = new TextBlock { FontWeight = FontWeight.SemiBold }; active.Bind(TextBlock.TextProperty, B(nameof(ViewModel.BisectSummary))); Grid.SetRow(active, 1); left.Children.Add(active);
        var log = Readout(nameof(ViewModel.BisectLog), "ToolsBisectLog"); Grid.SetRow(log, 2); left.Children.Add(log); grid.Children.Add(left);
        var form = Stack(); form.Children.Add(Field(nameof(ViewModel.BadRevision), "ToolsBadRevision")); form.Children.Add(Field(nameof(ViewModel.GoodRevision), "ToolsGoodRevision")); form.Children.Add(Button("ToolsBisectStart", "BisectStart", "Search", nameof(ViewModel.CanStartBisect)));
        form.Children.Add(Label("ToolsBisectMarkHint")); var actions = new WrapPanel(); actions.Children.Add(Button("ToolsGood", "BisectGood", "Check", nameof(ViewModel.CanMarkBisect))); actions.Children.Add(Button("ToolsBad", "BisectBad", "Close", nameof(ViewModel.CanMarkBisect))); actions.Children.Add(Button("ToolsSkip", "BisectSkip", "Switch", nameof(ViewModel.CanMarkBisect))); actions.Children.Add(Button("ToolsBisectReset", "BisectReset", "Undo", nameof(ViewModel.CanMarkBisect))); form.Children.Add(actions); Right(grid, form); return grid;
    }
    private Control SparsePanel()
    {
        var grid = Split(); var left = new Grid { RowDefinitions = new("Auto,Auto,*"), RowSpacing = 9 }; left.Children.Add(Label("ToolsSparseBoundary"));
        var status = new TextBlock { FontWeight = FontWeight.SemiBold }; status.Bind(TextBlock.TextProperty, B(nameof(ViewModel.SparseSummary))); Grid.SetRow(status, 1); left.Children.Add(status);
        var current = Readout(nameof(ViewModel.SparseCurrent), "ToolsSparseCurrent"); Grid.SetRow(current, 2); left.Children.Add(current); grid.Children.Add(left);
        var form = Stack(); form.Children.Add(Check(nameof(ViewModel.SparseCone), "ToolsCone")); form.Children.Add(Field(nameof(ViewModel.SparsePatterns), "ToolsSparsePatterns", true)); form.Children.Add(Label("ToolsSparsePatternHint"));
        var actions = new WrapPanel(); actions.Children.Add(Button("ToolsSparseSet", "SparseSet", "Save")); actions.Children.Add(Button("ToolsSparseDisable", "SparseDisable", "Undo", nameof(ViewModel.CanDisableSparse))); form.Children.Add(actions); Right(grid, form); return grid;
    }
}
