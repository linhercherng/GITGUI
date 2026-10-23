using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitGui.UI;

/// <summary>Visible text remains Button.Content for localization and accessible names.</summary>
public sealed class ActionButton : Button
{
    public static readonly StyledProperty<string> IconProperty = AvaloniaProperty.Register<ActionButton, string>(nameof(Icon), "Command");
    public string Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    protected override Type StyleKeyOverride => typeof(Button);
    public ActionButton()
    {
        ContentTemplate = new FuncDataTemplate<object>((content, _) =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var icon = new ActionIcon { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            icon.Bind(ActionIcon.KindProperty, new Binding(nameof(Icon)) { Source = this });
            panel.Children.Add(icon);
            panel.Children.Add(new TextBlock { Text = content?.ToString(), VerticalAlignment = VerticalAlignment.Center });
            return panel;
        });
    }
}

/// <summary>Original 24-unit stroke artwork; no icon font or platform glyph dependency.</summary>
public sealed class ActionIcon : Control
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<ActionIcon, string>(nameof(Kind), "Command");
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<ActionIcon>();
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    private static readonly IReadOnlyDictionary<string, Geometry> Artwork = new Dictionary<string, string>
    {
        ["Folder"] = "M3 7V5H9L11 7H21V20H3Z M3 10H21",
        ["Open"] = "M14 4H21V11 M21 4L11 14 M10 4H4V20H20V14",
        ["Repository"] = "M4 4H20V20H4Z M8 4V20 M12 8H17 M12 12H17",
        ["Settings"] = "M4 7H20 M4 17H20 M8 4V10 M16 14V20",
        ["Palette"] = "M21 12A9 9 0 1 0 12 21H14A2 2 0 0 0 14 17H13A2 2 0 0 1 13 13H19Q21 13 21 12 M7 8h2 M13 6h2 M16 9h2 M5 13h2",
        ["Fetch"] = "M12 3V15 M7 10L12 15L17 10 M4 16V21H20V16",
        ["Pull"] = "M12 3V18 M7 13L12 18L17 13 M4 21H20",
        ["Push"] = "M12 21V6 M7 11L12 6L17 11 M4 3H20",
        ["Undo"] = "M4 5V11H10 M4 11Q7 4 14 6Q22 8 19 16Q17 22 9 20",
        ["Redo"] = "M20 5V11H14 M20 11Q17 4 10 6Q2 8 5 16Q7 22 15 20",
        ["Plus"] = "M12 4V20 M4 12H20",
        ["Minus"] = "M4 12H20",
        ["StageAll"] = "M7 4V14 M2 9H12 M17 10V20 M12 15H22",
        ["UnstageAll"] = "M2 8H12 M12 16H22",
        ["Branch"] = "M6 8V16 M6 12H12Q18 12 18 8 M9 5A3 3 0 1 0 3 5A3 3 0 1 0 9 5 M9 19A3 3 0 1 0 3 19A3 3 0 1 0 9 19 M21 5A3 3 0 1 0 15 5A3 3 0 1 0 21 5",
        ["Switch"] = "M4 7H20L16 3 M20 17H4L8 21 M20 7L16 11 M4 17L8 13",
        ["Merge"] = "M6 3V8Q6 12 12 12H18 M18 3V21 M14 17L18 21L22 17",
        ["Rename"] = "M4 20L5 14L16 3L21 8L10 19Z M13 6L18 11",
        ["Trash"] = "M3 6H21 M8 6V3H16V6 M6 6L7 21H17L18 6 M10 10V17 M14 10V17",
        ["Commit"] = "M3 12H7 M17 12H21 M17 12A5 5 0 1 0 7 12A5 5 0 1 0 17 12",
        ["Check"] = "M4 12L9 17L20 6",
        ["Close"] = "M5 5L19 19 M19 5L5 19",
        ["Refresh"] = "M20 4V10H14 M20 10A8 8 0 1 0 20 16",
        ["Copy"] = "M8 8H21V21H8Z M16 8V3H3V16H8",
        ["Save"] = "M4 3H17L21 7V21H3V3Z M7 3V9H17V3 M7 21V14H17V21",
        ["Shield"] = "M12 3L21 6V12Q20 18 12 22Q4 18 3 12V6Z M7 12L11 16L17 9",
        ["Search"] = "M16 10A6 6 0 1 0 4 10A6 6 0 1 0 16 10 M14 15L21 22",
        ["More"] = "M6 10L12 16L18 10",
        ["Command"] = "M4 5L10 12L4 19 M13 19H21"
    }.ToDictionary(k => k.Key, v => Geometry.Parse(v.Value));
    static ActionIcon() => AffectsRender<ActionIcon>(KindProperty, ForegroundProperty);
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        using (context.PushTransform(Matrix.CreateScale(Bounds.Width / 24, Bounds.Height / 24)))
            context.DrawGeometry(null, new Pen(Foreground ?? Brushes.Gray, 1.75, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
                Artwork.TryGetValue(Kind, out var shape) ? shape : Artwork["Command"]);
    }
}
