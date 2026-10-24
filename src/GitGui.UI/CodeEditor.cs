using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Automation;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaloniaEdit;
using AvaloniaEdit.Editing;

namespace GitGui.UI;

public sealed class CodeEditor : TextEditor
{
    public static readonly StyledProperty<string> AccessibleNameProperty = AvaloniaProperty.Register<CodeEditor, string>(nameof(AccessibleName), "Text document");
    public string AccessibleName { get => GetValue(AccessibleNameProperty); set => SetValue(AccessibleNameProperty, value); }
    public static readonly StyledProperty<string> CodeTextProperty = AvaloniaProperty.Register<CodeEditor, string>(nameof(CodeText), "", defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public string CodeText { get => GetValue(CodeTextProperty); set => SetValue(CodeTextProperty, value); }
    private bool updatingText;
    public Task DisplayReady => TextArea.TextView is IncrementalTextView view ? view.DisplayReady : Task.CompletedTask;
    protected override Type StyleKeyOverride => typeof(TextEditor);
    public CodeEditor() : this(false) { }
    internal CodeEditor(bool incrementalRendering) : base(incrementalRendering ? new DiffTextArea() : new TextArea())
    {
        IsReadOnly = true; ShowLineNumbers = true; WordWrap = false; FontSize = 12;
        FontFamily = new("Cascadia Code,Consolas,Menlo,Microsoft JhengHei,PingFang TC,Noto Sans TC,monospace");
        AppearanceManager.Bind(this, BackgroundProperty, "EditorBrush");
        AppearanceManager.Bind(this, ForegroundProperty, "TextBrush");
        AppearanceManager.Bind(this, LineNumbersForegroundProperty, "MutedBrush");
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        TextChanged += (_, _) => { if (!updatingText && CodeText != Text) SetCurrentValue(CodeTextProperty, Text); };
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AccessibleNameProperty)
            ControlAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, change.OldValue, change.NewValue);
        if (change.Property == CodeTextProperty)
            ControlAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, change.OldValue, change.NewValue);
        if (change.Property != CodeTextProperty || Text == CodeText) return;
        updatingText = true;
        try
        {
            (TextArea.TextView as IncrementalTextView)?.BeginUpdate();
            Text = CodeText;
        }
        finally { updatingText = false; }
    }
    private sealed class DiffTextArea() : TextArea(new IncrementalTextView())
    {
        protected override Type StyleKeyOverride => typeof(TextArea);
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new EditorPeer(this);
    private sealed class EditorPeer(CodeEditor editor) : ControlAutomationPeer(editor), IValueProvider
    {
        protected override string GetNameCore() => editor.AccessibleName;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;
        protected override object? GetProviderCore(Type type) => type == typeof(IValueProvider) ? this : base.GetProviderCore(type);
        public bool IsReadOnly => editor.IsReadOnly || !editor.IsEnabled;
        public string Value => editor.CodeText;
        public void SetValue(string? value) { if (IsReadOnly) throw new InvalidOperationException("Document is read-only."); editor.CodeText = value ?? ""; }
    }
}

/// <summary>Owns its decoded bitmap and releases it when the source changes or the tab detaches.</summary>
public sealed class PreviewImage : Border
{
    public static readonly StyledProperty<string> AccessibleNameProperty = AvaloniaProperty.Register<PreviewImage, string>(nameof(AccessibleName), "Image");
    public string AccessibleName { get => GetValue(AccessibleNameProperty); set => SetValue(AccessibleNameProperty, value); }
    public static readonly StyledProperty<byte[]?> BytesProperty = AvaloniaProperty.Register<PreviewImage, byte[]?>(nameof(Bytes));
    public byte[]? Bytes { get => GetValue(BytesProperty); set => SetValue(BytesProperty, value); }
    private readonly Image image = new() { Stretch = Stretch.Uniform, MaxHeight = 560 };
    private Bitmap? bitmap;
    public PreviewImage() { Child = image; AppearanceManager.Bind(this, BackgroundProperty, "EditorBrush"); Padding = new Thickness(8); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BytesProperty) Load();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); if (bitmap is null) Load(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { image.Source = null; bitmap?.Dispose(); bitmap = null; base.OnDetachedFromVisualTree(e); }
    private void Load()
    {
        image.Source = null; bitmap?.Dispose(); bitmap = null;
        if (Bytes is null || Bytes.Length == 0) return;
        try { using var stream = new MemoryStream(Bytes, writable: false); bitmap = Bitmap.DecodeToWidth(stream, 2048); image.Source = bitmap; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException) { ToolTip.SetTip(this, ex.Message); }
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new ImagePeer(this);
    private sealed class ImagePeer(PreviewImage image) : ControlAutomationPeer(image)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetNameCore() => image.AccessibleName;
    }
}
