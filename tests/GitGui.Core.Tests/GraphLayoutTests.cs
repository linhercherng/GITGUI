using GitGui.Core;
using Xunit;

namespace GitGui.Core.Tests;

public sealed class GraphLayoutTests
{
    private static CommitInfo C(string hash, params string[] parents) => new(hash, hash, "Author", DateTimeOffset.UnixEpoch, parents);
    [Fact]
    public void Duplicate_parent_headers_do_not_preallocate_the_raw_header_count()
    {
        CommitInfo[] commits = [C("merge", Enumerable.Repeat("a", 100_000).ToArray()), C("a")];
        _ = CommitGraphLayout.Build(commits);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var rows = CommitGraphLayout.Build(commits);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Single(rows[0].Segments);
        Assert.InRange(allocated, 0, 16_384);
    }

    [Fact]
    public void Visible_linear_page_stays_within_200KB_allocation_budget()
    {
        var commits = Enumerable.Range(0, 500).Select(i => C(i.ToString("x40"), (i + 1).ToString("x40"))).ToArray();
        for (var i = 0; i < 10; i++) _ = CommitGraphLayout.Build(commits);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var rows = CommitGraphLayout.Build(commits);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(rows);
        Assert.InRange(allocated, 0, 200_000);
    }

    [Fact]
    public void Repeated_merge_parents_produce_one_edge_per_distinct_parent()
    {
        var rows = CommitGraphLayout.Build([C("merge", "a", "a", "b", "a"), C("a", "root"), C("b", "root", "root"), C("root")]);
        Assert.Equal(2, rows[0].Segments.Count(edge => edge.FromY == .5));
        Assert.Single(rows[2].Segments, edge => edge.FromY == .5);
        Assert.Equal(new[] { 0, 0, 1, 0 }, rows.Select(row => row.Lane));
    }

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
        Assert.Equal(new[] { 3, 4, 4, 3, 1 }, rows.Select(row => row.Segments.Count));
        Assert.All(rows.SelectMany(row => row.Segments), Assert.NotNull);
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
