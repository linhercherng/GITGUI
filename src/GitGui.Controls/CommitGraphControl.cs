using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using Avalonia.Utilities;
using GitGui.Core;

namespace GitGui.Controls;

/// <summary>Draws only the graph rows intersecting the viewport; no per-commit visual tree.</summary>
public sealed class CommitGraphControl : Control
{
    public static readonly StyledProperty<string> AccessibleNameProperty = AvaloniaProperty.Register<CommitGraphControl, string>(nameof(AccessibleName), "Commit history");
    public static readonly StyledProperty<string> EmptyTextProperty = AvaloniaProperty.Register<CommitGraphControl, string>(nameof(EmptyText), "No commits to display");
    public string AccessibleName { get => GetValue(AccessibleNameProperty); set => SetValue(AccessibleNameProperty, value); }
    public string EmptyText { get => GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }
    public static readonly StyledProperty<IBrush> GraphBackgroundProperty = AvaloniaProperty.Register<CommitGraphControl, IBrush>("GraphBackground", Brush.Parse("#151A22"));
    public static readonly StyledProperty<IBrush> GraphForegroundProperty = AvaloniaProperty.Register<CommitGraphControl, IBrush>("GraphForeground", Brush.Parse("#E4EAF3"));
    public static readonly StyledProperty<IBrush> GraphMutedProperty = AvaloniaProperty.Register<CommitGraphControl, IBrush>("GraphMuted", Brush.Parse("#A5B1C3"));
    public static readonly StyledProperty<IBrush> GraphSelectionProperty = AvaloniaProperty.Register<CommitGraphControl, IBrush>("GraphSelection", Brush.Parse("#223C60"));
    static CommitGraphControl() => AffectsRender<CommitGraphControl>(GraphBackgroundProperty, GraphForegroundProperty, GraphMutedProperty, GraphSelectionProperty);
    public static readonly StyledProperty<IEnumerable?> CommitsProperty =
        AvaloniaProperty.Register<CommitGraphControl, IEnumerable?>(nameof(Commits));
    public static readonly StyledProperty<CommitInfo?> SelectedCommitProperty =
        AvaloniaProperty.Register<CommitGraphControl, CommitInfo?>(nameof(SelectedCommit), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public IEnumerable? Commits { get => GetValue(CommitsProperty); set => SetValue(CommitsProperty, value); }
    public CommitInfo? SelectedCommit { get => GetValue(SelectedCommitProperty); set => SetValue(SelectedCommitProperty, value); }
    private IReadOnlyList<GraphRow> rows = [];
    private int offset;
    private double graphWidth = 50;
    private bool rebuildPending;
    private int layoutVersion;
    public Task LayoutReady { get; private set; } = Task.CompletedTask;
    private const double RowHeight = 24;
    private static readonly IBrush[] Palette = [Brush.Parse("#6EA8FE"), Brush.Parse("#B49BFF"), Brush.Parse("#52CCA3"), Brush.Parse("#E9B76C"), Brush.Parse("#E581AC")];
    private static readonly Typeface Mono = new("Cascadia Code,Consolas,Menlo,monospace");
    private static readonly Typeface Normal = new("Segoe UI,Inter,Microsoft JhengHei,PingFang TC,Noto Sans TC,sans-serif");
    private INotifyCollectionChanged? observed;
    private const int MaximumTextEntries = 256, MaximumTextCharacters = 65536, MaximumEntryCharacters = 4096;
    private readonly Dictionary<(int Row, int Column), LinkedListNode<CachedText>> textCache = new();
    private readonly LinkedList<CachedText> recentText = new();
    private int textCharacters;
    private TextContext? textContext;
    private sealed record CachedText((int Row, int Column) Key, TextLayout Layout, int Characters);
    private readonly record struct TextContext(double Width, double GraphWidth, IBrush Foreground, IBrush Muted,
        string Culture, FontFamily StyledFamily, double StyledSize, FontStyle StyledStyle, FontWeight StyledWeight,
        FontStretch StyledStretch, double Scaling);

    public CommitGraphControl()
    {
        Focusable = true;
        ClipToBounds = true;
        AutomationProperties.SetName(this, AccessibleName);
        MinHeight = 120;
        this.Bind(GraphBackgroundProperty, new DynamicResourceExtension("EditorBrush"));
        this.Bind(GraphForegroundProperty, new DynamicResourceExtension("TextBrush"));
        this.Bind(GraphMutedProperty, new DynamicResourceExtension("MutedBrush"));
        this.Bind(GraphSelectionProperty, new DynamicResourceExtension("SelectionBrush"));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CommitsProperty)
        {
            ClearTextCache();
            if (observed is not null) observed.CollectionChanged -= CollectionChanged;
            observed = Commits as INotifyCollectionChanged;
            if (observed is not null) observed.CollectionChanged += CollectionChanged;
            Rebuild();
        }
        if (change.Property == SelectedCommitProperty)
        {
            (ControlAutomationPeer.FromElement(this) as GraphPeer)?.Changed(change.OldValue as CommitInfo);
            InvalidateVisual();
        }
        if (change.Property == AccessibleNameProperty) AutomationProperties.SetName(this, AccessibleName);
        if (change.Property == EmptyTextProperty) ClearTextCache();
        if (change.Property == IsKeyboardFocusWithinProperty || change.Property == EmptyTextProperty) InvalidateVisual();
    }

    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (rebuildPending) return;
        rebuildPending = true;
        Dispatcher.UIThread.Post(() => { rebuildPending = false; Rebuild(); });
    }
    private void Rebuild() => LayoutReady = BuildRowsAsync();
    private async Task BuildRowsAsync()
    {
        var version = ++layoutVersion;
        var commits = Commits?.Cast<CommitInfo>().ToArray() ?? [];
        var layout = await Task.Run(() => CommitGraphLayout.Build(commits));
        if (version != layoutVersion) return;
        ClearTextCache();
        rows = layout;
        graphWidth = Math.Max(50, rows.Select(r => r.Lane).DefaultIfEmpty().Max() * 16 + 30);
        MinWidth = Math.Max(420, graphWidth + 390);
        offset = Math.Clamp(offset, 0, Math.Max(0, rows.Count - 1));
        (ControlAutomationPeer.FromElement(this) as GraphPeer)?.Changed(null);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var background = GetValue(GraphBackgroundProperty);
        // Immutable snapshots also detect changes inside an existing mutable brush.
        var foreground = GetValue(GraphForegroundProperty).ToImmutable();
        var muted = GetValue(GraphMutedProperty).ToImmutable();
        var current = new TextContext(Bounds.Width, graphWidth, foreground, muted, System.Globalization.CultureInfo.CurrentCulture.Name,
            GetValue(TextElement.FontFamilyProperty), GetValue(TextElement.FontSizeProperty), GetValue(TextElement.FontStyleProperty),
            GetValue(TextElement.FontWeightProperty), GetValue(TextElement.FontStretchProperty), TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
        if (textContext != current) { ClearTextCache(); textContext = current; }
        context.FillRectangle(background, Bounds.WithX(0).WithY(0));
        var visible = (int)Math.Ceiling(Bounds.Height / RowHeight);
        for (var i = offset; i < Math.Min(rows.Count, offset + visible); i++)
        {
            var row = rows[i];
            var y = (i - offset) * RowHeight;
            if (row.Commit == SelectedCommit) context.FillRectangle(GetValue(GraphSelectionProperty), new Rect(0, y, Bounds.Width, RowHeight));
            using (context.PushClip(new Rect(0, y, graphWidth - 4, RowHeight)))
            {
                foreach (var edge in row.Segments)
                    context.DrawLine(new Pen(LaneBrush(edge.FromLane), 1.5), new Point(12 + edge.FromLane * 16, y + edge.FromY * RowHeight), new Point(12 + edge.ToLane * 16, y + edge.ToY * RowHeight));
                context.DrawEllipse(LaneBrush(row.Lane), new Pen(background, 2), new Point(12 + row.Lane * 16, y + RowHeight / 2), 4, 4);
            }
            DrawText(context, i, 0, row.Commit.Hash, graphWidth, y + 4, Mono, muted, 65);
            DrawText(context, i, 1, row.Commit.Subject, graphWidth + 70, y + 4, Normal, foreground, Math.Max(30, Bounds.Width - graphWidth - 200));
            if (Bounds.Width > 470) DrawText(context, i, 2, row.Commit.Author, Bounds.Width - 120, y + 4, Normal, muted, 110);
        }
        if (rows.Count == 0) DrawText(context, -1, 3, EmptyText, 16, 24, Normal, muted, Bounds.Width - 30);
        if (IsKeyboardFocusWithin) context.DrawRectangle(new Pen(GetValue(GraphForegroundProperty), 2), new Rect(1, 1, Math.Max(0, Bounds.Width - 2), Math.Max(0, Bounds.Height - 2)));
    }

    private IBrush LaneBrush(int lane) => this.FindResource("GraphLane" + lane % Palette.Length) as IBrush ?? Palette[lane % Palette.Length];

    private void DrawText(DrawingContext context, int row, int column, string text, double x, double y, Typeface typeface, IBrush brush, double width)
    {
        var key = (row, column);
        if (textCache.TryGetValue(key, out var cached))
        {
            recentText.Remove(cached); recentText.AddFirst(cached);
            cached.Value.Layout.Draw(context, new Point(x, y)); return;
        }
        if (column == 0) text = text[..Math.Min(7, text.Length)];
        // FormattedText.Draw reshapes its lines each time in Avalonia11.3.
        // TextLayout owns retained shaped lines and can be drawn without formatting again.
        var properties = new GenericTextRunProperties(typeface, 12, foregroundBrush: brush, cultureInfo: System.Globalization.CultureInfo.CurrentCulture);
        var layout = new TextLayout(text, typeface, 12, brush, textWrapping: TextWrapping.WrapWithOverflow,
            textTrimming: TextTrimming.CharacterEllipsis, maxWidth: Math.Max(1, width), maxLines: 1,
            textStyleOverrides: text.Length == 0 ? null : new[] { new ValueSpan<TextRunProperties>(0, text.Length, properties) });
        if (text.Length > MaximumEntryCharacters)
        {
            using (layout) layout.Draw(context, new Point(x, y)); return;
        }
        while (textCache.Count >= MaximumTextEntries || textCharacters + text.Length > MaximumTextCharacters)
        {
            var oldest = recentText.Last!;
            recentText.RemoveLast(); textCache.Remove(oldest.Value.Key);
            textCharacters -= oldest.Value.Characters; oldest.Value.Layout.Dispose();
        }
        textCache.Add(key, recentText.AddFirst(new CachedText(key, layout, text.Length))); textCharacters += text.Length;
        layout.Draw(context, new Point(x, y));
    }
    private void ClearTextCache()
    {
        foreach (var entry in recentText) entry.Layout.Dispose();
        textCache.Clear(); recentText.Clear(); textCharacters = 0;
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ClearTextCache(); textContext = null; base.OnDetachedFromVisualTree(e);
    }
    private int IndexOf(string? hash)
    {
        if (hash is null) return -1;
        for (var i = 0; i < rows.Count; i++) if (rows[i].Commit.Hash == hash) return i;
        return -1;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.X != 0 || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        offset = Math.Clamp(offset - (int)(e.Delta.Y * 3), 0, Math.Max(0, rows.Count - Math.Max(1, (int)(Bounds.Height / RowHeight))));
        InvalidateVisual(); e.Handled = true;
        (ControlAutomationPeer.FromElement(this) as GraphPeer)?.Changed(null);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); Focus();
        var index = offset + (int)(e.GetPosition(this).Y / RowHeight);
        if (index >= 0 && index < rows.Count) SetCurrentValue(SelectedCommitProperty, rows[index].Commit);
        e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (rows.Count == 0) return;
        var current = IndexOf(SelectedCommit?.Hash);
        var index = e.Key switch { Key.Down => current + 1, Key.Up => current - 1, Key.PageDown => current + 20, Key.PageUp => current - 20, Key.Home => 0, Key.End => rows.Count - 1, _ => current };
        if (e.Key is not (Key.Down or Key.Up or Key.PageDown or Key.PageUp or Key.Home or Key.End)) return;
        index = Math.Clamp(index, 0, rows.Count - 1);
        SetCurrentValue(SelectedCommitProperty, rows[index].Commit);
        var visible = Math.Max(1, (int)(Bounds.Height / RowHeight));
        if (index < offset) offset = index;
        if (index >= offset + visible) offset = index - visible + 1;
        InvalidateVisual(); e.Handled = true;
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new GraphPeer(this);

    private sealed class GraphPeer(CommitGraphControl graph) : ControlAutomationPeer(graph), ISelectionProvider, IScrollProvider
    {
        private readonly Dictionary<string, RowPeer> peers = new(StringComparer.Ordinal);
        private int Visible => Math.Max(1, (int)Math.Ceiling(graph.Bounds.Height / RowHeight));
        private RowPeer Row(GraphRow row) => peers.TryGetValue(row.Commit.Hash, out var peer) ? peer : peers[row.Commit.Hash] = new(graph, this, row.Commit);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
        protected override string GetClassNameCore() => nameof(CommitGraphControl);
        protected override IReadOnlyList<AutomationPeer> GetChildrenCore()
        {
            var visible = graph.rows.Skip(graph.offset).Take(Visible).Select(Row).ToArray();
            var retained = visible.Select(p => p.Commit.Hash).Append(graph.SelectedCommit?.Hash ?? "").ToHashSet(StringComparer.Ordinal);
            foreach (var key in peers.Keys.Where(k => !retained.Contains(k)).ToArray()) peers.Remove(key);
            return visible;
        }
        protected override object? GetProviderCore(Type type) => type == typeof(ISelectionProvider) || type == typeof(IScrollProvider) ? this : base.GetProviderCore(type);
        public bool CanSelectMultiple => false;
        public bool IsSelectionRequired => false;
        public IReadOnlyList<AutomationPeer> GetSelection() => graph.IndexOf(graph.SelectedCommit?.Hash) is >= 0 and var index ? [Row(graph.rows[index])] : [];
        public void Changed(CommitInfo? previous)
        {
            foreach (var row in peers.Values.ToArray()) row.SelectionChanged(previous);
            InvalidateChildren();
            RaisePropertyChangedEvent(SelectionPatternIdentifiers.SelectionProperty, previous is null ? Array.Empty<AutomationPeer>() : peers.Values.Where(p => p.Commit == previous).Cast<AutomationPeer>().ToArray(), GetSelection());
        }
        public void RefreshChildren() => InvalidateChildren();
        public bool HorizontallyScrollable => false;
        public double HorizontalScrollPercent => -1;
        public double HorizontalViewSize => 100;
        public bool VerticallyScrollable => graph.rows.Count > Visible;
        public double VerticalScrollPercent => VerticallyScrollable ? 100d * graph.offset / Math.Max(1, graph.rows.Count - Visible) : 0;
        public double VerticalViewSize => Math.Min(100, 100d * Visible / Math.Max(1, graph.rows.Count));
        public void Scroll(ScrollAmount horizontalAmount, ScrollAmount verticalAmount)
        {
            var delta = verticalAmount switch { ScrollAmount.SmallDecrement => -1, ScrollAmount.SmallIncrement => 1, ScrollAmount.LargeDecrement => -Visible, ScrollAmount.LargeIncrement => Visible, _ => 0 };
            graph.offset = Math.Clamp(graph.offset + delta, 0, Math.Max(0, graph.rows.Count - Visible)); graph.InvalidateVisual(); InvalidateChildren();
        }
        public void SetScrollPercent(double horizontalPercent, double verticalPercent)
        {
            if (!double.IsFinite(verticalPercent) || verticalPercent is < 0 or > 100) return;
            graph.offset = (int)Math.Round(verticalPercent / 100 * Math.Max(0, graph.rows.Count - Visible)); graph.InvalidateVisual(); InvalidateChildren();
        }
    }
    private sealed class RowPeer(CommitGraphControl graph, GraphPeer parent, CommitInfo commit) : ControlAutomationPeer(graph), ISelectionItemProvider
    {
        private int Index => graph.IndexOf(commit.Hash);
        public CommitInfo Commit => Index is >= 0 and var index ? graph.rows[index].Commit : commit;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
        protected override string GetNameCore() => $"{Commit.Subject}; {Commit.Author}; {Commit.Hash}; {Commit.Date:yyyy-MM-dd}";
        protected override string GetAutomationIdCore() => "commit-" + commit.Hash;
        protected override string GetClassNameCore() => "Commit";
        protected override AutomationPeer GetParentCore() => parent;
        protected override IReadOnlyList<AutomationPeer> GetChildrenCore() => [];
        protected override bool IsOffscreenCore() { var index = Index; return index < 0 || index < graph.offset || index >= graph.offset + Math.Ceiling(graph.Bounds.Height / RowHeight); }
        protected override Rect GetBoundingRectangleCore()
        {
            var index = Index; if (index < 0) return default;
            var p = graph.PointToScreen(new Point(0, (index - graph.offset) * RowHeight));
            return new(p.X, p.Y, graph.Bounds.Width, RowHeight);
        }
        protected override bool HasKeyboardFocusCore() => graph.IsKeyboardFocusWithin && IsSelected;
        protected override void SetFocusCore() { if (Index < 0) return; Select(); graph.Focus(); }
        protected override object? GetProviderCore(Type type) => type == typeof(ISelectionItemProvider) ? this : base.GetProviderCore(type);
        public bool IsSelected => Index >= 0 && graph.SelectedCommit?.Hash == commit.Hash;
        public ISelectionProvider SelectionContainer => parent;
        public void Select()
        {
            var index = Index; if (index < 0) return;
            graph.SetCurrentValue(SelectedCommitProperty, graph.rows[index].Commit);
            var visible = Math.Max(1, (int)(graph.Bounds.Height / RowHeight));
            if (index < graph.offset) graph.offset = index; else if (index >= graph.offset + visible) graph.offset = index - visible + 1;
            graph.InvalidateVisual(); parent.RefreshChildren();
        }
        public void AddToSelection() => Select();
        public void RemoveFromSelection() { if (IsSelected) graph.SetCurrentValue(SelectedCommitProperty, null); }
        public void SelectionChanged(CommitInfo? previous) => RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, previous?.Hash == commit.Hash, IsSelected);
    }
}
