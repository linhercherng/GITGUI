using System.Diagnostics;
using GitGui.Engine.Cli;
using GitGui.Core;
using GitGui.Engine.Lib;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class RepositoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "gitgui-test-" + Guid.NewGuid());

    public RepositoryTests() => Directory.CreateDirectory(root);

    private async Task<string> Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(root, "absent-config");
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await stderr);
        return await stdout;
    }

    private async Task Init(string format = "sha1")
    {
        await Git("init", "-b", "main", "--object-format=" + format);
        await Git("config", "user.name", "GITGUI Test");
        await Git("config", "user.email", "test@example.invalid");
        await Git("config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(root, "中文 file.txt"), "before\n");
        await Git("add", "--", "中文 file.txt");
        await Git("commit", "-m", "First commit");
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task Reads_real_repository_without_changing_index(string format)
    {
        await Init(format);
        await File.AppendAllTextAsync(Path.Combine(root, "中文 file.txt"), "after\n");
        await File.WriteAllTextAsync(Path.Combine(root, "--option.txt"), "new");
        var before = await File.ReadAllBytesAsync(Path.Combine(root, ".git", "index"));
        var cli = new CliRepoReader(new GitProcessRunner());
        var reader = new RoutedRepoReader(cli, new LibHistoryReader());
        var status = await reader.GetStatusAsync(root, default);
        Assert.Equal("main", status.Branch);
        Assert.Equal(2, status.Files.Count);
        Assert.Contains("+after", await reader.GetDiffAsync(root, "中文 file.txt", false, default));
        Assert.Equal("First commit", Assert.Single(await reader.GetLogAsync(root, 10, default)).Subject);
        Assert.Equal(format, (await cli.GetFormatAsync(root, default)).ObjectFormat);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(root, ".git", "index")));
    }

    [Fact]
    public async Task Reads_unborn_repository_and_staged_diff()
    {
        await Git("init", "-b", "main");
        var reader = new CliRepoReader(new GitProcessRunner());
        Assert.Empty(await reader.GetLogAsync(root, 10, default));
        Assert.Equal("main", (await reader.GetStatusAsync(root, default)).Branch);
        await File.WriteAllTextAsync(Path.Combine(root, "new.txt"), "staged\n");
        await Git("add", "--", "new.txt");
        Assert.Contains("+staged", await reader.GetDiffAsync(root, "new.txt", true, default));
    }

    [Fact]
    public async Task Does_not_execute_fsmonitor_or_external_diff()
    {
        await Init();
        await Git("config", "core.fsmonitor", "touch injected-marker");
        await Git("config", "diff.external", "touch injected-marker");
        await File.AppendAllTextAsync(Path.Combine(root, "中文 file.txt"), "modified\n");
        var reader = new CliRepoReader(new GitProcessRunner());
        await reader.GetStatusAsync(root, default);
        await reader.GetDiffAsync(root, "中文 file.txt", false, default);
        Assert.False(File.Exists(Path.Combine(root, "injected-marker")));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}
