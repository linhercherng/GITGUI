using GitGui.Core;

namespace GitGui.UI;

public sealed partial class MainWindowViewModel
{
    public async Task RefreshStatusAsync()
    {
        if (IsBusy || disposed || state is null) return;
        readCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IsRefreshing = true; IsBusy = true;
        try
        {
            var root = Root;
            var update = await workspace.RefreshStatusAsync(root, lifetime.Token);
            var policy = stageRules;
            var (groups, stageCounts) = await Task.Run(() => (GroupStatus(update.Status.Files), CountStagingExclusions(update.Status.Files, policy)), lifetime.Token);
            if (Root != root || disposed) return;
            var selectedPaths = SelectedFiles.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            var selectedPath = SelectedChange?.Path;
            state = state with { Status = update.Status, InProgress = update.InProgress };
            applyingReadState = true;
            try
            {
                Changes.ReplaceAll(update.Status.Files); (unstaged, staged) = groups;
                UpdateStagingExclusionCounts(stageCounts);
                RestoreFileSelection(selectedPaths, selectedPath);
            }
            finally { applyingReadState = false; }
            Branch = update.Status.Branch; InProgress = update.InProgress.IsActive;
            InProgressText = InProgress ? $"{update.InProgress.Kind} · {L[update.InProgress.HasConflicts ? "ResolveConflicts" : "ReadyToContinue"]}" : "";
            OnPropertyChanged(nameof(SelectedFiles));
            OnPropertyChanged(nameof(UnstagedChanges)); OnPropertyChanged(nameof(StagedChanges)); OnPropertyChanged(nameof(Summary)); Notice = Summary;
            if (!IsHistoryDiff) { await LoadDiffAsync(); await LoadConflictAsync(SelectedChange); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Notice = ex.Message; }
        finally { IsBusy = false; IsRefreshing = false; readCompletion.TrySetResult(); }
    }

    private sealed record DiffProjection(bool Large, string Left, string Right, IReadOnlyList<PatchSelection> Rows);
    private static DiffProjection ProjectDiff(string output, bool history, bool force)
    {
        if (!force && (output.Length > 1024 * 1024 || output.Count(c => c == '\n') > 20000)) return new(true, "", "", []);
        var lines = output.Split('\n');
        var left = string.Join('\n', lines.Select(l => l.StartsWith('+') && !l.StartsWith("+++", StringComparison.Ordinal) ? "" : l));
        var right = string.Join('\n', lines.Select(l => l.StartsWith('-') && !l.StartsWith("---", StringComparison.Ordinal) ? "" : l));
        var rows = new List<PatchSelection>();
        if (!history && !output.Contains("Binary files", StringComparison.Ordinal) && !output.Contains("GIT binary patch", StringComparison.Ordinal))
            foreach (var hunk in UnifiedPatch.Parse(output))
            {
                rows.Add(new() { HunkId = hunk.Id, Label = hunk.Header });
                foreach (var line in hunk.Lines.Where(l => l.IsChange))
                    rows.Add(new() { HunkId = hunk.Id, LineId = line.Id,
                        Label = $"  {line.OldLine?.ToString() ?? ""}:{line.NewLine?.ToString() ?? ""} {line.Kind}{line.Text}" });
            }
        return new(false, left, right, rows);
    }
    private async Task ShowDiffAsync(string output, DiffIdentity identity, CancellationToken ct, bool force = false)
    {
        var history = IsHistoryDiff;
        var projection = output.Length <= 16_384 ? ProjectDiff(output, history, force) :
            await Task.Run(() => ProjectDiff(output, history, force), ct);
        if (ct.IsCancellationRequested || disposed || identity != CurrentDiffIdentity()) return;
        sourceDiff = output; ApplyDiff(output, projection);
    }
    private void ApplyDiff(string output, DiffProjection projection)
    {
        IsLargeDiff = projection.Large;
        if (projection.Large) { Diff = LeftDiff = RightDiff = L["LargeDiff"]; PatchRows.ReplaceAll([]); return; }
        Diff = output; LeftDiff = projection.Left; RightDiff = projection.Right; PatchRows.ReplaceAll(projection.Rows);
    }
}
