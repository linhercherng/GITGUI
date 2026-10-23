using GitGui.Core;
using LibGit2Sharp;

namespace GitGui.Engine.Lib;

public sealed class LibHistoryReader : IHistoryReader
{
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
