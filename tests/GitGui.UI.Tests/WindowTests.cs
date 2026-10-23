using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Styling;
using GitGui.Core;
using GitGui.UI;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(GitGui.UI.Tests.TestApplication))]

namespace GitGui.UI.Tests;

public class TestApplication : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }
}

public class WindowTests
{
    [AvaloniaFact]
    public async Task Open_populates_real_bound_lists_and_exposes_readonly_diff()
    {
        using var vm = new MainWindowViewModel(new SampleReader(), new MemoryRecent());
        vm.RepositoryPath = Path.GetTempPath();
        var window = new MainWindow(vm);
        window.Show();
        await vm.RefreshCommand.ExecuteAsync(null);
        window.UpdateLayout();
        Assert.Single(vm.Changes);
        Assert.Single(vm.Commits);
        Assert.Equal("main", vm.Branch);
        Assert.Equal(1, window.FindControl<ListBox>("ChangesList")!.ItemCount);
        Assert.True(window.FindControl<TextBox>("DiffViewer")!.IsReadOnly);
        Assert.Contains("M0", window.Title);
        vm.SelectedChange = vm.Changes[0];
        Assert.Equal("+modified", vm.Diff);
        var screenshot = Environment.GetEnvironmentVariable("GITGUI_SCREENSHOT");
        if (screenshot is not null)
        {
            vm.RepositoryPath = "demo-repository (Headless fixture)";
            vm.Recent.Clear();
            vm.Recent.Add("demo-repository");
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
            frame.Save(screenshot);
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task Failure_clears_stale_repository_state_and_is_shown_to_user()
    {
        using var vm = new MainWindowViewModel(new SampleReader(), new MemoryRecent());
        vm.RepositoryPath = Path.GetTempPath();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedChange = vm.Changes[0];
        vm.RepositoryPath = "";
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Empty(vm.Changes);
        Assert.Empty(vm.Commits);
        Assert.False(vm.IsBusy);
        Assert.NotEmpty(vm.Notice);
        Assert.DoesNotContain("+modified", vm.Diff);
    }

    private sealed class SampleReader : IRepoReader
    {
        public Task<RepoStatus> GetStatusAsync(string repository, CancellationToken ct)
            => Task.FromResult(new RepoStatus("main", "abc", [new("中文.txt", ".", "M", [])]));
        public Task<IReadOnlyList<CommitInfo>> GetLogAsync(string repository, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CommitInfo>>([new("1234567890", "Initial", "Tester", DateTimeOffset.UnixEpoch, [])]);
        public Task<string> GetDiffAsync(string repository, string path, bool staged, CancellationToken ct)
            => Task.FromResult("+modified");
        public Task<RepoFormat> GetFormatAsync(string repository, CancellationToken ct)
            => Task.FromResult(new RepoFormat("sha1", "files"));
    }

    private sealed class MemoryRecent : IRecentRepositories
    {
        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task RememberAsync(string path, CancellationToken ct) => Task.CompletedTask;
    }
}
