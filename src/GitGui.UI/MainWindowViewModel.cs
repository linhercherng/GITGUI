using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitGui.Core;

namespace GitGui.UI;

public partial class MainWindowViewModel(IRepoReader reader, IRecentRepositories recent) : ObservableObject, IDisposable
{
    [ObservableProperty] private string repositoryPath = "";
    [ObservableProperty] private string branch = "—";
    [ObservableProperty] private string notice = "開啟倉庫以查看變更與歷史。M0 目前僅提供讀取功能。";
    [ObservableProperty] private string diff = "選取變更檔案查看 Diff。";
    [ObservableProperty] private string commands = "唯讀模式 · 不會暫存、提交或變更 refs。";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool showStaged;
    [ObservableProperty] private FileChange? selectedChange;
    [ObservableProperty] private CommitInfo? selectedCommit;
    public ObservableCollection<FileChange> Changes { get; } = [];
    public ObservableCollection<CommitInfo> Commits { get; } = [];
    public ObservableCollection<string> Recent { get; } = [];
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? diffRequest;
    private string? openedRepository;
    private bool disposed;

    [RelayCommand]
    private async Task InitializeAsync()
    {
        try { foreach (var path in await recent.ListAsync(lifetime.Token)) Recent.Add(path); }
        catch (Exception ex) { Notice = ex.Message; }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        diffRequest?.Cancel();
        try
        {
            var path = Path.GetFullPath(RepositoryPath.Trim());
            var status = await reader.GetStatusAsync(path, lifetime.Token);
            var history = await reader.GetLogAsync(path, 500, lifetime.Token);
            openedRepository = path;
            RepositoryPath = path;
            Branch = status.Branch;
            Changes.Clear();
            foreach (var file in status.Files) Changes.Add(file);
            Commits.Clear();
            foreach (var commit in history) Commits.Add(commit);
            Diff = "選取變更檔案查看 Diff。";
            Commands = "git --no-optional-locks -c core.fsmonitor=false status --porcelain=v2 -z --branch\n" +
                "歷史：LibGit2Sharp（SHA-1/files）；其他格式或失敗時使用 git log --topo-order -n 500。";
            Notice = $"{Changes.Count} 個變更檔案 · 最近 {Commits.Count} 筆提交 · M0 唯讀";
            await recent.RememberAsync(path, lifetime.Token);
            Recent.Remove(path);
            Recent.Insert(0, path);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            openedRepository = null;
            Changes.Clear(); Commits.Clear(); Branch = "—";
            Diff = "無法讀取倉庫。請確認路徑與 Git 錯誤訊息後重試。";
            Commands = "本次倉庫讀取失敗。";
            Notice = ex.Message;
        }
        finally { IsBusy = false; }
    }

    partial void OnSelectedChangeChanged(FileChange? value) => _ = LoadDiffAsync();
    partial void OnShowStagedChanged(bool value) => _ = LoadDiffAsync();
    partial void OnSelectedCommitChanged(CommitInfo? value)
    {
        if (value is null) return;
        diffRequest?.Cancel();
        Diff = $"{value.Hash}\n{value.Author} · {value.Date:yyyy-MM-dd HH:mm zzz}\n\n{value.Subject}\n\n" +
            "M0 顯示提交中繼資料；提交檔案清單與圖形軌道將於 M1 加入。";
    }

    private async Task LoadDiffAsync()
    {
        diffRequest?.Cancel();
        diffRequest?.Dispose();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        diffRequest = request;
        var file = SelectedChange;
        var path = openedRepository;
        if (file is null || path is null) { diffRequest = null; return; }
        try
        {
            var output = await reader.GetDiffAsync(path, file.Path, ShowStaged, request.Token);
            if (!request.IsCancellationRequested) Diff = output;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!request.IsCancellationRequested) Diff = ex.Message; }
        finally { if (ReferenceEquals(diffRequest, request)) diffRequest = null; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        diffRequest?.Cancel();
        lifetime.Dispose();
    }
}
