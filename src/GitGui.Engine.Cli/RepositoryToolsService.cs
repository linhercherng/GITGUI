using System.Text;
using GitGui.Core;

namespace GitGui.Engine.Cli;

public sealed partial class RepositoryToolsService : IRepositoryToolsService
{
    private readonly GitAccess git;
    private readonly GitProcessRunner runner;
    private readonly WorkspaceService workspace;
    private readonly SnapshotManager snapshots;
    private readonly IWorkspaceInteraction interaction;

    public RepositoryToolsService(GitProcessRunner runner, WorkspaceService workspace,
        SnapshotManager snapshots, IWorkspaceInteraction interaction)
    {
        this.runner = runner; this.workspace = workspace; this.snapshots = snapshots;
        this.interaction = interaction; git = new(runner);
    }

    public Task<RepositoryToolsState> GetStateAsync(string path, CancellationToken ct) => Task.Run(async () =>
    {
        var root = await git.Root(path, ct);
        var trusted = (await workspace.OpenAsync(root, new(Take: 1), ct)).IsTrusted;
        var submodules = new List<SubmoduleInfo>();
        foreach (var entry in GitAccess.NullFields(await git.Bytes(root, ct, ["ls-files", "--stage", "-z"])))
        {
            var tab = entry.IndexOf('\t'); var header = entry[..tab].Split(' ');
            if (header[0] != "160000" || header[2] != "0") continue;
            var relative = entry[(tab + 1)..]; var child = GitAccess.FullPath(root, relative);
            submodules.Add(new(relative, header[1], File.Exists(Path.Combine(child, ".git")) || Directory.Exists(Path.Combine(child, ".git"))
                ? "initialized" : "uninitialized"));
        }
        var worktrees = await ReadWorktrees(root, ct);
        var lfs = await runner.RunAsync(root, ["lfs", "version"], ct, new() { HideOutput = true });
        var lfsVersion = lfs.ExitCode == 0 ? Encoding.UTF8.GetString(lfs.Output).Trim() : "Git LFS unavailable";
        var lfsStatus = "";
        if (lfs.ExitCode == 0 && trusted)
        {
            var status = await runner.RunAsync(root, ["lfs", "status"], ct, new() { Trusted = true, LiteralPathspecs = false });
            lfsStatus = GitProcessRunner.Redact(Encoding.UTF8.GetString(status.Output) + status.Error);
        }
        var bisectPath = await git.Text(root, ct, "rev-parse", "--path-format=absolute", "--git-path", "BISECT_LOG");
        var sparse = await git.TryText(root, ct, "config", "--bool", "core.sparseCheckout") == "true";
        var cone = await git.TryText(root, ct, "config", "--bool", "core.sparseCheckoutCone") == "true";
        var patterns = Array.Empty<string>();
        if (sparse)
        {
            if (cone) patterns = (await git.Text(root, ct, "sparse-checkout", "list")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            else
            {
                var file = await git.Text(root, ct, "rev-parse", "--path-format=absolute", "--git-path", "info/sparse-checkout");
                if (File.Exists(file)) patterns = (await File.ReadAllTextAsync(file, ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }
        }
        return new RepositoryToolsState(submodules, worktrees, lfsVersion, lfsStatus, File.Exists(bisectPath),
            File.Exists(bisectPath) ? GitProcessRunner.Redact(await File.ReadAllTextAsync(bisectPath, ct)) : "", sparse, cone, patterns);
    }, ct);

    public Task<OperationOutcome> ExecuteAsync(string path, RepositoryToolOperation op,
        IProgress<OperationProgress>? progress, CancellationToken ct)
        => Task.Run(() => Execute(path, op, progress, ct));

    private async Task<OperationOutcome> Execute(string path, RepositoryToolOperation op,
        IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        SnapshotInfo? snapshot = null;
        try
        {
            var root = await git.Root(path, ct);
            progress?.Report(new("等待倉庫操作佇列 / Waiting for repository queue", CanCancel: true));
            await using var lease = await workspace.AcquireTerminalLeaseAsync(root, ct);
            await workspace.TrustAsync(root, ct);
            var state = await workspace.OpenAsync(root, new(Take: 1), ct);
            if (state.InProgress.IsActive && !(state.InProgress.Kind == "bisect" && IsBisect(op.Action)))
                throw new InvalidOperationException("Finish the active " + state.InProgress.Kind + " operation first.");
            var recovery = IsRecovery(op.Action);
            if (recovery) await PreflightRecovery(root, op, ct);
            else await PreflightTools(root, state, op, ct);
            if (op.Action is RepositoryToolAction.WorktreeRemove or RepositoryToolAction.WorktreePrune or RepositoryToolAction.SubmoduleDeinitialize)
            {
                var answer = await interaction.ConfirmAsync(new(op.Action.ToString(),
                    op.Path + "\n" + (op.Action == RepositoryToolAction.SubmoduleDeinitialize
                        ? "子模組工作目錄會移除；父倉庫快照不包含子倉庫內容。Git 會拒絕未提交變更。 / Child worktree is removed; the parent snapshot does not contain child data. Git refuses dirty children."
                        : "此操作沒有快照復原。只移除 Git 允許清理的工作樹，保留分支；不使用 force。 / No snapshot undo. Git's non-force safeguards apply; branches are retained."), RiskLevel.High), ct);
                if (!answer.Accepted) return new(false, "Cancelled", Cancelled: true);
            }
            if (recovery)
            {
                progress?.Report(new("建立父倉庫快照 / Creating parent repository snapshot"));
                snapshot = await snapshots.CreateAsync(root, op.Action.ToString(), ct, await RecoveryOverwritePaths(root, op, ct));
            }
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(op.Action.ToString()));
            if (recovery) await ExecuteRecovery(root, op, CancellationToken.None, progress);
            else await ExecuteTools(root, op, CancellationToken.None, progress);
            return new(true, op.Action + " completed" + (snapshot is null ? "" : " · 父倉庫可復原 / Parent snapshot available"), Snapshot: snapshot);
        }
        catch (OperationCanceledException) { return new(false, "Cancelled", Snapshot: snapshot, Cancelled: true); }
        catch (Exception ex)
        {
            var message = ex is GitCommandException failure ? failure.Result.Error + Encoding.UTF8.GetString(failure.Result.Output) : ex.Message;
            return new(false, GitProcessRunner.Redact(message.Trim()), GitErrorKind.Unknown, snapshot);
        }
    }

    private static bool IsRecovery(RepositoryToolAction action) => action is >= RepositoryToolAction.SubmoduleAdd and <= RepositoryToolAction.SubmoduleDeinitialize
        or RepositoryToolAction.SparseSet or RepositoryToolAction.SparseDisable;
    private static bool IsBisect(RepositoryToolAction action) => action is >= RepositoryToolAction.BisectStart and <= RepositoryToolAction.BisectReset;

    private async Task Run(string root, string[] args, CancellationToken ct, IProgress<OperationProgress>? progress, byte[]? input = null)
    {
        // LFS uses Git's attr pathspecs internally; inheriting GIT_LITERAL_PATHSPECS
        // silently leaves pointer files in place even when lfs pull exits zero.
        var result = await runner.RunAsync(root, ["-c", "submodule.recurse=false", .. args], ct, new()
        { IsReadOnly = false, Trusted = true, Input = input, LiteralPathspecs = args[0] != "lfs" }, progress);
        if (result.ExitCode != 0) throw new GitCommandException(result);
    }

    private async Task<IReadOnlyList<WorktreeInfo>> ReadWorktrees(string root, CancellationToken ct)
    {
        var fields = Encoding.UTF8.GetString(await git.Bytes(root, ct, ["worktree", "list", "--porcelain", "-z"])).Split('\0');
        var rows = new List<WorktreeInfo>(); var values = new Dictionary<string, string>();
        void Save()
        {
            if (!values.TryGetValue("worktree", out var path)) return;
            rows.Add(new(Path.GetFullPath(path), values.GetValueOrDefault("HEAD", ""), values.GetValueOrDefault("branch", ""),
                values.ContainsKey("bare"), values.ContainsKey("detached"), values.ContainsKey("locked"),
                values.GetValueOrDefault("locked", ""), values.ContainsKey("prunable"))); values.Clear();
        }
        foreach (var field in fields)
        {
            if (field.Length == 0) { Save(); continue; }
            var split = field.IndexOf(' '); values[split < 0 ? field : field[..split]] = split < 0 ? "" : field[(split + 1)..];
        }
        Save(); return rows;
    }

    private async Task<string> Resolve(string root, string revision, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        return await git.Text(root, ct, "rev-parse", "--verify", "--end-of-options", revision + "^{commit}");
    }

    private async Task PreflightTools(string root, WorkspaceState state, RepositoryToolOperation op, CancellationToken ct)
    {
        if (op.Action is >= RepositoryToolAction.WorktreeAdd and <= RepositoryToolAction.WorktreeUnlock)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(op.Path);
            var target = Path.GetFullPath(op.Path);
            for (var part = target; part is not null; part = Path.GetDirectoryName(part))
                if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Worktree path traverses a symbolic link.");
            if (op.Action == RepositoryToolAction.WorktreeAdd)
            {
                if (File.Exists(target) || Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                    throw new InvalidOperationException("Choose a new or empty worktree directory.");
                _ = await Resolve(root, op.Revision, ct);
                if (op.Name.Length > 0) await git.Text(root, ct, "check-ref-format", "--branch", op.Name);
            }
            else
            {
                var row = (await ReadWorktrees(root, ct)).SingleOrDefault(w => GitAccess.SamePath(w.Path, target))
                    ?? throw new InvalidOperationException("Select a registered worktree.");
                if (GitAccess.SamePath(root, target) || row.Bare) throw new InvalidOperationException("Open another worktree before managing this worktree.");
                if (op.Action == RepositoryToolAction.WorktreeRemove && row.Locked) throw new InvalidOperationException("Unlock the worktree first.");
                if (op.Action == RepositoryToolAction.WorktreeRemove)
                {
                    var flags = GitAccess.NullFields(await git.Bytes(target, ct, ["ls-files", "-v", "-z"]));
                    if (flags.Any(p => p.Length > 0 && (char.IsLower(p[0]) || p[0] == 'S')))
                        throw new InvalidOperationException("Index flags can hide unsaved data. Back up files and clear assume-unchanged/skip-worktree flags before removing this worktree.");
                    var ignored = GitAccess.NullFields(await git.Bytes(target, ct, ["ls-files", "--others", "--ignored", "--exclude-standard", "-z"]));
                    if (ignored.Length > 0) throw new InvalidOperationException("Move or back up ignored files before removing this worktree:\n" + string.Join('\n', ignored.Take(30)));
                }
            }
        }
        if (op.Action is >= RepositoryToolAction.LfsInstall and <= RepositoryToolAction.LfsPull)
        {
            await git.Text(root, ct, "lfs", "version");
            if (op.Action is RepositoryToolAction.LfsTrack or RepositoryToolAction.LfsUntrack)
            {
                if (op.Patterns.Count == 0 || op.Patterns.Any(p => string.IsNullOrWhiteSpace(p) || p.StartsWith('-') || p.Any(c => c is '\0' or '\r' or '\n')))
                    throw new ArgumentException("Enter one LFS pattern per line; patterns cannot begin with '-'.");
                var attributes = GitAccess.FullPath(root, ".gitattributes");
                if (new FileInfo(attributes).LinkTarget is not null) throw new InvalidOperationException(".gitattributes is a symbolic link.");
            }
            if (op.Action is RepositoryToolAction.LfsFetch or RepositoryToolAction.LfsPull)
            {
                if (op.Name.StartsWith('-') || op.Name.Length > 0 && !state.Remotes.Any(r => r.Name == op.Name)) throw new ArgumentException("Select a configured remote.");
                if (op.Action == RepositoryToolAction.LfsPull && state.Status.Files.Any(f => f.IndexStatus != "?"))
                    throw new InvalidOperationException("Commit or stash tracked changes before LFS pull.");
            }
        }
        if (IsBisect(op.Action))
        {
            if (op.Action == RepositoryToolAction.BisectStart && state.InProgress.Kind == "bisect") throw new InvalidOperationException("Reset the existing bisect first.");
            if (op.Action != RepositoryToolAction.BisectStart && state.InProgress.Kind != "bisect") throw new InvalidOperationException("No bisect is active.");
            if (state.Status.Files.Any(f => f.IndexStatus != "?")) throw new InvalidOperationException("Commit or stash tracked changes before bisect checkout.");
            await CheckBisectCollisions(root, op, ct);
        }
    }

    private async Task CheckBisectCollisions(string root, RepositoryToolOperation op, CancellationToken ct)
    {
        var bad = await Resolve(root, op.Action == RepositoryToolAction.BisectStart ? op.Revision : "refs/bisect/bad", ct);
        var good = op.Action == RepositoryToolAction.BisectStart ? new[] { await Resolve(root, op.GoodRevision, ct) }
            : (await git.Text(root, ct, "for-each-ref", "--format=%(objectname)", "refs/bisect/good-")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (op.Action == RepositoryToolAction.BisectStart)
        {
            var ancestor = await runner.RunAsync(root, ["merge-base", "--is-ancestor", good[0], bad], ct);
            if (ancestor.ExitCode != 0 || good[0] == bad) throw new InvalidOperationException("The good commit must be a strict ancestor of the bad commit.");
        }
        var paths = new HashSet<string>(GitAccess.NullFields(await git.Bytes(root, ct, ["log", "--format=", "--name-only", "-z", "--no-renames", bad, "--not", .. good, "--"])), StringComparer.Ordinal);
        foreach (var rev in good.Append(bad)) paths.UnionWith(GitAccess.NullFields(await git.Bytes(root, ct, ["ls-tree", "-r", "--name-only", "-z", rev])));
        if (op.Action == RepositoryToolAction.BisectReset)
        {
            var startPath = await git.Text(root, ct, "rev-parse", "--path-format=absolute", "--git-path", "BISECT_START");
            if (File.Exists(startPath)) paths.UnionWith(GitAccess.NullFields(await git.Bytes(root, ct, ["ls-tree", "-r", "--name-only", "-z", await Resolve(root, (await File.ReadAllTextAsync(startPath, ct)).Trim(), ct)])));
        }
        var others = GitAccess.NullFields(await git.Bytes(root, ct, ["ls-files", "--others", "--exclude-standard", "-z"]))
            .Concat(GitAccess.NullFields(await git.Bytes(root, ct, ["ls-files", "--others", "--ignored", "--exclude-standard", "-z"])));
        var comparison = GitAccess.CollisionComparison;
        var collision = others.FirstOrDefault(other => paths.Any(p => p.Equals(other, comparison) || p.StartsWith(other + "/", comparison) || other.StartsWith(p + "/", comparison)));
        if (collision is not null) throw new InvalidOperationException("Move or stash this untracked/ignored file before bisect: " + collision);
    }

    private async Task ExecuteTools(string root, RepositoryToolOperation op, CancellationToken ct, IProgress<OperationProgress>? progress)
    {
        switch (op.Action)
        {
            case RepositoryToolAction.WorktreeAdd:
                await Run(root, ["worktree", "add", .. op.Name.Length == 0 ? new[] { "--detach" } : new[] { "-b", op.Name }, "--", Path.GetFullPath(op.Path), await Resolve(root, op.Revision, ct)], ct, progress); break;
            case RepositoryToolAction.WorktreeRemove: await Run(root, ["worktree", "remove", "--", Path.GetFullPath(op.Path)], ct, progress); break;
            case RepositoryToolAction.WorktreeLock: await Run(root, ["worktree", "lock", "--reason", op.Reason, "--", Path.GetFullPath(op.Path)], ct, progress); break;
            case RepositoryToolAction.WorktreeUnlock: await Run(root, ["worktree", "unlock", "--", Path.GetFullPath(op.Path)], ct, progress); break;
            case RepositoryToolAction.WorktreePrune: await Run(root, ["worktree", "prune", "--verbose", "--expire=now"], ct, progress); break;
            case RepositoryToolAction.LfsInstall: await Run(root, ["lfs", "install", "--local"], ct, progress); break;
            case RepositoryToolAction.LfsTrack: case RepositoryToolAction.LfsUntrack:
                await Run(root, ["lfs", op.Action == RepositoryToolAction.LfsTrack ? "track" : "untrack", .. op.Patterns], ct, progress); break;
            case RepositoryToolAction.LfsFetch: case RepositoryToolAction.LfsPull:
                await Run(root, ["lfs", op.Action == RepositoryToolAction.LfsFetch ? "fetch" : "pull", .. op.Name.Length == 0 ? Array.Empty<string>() : new[] { op.Name }], ct, progress); break;
            case RepositoryToolAction.BisectStart:
                await Run(root, ["bisect", "start", await Resolve(root, op.Revision, ct), await Resolve(root, op.GoodRevision, ct), "--"], ct, progress); break;
            case RepositoryToolAction.BisectGood: case RepositoryToolAction.BisectBad: case RepositoryToolAction.BisectSkip: case RepositoryToolAction.BisectReset:
                await Run(root, ["bisect", op.Action switch { RepositoryToolAction.BisectGood => "good", RepositoryToolAction.BisectBad => "bad", RepositoryToolAction.BisectSkip => "skip", _ => "reset" }], ct, progress); break;
            default: throw new ArgumentOutOfRangeException(nameof(op));
        }
    }
}
