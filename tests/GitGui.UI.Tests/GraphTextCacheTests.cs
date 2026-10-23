using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using GitGui.Controls;
using GitGui.Core;
using Xunit;
using Xunit.Abstractions;

namespace GitGui.UI.Tests;

public sealed class GraphTextCacheTests(ITestOutputHelper output)
{
    private static CommitInfo Commit(int index, string? subject = null) => new(index.ToString("x40"), subject ?? "中文 history 🙂 " + index, "Author", DateTimeOffset.UnixEpoch, []);
    private static void Draw(CommitGraphControl graph) { using var context = new DrawingGroup().Open(); graph.Render(context); }
    private static IDictionary Cache(CommitGraphControl graph)
    {
        var field = typeof(CommitGraphControl).GetField("textCache", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field); return Assert.IsAssignableFrom<IDictionary>(field.GetValue(graph));
    }
    private static object Entry(CommitGraphControl graph, int row = 0, int column = 1)
    {
        var entry = FindEntry(graph, row, column); Assert.NotNull(entry); return entry;
    }
    private static object? FindEntry(CommitGraphControl graph, int row, int column)
    {
        var commit = row < 0 ? null : graph.Commits!.Cast<CommitInfo>().ElementAt(row);
        var text = column switch { 0 => commit!.Hash[..Math.Min(7, commit.Hash.Length)], 1 => commit!.Subject, 2 => commit!.Author, _ => graph.EmptyText };
        foreach (var node in Cache(graph).Values)
        {
            var entry = node!.GetType().GetProperty("Value")!.GetValue(node)!;
            var key = entry.GetType().GetProperty("Key")!.GetValue(entry)!;
            if ((int)key.GetType().GetProperty("Column")!.GetValue(key)! == column && (string)key.GetType().GetProperty("Text")!.GetValue(key)! == text) return entry;
        }
        return null;
    }
    private static TextLayout Layout(CommitGraphControl graph, int row = 0, int column = 1)
        => Assert.IsType<TextLayout>(Entry(graph, row, column).GetType().GetProperty("Layout")!.GetValue(Entry(graph, row, column)));
    private static async Task<(Window Window, CommitGraphControl Graph)> Open(params CommitInfo[] commits)
    {
        var graph = new CommitGraphControl { Commits = commits, Width = 600, Height = 120 };
        var window = new Window { Content = graph, Width = 640, Height = 180 }; window.Show(); await graph.LayoutReady;
        window.UpdateLayout(); Draw(graph); return (window, graph);
    }

    [AvaloniaFact]
    public async Task Identical_visible_labels_reuse_one_shaped_layout_across_rows_and_scroll()
    {
        var commits = Enumerable.Range(0, 1500).Select(i => Commit(i, "Repeated title")).ToArray();
        var (window, graph) = await Open(commits);
        try
        {
            var subject = Layout(graph, 0);
            var author = Layout(graph, 0, 2);
            var hash = Layout(graph, 0, 0);
            Assert.Same(subject, Layout(graph, 1));
            Assert.Same(author, Layout(graph, 1, 2));
            Assert.Same(hash, Layout(graph, 1, 0));
            var provider = ControlAutomationPeer.CreatePeerForElement(graph)!.GetProvider<IScrollProvider>()!;
            provider.SetScrollPercent(-1, 50); Draw(graph);
            var row = (int)typeof(CommitGraphControl).GetField("offset", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(graph)!;
            Assert.Same(subject, Layout(graph, row));
            Assert.Same(author, Layout(graph, row, 2));
            Assert.Equal(3, Cache(graph).Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Graph_strokes_reuse_immutable_pens_and_preserve_mutable_brush_updates_and_pixels()
    {
        var first = Commit(0) with { Parents = [Commit(1).Hash] };
        var second = Commit(1) with { Parents = [Commit(2).Hash] };
        var (window, graph) = await Open(first, second, Commit(2));
        try
        {
            var lane = new SolidColorBrush(Colors.Red, .8);
            var background = new SolidColorBrush(Colors.Black);
            graph.Resources["GraphLane0"] = lane;
            graph.SetValue(CommitGraphControl.GraphBackgroundProperty, background);
            var drawing = Recorded(graph); var original = Pens(drawing).ToArray();
            var repeated = Pens(Recorded(graph)).ToArray();
            Assert.NotEmpty(original); Assert.Equal(original.Length, repeated.Length);
            for (var i = 0; i < original.Length; i++)
            {
                Assert.Same(original[i], repeated[i]);
                Assert.IsType<ImmutablePen>(original[i]);
                var defaults = new Pen(original[i].Brush, original[i].Thickness);
                Assert.Equal(defaults.LineCap, original[i].LineCap); Assert.Equal(defaults.LineJoin, original[i].LineJoin);
                Assert.Equal(defaults.MiterLimit, original[i].MiterLimit); Assert.Null(original[i].DashStyle);
            }
            Assert.Equal(Pixels(context => MutablePenOracle(drawing).Draw(context)), Pixels(drawing.Draw));
            var oldLane = original.First(p => p.Thickness == 1.5);
            lane.Color = Colors.Lime; lane.Opacity = .4; background.Color = Colors.Navy;
            var changed = Recorded(graph); var changedPens = Pens(changed).ToArray();
            var newLane = changedPens.First(p => p.Thickness == 1.5);
            Assert.NotSame(oldLane, newLane);
            Assert.Equal(Colors.Red, Assert.IsAssignableFrom<ISolidColorBrush>(oldLane.Brush).Color);
            Assert.Equal(Colors.Lime, Assert.IsAssignableFrom<ISolidColorBrush>(newLane.Brush).Color);
            Assert.Equal(.4, newLane.Brush!.Opacity);
            Assert.Contains(changedPens, p => p.Thickness == 2 && p.Brush is ISolidColorBrush solid && solid.Color == Colors.Navy);
            Assert.Equal(Pixels(context => MutablePenOracle(changed).Draw(context)), Pixels(changed.Draw));
            graph.Resources["GraphLane0"] = Brushes.Blue;
            Assert.Equal(Colors.Blue, Assert.IsAssignableFrom<ISolidColorBrush>(Pens(Recorded(graph)).First(p => p.Thickness == 1.5).Brush).Color);
        }
        finally { window.Close(); }
    }

    private static DrawingGroup Recorded(CommitGraphControl graph)
    {
        var drawing = new DrawingGroup(); using (var context = drawing.Open()) graph.Render(context); return drawing;
    }
    private static IEnumerable<IPen> Pens(Drawing drawing)
    {
        if (drawing is GeometryDrawing { Pen: { } pen }) yield return pen;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var item in Pens(child)) yield return item;
    }
    private static Drawing MutablePenOracle(Drawing drawing)
    {
        if (drawing is GeometryDrawing geometry)
            return new GeometryDrawing { Geometry = geometry.Geometry, Brush = geometry.Brush,
                Pen = geometry.Pen is { } pen ? new Pen(pen.Brush, pen.Thickness) : null };
        if (drawing is DrawingGroup group)
        {
            var result = new DrawingGroup { Opacity = group.Opacity, Transform = group.Transform,
                ClipGeometry = group.ClipGeometry, OpacityMask = group.OpacityMask };
            foreach (var child in group.Children) result.Children.Add(MutablePenOracle(child));
            return result;
        }
        return drawing;
    }

    [AvaloniaFact]
    public async Task Mutable_lane_updates_repaint_without_a_scroll_or_manual_graph_render()
    {
        var (window, graph) = await Open(Commit(0) with { Parents = [Commit(1).Hash] }, Commit(1));
        try
        {
            var lane = new SolidColorBrush(Colors.Red); graph.Resources["GraphLane0"] = lane;
            graph.InvalidateVisual(); PumpFrame(window);
            var field = typeof(CommitGraphControl).GetField("lanePens", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var oldPen = Assert.IsType<ImmutablePen>(((Array)field.GetValue(graph)!).GetValue(0));
            lane.Color = Colors.Lime; PumpFrame(window);
            var newPen = Assert.IsType<ImmutablePen>(((Array)field.GetValue(graph)!).GetValue(0));
            Assert.NotSame(oldPen, newPen);
            Assert.Equal(Colors.Lime, Assert.IsAssignableFrom<ISolidColorBrush>(newPen.Brush).Color);
            graph.Resources["GraphLane0"] = Brushes.Blue; PumpFrame(window);
            Assert.Equal(Colors.Blue, Assert.IsAssignableFrom<ISolidColorBrush>(((ImmutablePen)((Array)field.GetValue(graph)!).GetValue(0)!).Brush).Color);
            var gradient = new LinearGradientBrush { GradientStops = new GradientStops
                { new(Colors.Red, 0), new(Colors.Blue, 1) } };
            graph.Resources["GraphLane0"] = gradient; PumpFrame(window);
            var gradientPen = (ImmutablePen)((Array)field.GetValue(graph)!).GetValue(0)!;
            gradient.GradientStops[0].Color = Colors.Gold; PumpFrame(window);
            var changedGradient = (ImmutablePen)((Array)field.GetValue(graph)!).GetValue(0)!;
            Assert.NotSame(gradientPen, changedGradient);
            Assert.Equal(Colors.Gold, Assert.IsAssignableFrom<IGradientBrush>(changedGradient.Brush).GradientStops[0].Color);
            var transform = new TranslateTransform(1, 2); gradient.Transform = transform; PumpFrame(window);
            transform.X = 3; PumpFrame(window);
            Assert.Equal(transform.Value, ((ImmutablePen)((Array)field.GetValue(graph)!).GetValue(0)!).Brush!.Transform!.Value);
            var matrix = new MatrixTransform(Matrix.CreateTranslation(1, 2));
            var nested = new TransformGroup { Children = new Transforms { matrix } };
            var outer = new TransformGroup { Children = new Transforms { nested } };
            gradient.Transform = outer; PumpFrame(window);
            var beforeMatrix = outer.Value;
            matrix.Matrix = Matrix.CreateTranslation(5, 6); PumpFrame(window);
            Assert.NotEqual(beforeMatrix, outer.Value);
            Assert.Equal(outer.Value, ((ImmutablePen)((Array)field.GetValue(graph)!).GetValue(0)!).Brush!.Transform!.Value);
        }
        finally { window.Close(); }
        Assert.All(((Array)typeof(CommitGraphControl).GetField("brushObservers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(graph)!).Cast<object?>(), observer => Assert.Null(observer));
    }

    private static void PumpFrame(Window window)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var bitmap = window.CaptureRenderedFrame(); Assert.NotNull(bitmap);
    }

    [AvaloniaFact]
    public async Task Repeated_render_and_selection_reuse_shaped_lines()
    {
        var (window, graph) = await Open(Commit(0), Commit(1));
        try
        {
            var layout = Layout(graph); var line = Assert.Single(layout.TextLines);
            Draw(graph); Assert.Same(layout, Layout(graph)); Assert.Same(line, Assert.Single(Layout(graph).TextLines));
            graph.SelectedCommit = Commit(1); Draw(graph); Assert.Same(layout, Layout(graph));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Width_brush_mutation_culture_and_inherited_font_context_invalidate()
    {
        var (window, graph) = await Open(Commit(0)); var culture = CultureInfo.CurrentCulture;
        try
        {
            var previous = Layout(graph);
            graph.Width = 480; window.UpdateLayout(); Draw(graph); Assert.NotSame(previous, Layout(graph)); previous = Layout(graph);
            var brush = new SolidColorBrush(Colors.Red); graph.SetValue(CommitGraphControl.GraphForegroundProperty, brush);
            Draw(graph); Assert.NotSame(previous, Layout(graph)); previous = Layout(graph);
            brush.Color = Colors.Blue; Draw(graph); Assert.NotSame(previous, Layout(graph)); previous = Layout(graph);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture.Name == "en-US" ? "zh-TW" : "en-US");
            Draw(graph); Assert.NotSame(previous, Layout(graph)); previous = Layout(graph);
            graph.SetValue(TextElement.FontFamilyProperty, new FontFamily("Consolas"));
            Draw(graph); Assert.NotSame(previous, Layout(graph)); previous = Layout(graph);
            graph.SetValue(TextElement.FontSizeProperty, 20d); Draw(graph); Assert.NotSame(previous, Layout(graph));
        }
        finally { CultureInfo.CurrentCulture = culture; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Same_oid_replacement_and_empty_translation_never_reuse_stale_text()
    {
        var first = Commit(0, "Old subject"); var (window, graph) = await Open(first);
        try
        {
            var before = Layout(graph); var changed = first with { Subject = "New subject", Author = "New author" };
            graph.Commits = new[] { changed }; await graph.LayoutReady; Draw(graph); Assert.NotSame(before, Layout(graph));
            var peer = ControlAutomationPeer.CreatePeerForElement(graph)!; Assert.Contains("New subject", peer.GetChildren()![0].GetName());
            graph.Commits = Array.Empty<CommitInfo>(); await graph.LayoutReady; Draw(graph);
            Assert.Single(Cache(graph)); var empty = Entry(graph, -1, 3);
            graph.EmptyText = "No history / 沒有提交"; Draw(graph); Assert.NotSame(empty, Entry(graph, -1, 3));
        }
        finally { window.Close(); }
        Assert.Empty(Cache(graph));
    }

    [AvaloniaFact]
    public async Task Long_scroll_is_bounded_and_oversized_subjects_are_not_retained()
    {
        var commits = Enumerable.Range(0, 1000).Select(i => Commit(i, new string('x', 800) + i)).ToArray();
        var (window, graph) = await Open(commits);
        try
        {
            var provider = ControlAutomationPeer.CreatePeerForElement(graph)!.GetProvider<IScrollProvider>()!;
            for (var percent = 0; percent <= 100; percent += 2)
            {
                provider.SetScrollPercent(-1, percent); Draw(graph); var cache = Cache(graph);
                Assert.InRange(cache.Count, 1, 256);
                var characters = 0;
                foreach (var node in cache.Values) { var entry = node!.GetType().GetProperty("Value")!.GetValue(node)!; characters += (int)entry.GetType().GetProperty("Characters")!.GetValue(entry)!; }
                Assert.InRange(characters, 0, 65536);
            }
            graph.Commits = new[] { Commit(0, new string('界', 5000)) }; await graph.LayoutReady;
            provider.SetScrollPercent(-1, 0); Draw(graph);
            Assert.Null(FindEntry(graph, 0, 1)); Assert.NotNull(FindEntry(graph, 0, 0));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("中文提交 🙂", 180)]
    [InlineData("Long line with ellipsis and emoji 🙂 中文 long line", 70)]
    [InlineData("first line\nsecond line", 140)]
    [InlineData("עברית العربية mixed English", 120)]
    public async Task Cached_shaped_text_preserves_FormattedText_pixels(string text, double width)
    {
        var (window, graph) = await Open(Commit(0, text));
        try
        {
            graph.Width = width + 250; window.UpdateLayout(); Draw(graph); var layout = Layout(graph);
            var typeface = new Typeface("Segoe UI,Inter,Microsoft JhengHei,PingFang TC,Noto Sans TC,sans-serif");
            var brush = graph.GetValue(CommitGraphControl.GraphForegroundProperty).ToImmutable();
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12, brush)
                { MaxTextWidth = layout.MaxWidth, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            var expected = Pixels(context => context.DrawText(formatted, new Point(2, 2)));
            var actual = Pixels(context => layout.Draw(context, new Point(2, 2)));
            if (!expected.AsSpan().SequenceEqual(actual))
            {
                var destination = DiagnosticDestination();
                Directory.CreateDirectory(destination);
                File.WriteAllBytes(Path.Combine(destination, "expected.png"), expected);
                File.WriteAllBytes(Path.Combine(destination, "actual.png"), actual);
                var metrics = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Text = text, RequestedWidth = width, GraphWidth = graph.Bounds.Width, Culture = CultureInfo.CurrentCulture.Name,
                    Formatted = new { formatted.MaxTextWidth, formatted.Width, formatted.WidthIncludingTrailingWhitespace, formatted.Height, formatted.Baseline },
                    Cached = new { layout.MaxWidth, layout.Width, layout.WidthIncludingTrailingWhitespace, layout.Height, layout.Baseline,
                        Lines = layout.TextLines.Select(line => new { line.Length, line.Width, line.WidthIncludingTrailingWhitespace, line.Height, line.HasCollapsed }).ToArray() }
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(destination, "metrics.json"), metrics);
                output.WriteLine("Strict PNG comparison diagnostics: " + destination + Environment.NewLine + metrics);
            }
            Assert.Equal(expected, actual);
        }
        finally { window.Close(); }
    }

    private static string DiagnosticDestination([CallerFilePath] string sourcePath = "")
    {
        var folder = new DirectoryInfo(Path.GetDirectoryName(sourcePath) ?? Directory.GetCurrentDirectory());
        while (folder.Parent is not null && !File.Exists(Path.Combine(folder.FullName, "GitGui.sln"))) folder = folder.Parent;
        var root = File.Exists(Path.Combine(folder.FullName, "GitGui.sln")) ? folder.FullName : Directory.GetCurrentDirectory();
        return Path.Combine(root, "TestResults", "graph-text-cache", Guid.NewGuid().ToString("N"));
    }

    private static byte[] Pixels(Action<DrawingContext> draw)
    {
        var window = new Window { Content = new Painter(draw), Width = 360, Height = 80, Background = Brushes.White };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var bitmap = window.CaptureRenderedFrame(); Assert.NotNull(bitmap);
            using var stream = new MemoryStream(); bitmap.Save(stream); return stream.ToArray();
        }
        finally { window.Close(); }
    }
    private sealed class Painter(Action<DrawingContext> draw) : Control { public override void Render(DrawingContext context) => draw(context); }
}
