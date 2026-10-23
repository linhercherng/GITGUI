namespace GitGui.Core;

public sealed record WorkspaceStatus(string Root, RepoStatus Status, InProgressState InProgress);
public sealed record HistoryPage(IReadOnlyList<CommitInfo> Commits, string Generation, int NextSkip, bool HasMore, bool Reset = false);
