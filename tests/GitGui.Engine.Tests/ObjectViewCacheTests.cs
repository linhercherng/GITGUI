using GitGui.Core;
using GitGui.Engine.Cli;
using GitGui.Engine.Lib;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class ObjectViewCacheTests
{
    [Fact]
    public async Task Unshallow_with_unchanged_tips_reloads_native_history()
    {
        using var source = new TemporaryRepository(); await source.Init();
        await source.Git("commit", "--allow-empty", "-m", "second");
        using var clone = new TemporaryRepository();
        await source.Git("clone", "--no-local", "--depth", "1", new Uri(source.Root + Path.DirectorySeparatorChar).AbsoluteUri, clone.Root);
        using var native = new LibHistoryReader();
        var service = Create(native);
        var before = await service.OpenAsync(clone.Root, new(), default);
        Assert.Single(before.Commits);
        await clone.Git("fetch", "--unshallow");
        var after = await service.OpenAsync(clone.Root, new(), default);
        Assert.NotEqual(before.HistoryGeneration, after.HistoryGeneration);
        Assert.Equal(2, after.Commits.Count);
    }

    [Fact]
    public async Task Replacement_changes_refresh_immutable_details_and_history_including_packed_refs()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        using var native = new LibHistoryReader(); var service = Create(native);
        var oid = (await repo.Git("rev-parse", "HEAD")).Trim();
        Assert.Equal("initial", (await service.GetCommitAsync(repo.Root, oid, default)).Commit.Subject);
        var before = await service.OpenAsync(repo.Root, new(), default);
        var replacement = (await repo.Git("commit-tree", "HEAD^{tree}", "-m", "replacement message")).Trim();
        await repo.Git("replace", oid, replacement);
        await repo.Git("pack-refs", "--all");
        Assert.Equal("replacement message", (await service.GetCommitAsync(repo.Root, oid, default)).Commit.Subject);
        var after = await service.OpenAsync(repo.Root, new(), default);
        Assert.NotEqual(before.HistoryGeneration, after.HistoryGeneration);
        Assert.Equal("replacement message", Assert.Single(after.Commits).Subject);
        await repo.Git("replace", "-d", oid);
        Assert.Equal("initial", (await service.GetCommitAsync(repo.Root, oid, default)).Commit.Subject);
    }

    private static WorkspaceService Create(IGraphHistoryReader native)
    {
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        return new(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction(), native);
    }
}
