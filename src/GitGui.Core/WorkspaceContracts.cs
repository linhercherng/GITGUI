namespace GitGui.Core;

public enum GitAction
{
    Init, Clone, Stage, Unstage, StagePatch, UnstagePatch, Discard, Commit,
    CreateBranch, SwitchBranch, RenameBranch, DeleteBranch, Fetch, Pull, Push, Merge,
    AbortMerge, ResolveOurs, ResolveTheirs, MarkResolved, ExternalMergeTool, RestoreSnapshot, WriteConflictResult,
    StashPush, StashApply, StashPop, StashDrop, RebaseInteractive, CherryPick, Revert, Reset,
    CreateTag, DeleteTag, AddRemote, RenameRemote, SetRemoteUrl, RemoveRemote,
    ContinueOperation, SkipOperation, AbortOperation
}

public sealed record GitOperation(GitAction Action)
{
    public IReadOnlyList<string> Paths { get; init; } = [];
    public string? Name { get; init; }
    public string? NewName { get; init; }
    public string? Message { get; init; }
    public string? Remote { get; init; }
    public string? Url { get; init; }
    public string? InitAuthorName { get; init; }
    public string? InitAuthorEmail { get; init; }
    public string? Patch { get; init; }
    public string? SnapshotId { get; init; }
    public bool Amend { get; init; }
    public bool Force { get; init; }
    public bool SkipHooks { get; init; }
    public bool Sign { get; init; }
    public string? Revision { get; init; }
    public string? StashOid { get; init; }
    public bool IncludeUntracked { get; init; }
    public bool Annotated { get; init; }
    public bool PushUrl { get; init; }
    public ResetMode ResetMode { get; init; }
    public IReadOnlyList<RebaseStep> RebasePlan { get; init; } = [];
}

public sealed record LogFilter(string Search = "", string Author = "", string Path = "",
    string Since = "", string Until = "", int Skip = 0, int Take = 500);
public sealed record RefInfo(string Name, string FullName, string Target, string? Upstream = null,
    string Tracking = "", bool IsCurrent = false);
public sealed record RemoteInfo(string Name, string Url);
public sealed record InProgressState(string Kind, bool HasConflicts)
{
    public static readonly InProgressState None = new("", false);
    public bool IsActive => Kind.Length > 0;
}
public sealed record WorkspaceState(string Root, RepoStatus Status, IReadOnlyList<CommitInfo> Commits,
    IReadOnlyList<RefInfo> Refs, IReadOnlyList<RemoteInfo> Remotes, InProgressState InProgress,
    bool IsTrusted, IReadOnlyList<string> RiskySettings)
{
    public RepositoryTextSettings TextSettings { get; init; } = new();
    public string? HistoryGeneration { get; init; }
    public bool HasMoreHistory { get; init; }
}
public sealed record DiffRequest(string Path, bool Staged = false, string? Commit = null);
public sealed record CommitDetails(CommitInfo Commit, string Message, IReadOnlyList<FileChange> Files)
{
    public CommitMessageEncoding MessageEncoding { get; init; } = CommitMessageEncoding.Utf8;
}
public sealed record ConflictContent(string Base, string Ours, string Theirs, string Result, bool IsBinary);
public sealed record FilePreview(long? BeforeSize, long? AfterSize, byte[]? BeforeImage, byte[]? AfterImage, string? Notice = null);
public sealed record SnapshotInfo(string Id, string Repository, string Title, DateTimeOffset CreatedAt,
    string StateRef, string? Head, int FileCount)
{
    public string Label => $"{CreatedAt:MM-dd HH:mm:ss}  {Title}";
}
public enum GitErrorKind { None, NonFastForward, StaleLease, Conflict, AuthFailed, LockExists,
    HookRejected, Network, DirtyWorktree, Unknown, WorktreeCleanupIncomplete }
public sealed record OperationProgress(string Phase, int? Percent = null, bool CanCancel = false);
public sealed record CommandEntry(DateTimeOffset Time, string Command, string Output, int ExitCode,
    TimeSpan Duration)
{
    public string? WorkingDirectory { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string StandardError { get; init; } = "";
}
public sealed record OperationOutcome(bool Success, string Message, GitErrorKind Error = GitErrorKind.None,
    SnapshotInfo? Snapshot = null, bool Cancelled = false)
{
    public GitSuggestedAction SuggestedAction => Success || Cancelled ? GitSuggestedAction.None : GitFailurePolicy.Suggest(Error);
}
public sealed record Confirmation(string Title, string Details, RiskLevel Risk, string? RequiredText = null);
public sealed record ConfirmationAnswer(bool Accepted, string? Text = null);

public interface IWorkspaceInteraction
{
    Task<ConfirmationAnswer> ConfirmAsync(Confirmation confirmation, CancellationToken ct);
    Task<string?> CredentialAsync(string prompt, CancellationToken ct);
}

public interface IRepositorySettings
{
    Task<bool> IsTrustedAsync(string root, CancellationToken ct);
    Task TrustAsync(string root, CancellationToken ct);
}
public interface ISnapshotCatalog
{
    Task SaveAsync(SnapshotInfo snapshot, CancellationToken ct);
    Task<IReadOnlyList<SnapshotInfo>> ListAsync(string root, CancellationToken ct);
    Task RemoveAsync(string id, CancellationToken ct);
}

public interface IWorkspaceService
{
    event EventHandler<CommandEntry>? CommandExecuted;
    Task<WorkspaceState> OpenAsync(string path, LogFilter filter, CancellationToken ct);
    async Task<WorkspaceStatus> RefreshStatusAsync(string root, CancellationToken ct)
    {
        var state = await OpenAsync(root, new(), ct);
        return new(state.Root, state.Status, state.InProgress);
    }
    async Task<HistoryPage> GetHistoryPageAsync(string root, LogFilter filter, string? generation, CancellationToken ct)
    {
        var state = await OpenAsync(root, filter, ct);
        var current = state.Status.Head ?? "";
        return new(state.Commits, current, filter.Skip + state.Commits.Count, state.Commits.Count == filter.Take,
            generation is not null && generation != current);
    }
    Task<string> GetDiffAsync(string root, DiffRequest request, CancellationToken ct);
    Task<CommitDetails> GetCommitAsync(string root, string commit, CancellationToken ct);
    Task<ConflictContent> GetConflictAsync(string root, string path, CancellationToken ct);
    Task<FilePreview> GetPreviewAsync(string root, DiffRequest request, CancellationToken ct);
    Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsAsync(string root, CancellationToken ct);
    Task TrustAsync(string root, CancellationToken ct);
    Task<OperationOutcome> ExecuteAsync(string root, GitOperation operation,
        IProgress<OperationProgress>? progress, CancellationToken ct);
}
