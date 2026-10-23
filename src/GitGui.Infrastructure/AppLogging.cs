using Serilog;
using System.Text.RegularExpressions;
using GitGui.Core;

namespace GitGui.Infrastructure;

public static class AppLogging
{
    public static void Command(ILogger logger, CommandEntry entry)
    {
        try
        {
            // Never persist stdout/diff/blob/AskPass or arbitrary hook stderr. The
            // stderr summary is its classified kind, with raw details kept in UI.
            var directory = GitLogSanitizer.Text(entry.WorkingDirectory ?? "[unavailable]")
                .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
            logger.Information("Git {Arguments} cwd={Directory} elapsedMs={ElapsedMs} exit={ExitCode} stderrKind={ErrorKind}",
                GitLogSanitizer.Arguments(entry.Arguments), directory, entry.Duration.TotalMilliseconds,
                entry.ExitCode, GitFailurePolicy.Classify(entry.ExitCode, entry.StandardError));
        }
        catch (Exception) { /* Diagnostics must not alter a Git command's result. */ }
    }

    public static ILogger Create(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var file in Directory.EnumerateFiles(directory, "gitgui-*.log", SearchOption.TopDirectoryOnly))
            {
                if (!Regex.IsMatch(Path.GetFileName(file), @"^gitgui-\d{8}(?:_\d+)?\.log$")) continue;
                try
                {
                    if (File.GetLastWriteTimeUtc(file) >= DateTime.UtcNow.AddDays(-14)) continue;
                    File.Delete(file);
                }
                catch (IOException) { /* An open diagnostic is retained until a later startup. */ }
                catch (UnauthorizedAccessException) { }
            }
            return new LoggerConfiguration().WriteTo.File(Path.Combine(directory, "gitgui-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: null,
                retainedFileTimeLimit: TimeSpan.FromDays(14)).CreateLogger();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return new LoggerConfiguration().CreateLogger(); }
    }
}
