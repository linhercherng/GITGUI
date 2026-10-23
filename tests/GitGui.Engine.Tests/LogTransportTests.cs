using System.Text;
using GitGui.Core;
using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class LogTransportTests
{
    [Fact]
    public async Task Thousands_of_distinct_visible_tips_use_bounded_arguments_and_preserve_all_namespaces_and_detached_HEAD()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var initial = (await repo.Git("rev-parse", "HEAD")).Trim();
        const int count = 1200;
        var import = new StringBuilder();
        for (var i = 1; i <= count; i++)
        {
            var message = $"Visible item {i:D4}\n";
            import.Append($"commit refs/heads/import-staging\nmark :{i}\nauthor Fixture Author <fixture@example.invalid> {1700000000 + i} +0000\ncommitter Fixture Author <fixture@example.invalid> {1700000000 + i} +0000\ndata {Encoding.UTF8.GetByteCount(message)}\n{message}from {initial}\nM 100644 inline timeline.txt\ndata 2\nx\n\n");
        }
        import.Append("commit refs/heads/private-staging\ncommitter Fixture Author <fixture@example.invalid> 1700002000 +0000\ndata 13\nINTERNAL ONLY\n\ndone\n");
        var runner = new GitProcessRunner();
        await Write(runner, repo.Root, ["fast-import", "--quiet", "--done"], Encoding.UTF8.GetBytes(import.ToString()));
        var latest = (await repo.Git("rev-parse", "refs/heads/import-staging")).Trim();
        var hidden = (await repo.Git("rev-parse", "refs/heads/private-staging")).Trim();
        // Every generated commit is a sibling, so omitting any namespace really
        // loses commits instead of reaching them through another tip's ancestry.
        var objects = await runner.RunAsync(repo.Root, ["cat-file", "--batch-all-objects", "--batch-check=%(objectname) %(objecttype)"], default);
        Assert.Equal(0, objects.ExitCode);
        var commits = Encoding.ASCII.GetString(objects.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.EndsWith(" commit", StringComparison.Ordinal)).Select(line => line.Split(' ')[0]).ToArray();
        Assert.Equal(count + 2, commits.Length);
        var refs = new StringBuilder(); var index = 0;
        foreach (var oid in commits.Where(oid => oid != initial && oid != hidden && oid != latest))
        {
            var prefix = (index % 3) switch { 0 => "refs/heads/stress-", 1 => "refs/remotes/origin/stress-", _ => "refs/tags/stress-" };
            refs.Append($"update {prefix}{index++:D4} {oid}\n");
        }
        refs.Append($"update refs/gitgui/snapshots/private-only {hidden}\ndelete refs/heads/import-staging\ndelete refs/heads/private-staging\n");
        await Write(runner, repo.Root, ["update-ref", "--stdin"], Encoding.ASCII.GetBytes(refs.ToString()));
        await repo.Git("switch", "--detach", latest);
        var catalog = new MemoryCatalog();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction());
        var commands = new List<CommandEntry>(); runner.CommandExecuted += (_, command) => commands.Add(command);
        var opened = await service.OpenAsync(repo.Root, new(Take: 2000), default);
        Assert.Equal(count + 1, opened.Commits.Count);
        Assert.Contains(opened.Commits, commit => commit.Hash == latest);
        Assert.DoesNotContain(opened.Commits, commit => commit.Hash == hidden);
        Assert.Equal(commits.Where(oid => oid != hidden).Order(), opened.Commits.Select(commit => commit.Hash).Order());
        var log = Assert.Single(commands, command => command.Arguments.FirstOrDefault() == "log");
        Assert.True(log.Arguments.Sum(argument => argument.Length + 1) < 4096, "Revision lists must not grow the process command line.");
        Assert.Contains("--stdin", log.Arguments);
        Assert.DoesNotContain(latest, log.Arguments);

        var filtered = await service.GetHistoryPageAsync(repo.Root,
            new(Search: "Visible item", Author: "Fixture Author", Path: "timeline.txt", Since: "2023-11-01", Until: "2023-12-01", Take: 2000), null, default);
        Assert.Equal(count, filtered.Commits.Count);
        Assert.All(filtered.Commits, commit => Assert.StartsWith("Visible item", commit.Subject));
        commands.Clear();
        var exact = await service.GetHistoryPageAsync(repo.Root, new(Search: latest, Take: 2000), null, default);
        Assert.Equal(latest, Assert.Single(exact.Commits).Hash);
        var exactLog = Assert.Single(commands, command => command.Arguments.FirstOrDefault() == "log");
        Assert.Contains("--no-walk", exactLog.Arguments);
        Assert.DoesNotContain("--stdin", exactLog.Arguments);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetHistoryPageAsync(repo.Root, new(), null, cancelled.Token));
    }

    private static async Task Write(GitProcessRunner runner, string root, string[] args, byte[] input)
    {
        var result = await runner.RunAsync(root, args, default, new() { IsReadOnly = false, Trusted = true, Input = input, HideOutput = true });
        Assert.True(result.ExitCode == 0, result.Error);
    }
}
