using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GitGui.UI;

public sealed partial class ShortcutSettingsWindow : Window
{
    private sealed partial class Row : ObservableObject
    {
        public ShortcutDefinition Definition { get; }
        private readonly Localization localization;
        public string Label => localization[Definition.LabelKey];
        [ObservableProperty] private string gesture;
        public Row(ShortcutDefinition definition, Localization localization, string? value) { Definition = definition; this.localization = localization; gesture = value ?? ""; }
    }
    public ShortcutSettingsWindow(ShortcutManager shortcuts, Localization l)
    {
        Width = 760; Height = 650; MinWidth = 620; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AppearanceManager.StyleWindow(this); Title = l["ShortcutSettings"];
        l.Bind(this, TitleProperty, "ShortcutSettings");
        var grid = new Grid { RowDefinitions = new("Auto,Auto,*,Auto,Auto"), RowSpacing = 10, Margin = new(22) };
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap }; l.Bind(hint, TextBlock.TextProperty, "ShortcutHint"); grid.Children.Add(hint);
        var search = new TextBox { Watermark = l["CommandSearch"] }; Grid.SetRow(search, 1); grid.Children.Add(search);
        l.Bind(search, TextBox.WatermarkProperty, "CommandSearch");
        var rows = ShortcutManager.Definitions.Select(d => new Row(d, l, shortcuts.Gesture(d.Id))).ToArray();
        var stack = new StackPanel { Spacing = 8 }; var scroller = new ScrollViewer { Content = stack }; Grid.SetRow(scroller, 2); grid.Children.Add(scroller);
        var notice = new TextBlock { TextWrapping = TextWrapping.Wrap }; Grid.SetRow(notice, 3); grid.Children.Add(notice);
        void Populate()
        {
            stack.Children.Clear();
            foreach (var row in rows.Where(r => (r.Label + r.Definition.Id).Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)))
            {
                var item = new Grid { ColumnDefinitions = new("*,165,Auto"), ColumnSpacing = 8 };
                item.Children.Add(new TextBlock { Text = l["ShortcutScope" + row.Definition.Scope] + " · " + row.Label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
                var input = new TextBox { Text = row.Gesture, Watermark = "Ctrl+Shift+K" }; input.TextChanged += (_, _) => row.Gesture = input.Text ?? ""; Grid.SetColumn(input, 1); item.Children.Add(input);
                Avalonia.Automation.AutomationProperties.SetName(input, row.Label + " · " + l["ShortcutGesture"]);
                var save = new ActionButton { Content = l["ShortcutSave"], Icon = "Save" }; save.Click += (_, _) =>
                {
                    try { shortcuts.SetGesture(row.Definition.Id, row.Gesture); row.Gesture = shortcuts.Gesture(row.Definition.Id) ?? ""; input.Text = row.Gesture; notice.Text = l["ShortcutSaved"]; }
                    catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or UnauthorizedAccessException) { notice.Text = l[ex.Message is "ShortcutReserved" or "ShortcutDuplicate" or "ShortcutInvalid" ? ex.Message : "ShortcutSaveFailed"]; }
                }; Grid.SetColumn(save, 2); item.Children.Add(save); stack.Children.Add(item);
            }
        }
        Populate(); search.TextChanged += (_, _) => Populate();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var reset = new ActionButton { Content = l["ShortcutReset"], Icon = "Undo" }; reset.Click += (_, _) =>
        { try { shortcuts.Reset(); foreach (var row in rows) row.Gesture = shortcuts.Gesture(row.Definition.Id) ?? ""; Populate(); notice.Text = l["ShortcutSaved"]; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notice.Text = l["ShortcutSaveFailed"]; } };
        var close = new ActionButton { Content = l["Close"], Icon = "Close" }; close.Click += (_, _) => Close(); actions.Children.Add(reset); actions.Children.Add(close); Grid.SetRow(actions, 4); grid.Children.Add(actions); Content = grid;
        l.Bind(reset, ContentControl.ContentProperty, "ShortcutReset"); l.Bind(close, ContentControl.ContentProperty, "Close");
        void LocaleChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Populate();
        l.PropertyChanged += LocaleChanged; Closed += (_, _) => l.PropertyChanged -= LocaleChanged;
        Opened += (_, _) => search.Focus(); KeyBindings.Add(new() { Gesture = new(Avalonia.Input.Key.Escape), Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => Close()) });
    }
}
