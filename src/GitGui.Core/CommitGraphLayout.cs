namespace GitGui.Core;

public sealed record GraphSegment(int FromLane, int ToLane, double FromY, double ToY);
public sealed record GraphRow(CommitInfo Commit, int Lane, IReadOnlyList<GraphSegment> Segments);

/// <summary>Topology-only graph layout. Coordinates are fractions of one fixed-height row.</summary>
public static class CommitGraphLayout
{
    public static IReadOnlyList<GraphRow> Build(IReadOnlyList<CommitInfo> commits)
    {
        var lanes = new List<string?>();
        var rows = new List<GraphRow>(commits.Count);
        var incoming = new HashSet<string>(StringComparer.Ordinal);
        foreach (var commit in commits)
        {
            var lane = lanes.IndexOf(commit.Hash);
            if (lane < 0) lane = Allocate(lanes, commit.Hash);
            var segments = new List<GraphSegment>();
            for (var i = 0; i < lanes.Count; i++)
                if (lanes[i] is not null && i != lane) segments.Add(new(i, i, 0, 1));
            // A new branch tip has no incoming edge.
            if (incoming.Contains(commit.Hash)) segments.Add(new(lane, lane, 0, .5));
            lanes[lane] = null;
            var first = true;
            foreach (var parent in commit.Parents.Distinct(StringComparer.Ordinal))
            {
                incoming.Add(parent);
                var target = lanes.IndexOf(parent);
                if (target < 0)
                {
                    if (first && lanes[lane] is null) { target = lane; lanes[lane] = parent; }
                    else target = Allocate(lanes, parent);
                }
                segments.Add(new(lane, target, .5, 1));
                first = false;
            }
            rows.Add(new(commit, lane, segments));
            while (lanes.Count > 0 && lanes[^1] is null) lanes.RemoveAt(lanes.Count - 1);
        }
        return rows;
    }

    private static int Allocate(List<string?> lanes, string hash)
    {
        var free = lanes.IndexOf(null);
        if (free >= 0) { lanes[free] = hash; return free; }
        lanes.Add(hash);
        return lanes.Count - 1;
    }
}
