using Serilog;

namespace GitGui.Infrastructure;

public static class AppLogging
{
    // M0 logs only lifecycle events. Repository contents, paths and command output are never persisted.
    public static ILogger Create(string directory)
    {
        Directory.CreateDirectory(directory);
        return new LoggerConfiguration().WriteTo.File(Path.Combine(directory, "gitgui-.log"),
            rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14).CreateLogger();
    }
}
