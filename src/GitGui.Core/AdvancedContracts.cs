namespace GitGui.Core;

public enum ResetMode { Soft, Mixed, Hard }
public enum RebaseStepKind { Pick, Reword, Edit, Squash, Fixup, Drop }
public sealed record RebaseStep(string Commit, RebaseStepKind Action = RebaseStepKind.Pick, string? Message = null);
public sealed record StashInfo(string Oid, string Selector, string Message, DateTimeOffset CreatedAt);
public sealed record TagInfo(string Name, string Oid, string Target, bool Annotated, string Message);
public sealed record ReflogInfo(string Oid, string Selector, string Message, string Author, DateTimeOffset CreatedAt);
public sealed record AdvancedRemoteInfo(string Name, IReadOnlyList<string> FetchUrls, IReadOnlyList<string> PushUrls);
public sealed record AdvancedState(IReadOnlyList<StashInfo> Stashes, IReadOnlyList<TagInfo> Tags,
    IReadOnlyList<ReflogInfo> Reflog, IReadOnlyList<AdvancedRemoteInfo> Remotes, InProgressState InProgress);
public sealed record BlameLine(int LineNumber, string Commit, string Author, string Text);
public sealed record FileHistoryEntry(CommitInfo Commit, string Path);
public sealed record FileHistoryResult(IReadOnlyList<BlameLine> Blame, IReadOnlyList<FileHistoryEntry> History, string Backend);

public interface IAdvancedWorkspaceService
{
    Task<AdvancedState> GetAdvancedStateAsync(string root, CancellationToken ct);
    Task<IReadOnlyList<RebaseStep>> GetRebasePlanAsync(string root, string upstream, CancellationToken ct);
    Task<FileHistoryResult> GetFileHistoryAsync(string root, string path, CancellationToken ct);
}

public interface IFileHistoryReader
{
    Task<FileHistoryResult> ReadAsync(string root, string path, CancellationToken ct);
}
