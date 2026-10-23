using GitGui.Core;
using Xunit;

namespace GitGui.Core.Tests;

public sealed class GraphLayoutTests
{
    private static CommitInfo C(string hash, params string[] parents) => new(hash, hash, "Author", DateTimeOffset.UnixEpoch, parents);
    [Fact]
    public void Linear_history_stays_in_one_lane_and_connects_parent()
    {
        var rows = CommitGraphLayout.Build([C("a", "b"), C("b", "c"), C("c")]);
        Assert.All(rows, row => Assert.Equal(0, row.Lane));
        Assert.Contains(new GraphSegment(0, 0, .5, 1), rows[0].Segments);
        Assert.Contains(new GraphSegment(0, 0, 0, .5), rows[1].Segments);
        Assert.DoesNotContain(rows[2].Segments, edge => edge.ToY == 1);
    }
    [Fact]
    public void Octopus_merge_allocates_lanes_then_converges_without_duplicating_root()
    {
        var rows = CommitGraphLayout.Build([C("merge", "a", "b", "c"), C("a", "root"), C("b", "root"), C("c", "root"), C("root")]);
        Assert.Equal(3, rows[0].Segments.Count(edge => edge.FromY == .5));
        Assert.Equal(new[] { 0, 0, 1, 2, 0 }, rows.Select(row => row.Lane));
        Assert.Contains(rows[2].Segments, edge => edge.FromLane == 1 && edge.ToLane == 0);
        Assert.Contains(rows[3].Segments, edge => edge.FromLane == 2 && edge.ToLane == 0);
        Assert.Single(rows, row => row.Commit.Hash == "root");
    }
    [Fact]
    public void Disconnected_tip_reuses_freed_lane_and_empty_history_is_valid()
    {
        var rows = CommitGraphLayout.Build([C("a"), C("b", "c"), C("c")]);
        Assert.All(rows, row => Assert.Equal(0, row.Lane));
        Assert.Empty(CommitGraphLayout.Build([]));
        Assert.DoesNotContain(rows[1].Segments, edge => edge.FromY == 0);
    }
    [Fact]
    public void Boundary_parent_remains_connected_when_page_ends()
    {
        var rows = CommitGraphLayout.Build([C("tip", "not-loaded")]);
        Assert.Single(rows);
        Assert.Contains(rows[0].Segments, edge => edge.FromY == .5 && edge.ToY == 1);
    }
}
