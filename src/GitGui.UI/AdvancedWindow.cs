using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.VisualTree;
using GitGui.Core;

namespace GitGui.UI;

/// <summary>Owned non-modal advanced workspace. Backend confirmations remain owned by MainWindow.</summary>
public sealed class AdvancedWindow : Window
{
    public AdvancedWindowViewModel ViewModel { get; }
    private RebaseStepViewModel? draggedStep;
    private Border? dragHandle;
    public AdvancedWindow(AdvancedWindowViewModel vm)
    {
        ViewModel = vm; DataContext = vm;
        Width = 1120; Height = 820; MinWidth = 900; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontSize = 13; FontFamily = new("Segoe UI,Microsoft JhengHei,PingFang TC,Noto Sans TC,sans-serif");
        AppearanceManager.StyleWindow(this);
        AppearanceManager.Bind(this, BackgroundProperty, "CanvasBrush");
        Styles.Add(new StyleInclude(new Uri("avares://GitGui.UI/")) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
        Bind(TitleProperty, Bind("L[AdvancedTitle]"));
        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), RowSpacing = 12, Margin = new Thickness(18) };
        var header = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        var titles = new StackPanel { Spacing = 5 }; titles.Children.Add(Label("AdvancedTitle", 23, true));
        titles.Children.Add(new TextBlock { Text = vm.Root, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        header.Children.Add(titles);
        var refresh = Button("Refresh", "Refresh", icon: "Refresh"); refresh.Command = vm.RefreshCommand; Grid.SetColumn(refresh, 1); header.Children.Add(refresh); root.Children.Add(header);
        var active = new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(5) };
        AppearanceManager.Bind(active, Border.BackgroundProperty, "ConflictSurfaceBrush"); active.Bind(IsVisibleProperty, Bind(nameof(vm.HasActiveOperation)));
        var activePanel = new StackPanel { Spacing = 7 };
        var activeLabel = new TextBlock { FontWeight = FontWeight.SemiBold }; activeLabel.Bind(TextBlock.TextProperty, Bind(nameof(vm.ActiveOperationText))); activePanel.Children.Add(activeLabel);
        activePanel.Children.Add(Label("AdvancedActiveHint"));
        var activeActions = new WrapPanel { Orientation = Orientation.Horizontal };
        activeActions.Children.Add(Button("AdvancedContinue", "ContinueOperation", enabled: nameof(vm.CanContinue), icon: "Check"));
        activeActions.Children.Add(Button("AdvancedSkip", "SkipOperation", enabled: nameof(vm.CanSkip), icon: "Switch"));
        activeActions.Children.Add(Button("AdvancedAbort", "AbortOperation", enabled: nameof(vm.CanAbort), icon: "Close")); activePanel.Children.Add(activeActions);
        active.Child = activePanel; Grid.SetRow(active, 1); root.Children.Add(active);
        var tabs = new TabControl { Name = "AdvancedTabs" };
        tabs.Items.Add(Tab("AdvancedStashes", StashPanel()));
        tabs.Items.Add(Tab("AdvancedRebase", RebasePanel()));
        tabs.Items.Add(Tab("AdvancedHistoryActions", HistoryActionsPanel()));
        tabs.Items.Add(Tab("AdvancedTagsReflog", TagsPanel()));
        tabs.Items.Add(Tab("AdvancedRemotes", RemotesPanel()));
        tabs.Items.Add(Tab("AdvancedFileHistory", FileHistoryPanel()));
        Grid.SetRow(tabs, 2); root.Children.Add(tabs);
        var footer = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8 };
        var status = new StackPanel { Spacing = 6 };
        var progress = new ProgressBar { IsIndeterminate = true, Height = 3 }; progress.Bind(IsVisibleProperty, Bind(nameof(vm.IsBusy))); status.Children.Add(progress);
        var notice = new TextBlock { Name = "AdvancedNotice", TextWrapping = TextWrapping.Wrap }; notice.Bind(TextBlock.TextProperty, Bind(nameof(vm.Notice))); status.Children.Add(notice);
        var phase = new TextBlock { FontSize = 11 }; phase.Bind(TextBlock.TextProperty, Bind(nameof(vm.ProgressText))); AppearanceManager.Bind(phase, TextBlock.ForegroundProperty, "MutedBrush"); status.Children.Add(phase); footer.Children.Add(status);
        var undo = Button("AdvancedUndo", "Undo", enabled: nameof(vm.CanUndo), icon: "Undo"); undo.Command = vm.UndoCommand; Grid.SetColumn(undo, 1); footer.Children.Add(undo);
        var close = Button("Close", "Close", icon: "Close"); close.Command = null; close.Click += (_, _) => Close(); Grid.SetColumn(close, 2); footer.Children.Add(close);
        Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        Opened += async (_, _) => await vm.RefreshCommand.ExecuteAsync(null);
        Closing += (_, args) => { if (vm.IsWriting) { args.Cancel = true; vm.Notice = vm.L["AdvancedWait"]; } };
        Closed += (_, _) => vm.Dispose();
        KeyBindings.Add(new KeyBinding { Gesture = new(Key.F5), Command = vm.RefreshCommand });
        KeyBindings.Add(new KeyBinding { Gesture = new(Key.Escape), Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => Close()) });
    }
    private Binding Bind(string path, BindingMode mode = BindingMode.Default) => new(path) { Source = ViewModel, Mode = mode };
    private TextBlock Label(string key, double size = 12, bool heading = false)
    {
        var label = new TextBlock { FontSize = size, FontWeight = heading ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
        label.Bind(TextBlock.TextProperty, Bind("L[" + key + "]"));
        if (!heading) AppearanceManager.Bind(label, TextBlock.ForegroundProperty, "MutedBrush");
        return label;
    }
    private ActionButton Button(string key, string action, string enabled = nameof(AdvancedWindowViewModel.CanOperate), string icon = "Command")
    {
        var button = new ActionButton { Icon = icon, Name = "Advanced_" + action, Margin = new Thickness(0, 0, 7, 5) };
        button.Bind(ContentControl.ContentProperty, Bind("L[" + key + "]")); button.Bind(IsEnabledProperty, Bind(enabled));
        button.Command = ViewModel.ExecuteCommand; button.CommandParameter = action; return button;
    }
    private TextBox Input(string property, string label, bool multiline = false)
    {
        var input = new TextBox { Name = "AdvancedInput_" + property, AcceptsReturn = multiline, MinHeight = multiline ? 68 : 32, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
        input.Bind(TextBox.TextProperty, Bind(property, BindingMode.TwoWay)); input.Bind(TextBox.WatermarkProperty, Bind("L[" + label + "]"));
        input.Bind(IsEnabledProperty, Bind(nameof(ViewModel.CanOperate))); AutomationProperties.SetName(input, ViewModel.L[label]); return input;
    }
    private CheckBox Check(string property, string label)
    {
        var check = new CheckBox(); check.Bind(ContentControl.ContentProperty, Bind("L[" + label + "]")); check.Bind(CheckBox.IsCheckedProperty, Bind(property, BindingMode.TwoWay)); check.Bind(IsEnabledProperty, Bind(nameof(ViewModel.CanOperate))); return check;
    }
    private static Grid FormGrid(string rows = "Auto,*") => new() { RowDefinitions = new(rows), RowSpacing = 10, Margin = new Thickness(6, 12, 6, 6) };
    private static StackPanel Stack() => new() { Spacing = 8 };
    private TabItem Tab(string key, Control content) => new() { Header = Label(key, 13, true), Content = content };
    private ListBox List<T>(string property, string selection, Func<T, Control> template)
    {
        var list = new ListBox { Name = "AdvancedList_" + property, Background = Brushes.Transparent };
        list.Bind(ItemsControl.ItemsSourceProperty, Bind(property)); list.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty, Bind(selection, BindingMode.TwoWay));
        list.Bind(IsEnabledProperty, Bind(nameof(ViewModel.CanOperate))); list.ItemTemplate = new FuncDataTemplate<T>((item, _) => item is null ? new TextBlock() : template(item)); return list;
    }
    private static TextBlock Text(string text, double size = 12, bool muted = false)
    {
        var label = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        if (muted) AppearanceManager.Bind(label, TextBlock.ForegroundProperty, "MutedBrush"); return label;
    }
    private Control StashPanel()
    {
        var grid = FormGrid("Auto,*,Auto"); var form = Stack();
        form.Children.Add(Label("AdvancedStashHint")); form.Children.Add(Label("AdvancedStashMessage")); form.Children.Add(Input(nameof(ViewModel.StashMessage), "AdvancedStashMessage"));
        form.Children.Add(Check(nameof(ViewModel.IncludeUntracked), "AdvancedIncludeUntracked")); form.Children.Add(Button("AdvancedStashPush", "StashPush", icon: "Save")); grid.Children.Add(form);
        var list = List<StashInfo>(nameof(ViewModel.Stashes), nameof(ViewModel.SelectedStash), stash =>
        {
            var row = Stack(); row.Margin = new Thickness(6); row.Children.Add(Text(stash.Selector + " · " + stash.Message, 13)); row.Children.Add(Text($"{stash.CreatedAt:yyyy-MM-dd HH:mm} · {stash.Oid}", 11, true)); return row;
        }); Grid.SetRow(list, 1); grid.Children.Add(list);
        var actions = new WrapPanel(); actions.Children.Add(Button("AdvancedStashApply", "StashApply", icon: "Open")); actions.Children.Add(Button("AdvancedStashPop", "StashPop", icon: "Undo")); actions.Children.Add(Button("AdvancedStashDrop", "StashDrop", icon: "Trash")); Grid.SetRow(actions, 2); grid.Children.Add(actions); return grid;
    }
    private Control RebasePanel()
    {
        var grid = FormGrid("Auto,*,Auto"); var form = Stack(); form.Children.Add(Label("AdvancedRebaseHint"));
        var upstream = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 }; upstream.Children.Add(Input(nameof(ViewModel.Upstream), "AdvancedUpstream"));
        var load = Button("AdvancedLoadPlan", "LoadPlan", icon: "Search"); load.Command = ViewModel.LoadPlanCommand; Grid.SetColumn(load, 1); upstream.Children.Add(load); form.Children.Add(upstream); grid.Children.Add(form);
        var list = List<RebaseStepViewModel>(nameof(ViewModel.Plan), nameof(ViewModel.SelectedStep), step => RebaseRow(step)); list.Name = "AdvancedRebasePlan";
        list.PointerReleased += (_, args) =>
        {
            if (draggedStep is null) return;
            var point = args.GetPosition(list);
            foreach (var item in list.GetVisualDescendants().OfType<ListBoxItem>())
            {
                var top = item.TranslatePoint(default, list);
                if (top is null || point.Y < top.Value.Y || point.Y >= top.Value.Y + item.Bounds.Height || item.DataContext is not RebaseStepViewModel target) continue;
                ViewModel.MoveStep(ViewModel.Plan.IndexOf(draggedStep), ViewModel.Plan.IndexOf(target)); break;
            }
            if (dragHandle is not null) dragHandle.Opacity = 1;
            draggedStep = null; dragHandle = null; args.Pointer.Capture(null); args.Handled = true;
        };
        Grid.SetRow(list, 1); grid.Children.Add(list);
        var actions = new WrapPanel();
        var up = Button("AdvancedMoveUp", "MoveUp", icon: "Push"); up.Command = ViewModel.MoveUpCommand; actions.Children.Add(up);
        var down = Button("AdvancedMoveDown", "MoveDown", icon: "Pull"); down.Command = ViewModel.MoveDownCommand; actions.Children.Add(down);
        actions.Children.Add(Button("AdvancedStartRebase", "RebaseInteractive", icon: "Branch")); Grid.SetRow(actions, 2); grid.Children.Add(actions); return grid;
    }
    private Control RebaseRow(RebaseStepViewModel step)
    {
        var row = new Grid { ColumnDefinitions = new("28,100,120,*"), ColumnSpacing = 9, Margin = new Thickness(3, 7) };
        var handle = new Border { Name = "AdvancedDrag_" + step.ShortCommit, Cursor = new Cursor(StandardCursorType.SizeAll), Padding = new Thickness(5), Child = new ActionIcon { Kind = "Settings", Width = 18, Height = 18 } };
        ToolTip.SetTip(handle, ViewModel.L["AdvancedDrag"]); AppearanceManager.Bind(handle, Border.BackgroundProperty, "RaisedBrush");
        handle.PointerPressed += (_, args) =>
        {
            if (ViewModel.IsBusy || !args.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            draggedStep = step; dragHandle = handle; handle.Opacity = .45;
            var list = handle.GetVisualAncestors().OfType<ListBox>().FirstOrDefault();
            if (list is not null) { list.SelectedItem = step; args.Pointer.Capture(list); args.Handled = true; }
        };
        row.Children.Add(handle);
        var hash = Text(step.ShortCommit, 12); hash.VerticalAlignment = VerticalAlignment.Center; hash.FontFamily = new("Cascadia Code,Consolas,Menlo,monospace"); Grid.SetColumn(hash, 1); row.Children.Add(hash);
        var choice = new ComboBox { ItemsSource = ViewModel.StepKinds, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        choice.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty, new Binding(nameof(step.Action)) { Source = step, Mode = BindingMode.TwoWay }); Grid.SetColumn(choice, 2); row.Children.Add(choice);
        var message = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56, MaxHeight = 90, Watermark = ViewModel.L["AdvancedStepMessage"] };
        message.Bind(TextBox.TextProperty, new Binding(nameof(step.Message)) { Source = step, Mode = BindingMode.TwoWay }); Grid.SetColumn(message, 3); row.Children.Add(message); return row;
    }
    private Control HistoryActionsPanel()
    {
        var panel = Stack(); panel.Margin = new Thickness(8, 14); panel.Children.Add(Label("AdvancedHistoryHint")); panel.Children.Add(Label("AdvancedRevision")); panel.Children.Add(Input(nameof(ViewModel.Revision), "AdvancedRevision"));
        var apply = new WrapPanel(); apply.Children.Add(Button("AdvancedCherryPick", "CherryPick", icon: "Plus")); apply.Children.Add(Button("AdvancedRevert", "Revert", icon: "Undo")); panel.Children.Add(apply);
        panel.Children.Add(Label("AdvancedResetHint"));
        var modes = new ComboBox { ItemsSource = ViewModel.ResetModes, HorizontalAlignment = HorizontalAlignment.Stretch }; modes.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty, Bind(nameof(ViewModel.ResetMode), BindingMode.TwoWay)); modes.Bind(IsEnabledProperty, Bind(nameof(ViewModel.CanOperate))); panel.Children.Add(modes);
        panel.Children.Add(Button("AdvancedReset", "Reset", icon: "Shield")); return new ScrollViewer { Content = panel };
    }
    private Control TagsPanel()
    {
        var grid = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 18, Margin = new Thickness(8, 14) };
        var tags = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 9 }; tags.Children.Add(Label("AdvancedTags", 15, true));
        var list = List<TagInfo>(nameof(ViewModel.Tags), nameof(ViewModel.SelectedTag), tag => Text(tag.Name + (tag.Annotated ? " · annotated" : " · lightweight") + "\n" + tag.Target, 12)); Grid.SetRow(list, 1); tags.Children.Add(list);
        var form = Stack(); form.Children.Add(Input(nameof(ViewModel.TagName), "AdvancedTagName")); form.Children.Add(Input(nameof(ViewModel.Revision), "AdvancedRevision")); form.Children.Add(Check(nameof(ViewModel.AnnotatedTag), "AdvancedAnnotated")); form.Children.Add(Input(nameof(ViewModel.TagMessage), "AdvancedTagMessage", true));
        var actions = new WrapPanel(); actions.Children.Add(Button("AdvancedCreateTag", "CreateTag", icon: "Plus")); actions.Children.Add(Button("AdvancedDeleteTag", "DeleteTag", icon: "Trash")); form.Children.Add(actions); Grid.SetRow(form, 2); tags.Children.Add(form); grid.Children.Add(tags);
        var reflog = new Grid { RowDefinitions = new("Auto,Auto,*"), RowSpacing = 8 }; reflog.Children.Add(Label("AdvancedReflog", 15, true)); var hint = Label("AdvancedReflogHint"); Grid.SetRow(hint, 1); reflog.Children.Add(hint);
        var logs = List<ReflogInfo>(nameof(ViewModel.Reflog), nameof(ViewModel.SelectedReflog), entry => Text($"{entry.Selector} · {entry.Message}\n{entry.CreatedAt:yyyy-MM-dd HH:mm} · {entry.Author}\n{entry.Oid}", 12)); Grid.SetRow(logs, 2); reflog.Children.Add(logs); Grid.SetColumn(reflog, 1); grid.Children.Add(reflog); return grid;
    }
    private Control RemotesPanel()
    {
        var grid = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 18, Margin = new Thickness(8, 14) };
        grid.Children.Add(List<AdvancedRemoteInfo>(nameof(ViewModel.Remotes), nameof(ViewModel.SelectedRemote), remote => Text(remote.Name + "\n" + string.Join("\n", remote.FetchUrls), 13)));
        var form = Stack(); form.Children.Add(Label("AdvancedRemoteHint"));
        var urls = new TextBox { IsReadOnly = true, AcceptsReturn = true, MinHeight = 80 }; urls.Bind(TextBox.TextProperty, Bind(nameof(ViewModel.SelectedRemoteUrls))); form.Children.Add(urls);
        form.Children.Add(Input(nameof(ViewModel.RemoteName), "AdvancedRemoteName")); form.Children.Add(Input(nameof(ViewModel.RemoteUrl), "Url")); form.Children.Add(Check(nameof(ViewModel.PushUrl), "AdvancedPushUrl"));
        var save = new WrapPanel(); save.Children.Add(Button("AdvancedAddRemote", "AddRemote", icon: "Plus")); save.Children.Add(Button("AdvancedSetUrl", "SetRemoteUrl", icon: "Save")); form.Children.Add(save);
        form.Children.Add(Input(nameof(ViewModel.NewRemoteName), "AdvancedNewRemoteName")); var actions = new WrapPanel(); actions.Children.Add(Button("RenameBranch", "RenameRemote", icon: "Rename")); actions.Children.Add(Button("AdvancedRemoveRemote", "RemoveRemote", icon: "Trash")); form.Children.Add(actions);
        var scroll = new ScrollViewer { Content = form }; Grid.SetColumn(scroll, 1); grid.Children.Add(scroll); return grid;
    }
    private Control FileHistoryPanel()
    {
        var grid = FormGrid("Auto,Auto,*"); var search = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        search.Children.Add(Input(nameof(ViewModel.FilePath), "AdvancedFilePath")); var load = Button("AdvancedLoadFile", "LoadFile", icon: "Search"); load.Command = ViewModel.LoadFileCommand; Grid.SetColumn(load, 1); search.Children.Add(load); grid.Children.Add(search);
        var hint = new TextBlock { FontSize = 11 }; hint.Bind(TextBlock.TextProperty, Bind(nameof(ViewModel.FileBackend))); AppearanceManager.Bind(hint, TextBlock.ForegroundProperty, "MutedBrush"); Grid.SetRow(hint, 1); grid.Children.Add(hint);
        var panes = new Grid { ColumnDefinitions = new(".9*,1.1*"), ColumnSpacing = 12 };
        panes.Children.Add(List<FileHistoryEntry>(nameof(ViewModel.FileHistory), nameof(ViewModel.SelectedFileCommit), entry => Text($"{entry.Commit.Label}\n{entry.Commit.Author} · {entry.Commit.Date:yyyy-MM-dd}\n{entry.Path}", 12)));
        var blame = new ListBox { Name = "AdvancedBlame", Background = Brushes.Transparent };
        blame.Bind(ItemsControl.ItemsSourceProperty, Bind(nameof(ViewModel.Blame))); blame.ItemTemplate = new FuncDataTemplate<BlameLine>((line, _) =>
        {
            if (line is null) return new TextBlock();
            var row = new Grid { ColumnDefinitions = new("38,86,110,*"), ColumnSpacing = 8, Margin = new Thickness(0, 3) };
            row.Children.Add(Text(line.LineNumber.ToString(), 11, true)); var hash = Text(line.Commit[..Math.Min(8, line.Commit.Length)], 11, true); Grid.SetColumn(hash, 1); row.Children.Add(hash);
            var author = Text(line.Author, 11, true); Grid.SetColumn(author, 2); row.Children.Add(author); var code = Text(line.Text, 12); code.FontFamily = new("Cascadia Code,Consolas,Menlo,Microsoft JhengHei,monospace"); Grid.SetColumn(code, 3); row.Children.Add(code); return row;
        }); Grid.SetColumn(blame, 1); panes.Children.Add(blame); Grid.SetRow(panes, 2); grid.Children.Add(panes); return grid;
    }
}
