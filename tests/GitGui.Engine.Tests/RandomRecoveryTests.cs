using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public class RandomRecoveryTests
{
    [Fact]
    public async Task Seeded_mutation_sequence_restores_refs_flags_staging_and_every_raw_file()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "assumed.txt"), "assumed\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "skipped.txt"), "skipped\n");
        await repo.Git("add", "--all"); await repo.Git("commit", "-m", "baseline");
        await repo.Git("update-index", "--assume-unchanged", "assumed.txt");
        await repo.Git("update-index", "--skip-worktree", "skipped.txt");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "intent 中文.txt"), "intent\r\n");
        await repo.Git("add", "-N", "--", "intent 中文.txt");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "staged\n");
        await repo.Git("add", "--", "tracked.txt");
        await File.AppendAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "unstaged\r\n");
        await File.WriteAllBytesAsync(Path.Combine(repo.Root, "untracked.bin"), [0, 1, 128, 255]);
        await repo.Git("branch", "saved"); await repo.Git("tag", "-a", "baseline", "-m", "annotation");
        var expectedRefs = await repo.Git("for-each-ref", "--format=%(refname) %(objectname)", "refs/heads", "refs/tags");
        var expectedIndex = await repo.Git("ls-files", "--stage", "-z");
        var expectedFlags = await repo.Git("ls-files", "-v", "-z");
        var original = ReadFiles();
        var manager = new SnapshotManager(new GitProcessRunner(), new MemoryCatalog());
        var snapshot = await manager.CreateAsync(repo.Root, "before seeded mutations", default);
        await repo.Git("update-index", "--no-assume-unchanged", "assumed.txt");
        await repo.Git("update-index", "--no-skip-worktree", "skipped.txt");
        await repo.Git("reset", "--hard"); await repo.Git("clean", "-f");
        var random = new Random(64293);
        for (var step = 0; step < 20; step++)
        {
            var path = "mutation-" + random.Next(6) + ".bin";
            var bytes = new byte[random.Next(1, 300)]; random.NextBytes(bytes);
            await File.WriteAllBytesAsync(Path.Combine(repo.Root, path), bytes);
            if (step % 2 == 0) await repo.Git("add", "--", path);
            if (step % 5 == 0) await repo.Git("commit", "--allow-empty", "-m", "mutation " + step);
        }
        await repo.Git("branch", "-D", "saved"); await repo.Git("tag", "-d", "baseline");
        await repo.Git("branch", "new-after"); await repo.Git("gc", "--prune=now");
        await manager.RestoreAsync(repo.Root, snapshot, default);
        Assert.Equal(expectedRefs, await repo.Git("for-each-ref", "--format=%(refname) %(objectname)", "refs/heads", "refs/tags"));
        Assert.Equal(expectedIndex, await repo.Git("ls-files", "--stage", "-z"));
        Assert.Equal(expectedFlags, await repo.Git("ls-files", "-v", "-z"));
        var actual = ReadFiles(); Assert.Equal(original.Keys.Order(), actual.Keys.Order());
        foreach (var pair in original) Assert.Equal(pair.Value, actual[pair.Key]);
        Dictionary<string, byte[]> ReadFiles() => Directory.GetFiles(repo.Root)
            .ToDictionary(p => Path.GetFileName(p), File.ReadAllBytes);
    }
}
