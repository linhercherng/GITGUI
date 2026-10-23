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
            plan.Add(new(hash, Message: await git.Text(root, ct, "show", "-s", "--format=%B", hash)));
        return (IReadOnlyList<RebaseStep>)plan;
    }, ct);

    public Task<FileHistoryResult> GetFileHistoryAsync(string root, string path, CancellationToken ct) => Task.Run(async () =>
    {
        root = await git.Root(root, ct); GitAccess.FullPath(root, path);
        if (fileHistory is not null && ReadBackendPolicy.CanUseLibrary(await new CliRepoReader(runner).GetFormatAsync(root, ct)))
        {
            try { return await fileHistory.ReadAsync(root, path, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* Unsupported library reads repeat the same query with Git CLI. */ }
        }
        return await new CliFileHistoryReader(runner).ReadAsync(root, path, ct);
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
        if (op.Action is GitAction.CherryPick or GitAction.Revert or GitAction.RebaseInteractive && state.Status.Files.Count > 0)
            throw new InvalidOperationException("Commit or stash changes before rewriting history.");
        if (op.Action is GitAction.CherryPick or GitAction.Revert or GitAction.Reset or GitAction.CreateTag)
            _ = await reader.Resolve(root, op.Revision ?? (op.Action == GitAction.CreateTag ? "HEAD" : op.Name) ?? "HEAD", ct);
        if (op.Action is GitAction.StashApply or GitAction.StashPop or GitAction.StashDrop)
            _ = await StashSelector(root, op, ct);
        if (op.Action is GitAction.CreateTag or GitAction.DeleteTag)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(op.Name);
            await git.Text(root, ct, "check-ref-format", "refs/tags/" + op.Name);
            if (op.Name.StartsWith('-')) throw new ArgumentException("Invalid tag name.");
            if (op.Action == GitAction.CreateTag && op.Annotated && string.IsNullOrWhiteSpace(op.Message)) throw new ArgumentException("Enter the annotated tag message.");
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

    private async Task<string> StashSelector(string root, GitOperation op, CancellationToken ct)
    {
        // Never trust an index which may have shifted since the user selected it.
        ArgumentException.ThrowIfNullOrWhiteSpace(op.StashOid);
        var lines = (await git.Text(root, ct, "stash", "list", "--format=%H%x00%gd")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var match = lines.Select(l => l.TrimEnd('\r').Split('\0')).FirstOrDefault(f => f[0] == op.StashOid && (op.Name is null || f[1] == op.Name));
        return match is null ? throw new InvalidOperationException("Stash selection changed. Refresh and select again.") : match[1];
    }

    private async Task ExecuteAdvanced(string root, WorkspaceState state, GitOperation op, CancellationToken ct, IProgress<OperationProgress>? progress)
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
                if (op.Annotated) await RunWrite(root, ["tag", "--annotate", "--file=-", "--", op.Name!, await reader.Resolve(root, revision, ct)], Encoding.UTF8.GetBytes(op.Message!), ct, progress);
                else await Run("tag", "--", op.Name!, await reader.Resolve(root, revision, ct));
                break;
            case GitAction.DeleteTag: await Run("tag", "--delete", "--", op.Name!); break;
            case GitAction.AddRemote: await Run("remote", "add", "--", op.Remote ?? op.Name!, op.Url!); break;
            case GitAction.RemoveRemote: await Run("remote", "remove", op.Remote ?? op.Name!); break;
            case GitAction.RenameRemote: await Run("remote", "rename", op.Remote ?? op.Name!, op.NewName!); break;
            case GitAction.SetRemoteUrl:
                await Run(["remote", "set-url", .. (op.PushUrl ? new[] { "--push" } : []), op.Remote ?? op.Name!, op.Url!]); break;
            case GitAction.RebaseInteractive:
                await PrepareRebase(root, op, ct);
                await RunWithEditor(root, ["rebase", "--interactive", "--no-autostash", "--no-autosquash", "--no-update-refs", "--no-rebase-merges", "--no-fork-point", "--reapply-cherry-picks", "--keep-empty", "--empty=keep", await reader.Resolve(root, revision, ct)], ct, progress); break;
            case GitAction.ContinueOperation: case GitAction.SkipOperation: case GitAction.AbortOperation:
                var action = op.Action == GitAction.ContinueOperation ? "--continue" : op.Action == GitAction.SkipOperation ? "--skip" : "--abort";
                await RunWithEditor(root, [state.InProgress.Kind, action], ct, progress); break;
            default: throw new InvalidOperationException("Unsupported operation.");
        }
    }

    private static string ShellQuote(string path) => "'" + path.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private async Task<string> RebaseDirectory(string root, CancellationToken ct)
        => Path.Combine(await git.Text(root, ct, "rev-parse", "--absolute-git-dir"), "gitgui-rebase");

    private async Task PrepareRebase(string root, GitOperation op, CancellationToken ct)
    {
        var dir = await RebaseDirectory(root, ct);
        if (Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Unsafe rebase helper directory.");
        Directory.CreateDirectory(dir);
        async Task Write(string name, string content)
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Unsafe rebase helper file.");
            var temporary = Path.Combine(dir, ".write-" + Guid.NewGuid().ToString("N"));
            try
            {
                await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), ct);
                File.Move(temporary, path, true);
            }
            finally { File.Delete(temporary); }
        }
        await Write("todo", string.Join("\n", op.RebasePlan.Select(s => s.Action.ToString().ToLowerInvariant() + " " + s.Commit)) + "\n");
        await Write("sequence.sh", "#!/bin/sh\ncat -- " + ShellQuote(Path.Combine(dir, "todo").Replace('\\', '/')) + " > \"$1\"\n");
        var editor = "#!/bin/sh\n";
        // Git's done file identifies the original commit, including after a restart/conflict.
        var done = Path.Combine(Path.GetDirectoryName(dir)!, "rebase-merge", "done").Replace('\\', '/');
        editor += "step=$(tail -n 1 -- " + ShellQuote(done) + " 2>/dev/null)\ncase \"$step\" in\n";
        string? squashMessage = null;
        foreach (var step in op.RebasePlan)
        {
            if (step.Action == RebaseStepKind.Drop) continue;
            if (step.Action == RebaseStepKind.Squash && !string.IsNullOrWhiteSpace(step.Message)) squashMessage = step.Message;
            else if (step.Action is not (RebaseStepKind.Fixup or RebaseStepKind.Squash)) squashMessage = null;
            var message = step.Action == RebaseStepKind.Reword ? step.Message : squashMessage;
            if (string.IsNullOrWhiteSpace(message)) continue;
            await Write("message-" + step.Commit, message.TrimEnd() + "\n");
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
        var result = await runner.RunAsync(root, args, ct, new() { IsReadOnly = false, Trusted = true, Environment = env }, progress);
        if (result.ExitCode != 0) throw new GitCommandException(result);
    }
}
