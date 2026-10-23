using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Data;
using GitGui.Controls;
using GitGui.Core;
using GitGui.Engine.Cli;
using GitGui.Engine.Lib;
using GitGui.Infrastructure;
using GitGui.UI;
using Microsoft.Extensions.DependencyInjection;

namespace GitGui.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.FirstOrDefault() == "--askpass")
        {
            var pipe = Environment.GetEnvironmentVariable("GITGUI_ASKPASS_PIPE");
            if (pipe is null) { Environment.ExitCode = 1; return; }
            try
            {
                var answer = AskPassBridge.RequestAsync(pipe, args.ElementAtOrDefault(1) ?? "Git credential", CancellationToken.None).GetAwaiter().GetResult();
                if (answer is null) Environment.ExitCode = 1;
                else
                {
                    // WinExe has no console code page; write UTF-8 directly to Git's redirected handle.
                    using var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false));
                    output.WriteLine(answer);
                }
            }
            catch (Exception) { Environment.ExitCode = 1; }
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<DesktopApp>().UsePlatformDetect();
}

public sealed class DesktopApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitGui");
            var log = AppLogging.Create(directory);
            log.Information("GITGUI M1 started; repository content and credential logging are disabled");
            var services = new ServiceCollection();
            services.AddSingleton<GitProcessRunner>();
            services.AddSingleton<CliRepoReader>();
            services.AddSingleton<LibHistoryReader>();
            services.AddSingleton<IHistoryReader>(sp => sp.GetRequiredService<LibHistoryReader>());
            services.AddSingleton<IGraphHistoryReader>(sp => sp.GetRequiredService<LibHistoryReader>());
            services.AddSingleton<IRepoReader>(sp => new RoutedRepoReader(
                sp.GetRequiredService<CliRepoReader>(), sp.GetRequiredService<IHistoryReader>()));
            services.AddSingleton<IRecentRepositories>(new RecentRepositories(Path.Combine(directory, "gitgui.db")));
            services.AddSingleton(new WorkspaceStorage(Path.Combine(directory, "gitgui.db")));
            services.AddSingleton<IRepositorySettings>(sp => sp.GetRequiredService<WorkspaceStorage>());
            services.AddSingleton<ISnapshotCatalog>(sp => sp.GetRequiredService<WorkspaceStorage>());
            services.AddSingleton<IWorkspaceInteraction>(new DeferredInteraction(() => desktop.MainWindow as IWorkspaceInteraction));
            services.AddSingleton<SnapshotManager>();
            services.AddSingleton<IWorkspaceService, WorkspaceService>();
            services.AddTransient<MainWindowViewModel>();
            var provider = services.BuildServiceProvider();
            var graph = new CommitGraphControl();
            graph.Bind(CommitGraphControl.CommitsProperty, new Binding("Commits"));
            graph.Bind(CommitGraphControl.SelectedCommitProperty, new Binding("SelectedCommit") { Mode = BindingMode.TwoWay });
            desktop.MainWindow = new MainWindow(provider.GetRequiredService<MainWindowViewModel>(), graph);
            var bridge = new AskPassBridge(provider.GetRequiredService<IWorkspaceInteraction>());
            var runner = provider.GetRequiredService<GitProcessRunner>();
            var executable = Environment.ProcessPath!;
            static string Quote(string value) => OperatingSystem.IsWindows()
                ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
                : "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
            runner.AskPassCommand = Quote(executable) +
                (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                    ? " " + Quote(typeof(DesktopApp).Assembly.Location) : "") + " --askpass";
            runner.AskPassPipe = bridge.PipeName;
            desktop.Exit += (_, _) =>
            {
                provider.Dispose();
                bridge.Dispose();
                log.Information("GITGUI stopped");
                (log as IDisposable)?.Dispose();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class DeferredInteraction(Func<IWorkspaceInteraction?> current) : IWorkspaceInteraction
{
    public Task<ConfirmationAnswer> ConfirmAsync(Confirmation confirmation, CancellationToken ct)
        => (current() ?? throw new InvalidOperationException("Application window is not ready.")).ConfirmAsync(confirmation, ct);
    public Task<string?> CredentialAsync(string prompt, CancellationToken ct)
        => (current() ?? throw new InvalidOperationException("Application window is not ready.")).CredentialAsync(prompt, ct);
}
