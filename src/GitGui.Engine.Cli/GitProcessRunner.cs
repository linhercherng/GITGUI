using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using GitGui.Core;

namespace GitGui.Engine.Cli;

public sealed record GitResult(int ExitCode, byte[] Output, string Error);

public sealed record GitRunOptions
{
    public bool IsReadOnly { get; init; } = true;
    public bool Trusted { get; init; }
    public byte[]? Input { get; init; }
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
    public Stream? OutputStream { get; init; }
    public Func<Stream, CancellationToken, Task>? OutputConsumer { get; init; }
    public bool HideOutput { get; init; }
    public int MaxOutputBytes { get; init; } = 32 * 1024 * 1024;
}

public sealed class GitProcessRunner(GitRuntime? runtime = null)
{
    private readonly GitRuntime runtime = runtime ?? new();
    internal Task<ProcessStartInfo> CreateStartInfoAsync(string directory, CancellationToken ct)
        => runtime.CreateStartInfoAsync(directory, ct);
    public event EventHandler<CommandEntry>? CommandExecuted;
    public string? AskPassCommand { get; set; }
    public string? AskPassPipe { get; set; }

    public Task<GitResult> ReadAsync(string repository, IReadOnlyList<string> args, CancellationToken ct)
        => RunAsync(repository, args, ct);

    public async Task<GitResult> RunAsync(string repository, IReadOnlyList<string> args, CancellationToken ct,
        GitRunOptions? options = null, IProgress<OperationProgress>? progress = null)
    {
        options ??= new();
        ct.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        var start = await CreateStartInfoAsync(repository, ct);
        var prefix = new List<string> { "--literal-pathspecs", "-c", "core.quotepath=false",
            "-c", "color.ui=never", "-c", "core.pager=cat" };
        if (options.IsReadOnly) prefix.Insert(0, "--no-optional-locks");
        if (!options.Trusted) prefix.AddRange(["-c", "core.fsmonitor=false"]);
        if (!options.Trusted && args.Any(a => a is "status" or "diff" or "diff-files" or "diff-index"))
        {
            var configured = await RunAsync(repository,
                ["config", "-z", "--name-only", "--get-regexp", "^filter\\..*\\.(clean|smudge|process|required)$"], ct,
                new() { HideOutput = true });
            if (configured.ExitCode is not (0 or 1)) throw new GitCommandException(configured);
            foreach (var key in Encoding.UTF8.GetString(configured.Output).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(k => k[..k.LastIndexOf('.')]).Distinct())
                foreach (var property in new[] { "clean=", "smudge=", "process=", "required=false" })
                    prefix.AddRange(["-c", key + "." + property]);
        }
        if (OperatingSystem.IsWindows()) prefix.AddRange(["-c", "core.longpaths=true"]);
        foreach (var arg in prefix.Concat(args)) start.ArgumentList.Add(arg);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (options.IsReadOnly) start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        else start.Environment.Remove("GIT_OPTIONAL_LOCKS");
        start.Environment["LC_ALL"] = "C";
        // Do not let an inherited tracing setting write repository data to a file.
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_TRACE", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(key);
        if (AskPassCommand is not null)
        {
            start.Environment["GIT_ASKPASS"] = AskPassCommand;
            start.Environment["SSH_ASKPASS"] = AskPassCommand;
            start.Environment["SSH_ASKPASS_REQUIRE"] = "prefer";
            start.Environment["GITGUI_ASKPASS_PIPE"] = AskPassPipe;
        }
        foreach (var (key, value) in options.Environment) start.Environment[key] = value;
        using var process = new Process { StartInfo = start };
        process.Start();
        using var registration = ct.Register(() => Kill(process));
        async Task<byte[]> Drain(Stream stream)
        {
            try
            {
                if (options.OutputConsumer is not null)
                {
                    await options.OutputConsumer(stream, ct);
                    return [];
                }
                if (options.OutputStream is not null)
                {
                    await stream.CopyToAsync(options.OutputStream, ct);
                    return [];
                }
                using var output = new MemoryStream();
                var buffer = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    if (output.Length + count > options.MaxOutputBytes)
                        throw new InvalidOperationException("Git output is too large. Narrow the selection or load a smaller page.");
                    output.Write(buffer, 0, count);
                }
                return output.ToArray();
            }
            catch { Kill(process); throw; }
        }
        async Task WriteInput()
        {
            try
            {
                if (options.Input is not null) await process.StandardInput.BaseStream.WriteAsync(options.Input, ct);
                process.StandardInput.Close();
            }
            catch (IOException) { /* Rejected commands can close stdin early; preserve stderr. */ }
        }
        async Task<string> DrainError()
        {
            var stored = new StringBuilder();
            var tail = "";
            var buffer = new char[2048];
            int count;
            while ((count = await process.StandardError.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                var chunk = new string(buffer, 0, count);
                if (stored.Length < 1024 * 1024) stored.Append(chunk[..Math.Min(chunk.Length, 1024 * 1024 - stored.Length)]);
                tail += chunk;
                var lines = tail.Split(['\r', '\n']);
                foreach (var line in lines[..^1])
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var match = Regex.Match(line, @"(\d{1,3})%");
                    progress?.Report(new(Redact(line.Trim()), match.Success ? int.Parse(match.Groups[1].Value) : null,
                        ct.CanBeCanceled && (args.Contains("fetch") || args.Contains("clone"))));
                }
                tail = lines[^1];
                if (tail.Length > 8192) tail = tail[^8192..];
            }
            return Redact(stored.ToString());
        }
        // Start all pipe producers before invoking a consumer that may read synchronously.
        var stdin = WriteInput();
        var stderr = DrainError();
        var stdout = Drain(process.StandardOutput.BaseStream);
        try
        {
            await Task.WhenAll(stdout, stderr, stdin, process.WaitForExitAsync(ct));
            var output = await stdout;
            var error = await stderr;
            var text = options.HideOutput || options.OutputStream is not null || options.OutputConsumer is not null
                ? "[object/binary output omitted]" : Redact(Encoding.UTF8.GetString(output));
            if (text.Length > 16000) text = text[..16000] + "\n[output truncated]";
            CommandExecuted?.Invoke(this, new(DateTimeOffset.Now,
                FormatCommand(start.FileName, prefix.Concat(args)),
                text + (error.Length > 0 ? "\n" + error : ""), process.ExitCode, watch.Elapsed));
            return new(process.ExitCode, output, error);
        }
        catch
        {
            Kill(process);
            try { await process.WaitForExitAsync(CancellationToken.None); } catch (InvalidOperationException) { }
            throw;
        }
    }

    public static string Redact(string text)
    {
        text = Regex.Replace(text, @"(https?://)[^\s/@]+@", "$1***@", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"(Authorization\s*[:=]\s*)[^\r\n]+", "$1***", RegexOptions.IgnoreCase);
        return Regex.Replace(text, @"((?:access_token|token|password|passwd|secret)\s*[=:]\s*)[^\s&]+", "$1***", RegexOptions.IgnoreCase);
    }

    internal static string FormatCommand(string executable, IEnumerable<string> arguments)
        => (OperatingSystem.IsWindows() ? "& " : "") + ShellQuote(Redact(executable)) + " " + string.Join(" ", arguments.Select(QuoteDisplay));

    private static string QuoteDisplay(string value)
    {
        value = Redact(value);
        return value.Length == 0 || value.Any(c => char.IsWhiteSpace(c) || "\"'&;|$`()<>#*?[]{}!".Contains(c))
            ? ShellQuote(value) : value;
    }

    private static string ShellQuote(string value)
        => "'" + value.Replace("'", OperatingSystem.IsWindows() ? "''" : "'\\''", StringComparison.Ordinal) + "'";

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    internal void Record(CommandEntry entry) => CommandExecuted?.Invoke(this, entry);
}
