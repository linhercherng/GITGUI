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
var service = new WorkspaceService(runner, storage, storage, new(runner, storage), new AcceptInTest(), fileHistory: new LibHistoryReader());
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
// M2 is exercised against the extracted release DLLs and the bundled shell/Git.
await Execute(new(GitAction.Reset) { Revision = "HEAD", ResetMode = ResetMode.Hard });
await File.WriteAllTextAsync(Path.Combine(work, "暫存 中文.txt"), "untracked");
await Execute(new(GitAction.StashPush) { IncludeUntracked = true, Message = "package stash" });
var stash = (await service.GetAdvancedStateAsync(work, default)).Stashes.First();
Check(!File.Exists(Path.Combine(work, "暫存 中文.txt")), "Stash did not clean Unicode untracked file");
var drop = await Execute(new(GitAction.StashDrop) { StashOid = stash.Oid, Name = stash.Selector });
await Execute(new(GitAction.RestoreSnapshot) { SnapshotId = drop.Snapshot!.Id });
Check((await service.GetAdvancedStateAsync(work, default)).Stashes.First().Oid == stash.Oid, "Stash recovery failed");
await Execute(new(GitAction.StashPop) { StashOid = stash.Oid, Name = stash.Selector });
await Execute(new(GitAction.CreateTag) { Name = "package-m2", Annotated = true, Message = "M2 test" });
await Execute(new(GitAction.AddRemote) { Name = "backup", Url = bare });
await Execute(new(GitAction.RenameRemote) { Name = "backup", NewName = "backup-renamed" });
await Execute(new(GitAction.RemoveRemote) { Name = "backup-renamed" });
await Execute(new(GitAction.Stage) { Paths = ["暫存 中文.txt", "hook-result.txt"] });
await Execute(new(GitAction.Commit) { Message = "M2 second", SkipHooks = true });
var plan = await service.GetRebasePlanAsync(work, "HEAD~1", default);
await Execute(new(GitAction.RebaseInteractive) { Revision = "HEAD~1", RebasePlan = [plan[0] with { Action = RebaseStepKind.Reword, Message = "M2 中文 reword" }] });
Check((await Run("log", "-1", "--format=%s")).Trim() == "M2 中文 reword", "Packaged sequence/editor helper failed");
Check((await service.GetFileHistoryAsync(work, "中文 file.txt", default)).Blame.Count > 0, "Packaged M2 blame failed");
Check((await service.GetAdvancedStateAsync(work, default)).Reflog.Count > 0, "Packaged reflog failed");
var terminal = new TerminalService(runtime, storage, service.TrustAsync, service.AcquireTerminalLeaseAsync, runner);
await using (var session = await terminal.StartAsync(work, default))
{
    await session.SendLineAsync("git --version", default);
    await session.SendLineAsync("echo M2_TERMINAL_OK", default);
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while ((!session.Output.Contains("\nM2_TERMINAL_OK\n", StringComparison.Ordinal) || !session.Output.Contains(git.Version, StringComparison.Ordinal)) && DateTime.UtcNow < deadline) await Task.Delay(50);
    Check(session.Output.Contains("\nM2_TERMINAL_OK\n", StringComparison.Ordinal) && session.Output.Contains(git.Version, StringComparison.Ordinal), "Packaged persistent terminal/Git failed");
    await session.StopAsync();
}
Check(commands.All(c => c.Command.Contains(git.Executable, StringComparison.OrdinalIgnoreCase)), "Command used external Git");
Check(commands.Any(c => c.Command.Contains("update-ref --stdin", StringComparison.Ordinal)), "Ref transaction not exercised");
Console.WriteLine(JsonSerializer.Serialize(new { result = "PASS", runtime = RuntimeEnvironment.GetRuntimeDirectory(),
    git = git.Executable, gitVersion = git.Version, commands = commands.Count,
    checks = "SQLite/native libgit2/init/stage/commit/hooks/amend/branch/snapshot undo+redo/push/clone/M2 stash recovery/tag/remote/rebase reword/blame/reflog/terminal" }));

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
