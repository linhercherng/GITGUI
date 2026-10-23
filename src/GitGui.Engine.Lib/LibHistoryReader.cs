using GitGui.Core;
using LibGit2Sharp;

namespace GitGui.Engine.Lib;

public sealed class LibHistoryReader : IHistoryReader, IGraphHistoryReader
{
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
