using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaloniaEdit;

namespace GitGui.UI;

public sealed class CodeEditor : TextEditor
{
    public static readonly StyledProperty<string> CodeTextProperty = AvaloniaProperty.Register<CodeEditor, string>(nameof(CodeText), "", defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public string CodeText { get => GetValue(CodeTextProperty); set => SetValue(CodeTextProperty, value); }
    private bool updatingText;
    protected override Type StyleKeyOverride => typeof(TextEditor);
    public CodeEditor()
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
        if (change.Property != CodeTextProperty || Text == CodeText) return;
        updatingText = true;
        try { Text = CodeText; }
        finally { updatingText = false; }
    }
}

/// <summary>Owns its decoded bitmap and releases it when the source changes or the tab detaches.</summary>
public sealed class PreviewImage : Border
{
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
}
