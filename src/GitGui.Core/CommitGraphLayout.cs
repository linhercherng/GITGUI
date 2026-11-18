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
        var incoming = new HashSet<string>(commits.Count, StringComparer.Ordinal);
        foreach (var commit in commits)
        {
            var lane = lanes.IndexOf(commit.Hash);
            if (lane < 0) lane = Allocate(lanes, commit.Hash);
            var hasIncoming = incoming.Contains(commit.Hash);
            HashSet<string>? parentsRemaining = null;
            if (commit.Parents.Count > 1)
            {
                parentsRemaining = new(StringComparer.Ordinal);
                for (var i = 0; i < commit.Parents.Count; i++) parentsRemaining.Add(commit.Parents[i]);
            }
            var capacity = lanes.Count - 1 + (hasIncoming ? 1 : 0) + (parentsRemaining?.Count ?? commit.Parents.Count);
            var segments = capacity == 0 ? Array.Empty<GraphSegment>() : new GraphSegment[capacity];
            var segmentCount = 0;
            for (var i = 0; i < lanes.Count; i++)
                if (lanes[i] is not null && i != lane) segments[segmentCount++] = new(i, i, 0, 1);
            // A new branch tip has no incoming edge.
            if (hasIncoming) segments[segmentCount++] = new(lane, lane, 0, .5);
            lanes[lane] = null;
            var first = true;
            for (var parentIndex = 0; parentIndex < commit.Parents.Count; parentIndex++)
            {
                var parent = commit.Parents[parentIndex];
                if (parentsRemaining is not null && !parentsRemaining.Remove(parent)) continue;
                incoming.Add(parent);
                var target = lanes.IndexOf(parent);
                if (target < 0)
                {
                    if (first && lanes[lane] is null) { target = lane; lanes[lane] = parent; }
                    else target = Allocate(lanes, parent);
                }
                segments[segmentCount++] = new(lane, target, .5, 1);
                first = false;
            }
            if (segmentCount != segments.Length) Array.Resize(ref segments, segmentCount);
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
