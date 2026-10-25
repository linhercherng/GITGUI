using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitGui.Core;
using GitGui.Engine.Cli;
using GitGui.UI;
using Xunit;

namespace GitGui.UI.Tests;

public class RepositoryInitializationWindowTests
{
    [AvaloniaFact]
    public async Task Existing_project_can_complete_first_commit_using_window_controls_and_real_git()
    {
        using var fixture = new ProjectFolder();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "README.md"), "# 現有專案\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, ".gitignore"), ".env\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, ".env"), "EXAMPLE_ONLY=keep-local\n");
        using var vm = new MainWindowViewModel(fixture.Service, new MemoryRecent()) { RepositoryPath = fixture.Root };
        var window = new MainWindow(vm) { Width = 1380, Height = 900 };
        window.Show();
        try
        {
            if (vm.InitializeCommand.ExecutionTask is { } startup) await startup;
            Click(window, Button(window, vm.L["RepositoryActions"]));
            var dialog = Assert.Single(window.OwnedWindows);
            var name = Find<TextBox>(dialog, "InitAuthorNameInput");
            var email = Find<TextBox>(dialog, "InitAuthorEmailInput");
            name.Text = "GUI 新手"; email.Text = "gui@example.invalid";
            Assert.Equal(fixture.Root, Find<TextBox>(dialog, "RepositoryDestinationInput").Text);
            Assert.Contains(".git", Find<TextBlock>(dialog, "InitializationHelp").Text);
            await ClickAction(dialog, Find<Button>(dialog, "InitializeRepositoryButton"), vm);
            Assert.True(vm.HasRepository, vm.Notice);
            Assert.Equal("main", vm.Branch);
            Assert.Empty(vm.Commits);
            Assert.Equal(2, vm.Changes.Count);
            Assert.DoesNotContain(vm.Changes, f => f.Path == ".env");
            Assert.Equal("GUI 新手", (await fixture.Git("config", "--local", "--get", "user.name")).Trim());
            // Isolate inherited signing/hooks so this test exercises the GUI commit itself.
            await fixture.Git("config", "commit.gpgsign", "false");
            await fixture.Git("config", "core.hooksPath", Path.Combine(fixture.Root, ".git", "hooks"));
            await ClickAction(window, Button(window, vm.L["StageAll"]), vm);
            Assert.Empty(vm.UnstagedChanges);
            Assert.Equal(2, vm.StagedChanges.Count());
            var message = Find<TextBox>(window, "CommitMessageInput");
            message.Text = "透過 GUI 首次備份";
            Assert.Equal("透過 GUI 首次備份", vm.CommitMessage);
            await ClickAction(window, Find<Button>(window, "CommitButton"), vm);
            Assert.True(!vm.StagedChanges.Any(), vm.Notice + " · " + vm.CommitMessage);
            Assert.Equal("透過 GUI 首次備份", Assert.Single(vm.Commits).Subject);
            Assert.Equal("GUI 新手 <gui@example.invalid>", (await fixture.Git("log", "-1", "--format=%an <%ae>")).Trim());
            Assert.Empty((await fixture.Git("remote")).Trim());
            Assert.Equal("# 現有專案\n", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "README.md")));
            Assert.Equal("EXAMPLE_ONLY=keep-local\n", await File.ReadAllTextAsync(Path.Combine(fixture.Root, ".env")));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Incomplete_identity_stays_in_dialog_and_cancel_creates_nothing()
    {
        using var fixture = new ProjectFolder();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "keep.txt"), "keep");
        using var vm = new MainWindowViewModel(fixture.Service, new MemoryRecent()) { RepositoryPath = fixture.Root };
        var window = new MainWindow(vm) { Width = 1380, Height = 900 };
        window.Show();
        try
        {
            if (vm.InitializeCommand.ExecutionTask is { } startup) await startup;
            Click(window, Button(window, vm.L["RepositoryActions"]));
            var dialog = Assert.Single(window.OwnedWindows);
            Find<TextBox>(dialog, "InitAuthorNameInput").Text = "Not saved";
            Click(dialog, Find<Button>(dialog, "InitializeRepositoryButton"));
            Assert.Same(dialog, Assert.Single(window.OwnedWindows));
            Assert.True(Find<TextBlock>(dialog, "InitializationError").IsVisible);
            Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".git")));
            Click(dialog, Button(dialog, vm.L["Cancel"]));
            await Task.Yield(); Dispatcher.UIThread.RunJobs();
            Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".git")));
            Assert.Empty(vm.InitAuthorName);
            Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "keep.txt")));
        }
        finally { window.Close(); }
    }

    private static async Task ClickAction(Window owner, Button button, MainWindowViewModel vm)
    {
        var previous = vm.ActionCommand.ExecutionTask;
        Click(owner, button);
        await Task.Yield(); Dispatcher.UIThread.RunJobs();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (ReferenceEquals(previous, vm.ActionCommand.ExecutionTask))
        {
            await Task.Delay(10, timeout.Token);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.NotNull(vm.ActionCommand.ExecutionTask);
        await vm.ActionCommand.ExecutionTask.WaitAsync(TimeSpan.FromSeconds(30));
        while (vm.IsBusy)
        {
            await Task.Delay(10, timeout.Token);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.False(vm.IsBusy);
    }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static Button Button(Window window, string label) => window.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == label);
    private static void Click(Window owner, Button button)
    {
        owner.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Assert.True(button.IsEffectivelyVisible && button.IsEffectivelyEnabled);
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), owner)!.Value;
        owner.MouseDown(point, MouseButton.Left); owner.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class MemoryRecent : IRecentRepositories
    {
        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task RememberAsync(string root, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class ProjectFolder : IDisposable, IRepositorySettings, ISnapshotCatalog, IWorkspaceInteraction
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gitgui-gui-init-" + Guid.NewGuid().ToString("N"), "中文 專案");
        private readonly GitProcessRunner runner = new();
        private readonly List<SnapshotInfo> snapshots = [];
        public WorkspaceService Service { get; }
        public ProjectFolder()
        {
            Directory.CreateDirectory(Root);
            Service = new(runner, this, this, new SnapshotManager(runner, this), this);
        }
        public async Task<string> Git(params string[] args)
        {
            var result = await runner.RunAsync(Root, args, default, new() { IsReadOnly = false, Trusted = true });
            Assert.Equal(0, result.ExitCode);
            return Encoding.UTF8.GetString(result.Output);
        }
        public Task<bool> IsTrustedAsync(string root, CancellationToken ct) => Task.FromResult(true);
        public Task TrustAsync(string root, CancellationToken ct) => Task.CompletedTask;
        public Task SaveAsync(SnapshotInfo snapshot, CancellationToken ct) { snapshots.Add(snapshot); return Task.CompletedTask; }
        public Task<IReadOnlyList<SnapshotInfo>> ListAsync(string root, CancellationToken ct) => Task.FromResult<IReadOnlyList<SnapshotInfo>>(snapshots.Where(s => s.Repository == root).ToArray());
        public Task RemoveAsync(string id, CancellationToken ct) { snapshots.RemoveAll(s => s.Id == id); return Task.CompletedTask; }
        public Task<ConfirmationAnswer> ConfirmAsync(Confirmation confirmation, CancellationToken ct) => Task.FromResult(new ConfirmationAnswer(true, confirmation.RequiredText));
        public Task<string?> CredentialAsync(string prompt, CancellationToken ct) => Task.FromResult<string?>(null);
        public void Dispose()
        {
            var directory = Directory.GetParent(Root)!.FullName;
            if (!directory.StartsWith(Path.Combine(Path.GetTempPath(), "gitgui-gui-init-"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected test path");
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(directory, true);
        }
    }
}
