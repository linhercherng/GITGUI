namespace GitGui.Core;

public sealed record FileChange(string Path, string IndexStatus, string WorktreeStatus,
    byte[] RawPath, string? OriginalPath = null)
{
    public string Label => $"{IndexStatus}{WorktreeStatus}  {Path}";
}

public sealed record RepoStatus(string Branch, string? Head, IReadOnlyList<FileChange> Files);
public sealed record CommitInfo(string Hash, string Subject, string Author, DateTimeOffset Date,
    IReadOnlyList<string> Parents)
{
    public string Label => $"{Hash[..Math.Min(8, Hash.Length)]}  {Subject}";
}

public sealed record RepoFormat(string ObjectFormat, string RefStorage);

public interface IHistoryReader
{
    Task<IReadOnlyList<CommitInfo>> GetLogAsync(string repository, int limit, CancellationToken ct);
}

public interface IRepoReader : IHistoryReader
{
    Task<RepoStatus> GetStatusAsync(string repository, CancellationToken ct);
    Task<string> GetDiffAsync(string repository, string path, bool staged, CancellationToken ct);
    Task<RepoFormat> GetFormatAsync(string repository, CancellationToken ct);
}

public interface IRecentRepositories
{
    Task<IReadOnlyList<string>> ListAsync(CancellationToken ct);
    Task RememberAsync(string path, CancellationToken ct);
}

public enum RiskLevel { Low, Medium, High, RemoteIrreversible }

// The M0 release exposes reads only. Writer and snapshot contracts will be introduced together.
