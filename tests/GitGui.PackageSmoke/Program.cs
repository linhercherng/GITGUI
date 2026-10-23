using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GitGui.Core;
using GitGui.Engine.Cli;
using GitGui.Engine.Lib;
using GitGui.Infrastructure;

// Test-only apphost overlaid onto an extracted ZIP; never included in the release.
// The driver verifies the shared DLL hashes before copying only these four harness files.
Check(args.Length is 1 or 3, "Expected test directory, optionally legacy database and repository");
var root = Path.GetFullPath(args[0]);
Directory.CreateDirectory(root);
var package = AppContext.BaseDirectory;
Check(RuntimeEnvironment.GetRuntimeDirectory().StartsWith(package, StringComparison.OrdinalIgnoreCase), "Runtime outside package");
var runtime = new GitRuntime();
var git = await runtime.CheckAsync(null, default);
Check(git.Executable == Path.Combine(package, "PortableGit", "cmd", "git.exe"), "Git outside package");
var storage = new WorkspaceStorage(Path.Combine(root, "settings", "test.db"));
var legacyCatalogCount = 0;
WorkspaceStorage? legacyStore = null;
SnapshotInfo? legacySnapshot = null;
string? legacyRepository = null;
string? legacyBackup = null;
string? legacyRecovery = null;
string? legacyPreservedFixture = null;
byte[]? legacySourceDatabaseHash = null;
var legacyRestoreChecked = false;
if (args.Length == 3)
{
    // Snapshot objects bind their original root. Only an owned, disposable old smoke
    // fixture is accepted; its DB is copied and its complete repository is backed up.
    legacyRepository = ValidateLegacyFixture(args[2], args[1]);
    legacySourceDatabaseHash = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(args[1]));
    var legacy = Path.Combine(root, "legacy-m3-copy.db"); File.Copy(args[1], legacy, overwrite: false);
    var beforeHash = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(legacy));
    var oldStore = new WorkspaceStorage(legacy);
    Check(await oldStore.IsTrustedAsync(args[2], default), "M3 trust did not survive runtime upgrade");
    var oldSnapshots = await oldStore.ListAsync(args[2], default); legacyCatalogCount = oldSnapshots.Count;
    Check(legacyCatalogCount > 0 && oldSnapshots.All(s => s.StateRef.StartsWith("refs/gitgui/snapshots/", StringComparison.Ordinal)), "M3 snapshot catalog did not survive upgrade");
    Check(beforeHash.SequenceEqual(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(legacy))), "Read-only upgrade modified M3 database bytes");
    legacyBackup = Path.Combine(root, "legacy-m3-repository-backup");
    CopyLegacyDirectory(legacyRepository, legacyBackup);
    // A prepared sibling permits same-volume rename recovery without deleting anything.
    var parent = Path.GetDirectoryName(legacyRepository)!;
    legacyRecovery = Path.Combine(parent, "legacy-m3-recovery-" + Guid.NewGuid().ToString("N"));
    legacyPreservedFixture = Path.Combine(parent, "legacy-m3-tested-" + Guid.NewGuid().ToString("N"));
    CopyLegacyDirectory(legacyBackup, legacyRecovery);
    legacyStore = oldStore; legacySnapshot = oldSnapshots.First();
}
var runner = new GitProcessRunner(runtime);
var commands = new System.Collections.Concurrent.ConcurrentQueue<CommandEntry>();
runner.CommandExecuted += (_, e) => commands.Enqueue(e);
if (legacyStore is not null && legacySnapshot is not null && legacyRepository is { } legacyRoot &&
    legacyBackup is { } backup && legacyRecovery is { } recovery && legacyPreservedFixture is { } preserved)
{
    // This option is used only with the previous release's isolated smoke repository.
    async Task<byte[]> LegacyRead(params string[] arguments)
    {
        var result = await runner.RunAsync(legacyRoot, arguments, default, new() { Trusted = true });
        Check(result.ExitCode == 0, result.Error); return result.Output;
    }
    var resolvedRoot = Encoding.UTF8.GetString(await LegacyRead("rev-parse", "--show-toplevel")).Trim();
    Check(Path.GetFullPath(resolvedRoot).Equals(legacyRoot, StringComparison.OrdinalIgnoreCase), "Legacy Git worktree escapes the owned smoke fixture");
    var beforeHead = await LegacyRead("rev-parse", "HEAD");
    var beforeRefs = await LegacyRead("for-each-ref", "refs/heads", "refs/tags", "refs/remotes");
    var beforeStatus = await LegacyRead("status", "--porcelain=v2", "-z", "--untracked-files=all");
    var beforeIndex = await LegacyRead("ls-files", "--stage", "-z");
    var beforeFlags = await LegacyRead("ls-files", "-v", "-z");
    var beforeFiles = await WorktreeFingerprint(legacyRoot);
    var backupFingerprint = await WorktreeFingerprint(backup, includeGit: true);
    var upgradeService = new WorkspaceService(runner, legacyStore, legacyStore, new(runner, legacyStore), new AcceptInTest());
    try
    {
        var oldUndo = await upgradeService.ExecuteAsync(legacyRoot, new(GitAction.RestoreSnapshot) { SnapshotId = legacySnapshot.Id }, null, default);
        Check(oldUndo.Success && oldUndo.Snapshot is not null, "M4 could not restore an actual M3 snapshot: " + oldUndo.Message);
        var oldRedo = await upgradeService.ExecuteAsync(legacyRoot, new(GitAction.RestoreSnapshot) { SnapshotId = oldUndo.Snapshot!.Id }, null, default);
        Check(oldRedo.Success, "M3 snapshot redo failed: " + oldRedo.Message);
        Check(beforeHead.SequenceEqual(await LegacyRead("rev-parse", "HEAD")) &&
            beforeRefs.SequenceEqual(await LegacyRead("for-each-ref", "refs/heads", "refs/tags", "refs/remotes")) &&
            beforeStatus.SequenceEqual(await LegacyRead("status", "--porcelain=v2", "-z", "--untracked-files=all")) &&
            beforeIndex.SequenceEqual(await LegacyRead("ls-files", "--stage", "-z")) && beforeFlags.SequenceEqual(await LegacyRead("ls-files", "-v", "-z")) &&
            beforeFiles.SequenceEqual(await WorktreeFingerprint(legacyRoot)), "Legacy snapshot undo/redo did not preserve file bytes, index or repository state");
        legacyRestoreChecked = true;
    }
    finally
    {
        // Validate every move remains within the explicitly accepted old fixture parent.
        Check(ValidateLegacyFixture(legacyRoot, args[1], requireStorage: false) == legacyRoot &&
            Path.GetDirectoryName(recovery) == Path.GetDirectoryName(legacyRoot) &&
            Path.GetDirectoryName(preserved) == Path.GetDirectoryName(legacyRoot), "Unsafe legacy recovery paths");
        if (Directory.Exists(legacyRoot)) Directory.Move(legacyRoot, preserved); // Keep test data for inspection.
        else if (File.Exists(legacyRoot)) File.Move(legacyRoot, preserved);
        try { Directory.Move(recovery, legacyRoot); }
        catch (IOException error) { throw new IOException("Legacy recovery rename failed. Full backup: " + backup + "; prepared recovery: " + recovery + "; preserved test: " + preserved, error); }
        Check(backupFingerprint.SequenceEqual(await WorktreeFingerprint(legacyRoot, includeGit: true)), "Legacy repository backup recovery differs");
        Check(legacySourceDatabaseHash!.SequenceEqual(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(args[1]))), "Legacy test modified the original database");
    }
}
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
// M3 uses these exact shipping DLLs, the selected PortableGit, LFS and SSH signer.
var tools = new RepositoryToolsService(runner, service, new(runner, storage), new AcceptInTest());
var linked = Path.Combine(root, "工作樹 M3");
await Tool(new(RepositoryToolAction.WorktreeAdd) { Path = linked, Name = "package-worktree" });
Check((await tools.GetStateAsync(work, default)).Worktrees.Any(w => w.Path == linked), "Packaged worktree listing failed");
await Tool(new(RepositoryToolAction.WorktreeLock) { Path = linked, Reason = "package test" });
await Tool(new(RepositoryToolAction.WorktreeUnlock) { Path = linked });
await Tool(new(RepositoryToolAction.WorktreeRemove) { Path = linked });
Check(!Directory.Exists(linked), "Packaged worktree remove failed");
await Run("commit", "--allow-empty", "--no-verify", "-m", "M3 bisect middle");
await Run("commit", "--allow-empty", "--no-verify", "-m", "M3 bisect bad");
var bisectHead = await Run("rev-parse", "HEAD");
await Tool(new(RepositoryToolAction.BisectStart) { Revision = "HEAD", GoodRevision = "HEAD~2" });
Check((await tools.GetStateAsync(work, default)).BisectActive, "Packaged bisect did not start");
await Tool(new(RepositoryToolAction.BisectGood));
await Tool(new(RepositoryToolAction.BisectReset));
Check(await Run("rev-parse", "HEAD") == bisectHead, "Packaged bisect reset failed");
Directory.CreateDirectory(Path.Combine(work, "保留")); Directory.CreateDirectory(Path.Combine(work, "排除"));
await File.WriteAllTextAsync(Path.Combine(work, "保留", "a.txt"), "kept");
await File.WriteAllTextAsync(Path.Combine(work, "排除", "b.txt"), "excluded");
await Execute(new(GitAction.Stage) { Paths = ["保留/a.txt", "排除/b.txt"] });
await Execute(new(GitAction.Commit) { Message = "M3 sparse fixture", SkipHooks = true });
var sparse = await Tool(new(RepositoryToolAction.SparseSet) { Patterns = ["保留"], Cone = true });
Check(!File.Exists(Path.Combine(work, "排除", "b.txt")), "Packaged sparse checkout failed");
await Execute(new(GitAction.RestoreSnapshot) { SnapshotId = sparse.Snapshot!.Id });
Check(File.Exists(Path.Combine(work, "排除", "b.txt")), "Packaged sparse undo failed");
var sub = await Tool(new(RepositoryToolAction.SubmoduleAdd) { Path = "模組", Url = bare });
await File.WriteAllTextAsync(Path.Combine(work, "模組", "child-private.txt"), "child unsaved data");
await Execute(new(GitAction.RestoreSnapshot) { SnapshotId = sub.Snapshot!.Id });
Check(await File.ReadAllTextAsync(Path.Combine(work, "模組", "child-private.txt")) == "child unsaved data", "Parent undo touched child data");
await Tool(new(RepositoryToolAction.LfsInstall));
await Tool(new(RepositoryToolAction.LfsTrack) { Patterns = ["*.m3-lfs"] });
Check((await tools.GetStateAsync(work, default)).LfsVersion.StartsWith("git-lfs/", StringComparison.Ordinal), "Bundled Git LFS unavailable");
var large = Enumerable.Range(0, 100_000).Select(i => (byte)(i % 251)).ToArray();
await File.WriteAllBytesAsync(Path.Combine(work, "payload.m3-lfs"), large);
await Execute(new(GitAction.Stage) { Paths = [".gitattributes", "payload.m3-lfs"] });
await Execute(new(GitAction.Commit) { Message = "Packaged real LFS payload", SkipHooks = true });
await Execute(new(GitAction.Push) { Remote = "origin" });
var lfsClone = Path.Combine(root, "LFS clone 中文"); Directory.CreateDirectory(lfsClone);
var cloneResult = await runner.RunAsync(lfsClone, ["clone", "--no-checkout", "--", bare, "."], default, new() { IsReadOnly = false, Trusted = true });
Check(cloneResult.ExitCode == 0, cloneResult.Error); await storage.TrustAsync(lfsClone, default);
var install = await tools.ExecuteAsync(lfsClone, new(RepositoryToolAction.LfsInstall), null, default); Check(install.Success, install.Message);
var pointerCheckout = await runner.RunAsync(lfsClone, ["reset", "--hard", "HEAD"], default, new()
{ IsReadOnly = false, Trusted = true, Environment = new Dictionary<string, string?> { ["GIT_LFS_SKIP_SMUDGE"] = "1" } });
Check(pointerCheckout.ExitCode == 0, pointerCheckout.Error);
Check((await File.ReadAllTextAsync(Path.Combine(lfsClone, "payload.m3-lfs"))).StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal), "LFS pointer fixture failed");
foreach (var action in new[] { RepositoryToolAction.LfsFetch, RepositoryToolAction.LfsPull })
{ var result = await tools.ExecuteAsync(lfsClone, new(action) { Name = "origin" }, null, default); Check(result.Success, result.Message); }
Check(large.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(lfsClone, "payload.m3-lfs"))), "Packaged LFS object transfer/checkout failed");
await Tool(new(RepositoryToolAction.LfsUntrack) { Patterns = ["*.m3-lfs"] });
var signingKey = Path.Combine(work, ".git", "package-signing-key");
var keygen = new System.Diagnostics.ProcessStartInfo(Path.Combine(package, "PortableGit", "usr", "bin", "ssh-keygen.exe"))
{ UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
foreach (var argument in new[] { "-q", "-t", "ed25519", "-N", "", "-f", signingKey }) keygen.ArgumentList.Add(argument);
using (var process = System.Diagnostics.Process.Start(keygen)!)
{ var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Check(process.ExitCode == 0, await error); }
var signers = Path.Combine(work, ".git", "allowed-signers");
await File.WriteAllTextAsync(signers, "package@example.invalid " + await File.ReadAllTextAsync(signingKey + ".pub"));
await Run("config", "gpg.format", "ssh"); await Run("config", "user.signingkey", signingKey);
await Run("config", "gpg.ssh.allowedSignersFile", signers);
await Execute(new(GitAction.CreateTag) { Name = "package-signed-m3", Sign = true, Message = "Portable SSH signing" });
await Run("verify-tag", "package-signed-m3");
// M4: new preferences and runtime reopen the prior schema without replacing data.
await storage.SaveGitPathAsync(git.Executable, default);
var upgrade = new WorkspaceStorage(Path.Combine(root, "settings", "test.db"));
Check(await upgrade.IsTrustedAsync(work, default), "Upgrade lost repository trust");
Check(await upgrade.LoadGitPathAsync(default) == git.Executable, "Upgrade lost Git selection");
Check((await upgrade.ListAsync(work, default)).Count > 0, "Upgrade lost snapshot catalog");
var preferences = new UiPreferencesStore(Path.Combine(root, "settings", "ui.json"));
Check(preferences.Load().Culture == "zh-TW", "Existing settings without UI preferences fail to load");
preferences.Save(new("en", new Dictionary<string, string?> { ["Main.Refresh"] = "Ctrl+R", ["Main.Commit"] = null }));
var reopenedPreferences = new UiPreferencesStore(Path.Combine(root, "settings", "ui.json")).Load();
Check(reopenedPreferences.Culture == "en" && reopenedPreferences.Shortcuts!["Main.Refresh"] == "Ctrl+R" && reopenedPreferences.Shortcuts["Main.Commit"] is null,
    "M4 UI preferences did not survive reopening");
await Run("config", "i18n.commitEncoding", "Big5");
await Execute(new(GitAction.Commit) { Amend = true, Message = "免安裝中文編碼測試", SkipHooks = true });
var encodedCommit = await runner.ReadAsync(work, ["cat-file", "commit", "HEAD"], default);
Check(Encoding.ASCII.GetString(encodedCommit.Output).Contains("encoding Big5\n", StringComparison.Ordinal), "Packaged Big5 commit header missing");
Check(CommitMessageDecoder.DecodeObject(encodedCommit.Output).Message.TrimEnd('\r', '\n') == "免安裝中文編碼測試", "Packaged Big5 message bytes were not preserved");
var beforeRejected = await Run("rev-parse", "HEAD");
var beforeRejectedSnapshots = (await storage.ListAsync(work, default)).Select(s => s.Id).Order().ToArray();
var rejected = await service.ExecuteAsync(work, new(GitAction.Commit) { Amend = true, Message = "unsupported emoji 😀", SkipHooks = true }, null, default);
Check(!rejected.Success && rejected.Snapshot is null && await Run("rev-parse", "HEAD") == beforeRejected &&
    beforeRejectedSnapshots.SequenceEqual((await storage.ListAsync(work, default)).Select(s => s.Id).Order()), "Unrepresentable message modified repository or snapshot catalog");
await Run("config", "--unset", "i18n.commitEncoding");
var sanitized = GitLogSanitizer.Arguments(["commit", "-mprivate-message", "--token", "private-token"]);
Check(!string.Join(' ', sanitized).Contains("private", StringComparison.Ordinal), "Packaged audit redaction failed");
Check(commands.All(c => c.Command.Contains(git.Executable, StringComparison.OrdinalIgnoreCase)), "Command used external Git");
Check(commands.Any(c => c.Command.Contains("update-ref --stdin", StringComparison.Ordinal)), "Ref transaction not exercised");
Console.WriteLine(JsonSerializer.Serialize(new { result = "PASS", runtime = RuntimeEnvironment.GetRuntimeDirectory(),
    git = git.Executable, gitVersion = git.Version, commands = commands.Count, legacyCatalogCount, legacyRestoreChecked, legacyPreservedFixture,
    checks = "SQLite/native libgit2/init/stage/commit/hooks/amend/branch/snapshot undo+redo/push/clone/M2 stash recovery/tag/remote/rebase reword/blame/reflog/terminal/M3 worktree/bisect/sparse undo/submodule parent undo preserves child/LFS real local-remote transfer+checkout/bundled SSH signed tag verification/M4 settings+snapshot catalog reopen/locale+shortcut persistence/Big5 commit/unrepresentable message no-write/audit redaction" }));

async Task<OperationOutcome> Tool(RepositoryToolOperation operation)
{
    var result = await tools.ExecuteAsync(work, operation, null, default);
    Check(result.Success, result.Message); return result;
}

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
static void CopyLegacyDirectory(string source, string destination)
{
    Check((File.GetAttributes(source) & FileAttributes.ReparsePoint) == 0, "Legacy fixture contains a linked directory");
    Check(!Directory.Exists(destination) && !File.Exists(destination), "Legacy copy destination already exists");
    Directory.CreateDirectory(destination);
    foreach (var entry in Directory.EnumerateFileSystemEntries(source))
    {
        var attributes = File.GetAttributes(entry);
        Check((attributes & FileAttributes.ReparsePoint) == 0, "Legacy fixture contains a linked path");
        var target = Path.Combine(destination, Path.GetFileName(entry));
        if ((attributes & FileAttributes.Directory) != 0) CopyLegacyDirectory(entry, target);
        else File.Copy(entry, target, overwrite: false);
    }
}
static string ValidateLegacyFixture(string repository, string database, bool requireStorage = true)
{
    var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository));
    var folder = new DirectoryInfo(full);
    var isolated = folder.Parent ?? throw new InvalidOperationException("Legacy fixture has no parent");
    var extraction = isolated.Parent ?? throw new InvalidOperationException("Legacy fixture has no extraction directory");
    const string prefix = "GITGUI 解壓測試 ";
    Check(OperatingSystem.IsWindows() && folder.Name == "倉庫 with spaces" && isolated.Name == "isolated repositories" &&
        extraction.Name.StartsWith(prefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(extraction.Name[prefix.Length..], "N", out _) &&
        extraction.Parent?.FullName.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) == true,
        "Legacy restore accepts only an owned prior portable-smoke TEMP fixture");
    Check(Path.GetFullPath(database).Equals(Path.Combine(isolated.FullName, "settings", "test.db"), StringComparison.OrdinalIgnoreCase), "Legacy database must be the fixture's sibling settings/test.db");
    Check((!folder.Exists || (folder.Attributes & FileAttributes.ReparsePoint) == 0) && (isolated.Attributes & FileAttributes.ReparsePoint) == 0 &&
        (extraction.Attributes & FileAttributes.ReparsePoint) == 0 && (!requireStorage || Directory.Exists(Path.Combine(full, ".git"))), "Legacy fixture must contain standalone, unlinked Git storage");
    return full;
}
static async Task<byte[]> WorktreeFingerprint(string repository, bool includeGit = false)
{
    using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
    async Task Visit(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            if (!includeGit && directory == repository && Path.GetFileName(entry).Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
            var attributes = File.GetAttributes(entry);
            Check((attributes & FileAttributes.ReparsePoint) == 0, "Legacy fingerprint cannot follow linked paths");
            if ((attributes & FileAttributes.Directory) != 0) await Visit(entry);
            else
            {
                hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(repository, entry).Replace('\\', '/'))); hash.AppendData([0]);
                await using var input = File.OpenRead(entry);
                hash.AppendData(await System.Security.Cryptography.SHA256.HashDataAsync(input));
            }
        }
    }
    await Visit(repository); return hash.GetHashAndReset();
}
sealed class AcceptInTest : IWorkspaceInteraction
{
    public Task<ConfirmationAnswer> ConfirmAsync(Confirmation c, CancellationToken ct) => Task.FromResult(new ConfirmationAnswer(true, c.RequiredText));
    public Task<string?> CredentialAsync(string prompt, CancellationToken ct) => Task.FromResult<string?>(null);
}
