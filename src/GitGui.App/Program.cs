using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
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
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
            log.Information("GITGUI M0 started; repository data logging is disabled");
            var services = new ServiceCollection();
            services.AddSingleton<GitProcessRunner>();
            services.AddSingleton<CliRepoReader>();
            services.AddSingleton<IHistoryReader, LibHistoryReader>();
            services.AddSingleton<IRepoReader>(sp => new RoutedRepoReader(
                sp.GetRequiredService<CliRepoReader>(), sp.GetRequiredService<IHistoryReader>()));
            services.AddSingleton<IRecentRepositories>(new RecentRepositories(Path.Combine(directory, "gitgui.db")));
            services.AddTransient<MainWindowViewModel>();
            var provider = services.BuildServiceProvider();
            desktop.MainWindow = new MainWindow(provider.GetRequiredService<MainWindowViewModel>());
            desktop.Exit += (_, _) =>
            {
                provider.Dispose();
                log.Information("GITGUI stopped");
                (log as IDisposable)?.Dispose();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
