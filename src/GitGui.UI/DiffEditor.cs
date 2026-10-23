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
    public static readonly StyledProperty<string> DocumentNameProperty = AvaloniaProperty.Register<DiffEditor, string>(nameof(DocumentName), "Diff");
    public static readonly StyledProperty<string> BeforeLabelProperty = AvaloniaProperty.Register<DiffEditor, string>(nameof(BeforeLabel), "Before");
    public static readonly StyledProperty<string> AfterLabelProperty = AvaloniaProperty.Register<DiffEditor, string>(nameof(AfterLabel), "After");
    public string DocumentName { get => GetValue(DocumentNameProperty); set => SetValue(DocumentNameProperty, value); }
    public string BeforeLabel { get => GetValue(BeforeLabelProperty); set => SetValue(BeforeLabelProperty, value); }
    public string AfterLabel { get => GetValue(AfterLabelProperty); set => SetValue(AfterLabelProperty, value); }
    public static readonly StyledProperty<string> SourceProperty = AvaloniaProperty.Register<DiffEditor, string>(nameof(Source), "");
    public static readonly StyledProperty<string> LeftSourceProperty = AvaloniaProperty.Register<DiffEditor, string>(nameof(LeftSource), "");
    public static readonly StyledProperty<string> RightSourceProperty = AvaloniaProperty.Register<DiffEditor, string>(nameof(RightSource), "");
    public static readonly StyledProperty<bool> IsSplitProperty = AvaloniaProperty.Register<DiffEditor, bool>(nameof(IsSplit));
    public string Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public string LeftSource { get => GetValue(LeftSourceProperty); set => SetValue(LeftSourceProperty, value); }
    public string RightSource { get => GetValue(RightSourceProperty); set => SetValue(RightSourceProperty, value); }
    public bool IsSplit { get => GetValue(IsSplitProperty); set => SetValue(IsSplitProperty, value); }
    private readonly CodeEditor inline = CreateEditor();
    private readonly CodeEditor left = CreateEditor();
    private readonly CodeEditor right = CreateEditor();
    private readonly Grid split = new() { ColumnDefinitions = new("*,6,*") };
    private bool scrolling;
    public bool IsReadOnly => true;
    public void FocusDocument() => (IsSplit ? left : inline).TextArea.Focus();

    public DiffEditor()
    {
        split.Children.Add(left);
        var divider = new GridSplitter { ResizeDirection = GridResizeDirection.Columns };
        AppearanceManager.Bind(divider, BackgroundProperty, "BorderBrush");
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
        if (change.Property == SourceProperty && !IsSplit) inline.CodeText = Source;
        else if (change.Property == LeftSourceProperty && IsSplit) left.CodeText = LeftSource;
        else if (change.Property == RightSourceProperty && IsSplit) right.CodeText = RightSource;
        else if (change.Property == IsSplitProperty)
        {
            inline.IsVisible = !IsSplit; split.IsVisible = IsSplit;
            if (IsSplit) { left.CodeText = LeftSource; right.CodeText = RightSource; } else inline.CodeText = Source;
        }
        inline.AccessibleName = DocumentName; left.AccessibleName = DocumentName + " · " + BeforeLabel; right.AccessibleName = DocumentName + " · " + AfterLabel;
    }
    private static CodeEditor CreateEditor()
    {
        var editor = new CodeEditor
        {
            IsReadOnly = true, ShowLineNumbers = true, WordWrap = false,
            FontSize = 12, FontFamily = new("Cascadia Code,Consolas,Menlo,Microsoft JhengHei,PingFang TC,Noto Sans TC,monospace"),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        AppearanceManager.Bind(editor, TextEditor.BackgroundProperty, "EditorBrush");
        AppearanceManager.Bind(editor, TextEditor.ForegroundProperty, "TextBrush");
        AppearanceManager.Bind(editor, TextEditor.LineNumbersForegroundProperty, "MutedBrush");
        editor.PropertyChanged += (_, e) => { if (e.Property == TextEditor.ForegroundProperty) editor.TextArea.TextView.Redraw(); };
        editor.TextArea.TextView.LineTransformers.Add(new DiffColorizer(editor));
        return editor;
    }
    private sealed class DiffColorizer(TextEditor editor) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            var text = CurrentContext.Document.GetText(line);
            var key = text.StartsWith("@@", StringComparison.Ordinal) ? "AccentBrush"
                : text.StartsWith('+') && !text.StartsWith("+++", StringComparison.Ordinal) ? "SuccessBrush"
                : text.StartsWith('-') && !text.StartsWith("---", StringComparison.Ordinal) ? "DangerBrush" : null;
            if (key is null || editor.FindResource(key) is not IBrush brush) return;
            ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }
}
