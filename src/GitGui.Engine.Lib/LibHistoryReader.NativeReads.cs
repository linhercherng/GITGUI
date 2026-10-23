using System.Text;
using System.Text.RegularExpressions;
using System.IO.Enumeration;
using GitGui.Core;
using LibGit2Sharp;

namespace GitGui.Engine.Lib;

public sealed partial class LibHistoryReader : INativeReadBackend
{
    private const int NativeReadLimit = 32 * 1024 * 1024;
    private const int ImageLimit = 8 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public Task<string?> TryReadDiffAsync(string root, DiffRequest request, NativeReadContext context, CancellationToken ct)
        => Task.Run<string?>(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var repo = new Repository(root);
            if (!SupportsNativeRead(repo, root, request, context, ct)) return null;
            var (before, after) = ReadBlobs(repo, request);
            if (!SafeText(before, ct) || !SafeText(after, ct)) return null;
            var full = Path.Combine(root, request.Path.Replace('/', Path.DirectorySeparatorChar));
            if (request.Commit is null && !request.Staged && File.Exists(full))
            {
                if (new FileInfo(full).LinkTarget is not null || new FileInfo(full).Length > NativeReadLimit) return null;
                using var input = File.OpenRead(full);
                var bytes = ReadBytes(input, NativeReadLimit, ct);
                if (!SafeText(bytes)) return null;
            }
            var options = new CompareOptions { ContextLines = 3, InterhunkLines = 0, Similarity = SimilarityOptions.None,
                IndentHeuristic = !context.Configuration.Any(e => e.Key.Equals("diff.indentheuristic", StringComparison.OrdinalIgnoreCase) && e.Value is "false" or "no" or "off" or "0") };
            var paths = new[] { request.Path }; var literal = new ExplicitPathsOptions { ShouldFailOnUnmatchedPath = false };
            Patch patch;
            if (request.Commit is not null)
            {
                var commit = repo.Lookup<Commit>(request.Commit) ?? throw new InvalidOperationException("Commit is unavailable to native reads.");
                patch = repo.Diff.Compare<Patch>(commit.Parents.FirstOrDefault()?.Tree, commit.Tree, paths, literal, options);
            }
            else if (request.Staged)
                patch = repo.Diff.Compare<Patch>(repo.Head.Tip?.Tree, DiffTargets.Index, paths, literal, options);
            else
            {
                // Explicit ignored paths keep the CLI no-index semantics via fallback.
                if (repo.Index[request.Path] is null && repo.Ignore.IsPathIgnored(request.Path)) return null;
                patch = repo.Diff.Compare<Patch>(paths, true, literal, options);
            }
            using (patch)
            {
                ct.ThrowIfCancellationRequested();
                var content = patch.Content;
                return content.Length <= NativeReadLimit ? content : null;
            }
        }, ct);

    public Task<FilePreview?> TryReadPreviewAsync(string root, DiffRequest request, NativeReadContext context, CancellationToken ct)
        => Task.Run<FilePreview?>(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var repo = new Repository(root);
            if (!SupportsNativeRead(repo, root, request, context, ct)) return null;
            var (before, after) = ReadBlobs(repo, request);
            var image = new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico" }
                .Contains(Path.GetExtension(request.Path).ToLowerInvariant());
            string? notice = null;
            (long? Size, byte[]? Image) Read(Blob? blob)
            {
                if (blob is null) return (null, null);
                if (image && blob.Size > ImageLimit) notice = "Image preview is limited to 8 MiB per side.";
                return (blob.Size, image && blob.Size <= ImageLimit ? ReadBytes(blob, ct) : null);
            }
            var old = Read(before); var current = Read(after);
            if (request.Commit is null && !request.Staged)
            {
                var full = Path.Combine(root, request.Path.Replace('/', Path.DirectorySeparatorChar));
                if (new FileInfo(full).LinkTarget is { } target) current = (Encoding.UTF8.GetByteCount(target), null);
                else if (File.Exists(full))
                {
                    var size = new FileInfo(full).Length;
                    if (image && size > ImageLimit) notice = "Image preview is limited to 8 MiB per side.";
                    byte[]? content = null;
                    if (image && size <= ImageLimit) { using var input = File.OpenRead(full); content = ReadBytes(input, ImageLimit, ct); }
                    current = (size, content);
                }
                else current = (null, null);
            }
            ct.ThrowIfCancellationRequested();
            return new(old.Size, current.Size, old.Image, current.Image, notice);
        }, ct);

    private static (Blob? Before, Blob? After) ReadBlobs(Repository repo, DiffRequest request)
    {
        Blob? At(Commit? commit) => commit?[request.Path]?.Target as Blob;
        Blob? Indexed() => repo.Index[request.Path] is { } entry ? repo.Lookup<Blob>(entry.Id) : null;
        if (request.Commit is not null)
        {
            var commit = repo.Lookup<Commit>(request.Commit) ?? throw new InvalidOperationException("Commit is unavailable to native reads.");
            return (At(commit.Parents.FirstOrDefault()), At(commit));
        }
        return request.Staged ? (At(repo.Head.Tip), Indexed()) : (Indexed(), null);
    }

    private static bool SupportsNativeRead(Repository repo, string root, DiffRequest request, NativeReadContext context, CancellationToken ct)
    {
        if (RepositoryObjectView.Read(root).RequiresCli || request.Path.Any(char.IsControl)) return false;
        if (request.Commit is not null && !Regex.IsMatch(request.Commit, "^[0-9a-fA-F]{40}$")) return false;
        if (request.Commit is null && (repo.Index.Conflicts[request.Path] is not null ||
            repo.Index[request.Path]?.Mode is Mode.GitLink or Mode.SymbolicLink)) return false;
        foreach (var key in new[] { "core.autocrlf", "core.eol", "core.filemode", "core.symlinks", "core.ignorecase" })
        {
            var selected = context.Configuration.LastOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
            if (!string.Equals(selected, repo.Config.Get<string>(key)?.Value, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (context.Configuration.Any(e => e.Key.Equals("core.attributesfile", StringComparison.OrdinalIgnoreCase) ||
            e.Key.Equals("diff.algorithm", StringComparison.OrdinalIgnoreCase) && !e.Value.Equals("myers", StringComparison.OrdinalIgnoreCase))) return false;
        var attributes = new List<string> { Path.Combine(repo.Info.Path, "info", "attributes") };
        var commonPointer = Path.Combine(repo.Info.Path, "commondir");
        if (File.Exists(commonPointer)) attributes.Add(Path.Combine(Path.GetFullPath(File.ReadAllText(commonPointer).Trim(), repo.Info.Path), "info", "attributes"));
        var directory = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, request.Path.Replace('/', Path.DirectorySeparatorChar))));
        while (directory is not null)
        {
            attributes.Add(Path.Combine(directory, ".gitattributes"));
            if (Path.GetFullPath(directory).Equals(Path.GetFullPath(root), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
            directory = Path.GetDirectoryName(directory);
        }
        if (!string.IsNullOrEmpty(context.XdgConfigHome)) attributes.Add(Path.Combine(context.XdgConfigHome, "git", "attributes"));
        else if (!string.IsNullOrEmpty(context.HomeDirectory)) attributes.Add(Path.Combine(context.HomeDirectory, ".config", "git", "attributes"));
        if (context.SystemAttributesEnabled)
        {
            directory = Path.GetDirectoryName(context.GitExecutable);
            for (var depth = 0; directory is not null && depth < 4; depth++, directory = Path.GetDirectoryName(directory))
                attributes.Add(Path.Combine(directory, "etc", "gitattributes"));
        }
        foreach (var path in attributes.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            byte[] bytes;
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 1024 * 1024) return false;
                using var input = File.OpenRead(path);
                bytes = ReadBytes(input, 1024 * 1024, ct);
            }
            else if (Path.GetFileName(path) == ".gitattributes" &&
                repo.Index[Path.GetRelativePath(root, path).Replace('\\', '/')] is { } entry &&
                repo.Lookup<Blob>(entry.Id) is { } indexed)
            {
                // Git falls back to indexed attributes when the worktree copy
                // is absent. A local deletion must not bypass special filters.
                if (indexed.Size > 1024 * 1024) return false;
                bytes = ReadBytes(indexed, ct);
            }
            else continue;
            var text = StrictUtf8.GetString(bytes).TrimStart('\uFEFF');
            foreach (var line in text.Split('\n'))
            {
                if (line.TrimStart().StartsWith('#') || !Regex.IsMatch(line, @"(?:^|\s)(?:[!-]?filter(?:=|\s|$)|working-tree-encoding(?:=|\s|$)|[!-]?ident(?:=|\s|$)|diff=)")) continue;
                var pattern = Regex.Match(line.TrimStart(), @"^\S+").Value;
                // Simple stock rules such as *.doc must not disable native reads
                // for unrelated text files. Rich wildmatch/quoted/macro rules
                // conservatively use Git instead of approximating their scope.
                if (pattern.IndexOfAny(['[', ']', '\\', '"', '!']) >= 0 || pattern.Contains("**", StringComparison.Ordinal)) return false;
                var target = pattern.Contains('/')
                    ? Path.GetRelativePath(Path.GetFileName(path) == ".gitattributes" ? Path.GetDirectoryName(path)! : root,
                        Path.Combine(root, request.Path.Replace('/', Path.DirectorySeparatorChar))).Replace('\\', '/')
                    : Path.GetFileName(request.Path);
                if (FileSystemName.MatchesSimpleExpression(pattern.TrimStart('/'), target, ignoreCase: OperatingSystem.IsWindows())) return false;
            }
        }
        return true;
    }

    private static byte[] ReadBytes(Blob blob, CancellationToken ct)
    {
        using var input = blob.GetContentStream();
        return ReadBytes(input, NativeReadLimit, ct);
    }
    private static byte[] ReadBytes(Stream input, int limit, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[65536]; int count;
        while ((count = input.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (output.Length + count > limit) throw new InvalidOperationException("Native preview or diff exceeds its read limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    private static bool SafeText(Blob? blob, CancellationToken ct) => blob is null || blob.Size <= NativeReadLimit &&
        (blob.IsBinary || SafeText(ReadBytes(blob, ct)));
    private static bool SafeText(byte[] bytes)
    {
        if (bytes.Contains((byte)0)) return true;
        try { StrictUtf8.GetCharCount(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }
}
