using System.Text;
using GitGui.Core;
using LibGit2Sharp;

namespace GitGui.Engine.Lib;

public sealed partial class LibHistoryReader
{
    public Task<IReadOnlyList<RefInfo>?> TryReadRefsAsync(string root, NativeReadContext context, CancellationToken ct)
        => Task.Run<IReadOnlyList<RefInfo>?>(() =>
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                if (context.Configuration.Any(e =>
                    e.Key.Equals("extensions.objectformat", StringComparison.OrdinalIgnoreCase) && !e.Value.Equals("sha1", StringComparison.OrdinalIgnoreCase) ||
                    e.Key.Equals("extensions.refstorage", StringComparison.OrdinalIgnoreCase) && !e.Value.Equals("files", StringComparison.OrdinalIgnoreCase))) return null;
                if (!SafeSymbolicRefs(root, ct) || RepositoryObjectView.Read(root).RequiresCli) return null;
                using var repo = new Repository(root);
                // Upstream mapping is libgit2 configuration-dependent. Compare the selected Git's resolved
                // values first, including multivalue fetch mappings; never substitute another global config.
                static bool Relevant(string key) => key.StartsWith("branch.", StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith("remote.", StringComparison.OrdinalIgnoreCase) || key.Equals("core.warnambiguousrefs", StringComparison.OrdinalIgnoreCase);
                static string Key(string key)
                {
                    var first = key.IndexOf('.'); var last = key.LastIndexOf('.');
                    return first < 0 ? key.ToLowerInvariant() : key[..first].ToLowerInvariant() +
                        (last == first ? "" : key[first..last]) + key[last..].ToLowerInvariant();
                }
                static string[] Values(IEnumerable<KeyValuePair<string, string>> entries) => entries.Where(e => Relevant(e.Key))
                    .GroupBy(e => Key(e.Key), StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
                    .SelectMany(group => group.Select(e => group.Key + "\0" + e.Value)).ToArray();
                if (!Values(context.Configuration).SequenceEqual(Values(repo.Config.Select(e => new KeyValuePair<string, string>(e.Key, e.Value))))) return null;
                if (context.Configuration.Any(e => e.Key.Equals("core.warnambiguousrefs", StringComparison.OrdinalIgnoreCase) &&
                    e.Value is "false" or "no" or "off" or "0")) return null;
                var refs = repo.Refs.Take(100_001).Select(reference => { ct.ThrowIfCancellationRequested(); return reference; }).ToArray();
                if (refs.Length > 100_000 || refs.Any(r => r.CanonicalName.Contains('\uFFFD') ||
                    r.CanonicalName is "refs/heads" or "refs/remotes" or "refs/tags")) return null;
                var names = refs.Select(r => r.CanonicalName).ToHashSet(StringComparer.Ordinal);
                var admin = repo.Info.Path;
                string Short(string full)
                {
                    string[] rules = ["{0}", "refs/{0}", "refs/tags/{0}", "refs/heads/{0}", "refs/remotes/{0}", "refs/remotes/{0}/HEAD"];
                    for (var rule = rules.Length - 1; rule >= 0; rule--)
                    {
                        var marker = rules[rule].IndexOf("{0}", StringComparison.Ordinal);
                        var prefix = rules[rule][..marker]; var suffix = rules[rule][(marker + 3)..];
                        if (!full.StartsWith(prefix, StringComparison.Ordinal) || !full.EndsWith(suffix, StringComparison.Ordinal) || full.Length <= prefix.Length + suffix.Length) continue;
                        var candidate = full.Substring(prefix.Length, full.Length - prefix.Length - suffix.Length);
                        var matches = rules.Select(r => r.Replace("{0}", candidate, StringComparison.Ordinal))
                            .Count(name => name == full || names.Contains(name) || name == "HEAD" || File.Exists(Path.Combine(admin, name.Replace('/', Path.DirectorySeparatorChar))));
                        if (matches != 1) throw new NotSupportedException("Ambiguous ref names use Git CLI shortening.");
                        return candidate;
                    }
                    throw new NotSupportedException("Unsupported ref namespace.");
                }
                var result = new List<RefInfo>();
                var current = repo.Info.IsHeadDetached ? null : repo.Head.CanonicalName;
                foreach (var reference in refs.OrderBy(r => Convert.ToHexString(Encoding.UTF8.GetBytes(r.CanonicalName)), StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    var name = reference.CanonicalName;
                    if (!name.StartsWith("refs/heads/", StringComparison.Ordinal) && !name.StartsWith("refs/tags/", StringComparison.Ordinal) &&
                        !name.StartsWith("refs/remotes/", StringComparison.Ordinal)) continue;
                    var direct = reference.ResolveToDirectReference();
                    if (direct is null) return null;
                    string? upstream = null; var track = "";
                    if (name.StartsWith("refs/heads/", StringComparison.Ordinal))
                    {
                        var branch = repo.Branches[name];
                        var tracked = branch?.TrackedBranch;
                        if (tracked is not null)
                        {
                            upstream = Short(tracked.CanonicalName);
                            if (tracked.Tip is null) track = "[gone]";
                            else if (!string.Equals(direct.TargetIdentifier, tracked.Tip.Sha, StringComparison.Ordinal))
                            {
                                // libgit2's complete ancestry walk has no cancellation callback or node limit.
                                // Different tips use the selected Git process, which the runner can terminate.
                                return null;
                            }
                        }
                    }
                    result.Add(new(Short(name), name, direct.TargetIdentifier, upstream, track!, name == current));
                }
                ct.ThrowIfCancellationRequested();
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return null; }
        }, ct);

    // Reference.BuildFromPtr recursively follows symbolic targets. Validate the loose-ref graph
    // before asking LibGit2Sharp to construct any Reference, rejecting pre-existing cycles.
    private static bool SafeSymbolicRefs(string root, CancellationToken ct)
    {
        var marker = Path.Combine(root, ".git");
        var admin = Directory.Exists(marker) ? marker : File.Exists(marker)
            ? Path.GetFullPath(File.ReadAllText(marker).Trim()[8..], root) : root;
        var commonFile = Path.Combine(admin, "commondir");
        var common = File.Exists(commonFile) ? Path.GetFullPath(File.ReadAllText(commonFile).Trim(), admin) : admin;
        var symbols = new Dictionary<string, string>(StringComparer.Ordinal);
        var count = 0; var directories = 0;
        bool Add(string name, string path)
        {
            ct.ThrowIfCancellationRequested();
            if (++count > 100_000 || new FileInfo(path).LinkTarget is not null || new FileInfo(path).Length > 4096) return false;
            using var input = File.OpenRead(path);
            var value = StrictUtf8.GetString(ReadBytes(input, 4096, ct)).TrimEnd('\r', '\n');
            if (!value.StartsWith("ref: ", StringComparison.Ordinal)) return true;
            var target = value[5..];
            if (!target.StartsWith("refs/", StringComparison.Ordinal) || target.Split('/').Any(p => p is "" or "." or "..") ||
                target.Any(c => char.IsControl(c) || c is '\\' or ':' or ' ')) return false;
            symbols[name] = target; return true;
        }
        foreach (var directory in new[] { common, admin }.Distinct(StringComparer.Ordinal))
        {
            var refs = Path.Combine(directory, "refs");
            if (!Directory.Exists(refs)) continue;
            var pending = new Stack<string>(); pending.Push(refs);
            while (pending.TryPop(out var next))
            {
                ct.ThrowIfCancellationRequested();
                if (++directories > 100_000 || new DirectoryInfo(next).LinkTarget is not null) return false;
                foreach (var child in Directory.EnumerateDirectories(next))
                { if (pending.Count >= 100_000) return false; pending.Push(child); }
                foreach (var file in Directory.EnumerateFiles(next))
                    if (!file.EndsWith(".lock", StringComparison.Ordinal) && !Add(Path.GetRelativePath(directory, file).Replace('\\', '/'), file)) return false;
            }
        }
        if (File.Exists(Path.Combine(admin, "HEAD")) && !Add("HEAD", Path.Combine(admin, "HEAD"))) return false;
        foreach (var source in symbols.Keys)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal); var name = source;
            while (symbols.TryGetValue(name, out var target))
            {
                if (!visited.Add(name) || visited.Count > 16) return false;
                name = target;
            }
        }
        return true;
    }
}
