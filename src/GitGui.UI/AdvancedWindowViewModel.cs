using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitGui.Core;

namespace GitGui.UI;

public sealed partial class RebaseStepViewModel : ObservableObject
{
    public string Commit { get; }
    public string ShortCommit => Commit[..Math.Min(10, Commit.Length)];
    [ObservableProperty] private RebaseStepKind action;
    [ObservableProperty] private string message;
    public RebaseStepViewModel(RebaseStep step) { Commit = step.Commit; action = step.Action; message = step.Message ?? ""; }
    public RebaseStep ToStep() => new(Commit, Action, string.IsNullOrWhiteSpace(Message) ? null : Message);
}

public sealed partial class AdvancedWindowViewModel : ObservableObject, IDisposable
{
    private readonly IWorkspaceService workspace;
    private readonly IAdvancedWorkspaceService advanced;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? fileRequest;
    private string? loadedUpstream;
    private HashSet<string> planCommits = new(StringComparer.Ordinal);
    private bool disposed;
    public string Root { get; }
    public Localization L { get; }
    public OperationFeedback Feedback { get; }
    public event EventHandler<OperationOutcome>? WorkspaceChanged;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isWriting;
    [ObservableProperty] private string notice = "";
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private string stashMessage = "";
    [ObservableProperty] private bool includeUntracked;
    [ObservableProperty] private StashInfo? selectedStash;
    [ObservableProperty] private string upstream = "";
    [ObservableProperty] private RebaseStepViewModel? selectedStep;
    [ObservableProperty] private string revision = "HEAD";
    [ObservableProperty] private ResetMode resetMode = ResetMode.Mixed;
    [ObservableProperty] private string tagName = "";
    [ObservableProperty] private string tagMessage = "";
    [ObservableProperty] private bool annotatedTag = true;
    [ObservableProperty] private bool signTag;
    [ObservableProperty] private TagInfo? selectedTag;
    [ObservableProperty] private ReflogInfo? selectedReflog;
    [ObservableProperty] private string remoteName = "";
    [ObservableProperty] private string newRemoteName = "";
    [ObservableProperty] private string remoteUrl = "";
    [ObservableProperty] private bool pushUrl;
    [ObservableProperty] private AdvancedRemoteInfo? selectedRemote;
    [ObservableProperty] private string filePath = "";
    [ObservableProperty] private string fileBackend = "";
    [ObservableProperty] private FileHistoryEntry? selectedFileCommit;
    [ObservableProperty] private InProgressState inProgress = InProgressState.None;
    [ObservableProperty] private OperationOutcome? lastOutcome;
    public ObservableCollection<StashInfo> Stashes { get; } = [];
    public ObservableCollection<RebaseStepViewModel> Plan { get; } = [];
    public ObservableCollection<TagInfo> Tags { get; } = [];
    public ObservableCollection<ReflogInfo> Reflog { get; } = [];
    public ObservableCollection<AdvancedRemoteInfo> Remotes { get; } = [];
    public ObservableCollection<BlameLine> Blame { get; } = [];
    public ObservableCollection<FileHistoryEntry> FileHistory { get; } = [];
    public IReadOnlyList<RebaseStepKind> StepKinds { get; } = Enum.GetValues<RebaseStepKind>();
    public IReadOnlyList<ResetMode> ResetModes { get; } = Enum.GetValues<ResetMode>();
    public bool CanOperate => !IsBusy;
    public bool HasActiveOperation => InProgress.IsActive;
    public string ActiveOperationText => InProgress.IsActive ? $"{InProgress.Kind} · {L[InProgress.HasConflicts ? "ResolveConflicts" : "ReadyToContinue"]}" : "";
    public bool CanContinue => !IsBusy && InProgress.IsActive && !InProgress.HasConflicts;
    public bool CanSkip => !IsBusy && InProgress.Kind is "rebase" or "cherry-pick" or "revert";
    public bool CanAbort => !IsBusy && InProgress.IsActive;
    public bool CanUndo => !IsBusy && LastOutcome?.Snapshot is not null;
    public string SelectedRemoteUrls => SelectedRemote is null ? "" : "fetch: " + string.Join("\nfetch: ", SelectedRemote.FetchUrls) + "\npush: " + string.Join("\npush: ", SelectedRemote.PushUrls);
    public string SelectedTagDescription => SelectedTag is null ? "" : $"{SelectedTag.Name}\n{SelectedTag.Target}\n{SelectedTag.Message}";

    public AdvancedWindowViewModel(IWorkspaceService workspace, IAdvancedWorkspaceService advanced, string root,
        string? path = null, string? revision = null, Localization? localization = null)
    {
        this.workspace = workspace; this.advanced = advanced; Root = Path.GetFullPath(root); L = localization ?? new();
        Feedback = new(L, () => CanOperate);
        FilePath = path ?? ""; Revision = revision ?? "HEAD"; Notice = L["AdvancedHint"];
        L.PropertyChanged += LocaleChanged;
    }
    private void LocaleChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { OnPropertyChanged(nameof(ActiveOperationText)); OnPropertyChanged(nameof(SelectedRemoteUrls)); }
    partial void OnIsBusyChanged(bool value)
    {
        Feedback?.Notify();
        OnPropertyChanged(nameof(CanOperate)); OnPropertyChanged(nameof(CanContinue)); OnPropertyChanged(nameof(CanSkip)); OnPropertyChanged(nameof(CanAbort)); OnPropertyChanged(nameof(CanUndo));
    }
    partial void OnInProgressChanged(InProgressState value)
    {
        OnPropertyChanged(nameof(HasActiveOperation)); OnPropertyChanged(nameof(ActiveOperationText)); OnPropertyChanged(nameof(CanContinue)); OnPropertyChanged(nameof(CanSkip)); OnPropertyChanged(nameof(CanAbort));
    }
    partial void OnLastOutcomeChanged(OperationOutcome? value) => OnPropertyChanged(nameof(CanUndo));
    partial void OnUpstreamChanged(string value) { loadedUpstream = null; Plan.Clear(); SelectedStep = null; planCommits.Clear(); }
    partial void OnSelectedTagChanged(TagInfo? value) { if (value is not null) TagName = value.Name; OnPropertyChanged(nameof(SelectedTagDescription)); }
    partial void OnSelectedReflogChanged(ReflogInfo? value) { if (value is not null) Revision = value.Oid; }
    partial void OnSelectedFileCommitChanged(FileHistoryEntry? value) { if (value is not null) Revision = value.Commit.Hash; }
    partial void OnSelectedRemoteChanged(AdvancedRemoteInfo? value)
    {
        if (value is not null) { RemoteName = value.Name; RemoteUrl = (PushUrl ? value.PushUrls : value.FetchUrls).FirstOrDefault() ?? ""; }
        OnPropertyChanged(nameof(SelectedRemoteUrls));
    }
    partial void OnPushUrlChanged(bool value) { if (SelectedRemote is not null) RemoteUrl = (value ? SelectedRemote.PushUrls : SelectedRemote.FetchUrls).FirstOrDefault() ?? ""; }

    [RelayCommand] private async Task RefreshAsync()
    {
        if (IsBusy || disposed) return;
        IsBusy = true;
        try { await RefreshStateAsync(); if (!disposed) Notice = L["AdvancedReady"]; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Notice = ex.Message; }
        finally { IsBusy = false; }
    }
    private async Task RefreshStateAsync()
    {
        var state = await advanced.GetAdvancedStateAsync(Root, lifetime.Token);
        if (disposed || lifetime.IsCancellationRequested) return;
        var stash = SelectedStash?.Oid; var tag = SelectedTag?.Name; var remote = SelectedRemote?.Name;
        Replace(Stashes, state.Stashes); Replace(Tags, state.Tags); Replace(Reflog, state.Reflog); Replace(Remotes, state.Remotes);
        SelectedStash = Stashes.FirstOrDefault(s => s.Oid == stash); SelectedTag = Tags.FirstOrDefault(t => t.Name == tag); SelectedRemote = Remotes.FirstOrDefault(r => r.Name == remote);
        InProgress = state.InProgress;
    }
    [RelayCommand] private async Task LoadPlanAsync()
    {
        if (IsBusy || disposed) return;
        IsBusy = true;
        try
        {
            var requested = Require(Upstream, "AdvancedUpstreamRequired");
            var steps = await advanced.GetRebasePlanAsync(Root, requested, lifetime.Token);
            if (disposed || lifetime.IsCancellationRequested || Upstream.Trim() != requested) return;
            Replace(Plan, steps.Select(s => new RebaseStepViewModel(s)));
            planCommits = steps.Select(s => s.Commit).ToHashSet(StringComparer.Ordinal); loadedUpstream = requested;
            SelectedStep = Plan.FirstOrDefault(); Notice = $"{Plan.Count} · {L["AdvancedPlanLoaded"]}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { loadedUpstream = null; Plan.Clear(); Notice = ex.Message; }
        finally { IsBusy = false; }
    }
    [RelayCommand] private void MoveUp() { if (SelectedStep is not null) MoveStep(Plan.IndexOf(SelectedStep), Plan.IndexOf(SelectedStep) - 1); }
    [RelayCommand] private void MoveDown() { if (SelectedStep is not null) MoveStep(Plan.IndexOf(SelectedStep), Plan.IndexOf(SelectedStep) + 1); }
    public void MoveStep(int from, int to)
    {
        if (IsBusy || from < 0 || from >= Plan.Count || to < 0 || to >= Plan.Count || from == to) return;
        Plan.Move(from, to); SelectedStep = Plan[to];
    }
    [RelayCommand] private async Task LoadFileAsync()
    {
        if (IsBusy || disposed) return;
        fileRequest?.Cancel(); fileRequest?.Dispose();
        var request = fileRequest = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        IsBusy = true;
        try
        {
            var path = Require(FilePath, "SelectFile");
            var result = await advanced.GetFileHistoryAsync(Root, path, request.Token);
            if (request.IsCancellationRequested || FilePath.Trim() != path) return;
            Replace(Blame, result.Blame); Replace(FileHistory, result.History); FileBackend = result.Backend; Notice = L["AdvancedFileLoaded"];
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Blame.Clear(); FileHistory.Clear(); FileBackend = ""; Notice = ex.Message; }
        finally { IsBusy = false; }
    }
    [RelayCommand] private async Task ExecuteAsync(string? action)
    {
        if (IsBusy || disposed || action is null) return;
        try { await RunAsync(CreateOperation(action)); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Notice = ex.Message; }
    }
    [RelayCommand] private async Task UndoAsync()
    {
        if (!CanUndo || LastOutcome?.Snapshot is not { } snapshot) return;
        await RunAsync(new(GitAction.RestoreSnapshot) { SnapshotId = snapshot.Id });
    }
    public GitOperation CreateOperation(string action)
    {
        if (IsBusy) throw new InvalidOperationException(L["AdvancedWait"]);
        return action switch
        {
            "StashPush" => new(GitAction.StashPush) { Message = StashMessage, IncludeUntracked = IncludeUntracked },
            "StashApply" => new(GitAction.StashApply) { StashOid = RequireStash(), Name = SelectedStash!.Selector },
            "StashPop" => new(GitAction.StashPop) { StashOid = RequireStash(), Name = SelectedStash!.Selector },
            "StashDrop" => new(GitAction.StashDrop) { StashOid = RequireStash(), Name = SelectedStash!.Selector },
            "RebaseInteractive" => RebaseOperation(),
            "CherryPick" => new(GitAction.CherryPick) { Revision = Require(Revision, "AdvancedRevisionRequired") },
            "Revert" => new(GitAction.Revert) { Revision = Require(Revision, "AdvancedRevisionRequired") },
            "Reset" => new(GitAction.Reset) { Revision = Require(Revision, "AdvancedRevisionRequired"), ResetMode = ResetMode },
            "CreateTag" => new(GitAction.CreateTag) { Name = Require(TagName, "AdvancedTagRequired"), Revision = Require(Revision, "AdvancedRevisionRequired"), Annotated = AnnotatedTag || SignTag, Sign = SignTag, Message = AnnotatedTag || SignTag ? Require(TagMessage, "AdvancedTagMessageRequired") : null },
            "DeleteTag" => new(GitAction.DeleteTag) { Name = Require(TagName, "AdvancedTagRequired") },
            "AddRemote" => new(GitAction.AddRemote) { Name = Require(RemoteName, "AdvancedRemoteRequired"), Url = Require(RemoteUrl, "UrlRequired") },
            "RenameRemote" => new(GitAction.RenameRemote) { Name = Require(RemoteName, "AdvancedRemoteRequired"), NewName = Require(NewRemoteName, "NewNameRequired") },
            "SetRemoteUrl" => new(GitAction.SetRemoteUrl) { Name = Require(RemoteName, "AdvancedRemoteRequired"), Url = Require(RemoteUrl, "UrlRequired"), PushUrl = PushUrl },
            "RemoveRemote" => new(GitAction.RemoveRemote) { Name = Require(RemoteName, "AdvancedRemoteRequired") },
            "ContinueOperation" => CanContinue ? new(GitAction.ContinueOperation) : throw new InvalidOperationException(L["AdvancedResolveFirst"]),
            "SkipOperation" => CanSkip ? new(GitAction.SkipOperation) : throw new InvalidOperationException(L["AdvancedNoOperation"]),
            "AbortOperation" => CanAbort ? new(GitAction.AbortOperation) : throw new InvalidOperationException(L["AdvancedNoOperation"]),
            _ => throw new InvalidOperationException(action)
        };
    }
    private GitOperation RebaseOperation()
    {
        var requested = Require(Upstream, "AdvancedUpstreamRequired");
        if (loadedUpstream != requested || Plan.Count == 0 || Plan.Count != planCommits.Count || !planCommits.SetEquals(Plan.Select(s => s.Commit))) throw new InvalidOperationException(L["AdvancedLoadPlanFirst"]);
        if (Plan.Any(s => s.Action == RebaseStepKind.Reword && string.IsNullOrWhiteSpace(s.Message))) throw new InvalidOperationException(L["AdvancedRewordRequired"]);
        var first = Plan.FirstOrDefault(s => s.Action != RebaseStepKind.Drop);
        if (first?.Action is RebaseStepKind.Squash or RebaseStepKind.Fixup) throw new InvalidOperationException(L["AdvancedFirstCannotSquash"]);
        return new(GitAction.RebaseInteractive) { Revision = requested, RebasePlan = Plan.Select(s => s.ToStep()).ToArray() };
    }
    private string RequireStash() => SelectedStash?.Oid ?? throw new InvalidOperationException(L["AdvancedSelectStash"]);
    private string Require(string value, string key) => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(L[key]) : value.Trim();
    private async Task RunAsync(GitOperation operation)
    {
        IsBusy = IsWriting = true; ProgressText = operation.Action.ToString();
        try
        {
            var progress = new Progress<OperationProgress>(p => { if (!disposed && IsWriting) ProgressText = p.Phase; });
            var result = await workspace.ExecuteAsync(Root, operation, progress, lifetime.Token);
            Feedback.Record(result, () => CanOperate ? RunAsync(operation with { SkipHooks = false, Force = false }) : Task.CompletedTask);
            if (disposed) return;
            LastOutcome = result;
            try { await RefreshStateAsync(); }
            catch (Exception ex) { Notice = result.Message + "\n" + ex.Message; WorkspaceChanged?.Invoke(this, result); return; }
            Notice = (result.Success ? L["Completed"] : result.Cancelled ? L["Cancelled"] : result.Error.ToString()) + " · " + result.Message;
            if (result.Success && operation.Action == GitAction.StashPush) StashMessage = "";
            WorkspaceChanged?.Invoke(this, result);
        }
        catch (OperationCanceledException) { Notice = L["Cancelled"]; }
        catch (Exception ex) { Notice = ex.Message; }
        finally { IsWriting = IsBusy = false; ProgressText = ""; }
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values) { target.Clear(); foreach (var value in values) target.Add(value); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Feedback.Dispose(); L.PropertyChanged -= LocaleChanged; lifetime.Cancel(); fileRequest?.Cancel(); fileRequest?.Dispose(); lifetime.Dispose();
    }
    public Task ExecuteSuggestedAsync(GitAction action) => CanOperate && action is GitAction.Fetch or GitAction.Pull ? RunAsync(new(action)) : Task.CompletedTask;
}
