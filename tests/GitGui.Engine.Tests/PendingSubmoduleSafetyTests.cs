using System.Text;
using GitGui.Core;
using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class PendingSubmoduleSafetyTests
{
    [Fact]
    public async Task Continue_external_rebase_refuses_future_gitlink_without_snapshot_or_state_mutation()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var upstream = (await repo.Git("rev-parse", "HEAD")).Trim();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "edit pause\n");
        await repo.Git("commit", "-am", "pause before unsupported commit");
        await repo.Git("update-index", "--add", "--cacheinfo", "160000," + upstream + ",module");
        await repo.Git("commit", "-m", "later commit adds gitlink");

        var editor = Path.Combine(repo.Root, ".git", "pause-first.sh");
        await File.WriteAllTextAsync(editor, "#!/bin/sh\nsed '1s/^pick /edit /' \"$1\" > \"$1.tmp\" && mv -- \"$1.tmp\" \"$1\"\n", new UTF8Encoding(false));
        var runner = new GitProcessRunner();
        var paused = await runner.RunAsync(repo.Root, ["rebase", "--interactive", upstream], default,
            new() { IsReadOnly = false, Trusted = true, Environment = new Dictionary<string, string?>
            {
                ["GIT_SEQUENCE_EDITOR"] = "sh '" + editor.Replace('\\', '/').Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'",
                ["GIT_EDITOR"] = "true", ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_GLOBAL"] = Path.Combine(repo.Root, "missing-global-config")
            } });
        Assert.True(paused.ExitCode == 0, paused.Error);
        var stateDirectory = Path.Combine(repo.Root, ".git", "rebase-merge");
        Assert.True(Directory.Exists(stateDirectory));
        Assert.DoesNotContain("160000", await repo.Git("ls-files", "--stage"));
        Assert.Contains("later commit adds gitlink", await File.ReadAllTextAsync(Path.Combine(stateDirectory, "git-rebase-todo")));
        await File.WriteAllBytesAsync(Path.Combine(repo.Root, "private.bin"), [0, 255, 128, 13, 10, 42]);
        var beforeHead = await repo.Git("rev-parse", "HEAD");
        var beforeIndex = await File.ReadAllBytesAsync(Path.Combine(repo.Root, ".git", "index"));
        var beforeState = CaptureFiles(stateDirectory);
        var beforeWork = CaptureFiles(repo.Root, excludeGit: true);
        var catalog = new MemoryCatalog();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new SnapshotManager(runner, catalog), new TestInteraction());

        var outcome = await service.ExecuteAsync(repo.Root, new(GitAction.ContinueOperation), null, default);

        Assert.False(outcome.Success); Assert.Contains("Submodule", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(outcome.Snapshot); Assert.Empty(await catalog.ListAsync(repo.Root, default));
        Assert.Equal(beforeHead, await repo.Git("rev-parse", "HEAD"));
        Assert.Equal(beforeIndex, await File.ReadAllBytesAsync(Path.Combine(repo.Root, ".git", "index")));
        AssertFilesEqual(beforeState, CaptureFiles(stateDirectory));
        AssertFilesEqual(beforeWork, CaptureFiles(repo.Root, excludeGit: true));
    }

    private static Dictionary<string, byte[]> CaptureFiles(string root, bool excludeGit = false)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(root, path).Replace('\\', '/')))
            .Where(item => !excludeGit || !item.Relative.StartsWith(".git/", StringComparison.Ordinal))
            .ToDictionary(item => item.Relative, item => File.ReadAllBytes(item.Path), StringComparer.Ordinal);

    private static void AssertFilesEqual(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in expected) Assert.Equal(bytes, actual[path]);
    }
}
