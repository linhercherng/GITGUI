using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using GitGui.Core;

namespace GitGui.Engine.Cli;

internal sealed partial class WorkspaceReader
{
    private readonly SemaphoreSlim historyGate = new(1, 1);
    private readonly Dictionary<string, HistoryCache> historyCache = [];
    private readonly SemaphoreSlim detailsGate = new(1, 1);
    private readonly Dictionary<(string Root, string Oid), CommitDetails> detailsCache = [];
    private readonly Dictionary<(string Root, string Oid), CommitInfo> metadataCache = [];
    private readonly SemaphoreSlim metadataGate = new(1, 1);
    private readonly ConcurrentDictionary<string, RepoStatus> readStatuses = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AdminDescriptor> descriptors = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private sealed record AdminDescriptor(string Root, string GitDir, string CommonDir, RepoFormat Format, string Fingerprint);
    private sealed class HistoryCache
    {
        public List<CommitInfo> Items { get; } = [];
        public int Offset { get; set; }
        public long Characters { get; set; }
        public bool Complete { get; set; }
    }
    private static List<byte[]> RawFields(byte[] bytes)
    {
        var result = new List<byte[]>(); var offset = 0;
        while (offset < bytes.Length)
        {
            var end = Array.IndexOf(bytes, (byte)0, offset);
            if (end < 0) throw new FormatException("Truncated path record.");
            result.Add(bytes.AsSpan(offset, end - offset).ToArray()); offset = end + 1;
        }
        return result;
    }
    private void ValidateReadablePath(string root, string path)
    {
        var status = readStatuses.GetValueOrDefault(GitAccess.NormalizeSystemPath(root));
        GitPathPolicy.ValidateSelection(status?.Files ?? [], [path]);
    }
    private void RememberStatus(string root, RepoStatus status)
    {
        readStatuses[root] = status;
        foreach (var other in readStatuses.Keys.Where(k => !GitAccess.SamePath(k, root)))
        {
            if (readStatuses.Count <= 2) break;
            readStatuses.TryRemove(other, out _);
        }
    }
    private static string[] VisibleTips(IReadOnlyList<RefInfo> refs, RepoStatus status) => refs.Select(r => r.Target)
        .Concat(status.Head is not (null or "(initial)") ? new[] { status.Head } : [])
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string Fingerprint(AdminDescriptor descriptor)
    {
        return string.Join(';', new[] { Path.Combine(descriptor.Root, ".git"), Path.Combine(descriptor.GitDir, "commondir"),
            Path.Combine(descriptor.CommonDir, "config"), Path.Combine(descriptor.GitDir, "config.worktree"),
            Path.Combine(descriptor.CommonDir, "shallow"), Path.Combine(descriptor.CommonDir, "info", "grafts") }
            .Select(p => { var file = new FileInfo(p); return file.Exists ? file.Length + ":" + file.LastWriteTimeUtc.Ticks : "missing"; }));
    }

    private async Task<AdminDescriptor> Descriptor(string path, CancellationToken ct)
    {
        var full = GitAccess.NormalizeSystemPath(Path.GetFullPath(path));
        if (descriptors.TryGetValue(full, out var cached) && Directory.Exists(cached.Root) && cached.Fingerprint == Fingerprint(cached)) return cached;
        var root = await git.Root(path, ct);
        var directories = (await git.Text(root, ct, "rev-parse", "--path-format=absolute", "--absolute-git-dir", "--git-common-dir")).Split('\n');
        var descriptor = new AdminDescriptor(root, directories[0].TrimEnd('\r'), directories[1].TrimEnd('\r'), await statusReader.GetFormatAsync(root, ct), "");
        descriptor = descriptor with { Fingerprint = Fingerprint(descriptor) };
        descriptors[full] = descriptor; descriptors[root] = descriptor;
        return descriptor;
    }

    public async Task<WorkspaceStatus> Status(string root, CancellationToken ct)
    {
        root = (await Descriptor(root, ct)).Root;
        var status = await statusReader.GetStatusAsync(root, await settings.IsTrustedAsync(root, ct), ct);
        RememberStatus(root, status);
        return new(root, status, await InProgress(root, status, ct, presentation: true));
    }

    public async Task<HistoryPage> Page(string root, LogFilter filter, string? generation, CancellationToken ct)
    {
        var descriptor = await Descriptor(root, ct); root = descriptor.Root;
        var refs = await Refs(root, ct);
        var head = await Head(descriptor, refs, ct);
        return await History(root, filter, new("", head ?? "(initial)", []), refs, generation, ct);
    }
    private async Task<string?> Head(AdminDescriptor descriptor, IReadOnlyList<RefInfo> refs, CancellationToken ct)
    {
        var headFile = Path.Combine(descriptor.GitDir, "HEAD");
        var headValue = File.Exists(headFile) ? (await File.ReadAllTextAsync(headFile, ct)).Trim() : "";
        var head = headValue.StartsWith("ref: ", StringComparison.Ordinal) ? refs.FirstOrDefault(r => r.FullName == headValue[5..])?.Target :
            System.Text.RegularExpressions.Regex.IsMatch(headValue, "^[0-9a-f]{40}([0-9a-f]{24})?$") ? headValue :
            await git.TryText(descriptor.Root, ct, "rev-parse", "--verify", "--quiet", "HEAD");
        return head;
    }

    private async Task<HistoryPage> History(string root, LogFilter filter, RepoStatus status,
        IReadOnlyList<RefInfo> refs, string? expectedGeneration, CancellationToken ct)
    {
        var start = await runner.CreateStartInfoAsync(root, ct);
        var descriptor = await Descriptor(root, ct);
        var identity = root + "\0" + start.FileName + "\0" + File.GetLastWriteTimeUtc(start.FileName).Ticks + "\0" + status.Head + "\0" +
            string.Join('\0', refs.OrderBy(r => r.FullName, StringComparer.Ordinal).Select(r => r.FullName + "=" + r.Target)) + "\0" +
            descriptor.Format + "\0" + descriptor.Fingerprint + "\0" + RepositoryObjectView.Read(root).Fingerprint + "\0" +
            string.Join('\0', filter.Search, filter.Author, filter.Path, filter.Since, filter.Until);
        var generation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var reset = expectedGeneration is not null && expectedGeneration != generation;
        var skip = reset ? 0 : Math.Max(0, filter.Skip);
        var take = reset ? 500 : Math.Clamp(filter.Take, 1, 10_000);
        if ((long)skip + take + 1 > 100_000)
        {
            var values = await Log(root, filter with { Skip = skip, Take = take + 1 }, status, ct, refs);
            var items = values.Take(take).ToArray();
            return new(items, generation, skip + items.Length, values.Count > take, reset);
        }
        await historyGate.WaitAsync(ct);
        try
        {
            if (historyCache.TryGetValue(generation, out var previous) && previous.Offset > skip)
                historyCache.Remove(generation);
            if (!historyCache.TryGetValue(generation, out var cached))
            {
                if (historyCache.Count >= 4) historyCache.Clear();
                historyCache[generation] = cached = new();
            }
            var required = Math.Min(100_000, skip + take + 1);
            if (!cached.Complete && (long)cached.Offset + cached.Items.Count < required)
            {
                var requested = required - cached.Offset - cached.Items.Count;
                var values = status.Head == "(initial)" && refs.Count == 0 ? [] :
                    await Log(root, filter with { Skip = cached.Offset + cached.Items.Count, Take = requested }, status, ct, refs);
                ct.ThrowIfCancellationRequested();
                var characters = values.Sum(c => (long)c.Subject.Length + c.Author.Length);
                foreach (var other in historyCache.Keys.Where(k => k != generation).ToArray())
                {
                    if (historyCache.Values.Sum(c => c.Items.Count) + values.Count <= 100_000 &&
                        historyCache.Values.Sum(c => c.Characters) + characters <= 8_000_000) break;
                    historyCache.Remove(other);
                }
                cached.Items.AddRange(values);
                cached.Characters += characters;
                cached.Complete = values.Count < requested;
            }
            var page = cached.Items.Skip(skip - cached.Offset).Take(take).ToArray();
            var more = cached.Offset + cached.Items.Count > skip + page.Length || !cached.Complete;
            var remove = 0;
            while (cached.Characters > 8_000_000 && remove < cached.Items.Count && cached.Offset + remove < skip)
            {
                var item = cached.Items[remove++]; cached.Characters -= (long)item.Subject.Length + item.Author.Length;
            }
            if (remove > 0) { cached.Items.RemoveRange(0, remove); cached.Offset += remove; }
            if (cached.Characters > 8_000_000) historyCache.Remove(generation);
            return new(page, generation, skip + page.Length, more, reset);
        }
        finally { historyGate.Release(); }
    }

    public async Task<CommitDetails> Commit(string root, string revision, CancellationToken ct)
    {
        var oid = await Resolve(root, revision, ct);
        var key = (root, oid + "\0" + RepositoryObjectView.Read(root).Fingerprint);
        await detailsGate.WaitAsync(ct);
        try
        {
            if (detailsCache.TryGetValue(key, out var cached)) return cached;
            var result = await ReadCommit(root, oid, ct);
            ct.ThrowIfCancellationRequested();
            var characters = (long)result.Message.Length + result.Commit.Subject.Length;
            if (result.Files.Count <= 100_000 && characters <= 256_000)
            {
                if (detailsCache.Count >= 64 || detailsCache.Values.Sum(d => d.Files.Count) + result.Files.Count > 100_000 ||
                    detailsCache.Values.Sum(d => (long)d.Message.Length + d.Commit.Subject.Length) + characters > 1_000_000)
                    detailsCache.Clear();
                detailsCache[key] = result;
            }
            await metadataGate.WaitAsync(ct);
            try { CacheMetadata(key, result.Commit); }
            finally { metadataGate.Release(); }
            return result;
        }
        finally { detailsGate.Release(); }
    }

    private async Task<CommitInfo> CommitMetadata(string root, string revision, CancellationToken ct)
    {
        var oid = await Resolve(root, revision, ct);
        var key = (root, oid + "\0" + RepositoryObjectView.Read(root).Fingerprint);
        await metadataGate.WaitAsync(ct);
        try
        {
            if (metadataCache.TryGetValue(key, out var cached)) return cached;
            var fields = (await git.Text(root, ct, "show", "-s", "--encoding=UTF-8", "--format=%H%x00%an%x00%aI%x00%P", oid)).Split('\0');
            var text = await new CommitTextReader(runner).ReadAsync(root, oid, ct);
            var result = new CommitInfo(fields[0], text.Subject, fields[1], DateTimeOffset.Parse(fields[2], CultureInfo.InvariantCulture),
                fields[3].Split(' ', StringSplitOptions.RemoveEmptyEntries)) { MessageEncoding = text.Encoding };
            CacheMetadata(key, result);
            return result;
        }
        finally { metadataGate.Release(); }
    }

    private void CacheMetadata((string Root, string Oid) key, CommitInfo commit)
    {
        if (commit.Subject.Length > 256_000) return;
        if (metadataCache.Count >= 256 || metadataCache.Values.Sum(c => (long)c.Subject.Length) + commit.Subject.Length > 1_000_000)
            metadataCache.Clear();
        metadataCache[key] = commit;
    }
}
