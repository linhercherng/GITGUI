using GitGui.Core;
using Xunit;

namespace GitGui.Core.Tests;

public sealed class ConflictMarkerTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Large_context_does_not_allocate_a_string_and_object_for_every_line()
    {
        var text = string.Concat(Enumerable.Repeat("ordinary context\r\n", 200000)) + "<<<<<<< HEAD\r\nours\r\n=======\r\ntheirs\r\n>>>>>>> topic\r\n";
        _ = ConflictMarkers.Parse("warmup\n");
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sections = ConflictMarkers.Parse(text);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"200000 context lines parse allocation: {allocated} bytes");
        Assert.Single(sections);
        Assert.True(allocated < 100000, $"Parsing allocated {allocated:N0} bytes for ordinary context.");
    }

    [Fact]
    public void Cancellation_stops_large_parses_and_stale_diff3_base_is_rejected()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ConflictMarkers.Parse("context\n", cancellation.Token));
        const string text = "<<<<<<< HEAD\nours\n||||||| base\nold\n=======\ntheirs\n>>>>>>> topic\n";
        var section = Assert.Single(ConflictMarkers.Parse(text));
        Assert.Throws<InvalidOperationException>(() => ConflictMarkers.Replace(text.Replace("old", "new", StringComparison.Ordinal), section, ConflictChoice.Ours));
        Assert.Throws<InvalidOperationException>(() => ConflictMarkers.Replace(text, section with { Start = -1 }, ConflictChoice.Ours));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Parses_diff3_and_replaces_only_selected_section_preserving_newlines(string nl)
    {
        var text = string.Join(nl, new[] { "before", "<<<<<<< HEAD", "our one", "||||||| base", "base one", "=======", "their one", ">>>>>>> topic", "between", "<<<<<<< HEAD", "our two", "=======", "their two", ">>>>>>> topic", "after", "" });
        var sections = ConflictMarkers.Parse(text); Assert.Equal(2, sections.Count);
        Assert.Equal("base one" + nl, sections[0].Base);
        var resolved = ConflictMarkers.Replace(text, sections[1], ConflictChoice.Both);
        Assert.Contains("our two" + nl + "their two" + nl + "after", resolved);
        Assert.Single(ConflictMarkers.Parse(resolved));
        Assert.StartsWith("before" + nl + "<<<<<<< HEAD", resolved);
    }
    [Fact]
    public void Malformed_markers_are_untouched_and_stale_selection_is_rejected()
    {
        Assert.Empty(ConflictMarkers.Parse("<<<<<<< HEAD\nours\n=======\ntheirs\n"));
        const string text = "<<<<<<< HEAD\nours\n=======\ntheirs\n>>>>>>> topic\n";
        var section = Assert.Single(ConflictMarkers.Parse(text));
        Assert.Throws<InvalidOperationException>(() => ConflictMarkers.Replace("prefix\n" + text, section, ConflictChoice.Ours));
        Assert.Equal("theirs\n", ConflictMarkers.Replace(text, section, ConflictChoice.Theirs));
    }
}
