using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitGui.Core;

namespace GitGui.UI;

public sealed partial class RepositoryToolsWindowViewModel : ObservableObject, IDisposable
{
    private readonly IRepositoryToolsService tools;
    private readonly IWorkspaceService workspace;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operationRequest;
    private bool disposed;
    public string Root { get; }
    public Localization L { get; }
    public OperationFeedback Feedback { get; }
    public event EventHandler<OperationOutcome>? WorkspaceChanged;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isWriting;
    [ObservableProperty] private bool isBlocked;
    [ObservableProperty] private bool canCancel;
    [ObservableProperty] private string notice = "";
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private string commands = "";
    [ObservableProperty] private string submodulePath = "";
    [ObservableProperty] private string submoduleUrl = "";
    [ObservableProperty] private bool recursive = true;
    [ObservableProperty] private SubmoduleInfo? selectedSubmodule;
    [ObservableProperty] private string worktreePath = "";
    [ObservableProperty] private string worktreeBranch = "";
    [ObservableProperty] private string worktreeRevision = "HEAD";
    [ObservableProperty] private string lockReason = "";
    [ObservableProperty] private WorktreeInfo? selectedWorktree;
    [ObservableProperty] private string lfsPatterns = "";
    [ObservableProperty] private string lfsRemote = "origin";
    [ObservableProperty] private string lfsVersion = "";
    [ObservableProperty] private string lfsStatus = "";
    [ObservableProperty] private string badRevision = "HEAD";
    [ObservableProperty] private string goodRevision = "";
    [ObservableProperty] private bool bisectActive;
    [ObservableProperty] private string bisectLog = "";
    [ObservableProperty] private bool sparseEnabled;
    [ObservableProperty] private bool sparseCone = true;
    [ObservableProperty] private string sparsePatterns = "";
    [ObservableProperty] private OperationOutcome? lastOutcome;
    public ObservableCollection<SubmoduleInfo> Submodules { get; } = [];
    public ObservableCollection<WorktreeInfo> Worktrees { get; } = [];
    public bool CanOperate => !IsBusy && !IsBlocked && !disposed;
    public bool CanUndo => CanOperate && LastOutcome?.Snapshot is not null;
    public bool CanStartBisect => CanOperate && !BisectActive;
    public bool CanMarkBisect => CanOperate && BisectActive;
    public bool CanDisableSparse => CanOperate && SparseEnabled;
    public string BisectSummary => L[BisectActive ? "ToolsBisectActive" : "ToolsBisectInactive"];
    public string SparseSummary => L[SparseEnabled ? "ToolsSparseActive" : "ToolsSparseInactive"];

    public RepositoryToolsWindowViewModel(IWorkspaceService workspace, IRepositoryToolsService tools, string root, Localization? localization = null)
    {
        this.workspace = workspace; this.tools = tools; Root = Path.GetFullPath(root); L = localization ?? new();
        Feedback = new(L, () => CanOperate);
        Notice = L["ToolsHint"]; workspace.CommandExecuted += CommandExecuted;
        L.PropertyChanged += LocaleChanged;
    }
    private void LocaleChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { OnPropertyChanged(nameof(BisectSummary)); OnPropertyChanged(nameof(SparseSummary)); }
    partial void OnIsBusyChanged(bool value) => NotifyAvailability();
    partial void OnIsBlockedChanged(bool value) => NotifyAvailability();
    partial void OnLastOutcomeChanged(OperationOutcome? value) => OnPropertyChanged(nameof(CanUndo));
    partial void OnBisectActiveChanged(bool value) { NotifyAvailability(); OnPropertyChanged(nameof(BisectSummary)); }
    partial void OnSparseEnabledChanged(bool value) { NotifyAvailability(); OnPropertyChanged(nameof(SparseSummary)); }
    partial void OnSelectedSubmoduleChanged(SubmoduleInfo? value) { if (value is not null) SubmodulePath = value.Path; }
    partial void OnSelectedWorktreeChanged(WorktreeInfo? value) { if (value is not null) WorktreePath = value.Path; }
    private void NotifyAvailability()
    {
        Feedback?.Notify();
        OnPropertyChanged(nameof(CanOperate)); OnPropertyChanged(nameof(CanUndo)); OnPropertyChanged(nameof(CanStartBisect));
        OnPropertyChanged(nameof(CanMarkBisect)); OnPropertyChanged(nameof(CanDisableSparse));
    }
    [RelayCommand] private async Task RefreshAsync()
    {
        if (IsBusy || disposed) return;
        IsBusy = true;
        try { await RefreshStateAsync(); if (!disposed) Notice = L["ToolsReady"]; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!disposed) Notice = ex.Message; }
        finally { IsBusy = false; }
    }
    private async Task RefreshStateAsync()
    {
        var state = await tools.GetStateAsync(Root, lifetime.Token);
        if (disposed || lifetime.IsCancellationRequested) return;
        var submodule = SelectedSubmodule?.Path; var worktree = SelectedWorktree?.Path;
        Submodules.Clear(); foreach (var item in state.Submodules) Submodules.Add(item);
        Worktrees.Clear(); foreach (var item in state.Worktrees) Worktrees.Add(item);
        SelectedSubmodule = Submodules.FirstOrDefault(item => item.Path == submodule);
        SelectedWorktree = Worktrees.FirstOrDefault(item => item.Path == worktree);
        LfsVersion = state.LfsVersion; LfsStatus = state.LfsStatus;
        BisectActive = state.BisectActive; BisectLog = state.BisectLog; SparseEnabled = state.SparseEnabled;
        // Preserve an unfinished form while reporting the actual sparse configuration separately.
        if (!sparseFormInitialized) { SparseCone = state.SparseCone; SparsePatterns = string.Join('\n', state.SparsePatterns); sparseFormInitialized = true; }
        SparseCurrent = string.Join('\n', state.SparsePatterns);
    }
    private bool sparseFormInitialized;
    [ObservableProperty] private string sparseCurrent = "";
    public RepositoryToolOperation CreateOperation(string action)
    {
        if (!CanOperate) throw new InvalidOperationException(L["ToolsWait"]);
        if (!Enum.TryParse<RepositoryToolAction>(action, out var kind) || !Enum.IsDefined(kind)) throw new ArgumentException(action);
        return kind switch
        {
            RepositoryToolAction.SubmoduleAdd => new(kind) { Path = Require(SubmodulePath, "ToolsSubmodulePath"), Url = Require(SubmoduleUrl, "UrlRequired") },
            RepositoryToolAction.SubmoduleInitialize or RepositoryToolAction.SubmoduleUpdate or RepositoryToolAction.SubmoduleSync or RepositoryToolAction.SubmoduleDeinitialize => new(kind) { Path = Require(SubmodulePath, "ToolsSubmodulePath"), Recursive = Recursive },
            RepositoryToolAction.WorktreeAdd => new(kind) { Path = Require(WorktreePath, "ToolsWorktreePath"), Name = WorktreeBranch.Trim(), Revision = Require(WorktreeRevision, "AdvancedRevisionRequired") },
            RepositoryToolAction.WorktreeRemove or RepositoryToolAction.WorktreeLock or RepositoryToolAction.WorktreeUnlock => new(kind) { Path = Require(WorktreePath, "ToolsWorktreePath"), Reason = LockReason.Trim() },
            RepositoryToolAction.LfsTrack or RepositoryToolAction.LfsUntrack => new(kind) { Patterns = Patterns(LfsPatterns) },
            RepositoryToolAction.LfsFetch or RepositoryToolAction.LfsPull => new(kind) { Name = Require(LfsRemote, "AdvancedRemoteRequired") },
            RepositoryToolAction.BisectStart => new(kind) { Revision = Require(BadRevision, "AdvancedRevisionRequired"), GoodRevision = Require(GoodRevision, "ToolsGoodRevision") },
            RepositoryToolAction.SparseSet => new(kind) { Patterns = Patterns(SparsePatterns), Cone = SparseCone },
            _ => new(kind)
        };
    }
    private string Require(string value, string key) => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(L[key]) : value.Trim();
    private IReadOnlyList<string> Patterns(string text)
    {
        var patterns = text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        return patterns.Length == 0 ? throw new InvalidOperationException(L["ToolsPatternsRequired"]) : patterns;
    }
    [RelayCommand] private async Task ExecuteAsync(string? action)
    {
        if (!CanOperate || action is null) return;
        try { var operation = CreateOperation(action); await RunAsync((progress, ct) => tools.ExecuteAsync(Root, operation, progress, ct)); }
        catch (Exception ex) { if (!disposed) Notice = ex.Message; }
    }
    [RelayCommand] private async Task UndoAsync()
    {
        if (!CanUndo || LastOutcome?.Snapshot is not { } snapshot) return;
        await RunAsync((progress, ct) => workspace.ExecuteAsync(Root, new(GitAction.RestoreSnapshot) { SnapshotId = snapshot.Id }, progress, ct));
    }
    [RelayCommand] private void Cancel() { if (CanCancel) operationRequest?.Cancel(); }
    private async Task RunAsync(Func<IProgress<OperationProgress>, CancellationToken, Task<OperationOutcome>> execute)
    {
        IsBusy = IsWriting = true; CanCancel = false;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); operationRequest = request;
        try
        {
            var progress = new Progress<OperationProgress>(p => { if (!disposed && operationRequest == request) { ProgressText = p.Phase; CanCancel = p.CanCancel; } });
            var result = await execute(progress, request.Token);
            Feedback.Record(result, () => CanOperate ? RunAsync(execute) : Task.CompletedTask);
            if (disposed) return;
            LastOutcome = result;
            try { await RefreshStateAsync(); Notice = result.Message; }
            catch (Exception ex) { Notice = result.Message + "\n" + ex.Message; }
            WorkspaceChanged?.Invoke(this, result);
        }
        catch (OperationCanceledException) { if (!disposed) Notice = L["Cancelled"]; }
        catch (Exception ex) { if (!disposed) Notice = ex.Message; }
        finally { operationRequest = null; CanCancel = false; IsWriting = IsBusy = false; ProgressText = ""; }
    }
    private void CommandExecuted(object? sender, CommandEntry entry)
    {
        void Append()
        {
            if (disposed) return;
            Commands += entry.Command + "\n" + entry.Output + "\n\n";
            if (Commands.Length > 160_000) Commands = Commands[^160_000..];
        }
        if (Dispatcher.UIThread.CheckAccess()) Append(); else Dispatcher.UIThread.Post(Append);
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; workspace.CommandExecuted -= CommandExecuted; L.PropertyChanged -= LocaleChanged;
        Feedback.Dispose(); lifetime.Cancel(); lifetime.Dispose();
    }
    public Task ExecuteSuggestedAsync(GitAction action) => CanOperate && action is GitAction.Fetch or GitAction.Pull
        ? RunAsync((progress, ct) => workspace.ExecuteAsync(Root, new(action), progress, ct)) : Task.CompletedTask;
}
