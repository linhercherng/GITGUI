using System.Collections.Concurrent;

namespace GitGui.Engine.Cli;

internal sealed partial class WorkspaceReader
{
    private readonly ConcurrentDictionary<string, RepositoryReadConfiguration> readConfigurations = new(GitAccess.CollisionComparer);
    private async Task<RepositoryReadConfiguration> ReadConfigurationSnapshot(string root, CancellationToken ct)
    {
        root = GitAccess.NormalizeSystemPath(root);
        var start = await runner.CreateStartInfoAsync(root, ct);
        // Git resolves includes, scope priority and the selected installation's system configuration.
        // Full configuration may contain credentials: never place its values in command history/logs.
        var result = await runner.RunAsync(root, ["config", "--null", "--show-origin", "--list"], ct, new() { HideOutput = true });
        if (result.ExitCode != 0) throw new GitCommandException(result);
        var snapshot = new RepositoryReadConfiguration(root, start, result.Output);
        readConfigurations[root] = snapshot;
        foreach (var other in readConfigurations.Keys.Where(path => !GitAccess.SamePath(path, root)))
        { if (readConfigurations.Count <= 2) break; readConfigurations.TryRemove(other, out _); }
        return snapshot;
    }
    private async Task<RepositoryReadConfiguration> ConfigurationForNativeRead(string root, CancellationToken ct)
    {
        root = GitAccess.NormalizeSystemPath(root);
        var start = await runner.CreateStartInfoAsync(root, ct);
        return readConfigurations.TryGetValue(root, out var cached) && cached.IsCurrent(start)
            ? cached : await ReadConfigurationSnapshot(root, ct);
    }
}
