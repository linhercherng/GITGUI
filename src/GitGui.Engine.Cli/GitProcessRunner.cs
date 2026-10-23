using System.Diagnostics;
using System.Text;

namespace GitGui.Engine.Cli;

public sealed record GitResult(int ExitCode, byte[] Output, string Error);

public sealed class GitProcessRunner
{
    // M0: read-only commands only; network/authentication and writes are deliberately unavailable.
    public async Task<GitResult> ReadAsync(string repository, IReadOnlyList<string> args, CancellationToken ct)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Path.GetFullPath(repository), UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var arg in new[] { "--no-optional-locks", "--literal-pathspecs", "-c", "core.quotepath=false",
            "-c", "color.ui=never", "-c", "core.pager=cat", "-c", "core.fsmonitor=false" })
            start.ArgumentList.Add(arg);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["LC_ALL"] = "C";
        // Do not let an inherited tracing setting write repository data to a file.
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_TRACE", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(key);
        ct.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        process.Start();
        using var registration = ct.Register(() => Kill(process));
        async Task<byte[]> Drain(Stream stream)
        {
            try
            {
                using var output = new MemoryStream();
                var buffer = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    if (output.Length + count > 4 * 1024 * 1024)
                        throw new InvalidOperationException("Git output exceeds the M0 4 MiB limit. Narrow the selection.");
                    output.Write(buffer, 0, count);
                }
                return output.ToArray();
            }
            catch { Kill(process); throw; }
        }
        var stdout = Drain(process.StandardOutput.BaseStream);
        var stderr = Drain(process.StandardError.BaseStream);
        await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(ct));
        return new(process.ExitCode, await stdout, Encoding.UTF8.GetString(await stderr));
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
}
