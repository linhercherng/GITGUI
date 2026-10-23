using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GitGui.Core;

namespace GitGui.Engine.Cli;

public sealed partial class WorkspaceService
{
    public async Task<IAsyncDisposable> AcquireTerminalLeaseAsync(string path, CancellationToken ct)
    {
        var root = await git.Root(path, ct);
        var key = await git.Text(root, ct, "rev-parse", "--path-format=absolute", "--git-common-dir");
        var queue = Queues.GetOrAdd(key, _ => new(1, 1));
        await queue.WaitAsync(ct);
        return new QueueLease(queue);
    }
    private sealed class QueueLease(SemaphoreSlim queue) : IAsyncDisposable
    {
        private int disposed;
        public ValueTask DisposeAsync() { if (Interlocked.Exchange(ref disposed, 1) == 0) queue.Release(); return ValueTask.CompletedTask; }
    }

    public Task<AdvancedState> GetAdvancedStateAsync(string root, CancellationToken ct) => Task.Run(async () =>
    {
        root = await git.Root(root, ct);
        var stash = await git.Text(root, ct, "stash", "list", "--format=%H%x00%gd%x00%gs%x00%cI");
        var stashes = stash.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r').Split('\0'))
            .Select(f => new StashInfo(f[0], f[1], f[2], DateTimeOffset.Parse(f[3], CultureInfo.InvariantCulture))).ToArray();
        var tagsText = await git.Text(root, ct, "for-each-ref", "--format=%(refname:strip=2)%00%(objectname)%00%(*objectname)%00%(objecttype)%00%(contents:subject)", "refs/tags");
        var tags = tagsText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r').Split('\0'))
            .Select(f => new TagInfo(f[0], f[1], f[2].Length > 0 ? f[2] : f[1], f[3] == "tag", f[4])).ToArray();
        var hasHead = await git.TryText(root, ct, "rev-parse", "--verify", "--quiet", "HEAD") is not null;
        var reflogText = hasHead ? await git.Text(root, ct, "reflog", "show", "--max-count=500", "--date=iso-strict", "--format=%H%x00%gD%x00%gs%x00%gn", "HEAD") : "";
        var reflog = reflogText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r').Split('\0'))
            .Select(f => new ReflogInfo(f[0], f[1], f[2], f[3], DateTimeOffset.Parse(f[1][(f[1].LastIndexOf("@{", StringComparison.Ordinal) + 2)..^1], CultureInfo.InvariantCulture))).ToArray();
        var remotes = new List<AdvancedRemoteInfo>();
        foreach (var name in (await git.Text(root, ct, "remote")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(n => n.TrimEnd('\r')))
        {
            async Task<string[]> Urls(bool push) => (await git.Text(root, ct, ["remote", "get-url", "--all", .. (push ? new[] { "--push" } : []), name]))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(GitProcessRunner.Redact).ToArray();
            remotes.Add(new(name, await Urls(false), await Urls(true)));
        }
        var state = await reader.Open(root, new(Take: 1), ct);
        return new AdvancedState(stashes, tags, reflog, remotes, state.InProgress);
    }, ct);

    public Task<IReadOnlyList<RebaseStep>> GetRebasePlanAsync(string root, string upstream, CancellationToken ct) => Task.Run(async () =>
    {
        root = await git.Root(root, ct);
        var onto = await reader.Resolve(root, upstream, ct);
        var merges = await git.Text(root, ct, "rev-list", "--merges", onto + "..HEAD", "--");
        if (merges.Length > 0) throw new InvalidOperationException("This rebase plan contains merge commits. Select a linear commit range.");
        var commits = await git.Text(root, ct, "rev-list", "--reverse", onto + "..HEAD", "--");
        var plan = new List<RebaseStep>();
        foreach (var hash in commits.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd('\r')))
        {
            var message = await new CommitTextReader(runner).ReadAsync(root, hash, ct);
            if (message.Encoding.IsLossy) throw new InvalidOperationException("A rebase message has an unknown or invalid encoding. Correct its encoding with a byte-aware Git tool before editing this plan.");
            plan.Add(new(hash, Message: message.Message));
        }
        return (IReadOnlyList<RebaseStep>)plan;
    }, ct);

    public Task<FileHistoryResult> GetFileHistoryAsync(string root, string path, CancellationToken ct) => Task.Run(async () =>
    {
        root = await git.Root(root, ct); GitAccess.FullPath(root, path);
        if (fileHistory is not null && ReadBackendPolicy.CanUseLibrary(await new CliRepoReader(runner).GetFormatAsync(root, ct)))
        {
            try { return await Enrich(await fileHistory.ReadAsync(root, path, ct)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* Unsupported library reads repeat the same query with Git CLI. */ }
        }
        return await Enrich(await new CliFileHistoryReader(runner).ReadAsync(root, path, ct));
        async Task<FileHistoryResult> Enrich(FileHistoryResult result)
        {
            var commits = await new CommitTextReader(runner).EnrichAsync(root, result.History.Select(h => h.Commit).ToArray(), ct);
            return result with { History = result.History.Select((entry, i) => entry with { Commit = commits[i] }).ToArray() };
        }
    }, ct);

    private async Task PreflightAdvanced(string root, WorkspaceState state, GitOperation op, CancellationToken ct)
    {
        if (op.Action is GitAction.ContinueOperation or GitAction.SkipOperation or GitAction.AbortOperation)
        {
            if (!state.InProgress.IsActive || state.InProgress.Kind is not ("rebase" or "cherry-pick" or "revert" or "merge"))
                throw new InvalidOperationException("No supported operation in progress.");
            if (op.Action == GitAction.SkipOperation && state.InProgress.Kind == "merge") throw new InvalidOperationException("A merge cannot be skipped; resolve or abort it.");
            if (op.Action == GitAction.ContinueOperation && state.InProgress.HasConflicts) throw new InvalidOperationException("Resolve and stage every conflict before continuing.");
        }
        if (op.Action is GitAction.CherryPick or GitAction.Revert or GitAction.RebaseInteractive && await HasParentChanges(root, state.Status.Files, true, ct))
            throw new InvalidOperationException("Commit or stash changes before rewriting history.");
        if (op.Action is GitAction.CherryPick or GitAction.Revert or GitAction.Reset or GitAction.CreateTag)
            _ = await reader.Resolve(root, op.Revision ?? (op.Action == GitAction.CreateTag ? "HEAD" : op.Name) ?? "HEAD", ct);
        if (op.Action is GitAction.StashApply or GitAction.StashPop or GitAction.StashDrop)
            _ = await StashSelector(root, op, ct);
        if (op.Action is GitAction.StashApply or GitAction.StashPop)
            await EnsureNoSubmoduleTree(root, op.StashOid!, ct);
        if (op.Action == GitAction.Reset && op.ResetMode == ResetMode.Mixed)
            await EnsureNoSubmoduleTree(root, await reader.Resolve(root, op.Revision ?? op.Name ?? "HEAD", ct), ct);
        if (op.Action is GitAction.CherryPick or GitAction.Revert)
        {
            var raw = await git.Text(root, ct, "diff-tree", "--root", "--raw", "--no-renames", "--no-commit-id", "-r", "-z",
                await reader.Resolve(root, op.Revision ?? op.Name ?? "HEAD", ct), "--");
            EnsureNoSubmoduleChanges(raw);
        }
        if (op.Action is GitAction.CreateTag or GitAction.DeleteTag)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(op.Name);
            await git.Text(root, ct, "check-ref-format", "refs/tags/" + op.Name);
            if (op.Name.StartsWith('-')) throw new ArgumentException("Invalid tag name.");
            if (op.Action == GitAction.CreateTag && (op.Annotated || op.Sign) && string.IsNullOrWhiteSpace(op.Message)) throw new ArgumentException("Enter the annotated or signed tag message.");
        }
        if (op.Action is GitAction.AddRemote or GitAction.RenameRemote or GitAction.SetRemoteUrl or GitAction.RemoveRemote)
        {
            var name = op.Remote ?? op.Name;
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('-') || !Regex.IsMatch(name, @"^[\p{L}\p{N}._/-]+$")) throw new ArgumentException("Invalid remote name.");
            await git.Text(root, ct, "check-ref-format", "refs/remotes/" + name + "/probe");
            if (op.Action is GitAction.AddRemote or GitAction.SetRemoteUrl) ValidateCloneUrl(op.Url);
            if (op.Action == GitAction.RenameRemote)
            {
                if (string.IsNullOrWhiteSpace(op.NewName) || op.NewName.StartsWith('-') || !Regex.IsMatch(op.NewName, @"^[\p{L}\p{N}._/-]+$")) throw new ArgumentException("Invalid remote name.");
                await git.Text(root, ct, "check-ref-format", "refs/remotes/" + op.NewName + "/probe");
            }
        }
        if (op.Action == GitAction.RebaseInteractive)
        {
            if (state.Status.Branch.StartsWith('(')) throw new InvalidOperationException("Switch to a local branch before rebase.");
            var expected = await GetRebasePlanAsync(root, op.Revision ?? op.Name ?? "", ct);
            var upstream = await reader.Resolve(root, op.Revision ?? op.Name ?? "", ct);
            await EnsureNoSubmoduleTree(root, upstream, ct);
            EnsureNoSubmoduleChanges(await git.Text(root, ct, "log", "--format=", "--raw", "--no-renames", "-z", upstream + "..HEAD", "--"));
            if (expected.Count == 0 || expected.Count != op.RebasePlan.Count ||
                !expected.Select(s => s.Commit).ToHashSet(StringComparer.Ordinal).SetEquals(op.RebasePlan.Select(s => s.Commit)) ||
                op.RebasePlan.Select(s => s.Commit).Distinct(StringComparer.Ordinal).Count() != expected.Count)
                throw new InvalidOperationException("Rebase plan is stale or incomplete. Reload the plan.");
            var picked = false;
            foreach (var step in op.RebasePlan)
            {
                if (!Enum.IsDefined(step.Action)) throw new ArgumentException("Invalid rebase action.");
                if (step.Action is RebaseStepKind.Squash or RebaseStepKind.Fixup && !picked) throw new ArgumentException("Squash/fixup must follow a kept commit.");
                if (step.Action == RebaseStepKind.Reword && string.IsNullOrWhiteSpace(step.Message)) throw new ArgumentException("Enter a message for each reword step.");
                if (step.Action != RebaseStepKind.Drop) picked = true;
            }
        }
        if (op.Action == GitAction.Reset && !Enum.IsDefined(op.ResetMode)) throw new ArgumentException("Invalid reset mode.");
    }

    private async Task EnsureParentTarget(string root, string revision, CancellationToken ct)
    {
        await EnsureNoSubmoduleTree(root, revision, ct);
        var head = await git.TryText(root, ct, "rev-parse", "--verify", "--quiet", "HEAD");
        if (head is not null)
            EnsureNoSubmoduleChanges(await git.Text(root, ct, "diff", "--raw", "--no-renames", "-z", head, revision, "--"));
    }

    private async Task EnsureNoSubmoduleTree(string root, string revision, CancellationToken ct)
    {
        var tree = await git.Text(root, ct, "ls-tree", "-r", "-z", revision, "--");
        EnsureGitlinkTargets(root, tree.Split('\0', StringSplitOptions.RemoveEmptyEntries));
        await EnsureNoChildOverwrite(root, tree.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(e => !e.StartsWith("160000 ", StringComparison.Ordinal)).Select(e => e[(e.IndexOf('\t') + 1)..]), ct);
    }
    private static void EnsureNoSubmoduleChanges(string raw)
    {
        var fields = raw.Split('\0');
        for (var n = 0; n < fields.Length; n++)
        {
            var header = Regex.Match(fields[n].TrimStart('\r', '\n'), @"^:([0-7]{6}) ([0-7]{6}) [0-9a-f]+ [0-9a-f]+ [A-Z]$");
            if (!header.Success) continue;
            var before = header.Groups[1].Value; var after = header.Groups[2].Value;
            if ((before == "160000" && after is not ("160000" or "000000")) ||
                (after == "160000" && before is not ("160000" or "000000")))
                throw new InvalidOperationException("A file/submodule type transition can overwrite a child repository; move the child aside first.");
            n++; // --no-renames emits exactly one following path; it is never a header.
        }
    }

    private async Task<string> StashSelector(string root, GitOperation op, CancellationToken ct)
    {
        // Never trust an index which may have shifted since the user selected it.
        ArgumentException.ThrowIfNullOrWhiteSpace(op.StashOid);
        var lines = (await git.Text(root, ct, "stash", "list", "--format=%H%x00%gd")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var match = lines.Select(l => l.TrimEnd('\r').Split('\0')).FirstOrDefault(f => f[0] == op.StashOid && (op.Name is null || f[1] == op.Name));
        return match is null ? throw new InvalidOperationException("Stash selection changed. Refresh and select again.") : match[1];
    }

    private async Task<IReadOnlyList<string>> ResetCollisionPaths(string root, GitOperation op, CancellationToken ct)
    {
        var target = await reader.Resolve(root, op.Revision ?? op.Name ?? "HEAD", ct);
        var entries = GitAccess.NullFields(await git.Bytes(root, ct, ["ls-tree", "-r", "-z", target, "--"]));
        EnsureGitlinkTargets(root, entries);
        var paths = entries.Where(e => !e.StartsWith("160000 ", StringComparison.Ordinal)).Select(e => e[(e.IndexOf('\t') + 1)..]).ToArray();
        await EnsureNoChildOverwrite(root, paths, ct);
        foreach (var path in paths)
        {
            var full = GitAccess.FullPath(root, path);
            if (Directory.Exists(full) && new DirectoryInfo(full).LinkTarget is null)
                throw new InvalidOperationException("A directory blocks the reset target. Move it aside first so its ignored contents are preserved: " + path);
        }
        return GitAccess.OverwriteCollisions(root, paths);
    }

    private static IReadOnlyList<string> RawChangedPaths(string raw)
    {
        EnsureNoSubmoduleChanges(raw);
        var fields = raw.Split('\0'); var paths = new List<string>();
        for (var n = 0; n + 1 < fields.Length; n++)
        {
            var header = Regex.Match(fields[n].TrimStart('\r', '\n'), @"^:([0-7]{6}) ([0-7]{6}) [0-9a-f]+ [0-9a-f]+ [A-Z]$");
            if (header.Success)
            {
                var path = fields[++n];
                if (header.Groups[1].Value != "160000" && header.Groups[2].Value != "160000") paths.Add(path);
            }
        }
        return paths;
    }
    private async Task<IReadOnlyList<string>> TreePaths(string root, string revision, CancellationToken ct)
    {
        var entries = GitAccess.NullFields(await git.Bytes(root, ct, ["ls-tree", "-r", "-z", revision, "--"]));
        EnsureGitlinkTargets(root, entries);
        var paths = entries.Where(e => !e.StartsWith("160000 ", StringComparison.Ordinal)).Select(e => e[(e.IndexOf('\t') + 1)..]).ToArray();
        await EnsureNoChildOverwrite(root, paths, ct);
        return paths;
    }

    private async Task EnsureNoChildOverwrite(string root, IEnumerable<string> paths, CancellationToken ct)
    {
        var links = GitAccess.NullFields(await git.Bytes(root, ct, ["ls-files", "--stage", "-z"]))
            .Where(e => e.StartsWith("160000 ", StringComparison.Ordinal)).Select(e => e[(e.IndexOf('\t') + 1)..]).ToArray();
        var comparison = GitAccess.CollisionComparison;
        foreach (var path in paths)
            if (links.Any(p => path.Equals(p, comparison) || path.StartsWith(p + "/", comparison) || p.StartsWith(path + "/", comparison)))
                throw new InvalidOperationException("A parent file would overwrite a child repository: " + path);
    }

    private static void EnsureGitlinkTargets(string root, IEnumerable<string> entries)
    {
        var links = entries.Where(e => e.StartsWith("160000 ", StringComparison.Ordinal)).Select(e => e[(e.IndexOf('\t') + 1)..]);
        if (GitAccess.OverwriteCollisions(root, links).Count > 0)
            throw new InvalidOperationException("A parent file blocks a submodule target; move it aside before changing the parent tree.");
    }

    private async Task<bool> HasParentChanges(string root, IReadOnlyList<FileChange> files, bool includeUntracked, CancellationToken ct)
    {
        var links = GitAccess.NullFields(await git.Bytes(root, ct, ["ls-files", "--stage", "-z"]))
            .Where(e => e.StartsWith("160000 ", StringComparison.Ordinal)).Select(e => e[(e.IndexOf('\t') + 1)..])
            .ToHashSet(GitAccess.CollisionComparer);
        return files.Any(f => (includeUntracked || f.IndexStatus != "?") && !(f.IndexStatus == "." && links.Contains(f.Path)));
    }

    private async Task<IReadOnlyList<string>> OperationOverwritePaths(string root, InProgressState state, GitOperation op, CancellationToken ct)
    {
        if (op.Action == GitAction.Reset && op.ResetMode == ResetMode.Hard) return await ResetCollisionPaths(root, op, ct);
        var paths = new List<string>();
        if (op.Action is GitAction.CherryPick or GitAction.Revert)
            paths.AddRange(RawChangedPaths(await git.Text(root, ct, "diff-tree", "--root", "--raw", "--no-renames", "--no-commit-id", "-r", "-z",
                await reader.Resolve(root, op.Revision ?? op.Name ?? "HEAD", ct), "--")));
        else if (op.Action == GitAction.RebaseInteractive)
        {
            var upstream = await reader.Resolve(root, op.Revision ?? op.Name ?? "", ct);
            paths.AddRange(await TreePaths(root, upstream, ct));
            paths.AddRange(RawChangedPaths(await git.Text(root, ct, "log", "--format=", "--raw", "--no-renames", "-z", upstream + "..HEAD", "--")));
        }
        else if (op.Action is GitAction.StashApply or GitAction.StashPop)
        {
            paths.AddRange(await TreePaths(root, op.StashOid!, ct));
            var untracked = await git.TryText(root, ct, "rev-parse", "--verify", "--quiet", op.StashOid + "^3^{commit}");
            if (untracked is not null) paths.AddRange(await TreePaths(root, untracked, ct));
        }
        else if (op.Action is GitAction.ContinueOperation or GitAction.SkipOperation or GitAction.AbortOperation or GitAction.AbortMerge)
            paths.AddRange(await PendingOverwritePaths(root, state, ct));
        if (paths.Count == 0) return [];
        var directories = paths.Distinct().Where(p =>
        {
            var full = GitAccess.FullPath(root, p);
            return Directory.Exists(full) && new DirectoryInfo(full).LinkTarget is null;
        }).ToArray();
        if (directories.Length > 0 && (await git.Bytes(root, ct,
            ["ls-files", "--others", "--ignored", "--exclude-standard", "-z", "--", .. directories])).Length > 0)
            throw new InvalidOperationException("Ignored directory contents block this operation. Move them aside first to preserve them.");
        return GitAccess.OverwriteCollisions(root, paths);
    }

    private async Task<IReadOnlyList<string>> PendingOverwritePaths(string root, InProgressState state, CancellationToken ct)
    {
        var commits = new HashSet<string>(StringComparer.Ordinal);
        var trees = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in new[] { "HEAD", "ORIG_HEAD", "REBASE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD" })
        {
            var oid = await git.TryText(root, ct, "rev-parse", "--verify", "--quiet", name + "^{commit}");
            if (oid is null) continue;
            commits.Add(oid); if (name is "HEAD" or "ORIG_HEAD") trees.Add(oid);
        }
        var directory = await git.Text(root, ct, "rev-parse", "--absolute-git-dir");
        string? rebaseOriginal = null, rebaseOnto = null;
        foreach (var name in new[] { "rebase-merge/orig-head", "rebase-merge/onto", "rebase-apply/orig-head", "rebase-apply/onto", "sequencer/head" })
        {
            var full = GitAccess.FullPath(directory, name);
            if (!File.Exists(full)) continue;
            if (new FileInfo(full).LinkTarget is not null) throw new InvalidOperationException("Unsafe operation state file.");
            var value = (await File.ReadAllTextAsync(full, ct)).Trim();
            if (!Regex.IsMatch(value, "^[0-9a-f]{40}([0-9a-f]{24})?$")) throw new InvalidOperationException("Invalid operation commit.");
            commits.Add(value); trees.Add(value);
            if (name.EndsWith("/orig-head", StringComparison.Ordinal)) rebaseOriginal = value;
            if (name.EndsWith("/onto", StringComparison.Ordinal)) rebaseOnto = value;
        }
        foreach (var name in new[] { "rebase-merge/git-rebase-todo", "sequencer/todo" })
        {
            var full = GitAccess.FullPath(directory, name);
            if (!File.Exists(full)) continue;
            if (new FileInfo(full).LinkTarget is not null) throw new InvalidOperationException("Unsafe operation todo file.");
            foreach (var line in (await File.ReadAllTextAsync(full, ct)).Split('\n'))
            {
                var match = Regex.Match(line, @"^\s*(?:pick|reword|edit|squash|fixup|drop|revert|p|r|e|s|f|d) ([0-9a-f]{4,64})(?:\s|$)");
                if (match.Success) commits.Add(match.Groups[1].Value);
            }
        }
        var paths = new List<string>();
        foreach (var tree in trees) paths.AddRange(await TreePaths(root, tree, ct));
        if (commits.Count > 0) paths.AddRange(RawChangedPaths(await git.Text(root, ct,
            ["log", "--no-walk=unsorted", "--format=", "--raw", "--no-renames", "-z", .. commits, "--"])));
        if (state.Kind == "rebase" && rebaseOriginal is not null && rebaseOnto is not null)
            paths.AddRange(RawChangedPaths(await git.Text(root, ct, "log", "--format=", "--raw", "--no-renames", "-z", rebaseOnto + ".." + rebaseOriginal, "--")));
        return paths;
    }

    private async Task ExecuteAdvanced(string root, WorkspaceState state, GitOperation op, OperationMessageBytes messages, CancellationToken ct, IProgress<OperationProgress>? progress)
    {
        // Stash internally constructs its own clean pathspecs. Forcing Git's global literal
        // mode breaks removal of non-ASCII untracked names. These commands accept no user paths.
        async Task Run(params string[] args) => await RunWrite(root, args, null, ct, progress,
            literalPathspecs: op.Action is not (GitAction.StashPush or GitAction.StashApply or GitAction.StashPop or GitAction.StashDrop));
        var revision = op.Revision ?? (op.Action == GitAction.CreateTag ? "HEAD" : op.Name) ?? "HEAD";
        switch (op.Action)
        {
            case GitAction.StashPush:
                await Run(["stash", "push", .. (op.IncludeUntracked ? new[] { "--include-untracked" } : []), "--message", op.Message ?? "GITGUI stash", "--"]); break;
            case GitAction.StashApply: case GitAction.StashPop: case GitAction.StashDrop:
                await Run("stash", op.Action == GitAction.StashApply ? "apply" : op.Action == GitAction.StashPop ? "pop" : "drop", await StashSelector(root, op, ct)); break;
            case GitAction.CherryPick: case GitAction.Revert:
                await Run(op.Action == GitAction.CherryPick ? "cherry-pick" : "revert", "--no-edit", await reader.Resolve(root, revision, ct)); break;
            case GitAction.Reset:
                await Run("reset", "--" + op.ResetMode.ToString().ToLowerInvariant(), await reader.Resolve(root, revision, ct), "--"); break;
            case GitAction.CreateTag:
                if (op.Annotated || op.Sign) await RunWrite(root, ["tag", .. (op.Sign ? new[] { "--sign" } : new[] { "--annotate", "--no-sign" }), "--file=-", "--", op.Name!, await reader.Resolve(root, revision, ct)], Encoding.UTF8.GetBytes(op.Message!), ct, progress);
                else await Run("tag", "--no-sign", "--", op.Name!, await reader.Resolve(root, revision, ct));
                break;
            case GitAction.DeleteTag: await Run("tag", "--delete", "--", op.Name!); break;
            case GitAction.AddRemote: await Run("remote", "add", "--", op.Remote ?? op.Name!, op.Url!); break;
            case GitAction.RemoveRemote: await Run("remote", "remove", op.Remote ?? op.Name!); break;
            case GitAction.RenameRemote: await Run("remote", "rename", op.Remote ?? op.Name!, op.NewName!); break;
            case GitAction.SetRemoteUrl:
                await Run(["remote", "set-url", .. (op.PushUrl ? new[] { "--push" } : []), op.Remote ?? op.Name!, op.Url!]); break;
            case GitAction.RebaseInteractive:
                await PrepareRebase(root, op, messages, ct);
                // Git 2.40's fast-forward reword path omits --allow-empty when amending.
                // Replay reword plans (including preceding picks) with native hook/editor semantics;
                // plans without reword retain Git's normal fast-forward behavior.
                await RunWithEditor(root, ["rebase", "--interactive", .. (op.RebasePlan.Any(s => s.Action == RebaseStepKind.Reword) ? new[] { "--force-rebase" } : []), "--no-autostash", "--no-autosquash", "--no-update-refs", "--no-rebase-merges", "--no-fork-point", "--reapply-cherry-picks", "--keep-empty", "--empty=keep", await reader.Resolve(root, revision, ct)], ct, progress); break;
            case GitAction.ContinueOperation: case GitAction.SkipOperation: case GitAction.AbortOperation:
                var action = op.Action == GitAction.ContinueOperation ? "--continue" : op.Action == GitAction.SkipOperation ? "--skip" : "--abort";
                await RunWithEditor(root, [state.InProgress.Kind, action], ct, progress); break;
            default: throw new InvalidOperationException("Unsupported operation.");
        }
    }

    private static string ShellQuote(string path) => "'" + path.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private async Task<string> RebaseDirectory(string root, CancellationToken ct)
        => Path.Combine(await git.Text(root, ct, "rev-parse", "--absolute-git-dir"), "gitgui-rebase");

    private async Task PrepareRebase(string root, GitOperation op, OperationMessageBytes messages, CancellationToken ct)
    {
        var dir = await RebaseDirectory(root, ct);
        if (Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Unsafe rebase helper directory.");
        Directory.CreateDirectory(dir);
        async Task WriteBytes(string name, byte[] content)
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Unsafe rebase helper file.");
            var temporary = Path.Combine(dir, ".write-" + Guid.NewGuid().ToString("N"));
            try
            {
                await File.WriteAllBytesAsync(temporary, content, ct);
                File.Move(temporary, path, true);
            }
            finally { File.Delete(temporary); }
        }
        Task Write(string name, string content) => WriteBytes(name, Encoding.UTF8.GetBytes(content));
        await Write("message-encoding", messages.Encoding);
        await Write("todo", string.Join("\n", op.RebasePlan.Select(s => s.Action.ToString().ToLowerInvariant() + " " + s.Commit)) + "\n");
        await Write("sequence.sh", "#!/bin/sh\ncat -- " + ShellQuote(Path.Combine(dir, "todo").Replace('\\', '/')) + " > \"$1\"\n");
        var editor = "#!/bin/sh\n";
        // Git's done file identifies the original commit, including after a restart/conflict.
        var done = Path.Combine(Path.GetDirectoryName(dir)!, "rebase-merge", "done").Replace('\\', '/');
        editor += "step=$(tail -n 1 -- " + ShellQuote(done) + " 2>/dev/null)\ncase \"$step\" in\n";
        foreach (var step in op.RebasePlan)
        {
            if (!messages.Rebase.TryGetValue(step.Commit, out var message)) continue;
            await WriteBytes("message-" + step.Commit, message);
            editor += step.Action.ToString().ToLowerInvariant() + "\\ " + step.Commit + "*) cat -- " + ShellQuote(Path.Combine(dir, "message-" + step.Commit).Replace('\\', '/')) + " > \"$1\" ;;\n";
        }
        editor += "esac\nexit 0\n";
        await Write("editor.sh", editor);
    }

    private async Task RunWithEditor(string root, string[] args, CancellationToken ct, IProgress<OperationProgress>? progress)
    {
        var dir = await RebaseDirectory(root, ct);
        var env = new Dictionary<string, string?> { ["GIT_EDITOR"] = "true", ["GIT_SEQUENCE_EDITOR"] = "true" };
        if (File.Exists(Path.Combine(dir, "editor.sh"))) env["GIT_EDITOR"] = "sh " + ShellQuote(Path.Combine(dir, "editor.sh").Replace('\\', '/'));
        if (File.Exists(Path.Combine(dir, "sequence.sh"))) env["GIT_SEQUENCE_EDITOR"] = "sh " + ShellQuote(Path.Combine(dir, "sequence.sh").Replace('\\', '/'));
        var result = await runner.RunAsync(root, ["-c", "submodule.recurse=false", .. args], ct, new() { IsReadOnly = false, Trusted = true, Environment = env }, progress);
        if (result.ExitCode != 0) throw new GitCommandException(result);
    }
}
