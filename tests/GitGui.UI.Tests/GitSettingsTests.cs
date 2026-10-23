using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitGui.Core;
using GitGui.UI;
using Xunit;

namespace GitGui.UI.Tests;

public class GitSettingsTests
{
    [AvaloniaFact]
    public async Task Git_runtime_validation_and_persistence_are_dispatched_off_the_UI_thread()
    {
        var settings = new FakeSettings(); using var vm = new GitSettingsViewModel(settings);
        await vm.InitializeAsync(); Assert.False(settings.ReadOnUi); Assert.False(settings.CheckOnUi);
        await vm.SaveCommand.ExecuteAsync(null); Assert.False(settings.SaveOnUi);
        Assert.Contains("2.56", vm.Status);
    }
    [AvaloniaFact]
    public async Task Dialog_shows_stored_path_and_does_not_close_or_save_when_validation_fails()
    {
        var settings = new FakeSettings { Path = "C:/Portable Git/cmd/git.exe" };
        var window = new GitSettingsWindow(settings, new Localization()); window.Show();
        await window.InitializationTask;
        var vm = Assert.IsType<GitSettingsViewModel>(window.DataContext);
        Assert.Equal(settings.Path, vm.PathText); Assert.Contains("2.56", vm.Status);
        Assert.Equal(settings.Path, window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "GitPathInput").Text);
        if (Environment.GetEnvironmentVariable("GITGUI_GIT_SETTINGS_SCREENSHOT") is { } screenshot)
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(screenshot)!); frame.Save(screenshot);
        }
        settings.Fail = true; vm.PathText = "missing/git.exe";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("Git unavailable", vm.Status); Assert.True(window.IsVisible);
        Assert.Equal("C:/Portable Git/cmd/git.exe", settings.Path);
        settings.Fail = false; vm.PathText = "C:/Portable 中文/cmd/git.exe";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(vm.PathText, settings.Path); Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task Check_never_persists_and_pending_save_disables_inputs()
    {
        var settings = new FakeSettings(); using var vm = new GitSettingsViewModel(settings);
        await vm.InitializeAsync(); vm.PathText = "C:/other/git.exe";
        await vm.CheckCommand.ExecuteAsync(null); Assert.Null(settings.Path);
        settings.Pending = new();
        var save = vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy); Assert.False(vm.CanEdit);
        settings.Pending.SetResult(new(vm.PathText, "git version 2.56.0")); await save;
        Assert.True(vm.CanEdit);
        settings.Pending = null; vm.PathText = ""; await vm.SaveCommand.ExecuteAsync(null);
        Assert.Null(settings.Path);
    }

    private sealed class FakeSettings : IGitRuntimeSettings
    {
        public string? Path { get; set; }
        public bool Fail { get; set; }
        public TaskCompletionSource<GitRuntimeInfo>? Pending { get; set; }
        public bool ReadOnUi { get; private set; }
        public bool CheckOnUi { get; private set; }
        public bool SaveOnUi { get; private set; }
        public Task<string?> GetPathAsync(CancellationToken ct) { ReadOnUi = Dispatcher.UIThread.CheckAccess(); return Task.FromResult(Path); }
        public Task<GitRuntimeInfo> CheckAsync(string? path, CancellationToken ct)
        { CheckOnUi = Dispatcher.UIThread.CheckAccess(); return Fail ? Task.FromException<GitRuntimeInfo>(new InvalidOperationException("Git unavailable")) : Task.FromResult(new GitRuntimeInfo(path ?? "automatic/git", "git version 2.56.0")); }
        public async Task<GitRuntimeInfo> SaveAsync(string? path, CancellationToken ct)
        {
            SaveOnUi = Dispatcher.UIThread.CheckAccess();
            var info = Pending is null ? await CheckAsync(path, ct) : await Pending.Task;
            Path = path; return info;
        }
    }
}
