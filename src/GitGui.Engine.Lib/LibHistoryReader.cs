using GitGui.Core;
using LibGit2Sharp;

namespace GitGui.Engine.Lib;

public sealed partial class LibHistoryReader : IHistoryReader, IStableGraphHistoryReader, IFileHistoryReader, IDisposable
{
    private readonly object graphSync = new();
    private readonly Dictionary<string, GraphSession> graphs = [];
    private bool disposed;
    private sealed class GraphSession(Repository repository, IEnumerator<Commit> walk) : IDisposable
    {
        private bool released;
        public List<CommitInfo> Items { get; } = [];
        public int Offset { get; set; }
        public long Characters { get; set; }
        public bool Complete { get; set; }
        public bool MoveNext(out Commit? commit)
        {
            if (!Complete && walk.MoveNext()) { commit = walk.Current; return true; }
            Complete = true; commit = null; Dispose(); return false;
        }
        public void Dispose() { if (released) return; released = true; walk.Dispose(); repository.Dispose(); }
    }
    public Task<FileHistoryResult> ReadAsync(string root, string path, CancellationToken ct)
        => Task.Run(() =>
        {
            using var repo = new Repository(root);
            var history = new List<FileHistoryEntry>();
            // FileHistory carries rename paths from children to parents. Time-only
            // ordering can visit a parent first when timestamps tie or clocks differ.
            foreach (var entry in repo.Commits.QueryBy(path, new CommitFilter
                { SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time }).Take(500))
            {
                ct.ThrowIfCancellationRequested(); var c = entry.Commit;
                history.Add(new(new(c.Sha, c.MessageShort, c.Author.Name, c.Author.When, c.Parents.Select(p => p.Sha).ToArray()), entry.Path));
            }
            var blob = repo.Head.Tip?[path]?.Target as Blob;
            if (blob is null) throw new InvalidOperationException("Native file history needs a HEAD blob; retry deleted-file history using Git CLI.");
            if (blob.IsBinary) throw new InvalidOperationException("Blame is available for text files.");
            using var stream = blob.GetContentStream();
            using var input = new StreamReader(stream, new System.Text.UTF8Encoding(false, true));
            var lines = input.ReadToEnd().Split('\n');
            var count = lines.Length - (lines[^1].Length == 0 ? 1 : 0);
            var hunks = repo.Blame(path).ToArray();
            var blame = new List<BlameLine>();
            for (var n = 0; n < count; n++)
            {
                ct.ThrowIfCancellationRequested();
                var hunk = hunks.First(h => h.ContainsLine(n));
                blame.Add(new(n + 1, hunk.FinalCommit.Sha, hunk.FinalSignature.Name, lines[n].TrimEnd('\r')));
            }
            return new FileHistoryResult(blame, history, "libgit2");
        }, ct);

    public Task<IReadOnlyList<CommitInfo>> GetGraphAsync(string repository, int skip, int take, CancellationToken ct)
        => Task.Run(async () =>
        {
            using var repo = new Repository(repository);
            // Explicit reachability excludes our private snapshot and temporary fetch refs.
            var tips = repo.Branches.Select(b => b.Tip).Concat(repo.Tags.Select(t => t.PeeledTarget as Commit))
                .Append(repo.Head.Tip).Where(c => c is not null).Cast<Commit>().DistinctBy(c => c.Sha).ToArray();
            return await GetGraphAsync(repository, tips.Select(c => c.Sha).ToArray(), skip, take, ct);
        }, ct);

    public Task<IReadOnlyList<CommitInfo>> GetGraphAsync(string repository, IReadOnlyList<string> tips, int skip, int take, CancellationToken ct)
        => Task.Run<IReadOnlyList<CommitInfo>>(() =>
        {
            lock (graphSync)
            {
                ct.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                var key = Path.GetFullPath(repository) + "\0" + RepositoryObjectView.Read(repository).Fingerprint + "\0" +
                    string.Join('\0', tips.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
                if (graphs.TryGetValue(key, out var old) && old.Offset > Math.Max(0, skip))
                { old.Dispose(); graphs.Remove(key); }
                if (!graphs.TryGetValue(key, out var session))
                {
                    if (graphs.Count >= 1)
                    {
                        foreach (var value in graphs.Values) value.Dispose();
                        graphs.Clear();
                    }
                    var repo = new Repository(repository);
                    try
                    {
                        var commits = new List<Commit>();
                        foreach (var oid in tips.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                        {
                            GitObject? value = repo.Lookup(oid);
                            while (value is TagAnnotation annotation) value = annotation.Target;
                            if (value is Commit commit) commits.Add(commit);
                        }
                        var walk = repo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = commits,
                            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time }).GetEnumerator();
                        graphs[key] = session = new(repo, walk);
                    }
                    catch { repo.Dispose(); throw; }
                }
                var start = Math.Max(0, skip);
                var count = Math.Clamp(take, 1, 100_000);
                while (!session.Complete && (long)session.Offset + session.Items.Count < (long)start + count)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!session.MoveNext(out var commit)) break;
                    session.Items.Add(new(commit!.Sha, commit.MessageShort, commit.Author.Name, commit.Author.When,
                        commit.Parents.Select(p => p.Sha).ToArray()));
                    var added = session.Items[^1]; session.Characters += (long)added.Subject.Length + added.Author.Length;
                    if (session.Items.Count > 100_000 || session.Characters > 8_000_000)
                    {
                        var remove = Math.Min(session.Items.Count, Math.Min(1000, Math.Max(0, start - session.Offset)));
                        if (remove > 0)
                        {
                            session.Characters -= session.Items.Take(remove).Sum(c => (long)c.Subject.Length + c.Author.Length);
                            session.Items.RemoveRange(0, remove); session.Offset += remove;
                        }
                    }
                }
                var result = session.Items.Skip(start - session.Offset).Take(count).ToArray();
                if (session.Characters > 8_000_000) { session.Dispose(); graphs.Remove(key); }
                return result;
            }
        }, ct);

    public void Dispose()
    {
        lock (graphSync)
        {
            if (disposed) return;
            disposed = true;
            foreach (var session in graphs.Values) session.Dispose();
            graphs.Clear();
        }
    }

    public Task<IReadOnlyList<CommitInfo>> GetLogAsync(string repository, int limit, CancellationToken ct)
        => Task.Run<IReadOnlyList<CommitInfo>>(() =>
        {
            using var repo = new Repository(repository);
            var result = new List<CommitInfo>();
            if (repo.Info.IsHeadUnborn) return result;
            foreach (var commit in repo.Commits.QueryBy(new CommitFilter
                { SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time }).Take(Math.Clamp(limit, 1, 500)))
            {
                ct.ThrowIfCancellationRequested();
                result.Add(new(commit.Sha, commit.MessageShort, commit.Author.Name,
                    commit.Author.When, commit.Parents.Select(p => p.Sha).ToArray()));
            }
            return result;
        }, ct);
}
