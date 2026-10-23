using GitGui.Core;
using LibGit2Sharp;

namespace GitGui.Engine.Lib;

public sealed class LibHistoryReader : IHistoryReader, IGraphHistoryReader, IFileHistoryReader
{
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
        => Task.Run<IReadOnlyList<CommitInfo>>(() =>
        {
            using var repo = new Repository(repository);
            // Explicit reachability excludes our private snapshot and temporary fetch refs.
            var tips = repo.Branches.Select(b => b.Tip).Concat(repo.Tags.Select(t => t.PeeledTarget as Commit))
                .Append(repo.Head.Tip).Where(c => c is not null).Cast<Commit>().DistinctBy(c => c.Sha).ToArray();
            if (tips.Length == 0) return [];
            var result = new List<CommitInfo>();
            var commits = repo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = tips,
                SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time });
            var remainingSkip = Math.Max(0, skip);
            foreach (var commit in commits)
            {
                ct.ThrowIfCancellationRequested();
                if (remainingSkip-- > 0) continue;
                result.Add(new(commit.Sha, commit.MessageShort, commit.Author.Name, commit.Author.When,
                    commit.Parents.Select(p => p.Sha).ToArray()));
                if (result.Count >= Math.Clamp(take, 1, 100_000)) break;
            }
            return result;
        }, ct);

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
