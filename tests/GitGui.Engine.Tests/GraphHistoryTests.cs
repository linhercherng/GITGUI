using GitGui.Core;
using GitGui.Engine.Cli;
using GitGui.Engine.Lib;
using Xunit;

namespace GitGui.Engine.Tests;

public class GraphHistoryTests
{
    [Fact]
    public async Task Library_graph_includes_branch_tag_and_detached_tips_but_excludes_snapshots()
    {
        using var repo = new TemporaryRepository();
        await repo.Init();
        var initial = (await repo.Git("rev-parse", "HEAD")).Trim();
        await repo.Git("switch", "-c", "feature");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "feature.txt"), "feature\n");
        await repo.Git("add", "--", "feature.txt"); await repo.Git("commit", "-m", "feature tip");
        var feature = (await repo.Git("rev-parse", "HEAD")).Trim();
        await repo.Git("switch", "--detach", initial);
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tagged.txt"), "tagged\n");
        await repo.Git("add", "--", "tagged.txt"); await repo.Git("commit", "-m", "tag only");
        var tagged = (await repo.Git("rev-parse", "HEAD")).Trim();
        await repo.Git("tag", "-a", "v0", "-m", "annotated");
        await repo.Git("switch", "--detach", initial);
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "detached.txt"), "detached\n");
        await repo.Git("add", "--", "detached.txt"); await repo.Git("commit", "-m", "detached tip");
        var detached = (await repo.Git("rev-parse", "HEAD")).Trim();
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        var snapshot = await new SnapshotManager(runner, catalog).CreateAsync(repo.Root, "invisible", default);
        var snapshotCommit = (await repo.Git("rev-parse", snapshot.StateRef)).Trim();
        var history = await new LibHistoryReader().GetGraphAsync(repo.Root, 0, 1000, default);
        Assert.Equal(new[] { initial, feature, tagged, detached }.Order(), history.Select(c => c.Hash).Order());
        Assert.DoesNotContain(history, c => c.Hash == snapshotCommit);
        var positions = history.Select((c, i) => (c.Hash, i)).ToDictionary(p => p.Hash, p => p.i);
        foreach (var commit in history)
            foreach (var parent in commit.Parents) Assert.True(positions[commit.Hash] < positions[parent]);
        Assert.Single(await new LibHistoryReader().GetGraphAsync(repo.Root, 3, 1, default));
    }

    [Theory]
    [InlineData("sha1", false)]
    [InlineData("sha256", false)]
    [InlineData("sha1", true)]
    public async Task Workspace_routes_default_graph_and_falls_back_for_format_filters_or_library_errors(string format, bool fail)
    {
        using var repo = new TemporaryRepository(); await repo.Init(format);
        var library = new Probe(fail); var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction(), library);
        var result = await service.OpenAsync(repo.Root, new(), default);
        Assert.Single(result.Commits);
        Assert.Equal(format == "sha1" ? 1 : 0, library.Calls);
        if (format == "sha1" && !fail) Assert.Equal("library", result.Commits[0].Subject);
        else Assert.Equal("initial", result.Commits[0].Subject);
        var calls = library.Calls;
        Assert.Equal("initial", Assert.Single((await service.OpenAsync(repo.Root, new(Search: "initial"), default)).Commits).Subject);
        Assert.Equal(calls, library.Calls);
        var hash = (await repo.Git("rev-parse", "HEAD")).Trim();
        Assert.Equal(hash, Assert.Single((await service.OpenAsync(repo.Root, new(Search: hash[..8]), default)).Commits).Hash);
    }

    private sealed class Probe(bool fail) : IGraphHistoryReader
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<CommitInfo>> GetGraphAsync(string repository, int skip, int take, CancellationToken ct)
        {
            Calls++;
            if (fail) throw new InvalidOperationException("native unavailable");
            return Task.FromResult<IReadOnlyList<CommitInfo>>([new("library", "library", "test", DateTimeOffset.UtcNow, [])]);
        }
    }
}
