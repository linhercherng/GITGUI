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
    private static IDisposable? renderClock;
    internal static DesktopPerformanceProbe? Performance { get; private set; }
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
        try { Performance = DesktopPerformanceProbe.Parse(args); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { Environment.ExitCode = 2; return; }
        Performance?.StartupCheckpoint("Main parsed probe arguments");
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { renderClock?.Dispose(); }
    }
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<DesktopApp>().UsePlatformDetect();
        if (OperatingSystem.IsWindows())
        {
            var initialize = builder.WindowingSubsystemInitializer!;
            builder.UseWindowingSubsystem(() => { renderClock = WindowsRenderScheduling.Install(); initialize(); }, builder.WindowingSubsystemName!);
            // Native measurements on the supported Windows host found ANGLE surface submission
            // stalls. CPU rendering avoids those stalls and uses less resident memory here.
            builder.With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });
        }
        // An explicit probe-only experiment; normal desktop sessions retain platform defaults.
        if (Performance?.SoftwareRendering == true)
            builder.With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });
        else if (Performance?.DirectComposition == true)
            builder.With(new Win32PlatformOptions { CompositionMode = [Win32CompositionMode.DirectComposition, Win32CompositionMode.RedirectionSurface] });
        return builder;
    }
}

public sealed class DesktopApp : Application
{
    private AppearanceManager? appearance;
    public override void Initialize()
    {
        Program.Performance?.StartupCheckpoint("DesktopApp.Initialize enter");
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        appearance = new AppearanceManager(new AppearancePreferences(Path.Combine(
            Program.Performance?.DataPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitGui"), "appearance.json")));
        Program.Performance?.StartupCheckpoint("DesktopApp.Initialize exit");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Program.Performance?.StartupCheckpoint("OnFrameworkInitializationCompleted enter");
            var directory = Program.Performance?.DataPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitGui");
            var log = AppLogging.Create(directory);
            log.Information("GITGUI M4 started; repository content and credential logging are disabled");
            var services = new ServiceCollection();
            services.AddSingleton<GitProcessRunner>();
            services.AddSingleton<IGitPathStore>(sp => sp.GetRequiredService<WorkspaceStorage>());
            services.AddSingleton<GitRuntime>();
            services.AddSingleton<IGitRuntimeSettings>(sp => sp.GetRequiredService<GitRuntime>());
            services.AddSingleton<CliRepoReader>();
            services.AddSingleton<LibHistoryReader>();
            services.AddSingleton<IHistoryReader>(sp => sp.GetRequiredService<LibHistoryReader>());
            services.AddSingleton<IGraphHistoryReader>(sp => sp.GetRequiredService<LibHistoryReader>());
            services.AddSingleton<IFileHistoryReader>(sp => sp.GetRequiredService<LibHistoryReader>());
            services.AddSingleton<IRepoReader>(sp => new RoutedRepoReader(
                sp.GetRequiredService<CliRepoReader>(), sp.GetRequiredService<IHistoryReader>()));
            services.AddSingleton<IRecentRepositories>(new RecentRepositories(Path.Combine(directory, "gitgui.db")));
            services.AddSingleton(new WorkspaceStorage(Path.Combine(directory, "gitgui.db")));
            services.AddSingleton<IRepositorySettings>(sp => sp.GetRequiredService<WorkspaceStorage>());
            services.AddSingleton<ISnapshotCatalog>(sp => sp.GetRequiredService<WorkspaceStorage>());
            services.AddSingleton<IWorkspaceInteraction>(new DeferredInteraction(() => desktop.MainWindow as IWorkspaceInteraction));
            services.AddSingleton<SnapshotManager>();
            services.AddSingleton<WorkspaceService>();
            services.AddSingleton<IWorkspaceService>(sp => sp.GetRequiredService<WorkspaceService>());
            services.AddSingleton<IRepositoryToolsService, RepositoryToolsService>();
            services.AddSingleton(new ShortcutManager(new UiPreferencesStore(Path.Combine(directory, "ui.json"))));
            services.AddSingleton<ITerminalService>(sp => new TerminalService(sp.GetRequiredService<GitRuntime>(),
                sp.GetRequiredService<IRepositorySettings>(), sp.GetRequiredService<WorkspaceService>().TrustAsync,
                sp.GetRequiredService<WorkspaceService>().AcquireTerminalLeaseAsync, sp.GetRequiredService<GitProcessRunner>()));
            services.AddTransient<MainWindowViewModel>();
            var provider = services.BuildServiceProvider();
            Program.Performance?.StartupCheckpoint("Dependency provider built");
            var graph = new CommitGraphControl();
            graph.Bind(CommitGraphControl.CommitsProperty, new Binding("Commits"));
            graph.Bind(CommitGraphControl.SelectedCommitProperty, new Binding("SelectedCommit") { Mode = BindingMode.TwoWay });
            graph.Bind(CommitGraphControl.AccessibleNameProperty, new Binding("L[GraphName]"));
            graph.Bind(CommitGraphControl.EmptyTextProperty, new Binding("L[GraphEmpty]"));
            var mainModel = provider.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow(mainModel, graph,
                provider.GetRequiredService<IGitRuntimeSettings>(), appearance, provider.GetRequiredService<IWorkspaceService>(), provider.GetRequiredService<ITerminalService>(), provider.GetRequiredService<IRepositoryToolsService>(), provider.GetRequiredService<ShortcutManager>());
            Program.Performance?.StartupCheckpoint("MainWindow constructed");
            Program.Performance?.Attach(desktop, (MainWindow)desktop.MainWindow, mainModel, graph,
                provider.GetRequiredService<GitProcessRunner>(), provider.GetRequiredService<WorkspaceStorage>(), provider.GetRequiredService<IGitRuntimeSettings>());
            Program.Performance?.StartupCheckpoint("Probe attached");
            var bridge = new AskPassBridge(provider.GetRequiredService<IWorkspaceInteraction>());
            var runner = provider.GetRequiredService<GitProcessRunner>();
            runner.CommandExecuted += (_, entry) => AppLogging.Command(log, entry);
            var executable = Environment.ProcessPath!;
            static string Quote(string value) => OperatingSystem.IsWindows()
                ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
                : "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
            runner.AskPassCommand = Quote(executable) + (WindowsNativeHost.ActivePath() is { } nativeHost ? " " + Quote(nativeHost) + ",RunW" : "") +
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
