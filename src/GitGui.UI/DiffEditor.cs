using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace GitGui.UI;

/// <summary>AvaloniaEdit virtualizes document lines. Diff marks remain textual as well as colored.</summary>
public sealed class DiffEditor : Grid
{
    public static readonly StyledProperty<string> SourceProperty = AvaloniaProperty.Register<DiffEditor, string>(nameof(Source), "");
    public static readonly StyledProperty<string> LeftSourceProperty = AvaloniaProperty.Register<DiffEditor, string>(nameof(LeftSource), "");
    public static readonly StyledProperty<string> RightSourceProperty = AvaloniaProperty.Register<DiffEditor, string>(nameof(RightSource), "");
    public static readonly StyledProperty<bool> IsSplitProperty = AvaloniaProperty.Register<DiffEditor, bool>(nameof(IsSplit));
    public string Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public string LeftSource { get => GetValue(LeftSourceProperty); set => SetValue(LeftSourceProperty, value); }
    public string RightSource { get => GetValue(RightSourceProperty); set => SetValue(RightSourceProperty, value); }
    public bool IsSplit { get => GetValue(IsSplitProperty); set => SetValue(IsSplitProperty, value); }
    private readonly TextEditor inline = CreateEditor();
    private readonly TextEditor left = CreateEditor();
    private readonly TextEditor right = CreateEditor();
    private readonly Grid split = new() { ColumnDefinitions = new("*,6,*") };
    private bool scrolling;
    public bool IsReadOnly => true;

    public DiffEditor()
    {
        split.Children.Add(left);
        var divider = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brush.Parse("#303A49") };
        SetColumn(divider, 1); split.Children.Add(divider);
        SetColumn(right, 2); split.Children.Add(right);
        Children.Add(inline); Children.Add(split);
        split.IsVisible = false;
        // Diff projections are line-aligned, so both panes share vertical scroll offsets.
        left.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(left, right);
        right.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(right, left);
    }
    private void Sync(TextEditor from, TextEditor to)
    {
        if (scrolling || !IsSplit) return;
        scrolling = true;
        try { to.ScrollToVerticalOffset(from.VerticalOffset); }
        finally { scrolling = false; }
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty) inline.Text = Source;
        else if (change.Property == LeftSourceProperty) left.Text = LeftSource;
        else if (change.Property == RightSourceProperty) right.Text = RightSource;
        else if (change.Property == IsSplitProperty) { inline.IsVisible = !IsSplit; split.IsVisible = IsSplit; }
    }
    private static TextEditor CreateEditor()
    {
        var editor = new TextEditor
        {
            IsReadOnly = true, ShowLineNumbers = true, WordWrap = false,
            FontSize = 12, FontFamily = new("Cascadia Code,Consolas,Menlo,Microsoft JhengHei,PingFang TC,Noto Sans TC,monospace"),
            Background = Brush.Parse("#151A22"), Foreground = Brush.Parse("#DCE5F2"),
            LineNumbersForeground = Brush.Parse("#9AABC3"),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        editor.TextArea.TextView.LineTransformers.Add(new DiffColorizer());
        return editor;
    }
    private sealed class DiffColorizer : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            var text = CurrentContext.Document.GetText(line);
            var color = text.StartsWith("@@", StringComparison.Ordinal) ? "#98BFFF"
                : text.StartsWith('+') && !text.StartsWith("+++", StringComparison.Ordinal) ? "#8CE4AF"
                : text.StartsWith('-') && !text.StartsWith("---", StringComparison.Ordinal) ? "#FFABAC" : null;
            if (color is null) return;
            ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(Brush.Parse(color)));
        }
    }
}
