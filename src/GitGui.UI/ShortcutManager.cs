using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.Input;
using GitGui.Core;

namespace GitGui.UI;

public sealed record UiCommand(string Id, Func<Task> Execute, Func<bool>? CanExecute = null);
public sealed record ShortcutDefinition(string Id, string LabelKey, string Scope, string? DefaultGesture = null);

/// <summary>Window-scoped commands use the same guarded delegates for keys and the command palette.</summary>
public sealed class ShortcutManager
{
    private readonly IUiPreferences? preferences;
    private readonly Dictionary<string, string?> overrides = new(StringComparer.Ordinal);
    private readonly List<Host> hosts = [];
    public string Culture { get; private set; }
    public event EventHandler? CultureChanged;
    public event EventHandler? Changed;
    public static IReadOnlyList<ShortcutDefinition> Definitions { get; } = CreateDefinitions();
    public ShortcutManager(IUiPreferences? preferences = null)
    {
        this.preferences = preferences; var saved = preferences?.Load() ?? new(); Culture = saved.Culture == "en" ? "en" : "zh-TW";
        // Validate the complete effective map, so a valid swap does not conflict with defaults not yet replaced.
        foreach (var (id, gesture) in saved.Shortcuts ?? new Dictionary<string, string?>()) overrides[id] = gesture;
        try { foreach (var (id, gesture) in overrides) Validate(id, gesture); }
        catch (ArgumentException) { overrides.Clear(); /* Corrupt/conflicting imports fall back atomically to safe defaults. */ }
    }
    public string? Gesture(string id) => overrides.TryGetValue(id, out var gesture) ? gesture : Definitions.FirstOrDefault(d => d.Id == id)?.DefaultGesture;
    public void SetCulture(string culture)
    {
        var next = culture == "en" ? "en" : "zh-TW"; if (Culture == next) return;
        var previous = Culture; Culture = next;
        try { Save(); } catch { Culture = previous; throw; }
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }
    public void SetGesture(string id, string? gesture)
    {
        gesture = string.IsNullOrWhiteSpace(gesture) ? null : ParseGesture(gesture).ToString(); Validate(id, gesture);
        var existed = overrides.TryGetValue(id, out var previous); overrides[id] = gesture;
        try { Save(); } catch { if (existed) overrides[id] = previous; else overrides.Remove(id); throw; }
        Rebind();
    }
    private void Validate(string id, string? gesture)
    {
        var definition = Definitions.SingleOrDefault(d => d.Id == id) ?? throw new ArgumentException("Unknown shortcut command.");
        if (gesture is null) return;
        KeyGesture parsed;
        try { parsed = ParseGesture(gesture); } catch (Exception ex) when (ex is ArgumentException or FormatException) { throw new ArgumentException("ShortcutInvalid", ex); }
        var function = parsed.Key >= Key.F1 && parsed.Key <= Key.F24;
        if ((!function && (parsed.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0) ||
            parsed.Key is Key.Tab or Key.Escape or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown ||
            ((parsed.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 && parsed.Key is Key.C or Key.V or Key.X or Key.A or Key.Z or Key.Y))
            throw new ArgumentException("ShortcutReserved");
        foreach (var other in Definitions.Where(d => d.Scope == definition.Scope && d.Id != id))
            if (Gesture(other.Id) is { } existing && ParseGesture(existing).Equals(parsed)) throw new ArgumentException("ShortcutDuplicate");
    }
    private static KeyGesture ParseGesture(string gesture)
    {
        // Enum parsing treats a bare digit as a numeric enum value (1 = Cancel), rather than the number-row key.
        var parts = gesture.Split('+'); var key = parts[^1].Trim();
        if (key.Length == 1 && key[0] is >= '0' and <= '9') parts[^1] = "D" + key;
        return KeyGesture.Parse(string.Join('+', parts));
    }
    public void Reset() { var previous = overrides.ToArray(); overrides.Clear(); try { Save(); } catch { foreach (var item in previous) overrides.Add(item.Key, item.Value); throw; } Rebind(); }
    private void Save() => preferences?.Save(new(Culture, new Dictionary<string, string?>(overrides)));
    private void Rebind() { foreach (var host in hosts.ToArray()) host.Bind(); Changed?.Invoke(this, EventArgs.Empty); }
    public void Attach(Window window, string scope, Localization localization, IReadOnlyList<UiCommand> commands)
    {
        var host = new Host(this, window, scope, localization, commands); hosts.Add(host); host.Bind();
        window.Closed += (_, _) => { host.Clear(); hosts.Remove(host); };
    }
    public async Task ShowPaletteAsync(Window window)
    {
        var host = hosts.LastOrDefault(h => h.Window == window); if (host is null) return;
        var l = host.Localization;
        var dialog = new Window { Width = 620, Height = 520, Title = l["Commands"], WindowStartupLocation = WindowStartupLocation.CenterOwner };
        AppearanceManager.StyleWindow(dialog);
        var panel = new StackPanel { Spacing = 12, Margin = new(22) }; var search = new TextBox { Name = "PaletteSearch", Watermark = l["CommandSearch"] };
        var entries = host.Commands.Select(c => new Entry(c, l[Definitions.Single(d => d.Id == host.Scope + "." + c.Id).LabelKey], Gesture(host.Scope + "." + c.Id))).ToArray();
        var list = new ListBox { Name = "PaletteCommands", ItemsSource = entries, Height = 350 };
        search.TextChanged += (_, _) => { list.ItemsSource = entries.Where(e => e.ToString().Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToArray(); list.SelectedIndex = 0; };
        var execute = new ActionButton { Content = l["Continue"], Icon = "Command", HorizontalAlignment = HorizontalAlignment.Right };
        execute.Click += (_, _) => { if (list.SelectedItem is Entry entry) dialog.Close(entry.Command); };
        dialog.KeyBindings.Add(new KeyBinding { Gesture = new(Key.Escape), Command = new RelayCommand(() => dialog.Close(null)) });
        dialog.KeyBindings.Add(new KeyBinding { Gesture = new(Key.Enter), Command = new RelayCommand(() => { if (list.SelectedItem is Entry entry) dialog.Close(entry.Command); }) });
        panel.Children.Add(search); panel.Children.Add(list); panel.Children.Add(execute); dialog.Content = panel;
        dialog.Opened += (_, _) => { search.Focus(); list.SelectedIndex = 0; };
        var focus = window.FocusManager?.GetFocusedElement();
        var chosen = await dialog.ShowDialog<UiCommand?>(window);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { window.Activate(); focus?.Focus(); }, Avalonia.Threading.DispatcherPriority.Background);
        if (chosen is not null && (chosen.CanExecute?.Invoke() ?? true)) await chosen.Execute();
    }
    private sealed record Entry(UiCommand Command, string Label, string? Gesture) { public override string ToString() => Label + "  ·  " + (Gesture ?? "") + "  " + Command.Id; }
    private sealed class Host(ShortcutManager manager, Window window, string scope, Localization localization, IReadOnlyList<UiCommand> commands)
    {
        private readonly List<KeyBinding> installed = [];
        public Window Window => window; public string Scope => scope; public Localization Localization => localization; public IReadOnlyList<UiCommand> Commands => commands;
        public void Clear() { foreach (var binding in installed) window.KeyBindings.Remove(binding); installed.Clear(); }
        public void Bind()
        {
            Clear();
            foreach (var command in commands)
            {
                var gesture = manager.Gesture(scope + "." + command.Id); if (gesture is null) continue;
                var binding = new KeyBinding { Gesture = ParseGesture(gesture), Command = new AsyncRelayCommand(command.Execute, command.CanExecute ?? (() => true)) };
                installed.Add(binding); window.KeyBindings.Add(binding);
            }
        }
    }
    private static IReadOnlyList<ShortcutDefinition> CreateDefinitions()
    {
        var result = new List<ShortcutDefinition>();
        void Add(string scope, string action, string? key = null, string? gesture = null) => result.Add(new(scope + "." + action, key ?? action, scope, gesture));
        foreach (var action in new[] { "Refresh", "Stage", "StageAll", "Unstage", "UnstageAll", "StagePatch", "UnstagePatch", "Discard", "Commit", "CreateBranch", "SwitchBranch", "RenameBranch", "DeleteBranch", "Merge", "Fetch", "Pull", "Push", "AbortMerge", "ResolveOurs", "ResolveTheirs", "MarkResolved", "ExternalMergeTool", "RestoreSnapshot", "Undo", "Redo" })
            Add("Main", action, action switch { "ResolveOurs" => "Ours", "ResolveTheirs" => "Theirs", "ExternalMergeTool" => "MergeTool", "RestoreSnapshot" => "Restore", _ => action }, action == "Refresh" ? "F5" : action == "Commit" ? "Ctrl+Enter" : null);
        foreach (var action in new[] { "Browse", "Open", "RepositoryActions", "GitSettings", "Appearance", "AdvancedOpen", "ToolsOpen", "Terminal", "ShortcutSettings", "FocusFiles", "FocusHistory", "FocusDiff", "FocusCommit" }) Add("Main", action);
        foreach (var action in new[] { "Init", "Clone", "Trust", "Cancel", "UseOursFile", "UseTheirsFile", "UseBothFile", "SectionOurs", "SectionTheirs", "SectionBoth" }) Add("Main", action);
        Add("Main", "SearchHistory", "ApplyFilters"); Add("Main", "LoadMore"); Add("Main", "LoadLargeDiff", "LoadAnyway"); Add("Main", "WriteConflictResult", "SaveConflict");
        Add("Main", "NextPane", "NextPane", "F6"); Add("Main", "PreviousPane", "PreviousPane", "Shift+F6");
        foreach (var scope in new[] { "Main", "Advanced", "Tools" }) Add(scope, "Palette", "Commands", "Ctrl+Shift+P");
        foreach (var action in new[] { "StashPush", "StashApply", "StashPop", "StashDrop", "LoadPlan", "MoveUp", "MoveDown", "RebaseInteractive", "CherryPick", "Revert", "Reset", "CreateTag", "DeleteTag", "AddRemote", "RenameRemote", "SetRemoteUrl", "RemoveRemote", "ContinueOperation", "SkipOperation", "AbortOperation", "LoadFile" })
            Add("Advanced", action, "ShortcutAdvanced" + action);
        foreach (var action in Enum.GetNames<RepositoryToolAction>()) Add("Tools", action, "ShortcutTools" + action);
        foreach (var scope in new[] { "Advanced", "Tools" }) { Add(scope, "Refresh", "Refresh", "F5"); Add(scope, "Undo", "AdvancedUndo"); }
        return result;
    }
}
