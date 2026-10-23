using GitGui.Core;
using Xunit;

namespace GitGui.Core.Tests;

public class PatchTests
{
    private const string Patch = "diff --git a/a.txt b/a.txt\nindex 1111111..2222222 100644\n--- a/a.txt\n+++ b/a.txt\n@@ -1,3 +1,4 @@\n one\n-two\n+TWO\n+extra\n three\n";

    [Fact]
    public void Keeps_unselected_deletions_as_context_and_omits_unselected_additions()
    {
        var hunks = UnifiedPatch.Parse(Patch);
        var selected = hunks[0].Lines.Single(l => l.Text == "extra");
        var patch = UnifiedPatch.Build(Patch, new HashSet<int>(), new HashSet<int> { selected.Id });
        Assert.Contains(" two\n+extra", patch);
        Assert.DoesNotContain("+TWO", patch);
    }

    [Fact]
    public void Reverse_selection_uses_index_as_base()
    {
        var hunk = Assert.Single(UnifiedPatch.Parse(Patch));
        var selected = hunk.Lines.Single(l => l.Text == "extra");
        var patch = UnifiedPatch.Build(Patch, new HashSet<int>(), new HashSet<int> { selected.Id }, reverse: true);
        Assert.Contains(" TWO\n-extra", patch);
        Assert.DoesNotContain("+two", patch);
    }
}
