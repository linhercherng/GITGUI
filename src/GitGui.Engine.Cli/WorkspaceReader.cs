using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GitGui.Core;

namespace GitGui.Engine.Cli;

internal sealed partial class WorkspaceReader(GitProcessRunner runner, IRepositorySettings settings, IGraphHistoryReader? history = null)
{
    private readonly GitAccess git = new(runner);
    private readonly CliRepoReader statusReader = new(runner);

    public async Task<WorkspaceState> Open(string path, LogFilter filter, CancellationToken ct, bool presentation = true)
    {
        var root = presentation ? (await Descriptor(path, ct)).Root : await git.Root(path, ct);
        var trusted = await settings.IsTrustedAsync(root, ct);
        var statusTask = statusReader.GetStatusAsync(root, trusted, ct);
        var refsTask = Refs(root, ct);
        var remoteTask = runner.RunAsync(root, ["config", "-z", "--get-regexp", "^remote\\..*\\.url$"], ct);
        var riskyTask = RiskySettings(root, ct);
        var textTask = new TextConfigurationReader(runner).ReadAsync(root, ct);
        var pageTask = ReadPage();
        await Task.WhenAll(statusTask, refsTask, remoteTask, riskyTask, textTask, pageTask);
        var status = await statusTask; var refs = await refsTask; var page = await pageTask;
        RememberStatus(root, status);
        var commits = page?.Commits ?? [];
        var remoteResult = await remoteTask;
        if (remoteResult.ExitCode is not (0 or 1)) throw new GitCommandException(remoteResult);
        var remotes = GitAccess.NullFields(remoteResult.Output).Select(field =>
        { var at = field.IndexOf('\n'); return new RemoteInfo(field[7..(at - 4)], GitProcessRunner.Redact(field[(at + 1)..])); })
            .DistinctBy(r => r.Name).ToArray();
        return new(root, status, commits, refs, remotes, await InProgress(root, status, ct, presentation),
            trusted, await riskyTask)
        { HistoryGeneration = page?.Generation, HasMoreHistory = page?.HasMore ?? false,
            TextSettings = await textTask };
        async Task<HistoryPage?> ReadPage()
        {
            if (!presentation) return null;
            var visible = await refsTask;
            var descriptor = await Descriptor(root, ct);
            var head = await Head(descriptor, visible, ct);
            return await History(root, filter, new("", head ?? "(initial)", []), visible, null, ct);
        }
    }

    public async Task<IReadOnlyList<RefInfo>> Refs(string root, CancellationToken ct)
    {
        var text = await git.Text(root, ct, "for-each-ref",
            "--format=%(refname)%00%(refname:short)%00%(objectname)%00%(upstream:short)%00%(upstream:track)%00%(HEAD)",
            "refs/heads", "refs/remotes", "refs/tags");
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            var f = line.TrimEnd('\r').Split('\0');
            return new RefInfo(f[1], f[0], f[2], f[3].Length > 0 ? f[3] : null, f[4], f[5] == "*");
        }).ToArray();
    }

    public async Task<IReadOnlyList<CommitInfo>> Log(string root, LogFilter filter, RepoStatus status, CancellationToken ct,
        IReadOnlyList<RefInfo>? visibleRefs = null)
    {
        var objectView = RepositoryObjectView.Read(root);
        if (history is not null && (long)filter.Skip + filter.Take <= 100_000 && filter.Search.Length == 0 && filter.Author.Length == 0 &&
            filter.Path.Length == 0 && filter.Since.Length == 0 && filter.Until.Length == 0 &&
            ReadBackendPolicy.CanUseLibrary((await Descriptor(root, ct)).Format) && !objectView.RequiresCli && !objectView.HasCommitGraph)
        {
            try
            {
                var values = history is IStableGraphHistoryReader stable && visibleRefs is not null
                    ? await stable.GetGraphAsync(root, VisibleTips(visibleRefs, status), filter.Skip, filter.Take, ct)
                    : await history.GetGraphAsync(root, filter.Skip, filter.Take, ct);
                return await new CommitTextReader(runner).EnrichAsync(root, values, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* Unsupported native reads retry the same query with Git CLI. */ }
        }
        var args = new List<string> { "log", "--topo-order", "--branches", "--remotes", "--tags", "-z",
            "--encoding=UTF-8", "--format=%H%x00%x00%an%x00%aI%x00%P", "--max-count=" + Math.Clamp(filter.Take, 1, 100_000),
            "--skip=" + Math.Max(0, filter.Skip), "--fixed-strings", "--regexp-ignore-case" };
        if (visibleRefs is not null)
        {
            args.RemoveAll(a => a is "--branches" or "--remotes" or "--tags");
            args.AddRange(VisibleTips(visibleRefs, status));
        }
        var searchedCommit = Regex.IsMatch(filter.Search, "^[0-9a-fA-F]{7,64}$")
            ? await git.TryText(root, ct, "rev-parse", "--verify", "--quiet", "--end-of-options", filter.Search + "^{commit}") : null;
        if (searchedCommit is not null)
        {
            args.RemoveAll(arg => arg is "--branches" or "--remotes" or "--tags" || visibleRefs is not null && VisibleTips(visibleRefs, status).Contains(arg));
            args.Add("--no-walk"); args.Add(searchedCommit);
        }
        else if (filter.Search.Length > 0) args.Add("--grep=" + filter.Search);
        if (filter.Author.Length > 0) args.Add("--author=" + filter.Author);
        foreach (var (value, flag) in new[] { (filter.Since, "--since="), (filter.Until, "--until=") })
        {
            if (value.Length == 0) continue;
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date))
                throw new ArgumentException("Date filters must use a date such as 2026-10-23.");
            args.Add(flag + date.ToString("O"));
        }
        if (visibleRefs is null && searchedCommit is null && status.Head is not (null or "(initial)")) args.Add("HEAD");
        args.Add("--");
        if (filter.Path.Length > 0) { GitAccess.FullPath(root, filter.Path); args.Add(filter.Path); }
        var fields = Decode(await git.Bytes(root, ct, args.ToArray())).Split('\0');
        var result = new List<CommitInfo>();
        for (var n = 0; n + 4 < fields.Length; n += 5)
            result.Add(new(fields[n], fields[n + 1], fields[n + 2], DateTimeOffset.Parse(fields[n + 3], CultureInfo.InvariantCulture),
                fields[n + 4].Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        return await new CommitTextReader(runner).EnrichAsync(root, result, ct);
    }

    public async Task<InProgressState> InProgress(string root, RepoStatus status, CancellationToken ct, bool presentation = false)
    {
        var descriptor = presentation ? await Descriptor(root, ct) : null;
        var directory = descriptor?.GitDir ?? await git.Text(root, ct, "rev-parse", "--absolute-git-dir");
        var kind = new[] { ("rebase-merge", "rebase"), ("rebase-apply", "rebase"), ("MERGE_HEAD", "merge"),
            ("CHERRY_PICK_HEAD", "cherry-pick"), ("REVERT_HEAD", "revert"), ("BISECT_LOG", "bisect") }
            .FirstOrDefault(pair => File.Exists(Path.Combine(directory, pair.Item1)) || Directory.Exists(Path.Combine(directory, pair.Item1))).Item2 ?? "";
        if (kind.Length == 0 && Directory.Exists(Path.Combine(directory, "sequencer")))
        {
            var todo = Path.Combine(directory, "sequencer", "todo");
            kind = File.Exists(todo) && (await File.ReadAllTextAsync(todo, ct)).TrimStart().StartsWith("revert ", StringComparison.Ordinal)
                ? "revert" : "cherry-pick";
        }
        if (kind.Length == 0 && (descriptor?.Format.RefStorage ?? await git.TryText(root, ct, "config", "--get", "extensions.refStorage")) == "reftable")
            foreach (var (name, operation) in new[] { ("CHERRY_PICK_HEAD", "cherry-pick"), ("REVERT_HEAD", "revert"), ("MERGE_HEAD", "merge") })
                if (await git.TryText(root, ct, "rev-parse", "--verify", "--quiet", name) is not null) { kind = operation; break; }
        return new(kind, status.Files.Any(f => f.IndexStatus == "U" || f.WorktreeStatus == "U" ||
            f.IndexStatus + f.WorktreeStatus is "AA" or "DD"));
    }

    public async Task<IReadOnlyList<string>> RiskySettings(string root, CancellationToken ct)
    {
        var result = await runner.RunAsync(root, ["config", "-z", "--get-regexp",
            "^(core\\.(fsmonitor|sshcommand|hookspath)|filter\\..*|diff\\.external|diff\\..*\\.command|credential\\..*|protocol\\..*\\.allow|remote\\..*\\.url)$"], ct);
        if (result.ExitCode == 1) return [];
        if (result.ExitCode != 0) throw new GitCommandException(result);
        return GitAccess.NullFields(result.Output).Select(s => GitProcessRunner.Redact(s.Replace('\n', '='))).ToArray();
    }

    public async Task<string> Resolve(string root, string revision, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        return await git.Text(root, ct, "rev-parse", "--verify", "--end-of-options", revision + "^{commit}");
    }

    private async Task<CommitDetails> ReadCommit(string root, string commit, CancellationToken ct)
    {
        var oid = await Resolve(root, commit, ct);
        var data = Decode(await git.Bytes(root, ct, ["show", "-s", "--encoding=UTF-8", "--format=%H%x00%an%x00%aI%x00%P%x00", oid])).Split('\0', 5);
        var parents = data[3].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var args = new List<string> { "diff-tree", "--no-commit-id", "--name-status", "-z", "-r", "--no-renames" };
        if (parents.Length > 0) args.Add(parents[0]); else args.Add("--root");
        args.Add(oid); args.Add("--");
        var entries = RawFields(await git.Bytes(root, ct, args.ToArray()));
        var files = new List<FileChange>();
        for (var n = 0; n + 1 < entries.Count; n += 2)
            files.Add(new(GitPathPolicy.DecodePath(entries[n + 1]), Encoding.ASCII.GetString(entries[n]), ".", entries[n + 1]));
        var text = await new CommitTextReader(runner).ReadAsync(root, oid, ct);
        var message = text.Message.TrimEnd('\r', '\n');
        return new(new(oid, text.Subject, data[1], DateTimeOffset.Parse(data[2], CultureInfo.InvariantCulture), parents)
            { MessageEncoding = text.Encoding }, message, files) { MessageEncoding = text.Encoding };
    }

    public async Task<string> Diff(string root, DiffRequest request, CancellationToken ct)
    {
        ValidateReadablePath(root, request.Path);
        GitAccess.FullPath(root, request.Path);
        var args = new List<string> { "diff", "--no-color", "--no-ext-diff", "--no-textconv", "--no-renames", "--unified=3" };
        var acceptedOne = false;
        if (request.Commit is not null)
        {
            var detail = await CommitMetadata(root, request.Commit, ct);
            if (detail.Parents.Count > 0) args.Add(detail.Parents[0]);
            else { args[0] = "diff-tree"; args.AddRange(["--root", "-p", "--no-commit-id"]); }
            args.Add(detail.Hash);
        }
        else if (request.Staged) args.Add("--cached");
        args.AddRange(["--", request.Path]);
        var options = new GitRunOptions { Trusted = await settings.IsTrustedAsync(root, ct) };
        var result = await runner.RunAsync(root, args, ct, options);
        // A nonempty native diff already proves this is an indexed change. Only
        // an empty working diff needs the extra check for a new, untracked file.
        if (request.Commit is null && !request.Staged && result.ExitCode == 0 && result.Output.Length == 0 &&
            (await git.Bytes(root, ct, ["ls-files", "-z", "--", request.Path])).Length == 0)
        {
            args.RemoveRange(args.Count - 2, 2);
            args.AddRange(["--no-index", "--", "/dev/null", request.Path]);
            acceptedOne = true;
            result = await runner.RunAsync(root, args, ct, options);
        }
        if (result.ExitCode != 0 && !(acceptedOne && result.ExitCode == 1)) throw new GitCommandException(result);
        try { return GitAccess.Utf8.GetString(result.Output); }
        catch (DecoderFallbackException)
        {
            // Re-encoding a legacy diff as UTF-8 would change bytes when staging selected lines.
            return "Non-UTF-8 text: use whole-file staging/ours/theirs to preserve the original bytes.\n";
        }
    }

    private static string Decode(byte[] bytes)
    {
        try { return GitAccess.Utf8.GetString(bytes); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(950).GetString(bytes);
        }
    }

    public async Task<ConflictContent> Conflict(string root, string path, CancellationToken ct)
    {
        var full = GitAccess.FullPath(root, path);
        var entries = GitAccess.NullFields(await git.Bytes(root, ct, ["ls-files", "--unmerged", "--stage", "-z", "--", path]));
        if (entries.Length == 0) throw new InvalidOperationException("This file has no unresolved index stages.");
        var stages = new Dictionary<int, byte[]>();
        var binary = false;
        foreach (var entry in entries)
        {
            var fields = entry[..entry.IndexOf('\t')].Split(' ');
            var bytes = await git.Bytes(root, ct, ["cat-file", "blob", fields[1]], new() { HideOutput = true });
            stages[int.Parse(fields[2], CultureInfo.InvariantCulture)] = bytes;
            binary |= bytes.Contains((byte)0) || fields[0] == "120000" || !IsUtf8(bytes);
        }
        byte[] result;
        var link = new FileInfo(full).LinkTarget;
        if (link is not null) { binary = true; result = Encoding.UTF8.GetBytes(link); }
        else result = File.Exists(full) ? await File.ReadAllBytesAsync(full, ct) : [];
        binary |= result.Contains((byte)0) || !IsUtf8(result);
        return new(Decode(stages.GetValueOrDefault(1) ?? []), Decode(stages.GetValueOrDefault(2) ?? []),
            Decode(stages.GetValueOrDefault(3) ?? []), Decode(result), binary);
    }

    private static bool IsUtf8(byte[] bytes)
    {
        try { GitAccess.Utf8.GetCharCount(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    public async Task<FilePreview> Preview(string root, DiffRequest request, CancellationToken ct)
    {
        ValidateReadablePath(root, request.Path);
        const int imageLimit = 8 * 1024 * 1024;
        var full = GitAccess.FullPath(root, request.Path);
        var image = new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico" }
            .Contains(Path.GetExtension(request.Path).ToLowerInvariant());
        string? notice = null;
        async Task<(long? Size, byte[]? Image)> Blob(string? expression)
        {
            if (expression is null) return (null, null);
            var oid = await git.TryText(root, ct, "rev-parse", "--verify", "--quiet", "--end-of-options", expression);
            if (oid is null) return (null, null);
            var size = long.Parse(await git.Text(root, ct, "cat-file", "-s", oid), CultureInfo.InvariantCulture);
            if (size > imageLimit) { if (image) notice = "Image preview is limited to 8 MiB per side."; return (size, null); }
            return (size, image ? await git.Bytes(root, ct, ["cat-file", "blob", oid], new() { HideOutput = true }) : null);
        }
        (long? Size, byte[]? Image) before;
        (long? Size, byte[]? Image) after;
        if (request.Commit is not null)
        {
            var detail = await CommitMetadata(root, request.Commit, ct);
            before = await Blob(detail.Parents.Count > 0 ? detail.Parents[0] + ":" + request.Path : null);
            after = await Blob(detail.Hash + ":" + request.Path);
        }
        else if (request.Staged)
        {
            before = await Blob("HEAD:" + request.Path);
            after = await Blob(":" + request.Path);
        }
        else
        {
            before = await Blob(":" + request.Path);
            if (new FileInfo(full).LinkTarget is { } link)
                after = (Encoding.UTF8.GetByteCount(link), null);
            else if (File.Exists(full))
            {
                var size = new FileInfo(full).Length;
                if (size > imageLimit && image) notice = "Image preview is limited to 8 MiB per side.";
                after = (size, image && size <= imageLimit ? await File.ReadAllBytesAsync(full, ct) : null);
            }
            else after = (null, null);
        }
        return new(before.Size, after.Size, before.Image, after.Image, notice);
    }
}
