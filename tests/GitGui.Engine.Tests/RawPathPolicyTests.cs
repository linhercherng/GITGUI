using System.Diagnostics;
using System.Text;
using GitGui.Core;
using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class RawPathPolicyTests
{
    [Fact]
    public void Rename_preserves_both_original_byte_identities()
    {
        var prefix = Encoding.ASCII.GetBytes("2 R. N... 100644 100644 100644 abc def R100 ");
        var raw = prefix.Concat(new byte[] { 0xff, 0, 0xfe, 0 }).ToArray();
        var file = Assert.Single(PorcelainStatusParser.Parse(raw).Files);
        Assert.Equal(new byte[] { 0xff }, file.RawPath); Assert.Equal(new byte[] { 0xfe }, file.RawOriginalPath);
        Assert.Equal("\\xFF", file.Path); Assert.Equal("\\xFE", file.OriginalPath); Assert.False(file.IsPathSupported);
    }

    [Fact]
    public async Task Unrepresentable_UTF16_selection_does_not_stage_a_replacement_alias()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "�"), "valid alias\n");
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new SnapshotManager(runner, catalog), new TestInteraction());
        var before = await repo.Git("ls-files", "--stage", "-z");
        var result = await service.ExecuteAsync(repo.Root, new(GitAction.Stage) { Paths = ["\ud800"] }, null, default);
        Assert.False(result.Success); Assert.Contains("encoding", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await repo.Git("ls-files", "--stage", "-z"));
    }

    [Fact]
    public async Task Linux_raw_FF_and_valid_replacement_alias_remain_distinct_and_invalid_selection_is_blocked()
    {
        if (!OperatingSystem.IsLinux()) return; // This fixture needs a filesystem allowing arbitrary non-UTF-8 filename bytes.
        using var repo = new TemporaryRepository(); await repo.Init();
        async Task Shell(string command)
        {
            var start = new ProcessStartInfo("sh") { WorkingDirectory = repo.Root, UseShellExecute = false };
            start.ArgumentList.Add("-c"); start.ArgumentList.Add(command);
            using var process = Process.Start(start)!; await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
        }
        await Shell("printf private > \"$(printf '\\377')\"");
        try
        {
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "�"), "valid alias\n");
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new SnapshotManager(runner, catalog), new TestInteraction());
        var state = await service.OpenAsync(repo.Root, new(), default);
        var invalid = Assert.Single(state.Status.Files, f => !f.IsPathSupported);
        Assert.Equal(new byte[] { 0xff }, invalid.RawPath);
        Assert.Contains(state.Status.Files, f => f.Path == "�" && f.IsPathSupported);
        var before = await repo.Git("ls-files", "--stage", "-z");
        var blocked = await service.ExecuteAsync(repo.Root, new(GitAction.Stage) { Paths = [invalid.Path] }, null, default);
        Assert.False(blocked.Success); Assert.Equal(before, await repo.Git("ls-files", "--stage", "-z"));
        var valid = await service.ExecuteAsync(repo.Root, new(GitAction.Stage) { Paths = ["�"] }, null, default);
        Assert.True(valid.Success, valid.Message);
        var index = await runner.ReadAsync(repo.Root, ["ls-files", "-z"], default);
        Assert.DoesNotContain((byte)0xff, index.Output); Assert.Contains("�\0", Encoding.UTF8.GetString(index.Output));
        }
        finally { await Shell("rm -f -- \"$(printf '\\377')\""); }
    }
}
