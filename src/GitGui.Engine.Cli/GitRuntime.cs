using System.Diagnostics;
using System.Text;
using GitGui.Core;

namespace GitGui.Engine.Cli;

/// <summary>One selected Git distribution for every child process, including ref transactions.</summary>
public sealed class GitRuntime(IGitPathStore? store = null, string? baseDirectory = null, string? searchPath = null) : IGitRuntimeSettings
{
    private readonly string applicationDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
    private readonly string environmentPath = searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "";
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool loaded;
    private string? configuredPath;
    private GitRuntimeInfo? installation;

    public async Task<string?> GetPathAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { await Load(ct); return configuredPath; }
        finally { gate.Release(); }
    }

    public Task<GitRuntimeInfo> CheckAsync(string? path, CancellationToken ct) => Probe(ResolveExecutable(path), ct);

    public async Task<GitRuntimeInfo> SaveAsync(string? path, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var validated = await CheckAsync(path, ct);
            var value = string.IsNullOrWhiteSpace(path) ? null : validated.Executable;
            if (store is not null) await store.SaveGitPathAsync(value, ct);
            configuredPath = value; loaded = true; installation = validated;
            return validated;
        }
        finally { gate.Release(); }
    }

    internal async Task<ProcessStartInfo> CreateStartInfoAsync(string directory, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await Load(ct);
            installation ??= await CheckAsync(configuredPath, ct);
            if (!File.Exists(installation.Executable)) throw Missing();
            return StartInfo(installation.Executable, directory);
        }
        finally { gate.Release(); }
    }

    private async Task Load(CancellationToken ct)
    {
        if (loaded) return;
        configuredPath = store is null ? null : await store.LoadGitPathAsync(ct);
        loaded = true;
    }

    public string ResolveExecutable(string? path)
    {
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        if (!string.IsNullOrWhiteSpace(path))
        {
            var full = Path.GetFullPath(path.Trim(), applicationDirectory);
            if (Directory.Exists(full))
            {
                foreach (var relative in new[] { Path.Combine("cmd", name), Path.Combine("bin", name), name })
                    if (File.Exists(Path.Combine(full, relative))) return Path.Combine(full, relative);
            }
            else if (File.Exists(full) && Path.GetFileName(full).Equals(name,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return full;
            throw Missing(); // An explicit broken selection must never run a different system Git.
        }
        foreach (var folder in new[] { "PortableGit", "Git", Path.Combine("tools", "PortableGit"), Path.Combine("tools", "git") })
            foreach (var relative in new[] { Path.Combine("cmd", name), Path.Combine("bin", name), name })
            {
                var candidate = Path.Combine(applicationDirectory, folder, relative);
                if (File.Exists(candidate)) return candidate;
            }
        foreach (var entry in environmentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = entry.Trim().Trim('"');
            // Never discover executables from the current repository or a relative PATH entry.
            if (!Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        throw Missing();
    }

    private ProcessStartInfo StartInfo(string executable, string directory)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetFullPath(directory),
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        var paths = new List<string> { Path.GetDirectoryName(executable)! };
        if (OperatingSystem.IsWindows())
        {
            var parent = new DirectoryInfo(Path.GetDirectoryName(executable)!);
            for (var level = 0; level < 3 && parent is not null && !File.Exists(Path.Combine(parent.FullName, "cmd", "git.exe")); level++)
                parent = parent.Parent;
            // cmd/git.exe and bin/git.exe are stable entry points for full PortableGit/MinGit.
            if (parent is not null && File.Exists(Path.Combine(parent.FullName, "cmd", "git.exe")))
            {
                foreach (var relative in new[] { "cmd", "bin", "ucrt64/bin", "mingw64/bin", "usr/bin" })
                {
                    var candidate = Path.Combine(parent.FullName, relative);
                    if (Directory.Exists(candidate)) paths.Add(candidate);
                }
                foreach (var relative in new[] { "ucrt64/libexec/git-core", "mingw64/libexec/git-core", "libexec/git-core" })
                {
                    var candidate = Path.Combine(parent.FullName, relative);
                    if (Directory.Exists(candidate)) { start.Environment["GIT_EXEC_PATH"] = candidate; break; }
                }
            }
        }
        start.Environment["PATH"] = string.Join(Path.PathSeparator, paths.Append(environmentPath));
        return start;
    }

    private async Task<GitRuntimeInfo> Probe(string executable, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var start = StartInfo(executable, applicationDirectory);
        start.ArgumentList.Add("--version");
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start(); process.StandardInput.Close();
            using var cancel = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token));
            var version = (await output).Trim();
            if (process.ExitCode != 0 || !version.StartsWith("git version ", StringComparison.Ordinal))
                throw new InvalidOperationException("無法執行所選 Git / Selected Git could not be verified.");
            if (!GitVersionPolicy.Accepts(version))
                throw new InvalidOperationException("需要 Git 2.40 以上 / Git 2.40 or newer is required.");
            return new(executable, version);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new InvalidOperationException("Git 版本檢查逾時 / Git version check timed out."); }
        catch (System.ComponentModel.Win32Exception ex)
        { throw new InvalidOperationException("無法啟動 Git，請檢查 Git 設定 / Cannot launch Git; check Git settings.", ex); }
    }

    private static InvalidOperationException Missing() => new(
        "找不到 Git，請開啟「Git 設定」選取 Portable Git 資料夾或 git.exe / Git not found; choose a Git installation in Git settings.");
}
