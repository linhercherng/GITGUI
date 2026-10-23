using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GitGui.Core;
using GitGui.Engine.Cli;
using GitGui.Engine.Lib;
using GitGui.Infrastructure;

// Test-only apphost overlaid onto an extracted ZIP; never included in the release.
// The driver verifies the shared DLL hashes before copying only these four harness files.
var root = Path.GetFullPath(args.Single());
Directory.CreateDirectory(root);
var package = AppContext.BaseDirectory;
Check(RuntimeEnvironment.GetRuntimeDirectory().StartsWith(package, StringComparison.OrdinalIgnoreCase), "Runtime outside package");
var runtime = new GitRuntime();
var git = await runtime.CheckAsync(null, default);
Check(git.Executable == Path.Combine(package, "PortableGit", "cmd", "git.exe"), "Git outside package");
var storage = new WorkspaceStorage(Path.Combine(root, "settings", "test.db"));
var runner = new GitProcessRunner(runtime);
var commands = new List<CommandEntry>();
runner.CommandExecuted += (_, e) => commands.Add(e);
var service = new WorkspaceService(runner, storage, storage, new(runner, storage), new AcceptInTest());
var work = Path.Combine(root, "倉庫 with spaces"); Directory.CreateDirectory(work);
await storage.TrustAsync(work, default);
await Execute(new(GitAction.Init) { Name = "main" });
await Run("config", "user.name", "Package Smoke");
await Run("config", "user.email", "package@example.invalid");
await Run("config", "commit.gpgsign", "false");
await Run("config", "core.autocrlf", "false");
await Run("config", "core.hooksPath", Path.Combine(work, ".git", "hooks").Replace('\\', '/'));
Directory.CreateDirectory(Path.Combine(work, ".git", "hooks"));
await File.WriteAllTextAsync(Path.Combine(work, ".git", "hooks", "pre-commit"), "#!/bin/sh\nprintf 'hook ok' > hook-result.txt\n");
var file = Path.Combine(work, "中文 file.txt");
await File.WriteAllTextAsync(file, "first\r\n");
await Execute(new(GitAction.Stage) { Paths = ["中文 file.txt"] });
await Execute(new(GitAction.Commit) { Message = "first" });
Check(await File.ReadAllTextAsync(Path.Combine(work, "hook-result.txt")) == "hook ok", "Bundled shell hook failed");
var original = await Run("rev-parse", "HEAD");
await File.WriteAllTextAsync(file, "second\r\n");
await Execute(new(GitAction.Stage) { Paths = ["中文 file.txt"] });
var amended = await Execute(new(GitAction.Commit) { Message = "second", Amend = true });
await Execute(new(GitAction.CreateBranch) { Name = "after" });
var restored = await Execute(new(GitAction.RestoreSnapshot) { SnapshotId = amended.Snapshot!.Id });
Check(original == await Run("rev-parse", "HEAD"), "Snapshot HEAD mismatch");
Check(await File.ReadAllTextAsync(file) == "second\r\n", "Snapshot raw bytes mismatch");
Check((await Run("show", ":中文 file.txt")).Contains("second"), "Snapshot index mismatch");
Check(string.IsNullOrWhiteSpace(await Run("for-each-ref", "refs/heads/after")), "Snapshot refs mismatch");
var redo = await Execute(new(GitAction.RestoreSnapshot) { SnapshotId = restored.Snapshot!.Id });
Check((await Run("rev-parse", "HEAD")) != original, "Redo did not restore amended HEAD");
await Execute(new(GitAction.RestoreSnapshot) { SnapshotId = redo.Snapshot!.Id });
var history = await new LibHistoryReader().GetLogAsync(work, 10, default);
Check(history.Count == 1 && history[0].Hash == original.Trim(), "Packaged native libgit2 failed");
var bare = Path.Combine(root, "remote.git"); Directory.CreateDirectory(bare);
var init = await runner.RunAsync(bare, ["init", "--bare", "--initial-branch=main"], default, new() { IsReadOnly = false, Trusted = true });
Check(init.ExitCode == 0, init.Error);
await Run("remote", "add", "origin", bare);
await Execute(new(GitAction.Push) { Remote = "origin" });
var clone = Path.Combine(root, "clone 中文"); Directory.CreateDirectory(clone);
await storage.TrustAsync(clone, default);
var cloned = await service.ExecuteAsync(clone, new(GitAction.Clone) { Url = bare }, null, default);
Check(cloned.Success, cloned.Message);
var head = await runner.ReadAsync(clone, ["rev-parse", "HEAD"], default);
Check(Encoding.UTF8.GetString(head.Output) == original, "Clone HEAD mismatch");
Check(commands.All(c => c.Command.Contains(git.Executable, StringComparison.OrdinalIgnoreCase)), "Command used external Git");
Check(commands.Any(c => c.Command.Contains("update-ref --stdin", StringComparison.Ordinal)), "Ref transaction not exercised");
Console.WriteLine(JsonSerializer.Serialize(new { result = "PASS", runtime = RuntimeEnvironment.GetRuntimeDirectory(),
    git = git.Executable, gitVersion = git.Version, commands = commands.Count,
    checks = "SQLite/native libgit2/init/stage/commit/hooks/amend/branch/snapshot undo+redo/push/clone" }));

async Task<string> Run(params string[] arguments)
{
    var result = await runner.RunAsync(work, arguments, default, new() { IsReadOnly = false, Trusted = true });
    Check(result.ExitCode == 0, result.Error);
    return Encoding.UTF8.GetString(result.Output);
}
async Task<OperationOutcome> Execute(GitOperation operation)
{
    var result = await service.ExecuteAsync(work, operation, null, default);
    Check(result.Success, result.Message); return result;
}
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
sealed class AcceptInTest : IWorkspaceInteraction
{
    public Task<ConfirmationAnswer> ConfirmAsync(Confirmation c, CancellationToken ct) => Task.FromResult(new ConfirmationAnswer(true, c.RequiredText));
    public Task<string?> CredentialAsync(string prompt, CancellationToken ct) => Task.FromResult<string?>(null);
}
