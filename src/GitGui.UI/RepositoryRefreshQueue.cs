namespace GitGui.UI;

[Flags]
public enum RepositoryRefreshKind { None = 0, Status = 1, History = 2, Configuration = 4, Operation = 8, All = 15 }

/// <summary>Coalesces native watcher threads before posting a single UI callback.</summary>
public sealed class RepositoryRefreshQueue
{
    private readonly object gate = new();
    private RepositoryRefreshKind pending;
    private long lastEvent;
    private bool scheduled;
    public bool Add(RepositoryRefreshKind kind, long now)
    {
        if (kind == RepositoryRefreshKind.None) return false;
        lock (gate)
        {
            pending |= kind; lastEvent = now;
            if (scheduled) return false;
            scheduled = true; return true;
        }
    }
    public bool Take(long now, bool busy, out RepositoryRefreshKind kind, out int delayMilliseconds)
    {
        lock (gate)
        {
            kind = RepositoryRefreshKind.None;
            delayMilliseconds = busy ? 300 : (int)Math.Clamp(300 - (now - lastEvent), 0, 300);
            if (!scheduled || delayMilliseconds > 0) return false;
            kind = pending; pending = RepositoryRefreshKind.None; scheduled = false; return true;
        }
    }
    public static RepositoryRefreshKind Classify(string root, string? gitDirectory, string? commonDirectory, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var directory in new[] { gitDirectory, commonDirectory }.OfType<string>().Distinct())
        {
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(prefix, comparison)) continue;
            var relative = full[prefix.Length..].Replace('\\', '/');
            // A status query creates daemon cookies; observing its own IPC/log traffic would start an endless refresh loop.
            // Match only the daemon's top-level Git metadata paths, never similarly named working-tree files.
            if (relative.Equals("fsmonitor--daemon", comparison) || relative.StartsWith("fsmonitor--daemon/", comparison) ||
                relative.Equals("fsmonitor--daemon.log", comparison) || relative.StartsWith("fsmonitor--daemon.log.", comparison) ||
                relative.Equals("fsmonitor--daemon.ipc", comparison) || relative.StartsWith("fsmonitor--daemon.ipc.", comparison))
                return RepositoryRefreshKind.None;
            if (relative.StartsWith("objects/", comparison) || relative.StartsWith("logs/", comparison) ||
                relative.StartsWith("refs/gitgui/", comparison) || relative.EndsWith(".lock", comparison)) return RepositoryRefreshKind.None;
            if (relative.Equals("config", comparison) || relative.Equals("config.worktree", comparison)) return RepositoryRefreshKind.All;
            if (relative.Equals("HEAD", comparison) || relative.Equals("packed-refs", comparison) ||
                relative.Equals("shallow", comparison) || relative.Equals("info/grafts", comparison) ||
                relative.StartsWith("refs/", comparison) || relative.StartsWith("reftable/", comparison))
                return RepositoryRefreshKind.Status | RepositoryRefreshKind.History;
            if (relative.Equals("index", comparison) || relative.Equals("info/exclude", comparison)) return RepositoryRefreshKind.Status;
            return RepositoryRefreshKind.Status | RepositoryRefreshKind.Operation;
        }
        return Path.GetFileName(path).Equals(".git", comparison) && Path.GetDirectoryName(Path.GetFullPath(path)) == Path.GetFullPath(root)
            ? RepositoryRefreshKind.All : RepositoryRefreshKind.Status;
    }
}
