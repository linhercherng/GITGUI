using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitGui.UI;
using Xunit;

namespace GitGui.UI.Tests;

public class AppearanceTests
{
    [AvaloniaFact]
    public void All_palettes_have_readable_body_diff_and_primary_button_colors()
    {
        var appearance = new AppearanceManager();
        foreach (var palette in AppearanceManager.Palettes)
        {
            appearance.Select(palette.Id);
            Assert.True(Contrast(palette.Text, palette.Panel) >= 4.5);
            Assert.True(Contrast(palette.Muted, palette.Panel) >= 4.5);
            Assert.True(Contrast(palette.Text, palette.Selection) >= 4.5);
            Assert.True(Contrast(palette.Success, palette.IsLight ? "#FAFBFD" : palette.Canvas) >= 4.5);
            Assert.True(Contrast(palette.Danger, palette.IsLight ? "#FAFBFD" : palette.Canvas) >= 4.5);
            Assert.True(Contrast("#FFFFFF", palette.Primary) >= 4.5);
        }
        new AppearanceManager();
        static double Contrast(string first, string second)
        {
            static double Luminance(string value)
            {
                var c = Color.Parse(value);
                static double Linear(byte channel) { var n = channel / 255d; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); }
                return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
            }
            var a = Luminance(first); var b = Luminance(second);
            return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
        }
    }

    [AvaloniaFact]
    public void Unknown_saved_theme_falls_back_and_selecting_current_palette_repairs_preference()
    {
        var store = new MemoryAppearance { ThemeId = "future-theme" };
        var manager = new AppearanceManager(store);
        Assert.Equal("graphite", manager.Current.Id);
        manager.Select("graphite");
        Assert.Equal("graphite", store.ThemeId);
        Assert.Throws<InvalidOperationException>(() => manager.Select("invalid"));
        Assert.Equal("graphite", store.ThemeId);
    }

    [AvaloniaFact]
    public void Saved_palette_is_restored_and_switch_updates_existing_editors_and_dialogs()
    {
        var store = new MemoryAppearance { ThemeId = "porcelain" };
        var themes = new AppearanceManager(store);
        var editor = new CodeEditor();
        var window = new Window { Content = editor }; window.Show();
        try
        {
            Assert.Equal("porcelain", themes.Current.Id);
            Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);
            var before = Assert.IsType<SolidColorBrush>(editor.Background).Color;
            themes.Select("forest");
            Assert.Equal("forest", store.ThemeId);
            Assert.Equal(ThemeVariant.Dark, Application.Current.RequestedThemeVariant);
            Assert.NotEqual(before, Assert.IsType<SolidColorBrush>(editor.Background).Color);
            Assert.Equal("forest", new AppearanceManager(store).Current.Id);
        }
        finally { window.Close(); new AppearanceManager(); }
    }

    [AvaloniaFact]
    public void Save_failure_retains_current_palette_and_picker_reports_error()
    {
        var store = new MemoryAppearance(); var themes = new AppearanceManager(store);
        var picker = new AppearanceWindow(themes, new Localization()); picker.Show();
        try
        {
            store.Fail = true;
            picker.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "Theme_porcelain")
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("graphite", themes.Current.Id);
            Assert.Contains("disk full", picker.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "AppearanceStatus").Text);
            store.Fail = false;
            picker.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "Theme_porcelain")
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("porcelain", themes.Current.Id);
        }
        finally { picker.Close(); new AppearanceManager(); }
    }

    private sealed class MemoryAppearance : GitGui.Core.IAppearancePreferences
    {
        public string? ThemeId { get; set; }
        public bool Fail { get; set; }
        public string? LoadTheme() => ThemeId;
        public void SaveTheme(string id) { if (Fail) throw new IOException("disk full"); ThemeId = id; }
    }
}
