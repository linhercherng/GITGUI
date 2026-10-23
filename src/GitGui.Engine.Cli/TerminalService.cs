using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using GitGui.Core;

namespace GitGui.Engine.Cli;

/// <summary>Trusted, persistent line shell. The caller supplies the same repository lease used by GUI writes.</summary>
public sealed class TerminalService(GitRuntime runtime, IRepositorySettings settings,
    Func<string, CancellationToken, Task>? trustGate = null,
    Func<string, CancellationToken, Task<IAsyncDisposable>>? acquireLease = null,
    GitProcessRunner? credentialRunner = null) : ITerminalService
{
    public const int MaxTranscriptCharacters = 200_000;

    public async Task<ITerminalSession> StartAsync(string repository, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(repository);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        if (!await settings.IsTrustedAsync(root, ct))
        {
            if (trustGate is not null) await trustGate(root, ct);
            if (!await settings.IsTrustedAsync(root, ct))
                throw new InvalidOperationException("請先信任此倉庫才能開啟終端機 / Trust this repository before opening its terminal.");
        }
        IAsyncDisposable? lease = acquireLease is null ? null : await acquireLease(root, ct);
        try
        {
            // Reuse GitRuntime's selected/bundled Git environment, never reconstruct a different PATH.
            var start = await runtime.CreateStartInfoAsync(root, ct);
            var git = start.FileName;
            start.StandardInputEncoding = new UTF8Encoding(false);
            start.StandardOutputEncoding = Encoding.UTF8; start.StandardErrorEncoding = Encoding.UTF8;
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment["GIT_PAGER"] = "cat"; start.Environment["PAGER"] = "cat";
            start.Environment["TERM"] = "dumb";
            start.Environment["NO_COLOR"] = "1";
            foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_TRACE", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
            if (credentialRunner?.AskPassCommand is { } askPass)
            {
                start.Environment["GIT_ASKPASS"] = askPass;
                start.Environment["SSH_ASKPASS"] = askPass;
                start.Environment["SSH_ASKPASS_REQUIRE"] = "prefer";
                start.Environment["GITGUI_ASKPASS_PIPE"] = credentialRunner.AskPassPipe;
            }
            if (OperatingSystem.IsWindows())
            {
                start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                start.ArgumentList.Add("/D"); start.ArgumentList.Add("/Q");
                // Prevent a repository-local git.cmd/git.exe from preceding the selected Git installation.
                start.Environment["NoDefaultCurrentDirectoryInExePath"] = "1";
            }
            else start.FileName = "/bin/sh";
            var separateProcessGroup = !OperatingSystem.IsWindows() && File.Exists("/usr/bin/setsid");
            if (separateProcessGroup) { start.FileName = "/usr/bin/setsid"; start.ArgumentList.Add("/bin/sh"); }
            ct.ThrowIfCancellationRequested();
            var process = new Process { StartInfo = start };
            try { process.Start(); }
            catch { process.Dispose(); throw; }
            TerminalProcessLifetime ownership;
            try { ownership = new(process, separateProcessGroup); }
            catch { try { if (!process.HasExited) process.Kill(true); } finally { process.Dispose(); } throw; }
            var session = new TerminalSession(process, ownership, root, git, lease, ct);
            lease = null; // Session now owns the queue lease, including startup failure cleanup.
            try
            {
                if (OperatingSystem.IsWindows()) await session.InitializeWindowsAsync(ct);
                return session;
            }
            catch { await session.DisposeAsync(); throw; }
        }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }

    private sealed class TerminalSession : ITerminalSession
    {
        private readonly Process process;
        private readonly TerminalProcessLifetime ownership;
        private readonly IAsyncDisposable? lease;
        private readonly object outputGate = new();
        private readonly StringBuilder transcript = new();
        private readonly SemaphoreSlim inputGate = new(1, 1);
        private readonly CancellationTokenRegistration cancellation;
        private readonly Task outputPump;
        private readonly Task errorPump;
        private int stopping;
        private int disposed;
        public string Repository { get; }
        public string GitExecutable { get; }
        public int ProcessId { get; }
        public string Output { get { lock (outputGate) return transcript.ToString(); } }
        public bool IsRunning => Volatile.Read(ref stopping) == 0 && !Completion.IsCompleted;
        public Task<int> Completion { get; }
        public event EventHandler? OutputChanged;

        public TerminalSession(Process process, TerminalProcessLifetime ownership, string repository, string git, IAsyncDisposable? lease, CancellationToken ct)
        {
            this.process = process; this.ownership = ownership; this.lease = lease; Repository = repository; GitExecutable = git; ProcessId = process.Id;
            cancellation = ct.Register(Kill);
            outputPump = PumpAsync(process.StandardOutput);
            errorPump = PumpAsync(process.StandardError);
            Completion = MonitorAsync();
        }

        public Task InitializeWindowsAsync(CancellationToken ct) => WriteAsync("@echo off\r\nchcp 65001 >nul\r\n", ct);

        public async Task SendLineAsync(string line, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(line);
            if (line.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new ArgumentException("Enter one command line at a time.", nameof(line));
            if (!IsRunning) throw new InvalidOperationException("Terminal is closed.");
            Append("$ " + line + "\n");
            await WriteAsync(line + (OperatingSystem.IsWindows() ? "\r\n" : "\n"), ct);
        }

        private async Task WriteAsync(string text, CancellationToken ct)
        {
            await inputGate.WaitAsync(ct);
            try
            {
                if (Volatile.Read(ref stopping) != 0 || process.HasExited) throw new InvalidOperationException("Terminal is closed.");
                await process.StandardInput.WriteAsync(text.AsMemory(), ct);
                await process.StandardInput.FlushAsync(ct);
            }
            catch (IOException ex) { throw new InvalidOperationException("Terminal input is closed.", ex); }
            finally { inputGate.Release(); }
        }

        private async Task PumpAsync(StreamReader reader)
        {
            var pending = new StringBuilder(); var buffer = new char[2048]; var suppressingLongLine = false;
            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory())) > 0)
                    for (var i = 0; i < count; i++)
                    {
                        var value = buffer[i];
                        if (value is '\n' or '\r')
                        {
                            if (!suppressingLongLine && pending.Length > 0) Append(pending + "\n");
                            pending.Clear(); suppressingLongLine = false;
                        }
                        else if (!suppressingLongLine)
                        {
                            pending.Append(value);
                            // Suppress the entire overlong line, rather than leaking a secret split across chunks.
                            if (pending.Length > 32_768) { pending.Clear(); suppressingLongLine = true; Append("[overlong terminal line omitted]\n"); }
                        }
                    }
                if (!suppressingLongLine && pending.Length > 0) Append(pending + "\n");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }

        private void Append(string value)
        {
            // Plain text transcript: no terminal escape execution and no credentials in the UI/history.
            var text = GitProcessRunner.Redact(value);
            text = Regex.Replace(text, @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))", "");
            text = new string(text.Where(c => c is '\n' or '\t' || !char.IsControl(c)).ToArray());
            lock (outputGate)
            {
                transcript.Append(text);
                if (transcript.Length > MaxTranscriptCharacters) transcript.Remove(0, transcript.Length - MaxTranscriptCharacters);
            }
            OutputChanged?.Invoke(this, EventArgs.Empty);
        }

        private async Task<int> MonitorAsync()
        {
            try
            {
                await process.WaitForExitAsync();
                ownership.Dispose();
                // Programs inheriting redirected handles must not hold the repository lease indefinitely.
                try { await Task.WhenAll(outputPump, errorPump).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { process.StandardOutput.Close(); process.StandardError.Close(); }
                return process.ExitCode;
            }
            finally
            {
                Interlocked.Exchange(ref stopping, 1);
                cancellation.Dispose();
                if (lease is not null) await lease.DisposeAsync();
                OutputChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void Kill()
        {
            Interlocked.Exchange(ref stopping, 1);
            ownership.Dispose();
        }
        public async Task StopAsync() { Kill(); await Completion; }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            await StopAsync(); process.Dispose();
        }
    }
}
