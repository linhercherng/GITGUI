namespace GitGui.Core;

public sealed record StageExclusion(string Path, bool IsDirectory);

public interface IStageExclusionStore
{
    Task<IReadOnlyList<StageExclusion>> LoadStageExclusionsAsync(string root, CancellationToken ct);
    Task SaveStageExclusionsAsync(string root, IReadOnlyList<StageExclusion> rules, CancellationToken ct);
}

public sealed class StageExclusionRules
{
    private static readonly bool IgnoreCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    private static readonly StringComparer Comparer = IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison Comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly HashSet<string> files, directories;
    private readonly string[] prefixes;
    public static StageExclusionRules Empty { get; } = new([]);
    public IReadOnlyList<StageExclusion> Rules { get; }

    public StageExclusionRules(IEnumerable<StageExclusion> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var unique = new HashSet<string>(Comparer);
        var normalized = new List<StageExclusion>();
        foreach (var rule in rules)
        {
            ArgumentNullException.ThrowIfNull(rule);
            var path = OperatingSystem.IsWindows() ? rule.Path?.Replace('\\', '/') : rule.Path;
            if (path is null || !GitPathPolicy.IsRepresentable(path) || Path.IsPathRooted(path) ||
                !rule.IsDirectory && path.EndsWith('/')) throw new ArgumentException("Invalid repository-relative staging exclusion path.");
            if (rule.IsDirectory) path = path.TrimEnd('/');
            if (path.Split('/').Any(part => part is "" or "." or ".."))
                throw new ArgumentException("Invalid repository-relative staging exclusion path.");
            if (unique.Add((rule.IsDirectory ? "directory:" : "file:") + path)) normalized.Add(new(path, rule.IsDirectory));
        }
        Rules = normalized.AsReadOnly();
        files = normalized.Where(rule => !rule.IsDirectory).Select(rule => rule.Path).ToHashSet(Comparer);
        directories = normalized.Where(rule => rule.IsDirectory).Select(rule => rule.Path).ToHashSet(Comparer);
        var roots = new List<string>();
        foreach (var prefix in directories.Select(path => path + "/").Order(Comparer))
            if (roots.Count == 0 || !prefix.StartsWith(roots[^1], Comparison)) roots.Add(prefix);
        prefixes = roots.ToArray();
    }

    public bool IsExcluded(FileChange change) => IsExcluded(change.Path) ||
        change.OriginalPath is { } original && IsExcluded(original);

    public bool IsExcluded(string path)
    {
        if (Rules.Count == 0) return false;
        if (files.Contains(path) || directories.Contains(path)) return true;
        // Disjoint prefixes include '/', so lexical neighbors cannot hide an ancestor.
        var index = Array.BinarySearch(prefixes, path, Comparer);
        if (index < 0) index = ~index - 1;
        return index >= 0 && path.StartsWith(prefixes[index], Comparison);
    }
}
