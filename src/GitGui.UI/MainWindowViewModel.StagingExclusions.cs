using GitGui.Core;

namespace GitGui.UI;

public sealed partial class MainWindowViewModel
{
    private readonly IStageExclusionStore? stageExclusionStore;
    private StageExclusionRules stageRules = StageExclusionRules.Empty;
    private string? stageRulesRoot;
    private bool stageRulesLoadFailed, stageRulesSaving;
    private bool StageRulesReady => stageExclusionStore is null || stageRulesRoot == Root;
    public IReadOnlyList<StageExclusion> StagingExclusions => stageRules.Rules;
    public bool CanStageSelection => CanStageFiles && !stageRulesSaving && (IsRefreshing || StageRulesReady);
    public bool CanManageStagingExclusions => stageExclusionStore is not null && HasRepository && CanConfigure && !stageRulesSaving;
    public bool HasStagingExclusions => stageRules.Rules.Count > 0 || stageRulesLoadFailed;
    public bool SelectedStagingExcluded => !IsHistoryDiff && StageRulesReady && SelectedChange is { } change && stageRules.IsExcluded(change);
    public int ExcludedWorktreeCount { get; private set; }
    public int ExcludedStagedCount { get; private set; }
    public string StagingExclusionSummary => stageRulesLoadFailed ? L["StageExclusionsReadFailed"] :
        L.Format("StageExclusionsSummary", StagingExclusions.Count, ExcludedWorktreeCount, ExcludedStagedCount);

    private async Task<StageExclusionRules?> ReadStagingExclusionsAsync(string root)
    {
        if (stageExclusionStore is null) return StageExclusionRules.Empty;
        try
        {
            return await Task.Run(async () => new StageExclusionRules(
                await stageExclusionStore.LoadStageExclusionsAsync(root, lifetime.Token)), lifetime.Token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    private static (int Worktree, int Staged) CountStagingExclusions(IReadOnlyList<FileChange> files, StageExclusionRules? policy)
    {
        if (policy is null || policy.Rules.Count == 0) return (0, 0);
        var worktree = 0; var staged = 0;
        foreach (var file in files)
        {
            if (!policy.IsExcluded(file)) continue;
            if (file.WorktreeStatus is not "." and not " ") worktree++;
            if (file.IndexStatus is not "." and not " " and not "?") staged++;
        }
        return (worktree, staged);
    }

    private void ApplyStagingExclusions(string root, StageExclusionRules? policy, (int Worktree, int Staged) counts)
    {
        stageRules = policy ?? StageExclusionRules.Empty;
        stageRulesRoot = policy is null ? null : root;
        stageRulesLoadFailed = policy is null;
        UpdateStagingExclusionCounts(counts);
    }

    private void UpdateStagingExclusionCounts((int Worktree, int Staged) counts)
    {
        ExcludedWorktreeCount = counts.Worktree; ExcludedStagedCount = counts.Staged;
        NotifyStagingExclusions();
    }

    private void NotifyStagingExclusions()
    {
        OnPropertyChanged(nameof(StagingExclusions)); OnPropertyChanged(nameof(CanStageSelection));
        OnPropertyChanged(nameof(CanManageStagingExclusions)); OnPropertyChanged(nameof(HasStagingExclusions));
        OnPropertyChanged(nameof(SelectedStagingExcluded)); OnPropertyChanged(nameof(StagingExclusionSummary));
        OnPropertyChanged(nameof(ExcludedWorktreeCount)); OnPropertyChanged(nameof(ExcludedStagedCount));
    }

    public async Task SaveStagingExclusionsAsync(IReadOnlyList<StageExclusion> rules)
    {
        if (!CanManageStagingExclusions || stageExclusionStore is null) throw new InvalidOperationException(L["StageExclusionsUnavailable"]);
        var root = Root; var requested = rules.ToArray();
        stageRulesSaving = true; NotifyStagingExclusions();
        try
        {
            var files = Changes.ToArray();
            var (policy, counts) = await Task.Run(() =>
            {
                var prepared = new StageExclusionRules(requested);
                return (prepared, CountStagingExclusions(files, prepared));
            }, lifetime.Token);
            await Task.Run(() => stageExclusionStore.SaveStageExclusionsAsync(root, policy.Rules, lifetime.Token), lifetime.Token);
            if (!disposed && Root == root) ApplyStagingExclusions(root, policy, counts);
        }
        finally { stageRulesSaving = false; NotifyStagingExclusions(); }
    }

    private void EnsureStagingExclusionsReady()
    {
        if (!StageRulesReady || stageRulesSaving) throw new InvalidOperationException(L["StageExclusionsUnavailable"]);
    }

    private string[] StagingPaths(IEnumerable<FileChange> changes)
    {
        EnsureStagingExclusionsReady();
        var paths = new List<string>(); var requested = 0;
        foreach (var change in changes)
        { requested++; if (!stageRules.IsExcluded(change)) paths.Add(change.Path); }
        if (requested > 0 && paths.Count == 0) throw new InvalidOperationException(L["StageExclusionsNothingToStage"]);
        return paths.ToArray();
    }
}
