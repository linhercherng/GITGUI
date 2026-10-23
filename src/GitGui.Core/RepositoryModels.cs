namespace GitGui.Core;

public sealed record FileChange(string Path, string IndexStatus, string WorktreeStatus,
    byte[] RawPath, string? OriginalPath = null)
{
    public byte[]? RawOriginalPath { get; init; }
    public bool IsPathSupported => (RawPath.Length == 0 ? GitPathPolicy.IsRepresentable(Path) : GitPathPolicy.IsSupported(RawPath)) &&
        (RawOriginalPath is null ? OriginalPath is null || GitPathPolicy.IsRepresentable(OriginalPath) : GitPathPolicy.IsSupported(RawOriginalPath));
    public string DisplayPath => RawPath.Length == 0 ? GitPathPolicy.Display(Path) : GitPathPolicy.Display(RawPath);
    public string Label => $"{IndexStatus}{WorktreeStatus}  {DisplayPath}";
}

public sealed record RepoStatus(string Branch, string? Head, IReadOnlyList<FileChange> Files);
public sealed record CommitInfo(string Hash, string Subject, string Author, DateTimeOffset Date,
    IReadOnlyList<string> Parents)
{
    public CommitMessageEncoding MessageEncoding { get; init; } = CommitMessageEncoding.Utf8;
    public string Label => $"{Hash[..Math.Min(8, Hash.Length)]}  {Subject}";
}

public sealed record RepoFormat(string ObjectFormat, string RefStorage);

public interface IHistoryReader
{
    Task<IReadOnlyList<CommitInfo>> GetLogAsync(string repository, int limit, CancellationToken ct);
}

public interface IGraphHistoryReader
{
    Task<IReadOnlyList<CommitInfo>> GetGraphAsync(string repository, int skip, int take, CancellationToken ct);
}
public interface IStableGraphHistoryReader : IGraphHistoryReader
{
    Task<IReadOnlyList<CommitInfo>> GetGraphAsync(string repository, IReadOnlyList<string> tips, int skip, int take, CancellationToken ct);
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
