namespace GitGui.Core;

public static class GitVersionPolicy
{
    public static bool Accepts(string output)
    {
        if (!output.StartsWith("git version ", StringComparison.Ordinal)) return false;
        var parts = output[12..].Split('.');
        return parts.Length >= 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor) &&
            (major > 2 || major == 2 && minor >= 40);
    }
}

public sealed record GitRuntimeInfo(string Executable, string Version);

public interface IGitPathStore
{
    Task<string?> LoadGitPathAsync(CancellationToken ct);
    Task SaveGitPathAsync(string? path, CancellationToken ct);
}

public interface IGitRuntimeSettings
{
    Task<string?> GetPathAsync(CancellationToken ct);
    Task<GitRuntimeInfo> CheckAsync(string? path, CancellationToken ct);
    Task<GitRuntimeInfo> SaveAsync(string? path, CancellationToken ct);
}
