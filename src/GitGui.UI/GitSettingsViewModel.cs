using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitGui.Core;

namespace GitGui.UI;

public sealed partial class GitSettingsViewModel(IGitRuntimeSettings settings) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    [ObservableProperty] private string pathText = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool isBusy;
    public bool CanEdit => !IsBusy;
    public event EventHandler<GitRuntimeInfo>? Saved;
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanEdit));

    public async Task InitializeAsync()
    {
        IsBusy = true;
        try { PathText = await settings.GetPathAsync(lifetime.Token) ?? ""; }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
        await CheckAsync();
    }

    [RelayCommand]
    private async Task CheckAsync() => await Apply(false);
    [RelayCommand]
    private async Task SaveAsync() => await Apply(true);

    private async Task Apply(bool save)
    {
        if (IsBusy || disposed) return;
        IsBusy = true;
        try
        {
            var path = string.IsNullOrWhiteSpace(PathText) ? null : PathText.Trim();
            var result = save ? await settings.SaveAsync(path, lifetime.Token) : await settings.CheckAsync(path, lifetime.Token);
            if (disposed) return;
            Status = result.Version + "\n" + result.Executable;
            if (save) Saved?.Invoke(this, result);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!disposed) Status = ex.Message; }
        finally { IsBusy = false; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel(); lifetime.Dispose();
    }
}
