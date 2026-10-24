using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using GitGui.Core;

namespace GitGui.Engine.Cli;

public sealed partial class WorkspaceService : IProgressiveWorkspaceService, IAdvancedWorkspaceService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Queues = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly GitProcessRunner runner;
    private readonly IRepositorySettings settings;
    private readonly ISnapshotCatalog catalog;
    private readonly SnapshotManager snapshots;
    private readonly IWorkspaceInteraction interaction;
    private readonly GitAccess git;
    private readonly WorkspaceReader reader;
    private readonly IFileHistoryReader? fileHistory;
    public event EventHandler<CommandEntry>? CommandExecuted;

    public WorkspaceService(GitProcessRunner runner, IRepositorySettings settings, ISnapshotCatalog catalog,
        SnapshotManager snapshots, IWorkspaceInteraction interaction, IGraphHistoryReader? history = null, IFileHistoryReader? fileHistory = null)
    {
        this.runner = runner; this.settings = settings; this.catalog = catalog;
        this.snapshots = snapshots; this.interaction = interaction;
        git = new(runner); reader = new(runner, settings, history);
        this.fileHistory = fileHistory;
        runner.CommandExecuted += (_, entry) => CommandExecuted?.Invoke(this, entry);
    }

    public Task<WorkspaceState> OpenAsync(string path, LogFilter filter, CancellationToken ct)
        => Task.Run(() => reader.Open(path, filter, ct), ct);
    public Task<WorkspaceState> OpenWithHistoryAsync(string path, LogFilter filter,
        Func<WorkspaceHistoryPreview, Task> publishHistory, CancellationToken ct)
        => Task.Run(() => reader.Open(path, filter, ct, publishHistory: publishHistory), ct);
    public Task<WorkspaceStatus> RefreshStatusAsync(string root, CancellationToken ct)
        => Task.Run(() => reader.Status(root, ct), ct);
    public Task<HistoryPage> GetHistoryPageAsync(string root, LogFilter filter, string? generation, CancellationToken ct)
        => Task.Run(() => reader.Page(root, filter, generation, ct), ct);
    public Task<string> GetDiffAsync(string root, DiffRequest request, CancellationToken ct)
        => Task.Run(() => reader.Diff(root, request, ct), ct);
    public Task<CommitDetails> GetCommitAsync(string root, string commit, CancellationToken ct)
        => Task.Run(() => reader.Commit(root, commit, ct), ct);
    public Task<ConflictContent> GetConflictAsync(string root, string path, CancellationToken ct)
        => Task.Run(() => reader.Conflict(root, path, ct), ct);
    public Task<FilePreview> GetPreviewAsync(string root, DiffRequest request, CancellationToken ct)
        => Task.Run(() => reader.Preview(root, request, ct), ct);
    public Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsAsync(string root, CancellationToken ct)
        // SQLite async methods can wait synchronously for a database lock.
        => Task.Run(() => catalog.ListAsync(root, ct), ct);

    public async Task TrustAsync(string root, CancellationToken ct)
    {
        root = await git.Root(root, ct);
        if (await settings.IsTrustedAsync(root, ct)) return;
        var configured = await reader.RiskySettings(root, ct);
        var answer = await interaction.ConfirmAsync(new("信任倉庫 / Trust repository",
            root + "\n\nGit hooks、filters、SSH 與 credential helpers 可能執行外部程式。只信任你了解來源的倉庫。\n" +
            string.Join("\n", configured), RiskLevel.High), ct);
        if (!answer.Accepted) throw new OperationCanceledException("Repository trust was not granted.", ct);
        await settings.TrustAsync(root, ct);
    }

    public Task<OperationOutcome> ExecuteAsync(string root, GitOperation operation,
        IProgress<OperationProgress>? progress, CancellationToken ct)
        => Task.Run(() => Execute(root, operation, progress, ct), ct);

    private async Task<OperationOutcome> Execute(string path, GitOperation op, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        SnapshotInfo? snapshot = null;
        string root = Path.GetFullPath(path);
        SemaphoreSlim? queue = null;
        var entered = false;
        try
        {
            var creates = op.Action is GitAction.Init or GitAction.Clone;
            if (!creates) root = await git.Root(root, ct);
            var key = creates ? root : await git.Text(root, ct, "rev-parse", "--path-format=absolute", "--git-common-dir");
            queue = Queues.GetOrAdd(key, _ => new(1, 1));
            progress?.Report(new("等待倉庫操作佇列 / Waiting for repository queue"));
            await queue.WaitAsync(ct); entered = true;
            if (creates) return await CreateRepository(root, op, progress, ct);
            await TrustAsync(root, ct);
            var state = await reader.Open(root, new(Take: 1), ct, presentation: false);
            await Preflight(root, state, op, ct);
            var messageBytes = await PrepareMessageBytes(root, op, ct);
            var overwritePaths = await OperationOverwritePaths(root, state.InProgress, op, ct);
            var risk = Risk(op);
            if (risk is RiskLevel.High or RiskLevel.RemoteIrreversible)
            {
                var protectedName = risk == RiskLevel.RemoteIrreversible &&
                    (state.Status.Branch is "main" or "master" || state.Status.Branch.StartsWith("release/", StringComparison.Ordinal))
                    ? state.Status.Branch : null;
                var description = op.Action == GitAction.RestoreSnapshot
                    ? "還原會替換本地分支、標籤、暫存區與工作目錄。還原前會另建快照，可再重做。"
                    : risk == RiskLevel.RemoteIrreversible
                        ? "遠端變更無法以本地快照復原。強制推送只使用 --force-with-lease --force-if-includes。"
                        : "以下內容將被替換或刪除；執行前會保存快照。\n" + string.Join("\n", op.Paths) + "\n" + op.Name;
                if (op.Action == GitAction.DeleteBranch && op.Force)
                {
                    var tip = await reader.Resolve(root, op.Name!, ct);
                    description += "\n\n該分支獨有的提交（最多 20 筆） / Branch-only commits (up to 20):\n" +
                        await git.Text(root, ct, "log", "--oneline", "--max-count=20", "HEAD.." + tip, "--");
                }
                if (op.Action == GitAction.Reset && op.ResetMode == ResetMode.Hard)
                    description += "\n\nFiles / 檔案:\n" + string.Join("\n", state.Status.Files.Select(f => f.Path).Concat(overwritePaths).Distinct()) +
                        "\n\nCommits / 提交 (up to 20):\n" + await git.Text(root, ct, "log", "--oneline", "--max-count=20",
                            await reader.Resolve(root, op.Revision ?? op.Name ?? "HEAD", ct) + "..HEAD", "--");
                var answer = await interaction.ConfirmAsync(new(op.Action.ToString(), description, risk, protectedName), ct);
                if (!answer.Accepted || protectedName is not null && answer.Text != protectedName)
                    return new(false, "操作已取消 / Cancelled", Cancelled: true);
            }
            var needsSnapshot = op.Action is not (GitAction.Stage or GitAction.Unstage or GitAction.StagePatch or
                GitAction.UnstagePatch or GitAction.Fetch or GitAction.Push or GitAction.MarkResolved);
            SnapshotInfo? target = null;
            if (op.Action == GitAction.RestoreSnapshot)
            {
                target = (await catalog.ListAsync(root, ct)).SingleOrDefault(s => s.Id == op.SnapshotId)
                    ?? throw new InvalidOperationException("Snapshot was not found for this repository.");
            }
            if (needsSnapshot)
            {
                progress?.Report(new("建立安全快照 / Creating recovery snapshot"));
                var extra = target is null ? overwritePaths : await snapshots.RestorePathsAsync(root, target, ct);
                snapshot = await snapshots.CreateAsync(root, op.Action.ToString(), ct, extra);
            }
            var cancellable = op.Action is GitAction.Fetch;
            ct.ThrowIfCancellationRequested();
            var executionToken = cancellable ? ct : CancellationToken.None;
            progress?.Report(new(op.Action.ToString(), CanCancel: cancellable));
            async Task Run(string[] args, byte[]? input = null) => await RunWrite(root, args, input, executionToken, progress);
            switch (op.Action)
            {
                case GitAction.Stage: case GitAction.MarkResolved:
                    await Run(["add", "--all", "--", .. op.Paths]); break;
                case GitAction.Unstage:
                    if (state.Status.Head == "(initial)") await Run(["rm", "--cached", "-f", "--ignore-unmatch", "--", .. op.Paths]);
                    else await Run(["restore", "--staged", "--source=HEAD", "--", .. op.Paths]);
                    break;
                case GitAction.StagePatch: case GitAction.UnstagePatch:
                    var patch = GitAccess.Utf8.GetBytes(op.Patch!);
                    await Run(["apply", "--cached", "--recount", "--check", "--whitespace=nowarn"], patch);
                    await Run(["apply", "--cached", "--recount", "--whitespace=nowarn"], patch);
                    break;
                case GitAction.Discard:
                    var tracked = GitAccess.NullFields(await git.Bytes(root, executionToken, ["ls-files", "-z", "--", .. op.Paths])).Distinct().ToArray();
                    if (tracked.Length > 0) await Run(["restore", "--worktree", "--", .. tracked]);
                    var untracked = op.Paths.Except(tracked, StringComparer.Ordinal).ToArray();
                    if (untracked.Length > 0) await Run(["clean", "-f", "--", .. untracked]);
                    break;
                case GitAction.Commit:
                    var commitArgs = new List<string> { "commit", "--file=-" };
                    if (op.Amend) commitArgs.Add("--amend");
                    if (op.SkipHooks) commitArgs.Add("--no-verify");
                    if (op.Sign) commitArgs.Add("-S");
                    await Run(commitArgs.ToArray(), messageBytes.Commit); break;
                case GitAction.CreateBranch:
                    await Run(["branch", "--", op.Name!]); break;
                case GitAction.SwitchBranch:
                    if (await git.TryText(root, executionToken, "rev-parse", "--verify", "--quiet", "refs/heads/" + op.Name) is not null)
                        await Run(["switch", "--no-guess", "--no-overwrite-ignore", "--", op.Name!]);
                    else if (await git.TryText(root, executionToken, "rev-parse", "--verify", "--quiet", "refs/remotes/" + op.Name) is not null)
                        await Run(["switch", "--track", "--no-overwrite-ignore", "--", op.Name!]);
                    else await Run(["switch", "--detach", "--no-overwrite-ignore", await reader.Resolve(root, op.Name!, executionToken)]);
                    break;
                case GitAction.RenameBranch:
                    await Run(["branch", "-m", "--", op.Name!, op.NewName!]); break;
                case GitAction.DeleteBranch:
                    await Run(["branch", op.Force ? "-D" : "-d", "--", op.Name!]); break;
                case GitAction.Fetch:
                    await Run(["fetch", "--progress", "--prune", "--", Remote(state, op)]); break;
                case GitAction.Pull:
                    var remote = Remote(state, op);
                    var branch = state.Status.Branch;
                    var localRef = state.Refs.FirstOrDefault(r => r.IsCurrent);
                    var mergeRef = await git.TryText(root, executionToken, "config", "--get", $"branch.{branch}.merge");
                    var remoteBranch = mergeRef?.StartsWith("refs/heads/", StringComparison.Ordinal) == true ? mergeRef[11..] : branch;
                    if (op.Remote is null && localRef?.Upstream is not null)
                        remote = await git.TryText(root, executionToken, "config", "--get", $"branch.{branch}.remote") ?? remote;
                    var privateRef = "refs/gitgui/fetch/" + Guid.NewGuid().ToString("N");
                    try
                    {
                        await Run(["fetch", "--atomic", "--progress", "--no-write-fetch-head", "--", remote,
                            $"refs/heads/{remoteBranch}:{privateRef}", $"+refs/heads/{remoteBranch}:refs/remotes/{remote}/{remoteBranch}"]);
                        var mergeArgs = new List<string> { "merge", "--no-edit", "--no-overwrite-ignore", "--no-autostash" };
                        var ff = await git.TryText(root, executionToken, "config", "--get", "pull.ff");
                        if (ff == "only") mergeArgs.Add("--ff-only");
                        else if (ff == "false") mergeArgs.Add("--no-ff");
                        else if (ff == "true") mergeArgs.Add("--ff");
                        var fetchedTarget = await reader.Resolve(root, privateRef, executionToken);
                        await EnsureParentTarget(root, fetchedTarget, executionToken);
                        mergeArgs.Add(fetchedTarget);
                        await Run(mergeArgs.ToArray());
                    }
                    finally
                    {
                        try { await git.Write(root, CancellationToken.None, ["update-ref", "-d", privateRef]); }
                        catch (GitCommandException) { /* Kept as an internal reachability ref if cleanup is blocked. */ }
                    }
                    break;
                case GitAction.Push:
                    var pushArgs = new List<string> { "push", "--progress", "--set-upstream" };
                    if (op.Force) pushArgs.AddRange(["--force-with-lease", "--force-if-includes"]);
                    pushArgs.AddRange(["--", Remote(state, op), "HEAD:refs/heads/" + state.Status.Branch]);
                    await Run(pushArgs.ToArray()); break;
                case GitAction.Merge:
                    await Run(["merge", "--no-edit", "--no-overwrite-ignore", "--no-autostash", "--progress", await reader.Resolve(root, op.Name!, executionToken)]); break;
                case GitAction.AbortMerge: await Run(["merge", "--abort"]); break;
                case GitAction.ResolveOurs: case GitAction.ResolveTheirs:
                    await Run(["checkout", op.Action == GitAction.ResolveOurs ? "--ours" : "--theirs", "--", .. op.Paths]); break;
                case GitAction.ExternalMergeTool: await Run(["mergetool", "--no-prompt", "--", .. op.Paths]); break;
                case GitAction.WriteConflictResult:
                    var content = await reader.Conflict(root, op.Paths[0], executionToken);
                    if (content.IsBinary) throw new InvalidOperationException("Use whole-file ours/theirs for a binary or symbolic-link conflict.");
                    var blob = GitAccess.Utf8.GetString(await git.Bytes(root, executionToken, ["hash-object", "-w", "--stdin"],
                        new() { IsReadOnly = false, HideOutput = true, Input = Encoding.UTF8.GetBytes(op.Message ?? "") })).Trim();
                    var destination = GitAccess.FullPath(root, op.Paths[0]);
                    var unixMode = !OperatingSystem.IsWindows() && File.Exists(destination)
                        ? File.GetUnixFileMode(destination) : (UnixFileMode?)null;
                    var temporary = destination + ".gitgui-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            await git.Bytes(root, executionToken, ["cat-file", "blob", blob], new() { OutputStream = output, HideOutput = true });
                        if (!OperatingSystem.IsWindows() && unixMode is { } mode) File.SetUnixFileMode(temporary, mode);
                        File.Move(temporary, destination, true);
                    }
                    finally { File.Delete(temporary); }
                    break;
                case GitAction.RestoreSnapshot:
                    await snapshots.RestoreAsync(root, target!, executionToken); break;
                default: await ExecuteAdvanced(root, state, op, messageBytes, executionToken, progress); break;
            }
            if (snapshot is not null)
            {
                try { await snapshots.PruneAsync(root, CancellationToken.None); }
                catch (Exception ex) { return new(true, op.Action + " completed; snapshot cleanup postponed: " + GitProcessRunner.Redact(ex.Message), Snapshot: snapshot); }
            }
            return new(true, op.Action + " 完成 / completed" + (snapshot is not null ? " · 可從快照復原" : ""), Snapshot: snapshot);
        }
        catch (OperationCanceledException) { return new(false, "操作已取消。請重新整理檢查倉庫狀態。", Snapshot: snapshot, Cancelled: true); }
        catch (Exception ex)
        {
            var text = ex is GitCommandException gitError
                ? gitError.Result.Error + "\n" + Encoding.UTF8.GetString(gitError.Result.Output) : ex.Message;
            var exit = ex is GitCommandException failure ? failure.Result.ExitCode : -1;
            var unmerged = false;
            if (exit == 1 && op.Action is (GitAction.Merge or GitAction.Pull or GitAction.CherryPick or GitAction.Revert or
                GitAction.StashApply or GitAction.StashPop or GitAction.RebaseInteractive or GitAction.ContinueOperation or GitAction.SkipOperation))
            {
                try { unmerged = (await git.Bytes(root, CancellationToken.None, ["ls-files", "--unmerged", "-z"])).Length > 0; }
                catch (Exception) { /* Keep the original command failure if inspection also fails. */ }
            }
            var kind = GitFailurePolicy.Classify(exit, text, unmerged);
            return new(false, GitProcessRunner.Redact(text.Trim()), kind, snapshot);
        }
        finally { if (entered) queue!.Release(); }
    }

    private async Task Preflight(string root, WorkspaceState state, GitOperation op, CancellationToken ct)
    {
        GitPathPolicy.ValidateSelection(state.Status.Files, op.Paths);
        if (op.Action is GitAction.StagePatch or GitAction.UnstagePatch && state.Status.Files.Any(f => !f.IsPathSupported))
            throw new InvalidOperationException("Partial staging is unavailable while non-UTF-8 paths are present. Use whole-file staging for supported paths, or rename unsupported paths with a byte-aware Git tool.");
        var gitDirectory = await git.Text(root, ct, "rev-parse", "--absolute-git-dir");
        if (File.Exists(Path.Combine(gitDirectory, "index.lock"))) throw new InvalidOperationException("index.lock exists; wait for the other Git process.");
        var sequenceAllowed = op.Action is GitAction.Fetch or GitAction.Stage or GitAction.Unstage or GitAction.StagePatch or GitAction.UnstagePatch or
            GitAction.ResolveOurs or GitAction.ResolveTheirs or GitAction.MarkResolved or GitAction.ExternalMergeTool or GitAction.WriteConflictResult or
            GitAction.ContinueOperation or GitAction.SkipOperation or GitAction.AbortOperation or GitAction.RestoreSnapshot;
        var stateCommit = op.Action == GitAction.Commit && (state.InProgress.Kind == "merge" || state.InProgress.Kind == "rebase" && op.Amend);
        if (state.InProgress.IsActive && !sequenceAllowed && !stateCommit && !(state.InProgress.Kind == "merge" && op.Action == GitAction.AbortMerge))
            throw new InvalidOperationException("Finish the active " + state.InProgress.Kind + " operation first.");
        var conflictAction = op.Action is GitAction.ResolveOurs or GitAction.ResolveTheirs or GitAction.MarkResolved or GitAction.ExternalMergeTool or GitAction.WriteConflictResult;
        if (conflictAction && !state.InProgress.HasConflicts) throw new InvalidOperationException("No unresolved conflicts.");
        if (op.Action == GitAction.AbortMerge && state.InProgress.Kind != "merge") throw new InvalidOperationException("No merge in progress.");
        if (state.InProgress.Kind == "merge" && op.Action is GitAction.SwitchBranch or GitAction.CreateBranch or GitAction.RenameBranch or GitAction.DeleteBranch or GitAction.Merge or GitAction.Pull)
            throw new InvalidOperationException("Complete or abort the merge first.");
        if (op.Action is GitAction.SwitchBranch or GitAction.Merge or GitAction.Pull && await HasParentChanges(root, state.Status.Files, false, ct))
            throw new InvalidOperationException("Commit or discard tracked changes before switching/merging/pulling.");
        if (op.Action is GitAction.Push or GitAction.Pull && state.Status.Branch is "(detached)" or "(unknown)")
            throw new InvalidOperationException("Switch to a local branch before push/pull.");
        if (op.Action is GitAction.Stage or GitAction.Unstage or GitAction.Discard or GitAction.ResolveOurs or GitAction.ResolveTheirs or GitAction.MarkResolved or GitAction.ExternalMergeTool or GitAction.WriteConflictResult)
        {
            if (op.Paths.Count == 0) throw new ArgumentException("Select at least one file.");
            foreach (var path in op.Paths)
            {
                var full = GitAccess.FullPath(root, path);
                if (Directory.Exists(full) && new DirectoryInfo(full).LinkTarget is null)
                {
                    var entries = GitAccess.NullFields(await git.Bytes(root, ct, ["ls-files", "--stage", "-z", "--", path]));
                    var gitlink = entries.Any(e => e.StartsWith("160000 ", StringComparison.Ordinal) && e[(e.IndexOf('\t') + 1)..] == path);
                    if (!gitlink || op.Action is not (GitAction.Stage or GitAction.Unstage or GitAction.MarkResolved))
                        throw new InvalidOperationException("Select individual files or an indexed submodule for staging; child worktrees are managed separately.");
                }
            }
        }
        if (op.Action is GitAction.StagePatch or GitAction.UnstagePatch && string.IsNullOrWhiteSpace(op.Patch))
            throw new ArgumentException("Select a text hunk or line first.");
        if (op.Action == GitAction.WriteConflictResult && op.Paths.Count != 1)
            throw new ArgumentException("Save one conflict result at a time.");
        if (op.Action == GitAction.Discard && state.InProgress.HasConflicts)
            throw new InvalidOperationException("Resolve conflicts with ours/theirs or abort the merge.");
        if (op.Action == GitAction.Commit)
        {
            if (string.IsNullOrWhiteSpace(op.Message)) throw new ArgumentException("Enter a commit message.");
            if (state.InProgress.HasConflicts) throw new InvalidOperationException("Resolve and stage every conflict before committing.");
        }
        if (op.Action is GitAction.CreateBranch or GitAction.RenameBranch or GitAction.DeleteBranch)
        {
            await git.Text(root, ct, "check-ref-format", "--branch", op.Name ?? "");
            if (op.Action == GitAction.RenameBranch)
            {
                await git.Text(root, ct, "check-ref-format", "--branch", op.NewName ?? "");
                if (op.NewName!.StartsWith(op.Name + "/", StringComparison.Ordinal) || op.Name!.StartsWith(op.NewName + "/", StringComparison.Ordinal))
                    throw new InvalidOperationException("Rename across a ref file/directory boundary cannot be restored atomically; choose another branch name.");
            }
        }
        if (op.Action is GitAction.SwitchBranch or GitAction.Merge) ArgumentException.ThrowIfNullOrWhiteSpace(op.Name);
        if (op.Action is GitAction.SwitchBranch or GitAction.Merge)
            await EnsureParentTarget(root, await reader.Resolve(root, op.Name!, ct), ct);
        if (op.Action is GitAction.Fetch or GitAction.Pull or GitAction.Push) _ = Remote(state, op);
        if (op.Action == GitAction.Pull)
        {
            var rebase = await git.TryText(root, ct, "config", "--get", $"branch.{state.Status.Branch}.rebase")
                ?? await git.TryText(root, ct, "config", "--get", "pull.rebase");
            if (rebase is not (null or "false"))
            {
                var answer = await interaction.ConfirmAsync(new("Pull strategy",
                    $"This repository configures pull.rebase={rebase}. Pull uses merge. Continue using merge for this pull only? Use interactive rebase in Advanced for a reviewed rebase plan. The repository setting will be kept; pull.ff restrictions still apply.", RiskLevel.Medium), ct);
                if (!answer.Accepted) throw new OperationCanceledException("Pull strategy was not approved.", ct);
            }
        }
        await PreflightAdvanced(root, state, op, ct);
    }

    private async Task<OperationOutcome> CreateRepository(string destination, GitOperation op, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        if (File.Exists(destination) || new DirectoryInfo(destination).LinkTarget is not null ||
            Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new InvalidOperationException("Choose a new or empty destination directory.");
        var parent = Path.GetDirectoryName(destination) ?? throw new ArgumentException("Invalid destination.");
        Directory.CreateDirectory(parent);
        if (op.Action == GitAction.Init)
        {
            var name = op.Name ?? "main";
            await git.Text(parent, ct, "check-ref-format", "--branch", name);
            await RunWrite(parent, ["init", "--initial-branch=" + name, "--", destination], null, ct, progress);
        }
        else
        {
            ValidateCloneUrl(op.Url);
            var answer = await interaction.ConfirmAsync(new("Clone repository",
                GitProcessRunner.Redact(op.Url!) + "\n" + destination + "\nGit may run your configured credential helpers, filters and hooks.", RiskLevel.Medium), ct);
            if (!answer.Accepted) return new(false, "Clone cancelled.", Cancelled: true);
            progress?.Report(new("Clone", CanCancel: true));
            await RunWrite(parent, ["-c", "protocol.ext.allow=never", "clone", "--progress", "--", op.Url!, destination], null, ct, progress);
        }
        await settings.TrustAsync(destination, ct);
        return new(true, op.Action + " completed: " + destination);
    }

    private async Task RunWrite(string root, string[] args, byte[]? input, CancellationToken ct, IProgress<OperationProgress>? progress, bool literalPathspecs = true)
    {
        args = ["-c", "submodule.recurse=false", .. args];
        var result = await runner.RunAsync(root, args, ct, new() { IsReadOnly = false, Trusted = true, Input = input, LiteralPathspecs = literalPathspecs }, progress);
        if (result.ExitCode != 0) throw new GitCommandException(result);
    }
    private static string Remote(WorkspaceState state, GitOperation operation)
    {
        var name = operation.Remote ?? state.Remotes.FirstOrDefault()?.Name;
        if (name is null || !state.Remotes.Any(r => r.Name == name)) throw new InvalidOperationException("Select a configured remote first.");
        return name;
    }
    private static RiskLevel Risk(GitOperation op) => op.Action switch
    {
        GitAction.Push when op.Force => RiskLevel.RemoteIrreversible,
        GitAction.Discard or GitAction.RestoreSnapshot => RiskLevel.High,
        GitAction.DeleteBranch when op.Force => RiskLevel.High,
        GitAction.Reset when op.ResetMode == ResetMode.Hard => RiskLevel.High,
        GitAction.RebaseInteractive or GitAction.CherryPick or GitAction.Revert or GitAction.Reset or GitAction.StashDrop or
            GitAction.StashApply or GitAction.StashPop or GitAction.ContinueOperation or GitAction.SkipOperation or GitAction.AbortOperation => RiskLevel.Medium,
        GitAction.Commit when op.Amend => RiskLevel.Medium,
        GitAction.Merge or GitAction.Pull or GitAction.SwitchBranch or GitAction.RenameBranch or GitAction.DeleteBranch or
            GitAction.AbortMerge or GitAction.ResolveOurs or GitAction.ResolveTheirs or GitAction.ExternalMergeTool => RiskLevel.Medium,
        GitAction.WriteConflictResult => RiskLevel.Medium,
        _ => RiskLevel.Low
    };
    private static void ValidateCloneUrl(string? url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (url.Any(c => c is '\r' or '\n' or '\0') || url.StartsWith('-') || url.Contains("::", StringComparison.Ordinal))
            throw new ArgumentException("Unsupported clone transport.");
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            if (uri.Scheme is not ("https" or "http" or "ssh" or "git")) throw new ArgumentException("Unsupported clone protocol.");
            if ((uri.Scheme is "http" or "https") && uri.UserInfo.Length > 0 ||
                Regex.IsMatch(uri.Query, "token|password|secret", RegexOptions.IgnoreCase))
                throw new ArgumentException("Use Git Credential Manager or AskPass instead of credentials embedded in the URL.");
        }
    }
}
