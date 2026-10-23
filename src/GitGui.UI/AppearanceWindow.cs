using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitGui.UI;

public sealed class AppearanceWindow : Window
{
    public AppearanceWindow(AppearanceManager appearance, Localization l)
    {
        Title = l["Appearance"]; Width = 560; CanResize = false;
        SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AppearanceManager.StyleWindow(this);
        l.Bind(this, TitleProperty, "Appearance");
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        var heading = new TextBlock { FontSize = 22, FontWeight = FontWeight.SemiBold }; l.Bind(heading, TextBlock.TextProperty, "Appearance"); panel.Children.Add(heading);
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap }; l.Bind(hint, TextBlock.TextProperty, "AppearanceHint"); panel.Children.Add(hint);
        var status = new TextBlock { Name = "AppearanceStatus", TextWrapping = TextWrapping.Wrap };
        AppearanceManager.Bind(status, TextBlock.ForegroundProperty, "MutedBrush");
        var choices = new List<(Button Button, TextBlock Check, AppearancePalette Palette)>();
        void Update()
        {
            foreach (var (button, check, palette) in choices)
            {
                check.Text = appearance.Current == palette ? l["SelectedTheme"] : "";
                button.Classes.Set("selectedTheme", appearance.Current == palette);
            }
        }
        foreach (var palette in AppearanceManager.Palettes)
        {
            var row = new Grid { ColumnDefinitions = new("68,*,Auto"), ColumnSpacing = 14 };
            var swatch = new Border { Width = 64, Height = 34, CornerRadius = new CornerRadius(5),
                Background = Brush.Parse(palette.Canvas), BorderBrush = Brush.Parse(palette.Border), BorderThickness = new Thickness(1),
                Child = new Border { Background = Brush.Parse(palette.Primary), Width = 24, Height = 8, CornerRadius = new CornerRadius(2),
                    HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(9, 0, 0, 0) } };
            row.Children.Add(swatch);
            var name = new TextBlock { Text = l[palette.NameKey], FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            l.Bind(name, TextBlock.TextProperty, palette.NameKey);
            Grid.SetColumn(name, 1); row.Children.Add(name);
            var check = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            AppearanceManager.Bind(check, TextBlock.ForegroundProperty, "AccentBrush"); Grid.SetColumn(check, 2); row.Children.Add(check);
            var button = new Button { Name = "Theme_" + palette.Id, Content = row, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12), MinHeight = 58 };
            l.Bind(button, Avalonia.Automation.AutomationProperties.NameProperty, palette.NameKey);
            button.Click += (_, _) =>
            {
                try { appearance.Select(palette.Id); status.Text = l["AppearanceSaved"]; Update(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { status.Text = l["AppearanceSaveFailed"] + " " + ex.Message; }
            };
            choices.Add((button, check, palette)); panel.Children.Add(button);
        }
        Update(); panel.Children.Add(status);
        var close = new ActionButton { Icon = "Close", Content = l["Close"], HorizontalAlignment = HorizontalAlignment.Right };
        l.Bind(close, ContentControl.ContentProperty, "Close");
        close.Click += (_, _) => Close(); panel.Children.Add(close); Content = panel;
        KeyBindings.Add(new Avalonia.Input.KeyBinding { Gesture = new(Avalonia.Input.Key.Escape),
            Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => Close()) });
        void LocaleChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Update();
        l.PropertyChanged += LocaleChanged; Closed += (_, _) => l.PropertyChanged -= LocaleChanged;
    }
}
