using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using GitGui.Core;

namespace GitGui.Controls;

/// <summary>Draws only the graph rows intersecting the viewport; no per-commit visual tree.</summary>
public sealed class CommitGraphControl : Control
{
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

    public CommitGraphControl()
    {
        Focusable = true;
        ClipToBounds = true;
        AutomationProperties.SetName(this, "Commit history graph. Use Up and Down to select commits.");
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
            if (observed is not null) observed.CollectionChanged -= CollectionChanged;
            observed = Commits as INotifyCollectionChanged;
            if (observed is not null) observed.CollectionChanged += CollectionChanged;
            Rebuild();
        }
        if (change.Property == SelectedCommitProperty)
        {
            AutomationProperties.SetName(this, SelectedCommit is { } c ? $"{c.Subject}; {c.Author}; {c.Hash}" : "Commit history graph");
            InvalidateVisual();
        }
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
        rows = layout;
        graphWidth = Math.Max(50, rows.Select(r => r.Lane).DefaultIfEmpty().Max() * 16 + 30);
        MinWidth = Math.Max(420, graphWidth + 390);
        offset = Math.Clamp(offset, 0, Math.Max(0, rows.Count - 1));
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var background = GetValue(GraphBackgroundProperty);
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
            DrawText(context, row.Commit.Hash[..Math.Min(7, row.Commit.Hash.Length)], graphWidth, y + 4, Mono, GetValue(GraphMutedProperty), 65);
            DrawText(context, row.Commit.Subject, graphWidth + 70, y + 4, Normal, GetValue(GraphForegroundProperty), Math.Max(30, Bounds.Width - graphWidth - 200));
            if (Bounds.Width > 470) DrawText(context, row.Commit.Author, Bounds.Width - 120, y + 4, Normal, GetValue(GraphMutedProperty), 110);
        }
        if (rows.Count == 0) DrawText(context, "No commits to display", 16, 24, Normal, GetValue(GraphMutedProperty), Bounds.Width - 30);
    }

    private IBrush LaneBrush(int lane) => this.FindResource("GraphLane" + lane % Palette.Length) as IBrush ?? Palette[lane % Palette.Length];

    private static void DrawText(DrawingContext context, string text, double x, double y, Typeface typeface, IBrush brush, double width)
    {
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12, brush) { MaxTextWidth = Math.Max(1, width), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        context.DrawText(formatted, new Point(x, y));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.X != 0 || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        offset = Math.Clamp(offset - (int)(e.Delta.Y * 3), 0, Math.Max(0, rows.Count - Math.Max(1, (int)(Bounds.Height / RowHeight))));
        InvalidateVisual(); e.Handled = true;
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
        var current = rows.ToList().FindIndex(r => r.Commit == SelectedCommit);
        var index = e.Key switch { Key.Down => current + 1, Key.Up => current - 1, Key.PageDown => current + 20, Key.PageUp => current - 20, Key.Home => 0, Key.End => rows.Count - 1, _ => current };
        if (e.Key is not (Key.Down or Key.Up or Key.PageDown or Key.PageUp or Key.Home or Key.End)) return;
        index = Math.Clamp(index, 0, rows.Count - 1);
        SetCurrentValue(SelectedCommitProperty, rows[index].Commit);
        var visible = Math.Max(1, (int)(Bounds.Height / RowHeight));
        if (index < offset) offset = index;
        if (index >= offset + visible) offset = index - visible + 1;
        InvalidateVisual(); e.Handled = true;
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new ControlAutomationPeer(this);
}
