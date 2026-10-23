using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitGui.Core;

namespace GitGui.Engine.Cli;

internal sealed record IndexEntry(string Path, string Mode, string Oid, int Stage, uint Flags = 0);
internal sealed record SavedFile(string Path, string Mode, string Oid);
internal sealed record SnapshotManifest(string Root, string? HeadRef, string? HeadOid,
    Dictionary<string, string> Refs, List<IndexEntry> Index, List<SavedFile> Files,
    Dictionary<string, string> Markers, List<KeyValuePair<string, string>> BranchConfig);

public sealed class SnapshotManager(GitProcessRunner runner, ISnapshotCatalog catalog)
{
    private readonly GitAccess git = new(runner);
    private static readonly string[] MarkerNames = ["MERGE_HEAD", "MERGE_MSG", "MERGE_MODE", "AUTO_MERGE", "ORIG_HEAD", "SQUASH_MSG"];
    public int MinimumRetainedCount { get; set; } = 50;
    public TimeSpan MinimumRetainedAge { get; set; } = TimeSpan.FromDays(14);

    public async Task PruneAsync(string root, CancellationToken ct)
    {
        var all = (await catalog.ListAsync(root, ct)).OrderByDescending(s => s.CreatedAt).ToArray();
        foreach (var snapshot in all.Skip(Math.Max(1, MinimumRetainedCount))
            .Where(s => DateTimeOffset.UtcNow - s.CreatedAt > MinimumRetainedAge))
        {
            if (!Regex.IsMatch(snapshot.Id, "^[a-zA-Z0-9-]+$")) continue;
            var prefix = $"refs/gitgui/snapshots/{snapshot.Id}/";
            var refs = await git.Text(root, ct, "for-each-ref", "--format=%(refname)%00%(objectname)", prefix);
            var commands = new StringBuilder("start\n");
            foreach (var line in refs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split('\0');
                if (!fields[0].StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException("Invalid snapshot namespace.");
                commands.Append($"delete {fields[0]} {fields[1]}\n");
            }
            commands.Append("prepare\ncommit\n");
            await git.Write(root, ct, ["update-ref", "--stdin"], Encoding.UTF8.GetBytes(commands.ToString()));
            await catalog.RemoveAsync(snapshot.Id, ct);
        }
    }

    public async Task<SnapshotInfo> CreateAsync(string root, string title, CancellationToken ct,
        IReadOnlyList<string>? additionalPaths = null)
    {
        root = await git.Root(root, ct);
        await CheckSupported(root, ct);
        await using var indexLock = await RepositoryIndexLock.Acquire(git, root, ct);
        var index = await ReadIndex(root, ct);
        var modes = index.GroupBy(e => e.Path).ToDictionary(g => g.Key, g => g.First().Mode, StringComparer.Ordinal);
        var refs = await ReadRefs(root, ct);
        var headRef = await git.TryText(root, ct, "symbolic-ref", "--quiet", "HEAD");
        var headOid = await git.TryText(root, ct, "rev-parse", "--verify", "--quiet", "HEAD");
        var paths = GitAccess.NullFields(await git.Bytes(root, ct,
            ["ls-files", "--cached", "--others", "--exclude-standard", "-z"]))
            .Concat(additionalPaths ?? []).Distinct(StringComparer.Ordinal).ToArray();
        var files = new List<SavedFile>();
        var regular = new List<(string Path, string Mode)>();
        foreach (var path in paths)
        {
            var full = GitAccess.FullPath(root, path);
            var info = new FileInfo(full);
            var link = info.LinkTarget;
            if (link is not null)
                files.Add(new(path, "120000", await Hash(root, Encoding.UTF8.GetBytes(link), ct)));
            else if (File.Exists(full))
            {
                var mode = OperatingSystem.IsWindows()
                    ? modes.GetValueOrDefault(path) is "100755" ? "100755" : "100644"
                    : (File.GetUnixFileMode(full) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0
                        ? "100755" : "100644";
                regular.Add((path, mode));
            }
            else if (Directory.Exists(full))
                throw new InvalidOperationException("Nested repositories/submodules cannot be safely snapshotted in M1: " + path);
        }
        if (regular.Count > 0)
        {
            var output = await git.Bytes(root, ct, ["hash-object", "-w", "--no-filters", "--stdin-paths"],
                new() { IsReadOnly = false, HideOutput = true,
                    Input = Encoding.UTF8.GetBytes(string.Join("\n", regular.Select(f => GitAccess.QuotePath(f.Path))) + "\n") });
            var oids = GitAccess.Utf8.GetString(output).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (oids.Length != regular.Count) throw new InvalidOperationException("Snapshot object count mismatch.");
            for (var n = 0; n < regular.Count; n++) files.Add(new(regular[n].Path, regular[n].Mode, oids[n].Trim()));
        }
        var markers = new Dictionary<string, string>();
        var markerTargets = new List<string>();
        foreach (var marker in MarkerNames)
        {
            var path = await git.Text(root, ct, "rev-parse", "--path-format=absolute", "--git-path", marker);
            if (!File.Exists(path)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Symbolic Git state files are not supported.");
            var content = await File.ReadAllBytesAsync(path, ct);
            markers[marker] = await Hash(root, content, ct);
            if (marker is "MERGE_HEAD" or "AUTO_MERGE" or "ORIG_HEAD")
                markerTargets.AddRange(Encoding.UTF8.GetString(content).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Where(IsOid));
        }
        var configuration = await ReadBranchConfig(root, ct);
        var manifest = new SnapshotManifest(root, headRef, headOid, refs, index, files, markers, configuration);
        var manifestOid = await Hash(root, JsonSerializer.SerializeToUtf8Bytes(manifest), ct);
        var roots = new List<string> { $"100644 blob {manifestOid}\tmanifest\0" };
        roots.Add($"040000 tree {await BuildTree(root, files.Select(f => new IndexEntry(f.Path, f.Mode, f.Oid, 0)), ct)}\tworktree\0");
        for (var stage = 0; stage <= 3; stage++)
            roots.Add($"040000 tree {await BuildTree(root, index.Where(e => e.Stage == stage), ct)}\tindex{stage}\0");
        var markerTree = await BuildTree(root, markers.Select(kv => new IndexEntry(kv.Key, "100644", kv.Value, 0)), ct);
        roots.Add($"040000 tree {markerTree}\tstate\0");
        var tree = GitAccess.Utf8.GetString(await git.Bytes(root, ct, ["mktree", "-z"],
            new() { IsReadOnly = false, Input = Encoding.UTF8.GetBytes(string.Concat(roots)), HideOutput = true })).Trim();
        var id = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        var commit = GitAccess.Utf8.GetString(await git.Bytes(root, ct, ["-c", "commit.gpgsign=false", "commit-tree", tree],
            new() { IsReadOnly = false, HideOutput = true, Input = Encoding.UTF8.GetBytes("GITGUI snapshot " + id + "\n"),
                Environment = new Dictionary<string, string?> {
                    ["GIT_AUTHOR_NAME"] = "GITGUI", ["GIT_AUTHOR_EMAIL"] = "snapshot@localhost",
                    ["GIT_COMMITTER_NAME"] = "GITGUI", ["GIT_COMMITTER_EMAIL"] = "snapshot@localhost" } })).Trim();
        var prefix = "refs/gitgui/snapshots/" + id;
        var transaction = new StringBuilder("start\n").AppendLine($"create {prefix}/state {commit}");
        foreach (var (name, oid) in refs) transaction.AppendLine($"create {prefix}/saved/{name[5..]} {oid}");
        foreach (var (oid, n) in markerTargets.Append(headOid).Where(s => s is not null).Distinct().Select((s, n) => (s, n)))
            transaction.AppendLine($"create {prefix}/objects/{n} {oid}");
        transaction.Append("prepare\ncommit\n");
        // Verify refs did not change while the objects were captured.
        if (!refs.OrderBy(p => p.Key).SequenceEqual((await ReadRefs(root, ct)).OrderBy(p => p.Key)))
            throw new InvalidOperationException("Repository refs changed during snapshot. Retry after the other Git operation completes.");
        await git.Write(root, ct, ["update-ref", "--stdin"], Encoding.UTF8.GetBytes(transaction.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)));
        var snapshot = new SnapshotInfo(id, root, title, DateTimeOffset.UtcNow, prefix + "/state", headOid, files.Count);
        await catalog.SaveAsync(snapshot, ct);
        return snapshot;
    }

    public async Task<IReadOnlyList<string>> RestorePathsAsync(string root, SnapshotInfo info, CancellationToken ct)
        => (await ReadManifest(root, info, ct)).Files.Select(f => f.Path).ToArray();

    public async Task RestoreAsync(string root, SnapshotInfo info, CancellationToken ct)
    {
        root = await git.Root(root, ct);
        await CheckSupported(root, ct);
        var saved = await ReadManifest(root, info, ct);
        await using var indexLock = await RepositoryIndexLock.Acquire(git, root, ct);
        var currentRefs = await ReadRefs(root, ct);
        await CheckOtherWorktrees(root, currentRefs, saved.Refs, ct);
        var currentFiles = GitAccess.NullFields(await git.Bytes(root, ct,
            ["ls-files", "--cached", "--others", "--exclude-standard", "-z"])).Distinct().ToArray();
        var currentSet = currentFiles.ToHashSet(StringComparer.Ordinal);
        var savedSet = saved.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var file in saved.Files)
        {
            var full = GitAccess.FullPath(root, file.Path);
            if (!Directory.Exists(full) || new DirectoryInfo(full).LinkTarget is not null) continue;
            if (Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
                .Any(p => !currentSet.Contains(Path.GetRelativePath(root, p).Replace('\\', '/'))))
                throw new InvalidOperationException("An ignored file blocks snapshot restoration: " + file.Path);
        }
        var refCommands = new StringBuilder();
        foreach (var (name, oid) in saved.Refs)
            refCommands.Append(currentRefs.TryGetValue(name, out var old)
                ? $"update {name} {oid} {old}\n" : $"create {name} {oid}\n");
        foreach (var (name, oid) in currentRefs.Where(p => !saved.Refs.ContainsKey(p.Key)))
            refCommands.Append($"delete {name} {oid}\n");
        // Acquire all ref locks before touching a file. Namespace conflicts also fail here.
        await using var refLock = await PreparedRefs.Begin(root, refCommands.ToString(), runner, ct);
        var generatedIndex = Path.Combine(Path.GetTempPath(), "gitgui-restore-index-" + Guid.NewGuid().ToString("N"));
        var cleanupIndex = Path.Combine(Path.GetTempPath(), "gitgui-clean-index-" + Guid.NewGuid().ToString("N"));
        var env = new Dictionary<string, string?> { ["GIT_INDEX_FILE"] = generatedIndex };
        var cleanEnv = new Dictionary<string, string?> { ["GIT_INDEX_FILE"] = cleanupIndex };
        try
        {
            await git.Write(root, ct, ["read-tree", "--empty"], environment: cleanEnv);
            // No cancellation after mutation starts. Caller already created the redo snapshot.
            ct.ThrowIfCancellationRequested();
            ct = CancellationToken.None;
            await git.Write(root, ct, ["merge", "--quit"]);
            foreach (var batch in currentFiles.Where(p => !savedSet.Contains(p)).Chunk(100))
            {
                foreach (var path in batch)
                    if (Directory.Exists(GitAccess.FullPath(root, path)) && new DirectoryInfo(GitAccess.FullPath(root, path)).LinkTarget is null)
                        throw new InvalidOperationException("A tracked path became a directory during restoration.");
                await git.Write(root, ct, ["clean", "-f", "-x", "--", .. batch], environment: cleanEnv);
            }
            foreach (var file in saved.Files)
            {
                var path = GitAccess.FullPath(root, file.Path);
                if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null)
                    await git.Write(root, ct, ["clean", "-fd", "--", file.Path], environment: cleanEnv);
            }
            await Materialize(root, saved.Files, ct);
            await git.Write(root, ct, ["read-tree", "--empty"], environment: env);
            var entries = saved.Index.Where(e => (e.Flags & 0x20000000) == 0).ToArray();
            if (entries.Length > 0)
                await git.Write(root, ct, ["update-index", "-z", "--index-info"], Encoding.UTF8.GetBytes(string.Concat(entries.Select(IndexLine))), env);
            foreach (var entry in saved.Index.Where(e => (e.Flags & 0x20000000) != 0))
                await git.Write(root, ct, ["add", "--intent-to-add", "--", entry.Path], environment: env);
            foreach (var group in saved.Index.Where(e => e.Stage == 0).GroupBy(e => e.Flags))
            {
                if ((group.Key & 0x8000) != 0)
                    await git.Write(root, ct, ["update-index", "--assume-unchanged", "--", .. group.Select(e => e.Path)], environment: env);
                if ((group.Key & 0x40000000) != 0)
                    await git.Write(root, ct, ["update-index", "--skip-worktree", "--", .. group.Select(e => e.Path)], environment: env);
            }
            await refLock.Commit();
            if (saved.HeadRef is not null) await git.Write(root, ct, ["symbolic-ref", "HEAD", saved.HeadRef]);
            else if (saved.HeadOid is not null) await git.Write(root, ct, ["update-ref", "--no-deref", "HEAD", saved.HeadOid]);
            foreach (var key in (await ReadBranchConfig(root, ct)).Select(p => p.Key).Distinct())
                await git.Write(root, ct, ["config", "--local", "--unset-all", key]);
            foreach (var item in saved.BranchConfig)
                await git.Write(root, ct, ["config", "--local", "--add", item.Key, item.Value]);
            foreach (var marker in MarkerNames)
            {
                var path = await git.Text(root, ct, "rev-parse", "--path-format=absolute", "--git-path", marker);
                if (saved.Markers.TryGetValue(marker, out var oid))
                {
                    await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                    await git.Bytes(root, ct, ["cat-file", "blob", oid], new() { OutputStream = output, HideOutput = true });
                }
                else if (File.Exists(path)) File.Delete(path);
            }
            await indexLock.Install(generatedIndex, ct);
        }
        finally
        {
            File.Delete(generatedIndex); File.Delete(generatedIndex + ".lock");
            File.Delete(cleanupIndex); File.Delete(cleanupIndex + ".lock");
        }
    }

    private async Task<SnapshotManifest> ReadManifest(string root, SnapshotInfo info, CancellationToken ct)
    {
        if (!Regex.IsMatch(info.Id, "^[a-zA-Z0-9-]+$") || info.StateRef != $"refs/gitgui/snapshots/{info.Id}/state")
            throw new InvalidOperationException("Invalid snapshot reference.");
        var content = await git.Bytes(root, ct, ["cat-file", "blob", info.StateRef + ":manifest"], new() { HideOutput = true });
        var manifest = JsonSerializer.Deserialize<SnapshotManifest>(content) ?? throw new InvalidOperationException("Invalid snapshot metadata.");
        if (!GitAccess.SamePath(manifest.Root, root)) throw new InvalidOperationException("Snapshot belongs to a different worktree.");
        foreach (var path in manifest.Files.Select(f => f.Path).Concat(manifest.Index.Select(e => e.Path))) GitAccess.FullPath(root, path);
        if (manifest.Files.Select(f => f.Path).Distinct().Count() != manifest.Files.Count ||
            manifest.Files.Any(f => !IsOid(f.Oid) || f.Mode is not ("100644" or "100755" or "120000")) ||
            manifest.Index.Any(e => !IsOid(e.Oid) || e.Stage is < 0 or > 3 || e.Mode is not ("100644" or "100755" or "120000")) ||
            manifest.Refs.Any(p => !IsOid(p.Value) || !SafeRef(p.Key) || !(p.Key.StartsWith("refs/heads/", StringComparison.Ordinal) || p.Key.StartsWith("refs/tags/", StringComparison.Ordinal))) ||
            manifest.Markers.Any(p => !MarkerNames.Contains(p.Key) || !IsOid(p.Value)) ||
            manifest.HeadOid is not null && !IsOid(manifest.HeadOid) || manifest.HeadRef is not null && !SafeRef(manifest.HeadRef) ||
            manifest.BranchConfig.Any(p => !p.Key.StartsWith("branch.", StringComparison.Ordinal) || p.Key.Any(char.IsControl)))
            throw new InvalidOperationException("Snapshot metadata is not safe to restore.");
        return manifest;
    }

    private async Task CheckSupported(string root, CancellationToken ct)
    {
        foreach (var marker in new[] { "rebase-merge", "rebase-apply", "CHERRY_PICK_HEAD", "REVERT_HEAD", "BISECT_LOG", "MERGE_AUTOSTASH" })
        {
            var path = await git.Text(root, ct, "rev-parse", "--path-format=absolute", "--git-path", marker);
            if (File.Exists(path) || Directory.Exists(path)) throw new InvalidOperationException("Finish or abort the active " + marker + " operation first.");
        }
        if (await git.TryText(root, ct, "config", "--bool", "core.sparseCheckout") == "true")
            throw new InvalidOperationException("M1 snapshot writes are disabled for sparse checkouts.");
    }

    internal async Task<Dictionary<string, string>> ReadRefs(string root, CancellationToken ct)
    {
        var text = await git.Text(root, ct, "for-each-ref", "--format=%(refname)%00%(objectname)", "refs/heads", "refs/tags");
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd('\r').Split('\0'))
            .ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
    }

    private async Task<List<IndexEntry>> ReadIndex(string root, CancellationToken ct)
    {
        var entries = GitAccess.NullFields(await git.Bytes(root, ct, ["ls-files", "--stage", "-z"]))
            .Select(line => { var tab = line.IndexOf('\t'); var fields = line[..tab].Split(' ');
                return new IndexEntry(line[(tab + 1)..], fields[0], fields[1], int.Parse(fields[2], CultureInfo.InvariantCulture)); }).ToList();
        if (entries.Any(e => e.Mode == "160000")) throw new InvalidOperationException("Submodule snapshots are scheduled for P2; this write is blocked.");
        var debug = GitAccess.Utf8.GetString(await git.Bytes(root, ct, ["ls-files", "--debug", "-z"]));
        var flags = new Dictionary<string, uint>();
        var offset = 0;
        var flagPattern = new Regex(@"flags: ([0-9a-fA-F]+)\r?\n", RegexOptions.CultureInvariant);
        while (offset < debug.Length)
        {
            var end = debug.IndexOf('\0', offset);
            if (end < 0) break;
            var path = debug[offset..end];
            var match = flagPattern.Match(debug, end + 1);
            if (!match.Success) throw new FormatException("Cannot preserve index flags.");
            flags[path] = uint.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            offset = match.Index + match.Length;
        }
        return entries.Select(e => e with { Flags = flags.GetValueOrDefault(e.Path) }).ToList();
    }

    private async Task<string> Hash(string root, byte[] data, CancellationToken ct)
        => GitAccess.Utf8.GetString(await git.Bytes(root, ct, ["hash-object", "-w", "--stdin"],
            new() { IsReadOnly = false, Input = data, HideOutput = true })).Trim();

    private async Task<string> BuildTree(string root, IEnumerable<IndexEntry> source, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "gitgui-index-" + Guid.NewGuid().ToString("N"));
        var env = new Dictionary<string, string?> { ["GIT_INDEX_FILE"] = temp };
        try
        {
            await git.Write(root, ct, ["-c", "core.splitIndex=false", "read-tree", "--empty"], environment: env);
            var entries = source.Select(e => e with { Stage = 0 }).ToArray();
            if (entries.Length > 0)
                await git.Write(root, ct, ["update-index", "-z", "--index-info"], Encoding.UTF8.GetBytes(string.Concat(entries.Select(IndexLine))), env);
            return GitAccess.Utf8.GetString(await git.Bytes(root, ct, ["write-tree"], new() { IsReadOnly = false, Environment = env, HideOutput = true })).Trim();
        }
        finally { File.Delete(temp); File.Delete(temp + ".lock"); }
    }

    private static string IndexLine(IndexEntry e) => $"{e.Mode} {e.Oid} {e.Stage}\t{e.Path}\0";
    private static bool IsOid(string text) => Regex.IsMatch(text, "^(?:[0-9a-f]{40}|[0-9a-f]{64})$");
    private static bool SafeRef(string text) => text.StartsWith("refs/", StringComparison.Ordinal) &&
        !text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\') &&
        !text.Contains("..", StringComparison.Ordinal) && !text.Contains("@{", StringComparison.Ordinal) &&
        !text.Contains("//", StringComparison.Ordinal) && !text.EndsWith('/') && !text.EndsWith('.') &&
        text.Split('/').All(part => !part.StartsWith('.') && !part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));

    private async Task<List<KeyValuePair<string, string>>> ReadBranchConfig(string root, CancellationToken ct)
    {
        var result = await runner.RunAsync(root, ["config", "--local", "-z", "--get-regexp", "^branch\\."], ct);
        if (result.ExitCode == 1) return [];
        if (result.ExitCode != 0) throw new GitCommandException(result);
        return GitAccess.NullFields(result.Output).Select(item => { var at = item.IndexOf('\n');
            return new KeyValuePair<string, string>(item[..at], item[(at + 1)..]); }).ToList();
    }

    private async Task CheckOtherWorktrees(string root, Dictionary<string, string> current,
        Dictionary<string, string> desired, CancellationToken ct)
    {
        var tokens = GitAccess.NullFields(await git.Bytes(root, ct, ["worktree", "list", "--porcelain", "-z"]));
        string? worktree = null;
        foreach (var token in tokens)
        {
            if (token.StartsWith("worktree ", StringComparison.Ordinal)) worktree = token[9..];
            if (!token.StartsWith("branch ", StringComparison.Ordinal) || worktree is null || GitAccess.SamePath(root, worktree)) continue;
            var branch = token[7..];
            if (current.GetValueOrDefault(branch) != desired.GetValueOrDefault(branch))
                throw new InvalidOperationException("Snapshot changes a branch checked out in another worktree: " + branch);
        }
    }

    private async Task Materialize(string root, IReadOnlyList<SavedFile> files, CancellationToken ct)
    {
        var regular = files.Where(f => f.Mode != "120000").ToArray();
        if (regular.Length > 0)
        {
            // One Git process streams all raw blobs; memory stays bounded even for large binaries.
            await git.Bytes(root, ct, ["cat-file", "--batch"], new()
            {
                Input = Encoding.ASCII.GetBytes(string.Join("\n", regular.Select(f => f.Oid)) + "\n"), HideOutput = true,
                OutputConsumer = async (stream, token) =>
                {
                    using var buffered = new BufferedStream(stream, 65536);
                    var buffer = new byte[65536];
                    foreach (var file in regular)
                    {
                        var header = new StringBuilder();
                        int value;
                        while ((value = buffered.ReadByte()) != '\n')
                        {
                            if (value < 0 || header.Length > 200) throw new IOException("Truncated Git blob stream.");
                            header.Append((char)value);
                        }
                        var fields = header.ToString().Split(' ');
                        if (fields.Length != 3 || fields[0] != file.Oid || fields[1] != "blob" ||
                            !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 0)
                            throw new IOException("Invalid Git blob stream header.");
                        var path = GitAccess.FullPath(root, file.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        var temporary = path + ".gitgui-" + Guid.NewGuid().ToString("N");
                        try
                        {
                            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            {
                                while (length > 0)
                                {
                                    var count = await buffered.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), token);
                                    if (count == 0) throw new IOException("Truncated Git blob data.");
                                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                                    length -= count;
                                }
                            }
                            if (buffered.ReadByte() != '\n') throw new IOException("Invalid Git blob separator.");
                            File.Move(temporary, path, true);
                            if (!OperatingSystem.IsWindows())
                                File.SetUnixFileMode(path, file.Mode == "100755"
                                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                                    : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                        }
                        finally { File.Delete(temporary); }
                    }
                }
            });
        }
        foreach (var file in files.Where(f => f.Mode == "120000"))
        {
            var path = GitAccess.FullPath(root, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var link = GitAccess.Utf8.GetString(await git.Bytes(root, ct, ["cat-file", "blob", file.Oid], new() { HideOutput = true }));
            // Create the new link first, so privilege errors cannot remove an existing file.
            var temporary = path + ".gitgui-" + Guid.NewGuid().ToString("N");
            try { File.CreateSymbolicLink(temporary, link); File.Move(temporary, path, true); }
            finally { File.Delete(temporary); }
        }
    }
}
