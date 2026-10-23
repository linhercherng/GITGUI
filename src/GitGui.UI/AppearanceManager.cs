using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using GitGui.Core;

namespace GitGui.UI;

public sealed record AppearancePalette(string Id, string NameKey, bool IsLight,
    string Canvas, string Panel, string Raised, string Border, string Text, string Muted,
    string Accent, string Primary, string PrimaryHover, string Selection, string Success, string Danger,
    string Warning, string WarningSurface, string ConflictSurface);

public sealed class AppearanceManager
{
    public static IReadOnlyList<AppearancePalette> Palettes { get; } = Array.AsReadOnly<AppearancePalette>([
        new("graphite", "ThemeGraphite", false, "#14171D", "#1D2129", "#272D37", "#353D4B", "#E8ECF3", "#AFBACB",
            "#94BFFF", "#255CAE", "#306CC1", "#2B3E5A", "#8EE0B0", "#FFB0B6", "#F1CD92", "#35301F", "#392737"),
        new("midnight", "ThemeMidnight", false, "#0E1828", "#152338", "#1D3049", "#354A65", "#E6EEF9", "#ADBED5",
            "#89CDFA", "#155A8A", "#206D9F", "#224968", "#8BE0C0", "#FFB2BE", "#F5CE95", "#343021", "#392B43"),
        new("forest", "ThemeForest", false, "#111D1A", "#192B25", "#243A31", "#3D5448", "#E9F0E8", "#B0C4B8",
            "#9BD8B4", "#256344", "#307652", "#2F4C3D", "#A6E2B4", "#FFB4B4", "#E9D19B", "#343323", "#3C2E38"),
        new("porcelain", "ThemePorcelain", true, "#EEF1F5", "#FFFFFF", "#F5F7FA", "#CFD7E2", "#202D40", "#53657A",
            "#245B9F", "#255CAE", "#1B4C94", "#DDE9F9", "#176D44", "#A92338", "#805315", "#FFF4D9", "#F9E9F0")
    ]);
    private readonly IAppearancePreferences? preferences;
    public AppearancePalette Current { get; private set; }

    public AppearanceManager(IAppearancePreferences? preferences = null)
    {
        this.preferences = preferences;
        var id = preferences?.LoadTheme();
        Current = Palettes.FirstOrDefault(p => p.Id == id) ?? Palettes[0];
        Apply();
    }

    public void Select(string id)
    {
        var palette = Palettes.Single(p => p.Id == id);
        // Persist first: an I/O failure must not claim a different saved preference.
        preferences?.SaveTheme(id);
        if (Current == palette) return;
        Current = palette; Apply();
    }

    private void Apply()
    {
        var app = Application.Current ?? throw new InvalidOperationException("Appearance requires the UI application.");
        var p = Current;
        var brushes = new Dictionary<string, string>
        {
            ["CanvasBrush"] = p.Canvas, ["PanelBrush"] = p.Panel, ["RaisedBrush"] = p.Raised,
            ["BorderBrush"] = p.Border, ["TextBrush"] = p.Text, ["MutedBrush"] = p.Muted,
            ["AccentBrush"] = p.Accent, ["PrimaryBrush"] = p.Primary, ["PrimaryHoverBrush"] = p.PrimaryHover,
            ["SelectionBrush"] = p.Selection, ["SuccessBrush"] = p.Success, ["DangerBrush"] = p.Danger,
            ["WarningBrush"] = p.Warning, ["WarningSurfaceBrush"] = p.WarningSurface,
            ["ConflictSurfaceBrush"] = p.ConflictSurface, ["EditorBrush"] = p.IsLight ? "#FAFBFD" : p.Canvas,
            ["PrimaryTextBrush"] = "#FFFFFF", ["DestructiveBrush"] = p.IsLight ? "#B02A40" : "#A63447"
        };
        foreach (var (key, value) in brushes) app.Resources[key] = new SolidColorBrush(Color.Parse(value));
        var lanes = p.IsLight ? new[] { "#245B9F", "#7451B4", "#217354", "#986014", "#AD3C72" }
            : new[] { p.Accent, "#BEA5F6", p.Success, "#E7BD7B", "#EB9FC8" };
        for (var i = 0; i < lanes.Length; i++) app.Resources["GraphLane" + i] = Brush.Parse(lanes[i]);
        // Fluent templates (menus, inputs, selection, focus) follow the same variant/accent.
        var accent = Color.Parse(p.Accent);
        app.Resources["SystemAccentColor"] = accent;
        foreach (var suffix in new[] { "Light1", "Light2", "Light3", "Dark1", "Dark2", "Dark3" })
            app.Resources["SystemAccentColor" + suffix] = accent;
        app.RequestedThemeVariant = p.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    internal static void Bind(AvaloniaObject target, AvaloniaProperty property, string resource)
        => target.Bind(property, new DynamicResourceExtension(resource));

    internal static void StyleWindow(Window window)
    {
        if (!Application.Current!.Resources.ContainsKey("CanvasBrush")) _ = new AppearanceManager();
        Bind(window, Window.BackgroundProperty, "PanelBrush");
        Bind(window, Window.ForegroundProperty, "TextBrush");
        using var icon = Avalonia.Platform.AssetLoader.Open(new Uri("avares://GitGui.UI/Assets/gitgui.ico"));
        window.Icon = new WindowIcon(icon);
        window.Styles.Add(new StyleInclude(new Uri("avares://GitGui.UI/")) { Source = new Uri("avares://GitGui.UI/AppearanceStyles.axaml") });
    }
}
