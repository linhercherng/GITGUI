using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitGui.Controls;
using GitGui.Core;
using GitGui.UI;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class AccessibilityTests
{
    [Fact]
    public void Swapped_default_shortcuts_survive_restart_as_one_effective_configuration()
    {
        var preferences = new Preferences(); var manager = new ShortcutManager(preferences);
        manager.SetGesture("Main.Refresh", null); manager.SetGesture("Main.Commit", "F5"); manager.SetGesture("Main.Refresh", "Ctrl+Enter");
        var restored = new ShortcutManager(preferences);
        Assert.Equal("F5", restored.Gesture("Main.Commit")); Assert.Equal(manager.Gesture("Main.Refresh"), restored.Gesture("Main.Refresh"));
    }
    [AvaloniaFact]
    public async Task Failure_feedback_translates_live_routes_explicit_remedies_and_blocks_during_writes()
    {
        var l = new Localization(); var allowed = true; using var feedback = new OperationFeedback(l, () => allowed);
        var routes = new List<GitSuggestedAction>(); var retries = 0;
        feedback.Navigate = action => { routes.Add(action); return Task.CompletedTask; };
        var bar = new OperationFeedbackBar { Feedback = feedback }; var window = new Window { Content = bar }; window.Show();
        feedback.Record(new(false, "raw diagnostic", GitErrorKind.LockExists));
        Assert.True(bar.IsVisible); Assert.Equal(l["ErrorLockExists"], feedback.Summary);
        var chinese = feedback.ActionLabel; l.SetCulture("en"); Assert.NotEqual(chinese, feedback.ActionLabel);
        await feedback.RemedyCommand.ExecuteAsync(null); Assert.Equal(GitSuggestedAction.LockHelp, Assert.Single(routes));
        feedback.Record(new(false, "network", GitErrorKind.Network), () => { retries++; return Task.CompletedTask; });
        allowed = false; feedback.Notify(); await feedback.RemedyCommand.ExecuteAsync(null); Assert.Equal(0, retries);
        allowed = true; feedback.Notify(); await feedback.RemedyCommand.ExecuteAsync(null); Assert.Equal(1, retries);
        feedback.Record(new(false, "cancel", GitErrorKind.Network, Cancelled: true)); Assert.False(bar.IsVisible);
        feedback.Record(new(true, "done")); Assert.False(feedback.HasAction); window.Close();
    }

    [AvaloniaFact]
    public void Open_preferences_localize_live_and_shortcut_settings_preserve_native_tab_navigation()
    {
        var l = new Localization(); var appearance = new AppearanceWindow(new AppearanceManager(), l);
        var shortcuts = new ShortcutSettingsWindow(new ShortcutManager(), l);
        appearance.Show(); shortcuts.Show(); var originalTitle = appearance.Title;
        l.SetCulture("en"); Assert.NotEqual(originalTitle, appearance.Title); Assert.Equal(l["ShortcutSettings"], shortcuts.Title);
        var search = shortcuts.GetVisualDescendants().OfType<TextBox>().First(); search.Focus();
        shortcuts.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None); shortcuts.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        Assert.NotSame(search, shortcuts.FocusManager?.GetFocusedElement());
        shortcuts.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Shift); shortcuts.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.Shift);
        Assert.Same(search, shortcuts.FocusManager?.GetFocusedElement());
        shortcuts.Close(); appearance.Close();
    }
    [Fact]
    public void Resource_catalog_has_matching_keys_placeholders_and_every_shortcut_label()
    {
        using var zhStream = typeof(Localization).Assembly.GetManifestResourceStream("GitGui.UI.Resources.zh-TW.json")!;
        using var enStream = typeof(Localization).Assembly.GetManifestResourceStream("GitGui.UI.Resources.en.json")!;
        using var zh = System.Text.Json.JsonDocument.Parse(zhStream); using var en = System.Text.Json.JsonDocument.Parse(enStream);
        var chinese = zh.RootElement.EnumerateObject().ToArray(); var english = en.RootElement.EnumerateObject().ToArray();
        Assert.Equal(chinese.Length, chinese.Select(p => p.Name).Distinct().Count()); Assert.Equal(english.Length, english.Select(p => p.Name).Distinct().Count());
        Assert.Equal(chinese.Select(p => p.Name).Order(), english.Select(p => p.Name).Order());
        foreach (var property in chinese)
        {
            Assert.False(string.IsNullOrWhiteSpace(property.Value.GetString())); Assert.False(string.IsNullOrWhiteSpace(en.RootElement.GetProperty(property.Name).GetString()));
            static string[] Placeholders(string value) => System.Text.RegularExpressions.Regex.Matches(value, @"\{\d+\}").Select(m => m.Value).Order().ToArray();
            Assert.Equal(Placeholders(property.Value.GetString()!), Placeholders(en.RootElement.GetProperty(property.Name).GetString()!));
        }
        foreach (var definition in ShortcutManager.Definitions) Assert.True(en.RootElement.TryGetProperty(definition.LabelKey, out _), definition.LabelKey);
    }
    [AvaloniaFact]
    public void Shortcuts_validate_persist_unbind_and_respect_execution_gates()
    {
        var prefs = new Preferences(); var manager = new ShortcutManager(prefs); var localization = new Localization();
        var window = new Window(); var executed = 0; var allowed = false;
        manager.Attach(window, "Main", localization, [new("Refresh", () => { executed++; return Task.CompletedTask; }, () => allowed)]);
        manager.SetGesture("Main.Refresh", "Ctrl+R"); Assert.Equal("Ctrl+R", new ShortcutManager(prefs).Gesture("Main.Refresh"));
        Assert.Throws<ArgumentException>(() => manager.SetGesture("Main.Commit", "Ctrl+R"));
        Assert.Throws<ArgumentException>(() => manager.SetGesture("Main.Commit", "A"));
        Assert.Throws<ArgumentException>(() => manager.SetGesture("Main.Commit", "Ctrl+C"));
        window.Show(); window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.Control); Assert.Equal(0, executed);
        allowed = true; window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.Control); Assert.Equal(1, executed);
        manager.SetGesture("Main.Refresh", null); window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.Control); Assert.Equal(1, executed);
        manager.Reset(); Assert.Equal("F5", manager.Gesture("Main.Refresh")); window.Close();
        manager.SetCulture("en"); Assert.Equal("en", new ShortcutManager(prefs).Culture);
    }

    [AvaloniaFact]
    public async Task Graph_peer_exposes_virtual_rows_keyboard_selection_full_names_and_scroll()
    {
        var commits = Enumerable.Range(0, 10_000).Select(i => new CommitInfo(i.ToString("x40"), "中文提交 " + i, "Author", DateTimeOffset.UnixEpoch, [])).ToArray();
        var graph = new CommitGraphControl { Commits = commits, Height = 150, Width = 600 };
        var window = new Window { Content = graph, Width = 640, Height = 180 }; window.Show(); await graph.LayoutReady;
        window.UpdateLayout(); var peer = ControlAutomationPeer.CreatePeerForElement(graph)!;
        var selection = Assert.IsAssignableFrom<ISelectionProvider>(peer.GetProvider<ISelectionProvider>());
        Assert.InRange(peer.GetChildren()!.Count, 1, 20);
        graph.Focus(); window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None); Assert.Equal(commits[^1], graph.SelectedCommit);
        var selected = Assert.Single(selection.GetSelection()); Assert.Contains(commits[^1].Hash, selected.GetName());
        Assert.True(Assert.IsAssignableFrom<ISelectionItemProvider>(selected.GetProvider<ISelectionItemProvider>()).IsSelected);
        Assert.NotNull(peer.GetProvider<IScrollProvider>());
        window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None); Assert.Equal(commits[0], graph.SelectedCommit);
        window.Close();
    }

    [AvaloniaFact]
    public void Editors_expose_named_readonly_values_and_preserve_editable_result_semantics()
    {
        var editor = new CodeEditor { AccessibleName = "中文.cs · merge result", CodeText = "editable\n", IsReadOnly = false };
        var window = new Window { Content = editor }; window.Show();
        var peer = ControlAutomationPeer.CreatePeerForElement(editor)!;
        Assert.Contains("中文.cs", peer.GetName());
        var value = Assert.IsAssignableFrom<IValueProvider>(peer.GetProvider<IValueProvider>());
        Assert.False(value.IsReadOnly); value.SetValue("updated\n"); Assert.Equal("updated\n", editor.CodeText);
        editor.IsReadOnly = true; Assert.True(value.IsReadOnly); Assert.Throws<InvalidOperationException>(() => value.SetValue("bad"));
        window.Close();
    }
    private sealed class Preferences : IUiPreferences
    {
        private UiPreferences saved = new(); public UiPreferences Load() => saved; public void Save(UiPreferences preferences) => saved = preferences;
    }
}
