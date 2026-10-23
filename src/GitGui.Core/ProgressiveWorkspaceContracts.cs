namespace GitGui.Core;

/// <summary>A read-only history preview. It cannot authorize repository operations.</summary>
public sealed record WorkspaceHistoryPreview(string Root, HistoryPage Page);

public interface IProgressiveWorkspaceService : IWorkspaceService
{
    Task<WorkspaceState> OpenWithHistoryAsync(string path, LogFilter filter,
        Func<WorkspaceHistoryPreview, Task> publishHistory, CancellationToken ct);
}
