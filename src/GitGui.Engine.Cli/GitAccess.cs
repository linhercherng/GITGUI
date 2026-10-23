using System.Text;

namespace GitGui.Engine.Cli;

public sealed class GitCommandException(GitResult result) : InvalidOperationException(result.Error.Trim())
{
    public GitResult Result { get; } = result;
}

internal sealed class GitAccess(GitProcessRunner runner)
{
    // macOS commonly uses case-insensitive APFS. Conservative overlap guards
    // also protect those volumes; Linux paths retain distinct casing.
    internal static StringComparison CollisionComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal static StringComparer CollisionComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    public async Task<byte[]> Bytes(string root, CancellationToken ct, string[] args, GitRunOptions? options = null)
    {
        if (options?.Environment.ContainsKey("GIT_INDEX_FILE") == true)
            args = ["-c", "core.splitIndex=false", .. args];
        var result = await runner.RunAsync(root, args, ct, options);
        if (result.ExitCode != 0) throw new GitCommandException(result);
        return result.Output;
    }
    public async Task<string> Text(string root, CancellationToken ct, params string[] args)
        => Utf8.GetString(await Bytes(root, ct, args)).TrimEnd('\r', '\n');
    public async Task<string?> TryText(string root, CancellationToken ct, params string[] args)
    {
        var result = await runner.RunAsync(root, args, ct);
        if (result.ExitCode == 1 || result.ExitCode == 128 && args.Contains("--quiet")) return null;
        if (result.ExitCode != 0) throw new GitCommandException(result);
        return Utf8.GetString(result.Output).TrimEnd('\r', '\n');
    }
    public async Task Write(string root, CancellationToken ct, string[] args, byte[]? input = null,
        IReadOnlyDictionary<string, string?>? environment = null)
        => await Bytes(root, ct, args, new() { IsReadOnly = false, Input = input, HideOutput = true,
            Environment = environment ?? new Dictionary<string, string?>() });

    public async Task<string> Root(string path, CancellationToken ct)
        => Path.GetFullPath(await Text(path, ct, "rev-parse", "--show-toplevel"));

    public static string FullPath(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) ||
            relative.Split('/').Any(p => p is "" or "." or ".." || p.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Unsafe repository path: " + relative);
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Path escapes repository root.");
        for (var parent = Path.GetDirectoryName(full); parent is not null && !SamePath(parent, root); parent = Path.GetDirectoryName(parent))
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("A repository path traverses a symbolic link. Operation blocked.");
        return full;
    }

    internal static string NormalizeSystemPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsMacOS()) return full;
        // These are macOS system aliases, not repository-controlled symlinks.
        // Leave every other link untouched so mutation preflight still rejects it.
        foreach (var name in new[] { "var", "tmp", "etc" })
        {
            var alias = "/" + name; var physical = "/private/" + name;
            if ((full == alias || full.StartsWith(alias + "/", StringComparison.Ordinal)) &&
                new DirectoryInfo(alias).ResolveLinkTarget(true)?.FullName == physical)
                return physical + full[alias.Length..];
        }
        return full;
    }

    public static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(NormalizeSystemPath(left)), Path.TrimEndingDirectorySeparator(NormalizeSystemPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    internal static IReadOnlyList<string> OverwriteCollisions(string root, IEnumerable<string> targetPaths)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in targetPaths)
        {
            var parts = path.Split('/');
            for (var n = 1; n <= parts.Length; n++)
            {
                var prefix = string.Join('/', parts.Take(n));
                var full = FullPath(root, prefix);
                if (File.Exists(full) || new FileInfo(full).LinkTarget is not null) { result.Add(prefix); break; }
            }
        }
        return result.ToArray();
    }
    public static string[] NullFields(byte[] bytes) => Utf8.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    public static string QuotePath(string path) => "\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "\"";
}
