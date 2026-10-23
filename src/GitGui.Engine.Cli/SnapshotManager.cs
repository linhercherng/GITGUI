using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitGui.Core;

namespace GitGui.Engine.Cli;

internal sealed record IndexEntry(string Path, string Mode, string Oid, int Stage, uint Flags = 0);
internal sealed record SavedFile(string Path, string Mode, string Oid);
internal sealed record StashLogEntry(string Oid, string Name, string Email, string Date, string Message);
internal sealed record SparseSnapshot(List<KeyValuePair<string, string>> LocalConfig,
    List<KeyValuePair<string, string>> WorktreeConfig, string? PatternOid);
internal sealed record SnapshotManifest(string Root, string? HeadRef, string? HeadOid,
    Dictionary<string, string> Refs, List<IndexEntry> Index, List<SavedFile> Files,
    Dictionary<string, string> Markers, List<KeyValuePair<string, string>> BranchConfig,
    List<KeyValuePair<string, string>>? RemoteConfig = null, List<StashLogEntry>? StashLog = null,
    List<string>? StateDirectories = null, Dictionary<string, string>? StateModes = null,
    Dictionary<string, string>? OperationRefs = null, Dictionary<string, string>? SymbolicRefs = null,
    List<KeyValuePair<string, string>>? SubmoduleConfig = null, List<string>? ProtectedDirectories = null,
    SparseSnapshot? Sparse = null);

public sealed class SnapshotManager(GitProcessRunner runner, ISnapshotCatalog catalog)
{
    private readonly GitAccess git = new(runner);
    private static readonly string[] MarkerNames = ["MERGE_HEAD", "MERGE_MSG", "MERGE_MODE", "AUTO_MERGE", "ORIG_HEAD", "SQUASH_MSG",
        "CHERRY_PICK_HEAD", "REVERT_HEAD", "REBASE_HEAD", "MERGE_AUTOSTASH", "REBASE_AUTOSTASH", "MERGE_RR"];
    private static readonly string[] StateDirectoryNames = ["rebase-merge", "rebase-apply", "sequencer", "gitgui-rebase"];
    // Unlike MERGE_HEAD (a possibly multi-OID pseudoref), these refs can be
    // stored in reftable rather than in individual files.
    private static readonly string[] OperationRefNames = ["CHERRY_PICK_HEAD", "REVERT_HEAD", "REBASE_HEAD", "AUTO_MERGE",
        "ORIG_HEAD", "MERGE_AUTOSTASH", "REBASE_AUTOSTASH"];
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
        var statePaths = await ReadStatePaths(root, ct);
        var index = await ReadIndex(root, ct);
        var modes = index.GroupBy(e => e.Path).ToDictionary(g => g.Key, g => g.First().Mode, StringComparer.Ordinal);
        var refs = await ReadRefs(root, ct);
        var symbolicRefs = await ReadSymbolicRefs(root, ct);
        var headRef = await git.TryText(root, ct, "symbolic-ref", "--quiet", "HEAD");
        var headOid = await git.TryText(root, ct, "rev-parse", "--verify", "--quiet", "HEAD");
        var operationRefs = await ReadOperationRefs(root, ct);
        var paths = GitAccess.NullFields(await git.Bytes(root, ct,
            ["ls-files", "--cached", "--others", "--exclude-standard", "-z"]))
            .Concat(GitAccess.OverwriteCollisions(root, additionalPaths ?? [])).Select(p => p.TrimEnd('/'))
            .Distinct(StringComparer.Ordinal).ToArray();
        var protectedDirectories = ProtectedDirectories(root, index, paths);
        var files = new List<SavedFile>();
        var regular = new List<(string Path, string Mode)>();
        foreach (var path in paths)
        {
            if (protectedDirectories.Any(p => Within(path, p))) continue;
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
                throw new InvalidOperationException("An unexpected directory blocks a parent file snapshot: " + path);
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
        var stateModes = new Dictionary<string, string>();
        var stateDirectories = new List<string>();
        var stateObjects = new HashSet<string>(StringComparer.Ordinal);
        async Task SaveState(string relative, string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Symbolic Git state files are not supported.");
            var content = await File.ReadAllBytesAsync(path, ct);
            markers[relative] = await Hash(root, content, ct);
            stateModes[relative] = !OperatingSystem.IsWindows() &&
                (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0 ? "100755" : "100644";
            foreach (Match match in Regex.Matches(Encoding.UTF8.GetString(content), @"(?<![0-9a-f])[0-9a-f]{4,64}(?![0-9a-f])"))
                stateObjects.Add(match.Value);
        }
        foreach (var marker in MarkerNames)
        {
            var path = StatePath(marker, statePaths);
            if (new FileInfo(path).LinkTarget is not null)
                throw new InvalidOperationException("Symbolic Git state files are not supported.");
            if (Directory.Exists(path)) throw new InvalidOperationException("A Git state file became a directory.");
            if (!File.Exists(path)) continue;
            await SaveState(marker, path);
        }
        foreach (var name in StateDirectoryNames)
        {
            var directory = StatePath(name, statePaths);
            if (new DirectoryInfo(directory).LinkTarget is not null)
                throw new InvalidOperationException("Symbolic Git state directories are not supported.");
            if (File.Exists(directory)) throw new InvalidOperationException("A Git state directory became a file.");
            if (!Directory.Exists(directory)) continue;
            var pending = new Stack<(string Relative, string Full)>();
            pending.Push((name, directory));
            while (pending.TryPop(out var entry))
            {
                if ((File.GetAttributes(entry.Full) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Symbolic Git state directories are not supported.");
                stateDirectories.Add(entry.Relative);
                foreach (var child in Directory.EnumerateFileSystemEntries(entry.Full))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException("Symbolic Git state files are not supported.");
                    var relative = entry.Relative + "/" + Path.GetFileName(child);
                    if (Directory.Exists(child)) pending.Push((relative, child));
                    else await SaveState(relative, child);
                }
            }
        }
        var markerTargets = new List<string>();
        if (stateObjects.Count > 0)
        {
            var resolved = await git.Bytes(root, ct, ["cat-file", "--batch-check=%(objectname)"], new()
            { Input = Encoding.ASCII.GetBytes(string.Join("\n", stateObjects) + "\n"), HideOutput = true });
            markerTargets.AddRange(Encoding.ASCII.GetString(resolved).Split('\n').Where(IsOid));
        }
        var stashLog = await ReadStashLog(root, refs, ct);
        markerTargets.AddRange(stashLog.Select(s => s.Oid));
        markerTargets.AddRange(operationRefs.Values);
        var configuration = await ReadBranchConfig(root, ct);
        var remoteConfiguration = await ReadRemoteConfig(root, ct);
        var submoduleConfiguration = await ReadConfig(root, "^submodule\\.", "--local", ct);
        var sparse = await ReadSparse(root, ct);
        var manifest = new SnapshotManifest(root, headRef, headOid, refs, index, files, markers, configuration,
            remoteConfiguration, stashLog, stateDirectories, stateModes, operationRefs, symbolicRefs,
            submoduleConfiguration, protectedDirectories, sparse);
        var manifestOid = await Hash(root, JsonSerializer.SerializeToUtf8Bytes(manifest), ct);
        var roots = new List<string> { $"100644 blob {manifestOid}\tmanifest\0" };
        roots.Add($"040000 tree {await BuildTree(root, files.Select(f => new IndexEntry(f.Path, f.Mode, f.Oid, 0)), ct)}\tworktree\0");
        for (var stage = 0; stage <= 3; stage++)
            roots.Add($"040000 tree {await BuildTree(root, index.Where(e => e.Stage == stage), ct)}\tindex{stage}\0");
        var markerTree = await BuildTree(root, markers.Select(kv => new IndexEntry(kv.Key, "100644", kv.Value, 0)), ct);
        roots.Add($"040000 tree {markerTree}\tstate\0");
        if (sparse.PatternOid is not null) roots.Add($"100644 blob {sparse.PatternOid}\tsparse-patterns\0");
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
        if (!stashLog.SequenceEqual(await ReadStashLog(root, refs, ct)))
            throw new InvalidOperationException("Stash stack changed during snapshot. Retry after the other Git operation completes.");
        if (!operationRefs.OrderBy(p => p.Key).SequenceEqual((await ReadOperationRefs(root, ct)).OrderBy(p => p.Key)))
            throw new InvalidOperationException("Sequencer refs changed during snapshot. Retry after the other Git operation completes.");
        if (!symbolicRefs.OrderBy(p => p.Key).SequenceEqual((await ReadSymbolicRefs(root, ct)).OrderBy(p => p.Key)))
            throw new InvalidOperationException("Symbolic refs changed during snapshot. Retry after the other Git operation completes.");
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
        var currentIndex = await ReadIndex(root, ct);
        var statePaths = await ReadStatePaths(root, ct);
        ValidateStateStorage(statePaths);
        var currentRefs = await ReadRefs(root, ct);
        var currentOperationRefs = await ReadOperationRefs(root, ct);
        var savedOperationRefs = saved.OperationRefs ?? new Dictionary<string, string>();
        if (saved.OperationRefs is null)
            foreach (var (name, oid) in saved.Markers.Where(p => OperationRefNames.Contains(p.Key)))
            {
                var target = GitAccess.Utf8.GetString(await git.Bytes(root, ct, ["cat-file", "blob", oid], new() { HideOutput = true })).Trim();
                if (!IsOid(target)) throw new InvalidOperationException("Invalid saved operation reference.");
                savedOperationRefs[name] = target;
            }
        if (saved.RemoteConfig is null && saved.StashLog is null)
            currentRefs = currentRefs.Where(p => p.Key.StartsWith("refs/heads/", StringComparison.Ordinal) ||
                p.Key.StartsWith("refs/tags/", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        await CheckOtherWorktrees(root, currentRefs, saved.Refs, ct);
        var listedFiles = GitAccess.NullFields(await git.Bytes(root, ct,
            ["ls-files", "--cached", "--others", "--exclude-standard", "-z"]))
            .Concat(GitAccess.OverwriteCollisions(root, saved.Files.Select(f => f.Path))).Select(p => p.TrimEnd('/')).Distinct().ToArray();
        var protectedDirectories = ProtectedDirectories(root, currentIndex, listedFiles)
            .Concat(saved.ProtectedDirectories ?? []).Concat(saved.Index.Where(e => e.Mode == "160000").Select(e => e.Path)).Distinct().ToArray();
        if (GitAccess.OverwriteCollisions(root, saved.Index.Where(e => e.Mode == "160000").Select(e => e.Path)).Count > 0)
            throw new InvalidOperationException("A file blocks a saved submodule path; move it aside before parent restoration.");
        if (saved.Files.Any(f => protectedDirectories.Any(p => Within(f.Path, p) || Within(p, f.Path))))
            throw new InvalidOperationException("A child repository blocks parent restoration. Move it aside; child worktrees are never overwritten.");
        var currentFiles = listedFiles.Where(f => !protectedDirectories.Any(p => Within(f, p))).ToArray();
        if (saved.Sparse is not null) _ = await SparsePatternPath(root, ct);
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
        // Remote HEAD is commonly symbolic. Never dereference a ref transaction:
        // doing so could update/delete its target or update the same target twice.
        var refCommands = new StringBuilder();
        foreach (var (name, oid) in saved.Refs)
            refCommands.Append("option no-deref\n").Append(currentRefs.TryGetValue(name, out var old)
                ? $"update {name} {oid} {old}\n" : $"create {name} {oid}\n");
        foreach (var (name, oid) in currentRefs.Where(p => !saved.Refs.ContainsKey(p.Key)))
            refCommands.Append("option no-deref\n").Append($"delete {name} {oid}\n");
        foreach (var (name, oid) in savedOperationRefs)
            refCommands.Append("option no-deref\n").Append(currentOperationRefs.TryGetValue(name, out var old)
                ? $"update {name} {oid} {old}\n" : $"create {name} {oid}\n");
        foreach (var (name, oid) in currentOperationRefs.Where(p => !savedOperationRefs.ContainsKey(p.Key)))
            refCommands.Append("option no-deref\n").Append($"delete {name} {oid}\n");
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
            // Restore the captured sequencer state directly. Git's --quit commands can
            // move autostashes or discard state needed to redo an interrupted operation.
            await ClearState(root, statePaths, ct);
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
                    await git.Write(root, ct, ["clean", "-fdx", "--", file.Path], environment: cleanEnv);
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
            if (saved.RemoteConfig is not null)
            {
                foreach (var key in (await ReadRemoteConfig(root, ct)).Select(p => p.Key).Distinct())
                    await git.Write(root, ct, ["config", "--local", "--unset-all", key]);
                foreach (var item in saved.RemoteConfig)
                    await git.Write(root, ct, ["config", "--local", "--add", item.Key, item.Value]);
            }
            if (saved.SubmoduleConfig is not null)
            {
                foreach (var key in (await ReadConfig(root, "^submodule\\.", "--local", ct)).Select(p => p.Key).Distinct())
                    await git.Write(root, ct, ["config", "--local", "--unset-all", key]);
                foreach (var item in saved.SubmoduleConfig)
                    await git.Write(root, ct, ["config", "--local", "--add", item.Key, item.Value]);
            }
            if (saved.Sparse is not null) await RestoreSparse(root, saved.Sparse, ct);
            foreach (var directory in (saved.StateDirectories ?? []).OrderBy(p => p.Length))
                Directory.CreateDirectory(StatePath(directory, statePaths));
            foreach (var (marker, oid) in saved.Markers)
            {
                if (OperationRefNames.Contains(marker)) continue;
                var path = StatePath(marker, statePaths);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                    await git.Bytes(root, ct, ["cat-file", "blob", oid], new() { OutputStream = output, HideOutput = true });
                if (!OperatingSystem.IsWindows() && saved.StateModes?.GetValueOrDefault(marker) == "100755")
                    File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
            }
            if (saved.StashLog is not null) await RestoreStashLog(root, info.StateRef, saved, ct);
            foreach (var (name, target) in saved.SymbolicRefs ?? new Dictionary<string, string>())
                await git.Write(root, ct, ["symbolic-ref", name, target]);
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
            manifest.Index.Any(e => !IsOid(e.Oid) || e.Stage is < 0 or > 3 || e.Mode is not ("100644" or "100755" or "120000" or "160000")) ||
            manifest.Refs.Any(p => !IsOid(p.Value) || !SafeRef(p.Key) || !SnapshotRef(p.Key)) ||
            manifest.Markers.Any(p => !SafeStatePath(p.Key) || !IsOid(p.Value)) ||
            manifest.HeadOid is not null && !IsOid(manifest.HeadOid) || manifest.HeadRef is not null && !SafeRef(manifest.HeadRef) ||
            manifest.BranchConfig.Any(p => !p.Key.StartsWith("branch.", StringComparison.Ordinal) || p.Key.Any(char.IsControl)) ||
            manifest.RemoteConfig?.Any(p => !p.Key.StartsWith("remote.", StringComparison.Ordinal) || p.Key.Any(char.IsControl)) == true ||
            manifest.StateDirectories?.Any(p => !SafeStatePath(p, directory: true)) == true ||
            manifest.StateModes?.Any(p => !manifest.Markers.ContainsKey(p.Key) || p.Value is not ("100644" or "100755")) == true ||
            manifest.OperationRefs?.Any(p => !OperationRefNames.Contains(p.Key) || !IsOid(p.Value)) == true ||
            manifest.SymbolicRefs?.Any(p => !manifest.Refs.ContainsKey(p.Key) || !SafeRef(p.Key) || !SafeRef(p.Value)) == true ||
            manifest.SubmoduleConfig?.Any(p => !p.Key.StartsWith("submodule.", StringComparison.Ordinal) || p.Key.Any(char.IsControl)) == true ||
            manifest.Sparse is not null && (manifest.Sparse.PatternOid is not null && !IsOid(manifest.Sparse.PatternOid) ||
                manifest.Sparse.LocalConfig.Concat(manifest.Sparse.WorktreeConfig).Any(p => !SparseKey(p.Key))) ||
            manifest.StashLog?.Any(s => !IsOid(s.Oid) || !Regex.IsMatch(s.Date, @"^-?[0-9]+ [+-][0-9]{4}$") ||
                s.Name.Any(char.IsControl) || s.Email.Any(char.IsControl) || s.Message.Contains('\0')) == true ||
            manifest.StashLog is { Count: > 0 } && manifest.Refs.GetValueOrDefault("refs/stash") != manifest.StashLog[0].Oid)
            throw new InvalidOperationException("Snapshot metadata is not safe to restore.");
        foreach (var path in manifest.ProtectedDirectories ?? []) GitAccess.FullPath(root, path);
        return manifest;
    }

    private async Task CheckSupported(string root, CancellationToken ct)
    {
        foreach (var marker in new[] { "BISECT_LOG" })
        {
            var path = await git.Text(root, ct, "rev-parse", "--path-format=absolute", "--git-path", marker);
            if (File.Exists(path) || Directory.Exists(path)) throw new InvalidOperationException("Finish or abort the active " + marker + " operation first.");
        }
    }

    internal async Task<Dictionary<string, string>> ReadRefs(string root, CancellationToken ct)
    {
        var text = await git.Text(root, ct, "for-each-ref", "--format=%(refname)%00%(objectname)",
            "refs/heads", "refs/tags", "refs/remotes", "refs/stash", "refs/rewritten");
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd('\r').Split('\0'))
            .Where(p => SnapshotRef(p[0])).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, string>> ReadOperationRefs(string root, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var output = await git.Bytes(root, ct, ["cat-file", "--batch-check=%(objectname)"], new()
        { Input = Encoding.ASCII.GetBytes(string.Join("\n", OperationRefNames) + "\n"), HideOutput = true });
        var lines = Encoding.ASCII.GetString(output).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != OperationRefNames.Length) throw new InvalidOperationException("Cannot preserve operation references.");
        for (var n = 0; n < OperationRefNames.Length; n++)
        {
            if (lines[n] == OperationRefNames[n] + " missing") continue;
            if (!IsOid(lines[n])) throw new InvalidOperationException("Invalid operation reference: " + OperationRefNames[n]);
            result[OperationRefNames[n]] = lines[n];
        }
        return result;
    }

    private async Task<Dictionary<string, string>> ReadSymbolicRefs(string root, CancellationToken ct)
    {
        var output = await git.Text(root, ct, "for-each-ref", "--format=%(refname)%00%(symref)",
            "refs/heads", "refs/tags", "refs/remotes", "refs/stash", "refs/rewritten");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r').Split('\0'))
            .Where(p => SnapshotRef(p[0]) && p[1].Length > 0).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
    }

    private async Task<List<IndexEntry>> ReadIndex(string root, CancellationToken ct)
    {
        var entries = GitAccess.NullFields(await git.Bytes(root, ct, ["ls-files", "--stage", "-z"]))
            .Select(line => { var tab = line.IndexOf('\t'); var fields = line[..tab].Split(' ');
                return new IndexEntry(line[(tab + 1)..], fields[0], fields[1], int.Parse(fields[2], CultureInfo.InvariantCulture)); }).ToList();
        if (entries.GroupBy(e => e.Path).Any(g => g.Any(e => e.Mode == "160000") && g.Any(e => e.Mode != "160000")))
            throw new InvalidOperationException("Resolve the file/submodule type conflict before using parent snapshots.");
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

    private async Task<List<KeyValuePair<string, string>>> ReadRemoteConfig(string root, CancellationToken ct)
    {
        var result = await runner.RunAsync(root, ["config", "--local", "-z", "--get-regexp", "^remote\\."], ct);
        if (result.ExitCode == 1) return [];
        if (result.ExitCode != 0) throw new GitCommandException(result);
        return GitAccess.NullFields(result.Output).Select(item => { var at = item.IndexOf('\n');
            return new KeyValuePair<string, string>(item[..at], item[(at + 1)..]); }).ToList();
    }

    private static bool Within(string path, string directory) => path.Equals(directory, GitAccess.CollisionComparison) ||
        path.StartsWith(directory + "/", GitAccess.CollisionComparison);

    private static List<string> ProtectedDirectories(string root, IEnumerable<IndexEntry> index, IEnumerable<string> paths)
    {
        var result = index.Where(e => e.Mode == "160000").Select(e => e.Path)
            .ToHashSet(GitAccess.CollisionComparer);
        foreach (var path in result)
            if (File.Exists(GitAccess.FullPath(root, path)) || new FileInfo(GitAccess.FullPath(root, path)).LinkTarget is not null)
                throw new InvalidOperationException("A file occupies an indexed submodule path; move it aside before parent restoration.");
        foreach (var path in paths)
        {
            var full = GitAccess.FullPath(root, path);
            if (Directory.Exists(full) && (File.Exists(Path.Combine(full, ".git")) || Directory.Exists(Path.Combine(full, ".git"))))
            {
                if (new DirectoryInfo(full).LinkTarget is not null) throw new InvalidOperationException("Symbolic child repository directory.");
                result.Add(path);
            }
        }
        return result.ToList();
    }

    private const string SparseConfigPattern = "^(core\\.sparsecheckout(cone)?|index\\.sparse)$";
    private static bool SparseKey(string key) => key.Equals("core.sparsecheckout", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("core.sparsecheckoutcone", StringComparison.OrdinalIgnoreCase) || key.Equals("index.sparse", StringComparison.OrdinalIgnoreCase);

    private async Task<List<KeyValuePair<string, string>>> ReadConfig(string root, string pattern, string scope, CancellationToken ct)
    {
        var result = await runner.RunAsync(root, ["config", scope, "-z", "--get-regexp", pattern], ct);
        if (result.ExitCode == 1) return [];
        if (result.ExitCode != 0) throw new GitCommandException(result);
        return GitAccess.NullFields(result.Output).Select(item => { var at = item.IndexOf('\n');
            return new KeyValuePair<string, string>(item[..at], item[(at + 1)..]); }).ToList();
    }

    private async Task<string> SparsePatternPath(string root, CancellationToken ct)
    {
        var path = await git.Text(root, ct, "rev-parse", "--path-format=absolute", "--git-path", "info/sparse-checkout");
        if (new FileInfo(path).LinkTarget is not null || Directory.Exists(path) ||
            new DirectoryInfo(Path.GetDirectoryName(path)!).LinkTarget is not null)
            throw new InvalidOperationException("Unsafe sparse-checkout patterns path.");
        return path;
    }

    private async Task<SparseSnapshot> ReadSparse(string root, CancellationToken ct)
    {
        var local = await ReadConfig(root, SparseConfigPattern, "--local", ct);
        var worktree = await git.TryText(root, ct, "config", "--bool", "extensions.worktreeConfig") == "true"
            ? await ReadConfig(root, SparseConfigPattern, "--worktree", ct) : [];
        var path = await SparsePatternPath(root, ct);
        return new(local, worktree, File.Exists(path) ? await Hash(root, await File.ReadAllBytesAsync(path, ct), ct) : null);
    }

    private async Task RestoreSparse(string root, SparseSnapshot sparse, CancellationToken ct)
    {
        var worktreeConfig = await git.TryText(root, ct, "config", "--bool", "extensions.worktreeConfig") == "true";
        if (!worktreeConfig && sparse.WorktreeConfig.Count > 0)
        {
            await git.Write(root, ct, ["config", "--local", "extensions.worktreeConfig", "true"]);
            worktreeConfig = true;
        }
        // Retain Git's repository-format upgrade: disabling worktreeConfig can
        // invalidate sibling worktrees whose core settings Git migrated there.
        foreach (var scope in worktreeConfig ? new[] { "--local", "--worktree" } : ["--local"])
            foreach (var key in (await ReadConfig(root, SparseConfigPattern, scope, ct)).Select(p => p.Key).Distinct())
                await git.Write(root, ct, ["config", scope, "--unset-all", key]);
        foreach (var item in sparse.LocalConfig) await git.Write(root, ct, ["config", "--local", "--add", item.Key, item.Value]);
        foreach (var item in sparse.WorktreeConfig) await git.Write(root, ct, ["config", "--worktree", "--add", item.Key, item.Value]);
        var path = await SparsePatternPath(root, ct);
        if (sparse.PatternOid is null) { if (File.Exists(path)) File.Delete(path); }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            await git.Bytes(root, ct, ["cat-file", "blob", sparse.PatternOid], new() { OutputStream = output, HideOutput = true });
        }
    }

    private static bool SnapshotRef(string name) => name == "refs/stash" ||
        new[] { "refs/heads/", "refs/tags/", "refs/remotes/", "refs/rewritten/" }.Any(p => name.StartsWith(p, StringComparison.Ordinal));

    private static bool SafeStatePath(string name, bool directory = false)
    {
        if (!directory && MarkerNames.Contains(name)) return true;
        var parts = name.Split('/');
        return StateDirectoryNames.Contains(parts[0]) && (directory || parts.Length > 1) &&
            parts.All(p => p.Length > 0 && p is not ("." or "..") && !p.Equals(".git", StringComparison.OrdinalIgnoreCase) &&
                !p.Any(c => char.IsControl(c) || c is '\\' or ':'));
    }

    private async Task<Dictionary<string, string>> ReadStatePaths(string root, CancellationToken ct)
    {
        var names = MarkerNames.Concat(StateDirectoryNames).ToArray();
        var args = new List<string> { "rev-parse", "--path-format=absolute" };
        foreach (var name in names) args.AddRange(["--git-path", name]);
        var lines = (await git.Text(root, ct, args.ToArray())).Split('\n').Select(s => s.TrimEnd('\r')).ToArray();
        if (lines.Length != names.Length) throw new InvalidOperationException("Cannot resolve Git state directories safely.");
        return names.Select((name, n) => (name, path: Path.GetFullPath(lines[n])))
            .ToDictionary(p => p.name, p => p.path, StringComparer.Ordinal);
    }

    private static string StatePath(string relative, IReadOnlyDictionary<string, string> locations)
    {
        if (!SafeStatePath(relative) && !SafeStatePath(relative, directory: true))
            throw new InvalidOperationException("Unsafe Git state path.");
        var slash = relative.IndexOf('/');
        var name = slash < 0 ? relative : relative[..slash];
        var top = locations[name];
        return slash < 0 ? top : GitAccess.FullPath(top, relative[(slash + 1)..]);
    }

    private static void ValidateStateStorage(IReadOnlyDictionary<string, string> locations)
    {
        foreach (var name in MarkerNames.Concat(StateDirectoryNames))
        {
            var path = StatePath(name, locations);
            if (new FileInfo(path).LinkTarget is not null)
                throw new InvalidOperationException("Symbolic Git state files/directories are not supported.");
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.TryPop(out var entry))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Symbolic Git state files/directories are not supported.");
                if (Directory.Exists(entry))
                {
                    if (MarkerNames.Contains(name)) throw new InvalidOperationException("A Git state file became a directory.");
                    foreach (var child in Directory.EnumerateFileSystemEntries(entry)) pending.Push(child);
                }
                else if (entry == path && StateDirectoryNames.Contains(name))
                    throw new InvalidOperationException("A Git state directory became a file.");
            }
        }
    }

    private async Task ClearState(string root, IReadOnlyDictionary<string, string> locations, CancellationToken ct)
    {
        // Paths are resolved and checked before mutation; only these Git-owned
        // whitelisted directories can be recursively removed.
        var gitDirectory = await git.Text(root, ct, "rev-parse", "--absolute-git-dir");
        var commonDirectory = await git.Text(root, ct, "rev-parse", "--path-format=absolute", "--git-common-dir");
        foreach (var name in MarkerNames)
        {
            if (OperationRefNames.Contains(name)) continue;
            var path = StatePath(name, locations);
            if (File.Exists(path)) File.Delete(path);
        }
        foreach (var name in StateDirectoryNames)
        {
            var path = StatePath(name, locations);
            if (!GitAccess.SamePath(path, Path.Combine(gitDirectory, name)) &&
                !GitAccess.SamePath(path, Path.Combine(commonDirectory, name)))
                throw new InvalidOperationException("Git state directory escaped its metadata directory.");
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private async Task<List<StashLogEntry>> ReadStashLog(string root, IReadOnlyDictionary<string, string> refs, CancellationToken ct)
    {
        if (!refs.ContainsKey("refs/stash")) return [];
        var output = await git.Bytes(root, ct,
            ["log", "-g", "-z", "--date=raw", "--format=%H%x00%gn%x00%ge%x00%gD%x00%gs", "refs/stash", "--"],
            new() { HideOutput = true });
        if (output.Length == 0) return [];
        var fields = GitAccess.Utf8.GetString(output).Split('\0');
        if (fields[^1].Length == 0) fields = fields[..^1];
        if (fields.Length % 5 != 0) throw new InvalidOperationException("Cannot preserve the stash reflog.");
        var result = new List<StashLogEntry>();
        for (var n = 0; n < fields.Length; n += 5)
        {
            var selector = fields[n + 3];
            var at = selector.LastIndexOf("@{", StringComparison.Ordinal);
            if (at < 0 || !selector.EndsWith('}')) throw new InvalidOperationException("Cannot preserve stash reflog timestamps.");
            result.Add(new(fields[n], fields[n + 1], fields[n + 2], selector[(at + 2)..^1], fields[n + 4]));
        }
        return result;
    }

    private async Task RestoreStashLog(string root, string snapshotRef, SnapshotManifest saved, CancellationToken ct)
    {
        if (!saved.Refs.TryGetValue("refs/stash", out var current)) return;
        // Recreate through Git rather than editing the logs/ layout: this also
        // supports reftable. Ref updates compare the previous OID at every step.
        var exists = await runner.RunAsync(root, ["reflog", "exists", "refs/stash"], ct);
        if (exists.ExitCode == 0)
            await git.Write(root, ct, ["reflog", "expire", "--expire=all", "--expire-unreachable=all", "refs/stash"]);
        else if (exists.ExitCode != 1) throw new GitCommandException(exists);
        var sentinel = await git.Text(root, ct, "rev-parse", "--verify", snapshotRef);
        foreach (var entry in saved.StashLog!.AsEnumerable().Reverse())
        {
            var environment = new Dictionary<string, string?> {
                ["GIT_COMMITTER_NAME"] = entry.Name, ["GIT_COMMITTER_EMAIL"] = entry.Email,
                ["GIT_COMMITTER_DATE"] = "@" + entry.Date };
            var same = current == entry.Oid;
            if (same)
            {
                if (sentinel == current) throw new InvalidOperationException("Snapshot and stash object unexpectedly collide.");
                await git.Write(root, ct, ["update-ref", "--no-deref", "--create-reflog", "-m", "GITGUI temporary reflog entry", "refs/stash", sentinel, current], environment: environment);
                current = sentinel;
            }
            await git.Write(root, ct, ["update-ref", "--no-deref", "--create-reflog", "-m", entry.Message, "refs/stash", entry.Oid, current], environment: environment);
            current = entry.Oid;
            if (same) await git.Write(root, ct, ["reflog", "delete", "--rewrite", "refs/stash@{1}"]);
        }
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
