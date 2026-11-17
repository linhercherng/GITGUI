using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public class SnapshotBatchTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task Symbolic_link_targets_share_the_blob_batch_and_restore_as_links_on_Unix()
    {
        if (OperatingSystem.IsWindows()) return; // Windows link privileges vary; Unix CI covers real links.
        using var repo = new TemporaryRepository(); await repo.Init();
        for (var n = 0; n < 12; n++) File.CreateSymbolicLink(Path.Combine(repo.Root, $"link-{n}"), $"missing target {n}\n");
        var runner = new GitProcessRunner(); var commands = new List<string[]>();
        runner.CommandExecuted += (_, entry) => commands.Add(entry.Arguments.ToArray());
        var manager = new SnapshotManager(runner, new MemoryCatalog());
        var snapshot = await manager.CreateAsync(repo.Root, "links", default);
        Assert.Equal(3, commands.Count(a => a.Contains("hash-object")));
        for (var n = 0; n < 12; n++) { File.Delete(Path.Combine(repo.Root, $"link-{n}")); File.WriteAllText(Path.Combine(repo.Root, $"link-{n}"), "changed"); }
        commands.Clear(); await manager.RestoreAsync(repo.Root, snapshot, default);
        Assert.Single(commands, a => a.Contains("cat-file") && a.Contains("--batch"));
        for (var n = 0; n < 12; n++) Assert.Equal($"missing target {n}\n", new FileInfo(Path.Combine(repo.Root, $"link-{n}")).LinkTarget);
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task Sequencer_files_are_hashed_and_restored_in_batches_without_changing_bytes(string format)
    {
        using var repo = new TemporaryRepository();
        await repo.Init(format);
        var directory = Path.Combine(repo.Root, ".git", "rebase-merge");
        Directory.CreateDirectory(directory);
        var payloads = Enumerable.Range(0, 24).Select(n => new byte[] { (byte)n, 0, 255, 13, 10 }).ToArray();
        for (var n = 0; n < payloads.Length; n++) await File.WriteAllBytesAsync(Path.Combine(directory, $"state-{n}"), payloads[n]);
        var runner = new GitProcessRunner();
        var commands = new List<string[]>();
        runner.CommandExecuted += (_, entry) => commands.Add(entry.Arguments.ToArray());
        var manager = new SnapshotManager(runner, new MemoryCatalog());
        var snapshot = await manager.CreateAsync(repo.Root, "batch", default);
        var hashProcesses = commands.Count(a => a.Contains("hash-object"));
        output.WriteLine($"State capture hash-object processes: {hashProcesses}; files: {payloads.Length}");
        Assert.True(commands.Count(a => a.Contains("write-tree")) <= 3);
        Assert.True(hashProcesses <= 3, $"Capture started {hashProcesses} hash-object processes for 24 state files.");
        for (var n = 0; n < payloads.Length; n++) await File.WriteAllTextAsync(Path.Combine(directory, $"state-{n}"), "changed");
        commands.Clear();
        await manager.RestoreAsync(repo.Root, snapshot, default);
        var restoreProcesses = commands.Count(a => a.Contains("cat-file") && a.Contains("--batch"));
        output.WriteLine($"Restore cat-file batch processes: {restoreProcesses}");
        Assert.Equal(2, restoreProcesses);
        Assert.DoesNotContain(commands, a => a.Contains("cat-file") && a.Contains("blob") && !a.Any(x => x.EndsWith(":manifest", StringComparison.Ordinal)));
        for (var n = 0; n < payloads.Length; n++) Assert.Equal(payloads[n], await File.ReadAllBytesAsync(Path.Combine(directory, $"state-{n}")));
    }
}
