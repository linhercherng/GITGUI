using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using GitGui.Core;

namespace GitGui.UI;

public sealed class StagingExclusionsWindow : Window
{
    public StagingExclusionsWindow(MainWindowViewModel vm)
    {
        var l = vm.L; var root = vm.Root; var saving = false;
        var drafts = new ObservableCollection<StageExclusion>(vm.StagingExclusions);
        var selected = vm.SelectedFiles.Count > 0 ? vm.SelectedFiles.ToArray() : vm.SelectedChange is { } file ? new[] { file } : [];
        Width = 800; Height = 600; MinWidth = 620; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AppearanceManager.StyleWindow(this); l.Bind(this, TitleProperty, "StageExclusions");
        var grid = new Grid { RowDefinitions = new("Auto,Auto,Auto,*,Auto,Auto"), RowSpacing = 10, Margin = new(22) };
        var heading = new StackPanel { Spacing = 6 };
        var rootLabel = new TextBlock { Text = root, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, FontWeight = FontWeight.SemiBold };
        ToolTip.SetTip(rootLabel, root); heading.Children.Add(rootLabel);
        heading.Children.Add(new TextBlock { Text = l["StageExclusionsHint"], TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(heading);
        var actions = new WrapPanel(); Grid.SetRow(actions, 1); grid.Children.Add(actions);
        var addSelected = new ActionButton { Name = "AddSelectedExclusions", Icon = "Exclude", Content = l["StageExclusionsAddSelected"], IsEnabled = selected.Length > 0, Margin = new(0, 0, 6, 6) };
        var addFiles = new ActionButton { Name = "AddExcludedFiles", Icon = "Plus", Content = l["StageExclusionsAddFiles"], Margin = new(0, 0, 6, 6) };
        var addFolder = new ActionButton { Name = "AddExcludedFolder", Icon = "Folder", Content = l["StageExclusionsAddFolder"], Margin = new(0, 0, 6, 6) };
        actions.Children.Add(addSelected); actions.Children.Add(addFiles); actions.Children.Add(addFolder);
        var count = new TextBlock(); Grid.SetRow(count, 2); grid.Children.Add(count);
        var list = new ListBox { Name = "StageExclusionRules", ItemsSource = drafts, SelectionMode = SelectionMode.Multiple,
            ItemTemplate = new FuncDataTemplate<StageExclusion>((rule, _) => new TextBlock { Text = rule is null ? "" :
                l[rule.IsDirectory ? "StageExclusionFolder" : "StageExclusionFile"] + " · " + GitPathPolicy.Display(rule.Path) + (rule.IsDirectory ? "/" : ""), TextWrapping = TextWrapping.Wrap }) };
        AutomationProperties.SetName(list, l["StageExclusions"]);
        Grid.SetRow(list, 3); grid.Children.Add(list);
        var notice = new TextBlock { Name = "StageExclusionsNotice", Text = l["StageExclusionsExistingIndex"], TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(notice, 4); grid.Children.Add(notice);
        var footer = new DockPanel { LastChildFill = false }; Grid.SetRow(footer, 5); grid.Children.Add(footer);
        var remove = new ActionButton { Name = "RemoveStageExclusions", Icon = "Minus", Content = l["StageExclusionsRemove"], IsEnabled = false };
        footer.Children.Add(remove);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(right, Dock.Right); footer.Children.Add(right);
        var cancel = new ActionButton { Name = "CancelStageExclusions", Icon = "Close", Content = l["Cancel"] };
        var save = new ActionButton { Name = "SaveStageExclusions", Icon = "Save", Content = l["StageExclusionsSave"] };
        right.Children.Add(cancel); right.Children.Add(save);
        void Update() => count.Text = l.Format("StageExclusionsRuleCount", drafts.Count);
        Update(); drafts.CollectionChanged += (_, _) => Update();
        list.SelectionChanged += (_, _) => remove.IsEnabled = list.SelectedItems?.Count > 0;
        void Add(IEnumerable<StageExclusion> rules)
        {
            var normalized = new StageExclusionRules([.. drafts, .. rules]).Rules;
            drafts.Clear(); foreach (var rule in normalized) drafts.Add(rule);
            notice.Text = l["StageExclusionsExistingIndex"];
        }
        void AddItems(IReadOnlyList<IStorageItem> items, bool directory)
        {
            if (items.Count == 0) return;
            try
            {
                var rules = items.Select(item => item.TryGetLocalPath() is { } path
                    ? new StageExclusion(Path.GetRelativePath(root, path), directory)
                    : throw new ArgumentException()).ToArray();
                Add(rules);
            }
            finally { foreach (var item in items) item.Dispose(); }
        }
        addSelected.Click += (_, _) =>
        {
            try
            {
                if (selected.Any(change => !change.IsPathSupported)) throw new ArgumentException();
                Add(selected.Select(change => new StageExclusion(change.Path, false)));
            }
            catch (ArgumentException) { notice.Text = l["StageExclusionsInvalidPath"]; }
        };
        addFiles.Click += async (_, _) =>
        {
            try
            {
                using var location = await StorageProvider.TryGetFolderFromPathAsync(new Uri(root));
                var items = await StorageProvider.OpenFilePickerAsync(new() { Title = l["StageExclusionsAddFiles"], AllowMultiple = true,
                    SuggestedStartLocation = location });
                AddItems(items, false);
            }
            catch (Exception) { notice.Text = l["StageExclusionsInvalidPath"]; }
        };
        addFolder.Click += async (_, _) =>
        {
            try
            {
                using var location = await StorageProvider.TryGetFolderFromPathAsync(new Uri(root));
                var items = await StorageProvider.OpenFolderPickerAsync(new() { Title = l["StageExclusionsAddFolder"], AllowMultiple = true,
                    SuggestedStartLocation = location });
                AddItems(items, true);
            }
            catch (Exception) { notice.Text = l["StageExclusionsInvalidPath"]; }
        };
        remove.Click += (_, _) => { foreach (var item in list.SelectedItems?.OfType<StageExclusion>().ToArray() ?? []) drafts.Remove(item); };
        cancel.Click += (_, _) => Close();
        save.Click += async (_, _) =>
        {
            if (saving) return;
            saving = true; grid.IsEnabled = false; save.Content = l["StageExclusionsSaving"];
            try
            {
                if (vm.Root != root) { notice.Text = l["StageExclusionsRepositoryChanged"]; return; }
                await vm.SaveStagingExclusionsAsync(drafts.ToArray());
                saving = false; Close();
            }
            catch (Exception) { notice.Text = l["StageExclusionsSaveFailed"]; }
            finally { saving = false; grid.IsEnabled = true; save.Content = l["StageExclusionsSave"]; }
        };
        Closing += (_, args) => args.Cancel = saving;
        KeyDown += (_, args) => { if (args.Key == Key.Escape && !saving) { Close(); args.Handled = true; } };
        Opened += (_, _) => { if (addSelected.IsEnabled) addSelected.Focus(); else addFiles.Focus(); };
        Content = grid;
    }
}
