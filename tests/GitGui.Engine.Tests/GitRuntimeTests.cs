using System.Diagnostics;
using GitGui.Core;
using GitGui.Engine.Cli;
using GitGui.Infrastructure;
using Xunit;

namespace GitGui.Engine.Tests;

public class GitRuntimeTests
{
    [Fact]
    public async Task Explicit_installation_is_persisted_and_invalid_selection_keeps_previous_value()
    {
        using var repo = new TemporaryRepository();
        var store = new WorkspaceStorage(Path.Combine(repo.Root, "settings.db"));
        var executable = FindSystemGit();
        var runtime = new GitRuntime(store, repo.Root, searchPath: "");
        var info = await runtime.SaveAsync(executable, default);
        Assert.Equal(executable, info.Executable); Assert.StartsWith("git version ", info.Version);
        var reopened = new GitRuntime(new WorkspaceStorage(Path.Combine(repo.Root, "settings.db")), repo.Root, searchPath: "");
        Assert.Equal(executable, await reopened.GetPathAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SaveAsync(Path.Combine(repo.Root, "missing", "git.exe"), default));
        Assert.Equal(executable, await runtime.GetPathAsync(default));
        Assert.Equal(executable, (await reopened.CheckAsync(await reopened.GetPathAsync(default), default)).Executable);
    }

    [Fact]
    public async Task Missing_explicit_installation_never_silently_falls_back_to_PATH()
    {
        using var repo = new TemporaryRepository(); var store = new WorkspaceStorage(Path.Combine(repo.Root, "settings.db"));
        await store.SaveGitPathAsync(Path.Combine(repo.Root, "removed", "git.exe"), default);
        var runner = new GitProcessRunner(new GitRuntime(store, repo.Root));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ReadAsync(repo.Root, ["--version"], default));
        Assert.Contains("Git", error.Message);
    }

    [Fact]
    public async Task Empty_PATH_and_no_adjacent_installation_report_an_actionable_error()
    {
        using var repo = new TemporaryRepository();
        var runtime = new GitRuntime(baseDirectory: repo.Root, searchPath: "");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.CheckAsync(null, default));
        Assert.Contains("Git", error.Message); Assert.Contains("設定", error.Message);
    }

    internal static string FindSystemGit()
    {
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        return Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator)
            .Select(p => Path.Combine(p.Trim('"'), name)).First(File.Exists);
    }
}
