namespace GitGui.Core;

public enum RepositoryToolAction
{
    SubmoduleAdd, SubmoduleInitialize, SubmoduleUpdate, SubmoduleSync, SubmoduleDeinitialize,
    WorktreeAdd, WorktreeRemove, WorktreeLock, WorktreeUnlock, WorktreePrune,
    LfsInstall, LfsTrack, LfsUntrack, LfsFetch, LfsPull,
    BisectStart, BisectGood, BisectBad, BisectSkip, BisectReset,
    SparseSet, SparseDisable
}

public sealed record RepositoryToolOperation(RepositoryToolAction Action)
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public string Revision { get; init; } = "HEAD";
    public string GoodRevision { get; init; } = "";
    public string Url { get; init; } = "";
    public string Reason { get; init; } = "";
    public IReadOnlyList<string> Patterns { get; init; } = [];
    public bool Cone { get; init; } = true;
    public bool Recursive { get; init; } = true;
}

public sealed record SubmoduleInfo(string Path, string Commit, string State);
public sealed record WorktreeInfo(string Path, string Head, string Branch, bool Bare, bool Detached,
    bool Locked, string LockReason, bool Prunable);
public sealed record RepositoryToolsState(IReadOnlyList<SubmoduleInfo> Submodules,
    IReadOnlyList<WorktreeInfo> Worktrees, string LfsVersion, string LfsStatus,
    bool BisectActive, string BisectLog, bool SparseEnabled, bool SparseCone,
    IReadOnlyList<string> SparsePatterns);

public interface IRepositoryToolsService
{
    Task<RepositoryToolsState> GetStateAsync(string root, CancellationToken ct);
    Task<OperationOutcome> ExecuteAsync(string root, RepositoryToolOperation operation,
        IProgress<OperationProgress>? progress, CancellationToken ct);
}
