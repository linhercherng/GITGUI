using System.Collections;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using GitGui.Controls;
using GitGui.Core;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class GraphTextCacheTests
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
        var node = Cache(graph)[(row, column)]; Assert.NotNull(node);
        return node.GetType().GetProperty("Value")!.GetValue(node)!;
    }
    private static TextLayout Layout(CommitGraphControl graph, int row = 0) => Assert.IsType<TextLayout>(Entry(graph, row).GetType().GetProperty("Layout")!.GetValue(Entry(graph, row)));
    private static async Task<(Window Window, CommitGraphControl Graph)> Open(params CommitInfo[] commits)
    {
        var graph = new CommitGraphControl { Commits = commits, Width = 600, Height = 120 };
        var window = new Window { Content = graph, Width = 640, Height = 180 }; window.Show(); await graph.LayoutReady;
        window.UpdateLayout(); Draw(graph); return (window, graph);
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
            Assert.False(Cache(graph).Contains((0, 1))); Assert.True(Cache(graph).Contains((0, 0)));
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
            var expected = Pixels(context => context.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12, brush)
                { MaxTextWidth = layout.MaxWidth, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis }, new Point(2, 2)));
            var actual = Pixels(context => layout.Draw(context, new Point(2, 2)));
            Assert.Equal(expected, actual);
        }
        finally { window.Close(); }
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
