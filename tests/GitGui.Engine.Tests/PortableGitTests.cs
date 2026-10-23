using GitGui.Core;
using GitGui.Engine.Cli;
using GitGui.Infrastructure;
using Xunit;

namespace GitGui.Engine.Tests;

public class PortableGitTests
{
    // The Windows CI provisions a verified official PortableGit archive. Other platforms exercise native explicit Git paths.
    [Fact]
    public async Task Explicit_Git_with_no_Git_on_PATH_can_commit_run_hooks_and_restore_refs_and_worktree()
    {
        using var repo = new TemporaryRepository();
        var portable = Environment.GetEnvironmentVariable("GITGUI_TEST_PORTABLE_ROOT");
        var executable = portable is null ? GitRuntimeTests.FindSystemGit() : Path.Combine(portable, "cmd", "git.exe");
        var store = new WorkspaceStorage(Path.Combine(repo.Root, ".settings", "git.db"));
        var systemPath = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.System) : "";
        var runtime = new GitRuntime(store, repo.Root, searchPath: systemPath);
        await runtime.SaveAsync(executable, default);
        var runner = new GitProcessRunner(runtime); var catalog = new MemoryCatalog();
        var commands = new List<CommandEntry>(); runner.CommandExecuted += (_, entry) => commands.Add(entry);
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction());
        // Database is outside this new repository, so init's empty-directory guard remains meaningful.
        using var worktree = new TemporaryRepository();
        await Execute(new(GitAction.Init) { Name = "main" });
        await Run("config", "user.name", "Portable Test"); await Run("config", "user.email", "portable@example.invalid");
        await Run("config", "commit.gpgsign", "false"); await Run("config", "core.autocrlf", "false");
        var hook = Path.Combine(worktree.Root, ".git", "hooks", "pre-commit");
        await File.WriteAllTextAsync(hook, "#!/bin/sh\nprintf 'portable hook ran\\n' > hook-result.txt\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(Path.Combine(worktree.Root, "中文 file.txt"), "first\r\n");
        await Execute(new(GitAction.Stage) { Paths = ["中文 file.txt"] });
        await Execute(new(GitAction.Commit) { Message = "first" });
        Assert.True(File.Exists(Path.Combine(worktree.Root, "hook-result.txt")));
        var original = await Run("rev-parse", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(worktree.Root, "中文 file.txt"), "second\r\n");
        await Execute(new(GitAction.Stage) { Paths = ["中文 file.txt"] });
        var amended = await Execute(new(GitAction.Commit) { Message = "second", Amend = true });
        await Execute(new(GitAction.CreateBranch) { Name = "after" });
        await Execute(new(GitAction.RestoreSnapshot) { SnapshotId = amended.Snapshot!.Id });
        Assert.Equal(original, await Run("rev-parse", "HEAD"));
        Assert.Equal("second\r\n", await File.ReadAllTextAsync(Path.Combine(worktree.Root, "中文 file.txt")));
        Assert.Empty((await Run("for-each-ref", "refs/heads/after")).Trim());
        Assert.Contains("second", await Run("show", ":中文 file.txt"));
        using var bare = new TemporaryRepository(); using var cloned = new TemporaryRepository();
        var initialized = await runner.RunAsync(bare.Root, ["init", "--bare", "--initial-branch=main"], default,
            new() { IsReadOnly = false, Trusted = true });
        Assert.Equal(0, initialized.ExitCode);
        await Run("remote", "add", "origin", bare.Root);
        await Execute(new(GitAction.Push) { Remote = "origin" });
        var clone = await service.ExecuteAsync(cloned.Root, new(GitAction.Clone) { Url = bare.Root }, null, default);
        Assert.True(clone.Success, clone.Message);
        var clonedHead = await runner.ReadAsync(cloned.Root, ["rev-parse", "HEAD"], default);
        Assert.Equal(original, System.Text.Encoding.UTF8.GetString(clonedHead.Output));
        Assert.All(commands, command => Assert.Contains(executable, command.Command));
        Assert.Contains(commands, command => command.Command.Contains("update-ref --stdin", StringComparison.Ordinal));
        async Task<string> Run(params string[] args)
        {
            var result = await runner.RunAsync(worktree.Root, args, default, new() { IsReadOnly = false, Trusted = true });
            Assert.Equal(0, result.ExitCode); return System.Text.Encoding.UTF8.GetString(result.Output);
        }
        async Task<OperationOutcome> Execute(GitOperation op)
        {
            var result = await service.ExecuteAsync(worktree.Root, op, null, default);
            Assert.True(result.Success, result.Message); return result;
        }
    }

    [Fact]
    public void Adjacent_portable_folder_is_discovered_without_PATH_and_auto_mode_survives_relocation()
    {
        using var application = new TemporaryRepository();
        var folder = Path.Combine(application.Root, "PortableGit", "cmd"); Directory.CreateDirectory(folder);
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        File.WriteAllText(Path.Combine(folder, name), "discovery fixture only");
        var runtime = new GitRuntime(baseDirectory: application.Root, searchPath: "");
        Assert.Equal(Path.Combine(folder, name), runtime.ResolveExecutable(null));
        var relocated = Path.Combine(application.Root, "relocated"); Directory.CreateDirectory(relocated);
        Directory.Move(Path.Combine(application.Root, "PortableGit"), Path.Combine(relocated, "PortableGit"));
        var moved = new GitRuntime(baseDirectory: relocated, searchPath: "");
        Assert.Equal(Path.Combine(relocated, "PortableGit", "cmd", name), moved.ResolveExecutable(null));
    }
}
