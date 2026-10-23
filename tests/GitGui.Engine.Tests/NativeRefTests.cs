using System.Collections.Concurrent;
using GitGui.Core;
using GitGui.Engine.Cli;
using GitGui.Engine.Lib;
using Xunit;

namespace GitGui.Engine.Tests;

[CollectionDefinition("NativeRefEnvironment", DisableParallelization = true)]
public sealed class NativeRefEnvironmentCollection;

[Collection("NativeRefEnvironment")]
public sealed class NativeRefTests
{
    private const string Format = "--format=%(refname)%00%(refname:short)%00%(objectname)%00%(upstream:short)%00%(upstream:track)%00%(HEAD)";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task External_shallow_environment_preserves_selected_Git_tracking_through_CLI_fallback(bool differentTips)
    {
        var previous = Environment.GetEnvironmentVariable("GIT_SHALLOW_FILE");
        using var repo = new TemporaryRepository();
        try
        {
            Environment.SetEnvironmentVariable("GIT_SHALLOW_FILE", null);
            await repo.Init();
            var initial = (await repo.Git("rev-parse", "HEAD")).Trim();
            await repo.Git("remote", "add", "origin", "https://example.invalid/repo.git");
            await repo.Git("update-ref", "refs/remotes/origin/main", initial);
            await repo.Git("config", "branch.main.remote", "origin");
            await repo.Git("config", "branch.main.merge", "refs/heads/main");
            if (differentTips) await repo.Git("commit", "--allow-empty", "-m", "local commit");
            var head = (await repo.Git("rev-parse", "HEAD")).Trim();
            var shallow = Path.Combine(repo.Root, ".git", "external-shallow");
            await File.WriteAllTextAsync(shallow, head + "\n");
            Environment.SetEnvironmentVariable("GIT_SHALLOW_FILE", shallow);

            var expected = await Oracle(repo);
            Assert.Equal(differentTips ? "[ahead 1, behind 1]" : "", expected.Single(r => r.FullName == "refs/heads/main").Tracking);
            var (actual, commands) = await Open(repo);
            Assert.Equal(expected, actual);
            Assert.Contains(commands, command => command.Contains("for-each-ref", StringComparison.Ordinal));
        }
        finally { Environment.SetEnvironmentVariable("GIT_SHALLOW_FILE", previous); }
    }

    [Theory]
    [InlineData("equal")]
    [InlineData("behind")]
    [InlineData("diverged")]
    [InlineData("gone")]
    [InlineData("local")]
    [InlineData("mapped")]
    [InlineData("packed")]
    [InlineData("detached")]
    [InlineData("unborn")]
    public async Task Native_refs_preserve_tracking_and_repository_state_variants(string kind)
    {
        using var repo = new TemporaryRepository(); await repo.Init(commit: kind != "unborn");
        if (kind != "unborn")
        {
            var initial = (await repo.Git("rev-parse", "HEAD")).Trim();
            await repo.Git("remote", "add", "origin", "https://example.invalid/repo.git");
            var upstream = kind == "local" ? "refs/heads/upstream" : kind == "mapped" ? "refs/remotes/mapped/main" : "refs/remotes/origin/main";
            if (kind == "mapped") await repo.Git("config", "remote.origin.fetch", "+refs/heads/*:refs/remotes/mapped/*");
            await repo.Git("config", "branch.main.remote", kind == "local" ? "." : "origin");
            await repo.Git("config", "branch.main.merge", kind == "local" ? "refs/heads/upstream" : "refs/heads/main");
            if (kind != "gone") await repo.Git("update-ref", upstream, initial);
            if (kind is "behind" or "diverged")
            {
                var other = (await repo.Git("commit-tree", "HEAD^{tree}", "-p", initial, "-m", "remote commit")).Trim();
                await repo.Git("update-ref", upstream, other);
                if (kind == "diverged") await repo.Git("commit", "--allow-empty", "-m", "local commit");
            }
            if (kind == "packed") await repo.Git("pack-refs", "--all");
            if (kind == "detached") await repo.Git("checkout", "--detach", initial);
        }
        var expected = await Oracle(repo); var (actual, commands) = await Open(repo);
        Assert.Equal(expected, actual);
        if (kind is "behind" or "diverged")
            Assert.Contains(commands, command => command.Contains("for-each-ref", StringComparison.Ordinal));
        else
            Assert.DoesNotContain(commands, command => command.Contains("for-each-ref", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ambiguous")]
    [InlineData("warn-disabled")]
    [InlineData("sha256")]
    [InlineData("shallow")]
    [InlineData("namespace-root")]
    public async Task Unsupported_ref_semantics_use_selected_CLI(string kind)
    {
        using var repo = new TemporaryRepository(); await repo.Init(kind == "sha256" ? "sha256" : "sha1");
        if (kind == "ambiguous") await repo.Git("tag", "main");
        if (kind == "warn-disabled") await repo.Git("config", "core.warnAmbiguousRefs", "false");
        if (kind == "reftable") await repo.Git("refs", "migrate", "--ref-format=reftable");
        if (kind == "shallow") await File.WriteAllTextAsync(Path.Combine(repo.Root, ".git", "shallow"), (await repo.Git("rev-parse", "HEAD")).Trim() + "\n");
        if (kind == "namespace-root")
        {
            await repo.Git("checkout", "--detach");
            await repo.Git("update-ref", "-d", "refs/heads/main");
            var heads = Path.Combine(repo.Root, ".git", "refs", "heads");
            if (Directory.Exists(heads)) Directory.Delete(heads);
            await repo.Git("update-ref", "refs/heads", "HEAD");
        }
        var expected = await Oracle(repo); var (actual, commands) = await Open(repo);
        Assert.Equal(expected, actual);
        Assert.Contains(commands, command => command.Contains("for-each-ref", StringComparison.Ordinal));
    }

    [ReftableAvailableFact]
    public Task Reftable_refs_use_selected_CLI() => Unsupported_ref_semantics_use_selected_CLI("reftable");

    [Fact]
    public async Task Symbolic_cycles_and_selected_config_mismatch_are_rejected_without_native_recursion()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        using var native = new LibHistoryReader();
        using var library = new LibGit2Sharp.Repository(repo.Root);
        var entries = library.Config.Select(e => new KeyValuePair<string, string>(e.Key, e.Value)).ToArray();
        var context = new NativeReadContext(entries, new GitRuntime().ResolveExecutable(null), null, null, false);
        Assert.NotNull(await native.TryReadRefsAsync(repo.Root, context, default));
        Assert.Null(await native.TryReadRefsAsync(repo.Root, context with { Configuration = entries.Append(new("branch.main.remote", "different-selected-git")).ToArray() }, default));
        var refs = Path.Combine(repo.Root, ".git", "refs", "gitgui"); Directory.CreateDirectory(refs);
        await File.WriteAllTextAsync(Path.Combine(refs, "cycle-a"), "ref: refs/gitgui/cycle-b\n");
        await File.WriteAllTextAsync(Path.Combine(refs, "cycle-b"), "ref: refs/gitgui/cycle-a\n");
        Assert.Null(await native.TryReadRefsAsync(repo.Root, context, default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => native.TryReadRefsAsync(repo.Root, context, cancellation.Token));
    }

    [Fact]
    public async Task Linked_worktree_refs_mark_its_own_HEAD_and_selected_includes_are_rechecked()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var linked = Path.Combine(repo.Root, "linked-worktree");
        await repo.Git("worktree", "add", "-b", "linked", linked);
        var expected = await Oracle(repo, linked); var (actual, commands) = await Open(repo, linked);
        Assert.Equal(expected, actual);
        Assert.True(actual.Single(r => r.FullName == "refs/heads/linked").IsCurrent);
        Assert.DoesNotContain(commands, command => command.Contains("for-each-ref", StringComparison.Ordinal));

        await repo.Git("remote", "add", "origin", "https://example.invalid/repo.git");
        await repo.Git("update-ref", "refs/remotes/origin/main", "HEAD");
        var include = Path.Combine(repo.Root, ".git", "read-refs-include");
        await File.WriteAllTextAsync(include, "[branch \"main\"]\n remote = origin\n merge = refs/heads/main\n");
        await repo.Git("config", "include.path", include);
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog(); using var native = new LibHistoryReader();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction(), native);
        Assert.Equal(await Oracle(repo), (await service.OpenAsync(repo.Root, new(), default)).Refs);
        await File.WriteAllTextAsync(include, "[branch \"main\"]\n remote = origin\n merge = refs/heads/no-longer-existing-upstream\n");
        Assert.Equal(await Oracle(repo), (await service.OpenAsync(repo.Root, new(), default)).Refs);
        commands.Clear(); runner.CommandExecuted += (_, e) => commands.Enqueue(e.Command);
        Assert.True((await service.ExecuteAsync(repo.Root, new(GitAction.CreateBranch) { Name = "fresh-write" }, null, default)).Success);
        Assert.Contains(commands, command => command.Contains("for-each-ref", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_refs_match_symbolic_HEAD_and_unpeeled_tags_and_delegate_divergence_to_CLI(bool ahead)
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var initial = (await repo.Git("rev-parse", "HEAD")).Trim();
        await repo.Git("remote", "add", "origin", "https://example.invalid/repo.git");
        await repo.Git("update-ref", "refs/remotes/origin/main", initial);
        await repo.Git("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        await repo.Git("config", "branch.main.remote", "origin");
        await repo.Git("config", "branch.main.merge", "refs/heads/main");
        if (ahead) await repo.Git("commit", "--allow-empty", "-m", "ahead");
        await repo.Git("tag", "-a", "release", "-m", "annotated");
        await repo.Git("tag", "base", initial);
        await repo.Git("update-ref", "refs/gitgui/private", initial);
        var expected = await Oracle(repo);
        var (actual, commands) = await Open(repo);
        Assert.Equal(expected, actual);
        if (ahead)
            Assert.Contains(commands, command => command.Contains("for-each-ref", StringComparison.Ordinal));
        else
            Assert.DoesNotContain(commands, command => command.Contains("for-each-ref", StringComparison.Ordinal));
        Assert.Equal(ahead ? "[ahead 1]" : "", actual.Single(r => r.FullName == "refs/heads/main").Tracking);
        Assert.Equal((await repo.Git("rev-parse", "release")).Trim(), actual.Single(r => r.FullName == "refs/tags/release").Target);
        Assert.DoesNotContain(actual, r => r.FullName.StartsWith("refs/gitgui/", StringComparison.Ordinal));
    }

    private static async Task<RefInfo[]> Oracle(TemporaryRepository repo, string? root = null)
        => (await repo.Git("-C", root ?? repo.Root, "for-each-ref", Format, "refs/heads", "refs/remotes", "refs/tags"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
            {
                var f = line.TrimEnd('\r').Split('\0');
                return new RefInfo(f[1], f[0], f[2], f[3].Length == 0 ? null : f[3], f[4], f[5] == "*");
            }).ToArray();

    private static async Task<(IReadOnlyList<RefInfo> Refs, ConcurrentQueue<string> Commands)> Open(TemporaryRepository repo, string? root = null)
    {
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog(); using var native = new LibHistoryReader();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction(), native);
        var commands = new ConcurrentQueue<string>(); runner.CommandExecuted += (_, e) => commands.Enqueue(e.Command);
        var state = await service.OpenAsync(root ?? repo.Root, new(), default);
        return (state.Refs, commands);
    }
}
