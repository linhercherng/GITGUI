using GitGui.Core;
using Xunit;

namespace GitGui.Core.Tests;

public sealed class ConflictMarkerTests
{
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
