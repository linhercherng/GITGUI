using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitGui.Core;

namespace GitGui.UI;

public sealed partial class PatchSelection : ObservableObject
{
    public int HunkId { get; init; }
    public int? LineId { get; init; }
    public string Label { get; init; } = "";
    [ObservableProperty] private bool selected;
}

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IWorkspaceService workspace;
    private readonly IRecentRepositories recent;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? selectionRequest;
    private CancellationTokenSource? commitRequest;
    private CancellationTokenSource? conflictRequest;
    private CancellationTokenSource? operationRequest;
    private WorkspaceState? state;
    private string sourceDiff = "";
    private DiffIdentity? sourceIdentity;
    private (string Root, string Path)? loadedConflict;
    private bool assigningConflict;
    private readonly Dictionary<(string Root, string Path), ConflictDraft> conflictDrafts = [];
    private sealed record ConflictDraft(string Base, string Ours, string Theirs, string Result, long Revision);
    private long conflictRevision;
    private int historyLimit = 500;
    private string? historyGeneration;
    private bool applyingReadState;
    private bool openingHistoryPreview;
    private IReadOnlyList<FileChange> unstaged = [];
    private IReadOnlyList<FileChange> staged = [];
    private CommitMessageEncoding selectedEncoding = CommitMessageEncoding.Utf8;
    private bool disposed;
    private string? undoId;
    private string? redoId;
    public Localization L { get; } = new();
    public OperationFeedback Feedback { get; }
    public Func<Task<bool>>? ConfirmSkipHooks { get; set; }
    [ObservableProperty] private bool english;
    [ObservableProperty] private string repositoryPath = "";
    [ObservableProperty] private string branch = "—";
    [ObservableProperty] private string tracking = "";
    [ObservableProperty] private string notice = "";
    [ObservableProperty] private string diff = "";
    [ObservableProperty] private string leftDiff = "";
    [ObservableProperty] private string rightDiff = "";
    [ObservableProperty] private string commands = "";
    [ObservableProperty] private string commitMessage = "";
    [ObservableProperty] private string commitDetails = "";
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string authorFilter = "";
    [ObservableProperty] private string pathFilter = "";
    [ObservableProperty] private string sinceFilter = "";
    [ObservableProperty] private string untilFilter = "";
    [ObservableProperty] private string branchName = "";
    [ObservableProperty] private string newBranchName = "";
    [ObservableProperty] private string cloneUrl = "";
    [ObservableProperty] private string remoteName = "origin";
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private int progressPercent;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))] private bool hasProgressPercent;
    [ObservableProperty] private bool canCancel;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))] private bool isBusy;
    public bool IsProgressIndeterminate => IsBusy && !HasProgressPercent;
    [ObservableProperty] private bool hasRepository;
    [ObservableProperty] private bool showStaged;
    [ObservableProperty] private bool splitDiff;
    [ObservableProperty] private bool isLargeDiff;
    [ObservableProperty] private bool isDiffLoading;
    [ObservableProperty] private bool hasConflict;
    [ObservableProperty] private bool conflictBinary;
    [ObservableProperty] private bool conflictLoading;
    [ObservableProperty] private bool conflictDirty;
    [ObservableProperty] private string conflictBase = "";
    [ObservableProperty] private string conflictOurs = "";
    [ObservableProperty] private string conflictTheirs = "";
    [ObservableProperty] private string conflictResult = "";
    [ObservableProperty] private ConflictSection? selectedConflictSection;
    [ObservableProperty] private long? beforeSize;
    [ObservableProperty] private long? afterSize;
    [ObservableProperty] private byte[]? beforeImage;
    [ObservableProperty] private byte[]? afterImage;
    [ObservableProperty] private string previewNotice = "";
    [ObservableProperty] private bool amend;
    [ObservableProperty] private bool skipHooks;
    [ObservableProperty] private bool signCommit;
    [ObservableProperty] private bool forcePush;
    [ObservableProperty] private bool forceDelete;
    [ObservableProperty] private bool isHistoryDiff;
    [ObservableProperty] private bool inProgress;
    [ObservableProperty] private string inProgressText = "";
    [ObservableProperty] private bool isUntrusted;
    [ObservableProperty] private string trustDetails = "";
    [ObservableProperty] private bool canUndo;
    [ObservableProperty] private bool canRedo;
    [ObservableProperty] private FileChange? selectedChange;
    [ObservableProperty] private FileChange? selectedCommitFile;
    [ObservableProperty] private CommitInfo? selectedCommit;
    [ObservableProperty] private RefInfo? selectedRef;
    [ObservableProperty] private SnapshotInfo? selectedSnapshot;
    public BatchObservableCollection<FileChange> Changes { get; } = [];
    public IEnumerable<FileChange> UnstagedChanges => unstaged;
    public IEnumerable<FileChange> StagedChanges => staged;
    public ObservableCollection<FileChange> SelectedFiles { get; } = [];
    public BatchObservableCollection<FileChange> CommitFiles { get; } = [];
    public BatchObservableCollection<CommitInfo> Commits { get; } = [];
    public BatchObservableCollection<RefInfo> Refs { get; } = [];
    public BatchObservableCollection<RemoteInfo> Remotes { get; } = [];
    public BatchObservableCollection<SnapshotInfo> Snapshots { get; } = [];
    public BatchObservableCollection<PatchSelection> PatchRows { get; } = [];
    public BatchObservableCollection<ConflictSection> ConflictSections { get; } = [];
    public BatchObservableCollection<string> Recent { get; } = [];
    public string SelectedEncodingText => L["Encoding" + selectedEncoding.Source] + " · " + selectedEncoding.Name;
    public string RepositoryTextSettingsText => string.Format(L["AutoCrlfSummary"], state?.TextSettings.AutoCrlf ?? "unset", state?.TextSettings.SafeCrlf ?? "unset", state?.TextSettings.Eol ?? "unset");
    public string Root => state?.Root ?? RepositoryPath.Trim();
    public string Summary => $"{Changes.Count} {L["Changes"]}  ·  {Commits.Count} {L["Commits"]}";
    [ObservableProperty] private bool terminalActive;
    [ObservableProperty] private bool externalWriteActive;
    partial void OnExternalWriteActiveChanged(bool value) { Feedback?.Notify(); OnPropertyChanged(nameof(CanConfigure)); OnPropertyChanged(nameof(CanOperate)); OnPropertyChanged(nameof(CanApplyPatch)); OnPropertyChanged(nameof(CanSaveConflict)); }
    partial void OnTerminalActiveChanged(bool value) { Feedback?.Notify(); OnPropertyChanged(nameof(CanConfigure)); OnPropertyChanged(nameof(CanOperate)); OnPropertyChanged(nameof(CanApplyPatch)); OnPropertyChanged(nameof(CanSaveConflict)); }
    public bool CanConfigure => !IsBusy && !TerminalActive && !ExternalWriteActive;
    public bool CanOperate => CanConfigure && HasRepository;
    public bool CanApplyPatch => CanOperate && SelectedChange?.IsPathSupported != false && !IsDiffLoading && !IsHistoryDiff && !IsLargeDiff && sourceIdentity is not null && sourceIdentity == CurrentDiffIdentity();
    public bool CanSaveConflict => CanOperate && SelectedChange?.IsPathSupported != false && HasConflict && !ConflictLoading && !ConflictBinary;
    public bool HasImages => BeforeImage is not null || AfterImage is not null;
    public string PreviewSummary => BeforeSize is null && AfterSize is null ? "" : $"{L["Before"]}: {BeforeSize?.ToString("N0") ?? "—"} B  →  {L["After"]}: {AfterSize?.ToString("N0") ?? "—"} B";
    public string DiffTitle => IsHistoryDiff ? SelectedCommitFile?.Path ?? L["CommitDetails"] : SelectedChange?.Path ?? L["SelectFile"];
    public event EventHandler? RepositoryOpened;

    public MainWindowViewModel(IWorkspaceService workspace, IRecentRepositories recent)
    {
        this.workspace = workspace;
        this.recent = recent;
        Feedback = new(L, () => CanConfigure);
        Notice = L["OpenRepositoryHint"];
        workspace.CommandExecuted += CommandExecuted;
    }
    partial void OnEnglishChanged(bool value)
    {
        var initialNotice = Notice == L["OpenRepositoryHint"];
        L.SetCulture(value ? "en" : "zh-TW"); if (initialNotice) Notice = L["OpenRepositoryHint"];
        OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(DiffTitle)); OnPropertyChanged(nameof(PreviewSummary));
        OnPropertyChanged("SelectedEncodingText"); OnPropertyChanged("RepositoryTextSettingsText");
        if (state is not null) InProgressText = state.InProgress.IsActive ? $"{state.InProgress.Kind} · {L[state.InProgress.HasConflicts ? "ResolveConflicts" : "ReadyToContinue"]}" : "";
    }
    partial void OnIsBusyChanged(bool value) { Feedback?.Notify(); OnPropertyChanged(nameof(CanConfigure)); OnPropertyChanged(nameof(CanOperate)); OnPropertyChanged(nameof(CanApplyPatch)); OnPropertyChanged(nameof(CanSaveConflict)); }
    partial void OnHasRepositoryChanged(bool value) { OnPropertyChanged(nameof(CanOperate)); OnPropertyChanged(nameof(CanApplyPatch)); OnPropertyChanged(nameof(CanSaveConflict)); }
    partial void OnIsDiffLoadingChanged(bool value) => OnPropertyChanged(nameof(CanApplyPatch));
    partial void OnIsLargeDiffChanged(bool value) => OnPropertyChanged(nameof(CanApplyPatch));
    partial void OnIsHistoryDiffChanged(bool value) => OnPropertyChanged(nameof(CanApplyPatch));
    partial void OnHasConflictChanged(bool value) => OnPropertyChanged(nameof(CanSaveConflict));
    partial void OnConflictLoadingChanged(bool value) => OnPropertyChanged(nameof(CanSaveConflict));
    partial void OnConflictBinaryChanged(bool value) => OnPropertyChanged(nameof(CanSaveConflict));
    partial void OnBeforeSizeChanged(long? value) => OnPropertyChanged(nameof(PreviewSummary));
    partial void OnAfterSizeChanged(long? value) => OnPropertyChanged(nameof(PreviewSummary));
    partial void OnBeforeImageChanged(byte[]? value) => OnPropertyChanged(nameof(HasImages));
    partial void OnAfterImageChanged(byte[]? value) => OnPropertyChanged(nameof(HasImages));
    partial void OnConflictResultChanged(string value)
    {
        Replace(ConflictSections, ConflictMarkers.Parse(value));
        SelectedConflictSection = ConflictSections.FirstOrDefault();
        if (!assigningConflict && loadedConflict is { } key)
        {
            ConflictDirty = true;
            conflictDrafts[key] = new(ConflictBase, ConflictOurs, ConflictTheirs, value, ++conflictRevision);
        }
    }
    partial void OnSelectedRefChanged(RefInfo? value) { if (value is not null) BranchName = value.Name; }
    partial void OnSelectedChangeChanged(FileChange? value) { if (applyingReadState) return; InvalidateCommitSelection(); IsHistoryDiff = false; _ = LoadDiffAsync(); _ = LoadConflictAsync(value); }
    partial void OnSelectedCommitFileChanged(FileChange? value) { if (value is null || applyingReadState) return; IsHistoryDiff = true; HasConflict = false; CancelRead(ref conflictRequest); ConflictLoading = false; _ = LoadDiffAsync(); }
    partial void OnShowStagedChanged(bool value)
    {
        InvalidateCommitSelection();
        var visible = value ? StagedChanges : UnstagedChanges;
        if (SelectedChange is null || !visible.Contains(SelectedChange)) SelectedChange = visible.FirstOrDefault();
        IsHistoryDiff = false; _ = LoadDiffAsync();
    }
    partial void OnSelectedCommitChanged(CommitInfo? value) { if (!applyingReadState && !openingHistoryPreview && value is not null) { HasConflict = false; CancelRead(ref conflictRequest); ConflictLoading = false; _ = LoadCommitAsync(value); } }
    private void InvalidateCommitSelection()
    {
        CancelRead(ref commitRequest);
        SelectedCommit = null; SelectedCommitFile = null;
        CommitFiles.Clear(); CommitDetails = "";
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        try { Replace(Recent, await recent.ListAsync(lifetime.Token)); }
        catch (Exception ex) { Notice = ex.Message; }
    }
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy || disposed) return;
        IsBusy = true;
        try { await RefreshStateAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ClearState(); Notice = ex.Message; }
        finally { IsBusy = false; }
    }
    private async Task RefreshStateAsync()
    {
        try { await ReadStateAsync(); }
        catch { ClearState(); throw; }
    }
    private async Task ReadStateAsync()
    {
        if (string.IsNullOrWhiteSpace(RepositoryPath)) throw new InvalidOperationException(L["PathRequired"]);
        var previous = state?.Root;
        var wasHistoryDiff = IsHistoryDiff;
        var previousSelectedPath = SelectedChange?.Path;
        var previousSelectedHash = SelectedCommit?.Hash;
        var requestedPath = RepositoryPath.Trim();
        WorkspaceHistoryPreview? preview = null;
        WorkspaceState opened;
        openingHistoryPreview = true;
        try
        {
            opened = workspace is IProgressiveWorkspaceService progressive
                ? await progressive.OpenWithHistoryAsync(requestedPath, CurrentFilter(), PublishHistory, lifetime.Token)
                : await workspace.OpenAsync(requestedPath, CurrentFilter(), lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
        }
        finally { openingHistoryPreview = false; }
        async Task PublishHistory(WorkspaceHistoryPreview value)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (disposed || lifetime.IsCancellationRequested || RepositoryPath.Trim() != requestedPath) return;
                if (previous == value.Root && historyGeneration is not null && historyGeneration == value.Page.Generation) return;
                // This is a display-only preview: state/trust and all write controls stay pending.
                CancelRead(ref commitRequest);
                applyingReadState = true;
                try { SelectedCommit = null; Replace(Commits, value.Page.Commits); }
                finally { applyingReadState = false; }
                preview = value;
            });
        }
        var groups = await Task.Run(() => GroupStatus(opened.Status.Files), lifetime.Token);
        state = opened;
        RepositoryPath = state.Root;
        Branch = state.Status.Branch;
        Tracking = state.Refs.FirstOrDefault(r => r.IsCurrent)?.Tracking ?? "";
        HasRepository = true;
        IsUntrusted = !state.IsTrusted;
        TrustDetails = string.Join("\n", state.RiskySettings);
        InProgress = state.InProgress.IsActive;
        InProgressText = state.InProgress.IsActive ? $"{state.InProgress.Kind} · {L[state.InProgress.HasConflicts ? "ResolveConflicts" : "ReadyToContinue"]}" : "";
        var selectedPath = previous == state.Root ? previousSelectedPath : null;
        var selectedHash = previous == state.Root ? previousSelectedHash : null;
        var savedSnapshots = await workspace.GetSnapshotsAsync(Root, lifetime.Token);
        applyingReadState = true;
        Replace(Changes, state.Status.Files);
        (unstaged, staged) = groups;
        OnPropertyChanged(nameof(UnstagedChanges)); OnPropertyChanged(nameof(StagedChanges));
        SelectedFiles.Clear();
        SelectedChange = Changes.FirstOrDefault(f => f.Path == selectedPath) ?? (ShowStaged ? staged : unstaged).FirstOrDefault();
        if (!ReferenceEquals(preview?.Page.Commits, state.Commits) &&
            (historyGeneration is null || historyGeneration != state.HistoryGeneration || previous != state.Root))
            Replace(Commits, state.Commits);
        historyGeneration = state.HistoryGeneration;
        Replace(Refs, state.Refs);
        Replace(Remotes, state.Remotes);
        Replace(Snapshots, savedSnapshots);
        if (Remotes.Count > 0 && !Remotes.Any(r => r.Name == RemoteName)) RemoteName = Remotes[0].Name;
        SelectedCommit = Commits.FirstOrDefault(c => c.Hash == selectedHash);
        applyingReadState = false;
        OnPropertyChanged(nameof(RepositoryTextSettingsText));
        if (previous != state.Root)
        {
            undoId = redoId = null; CanUndo = CanRedo = false;
            SelectedChange = (ShowStaged ? StagedChanges : UnstagedChanges).FirstOrDefault();
            CommitFiles.Clear(); CommitDetails = "";
            if (Changes.Count == 0) ShowDiff("");
            await recent.RememberAsync(Root, lifetime.Token);
            Recent.Remove(Root); Recent.Insert(0, Root);
            RepositoryOpened?.Invoke(this, EventArgs.Empty);
        }
        if (wasHistoryDiff && SelectedCommit is not null)
        {
            await LoadCommitAsync(SelectedCommit);
            IsHistoryDiff = true;
            await LoadDiffAsync();
        }
        else
        {
            SelectedChange ??= (ShowStaged ? StagedChanges : UnstagedChanges).FirstOrDefault();
            IsHistoryDiff = false;
            OnPropertyChanged(nameof(SelectedChange));
            if (SelectedChange is null) ShowDiff(""); else await LoadDiffAsync();
            await LoadConflictAsync(SelectedChange);
        }
        Notice = Summary;
        OnPropertyChanged(nameof(Summary));
    }
    private LogFilter CurrentFilter() => new(Search, AuthorFilter, PathFilter, SinceFilter, UntilFilter, Take: historyLimit);
    [RelayCommand] private async Task SearchHistoryAsync() { if (!CanConfigure || disposed) return; historyLimit = 500; historyGeneration = null; await RefreshAsync(); }
    [RelayCommand] private async Task LoadMoreAsync()
    {
        if (IsBusy || disposed || state is null) return;
        IsBusy = true;
        try
        {
            var page = await workspace.GetHistoryPageAsync(Root, CurrentFilter() with { Skip = Commits.Count, Take = 1000 }, historyGeneration, lifetime.Token);
            if (page.Reset) Commits.ReplaceAll(page.Commits);
            else
            {
                var current = Commits.ToArray();
                var added = await Task.Run(() =>
                {
                    var existing = current.Select(c => c.Hash).ToHashSet(StringComparer.Ordinal);
                    return page.Commits.Where(c => existing.Add(c.Hash)).ToArray();
                }, lifetime.Token);
                Commits.AddRange(added);
            }
            historyGeneration = page.Generation;
            OnPropertyChanged(nameof(Summary)); Notice = Summary;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Notice = ex.Message; }
        finally { IsBusy = false; }
    }
    private static (IReadOnlyList<FileChange> Unstaged, IReadOnlyList<FileChange> Staged) GroupStatus(IReadOnlyList<FileChange> files)
        => (files.Where(f => f.WorktreeStatus is not "." and not " ").ToArray(), files.Where(f => f.IndexStatus is not "." and not " " and not "?").ToArray());
    [RelayCommand] private async Task OpenRecentAsync(string? path) { if (path is null || IsBusy) return; RepositoryPath = path; await RefreshAsync(); }
    [RelayCommand] private void Cancel() { if (CanCancel) operationRequest?.Cancel(); }
    [RelayCommand] private async Task LoadLargeDiffAsync()
    {
        var identity = CurrentDiffIdentity();
        if (identity is null) return;
        var request = StartSelectionRequest(); IsDiffLoading = true;
        try { await ShowDiffAsync(sourceDiff, identity, request.Token, force: true); }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(request, selectionRequest)) IsDiffLoading = false; }
    }
    [RelayCommand] private void UseConflictSide(string? side)
    {
        if (!CanSaveConflict) return;
        ConflictResult = side switch { "Ours" => ConflictOurs, "Theirs" => ConflictTheirs, "Both" => ConflictOurs + (ConflictOurs.Length > 0 && !ConflictOurs.EndsWith('\n') ? "\n" : "") + ConflictTheirs, _ => ConflictResult };
    }
    [RelayCommand] private void UseConflictSection(string? side)
    {
        if (!CanSaveConflict || SelectedConflictSection is null) return;
        ConflictResult = ConflictMarkers.Replace(ConflictResult, SelectedConflictSection,
            side switch { "Ours" => ConflictChoice.Ours, "Theirs" => ConflictChoice.Theirs, _ => ConflictChoice.Both });
    }
    [RelayCommand] private async Task UndoAsync() { if (undoId is not null) await RunAsync(new(GitAction.RestoreSnapshot) { SnapshotId = undoId }, isUndo: true); }
    [RelayCommand] private async Task RedoAsync() { if (redoId is not null) await RunAsync(new(GitAction.RestoreSnapshot) { SnapshotId = redoId }, isRedo: true); }

    [RelayCommand]
    private async Task ActionAsync(string? action)
    {
        if (action is null || IsBusy || TerminalActive || ExternalWriteActive || disposed) return;
        try
        {
            if (action == "Trust")
            {
                await workspace.TrustAsync(Root, lifetime.Token); await RefreshAsync(); return;
            }
            var operation = CreateOperation(action);
            if (operation is null) return;
            await RunAsync(operation);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Notice = ex.Message; }
    }

    public GitOperation? CreateOperation(string action)
    {
        IEnumerable<FileChange> FileSelection(bool all) => all ? Changes : SelectedFiles.Count > 0 ? SelectedFiles : SelectedChange is null ? [] : new[] { SelectedChange };
        string[] Paths(bool all, bool staged) => FileSelection(all)
            .Where(f => staged ? f.IndexStatus is not "." and not " " and not "?" : f.WorktreeStatus is not "." and not " ")
            .Select(f => f.Path).ToArray();
        var paths = Paths(false, false);
        switch (action)
        {
            case "Stage": case "StageAll": return WithPaths(GitAction.Stage, Paths(action == "StageAll", false));
            case "Unstage": case "UnstageAll": return WithPaths(GitAction.Unstage, Paths(action == "UnstageAll", true));
            case "Discard": return WithPaths(GitAction.Discard, paths);
            case "StagePatch": case "UnstagePatch":
                if (!CanApplyPatch) throw new InvalidOperationException(L["DiffLoading"]);
                if (IsHistoryDiff || IsLargeDiff) throw new InvalidOperationException(L["SelectWorktreeDiff"]);
                if ((action == "UnstagePatch") != ShowStaged) throw new InvalidOperationException(L["PatchModeRequired"]);
                var hunks = PatchRows.Where(r => r.Selected && r.LineId is null).Select(r => r.HunkId).ToHashSet();
                var lines = PatchRows.Where(r => r.Selected && r.LineId is not null).Select(r => r.LineId!.Value).ToHashSet();
                if (hunks.Count + lines.Count == 0) throw new InvalidOperationException(L["SelectPatch"]);
                return new(action == "StagePatch" ? GitAction.StagePatch : GitAction.UnstagePatch) { Patch = UnifiedPatch.Build(sourceDiff, hunks, lines, action == "UnstagePatch") };
            case "Commit":
                if (string.IsNullOrWhiteSpace(CommitMessage)) throw new InvalidOperationException(L["MessageRequired"]);
                return new(GitAction.Commit) { Message = CommitMessage, Amend = Amend, SkipHooks = SkipHooks, Sign = SignCommit };
            case "Init": return new(GitAction.Init);
            case "Clone": return new(GitAction.Clone) { Url = Require(CloneUrl, "UrlRequired") };
            case "CreateBranch": return new(GitAction.CreateBranch) { Name = Require(BranchName, "BranchRequired") };
            case "SwitchBranch": return new(GitAction.SwitchBranch) { Name = Require(BranchName, "BranchRequired") };
            case "RenameBranch": return new(GitAction.RenameBranch) { Name = Require(BranchName, "BranchRequired"), NewName = Require(NewBranchName, "NewNameRequired") };
            case "DeleteBranch": return new(GitAction.DeleteBranch) { Name = Require(BranchName, "BranchRequired"), Force = ForceDelete };
            case "Fetch": return new(GitAction.Fetch) { Remote = RemoteName };
            case "Pull": return new(GitAction.Pull) { Remote = RemoteName };
            case "Push": return new(GitAction.Push) { Remote = RemoteName, Force = ForcePush };
            case "Merge": return new(GitAction.Merge) { Name = Require(BranchName, "BranchRequired") };
            case "AbortMerge": return new(GitAction.AbortMerge);
            case "ResolveOurs": return WithPaths(GitAction.ResolveOurs, SelectedPaths());
            case "ResolveTheirs": return WithPaths(GitAction.ResolveTheirs, SelectedPaths());
            case "MarkResolved":
                if (ConflictDirty) throw new InvalidOperationException(L["SaveConflictFirst"]);
                return WithPaths(GitAction.MarkResolved, SelectedPaths());
            case "ExternalMergeTool": return WithPaths(GitAction.ExternalMergeTool, SelectedPaths());
            case "WriteConflictResult":
                if (!CanSaveConflict || SelectedChange is null) throw new InvalidOperationException(L["ConflictUnavailable"]);
                return new(GitAction.WriteConflictResult) { Paths = [SelectedChange.Path], Message = ConflictResult };
            case "RestoreSnapshot": return SelectedSnapshot is null ? throw new InvalidOperationException(L["SelectSnapshot"]) : new(GitAction.RestoreSnapshot) { SnapshotId = SelectedSnapshot.Id };
            default: throw new InvalidOperationException(action);
        }
        GitOperation WithPaths(GitAction kind, string[] selected) => selected.Length == 0 ? throw new InvalidOperationException(L["SelectFile"]) : new(kind) { Paths = selected };
        string[] SelectedPaths() => SelectedFiles.Count > 0 ? SelectedFiles.Select(f => f.Path).ToArray() : SelectedChange is null ? [] : [SelectedChange.Path];
    }
    private string Require(string value, string key) => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(L[key]) : value.Trim();

    private async Task RunAsync(GitOperation operation, bool isUndo = false, bool isRedo = false)
    {
        if (IsBusy || TerminalActive || ExternalWriteActive) return;
        IsBusy = true; CanCancel = false; HasProgressPercent = false; ProgressText = operation.Action.ToString();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operationRequest = request;
        try
        {
            var progress = new Progress<OperationProgress>(p => { if (disposed || !ReferenceEquals(operationRequest, request)) return; ProgressText = p.Phase; ProgressPercent = p.Percent ?? 0; HasProgressPercent = p.Percent.HasValue; CanCancel = p.CanCancel; });
            var executionRoot = operation.Action is GitAction.Init or GitAction.Clone ? Require(RepositoryPath, "PathRequired") : Root;
            var conflictRevisions = operation.Paths.ToDictionary(path => path,
                path => conflictDrafts.TryGetValue((executionRoot, path), out var draft) ? draft.Revision : 0L,
                StringComparer.Ordinal);
            var outcome = await workspace.ExecuteAsync(executionRoot, operation, progress, request.Token);
            var canOfferHookBypass = !outcome.Success && !outcome.Cancelled && outcome.Error == GitErrorKind.HookRejected && operation.Action == GitAction.Commit && !operation.SkipHooks;
            Feedback.Record(outcome,
                () => CanConfigure && (operation.Action is GitAction.Init or GitAction.Clone ? RepositoryPath == executionRoot : Root == executionRoot && HasRepository) ? RunAsync(operation with { SkipHooks = false, Force = false }) : Task.CompletedTask,
                canOfferHookBypass ? "RetryWithoutHooks" : null,
                canOfferHookBypass ? async () =>
                {
                    bool StillCurrent() => !disposed && CanOperate && Root == executionRoot && RepositoryPath == executionRoot && CommitMessage == operation.Message && Amend == operation.Amend && SignCommit == operation.Sign;
                    if (!StillCurrent() || ConfirmSkipHooks is not { } confirm || !await confirm() || !StillCurrent()) return;
                    SkipHooks = true; await RunAsync(operation with { SkipHooks = true, Force = false });
                } : null);
            if (outcome.Success && operation.Action is GitAction.WriteConflictResult or GitAction.ResolveOurs or GitAction.ResolveTheirs or GitAction.MarkResolved)
            {
                foreach (var path in operation.Paths)
                {
                    var key = (executionRoot, path);
                    var unchanged = !conflictDrafts.TryGetValue(key, out var draft) || draft.Revision == conflictRevisions[path];
                    if (!unchanged) continue;
                    conflictDrafts.Remove(key);
                    if (loadedConflict == key) ConflictDirty = false;
                }
            }
            var message = outcome.Message;
            if (outcome.Snapshot is { } snapshot)
            {
                if (isUndo) { redoId = snapshot.Id; undoId = null; }
                else if (isRedo) { undoId = snapshot.Id; redoId = null; }
                else { undoId = snapshot.Id; redoId = null; }
                CanUndo = undoId is not null; CanRedo = redoId is not null;
            }
            if (outcome.Success && operation.Action == GitAction.Commit && CommitMessage == operation.Message) { CommitMessage = ""; Amend = false; }
            if (!outcome.Cancelled)
            {
                try { await RefreshStateAsync(); }
                catch (Exception ex) { message += "\n" + ex.Message; }
            }
            Notice = (outcome.Success ? L["Completed"] : outcome.Cancelled ? L["Cancelled"] : outcome.Error.ToString()) + " · " + message;
        }
        catch (OperationCanceledException) { Notice = L["Cancelled"]; }
        catch (Exception ex) { Notice = ex.Message; }
        finally
        {
            if (operation.Action == GitAction.Commit) SkipHooks = false;
            if (operation.Action == GitAction.Push) ForcePush = false;
            CanCancel = false; IsBusy = false; ProgressText = ""; operationRequest = null;
        }
    }

    private async Task LoadCommitAsync(CommitInfo commit)
    {
        CancelRead(ref selectionRequest); sourceIdentity = null; ShowDiff(""); IsDiffLoading = false;
        CancelRead(ref commitRequest);
        var request = commitRequest = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var root = Root;
        try
        {
            var details = await workspace.GetCommitAsync(root, commit.Hash, request.Token);
            if (request.IsCancellationRequested || Root != root || SelectedCommit?.Hash != commit.Hash) return;
            CommitDetails = $"{commit.Hash}\n{commit.Author} · {commit.Date:yyyy-MM-dd HH:mm zzz}\n\n{details.Message}";
            selectedEncoding = details.MessageEncoding; OnPropertyChanged(nameof(SelectedEncodingText));
            Replace(CommitFiles, details.Files);
            SelectedCommitFile = CommitFiles.FirstOrDefault();
            if (CommitFiles.Count == 0) { IsHistoryDiff = true; ShowDiff(""); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!request.IsCancellationRequested) { Notice = ex.Message; CommitDetails = ex.Message; CommitFiles.Clear(); } }
    }
    private async Task LoadDiffAsync()
    {
        var identity = CurrentDiffIdentity();
        var retainedIdentity = sourceIdentity;
        var retainDocument = identity is not null && identity == retainedIdentity;
        OnPropertyChanged(nameof(DiffTitle));
        var request = StartSelectionRequest();
        sourceIdentity = null;
        if (!retainDocument) ShowDiff("");
        IsDiffLoading = false;
        BeforeSize = AfterSize = null; BeforeImage = AfterImage = null; PreviewNotice = "";
        if (identity is null || state is null) return;
        IsDiffLoading = true;
        _ = LoadPreviewAsync(identity, request.Token);
        try
        {
            var output = await workspace.GetDiffAsync(identity.Root, new(identity.Path, identity.Staged, identity.Commit), request.Token);
            if (!request.IsCancellationRequested && identity == CurrentDiffIdentity())
            {
                sourceIdentity = identity;
                if (!retainDocument || output != sourceDiff) await ShowDiffAsync(output, identity, request.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!request.IsCancellationRequested) { Notice = ex.Message; ShowDiff(ex.Message); } }
        finally { if (ReferenceEquals(selectionRequest, request)) { IsDiffLoading = false; OnPropertyChanged(nameof(CanApplyPatch)); } }
    }
    private async Task LoadPreviewAsync(DiffIdentity identity, CancellationToken ct)
    {
        try
        {
            var preview = await workspace.GetPreviewAsync(identity.Root, new(identity.Path, identity.Staged, identity.Commit), ct);
            if (ct.IsCancellationRequested || identity != CurrentDiffIdentity()) return;
            BeforeSize = preview.BeforeSize; AfterSize = preview.AfterSize; BeforeImage = preview.BeforeImage; AfterImage = preview.AfterImage; PreviewNotice = preview.Notice ?? "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested && identity == CurrentDiffIdentity()) PreviewNotice = ex.Message; }
    }
    private sealed record DiffIdentity(string Root, string Path, bool Staged, string? Commit);
    private DiffIdentity? CurrentDiffIdentity()
    {
        var path = IsHistoryDiff ? SelectedCommitFile?.Path : SelectedChange?.Path;
        if ((IsHistoryDiff ? SelectedCommitFile : SelectedChange)?.IsPathSupported == false) return null;
        return path is null || state is null ? null : new(Root, path, ShowStaged, IsHistoryDiff ? SelectedCommit?.Hash : null);
    }
    private async Task LoadConflictAsync(FileChange? change)
    {
        var conflicted = change is { IsPathSupported: true } && (change.IndexStatus == "U" || change.WorktreeStatus == "U" || change.IndexStatus + change.WorktreeStatus is "AA" or "DD");
        if (conflicted && ConflictDirty && loadedConflict == (Root, change!.Path)) return;
        CancelRead(ref conflictRequest);
        var request = conflictRequest = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        HasConflict = conflicted;
        assigningConflict = true;
        try { ConflictBase = ConflictOurs = ConflictTheirs = ConflictResult = ""; ConflictDirty = false; loadedConflict = null; }
        finally { assigningConflict = false; }
        ConflictBinary = false; ConflictLoading = false;
        if (!HasConflict || change is null || state is null) return;
        var root = Root; ConflictLoading = true;
        try
        {
            var content = await workspace.GetConflictAsync(root, change.Path, request.Token);
            if (request.IsCancellationRequested || Root != root || SelectedChange?.Path != change.Path || IsHistoryDiff) return;
            ConflictBase = content.Base; ConflictOurs = content.Ours; ConflictTheirs = content.Theirs;
            assigningConflict = true;
            try
            {
                var key = (root, change.Path);
                var hasDraft = !content.IsBinary && conflictDrafts.TryGetValue(key, out var draft) && draft.Base == content.Base && draft.Ours == content.Ours && draft.Theirs == content.Theirs;
                ConflictResult = hasDraft ? conflictDrafts[key].Result : content.Result;
                ConflictBinary = content.IsBinary; loadedConflict = key; ConflictDirty = hasDraft;
            }
            finally { assigningConflict = false; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!request.IsCancellationRequested) { Notice = ex.Message; HasConflict = false; } }
        finally { if (ReferenceEquals(conflictRequest, request)) ConflictLoading = false; }
    }
    private CancellationTokenSource StartSelectionRequest()
    {
        CancelRead(ref selectionRequest);
        return selectionRequest = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
    }
    private static void CancelRead(ref CancellationTokenSource? current)
    {
        var previous = current; current = null;
        if (previous is not null) _ = CancelAndDisposeAsync(previous);
    }
    private static async Task CancelAndDisposeAsync(CancellationTokenSource source)
    {
        try
        {
            // CancelAsync marks the token immediately, preserving stale-result guards, and dispatches slow process-kill callbacks off the UI.
            // Always finish disposal on a worker, including sources that had no callbacks.
            await source.CancelAsync().ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }
        catch (Exception ex) when (ex is AggregateException or ObjectDisposedException) { /* Read cancellation cleanup is best effort. */ }
        finally { source.Dispose(); }
    }
    private void ShowDiff(string output)
    {
        sourceDiff = output;
        PatchRows.Clear();
        IsLargeDiff = output.Length > 1024 * 1024 || output.Count(c => c == '\n') > 20000;
        if (IsLargeDiff) { Diff = LeftDiff = RightDiff = L["LargeDiff"]; return; }
        PresentDiff(output);
    }
    private void PresentDiff(string output)
    {
        ApplyDiff(output, ProjectDiff(output, IsHistoryDiff, force: true));
    }
    private void CommandExecuted(object? sender, CommandEntry entry)
    {
        void Append()
        {
            if (disposed) return;
            var updated = Commands + $"[{entry.Time:HH:mm:ss}] $ {entry.Command}\n{entry.Output}\n[exit {entry.ExitCode} · {entry.Duration.TotalMilliseconds:0} ms]\n\n";
            Commands = updated.Length > 500000 ? updated[^400000..] : updated;
        }
        if (Dispatcher.UIThread.CheckAccess()) Append(); else Dispatcher.UIThread.Post(Append);
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    { if (target is BatchObservableCollection<T> batch) batch.ReplaceAll(values); else { target.Clear(); foreach (var value in values) target.Add(value); } }
    private void ClearState()
    {
        applyingReadState = false; historyGeneration = null; unstaged = staged = []; selectedEncoding = CommitMessageEncoding.Utf8;
        state = null; HasRepository = false; InProgress = false; IsUntrusted = false; Branch = "—"; Tracking = "";
        SelectedChange = null; SelectedCommit = null; SelectedCommitFile = null; SelectedSnapshot = null;
        Changes.Clear(); Commits.Clear(); Refs.Clear(); Remotes.Clear(); CommitFiles.Clear(); Snapshots.Clear(); SelectedFiles.Clear();
        OnPropertyChanged(nameof(UnstagedChanges)); OnPropertyChanged(nameof(StagedChanges));
        CommitDetails = ""; ShowDiff(""); CanUndo = CanRedo = false; undoId = redoId = null;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; ConfirmSkipHooks = null; workspace.CommandExecuted -= CommandExecuted;
        Feedback.Dispose(); CancelRead(ref selectionRequest); CancelRead(ref commitRequest); CancelRead(ref conflictRequest);
        _ = CancelAndDisposeAsync(lifetime); operationRequest?.Cancel();
    }
}
