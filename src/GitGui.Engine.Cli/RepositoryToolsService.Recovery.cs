using GitGui.Core;
using System.Text;
using System.Text.RegularExpressions;

namespace GitGui.Engine.Cli;

public sealed partial class RepositoryToolsService
{
    private static StringComparison RecoveryPathComparison => GitAccess.CollisionComparison;
    private static StringComparer RecoveryPathComparer => GitAccess.CollisionComparer;
    private static bool RecoveryPathsOverlap(string a, string b) => a.Equals(b, RecoveryPathComparison) ||
        a.StartsWith(b + "/", RecoveryPathComparison) || b.StartsWith(a + "/", RecoveryPathComparison);

    private async Task PreflightRecovery(string root, RepositoryToolOperation op, CancellationToken ct)
    {
        if (op.Action is RepositoryToolAction.SparseSet or RepositoryToolAction.SparseDisable)
        {
            if (op.Action == RepositoryToolAction.SparseSet)
            {
                if (op.Patterns.Count == 0) throw new ArgumentException("Enter at least one sparse directory or pattern.");
                foreach (var pattern in op.Patterns)
                {
                    if (string.IsNullOrWhiteSpace(pattern) || pattern.Any(c => c is '\r' or '\n' or '\0'))
                        throw new ArgumentException("Each sparse pattern must occupy one line.");
                    if (op.Cone) _ = RecoveryPath(root, pattern.TrimEnd('/'));
                }
            }
            _ = await RecoveryOverwritePaths(root, op, ct);
            return;
        }
        var modules = await RecoveryGitlinks(root, ct);
        if (op.Path.Length > 0) _ = RecoveryPath(root, op.Path);
        foreach (var path in modules.Keys.Where(p => op.Path.Length == 0 || p.Equals(op.Path, RecoveryPathComparison)))
            if (new DirectoryInfo(RecoveryPath(root, path)).LinkTarget is not null)
                throw new InvalidOperationException("Symbolic child repository directory.");
        if (op.Action == RepositoryToolAction.SubmoduleAdd)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(op.Path);
            var destination = RecoveryPath(root, op.Path);
            if (op.Path.Equals(".gitmodules", RecoveryPathComparison) || new FileInfo(destination).LinkTarget is not null)
                throw new InvalidOperationException("Unsafe submodule destination.");
            if (File.Exists(destination) || Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                throw new InvalidOperationException("Choose a new or empty submodule directory.");
            if (modules.Keys.Any(p => RecoveryPathsOverlap(op.Path, p)))
                throw new InvalidOperationException("Submodule paths cannot overlap another child repository.");
            if (op.Name.Length > 0 && (!Regex.IsMatch(op.Name, @"^[\p{L}\p{N}_][\p{L}\p{N}._-]*$") || op.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Invalid submodule name.");
            _ = await LocalSubmoduleSource(op.Url, ct);
        }
        else
        {
            if (op.Path.Length > 0 && !modules.ContainsKey(op.Path)) throw new ArgumentException("Select an indexed submodule.");
            if (modules.Count == 0) throw new InvalidOperationException("No submodules are registered in the parent index.");
            if (op.Action is RepositoryToolAction.SubmoduleUpdate or RepositoryToolAction.SubmoduleDeinitialize)
                foreach (var entry in modules.Where(p => op.Path.Length == 0 || p.Key.Equals(op.Path, RecoveryPathComparison)))
                    await CheckChildData(RecoveryPath(root, entry.Key), entry.Value, op.Action == RepositoryToolAction.SubmoduleDeinitialize,
                        op.Recursive || op.Action == RepositoryToolAction.SubmoduleDeinitialize, ct);
        }
        var gitmodules = Path.Combine(root, ".gitmodules");
        if (new FileInfo(gitmodules).LinkTarget is not null || Directory.Exists(gitmodules))
            throw new InvalidOperationException("Unsafe .gitmodules file.");
    }

    private async Task ExecuteRecovery(string root, RepositoryToolOperation op, CancellationToken ct, IProgress<OperationProgress>? progress)
    {
        var prefix = new[] { "-c", "protocol.ext.allow=never", "-c", "submodule.recurse=false" };
        var paths = op.Path.Length > 0 ? new[] { op.Path } : [];
        var recursive = op.Recursive ? new[] { "--recursive" } : [];
        switch (op.Action)
        {
            case RepositoryToolAction.SubmoduleAdd:
                var local = await LocalSubmoduleSource(op.Url, ct);
                await Run(root, [.. prefix, .. (local is null ? Array.Empty<string>() : ["-c", "protocol.file.allow=always"]),
                    "submodule", "add", .. (op.Name.Length > 0 ? new[] { "--name", op.Name } : []), "--", local ?? op.Url, op.Path], ct, progress);
                break;
            case RepositoryToolAction.SubmoduleInitialize:
                await Run(root, [.. prefix, "submodule", "init", "--", .. paths], ct, progress); break;
            case RepositoryToolAction.SubmoduleUpdate:
                await UpdateSubmodules(root, op, prefix, paths, ct, progress); break;
            case RepositoryToolAction.SubmoduleSync:
                await Run(root, [.. prefix, "submodule", "sync", .. recursive, "--", .. paths], ct, progress); break;
            case RepositoryToolAction.SubmoduleDeinitialize:
                await Run(root, [.. prefix, "submodule", "deinit", .. (paths.Length == 0 ? new[] { "--all" } : []), "--", .. paths], ct, progress); break;
            case RepositoryToolAction.SparseSet:
                var patterns = op.Cone ? op.Patterns.Select(p => GitAccess.QuotePath(p.TrimEnd('/'))) : op.Patterns;
                await Run(root, ["-c", "submodule.recurse=false", "sparse-checkout", "set", op.Cone ? "--cone" : "--no-cone",
                    "--no-sparse-index", "--stdin"], ct, progress, Encoding.UTF8.GetBytes(string.Join("\n", patterns) + "\n")); break;
            case RepositoryToolAction.SparseDisable:
                await Run(root, ["-c", "submodule.recurse=false", "sparse-checkout", "disable"], ct, progress); break;
            default: throw new ArgumentException("Unsupported recovery operation.");
        }
    }

    private async Task UpdateSubmodules(string root, RepositoryToolOperation op, string[] prefix, string[] paths,
        CancellationToken ct, IProgress<OperationProgress>? progress)
    {
        await Run(root, [.. prefix, "submodule", "init", "--", .. paths], ct, progress);
        var modules = await RecoveryGitlinks(root, ct);
        var configured = await git.TryText(root, ct, "config", "--file", ".gitmodules", "--null", "--get-regexp", @"^submodule\..*\.path$");
        var urls = new Dictionary<string, string>(RecoveryPathComparer);
        foreach (var field in (configured ?? "").Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = field.IndexOf('\n');
            if (separator < 0) continue;
            var key = field[..separator]; var path = field[(separator + 1)..];
            var url = await git.TryText(root, ct, "config", "--get", key[..^4] + "url");
            if (url is not null) urls[path] = url;
        }
        foreach (var entry in modules.Where(e => paths.Length == 0 || paths.Contains(e.Key, RecoveryPathComparer)))
        {
            if (!urls.TryGetValue(entry.Key, out var url)) throw new InvalidOperationException("Missing submodule URL: " + entry.Key);
            var local = await LocalSubmoduleSource(url, ct);
            if (local is not null)
            {
                var child = RecoveryPath(root, entry.Key);
                if (File.Exists(Path.Combine(child, ".git")) || Directory.Exists(Path.Combine(child, ".git")))
                {
                    var available = await runner.RunAsync(child, ["cat-file", "-e", entry.Value + "^{commit}"], ct, new() { HideOutput = true });
                    // Fetch only this validated source explicitly. A changed child remote must
                    // never inherit permission to fetch a different local repository.
                    if (available.ExitCode != 0)
                        await Run(child, [.. prefix, "-c", "protocol.file.allow=always", "fetch", "--no-recurse-submodules", "--", local, entry.Value], ct, progress);
                    await CheckChildData(child, entry.Value, deinitialize: false, op.Recursive, ct);
                    await Run(root, [.. prefix, "submodule", "update", "--init", "--checkout", "--no-fetch", "--", entry.Key], ct, progress);
                }
                else
                    await Run(root, [.. prefix, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--checkout", "--", entry.Key], ct, progress);
                // Nested transports have not been individually validated; use native Git
                // policy for the separate recursive operation, without our file override.
                if (op.Recursive)
                    await Run(root, [.. prefix, "submodule", "update", "--init", "--checkout", "--recursive", "--", entry.Key], ct, progress);
            }
            else
                await Run(root, [.. prefix, "submodule", "update", "--init", "--checkout", .. (op.Recursive ? new[] { "--recursive" } : []), "--", entry.Key], ct, progress);
        }
    }

    private async Task<IReadOnlyList<string>> RecoveryOverwritePaths(string root, RepositoryToolOperation op, CancellationToken ct)
    {
        if (op.Action is not (RepositoryToolAction.SparseSet or RepositoryToolAction.SparseDisable)) return [];
        var head = await git.TryText(root, ct, "rev-parse", "--verify", "--quiet", "HEAD");
        if (head is null) return [];
        var entries = GitAccess.NullFields(await git.Bytes(root, ct, ["ls-tree", "-r", "-z", head, "--"]));
        var paths = entries.Where(e => !e.StartsWith("160000 ", StringComparison.Ordinal)).Select(e => e[(e.IndexOf('\t') + 1)..]).ToArray();
        foreach (var path in paths)
        {
            var full = RecoveryPath(root, path);
            if (Directory.Exists(full) && new DirectoryInfo(full).LinkTarget is null)
                throw new InvalidOperationException("A directory blocks sparse expansion; move it aside so its contents are preserved: " + path);
        }
        return GitAccess.OverwriteCollisions(root, paths);
    }

    private static string RecoveryPath(string root, string path)
    {
        if (path.Any(c => char.IsControl(c) || c is '\\' or ':')) throw new ArgumentException("Use a safe repository-relative path with forward slashes.");
        var full = GitAccess.FullPath(root, path);
        for (var parent = Path.GetDirectoryName(full); parent is not null && !GitAccess.SamePath(parent, root); parent = Path.GetDirectoryName(parent))
            if (File.Exists(Path.Combine(parent, ".git")) || Directory.Exists(Path.Combine(parent, ".git")))
                throw new InvalidOperationException("A parent path enters another child repository.");
        return full;
    }

    private async Task<Dictionary<string, string>> RecoveryGitlinks(string root, CancellationToken ct)
        => GitAccess.NullFields(await git.Bytes(root, ct, ["ls-files", "--stage", "-z"]))
            .Where(e => e.StartsWith("160000 ", StringComparison.Ordinal)).ToDictionary(e => e[(e.IndexOf('\t') + 1)..], e => e.Split(' ')[1], RecoveryPathComparer);

    private async Task<string?> LocalSubmoduleSource(string url, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (url.StartsWith('-') || url.Any(char.IsControl) || url.Contains("::", StringComparison.Ordinal)) throw new ArgumentException("Unsupported submodule transport.");
        string? local = Path.IsPathRooted(url) ? Path.GetFullPath(url) : null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile) local = Path.GetFullPath(uri.LocalPath);
            else if (uri.Scheme is "http" or "https" or "ssh" or "git")
            {
                if ((uri.Scheme is "http" or "https") && uri.UserInfo.Length > 0 || Regex.IsMatch(uri.Query, "token|password|secret", RegexOptions.IgnoreCase))
                    throw new ArgumentException("Use Git credentials rather than secrets in the submodule URL.");
            }
            else if (local is null) throw new ArgumentException("Unsupported submodule protocol.");
        }
        else if (local is null && !Regex.IsMatch(url, @"^[^\s/:]+(?:@[^\s/:]+)?:[^\s]+$"))
            throw new ArgumentException("Use an explicit absolute local repository path or HTTPS/SSH URL.");
        if (local is null) return null;
        if (!Directory.Exists(local)) throw new ArgumentException("Local submodule source does not exist.");
        var bare = await git.Text(local, ct, "rev-parse", "--is-bare-repository");
        var actual = bare == "true" ? await git.Text(local, ct, "rev-parse", "--absolute-git-dir") : await git.Root(local, ct);
        if (!GitAccess.SamePath(local, actual)) throw new ArgumentException("Select the local Git repository root.");
        return local;
    }

    private async Task CheckChildData(string child, string target, bool deinitialize, bool recursive, CancellationToken ct, bool unknownAncestor = false)
    {
        if (new DirectoryInfo(child).LinkTarget is not null) throw new InvalidOperationException("Symbolic child repository directory.");
        if (!File.Exists(Path.Combine(child, ".git")) && !Directory.Exists(Path.Combine(child, ".git"))) return;
        var childFlags = GitAccess.NullFields(await git.Bytes(child, ct, ["ls-files", "-v", "-z"]));
        if (childFlags.Any(e => e.Length > 0 && (e[0] == 'S' || char.IsLower(e[0]))))
            throw new InvalidOperationException("Child assume-unchanged or skip-worktree entries can hide private changes; clear those flags before updating or deinitializing the child.");
        if (deinitialize && (await git.Bytes(child, ct, ["status", "--porcelain=v2", "-z", "--untracked-files=all", "--ignore-submodules=none"], new() { Trusted = true })).Length > 0)
            throw new InvalidOperationException("Child repository contains changes; deinitialization is refused.");
        var ignored = GitAccess.NullFields(await git.Bytes(child, ct, ["ls-files", "--others", "--ignored", "--exclude-standard", "-z"]))
            .Select(p => p.TrimEnd('/')).ToArray();
        var exists = await runner.RunAsync(child, ["cat-file", "-e", target + "^{commit}"], ct, new() { HideOutput = true });
        var currentEntries = GitAccess.NullFields(await git.Bytes(child, ct, ["ls-files", "--stage", "-z"]));
        var currentChildren = currentEntries.Where(e => e.StartsWith("160000 ", StringComparison.Ordinal))
            .Select(e => e[(e.IndexOf('\t') + 1)..]).ToArray();
        if (!deinitialize)
        {
            if (exists.ExitCode != 0 && currentChildren.Length > 0)
                throw new InvalidOperationException("Fetch the child target first so transitions around its nested repositories can be checked safely.");
            if (exists.ExitCode == 0)
            {
                var targetEntries = GitAccess.NullFields(await git.Bytes(child, ct, ["ls-tree", "-r", "-z", target, "--"]));
                var targetChildren = targetEntries.Where(e => e.StartsWith("160000 ", StringComparison.Ordinal))
                    .Select(e => e[(e.IndexOf('\t') + 1)..]).ToArray();
                var targetFiles = targetEntries.Where(e => !e.StartsWith("160000 ", StringComparison.Ordinal))
                    .Select(e => e[(e.IndexOf('\t') + 1)..]).ToArray();
                var currentFiles = currentEntries.Where(e => !e.StartsWith("160000 ", StringComparison.Ordinal))
                    .Select(e => e[(e.IndexOf('\t') + 1)..]).ToArray();
                if (targetFiles.Any(p => currentChildren.Any(c => RecoveryPathsOverlap(p, c))) ||
                    currentFiles.Any(p => targetChildren.Any(c => RecoveryPathsOverlap(p, c))) ||
                    GitAccess.OverwriteCollisions(child, targetChildren).Count > 0)
                    throw new InvalidOperationException("A child file/submodule type transition can overwrite a nested repository; move it aside before updating.");
            }
        }
        if (ignored.Length > 0)
        {
            if (deinitialize || unknownAncestor || exists.ExitCode != 0) throw new InvalidOperationException("Ignored child files must be moved aside before deinit or fetching an unavailable update target.");
            var tree = GitAccess.NullFields(await git.Bytes(child, ct, ["ls-tree", "-r", "--name-only", "-z", target, "--"]));
            if (tree.Any(p => ignored.Any(i => RecoveryPathsOverlap(p, i))))
                throw new InvalidOperationException("Ignored child files block the update target.");
        }
        if (recursive)
        {
            var children = exists.ExitCode == 0 && !deinitialize
                ? GitAccess.NullFields(await git.Bytes(child, ct, ["ls-tree", "-r", "-z", target, "--"]))
                    .Where(e => e.StartsWith("160000 ", StringComparison.Ordinal))
                    .ToDictionary(e => e[(e.IndexOf('\t') + 1)..], e => e.Split(' ')[2].Split('\t')[0], RecoveryPathComparer)
                : await RecoveryGitlinks(child, ct);
            foreach (var entry in children)
                await CheckChildData(RecoveryPath(child, entry.Key), entry.Value, deinitialize, recursive: true, ct,
                    unknownAncestor || exists.ExitCode != 0);
        }
    }
}
