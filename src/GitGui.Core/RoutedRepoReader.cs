namespace GitGui.Core;

public static class ReadBackendPolicy
{
    public static bool CanUseLibrary(RepoFormat format)
        => format.ObjectFormat == "sha1" && format.RefStorage == "files";
}

public sealed class RoutedRepoReader(IRepoReader cli, IHistoryReader library) : IRepoReader
{
    public Task<RepoStatus> GetStatusAsync(string repository, CancellationToken ct)
        => cli.GetStatusAsync(repository, ct);
    public Task<string> GetDiffAsync(string repository, string path, bool staged, CancellationToken ct)
        => cli.GetDiffAsync(repository, path, staged, ct);
    public Task<RepoFormat> GetFormatAsync(string repository, CancellationToken ct)
        => cli.GetFormatAsync(repository, ct);

    public async Task<IReadOnlyList<CommitInfo>> GetLogAsync(string repository, int limit, CancellationToken ct)
    {
        if (ReadBackendPolicy.CanUseLibrary(await cli.GetFormatAsync(repository, ct)))
        {
            try { return await library.GetLogAsync(repository, limit, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* Correctness first: retry unsupported/corrupt native reads via CLI. */ }
        }
        return await cli.GetLogAsync(repository, limit, ct);
    }
}
